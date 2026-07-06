using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal static class AtomicFileWriter
    {
        // keepBackup: app-data persistence relies on the .bak sidecar for recovery
        // (JsonPersistenceRecovery reads it), so it stays the default. Writes into a USER'S
        // connected workspace must pass false — a stray "index.html.bak" pollutes their git
        // status and their project, and codebase undo restores from stored PreviousContent,
        // never from the sidecar.
        public static void WriteAllText(string path, string content, bool keepBackup = true)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            if (keepBackup && File.Exists(path))
                File.Copy(path, path + ".bak", true);

            File.WriteAllText(tempPath, content);
            File.Move(tempPath, path, true);
        }

        public static async Task WriteAllTextAsync(string path, string content, CancellationToken cancellationToken = default)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string backupPath = path + ".bak";
            string tempPath = path + ".tmp";
            if (File.Exists(path))
                File.Copy(path, backupPath, true);

            await File.WriteAllTextAsync(tempPath, content, cancellationToken);
            File.Move(tempPath, path, true);
        }
    }
}
