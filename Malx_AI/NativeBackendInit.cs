using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using LLama.Native;

namespace Malx_AI
{
    /// <summary>
    /// Configures the LLamaSharp native library backend ONCE at application startup,
    /// before any other LLamaSharp types are touched. This is critical because
    /// NativeLibraryConfig is frozen the moment any native call is made.
    /// </summary>
    public static class NativeBackendInit
    {
        private static bool _initialized;
        private static readonly object _lock = new();

        public static bool GpuConfigured { get; private set; }
        public static string DiagnosticMessage { get; private set; } = "";

        /// <summary>
        /// Must be called as early as possible in App startup — before MainWindow,
        /// before HardwareProfiler, before any LLamaWeights or ModelParams usage.
        /// </summary>
        public static void Configure()
        {
            lock (_lock)
            {
                if (_initialized) return;
                _initialized = true;

                try
                {
                    string baseNativePath = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
                    string cudaPath = Path.Combine(baseNativePath, "cuda12");
                    bool cudaDirExists = Directory.Exists(cudaPath);
                    bool hasCudaDll = cudaDirExists && File.Exists(Path.Combine(cudaPath, "ggml-cuda.dll"));
                    bool hasNvidiaGpu = ProbeNvidiaPresence();

                    Debug.WriteLine($"[NativeBackendInit] baseNativePath={baseNativePath}");
                    Debug.WriteLine($"[NativeBackendInit] cudaPath={cudaPath}, exists={cudaDirExists}, hasCudaDll={hasCudaDll}");
                    Debug.WriteLine($"[NativeBackendInit] hasNvidiaGpu={hasNvidiaGpu}");

                    bool enableCuda = hasCudaDll && hasNvidiaGpu;

                    // ggml-cuda.dll depends on CUDA runtime DLLs (cudart64_12.dll, cublas64_12.dll,
                    // cublasLt64_12.dll) that are NOT bundled with LLamaSharp. They live in the
                    // CUDA Toolkit installation. We must add that directory to PATH before the OS
                    // loader tries to resolve ggml-cuda.dll's imports, otherwise the DLL load fails
                    // silently and LLamaSharp falls back to CPU.
                    if (enableCuda)
                    {
                        string? cudaRuntimeDir = FindCuda12RuntimeDir();
                        if (cudaRuntimeDir != null)
                        {
                            PrependToPath(cudaRuntimeDir);
                            Debug.WriteLine($"[NativeBackendInit] CUDA runtime dir added to PATH: {cudaRuntimeDir}");
                        }
                        else
                        {
                            // Cannot find CUDA runtime — ggml-cuda.dll will fail to load.
                            enableCuda = false;
                            Debug.WriteLine("[NativeBackendInit] CUDA runtime DLLs not found; falling back to CPU.");
                        }
                    }

                    // Prepend the correct native directory to PATH so the OS loader finds llama DLLs
                    string targetPath = enableCuda ? cudaPath : baseNativePath;
                    PrependToPath(targetPath);

                    // Also ensure the base native path is on PATH
                    if (baseNativePath != targetPath)
                        PrependToPath(baseNativePath);

                    // Configure LLamaSharp's native library selection.
                    var config = NativeLibraryConfig.LLama;

                    if (enableCuda)
                    {
                        config.WithCuda();
                        config.WithSearchDirectory(cudaPath);
                        config.WithSearchDirectory(baseNativePath);
                        Debug.WriteLine("[NativeBackendInit] WithCuda() called");
                    }
                    else
                    {
                        config.WithSearchDirectory(baseNativePath);
                    }

                    config.WithAutoFallback();

                    GpuConfigured = enableCuda;
                    DiagnosticMessage = enableCuda
                        ? $"CUDA backend configured (ggml-cuda.dll found at {cudaPath})"
                        : hasCudaDll
                            ? "CUDA DLLs found but CUDA runtime not available — using CPU"
                            : $"No CUDA DLLs at {cudaPath} — using CPU";

                    Debug.WriteLine($"[NativeBackendInit] {DiagnosticMessage}");
                }
                catch (Exception ex)
                {
                    GpuConfigured = false;
                    DiagnosticMessage = $"Backend init failed: {ex.Message}";
                    Debug.WriteLine($"[NativeBackendInit] ERROR: {ex}");
                }
            }
        }

        /// <summary>
        /// Finds the CUDA 12 runtime bin directory containing cudart64_12.dll.
        /// Checks CUDA_PATH env var first, then the standard NVIDIA installation path.
        /// </summary>
        private static string? FindCuda12RuntimeDir()
        {
            // CUDA Toolkit installer sets CUDA_PATH to the versioned install dir
            string? cudaEnvPath = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrEmpty(cudaEnvPath))
            {
                string binDir = Path.Combine(cudaEnvPath, "bin");
                if (File.Exists(Path.Combine(binDir, "cudart64_12.dll")))
                {
                    Debug.WriteLine($"[NativeBackendInit] Found CUDA runtime via CUDA_PATH: {binDir}");
                    return binDir;
                }
            }

            // Fall back to scanning the standard install root for any CUDA 12.x version
            string cudaBase = @"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA";
            if (!Directory.Exists(cudaBase))
                return null;

            var candidates = Directory.GetDirectories(cudaBase)
                .Where(d => Path.GetFileName(d).StartsWith("v12.", StringComparison.OrdinalIgnoreCase)
                         || Path.GetFileName(d).Equals("v12", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d)
                .ToList();

            foreach (string dir in candidates)
            {
                string binDir = Path.Combine(dir, "bin");
                if (File.Exists(Path.Combine(binDir, "cudart64_12.dll")))
                {
                    Debug.WriteLine($"[NativeBackendInit] Found CUDA runtime at: {binDir}");
                    return binDir;
                }
            }

            return null;
        }

        private static void PrependToPath(string directory)
        {
            if (!Directory.Exists(directory)) return;
            string envPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!envPath.Contains(directory, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + envPath);
        }

        /// <summary>
        /// Lightweight GPU presence check that does NOT touch any LLamaSharp types.
        /// Uses nvidia-smi which the app already depends on.
        /// </summary>
        private static bool ProbeNvidiaPresence()
        {
            string[] candidates = { "nvidia-smi", @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe" };

            foreach (var exe in candidates)
            {
                try
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exe,
                        Arguments = "-L",
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var proc = Process.Start(psi);
                    if (proc == null) continue;

                    string output = proc.StandardOutput.ReadToEnd();
                    proc.WaitForExit(2000);

                    if (proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output))
                    {
                        Debug.WriteLine($"[NativeBackendInit] nvidia-smi detected GPU: {output.Trim().Split('\n')[0]}");
                        return true;
                    }
                }
                catch
                {
                    continue;
                }
            }

            return false;
        }
    }
}
