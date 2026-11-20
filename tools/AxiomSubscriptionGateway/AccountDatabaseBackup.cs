using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AxiomSubscriptionGateway;

internal static class AccountDatabaseBackup
{
    private static readonly TimeSpan DefaultBackupInterval = TimeSpan.FromHours(6);

    public static void Create(string databasePath, string backupPath)
    {
        string destination = Path.GetFullPath(backupPath);
        if (File.Exists(destination))
            throw new IOException("Choose a new filename for the account backup.");

        string? destinationDir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(destinationDir))
            Directory.CreateDirectory(destinationDir);

        // Keep the unfinished copy in the destination directory so publication is a same-volume rename.
        string temporary = Path.Combine(destinationDir!, $".axiom-backup-{Guid.NewGuid():N}.tmp");
        using (new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        try
        {
            using (var source = Open(databasePath, SqliteOpenMode.ReadOnly))
            using (var copy = Open(temporary, SqliteOpenMode.ReadWrite))
            {
                source.BackupDatabase(copy);
                using var command = copy.CreateCommand();
                command.CommandText = "PRAGMA journal_mode = DELETE;";
                if (!string.Equals(command.ExecuteScalar() as string, "delete", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Could not prepare a standalone account backup.");

                // A restored snapshot requires a fresh sign-in. It cannot revive old sessions or email codes.
                command.CommandText = """
                    PRAGMA secure_delete = ON;
                    DELETE FROM axiom_email_challenge;
                    DELETE FROM axiom_account_session;
                    """;
                command.ExecuteNonQuery();

                command.CommandText = """
                    SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name = 'axiom_checkout_lock';
                    """;
                if (Convert.ToInt64(command.ExecuteScalar() ?? 0L) == 1)
                {
                    command.CommandText = "DELETE FROM axiom_checkout_lock;";
                    command.ExecuteNonQuery();
                }

                command.CommandText = """
                    SELECT COUNT(*) FROM sqlite_schema WHERE type = 'table' AND name IN (
                        'axiom_account', 'axiom_account_recovery', 'allowance_period',
                        'allowance_request', 'upstream_generation'
                    );
                    """;
                if ((long)command.ExecuteScalar()! != 5)
                    throw new InvalidOperationException("The source is missing required account or allowance tables.");

                command.CommandText = "PRAGMA quick_check;";
                if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
                    throw new InvalidOperationException("The account backup did not pass SQLite's consistency check.");
            }

            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static int RunBackupCycle(string databasePath, string backupDirectory, int retentionCount, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory) || !File.Exists(databasePath))
            return 0;

        string dir = Path.GetFullPath(backupDirectory);
        Directory.CreateDirectory(dir);
        DateTimeOffset now = (clock ?? TimeProvider.System).GetUtcNow();
        string stamp = now.ToString("yyyyMMdd-HHmmss");
        string target = Path.Combine(dir, $"axiom-accounts-{stamp}.sqlite");
        if (File.Exists(target))
            target = Path.Combine(dir, $"axiom-accounts-{stamp}-{Guid.NewGuid():N}.sqlite");

        Create(databasePath, target);

        int keep = Math.Max(1, retentionCount);
        var snapshots = Directory.GetFiles(dir, "axiom-accounts-*.sqlite")
            .OrderByDescending(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (int i = keep; i < snapshots.Length; i++)
        {
            try { File.Delete(snapshots[i]); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return Math.Min(snapshots.Length, keep);
    }

    internal static TimeSpan ParseIntervalFromEnvironment()
    {
        string? raw = Environment.GetEnvironmentVariable("AXIOM_BACKUP_INTERVAL_HOURS");
        if (double.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hours)
            && hours >= 0.25 && hours <= 168)
        {
            return TimeSpan.FromHours(hours);
        }
        return DefaultBackupInterval;
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(path), Mode = mode, Pooling = false, DefaultTimeout = 10
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }
}

internal sealed class AccountDatabaseBackupWorker(
    string databasePath,
    string backupDirectory,
    ILogger<AccountDatabaseBackupWorker> logger,
    TimeProvider? clock = null,
    TimeSpan? interval = null,
    int retentionCount = 14) : BackgroundService
{
    private readonly TimeSpan _interval = interval ?? TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval, clock ?? TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    AccountDatabaseBackup.RunBackupCycle(databasePath, backupDirectory, retentionCount, clock);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested
                    && ex is IOException or SqliteException or InvalidOperationException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Scheduled account database backup failed: {Message}", ex.Message);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
