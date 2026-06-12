using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using LLama.Common;

namespace Malx_AI
{
    public sealed class HardwareProfile
    {
        public long AvailableRamBytes { get; set; }
        public long AvailableVramBytes { get; set; }
        public bool HasNvidiaGpu { get; set; }
        public bool HasAmdGpu { get; set; }
        public string PrimaryGpuName { get; set; } = "Unknown";
        public double AvailableRamGb => AvailableRamBytes / 1024d / 1024d / 1024d;
        public double AvailableVramGb => AvailableVramBytes / 1024d / 1024d / 1024d;
    }

    public static class HardwareProfiler
    {
        private static readonly string[] NvidiaSmiCandidates =
        {
            "nvidia-smi",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe"
        };

        private static readonly object _probeLock = new();
        private static HardwareProfile? _cachedGpuIdentity;

        public static bool IsNvidiaRuntimeAvailable()
        {
            foreach (var candidate in NvidiaSmiCandidates)
            {
                if (TryRunNvidiaSmi(candidate, "-L", out var output))
                {
                    return !string.IsNullOrWhiteSpace(output);
                }
            }

            return false;
        }

        public static HardwareProfile Capture()
        {
            HardwareProfile gpuIdentity;
            lock (_probeLock)
            {
                _cachedGpuIdentity ??= ProbeGraphicsHardware();
                gpuIdentity = _cachedGpuIdentity;
            }

            var profile = new HardwareProfile
            {
                HasNvidiaGpu = gpuIdentity.HasNvidiaGpu,
                HasAmdGpu = gpuIdentity.HasAmdGpu,
                PrimaryGpuName = gpuIdentity.PrimaryGpuName,
                AvailableRamBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
                AvailableVramBytes = GetFreeGpuMemoryBytes()
            };

            if (profile.AvailableRamBytes <= 0)
            {
                profile.AvailableRamBytes = 8L * 1024 * 1024 * 1024;
            }

            return profile;
        }

        public static ModelParams BuildSafeModelParams(string modelPath, uint requestedContext, HardwareProfile profile, bool allowGpu)
        {
            long fileBytes = new FileInfo(modelPath).Length;
            GgufModelMetadata? meta = GgufMetadataReader.TryRead(modelPath);

            bool largeModel = fileBytes > 8L * 1024 * 1024 * 1024;
            bool gpuCandidate = allowGpu && profile.HasNvidiaGpu;

            // Quantizing the KV cache to q8_0 halves its memory cost with negligible quality
            // loss. It requires flash attention, which we only enable on the CUDA path.
            bool useKvQuant = gpuCandidate && largeModel;

            long kvBytesPerToken = meta?.KvBytesPerTokenF16 ?? EstimateKvBytesPerTokenFromSize(fileBytes);
            long effectiveKvPerToken = useKvQuant ? kvBytesPerToken / 2 : kvBytesPerToken;

            uint safeContext = ResolveSafeContext(requestedContext, fileBytes, meta, profile, gpuCandidate, effectiveKvPerToken);

            int gpuLayers = 0;
            if (gpuCandidate)
            {
                gpuLayers = CalculateGpuLayerCount(fileBytes, profile.AvailableVramBytes, safeContext, effectiveKvPerToken, meta?.BlockCount);
            }

            // When most layers stay on the CPU, the weights working set lives in RAM.
            // Shrink the context if the model is large relative to free RAM so the KV
            // cache and compute buffers cannot push the process into the page file.
            int totalLayers = meta?.BlockCount ?? EstimateLayerCountFromSize(fileBytes);
            bool fullOffload = gpuLayers > totalLayers;
            if (!fullOffload)
            {
                long ramBudget = (long)(profile.AvailableRamBytes * 0.70);
                if (fileBytes > ramBudget)
                {
                    safeContext = Math.Max(1024, safeContext / 2);
                }
            }

            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = safeContext,
                GpuLayerCount = gpuLayers,
                MainGpu = 0
            };

            ApplyPerformanceTuning(modelParams, gpuLayers, fullOffload, largeModel, useKvQuant, profile);

            Debug.WriteLine($"HardwareProfiler: model={Path.GetFileName(modelPath)}, ctx={safeContext}, gpuLayers={gpuLayers}/{totalLayers}, kvQuant={useKvQuant}, threads={modelParams.Threads}, batch={modelParams.BatchSize}/{modelParams.UBatchSize}");
            return modelParams;
        }

        /// <summary>
        /// Builds context parameters for a model whose WEIGHTS ARE ALREADY RESIDENT
        /// (cached LLamaWeights). A fresh free-memory probe at this point sees the
        /// weights themselves as "used" memory; running the normal planner against it
        /// double-charges the model and concludes 0 GPU layers fit — which made the
        /// council evict a perfectly good GPU-resident model and reload it CPU-only on
        /// every stage after the first. Instead, keep the cached layer split and size
        /// only the context from what is currently free.
        /// </summary>
        public static ModelParams BuildParamsForCachedWeights(string modelPath, uint requestedContext, HardwareProfile profile, int cachedGpuLayerCount)
        {
            long fileBytes = new FileInfo(modelPath).Length;
            GgufModelMetadata? meta = GgufMetadataReader.TryRead(modelPath);

            bool largeModel = fileBytes > 8L * 1024 * 1024 * 1024;
            bool gpuResident = cachedGpuLayerCount > 0;
            bool useKvQuant = gpuResident && largeModel;

            long kvBytesPerToken = meta?.KvBytesPerTokenF16 ?? EstimateKvBytesPerTokenFromSize(fileBytes);
            long effectiveKvPerToken = useKvQuant ? kvBytesPerToken / 2 : kvBytesPerToken;

            uint upperBound = requestedContext;
            if (meta?.ContextLength is > 0)
                upperBound = Math.Min(upperBound, (uint)meta.ContextLength);

            // Weights are resident, so current free memory is all KV/compute budget —
            // do NOT subtract the file size again.
            long memPool = gpuResident && profile.AvailableVramBytes > 0
                ? profile.AvailableVramBytes
                : (long)(profile.AvailableRamBytes * 0.70);

            uint safeContext;
            if (effectiveKvPerToken > 0 && memPool > 0)
            {
                long kvBudget = memPool / 2;
                long ctxByMemory = Math.Clamp(kvBudget / effectiveKvPerToken, 1024, 32768);
                safeContext = Math.Clamp((uint)ctxByMemory, 1024u, Math.Max(1024u, upperBound));
            }
            else
            {
                safeContext = Math.Clamp(upperBound, 1024u, 8192u);
            }

            int totalLayers = meta?.BlockCount ?? EstimateLayerCountFromSize(fileBytes);
            var modelParams = new ModelParams(modelPath)
            {
                ContextSize = safeContext,
                GpuLayerCount = cachedGpuLayerCount,
                MainGpu = 0
            };

            ApplyPerformanceTuning(modelParams, cachedGpuLayerCount, cachedGpuLayerCount > totalLayers, largeModel, useKvQuant, profile);

            Debug.WriteLine($"HardwareProfiler: cached-weights plan model={Path.GetFileName(modelPath)}, ctx={safeContext}, gpuLayers={cachedGpuLayerCount}/{totalLayers} (kept from cache)");
            return modelParams;
        }

        /// <summary>
        /// Picks the context size from real memory math when GGUF metadata is available:
        /// whatever KV cache fits in half the memory left after the weights, never above
        /// the model's trained context or the caller's request. Falls back to the static
        /// file-size table when the header could not be parsed.
        /// </summary>
        private static uint ResolveSafeContext(uint requestedContext, long fileBytes, GgufModelMetadata? meta, HardwareProfile profile, bool gpuCandidate, long kvBytesPerToken)
        {
            uint maxCtxForSize = fileBytes switch
            {
                > 16L * 1024 * 1024 * 1024 => 2048u,
                > 8L * 1024 * 1024 * 1024  => 3072u,
                > 4L * 1024 * 1024 * 1024  => 6144u,
                _                           => 8192u
            };

            uint upperBound = requestedContext;
            if (meta?.ContextLength is > 0)
                upperBound = Math.Min(upperBound, (uint)meta.ContextLength);

            long memPool = gpuCandidate && profile.AvailableVramBytes > 0
                ? profile.AvailableVramBytes
                : (long)(profile.AvailableRamBytes * 0.70);

            long memAfterWeights = memPool - fileBytes;
            if (meta != null && kvBytesPerToken > 0 && memAfterWeights > 0)
            {
                long kvBudget = memAfterWeights / 2;
                long ctxByMemory = Math.Clamp(kvBudget / kvBytesPerToken, 1024, 32768);
                return Math.Clamp((uint)ctxByMemory, 1024u, Math.Max(1024u, upperBound));
            }

            return Math.Clamp(Math.Min(upperBound, maxCtxForSize), 1024u, 32768u);
        }

        // Fallback KV-cache cost per token when GGUF metadata is unavailable.
        // Derived from typical architectures at each size class (f16, K+V).
        private static long EstimateKvBytesPerTokenFromSize(long modelBytes)
        {
            long fileMB = modelBytes / (1024 * 1024);
            return fileMB switch
            {
                < 1500  => 56L * 1024,    // ~1B  (e.g. 22 layers × GQA)
                < 3500  => 96L * 1024,    // ~3-4B
                < 6000  => 128L * 1024,   // ~7B
                < 10000 => 200L * 1024,   // ~13B
                < 25000 => 320L * 1024,   // ~30-34B
                _       => 640L * 1024    // ~70B+
            };
        }

        private static int EstimateLayerCountFromSize(long modelBytes)
        {
            long fileMB = modelBytes / (1024 * 1024);
            return fileMB switch
            {
                < 1500  => 28,
                < 3500  => 32,
                < 6000  => 32,
                < 10000 => 40,
                < 25000 => 60,
                _       => 80
            };
        }

        public static string GetRecommendedModeLabel(HardwareProfile profile)
        {
            if (profile.HasNvidiaGpu)
            {
                return "GPU Accelerated (Recommended)";
            }

            if (profile.HasAmdGpu)
            {
                return "CPU Only (Recommended for AMD on CUDA build)";
            }

            return "CPU Only (No compatible GPU detected)";
        }

        private static HardwareProfile ProbeGraphicsHardware()
        {
            var profile = new HardwareProfile();

            try
            {
                string cmd = "Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name";
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -Command \"{cmd}\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return profile;
                }

                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1800);

                var names = output
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (names.Count > 0)
                {
                    profile.PrimaryGpuName = names[0];
                }

                profile.HasNvidiaGpu = names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
                profile.HasAmdGpu = names.Any(n => n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                profile.PrimaryGpuName = "Unknown";
            }

            return profile;
        }

        /// <summary>
        /// Returns the number of model layers to offload to GPU using the real layer
        /// count when available. KV-cache cost is charged PER OFFLOADED LAYER (llama.cpp
        /// allocates each layer's KV slice on the device that holds the layer), with a
        /// half-context session reserve for short-lived secondary contexts plus CUDA
        /// compute buffers, because a CUDA OOM inside ggml aborts the whole process
        /// with no managed exception.
        ///
        /// The previous math reserved TWO full-context KV caches up front and then
        /// charged each layer only its weights. On 8GB-class GPUs that over-reservation
        /// (~3GB for an 8B model) routinely planned far fewer layers than actually fit —
        /// and under any VRAM pressure planned 0, silently dropping generation to pure
        /// CPU even when the user selected GPU mode.
        /// </summary>
        private static int CalculateGpuLayerCount(long modelBytes, long freeVramBytes, uint contextSize, long kvBytesPerToken, int? actualLayerCount)
        {
            int totalLayers = actualLayerCount ?? EstimateLayerCountFromSize(modelBytes);

            if (freeVramBytes <= 0)
            {
                // VRAM probe failed. Be optimistic only for small models; GPU init
                // failure falls back to CPU at the call sites.
                return modelBytes <= 5L * 1024 * 1024 * 1024 ? totalLayers + 1 : 0;
            }

            long kvTotal = Math.Max(64L * 1024 * 1024, kvBytesPerToken * contextSize);
            long computeReserve = 640L * 1024 * 1024; // CUDA compute buffers + fragmentation headroom
            // Headroom for short-lived secondary contexts (council base-state vault,
            // KV-state loads). These never coexist with more than one main context.
            long sessionReserve = Math.Max(256L * 1024 * 1024, kvTotal / 2);

            long budget = freeVramBytes - computeReserve - sessionReserve;
            if (budget <= 0)
                return 0;

            // +1 accounts for the output/embedding tensors offloaded with the last layer.
            long perLayerWeights = modelBytes / Math.Max(1, totalLayers + 1);
            long kvPerLayer = kvTotal / Math.Max(1, totalLayers);

            // Full offload: all weights plus the whole KV cache fit in the budget.
            if (modelBytes + kvTotal <= budget)
                return totalLayers + 1;

            int layers = (int)(budget / Math.Max(1, perLayerWeights + kvPerLayer));

            if (layers >= totalLayers)
                layers = totalLayers;

            // Offloading only a handful of layers costs more in PCIe transfers than it
            // saves in compute — pure CPU is faster and far less likely to OOM.
            if (layers < 4)
                return 0;

            return layers;
        }

        /// <summary>
        /// Applies thread, batch, flash-attention, and KV-cache settings tuned to the
        /// chosen offload split. Runs for both CPU and GPU plans.
        /// </summary>
        private static void ApplyPerformanceTuning(ModelParams modelParams, int gpuLayers, bool fullOffload, bool largeModel, bool useKvQuant, HardwareProfile profile)
        {
            int physicalCores = PhysicalCoreCount.Value;
            int logicalCores = Environment.ProcessorCount;

            // Generation threads: physical cores. Hyperthread oversubscription slows
            // token generation AND starves the WPF dispatcher, freezing the UI.
            // With full GPU offload the CPU only orchestrates, so fewer threads suffice.
            modelParams.Threads = fullOffload
                ? Math.Clamp(physicalCores, 1, 8)
                : Math.Max(1, physicalCores);

            // Prompt-processing threads scale further; leave one logical core free for the UI.
            modelParams.BatchThreads = Math.Max(1, logicalCores - 1);

            if (gpuLayers > 0)
            {
                // Larger logical batch improves prompt throughput on CUDA. The physical
                // micro-batch (UBatchSize) drives compute-buffer VRAM, so keep it small
                // when VRAM is tight or the model is large.
                bool tightVram = profile.AvailableVramBytes > 0 && profile.AvailableVramGb < 6.0;
                modelParams.BatchSize = 1024;
                modelParams.UBatchSize = (largeModel || tightVram) ? 256u : 512u;

                modelParams.FlashAttention = true;

                if (useKvQuant)
                {
                    modelParams.TypeK = LLama.Native.GGMLType.GGML_TYPE_Q8_0;
                    modelParams.TypeV = LLama.Native.GGMLType.GGML_TYPE_Q8_0;
                }
            }
            else
            {
                modelParams.BatchSize = 512;
                modelParams.UBatchSize = largeModel ? 256u : 512u;
            }

            // Compact the KV cache when fragmentation passes 10% — prevents spurious
            // NoKvSlot failures in long interactive sessions.
            modelParams.DefragThreshold = 0.1f;
            modelParams.UseMemorymap = true;
        }

        // Physical core count probed once. nvidia-smi-style external probe is the
        // pattern already used in this class; fall back to a safe heuristic.
        private static readonly Lazy<int> PhysicalCoreCount = new(ProbePhysicalCoreCount);

        private static int ProbePhysicalCoreCount()
        {
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -Command \"(Get-CimInstance Win32_Processor | Measure-Object -Property NumberOfCores -Sum).Sum\"",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                if (process != null)
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(3000);
                    if (int.TryParse(output.Trim(), out int cores) && cores > 0)
                        return cores;
                }
            }
            catch
            {
            }

            int logical = Environment.ProcessorCount;
            return logical >= 8 ? logical / 2 : Math.Max(1, logical - 1);
        }

        private static long GetFreeGpuMemoryBytes()
        {
            foreach (var candidate in NvidiaSmiCandidates)
            {
                if (!TryRunNvidiaSmi(candidate, "--query-gpu=memory.free --format=csv,noheader,nounits", out string output))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(output))
                {
                    continue;
                }

                long maxMb = 0;
                foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (long.TryParse(line.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long mb))
                    {
                        maxMb = Math.Max(maxMb, mb);
                    }
                }

                if (maxMb > 0)
                {
                    return maxMb * 1024 * 1024;
                }
            }

            return 0;
        }

        private static bool TryRunNvidiaSmi(string fileName, string arguments, out string output)
        {
            output = string.Empty;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(startInfo);
                if (process == null)
                {
                    return false;
                }

                output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(1500);
                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
