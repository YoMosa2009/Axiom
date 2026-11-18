using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal static class AtomicFileWriter
    {
        private static readonly object CommitLock = new();

        // keepBackup: app-data persistence relies on the .bak sidecar for recovery
        // (JsonPersistenceRecovery reads it), so it stays the default. Writes into a USER'S
        // connected workspace must pass false — a stray "index.html.bak" pollutes their git
        // status and their project, and codebase undo restores from stored PreviousContent,
        // never from the sidecar.
        public static void WriteAllText(string path, string content, bool keepBackup = true)
        {
            string tempPath = PrepareTemporaryPath(path);
            try
            {
                File.WriteAllText(tempPath, content);
                Commit(tempPath, path, keepBackup);
            }
            finally
            {
                DeleteTemporaryFile(tempPath);
            }
        }

        public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string tempPath = PrepareTemporaryPath(path);
            try
            {
                await File.WriteAllTextAsync(tempPath, content, cancellationToken);
                Commit(tempPath, path, keepBackup: true, cancellationToken);
            }
            finally
            {
                DeleteTemporaryFile(tempPath);
            }
        }

        private static string PrepareTemporaryPath(string path)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            // Stage beside the target for an atomic rename, using a name owned by this write.
            return Path.Combine(directory, $".axiom-{Guid.NewGuid():N}.tmp");
        }

        private static void Commit(string tempPath, string path, bool keepBackup, CancellationToken cancellationToken = default)
        {
            // Staging can overlap, but two writers must not both treat the target as new.
            lock (CommitLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (keepBackup && File.Exists(path))
                    File.Replace(tempPath, path, path + ".bak");
                else
                    File.Move(tempPath, path, overwrite: true);
            }
        }

        private static void DeleteTemporaryFile(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Preserve the original write error if cleanup also failed.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
