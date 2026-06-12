using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal static class AtomicFileWriter
    {
        public static void WriteAllText(string path, string content)
        {
            string directory = Path.GetDirectoryName(path) ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            string backupPath = path + ".bak";
            string tempPath = path + ".tmp";
            if (File.Exists(path))
                File.Copy(path, backupPath, true);

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
