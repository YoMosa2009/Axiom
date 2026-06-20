using System;
using System.IO;

namespace Malx_AI
{
    /// <summary>
    /// Synchronous crash forensics for native llama.cpp decodes. An oversized or invalid
    /// decode aborts the whole process (ucrtbase 0xc0000409) with no managed exception and
    /// no chance to log anything, so a tiny marker file is written before every guarded
    /// decode and removed when it completes. A marker still present at startup means the
    /// previous session died mid-decode — its contents identify the stage, prompt size,
    /// and model, which is otherwise unrecoverable after a native abort.
    /// </summary>
    public static class NativeDecodeForensics
    {
        private static readonly object Gate = new object();
        private static int _activeDecodes;

        // The configuration of the currently loaded model, stamped into every marker so a
        // next-launch crash recovery knows exactly which model+backend died (and can step it
        // down). Set once at model load via SetActiveModel.
        private static string _activeModelPath = string.Empty;
        private static bool _activeGpu;
        private static int _activeGpuLayers;

        private static string MarkerPath => Path.Combine(AppDataPaths.Logs, "decode-inflight.marker");

        /// <summary>
        /// Records the loaded model's identity and backend so the crash marker (and the
        /// crash ledger that consumes it) can attribute a native abort to a specific model
        /// and offload configuration. Call once whenever a model finishes loading.
        /// </summary>
        public static void SetActiveModel(string modelPath, bool gpu, int gpuLayers)
        {
            lock (Gate)
            {
                _activeModelPath = modelPath ?? string.Empty;
                _activeGpu = gpu;
                _activeGpuLayers = gpuLayers;
            }
        }

        public static void BeginDecode(string stage, int promptTokens, int contextSize, string modelName, string detail = "")
        {
            try
            {
                lock (Gate)
                {
                    _activeDecodes++;
                    Directory.CreateDirectory(AppDataPaths.Logs);
                    // ModelPath is written LAST so the ledger can parse it as "everything after
                    // ModelPath:" — a Windows path contains ':' and spaces that would otherwise
                    // confuse field splitting.
                    File.WriteAllText(MarkerPath,
                        $"Stage:{stage} | PromptTokens:{promptTokens} | Ctx:{contextSize} | Model:{modelName}"
                        + $" | Backend:{(_activeGpu ? "CUDA" : "CPU")} | GpuLayers:{_activeGpuLayers}"
                        + $" | Started:{DateTime.Now:yyyy-MM-dd HH:mm:ss}"
                        + (string.IsNullOrWhiteSpace(detail) ? string.Empty : $" | {detail}")
                        + $" | ModelPath:{_activeModelPath}");
                }
            }
            catch
            {
                // Forensics must never break inference.
            }
        }

        public static void EndDecode()
        {
            try
            {
                lock (Gate)
                {
                    if (_activeDecodes > 0)
                        _activeDecodes--;
                    if (_activeDecodes == 0 && File.Exists(MarkerPath))
                        File.Delete(MarkerPath);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Clears the in-flight decode marker on a GRACEFUL application shutdown. A real native
        /// abort (CUDA illegal-memory-access / GGML_ASSERT → ucrtbase fail-fast) tears the process
        /// down WITHOUT running any managed shutdown code, so this is reached only on a normal exit.
        /// Any marker still present at that point therefore belongs to a decode the user merely
        /// interrupted by closing the app mid-generation — NOT a crash. Removing it here stops the
        /// next launch from misreading it as a GPU crash and recording a false strike that would
        /// wrongly pin the model to CPU. (A genuine abort never reaches this, so true crashes still
        /// leave their marker and are still recorded.)
        /// </summary>
        public static void MarkCleanShutdown()
        {
            try
            {
                lock (Gate)
                {
                    if (File.Exists(MarkerPath))
                        File.Delete(MarkerPath);
                }
            }
            catch
            {
            }
        }

        /// <summary>
        /// Returns the marker left behind by a session that died mid-decode (clearing it),
        /// or null when the previous session shut down cleanly.
        /// </summary>
        public static string? ConsumeCrashReport()
        {
            try
            {
                if (!File.Exists(MarkerPath))
                    return null;

                string content = File.ReadAllText(MarkerPath);
                File.Delete(MarkerPath);
                return string.IsNullOrWhiteSpace(content) ? null : content.Trim();
            }
            catch
            {
                return null;
            }
        }
    }
}
