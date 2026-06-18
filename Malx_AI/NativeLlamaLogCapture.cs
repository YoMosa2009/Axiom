using System;
using System.IO;
using System.Text;
using LLama.Native;

namespace Malx_AI
{
    /// <summary>
    /// Captures llama.cpp's own native log stream to a file. This is the missing diagnostic
    /// for native aborts: when llama.cpp hits a GGML_ASSERT / GGML_ABORT it logs the failing
    /// condition (and the warnings leading up to it) THROUGH this callback and then calls
    /// abort() — which kills the process with no managed exception and no chance for our async
    /// BackendLogService to flush. So every message is written SYNCHRONOUSLY and the stream is
    /// FLUSHED per line: the last line before the process dies is guaranteed to be on disk.
    ///
    /// Read the tail of native-llama.log right after a crash to see the real reason
    /// (e.g. "llama_decode: failed to find KV slot", an n_past/batch assert, a CUDA error).
    /// </summary>
    public static class NativeLlamaLogCapture
    {
        private static readonly object _gate = new();
        private static string _logPath = string.Empty;
        private static bool _installed;

        // Keep a strong reference to the delegate so the GC never collects it while native
        // code still holds the function pointer.
        private static NativeLogConfig.LLamaLogCallback? _callback;

        public static NativeLogConfig.LLamaLogCallback? TryCreateCallback(string logDirectory)
        {
            try
            {
                Directory.CreateDirectory(logDirectory);
                _logPath = Path.Combine(logDirectory, "native-llama.log");

                // Start each session with a header so crash investigation can scope to the
                // current run without the file growing without bound across launches.
                RotateIfLarge(_logPath);
                File.AppendAllText(_logPath, $"{Environment.NewLine}===== native llama log session {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");

                _callback = OnNativeLog;
                _installed = true;
                return _callback;
            }
            catch
            {
                // Diagnostics must never destabilize startup — fall back to no capture.
                return null;
            }
        }

        private static void OnNativeLog(LLamaLogLevel level, string message)
        {
            if (!_installed || string.IsNullOrEmpty(message))
                return;

            try
            {
                // llama.cpp emits messages WITHOUT trailing newlines and uses level=Continue
                // for line continuations; just append the raw text and let the level prefix
                // mark the start of a fresh record.
                string text = level == LLamaLogLevel.Continue
                    ? message
                    : $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";

                lock (_gate)
                {
                    // Open/append/flush/close per write so a native abort immediately after a
                    // logged assert cannot lose the buffered line.
                    using var stream = new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    byte[] bytes = Encoding.UTF8.GetBytes(text);
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
            }
            catch
            {
                // Never throw out of a native callback.
            }
        }

        private static void RotateIfLarge(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2L * 1024 * 1024)
                    File.WriteAllText(path, string.Empty);
            }
            catch
            {
            }
        }

        /// <summary>Returns the last <paramref name="maxChars"/> characters of the native log, or empty.</summary>
        public static string ReadTail(int maxChars = 4000)
        {
            try
            {
                if (string.IsNullOrEmpty(_logPath) || !File.Exists(_logPath))
                    return string.Empty;

                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (stream.Length > maxChars)
                    stream.Seek(-maxChars, SeekOrigin.End);

                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd();
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
