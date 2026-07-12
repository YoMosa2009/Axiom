using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI
{
    public static class BackendLogService
    {
        private static readonly SemaphoreSlim LogGate = new SemaphoreSlim(1, 1);

        // Diagnostic logs must never become a disk-space problem on a user's machine: the
        // event log alone grows by hundreds of KB per active day and previously grew forever.
        // When a log passes the cap it is rotated to a single ".1" generation (previous
        // generation replaced), so worst case is ~2x cap per log file.
        private const long MaxLogFileBytes = 4 * 1024 * 1024;

        public static Task LogEventAsync(string area, string message)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}]");
            sb.AppendLine(message ?? string.Empty);
            sb.AppendLine(new string('-', 80));
            return AppendWithRotationAsync("backend-events.log", sb.ToString());
        }

        public static Task LogErrorAsync(string area, Exception ex)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}]");
            sb.AppendLine(ex.Message);
            sb.AppendLine(ex.ToString());
            sb.AppendLine(new string('-', 80));
            return AppendWithRotationAsync("backend-errors.log", sb.ToString());
        }

        public static Task<string> ReadRecentLinesAsync(int maxLines = 200)
        {
            int boundedMaxLines = Math.Clamp(maxLines, 1, 2000);
            return Task.Run(() =>
            {
                string logDir = AppDataPaths.Logs;
                if (!Directory.Exists(logDir))
                    return string.Empty;

                var tail = new Queue<string>(boundedMaxLines);
                foreach (FileInfo file in new DirectoryInfo(logDir)
                    .EnumerateFiles("backend-*.log*")
                    .OrderBy(item => item.LastWriteTimeUtc))
                {
                    tail.Enqueue($"--- {file.Name} ---");
                    using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    while (reader.ReadLine() is string line)
                    {
                        tail.Enqueue(line);
                        while (tail.Count > boundedMaxLines)
                            tail.Dequeue();
                    }
                }
                return string.Join(Environment.NewLine, tail);
            });
        }

        private static async Task AppendWithRotationAsync(string fileName, string text)
        {
            try
            {
                string logDir = AppDataPaths.Logs;
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, fileName);

                await LogGate.WaitAsync();
                try
                {
                    RotateIfNeeded(logPath);
                    await File.AppendAllTextAsync(logPath, text);
                }
                finally
                {
                    LogGate.Release();
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        private static void RotateIfNeeded(string logPath)
        {
            try
            {
                var info = new FileInfo(logPath);
                if (!info.Exists || info.Length < MaxLogFileBytes)
                    return;

                string rotatedPath = logPath + ".1";
                if (File.Exists(rotatedPath))
                    File.Delete(rotatedPath);
                File.Move(logPath, rotatedPath);
            }
            catch
            {
                // If rotation fails (file lock, permissions), keep appending — better a large
                // log than a lost one.
            }
        }
    }
}
