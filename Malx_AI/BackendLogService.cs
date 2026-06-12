using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public static class BackendLogService
    {
        private static readonly SemaphoreSlim LogGate = new SemaphoreSlim(1, 1);

        public static async Task LogEventAsync(string area, string message)
        {
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "backend-events.log");

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}]");
                sb.AppendLine(message ?? string.Empty);
                sb.AppendLine(new string('-', 80));

                await LogGate.WaitAsync();
                try
                {
                    await File.AppendAllTextAsync(logPath, sb.ToString());
                }
                finally
                {
                    LogGate.Release();
                }
            }
            catch
            {
            }
        }

        public static async Task LogErrorAsync(string area, Exception ex)
        {
            try
            {
                string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                string logPath = Path.Combine(logDir, "backend-errors.log");

                var sb = new StringBuilder();
                sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{area}]");
                sb.AppendLine(ex.Message);
                sb.AppendLine(ex.ToString());
                sb.AppendLine(new string('-', 80));

                await LogGate.WaitAsync();
                try
                {
                    await File.AppendAllTextAsync(logPath, sb.ToString());
                }
                finally
                {
                    LogGate.Release();
                }
            }
            catch
            {
            }
        }
    }
}
