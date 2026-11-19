using Microsoft.Data.Sqlite;

namespace AxiomSubscriptionGateway;

internal static class AccountDatabaseBackup
{
    public static void Create(string databasePath, string backupPath)
    {
        string destination = Path.GetFullPath(backupPath);
        if (File.Exists(destination))
            throw new IOException("Choose a new filename for the account backup.");

        // Keep the unfinished copy in the destination directory so publication is a same-volume rename.
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".axiom-backup-{Guid.NewGuid():N}.tmp");
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
