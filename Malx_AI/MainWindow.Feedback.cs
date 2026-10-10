using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Malx_AI
{
    /// <summary>Settings › General › "Send feedback or report a problem".</summary>
    public partial class MainWindow
    {
        private const int ErrorLogTailBytes = 64 * 1024;

        private async void SendFeedbackButton_Click(object sender, RoutedEventArgs e)
        {
            SendFeedbackButton.IsEnabled = false;
            try
            {
                FeedbackContext context = await BuildFeedbackContextAsync();
                new FeedbackDialog(context) { Owner = this }.ShowDialog();
            }
            finally
            {
                SendFeedbackButton.IsEnabled = true;
            }
        }

        private async Task<FeedbackContext> BuildFeedbackContextAsync()
        {
            HardwareProfile? hardware = _detectedHardware;
            if (hardware == null)
            {
                try
                {
                    hardware = await Task.Run(HardwareProfiler.Capture);
                }
                catch (Exception ex)
                {
                    _ = BackendLogService.LogErrorAsync("Feedback", ex);
                }
            }

            string mode = !_cloudModeActive ? "Local" : IsCustomEndpointModelSelected() ? "Hybrid Local" : "Cloud";
            string errorLog = await Task.Run(() => ReadLogTail(Path.Combine(AppDataPaths.Logs, "backend-errors.log"), ErrorLogTailBytes));

            return new FeedbackContext(
                UpdateCheckService.GetCurrentVersionText(),
                RuntimeInformation.OSDescription + (Environment.Is64BitOperatingSystem ? " (64-bit)" : string.Empty),
                DescribeHardwareForFeedback(hardware),
                $"{mode}, model: {GetActiveModelDisplayName()}",
                errorLog);
        }

        private static string DescribeHardwareForFeedback(HardwareProfile? hardware)
        {
            if (hardware == null)
                return "unknown";

            double vramGb = hardware.TotalVramGb > 0 ? hardware.TotalVramGb : hardware.AvailableVramGb;
            string gpu = hardware.HasNvidiaGpu
                ? $"{hardware.PrimaryGpuName}{(vramGb > 0 ? $" {vramGb:0} GB" : string.Empty)}"
                : hardware.PrimaryGpuName is { Length: > 0 } name && name != "Unknown" ? $"{name} (not used)" : "no supported GPU";
            return $"{hardware.LogicalProcessorCount} threads, {hardware.AvailableRamGb:0} GB RAM, {gpu}";
        }

        /// <summary>The last part of a log file, without loading a large file into memory.</summary>
        private static string ReadLogTail(string path, int maxBytes)
        {
            try
            {
                if (!File.Exists(path))
                    return string.Empty;

                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                long start = Math.Max(0, stream.Length - maxBytes);
                stream.Seek(start, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();

                // Starting mid-file usually lands mid-line; drop the partial first line.
                if (start > 0)
                {
                    int firstBreak = text.IndexOf('\n');
                    if (firstBreak >= 0)
                        text = text[(firstBreak + 1)..];
                }

                return text;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }
}
