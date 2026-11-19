using Microsoft.Data.Sqlite;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace AxiomSubscriptionGateway;

internal sealed record AxiomAccount(string Id, string Email, string? StripeCustomerId);
internal sealed record AxiomAccountSession(string Token, string Email, DateTimeOffset ExpiresAt);

internal sealed class AxiomAccountStore
{
    private const string RecoveryPrefix = "axr_";
    private const long SessionLifetimeSeconds = 30 * 24 * 60 * 60;
    private const long RecentSignInSeconds = 15 * 60;
    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public AxiomAccountStore(string databasePath, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath, ForeignKeys = true, Pooling = false
        }.ToString();

        using var connection = Open();
        using var command = Command(connection, """
            CREATE TABLE IF NOT EXISTS axiom_account (
                id TEXT PRIMARY KEY,
                email TEXT NOT NULL UNIQUE,
                stripe_customer_id TEXT UNIQUE
            );
            CREATE TABLE IF NOT EXISTS axiom_email_challenge (
                email TEXT PRIMARY KEY,
                code_hash TEXT NOT NULL UNIQUE,
                issued_at INTEGER NOT NULL,
                expires_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS axiom_account_session (
                secret_hash TEXT PRIMARY KEY,
                account_id TEXT NOT NULL REFERENCES axiom_account (id),
                expires_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS axiom_account_recovery (
                account_id TEXT PRIMARY KEY REFERENCES axiom_account (id),
                secret_hash TEXT NOT NULL UNIQUE
            );
            """);
        command.ExecuteNonQuery();
    }

    public string? IssueCode(string email)
    {
        string code = NewSecret(16);
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var cleanup = Command(connection, "DELETE FROM axiom_email_challenge WHERE expires_at <= $now",
            transaction, ("$now", now));
        cleanup.ExecuteNonQuery();
        using var issue = Command(connection, """
            INSERT INTO axiom_email_challenge (email, code_hash, issued_at, expires_at)
            VALUES ($email, $hash, $now, $expiry)
            ON CONFLICT(email) DO UPDATE SET code_hash = $hash, issued_at = $now, expires_at = $expiry
            WHERE axiom_email_challenge.issued_at <= $cooldown;
            """, transaction, ("$email", email), ("$hash", Hash(code)), ("$now", now),
            ("$expiry", now + (long)TimeSpan.FromMinutes(15).TotalSeconds), ("$cooldown", now - 60));
        bool created = issue.ExecuteNonQuery() == 1;
        transaction.Commit();
        return created ? code : null;
    }

    public void RevokeCode(string email, string code)
    {
        using var connection = Open();
        using var command = Command(connection, "DELETE FROM axiom_email_challenge WHERE email = $email AND code_hash = $hash",
            null, ("$email", email), ("$hash", Hash(code)));
        command.ExecuteNonQuery();
    }

    public AxiomAccountSession? CompleteSignIn(string email, string code)
    {
        if (!IsSecret(code, 32))
            return null;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        if (!ConsumeEmailCode(connection, transaction, email, code, now))
            return null;

        using var account = Command(connection, """
            INSERT INTO axiom_account (id, email) VALUES ($id, $email)
            ON CONFLICT(email) DO UPDATE SET email = excluded.email RETURNING id;
            """, transaction, ("$id", Guid.NewGuid().ToString("N")), ("$email", email));
        string accountId = (string)account.ExecuteScalar()!;
        var session = CreateSession(connection, transaction, accountId, email, now);
        transaction.Commit();
        return session;
    }

    public string? IssueRecoveryKey(string sessionToken)
    {
        if (!IsSecret(sessionToken, 64))
            return null;

        string secret = NewSecret(32);
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = Open();
        using var command = Command(connection, """
            INSERT INTO axiom_account_recovery (account_id, secret_hash)
            SELECT account_id, $newHash FROM axiom_account_session
            WHERE secret_hash = $sessionHash AND expires_at > $recent
            ON CONFLICT(account_id) DO UPDATE SET secret_hash = excluded.secret_hash;
            """, null, ("$newHash", Hash(secret)), ("$sessionHash", Hash(sessionToken)),
            ("$recent", now + SessionLifetimeSeconds - RecentSignInSeconds));
        return command.ExecuteNonQuery() == 1 ? RecoveryPrefix + secret : null;
    }

    public AxiomAccountSession? RecoverAccount(string email, string code, string recoveryKey)
    {
        if (!IsSecret(code, 32) || !recoveryKey.StartsWith(RecoveryPrefix, StringComparison.Ordinal)
            || !IsSecret(recoveryKey[RecoveryPrefix.Length..], 64))
            return null;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var consume = Command(connection, """
            DELETE FROM axiom_account_recovery WHERE secret_hash = $hash RETURNING account_id;
            """, transaction, ("$hash", Hash(recoveryKey[RecoveryPrefix.Length..])));
        if (consume.ExecuteScalar() is not string accountId
            || !ConsumeEmailCode(connection, transaction, email, code, now))
            return null;

        // Changing the email changes sign-in authority. The Stripe customer and allowance
        // continue to belong to the same account ID.
        using var clearPrior = Command(connection, """
            DELETE FROM axiom_email_challenge WHERE email = (SELECT email FROM axiom_account WHERE id = $id);
            DELETE FROM axiom_account_session WHERE account_id = $id;
            """, transaction, ("$id", accountId));
        clearPrior.ExecuteNonQuery();
        using var change = Command(connection, """
            UPDATE axiom_account SET email = $email
            WHERE id = $id AND NOT EXISTS (
                SELECT 1 FROM axiom_account WHERE email = $email AND id <> $id
            );
            """, transaction, ("$email", email), ("$id", accountId));
        if (change.ExecuteNonQuery() != 1)
            return null;

        var session = CreateSession(connection, transaction, accountId, email, now);
        transaction.Commit();
        return session;
    }

    public AxiomAccount? Authenticate(string token)
    {
        if (!IsSecret(token, 64))
            return null;
        using var connection = Open();
        using var command = Command(connection, """
            SELECT a.id, a.email, a.stripe_customer_id FROM axiom_account a
            JOIN axiom_account_session s ON s.account_id = a.id
            WHERE s.secret_hash = $hash AND s.expires_at > $now;
            """, null, ("$hash", Hash(token)), ("$now", _clock.GetUtcNow().ToUnixTimeSeconds()));
        return ReadAccount(command);
    }

    public void SignOut(string token)
    {
        if (!IsSecret(token, 64))
            return;
        using var connection = Open();
        using var command = Command(connection, "DELETE FROM axiom_account_session WHERE secret_hash = $hash",
            null, ("$hash", Hash(token)));
        command.ExecuteNonQuery();
    }

    public AxiomAccount? FindStripeCustomer(string stripeCustomer)
    {
        using var connection = Open();
        using var command = Command(connection, """
            SELECT id, email, stripe_customer_id FROM axiom_account WHERE stripe_customer_id = $customer;
            """, null, ("$customer", stripeCustomer));
        return ReadAccount(command);
    }

    public string BindStripeCustomer(string accountId, string stripeCustomer)
    {
        using var connection = Open();
        using var command = Command(connection, """
            UPDATE axiom_account SET stripe_customer_id = COALESCE(stripe_customer_id, $customer)
            WHERE id = $id RETURNING stripe_customer_id;
            """, null, ("$id", accountId), ("$customer", stripeCustomer));
        return (string)command.ExecuteScalar()!;
    }

    public static string? NormalizeEmail(string? email)
    {
        email = email?.Trim();
        return email is { Length: > 0 and <= 254 } && MailAddress.TryCreate(email, out var address)
            && address.Address.Equals(email, StringComparison.OrdinalIgnoreCase)
            && address.Host.Contains('.') ? address.Address.ToLowerInvariant() : null;
    }

    private static bool ConsumeEmailCode(SqliteConnection connection, SqliteTransaction transaction,
        string email, string code, long now)
    {
        using var command = Command(connection, """
            DELETE FROM axiom_email_challenge WHERE email = $email AND code_hash = $hash AND expires_at > $now;
            """, transaction, ("$email", email), ("$hash", Hash(code)), ("$now", now));
        return command.ExecuteNonQuery() == 1;
    }

    private static AxiomAccountSession CreateSession(SqliteConnection connection, SqliteTransaction transaction,
        string accountId, string email, long now)
    {
        string token = NewSecret(32);
        long expiry = now + SessionLifetimeSeconds;
        using var save = Command(connection, """
            DELETE FROM axiom_account_session WHERE expires_at <= $now;
            INSERT INTO axiom_account_session (secret_hash, account_id, expires_at) VALUES ($hash, $id, $expiry);
            """, transaction, ("$now", now), ("$hash", Hash(token)), ("$id", accountId), ("$expiry", expiry));
        save.ExecuteNonQuery();
        return new AxiomAccountSession(token, email, DateTimeOffset.FromUnixTimeSeconds(expiry));
    }

    private static AxiomAccount? ReadAccount(SqliteCommand command)
    {
        using var row = command.ExecuteReader();
        return row.Read()
            ? new AxiomAccount(row.GetString(0), row.GetString(1), row.IsDBNull(2) ? null : row.GetString(2))
            : null;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, SqliteTransaction? transaction = null,
        params (string Name, object Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value);
        return command;
    }

    private static string NewSecret(int bytes) => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(bytes));
    private static bool IsSecret(string text, int length) => text.Length == length && text.All(Uri.IsHexDigit);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(value.ToLowerInvariant())));
}
