using Microsoft.Data.Sqlite;
using System.Text.RegularExpressions;

namespace AxiomSubscriptionGateway;

/// <summary>One anonymous diagnostics event sent by the desktop app (see Malx_AI/DiagnosticsReport.cs).</summary>
internal sealed record DiagnosticsEventInput(
    string? Kind, string? Area, string? ExceptionType, string? Message, string? Stack,
    string? Fingerprint, int Occurrences, string? Detail, string? OccurredAt);

/// <summary>A batch of events from one install. InstallId is a random per-install id, not an account.</summary>
internal sealed record DiagnosticsBatchInput(
    string? InstallId, string? AppVersion, string? Os, List<DiagnosticsEventInput>? Events);

internal sealed record DiagnosticsSummaryRow(
    string Kind, string Area, string ExceptionType, string Message, long Occurrences, long Installs,
    string LatestVersion, string LastSeen);

/// <summary>
/// Stores opt-out desktop diagnostics in the gateway database: no IP address, no account, and
/// a 90-day retention. Inputs are length-capped and validated; a batch that fails validation is
/// rejected whole.
/// </summary>
internal sealed class DiagnosticsStore
{
    internal const int MaxEventsPerBatch = 50;
    internal const int MaxRowsPerInstallPerDay = 300;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(90);
    private static readonly HashSet<string> Kinds = new(StringComparer.Ordinal) { "error", "crash", "session", "feature" };
    private static readonly Regex InstallIdPattern = new("^[0-9a-f]{32}$", RegexOptions.Compiled);
    private static readonly Regex Token = new(@"^[A-Za-z0-9 ._:/+()\-]{1,80}$", RegexOptions.Compiled);
    private readonly string _connectionString;
    private readonly TimeProvider _clock;

    public DiagnosticsStore(string databasePath, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS diagnostics_event (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                received_at INTEGER NOT NULL,
                occurred_at TEXT,
                install_id TEXT NOT NULL,
                app_version TEXT NOT NULL,
                os TEXT NOT NULL,
                kind TEXT NOT NULL,
                area TEXT NOT NULL,
                fingerprint TEXT NOT NULL,
                exception_type TEXT NOT NULL,
                message TEXT NOT NULL,
                stack TEXT NOT NULL,
                detail TEXT NOT NULL,
                occurrences INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS diagnostics_event_received ON diagnostics_event (received_at);
            CREATE INDEX IF NOT EXISTS diagnostics_event_install ON diagnostics_event (install_id, received_at);
            """;
        command.ExecuteNonQuery();
    }

    /// <summary>Returns the number of events stored, or null when the batch is invalid.</summary>
    public int? Accept(DiagnosticsBatchInput batch)
    {
        if (batch.InstallId is null || !InstallIdPattern.IsMatch(batch.InstallId)
            || !IsToken(batch.AppVersion) || !IsToken(batch.Os)
            || batch.Events is not { Count: > 0 and <= MaxEventsPerBatch } events
            || events.Any(e => e is null || e.Kind is null || !Kinds.Contains(e.Kind) || !IsToken(e.Area)))
            return null;

        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        using var connection = Open();
        using var transaction = connection.BeginTransaction();

        using (var purge = connection.CreateCommand())
        {
            purge.Transaction = transaction;
            purge.CommandText = "DELETE FROM diagnostics_event WHERE received_at < $cutoff";
            purge.Parameters.AddWithValue("$cutoff", now - (long)Retention.TotalSeconds);
            purge.ExecuteNonQuery();
        }

        long todayRows;
        using (var count = connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM diagnostics_event WHERE install_id = $id AND received_at >= $since";
            count.Parameters.AddWithValue("$id", batch.InstallId);
            count.Parameters.AddWithValue("$since", now - 86_400);
            todayRows = (long)count.ExecuteScalar()!;
        }

        int room = (int)Math.Max(0, MaxRowsPerInstallPerDay - todayRows);
        int stored = 0;
        foreach (var e in events.Take(room))
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO diagnostics_event (received_at, occurred_at, install_id, app_version, os, kind, area,
                    fingerprint, exception_type, message, stack, detail, occurrences)
                VALUES ($at, $occurred, $id, $version, $os, $kind, $area, $fp, $type, $message, $stack, $detail, $n)
                """;
            insert.Parameters.AddWithValue("$at", now);
            insert.Parameters.AddWithValue("$occurred", (object?)Cap(e.OccurredAt, 40) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$id", batch.InstallId);
            insert.Parameters.AddWithValue("$version", batch.AppVersion);
            insert.Parameters.AddWithValue("$os", batch.Os);
            insert.Parameters.AddWithValue("$kind", e.Kind);
            insert.Parameters.AddWithValue("$area", e.Area);
            insert.Parameters.AddWithValue("$fp", Cap(e.Fingerprint, 64) ?? "");
            insert.Parameters.AddWithValue("$type", Cap(e.ExceptionType, 200) ?? "");
            insert.Parameters.AddWithValue("$message", Cap(e.Message, 500) ?? "");
            insert.Parameters.AddWithValue("$stack", Cap(e.Stack, 4000) ?? "");
            insert.Parameters.AddWithValue("$detail", Cap(e.Detail, 500) ?? "");
            insert.Parameters.AddWithValue("$n", Math.Clamp(e.Occurrences, 1, 10_000));
            insert.ExecuteNonQuery();
            stored++;
        }
        transaction.Commit();
        return stored;
    }

    /// <summary>Errors and crashes grouped by fingerprint over the last <paramref name="days"/> days, most frequent first.</summary>
    public IReadOnlyList<DiagnosticsSummaryRow> Summarize(int days, int limit = 40)
    {
        long since = _clock.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90)).ToUnixTimeSeconds();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT kind, area, exception_type, MAX(message), SUM(occurrences), COUNT(DISTINCT install_id),
                   MAX(app_version), datetime(MAX(received_at), 'unixepoch')
            FROM diagnostics_event
            WHERE received_at >= $since
            GROUP BY kind, area, CASE WHEN fingerprint = '' THEN exception_type ELSE fingerprint END,
                     CASE WHEN kind IN ('session', 'feature') THEN detail ELSE '' END
            ORDER BY SUM(occurrences) DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$since", since);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        var rows = new List<DiagnosticsSummaryRow>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetString(6), reader.GetString(7)));
        return rows;
    }

    /// <summary>Distinct installs that sent anything in the window, by app version.</summary>
    public IReadOnlyList<(string Version, long Installs)> ActiveInstalls(int days)
    {
        long since = _clock.GetUtcNow().AddDays(-Math.Clamp(days, 1, 90)).ToUnixTimeSeconds();
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT app_version, COUNT(DISTINCT install_id) FROM diagnostics_event
            WHERE received_at >= $since GROUP BY app_version ORDER BY 2 DESC
            """;
        command.Parameters.AddWithValue("$since", since);
        var rows = new List<(string, long)>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        return rows;
    }

    private static bool IsToken(string? value) => value is not null && Token.IsMatch(value);

    private static string? Cap(string? value, int max)
        => value is null ? null : value.Length <= max ? value : value[..max];

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
