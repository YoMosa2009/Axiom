using Microsoft.Data.Sqlite;

namespace AxiomSubscriptionGateway;

internal sealed class AllowanceLedger
{
    internal const long SessionWindowSeconds = 3 * 3600;
    internal const long WeekWindowSeconds = 7 * 86_400;

    private readonly string _connectionString;
    private readonly string _account;
    private readonly TimeProvider _clock;
    private readonly long? _sessionLimitMicros;
    private readonly long? _weeklyLimitMicros;

    public AllowanceLedger(string databasePath, long allowanceMicros, string? stripeCustomer = null,
        TimeProvider? clock = null, long? sessionLimitMicros = null, long? weeklyLimitMicros = null)
    {
        _account = stripeCustomer ?? "local";
        _clock = clock ?? TimeProvider.System;
        _sessionLimitMicros = sessionLimitMicros;
        _weeklyLimitMicros = weeklyLimitMicros;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        }.ToString();

        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS allowance_period (
                id TEXT PRIMARY KEY,
                account TEXT NOT NULL,
                starts INTEGER NOT NULL,
                ends INTEGER NOT NULL CHECK (ends > starts),
                allowance INTEGER NOT NULL CHECK (allowance >= 0),
                spent INTEGER NOT NULL DEFAULT 0 CHECK (spent >= 0),
                reserved INTEGER NOT NULL DEFAULT 0 CHECK (reserved >= 0),
                CHECK (spent + reserved <= allowance)
            );
            CREATE INDEX IF NOT EXISTS period_account_start ON allowance_period (account, starts);
            CREATE TABLE IF NOT EXISTS allowance_request (
                id TEXT PRIMARY KEY,
                period_id TEXT NOT NULL REFERENCES allowance_period (id),
                reserved INTEGER NOT NULL CHECK (reserved > 0),
                actual INTEGER CHECK (actual >= 0 AND actual <= reserved),
                created_at INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS upstream_generation (
                request_id TEXT PRIMARY KEY REFERENCES allowance_request (id),
                generation_id TEXT NOT NULL UNIQUE,
                observed_at INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS usage_session_window (
                account TEXT PRIMARY KEY,
                starts INTEGER NOT NULL,
                ends INTEGER NOT NULL CHECK (ends > starts)
            );
            """;
        command.ExecuteNonQuery();
        EnsureCreatedAtColumn(connection, transaction);
        using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.Transaction = transaction;
            indexCommand.CommandText = "CREATE INDEX IF NOT EXISTS request_period_created ON allowance_request (period_id, created_at);";
            indexCommand.ExecuteNonQuery();
        }
        transaction.Commit();
        if (stripeCustomer is null)
            TryGrantPeriod("local-seed", 0, long.MaxValue, allowanceMicros);
    }

    private AllowanceLedger(string connectionString, string account, TimeProvider clock,
        long? sessionLimitMicros, long? weeklyLimitMicros)
    {
        _connectionString = connectionString;
        _account = account;
        _clock = clock;
        _sessionLimitMicros = sessionLimitMicros;
        _weeklyLimitMicros = weeklyLimitMicros;
    }

    public AllowanceLedger ForAccount(string accountId)
        => new(_connectionString, accountId, _clock, _sessionLimitMicros, _weeklyLimitMicros);

    private static void EnsureCreatedAtColumn(SqliteConnection connection, SqliteTransaction transaction)
    {
        bool hasCreatedAt = false;
        using (var pragma = connection.CreateCommand())
        {
            pragma.Transaction = transaction;
            pragma.CommandText = "PRAGMA table_info(allowance_request);";
            using var reader = pragma.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "created_at", StringComparison.OrdinalIgnoreCase))
                {
                    hasCreatedAt = true;
                    break;
                }
            }
        }

        if (!hasCreatedAt)
        {
            using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE allowance_request ADD COLUMN created_at INTEGER NOT NULL DEFAULT 0;";
            alter.ExecuteNonQuery();
        }
    }

    public bool TryGrantPeriod(string invoiceId, long starts, long ends, long allowanceMicros)
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO allowance_period (id, account, starts, ends, allowance)
            SELECT $id, $account, $starts, $ends, $allowance
            WHERE NOT EXISTS (
                SELECT 1 FROM allowance_period
                WHERE id = $id OR (account = $account AND starts < $ends AND ends > $starts)
            );
            """;
        command.Parameters.AddWithValue("$id", invoiceId);
        command.Parameters.AddWithValue("$account", _account);
        command.Parameters.AddWithValue("$starts", starts);
        command.Parameters.AddWithValue("$ends", ends);
        command.Parameters.AddWithValue("$allowance", allowanceMicros);
        bool applied = command.ExecuteNonQuery() == 1;
        transaction.Commit();
        return applied;
    }

    public AllowanceBalance GetBalance() => GetPeriod().Balance;

    public (AllowanceBalance Balance, DateTimeOffset? RenewsAt) GetPeriod()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT allowance, spent, reserved, ends FROM allowance_period
            WHERE account = $account AND starts <= $now AND ends > $now;
            """;
        BindActivePeriod(command);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return (new AllowanceBalance(0, 0, 0), null);

        long ends = reader.GetInt64(3);
        return (new AllowanceBalance(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2)),
            ends == long.MaxValue ? null : DateTimeOffset.FromUnixTimeSeconds(ends));
    }

    public AllowanceUsageSnapshot GetUsage()
    {
        long sessionLimit = _sessionLimitMicros ?? 0;
        long weeklyLimit = _weeklyLimitMicros ?? 0;
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();

        using var connection = Open();
        using var periodCommand = connection.CreateCommand();
        periodCommand.CommandText = """
            SELECT id, starts, ends, allowance FROM allowance_period
            WHERE account = $account AND starts <= $now AND ends > $now;
            """;
        periodCommand.Parameters.AddWithValue("$account", _account);
        periodCommand.Parameters.AddWithValue("$now", now);

        string? periodId = null;
        long periodStarts = 0;
        long periodEnds = 0;
        long allowance = 0;
        using (var reader = periodCommand.ExecuteReader())
        {
            if (reader.Read())
            {
                periodId = reader.GetString(0);
                periodStarts = reader.GetInt64(1);
                periodEnds = reader.GetInt64(2);
                allowance = reader.GetInt64(3);
            }
        }

        if (periodId is null || allowance <= 0)
        {
            return new AllowanceUsageSnapshot(
                new UsageWindowSnapshot(0, sessionLimit, 0, false, null, 3),
                new UsageWindowSnapshot(0, weeklyLimit, 0, false, null));
        }

        var (weekStart, weekEnd, includeLegacyWeek0) = ComputeWeekWindow(now, periodStarts, periodEnds);
        long weekUsed = QueryWindowUsage(connection, null, periodId, weekStart, weekEnd, includeLegacyWeek0);
        double weekPercent = ComputePercent(weekUsed, weeklyLimit);
        var week = new UsageWindowSnapshot(
            weekUsed,
            weeklyLimit,
            weekPercent,
            Active: weeklyLimit > 0,
            ResetsAt: DateTimeOffset.FromUnixTimeSeconds(weekEnd));

        UsageWindowSnapshot session;
        if (TryReadSessionWindow(connection, null, now, out long sessionStart, out long sessionEnd))
        {
            long sessionUsed = QueryWindowUsage(connection, null, periodId, sessionStart, sessionEnd, includeLegacyWeek0: false);
            session = new UsageWindowSnapshot(
                sessionUsed,
                sessionLimit,
                ComputePercent(sessionUsed, sessionLimit),
                Active: true,
                ResetsAt: DateTimeOffset.FromUnixTimeSeconds(sessionEnd),
                WindowHours: 3);
        }
        else
        {
            session = new UsageWindowSnapshot(0, sessionLimit, 0, Active: false, ResetsAt: null, WindowHours: 3);
        }

        return new AllowanceUsageSnapshot(session, week);
    }

    public bool TryReserve(string requestId, long amount)
        => TryReserveDetailed(requestId, amount).Status == ReserveStatus.Reserved;

    public ReserveOutcome TryReserveDetailed(string requestId, long amount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(amount);
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = Open();
        // The conditional debit, pacing window checks, and request record commit together under SQLite's writer lock.
        using var transaction = connection.BeginTransaction();

        string? periodId = null;
        long periodStarts = 0;
        long periodEnds = 0;
        long allowance = 0;
        long spent = 0;
        long reserved = 0;
        using (var periodCommand = connection.CreateCommand())
        {
            periodCommand.Transaction = transaction;
            periodCommand.CommandText = """
                SELECT id, starts, ends, allowance, spent, reserved FROM allowance_period
                WHERE account = $account AND starts <= $now AND ends > $now;
                """;
            periodCommand.Parameters.AddWithValue("$account", _account);
            periodCommand.Parameters.AddWithValue("$now", now);
            using var reader = periodCommand.ExecuteReader();
            if (reader.Read())
            {
                periodId = reader.GetString(0);
                periodStarts = reader.GetInt64(1);
                periodEnds = reader.GetInt64(2);
                allowance = reader.GetInt64(3);
                spent = reader.GetInt64(4);
                reserved = reader.GetInt64(5);
            }
        }

        if (periodId is null || allowance - spent - reserved < amount)
            return new ReserveOutcome(ReserveStatus.AllowanceExhausted);

        if (_weeklyLimitMicros is long weeklyLimit && weeklyLimit > 0)
        {
            var (weekStart, weekEnd, includeLegacyWeek0) = ComputeWeekWindow(now, periodStarts, periodEnds);
            long weekUsed = QueryWindowUsage(connection, transaction, periodId, weekStart, weekEnd, includeLegacyWeek0);
            if (weekUsed >= weeklyLimit)
                return new ReserveOutcome(ReserveStatus.WeeklyLimitReached, DateTimeOffset.FromUnixTimeSeconds(weekEnd));
        }

        bool startNewSessionWindow = false;
        if (_sessionLimitMicros is long sessionLimit && sessionLimit > 0)
        {
            if (TryReadSessionWindow(connection, transaction, now, out long sessionStart, out long sessionEnd))
            {
                long sessionUsed = QueryWindowUsage(connection, transaction, periodId, sessionStart, sessionEnd, includeLegacyWeek0: false);
                if (sessionUsed >= sessionLimit)
                    return new ReserveOutcome(ReserveStatus.SessionLimitReached, DateTimeOffset.FromUnixTimeSeconds(sessionEnd));
            }
            else
            {
                startNewSessionWindow = true;
            }
        }

        using (var reserveCommand = connection.CreateCommand())
        {
            reserveCommand.Transaction = transaction;
            reserveCommand.CommandText = """
                UPDATE allowance_period SET reserved = reserved + $amount
                WHERE id = $period AND allowance - spent - reserved >= $amount;
                """;
            reserveCommand.Parameters.AddWithValue("$period", periodId);
            reserveCommand.Parameters.AddWithValue("$amount", amount);
            if (reserveCommand.ExecuteNonQuery() != 1)
                return new ReserveOutcome(ReserveStatus.AllowanceExhausted);
        }

        if (startNewSessionWindow)
        {
            using var windowCommand = connection.CreateCommand();
            windowCommand.Transaction = transaction;
            windowCommand.CommandText = """
                INSERT INTO usage_session_window (account, starts, ends)
                VALUES ($account, $starts, $ends)
                ON CONFLICT(account) DO UPDATE SET starts = excluded.starts, ends = excluded.ends;
                """;
            windowCommand.Parameters.AddWithValue("$account", _account);
            windowCommand.Parameters.AddWithValue("$starts", now);
            windowCommand.Parameters.AddWithValue("$ends", now + SessionWindowSeconds);
            windowCommand.ExecuteNonQuery();
        }

        using (var insertCommand = connection.CreateCommand())
        {
            insertCommand.Transaction = transaction;
            insertCommand.CommandText = "INSERT INTO allowance_request (id, period_id, reserved, created_at) VALUES ($id, $period, $amount, $now)";
            insertCommand.Parameters.AddWithValue("$id", requestId);
            insertCommand.Parameters.AddWithValue("$period", periodId);
            insertCommand.Parameters.AddWithValue("$amount", amount);
            insertCommand.Parameters.AddWithValue("$now", now);
            insertCommand.ExecuteNonQuery();
        }

        transaction.Commit();
        return new ReserveOutcome(ReserveStatus.Reserved);
    }

    private static (long WeekStart, long WeekEnd, bool IncludeLegacyWeek0) ComputeWeekWindow(long now, long periodStarts, long periodEnds)
    {
        if (periodStarts > 0)
        {
            long weekIndex = Math.Max(0, (now - periodStarts) / WeekWindowSeconds);
            long weekStart = periodStarts + (weekIndex * WeekWindowSeconds);
            long weekEnd = periodEnds == long.MaxValue
                ? weekStart + WeekWindowSeconds
                : Math.Min(periodEnds, weekStart + WeekWindowSeconds);
            return (weekStart, weekEnd, weekIndex == 0);
        }

        DateTimeOffset utcNow = DateTimeOffset.FromUnixTimeSeconds(now).ToUniversalTime();
        var sundayStart = new DateTimeOffset(utcNow.Year, utcNow.Month, utcNow.Day, 0, 0, 0, TimeSpan.Zero)
            .AddDays(-(int)utcNow.DayOfWeek);
        long localWeekStart = sundayStart.ToUnixTimeSeconds();
        return (localWeekStart, localWeekStart + WeekWindowSeconds, false);
    }

    private bool TryReadSessionWindow(SqliteConnection connection, SqliteTransaction? transaction, long now, out long starts, out long ends)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT starts, ends FROM usage_session_window WHERE account = $account;";
        command.Parameters.AddWithValue("$account", _account);
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            long s = reader.GetInt64(0);
            long e = reader.GetInt64(1);
            if (s <= now && now < e)
            {
                starts = s;
                ends = e;
                return true;
            }
        }

        starts = 0;
        ends = 0;
        return false;
    }

    private static long QueryWindowUsage(SqliteConnection connection, SqliteTransaction? transaction,
        string periodId, long windowStart, long windowEnd, bool includeLegacyWeek0)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(SUM(COALESCE(actual, reserved)), 0)
            FROM allowance_request
            WHERE period_id = $period
                AND ((created_at >= $start AND created_at < $end) OR ($legacyWeek0 = 1 AND created_at = 0));
            """;
        command.Parameters.AddWithValue("$period", periodId);
        command.Parameters.AddWithValue("$start", windowStart);
        command.Parameters.AddWithValue("$end", windowEnd);
        command.Parameters.AddWithValue("$legacyWeek0", includeLegacyWeek0 ? 1 : 0);
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static double ComputePercent(long usedMicros, long limitMicros)
        => limitMicros > 0
            ? Math.Clamp(Math.Round((usedMicros * 100.0) / limitMicros, 2), 0, 100)
            : 0;

    public bool TrySettle(string requestId, long actual)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actual);
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE allowance_request SET actual = $actual
            WHERE id = $id AND actual IS NULL
                AND period_id IN (SELECT id FROM allowance_period WHERE account = $account);
            """;
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$actual", actual);
        command.Parameters.AddWithValue("$account", _account);
        if (command.ExecuteNonQuery() == 0)
            return false;

        command.CommandText = """
            UPDATE allowance_period
            SET spent = spent + $actual,
                reserved = reserved - (SELECT reserved FROM allowance_request WHERE id = $id)
            WHERE id = (SELECT period_id FROM allowance_request WHERE id = $id);
            """;
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public void RecordUpstreamGeneration(string requestId, string generationId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO upstream_generation (request_id, generation_id, observed_at)
            SELECT r.id, $generation, $now FROM allowance_request r
            JOIN allowance_period p ON p.id = r.period_id
            WHERE r.id = $id AND p.account = $account;
            """;
        command.Parameters.AddWithValue("$id", requestId);
        command.Parameters.AddWithValue("$generation", generationId);
        BindActivePeriod(command);
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<PendingGeneration> PendingUpstreamRequests(bool allAccounts = false)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT g.request_id, g.generation_id, p.account FROM upstream_generation g
            JOIN allowance_request r ON r.id = g.request_id
            JOIN allowance_period p ON p.id = r.period_id
            WHERE ($all = 1 OR p.account = $account) AND r.actual IS NULL AND g.observed_at <= $cutoff
            ORDER BY g.observed_at LIMIT 20;
            """;
        command.Parameters.AddWithValue("$account", _account);
        command.Parameters.AddWithValue("$all", allAccounts ? 1 : 0);
        command.Parameters.AddWithValue("$cutoff", _clock.GetUtcNow().AddMinutes(-2).ToUnixTimeSeconds());
        using var reader = command.ExecuteReader();
        var pending = new List<PendingGeneration>();
        while (reader.Read())
            pending.Add(new PendingGeneration(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        return pending;
    }

    private void BindActivePeriod(SqliteCommand command)
    {
        command.Parameters.AddWithValue("$account", _account);
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().ToUnixTimeSeconds());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
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

internal enum ReserveStatus
{
    Reserved,
    AllowanceExhausted,
    SessionLimitReached,
    WeeklyLimitReached
}

internal sealed record ReserveOutcome(ReserveStatus Status, DateTimeOffset? ResetsAt = null);

internal sealed record UsageWindowSnapshot(long UsedMicros, long LimitMicros, double UsedPercent, bool Active,
    DateTimeOffset? ResetsAt, int? WindowHours = null);

internal sealed record AllowanceUsageSnapshot(UsageWindowSnapshot Session, UsageWindowSnapshot Week);

internal sealed record AllowanceBalance(long AllowanceMicros, long SpentMicros, long ReservedMicros)
{
    public long AvailableMicros => AllowanceMicros - SpentMicros - ReservedMicros;
}

internal sealed record PendingGeneration(string RequestId, string GenerationId, string AccountId);
