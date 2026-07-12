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
        /// <summary>
        /// NVIDIA CUDA compute capability (e.g. 6.1 for Pascal, 7.5 for Turing), or 0 when
        /// it could not be probed. Flash Attention is only numerically reliable / accelerated
        /// from Turing (7.5) onward — on older architectures llama.cpp's FA kernel can emit
        /// NaN logits, producing gibberish output that ends in a native GGML abort.
        /// </summary>
        public double GpuComputeCapability { get; set; }
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
                GpuComputeCapability = gpuIdentity.GpuComputeCapability,
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

            // Flash Attention is only enabled on GPUs/models where it is numerically safe;
            // an oversold FA path produces gibberish and crashes (see ShouldEnableFlashAttention).
            bool flashAttention = gpuCandidate && ShouldEnableFlashAttention(modelPath, profile);

            // Quantizing the KV cache to q8_0 halves its memory cost with negligible quality
            // loss, but llama.cpp REQUIRES flash attention for a quantized V-cache — without it
            // the decode aborts the process. So KV quant is gated on FA being enabled.
            // Applied from ~2GB up (was 4GB): this is what lets a 4B-class model — including the
            // default workplace model — run a much LARGER context window in the same VRAM (the
            // halved KV cache buys ~2× the usable context with full GPU offload), which is the
            // single biggest lever for reducing truncation-driven hallucination without losing speed.
            bool useKvQuant = gpuCandidate && flashAttention && fileBytes > 2L * 1024 * 1024 * 1024;

            long kvBytesPerToken = meta?.KvBytesPerTokenF16 ?? EstimateKvBytesPerTokenFromSize(fileBytes);
            long effectiveKvPerToken = useKvQuant ? kvBytesPerToken / 2 : kvBytesPerToken;

            // Physical micro-batch drives prompt-processing throughput (ingesting documents and long
            // history — which matters more now that contexts are larger) AND the CUDA compute-buffer
            // VRAM cost. Pick it from free VRAM headroom so it only steps up where there is clearly
            // room, then charge its compute buffer in the sizing/layer math below so a bigger batch can
            // never strand layers on the CPU or OOM. Constrained cards (≤ ~12 GB free) keep the safe 256/512.
            uint gpuMicroBatch = ChooseGpuMicroBatch(profile, gpuCandidate, largeModel);
            long computeReserve = gpuCandidate
                ? GpuComputeBufferReserveBytes(gpuMicroBatch)
                : 640L * 1024 * 1024;

            uint safeContext = ResolveSafeContext(requestedContext, fileBytes, meta, profile, gpuCandidate, effectiveKvPerToken, computeReserve);

            int gpuLayers = 0;
            if (gpuCandidate)
            {
                gpuLayers = CalculateGpuLayerCount(fileBytes, profile.AvailableVramBytes, safeContext, effectiveKvPerToken, meta?.BlockCount, IsPartialGpuSplitUnsafe(meta), computeReserve);
            }

            // When most layers stay on the CPU, the weights working set lives in RAM.
            // Shrink the context if the model is large relative to free RAM so the KV
            // cache and compute buffers cannot push the process into the page file.
            // Only applied on the heuristic path: with GGUF metadata, ResolveSafeContext
            // already charged the weights against the combined pool — halving again here
            // double-punished partially offloaded models.
            int totalLayers = meta?.BlockCount ?? EstimateLayerCountFromSize(fileBytes);
            if (gpuCandidate && gpuLayers == 0 && profile.AvailableVramBytes > 0)
            {
                uint recoveredContext = FindLargestGpuBackedContext(
                    safeContext,
                    requestedContext,
                    meta,
                    fileBytes,
                    profile.AvailableVramBytes,
                    effectiveKvPerToken,
                    computeReserve);
                if (recoveredContext > 0 && recoveredContext < safeContext)
                {
                    int recoveredLayers = CalculateGpuLayerCount(
                        fileBytes,
                        profile.AvailableVramBytes,
                        recoveredContext,
                        effectiveKvPerToken,
                        meta?.BlockCount,
                        IsPartialGpuSplitUnsafe(meta),
                        computeReserve);

                    if (recoveredLayers > 0)
                    {
                        safeContext = recoveredContext;
                        gpuLayers = recoveredLayers;
                    }
                }
            }

            bool fullOffload = IsFullGpuOffload(gpuLayers, totalLayers);
            if (meta == null && !fullOffload)
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

            ApplyPerformanceTuning(modelParams, gpuLayers, fullOffload, largeModel, useKvQuant, flashAttention, profile, gpuMicroBatch);

            Debug.WriteLine($"HardwareProfiler: model={Path.GetFileName(modelPath)}, requestedCtx={requestedContext}, ctx={safeContext}, gpuLayers={gpuLayers}/{totalLayers}, kvQuant={useKvQuant}, flashAttn={flashAttention}, computeCap={profile.GpuComputeCapability}, freeVram={profile.AvailableVramGb:F1}GB, threads={modelParams.Threads}, batch={modelParams.BatchSize}/{modelParams.UBatchSize}");
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
            bool flashAttention = gpuResident && ShouldEnableFlashAttention(modelPath, profile);
            // Must match BuildSafeModelParams' gate so a cached-weights replan prices the
            // KV cache the same way the original load did (q8_0 from ~2GB up on FA-capable GPUs).
            bool useKvQuant = gpuResident && flashAttention && fileBytes > 2L * 1024 * 1024 * 1024;

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

            // Weights are already resident, so size the micro-batch from CURRENT free VRAM (what is left
            // for the KV cache + compute buffer). Conservative by construction: less free VRAM → smaller
            // batch.
            uint gpuMicroBatch = ChooseGpuMicroBatch(profile, gpuResident, largeModel);
            ApplyPerformanceTuning(modelParams, cachedGpuLayerCount, IsFullGpuOffload(cachedGpuLayerCount, totalLayers), largeModel, useKvQuant, flashAttention, profile, gpuMicroBatch);

            Debug.WriteLine($"HardwareProfiler: cached-weights plan model={Path.GetFileName(modelPath)}, ctx={safeContext}, gpuLayers={cachedGpuLayerCount}/{totalLayers} (kept from cache), flashAttn={flashAttention}, ubatch={gpuMicroBatch}");
            return modelParams;
        }

        /// <summary>
        /// Picks the context size from real memory math when GGUF metadata is available:
        /// whatever KV cache fits in half the memory left after the weights, never above
        /// the model's trained context or the caller's request. Falls back to the static
        /// file-size table when the header could not be parsed.
        ///
        /// The pool is VRAM PLUS system RAM for GPU candidates: with partial offload the
        /// CPU-resident layers' weights AND their KV slices live in RAM, so sizing the
        /// context as if everything had to fit in VRAM alone systematically over-shrank
        /// it (a 9B model on an 8GB card planned ~3k even with 16GB of RAM free).
        /// </summary>
        private static uint ResolveSafeContext(uint requestedContext, long fileBytes, GgufModelMetadata? meta, HardwareProfile profile, bool gpuCandidate, long kvBytesPerToken, long computeReserve)
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

            // PERFORMANCE-FIRST GPU path: prefer the largest context whose KV cache still leaves room
            // for EVERY layer on the GPU. Full offload is by far the biggest speed factor, so we never
            // grow the window past the point where it would spill layers onto the CPU. Only if the model
            // cannot fully offload even at a small window (big model / small card — it will be partial
            // regardless) do we fall through to the VRAM+RAM pool math and maximize context there.
            // This is what lets the larger requested windows reduce hallucination WITHOUT costing speed.
            if (gpuCandidate && profile.AvailableVramBytes > 0 && kvBytesPerToken > 0)
            {
                const long sessionReserve = 512L * 1024 * 1024; // mirrors CalculateGpuLayerCount's cap
                long vramForKv = profile.AvailableVramBytes - fileBytes - computeReserve - sessionReserve;
                long fullOffloadCtx = vramForKv > 0 ? vramForKv / kvBytesPerToken : 0;
                if (fullOffloadCtx >= 2048)
                {
                    uint capped = (uint)Math.Clamp(Math.Min((long)upperBound, fullOffloadCtx), 1024L, 65536L);
                    return Math.Max(1024u, capped);
                }
                // else: cannot fully offload even at a minimal window → partial offload happens anyway,
                // so fall through and size context from the combined VRAM+RAM pool below.
            }

            long ramPool = (long)(profile.AvailableRamBytes * 0.70);
            long memPool = gpuCandidate && profile.AvailableVramBytes > 0
                ? profile.AvailableVramBytes + ramPool
                : ramPool;

            long memAfterWeights = memPool - fileBytes;
            if (meta != null && kvBytesPerToken > 0 && memAfterWeights > 0)
            {
                long kvBudget = memAfterWeights / 2;
                long ctxByMemory = Math.Clamp(kvBudget / kvBytesPerToken, 1024, 32768);
                return Math.Clamp((uint)ctxByMemory, 1024u, Math.Max(1024u, upperBound));
            }

            return Math.Clamp(Math.Min(upperBound, maxCtxForSize), 1024u, 32768u);
        }

        private static uint FindLargestGpuBackedContext(
            uint currentContext,
            uint requestedContext,
            GgufModelMetadata? meta,
            long fileBytes,
            long freeVramBytes,
            long kvBytesPerToken,
            long computeReserve)
        {
            if (currentContext <= 1024 || freeVramBytes <= 0 || kvBytesPerToken <= 0)
                return 0;

            uint upperBound = currentContext;
            if (requestedContext > 0)
                upperBound = Math.Min(upperBound, requestedContext);
            if (meta?.ContextLength is > 0)
                upperBound = Math.Min(upperBound, (uint)meta.ContextLength);

            uint[] candidates =
            [
                upperBound,
                32768u,
                24576u,
                16384u,
                12288u,
                8192u,
                6144u,
                4096u,
                3072u,
                2048u,
                1536u,
                1024u
            ];

            foreach (uint candidate in candidates
                         .Where(c => c <= upperBound && c >= 1024)
                         .Distinct()
                         .OrderByDescending(c => c))
            {
                int layers = CalculateGpuLayerCount(
                    fileBytes,
                    freeVramBytes,
                    candidate,
                    kvBytesPerToken,
                    meta?.BlockCount,
                    IsPartialGpuSplitUnsafe(meta),
                    computeReserve);

                if (layers > 0)
                    return candidate;
            }

            return 0;
        }

        private static bool IsFullGpuOffload(int gpuLayers, int totalLayers)
            => totalLayers > 0 && gpuLayers >= totalLayers;

        private static bool IsPartialGpuSplitUnsafe(GgufModelMetadata? meta)
            => meta?.IsHybridRecurrent == true || meta?.IsSlidingWindowAttention == true;

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
                    string? nvidiaName = names.FirstOrDefault(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
                    string? amdName = names.FirstOrDefault(n => n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase));
                    profile.PrimaryGpuName = nvidiaName ?? amdName ?? names[0];
                }

                profile.HasNvidiaGpu = names.Any(n => n.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
                profile.HasAmdGpu = names.Any(n => n.Contains("AMD", StringComparison.OrdinalIgnoreCase) || n.Contains("Radeon", StringComparison.OrdinalIgnoreCase));

                if (profile.HasNvidiaGpu)
                    profile.GpuComputeCapability = ProbeNvidiaComputeCapability(profile.PrimaryGpuName);
            }
            catch
            {
                profile.PrimaryGpuName = "Unknown";
            }

            return profile;
        }

        /// <summary>
        /// Reads the CUDA compute capability from nvidia-smi (e.g. 6.1, 7.5, 8.6). Falls back
        /// to inferring it from the GPU marketing name when nvidia-smi is unavailable or too
        /// old to support the query, so the Flash Attention gate still works on a bare driver.
        /// </summary>
        private static double ProbeNvidiaComputeCapability(string gpuName)
        {
            foreach (var candidate in NvidiaSmiCandidates)
            {
                if (TryRunNvidiaSmi(candidate, "--query-gpu=compute_cap --format=csv,noheader", out var output)
                    && !string.IsNullOrWhiteSpace(output))
                {
                    string first = output
                        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(x => x.Trim())
                        .FirstOrDefault(x => double.TryParse(x, NumberStyles.Any, CultureInfo.InvariantCulture, out _))
                        ?? string.Empty;

                    if (double.TryParse(first, NumberStyles.Any, CultureInfo.InvariantCulture, out double cap) && cap > 0)
                        return cap;
                }
            }

            return InferComputeCapabilityFromName(gpuName);
        }

        // Best-effort architecture inference from the GPU name. Only needs to separate
        // pre-Turing (< 7.5, Flash-Attention-unsafe) from Turing-and-newer.
        private static double InferComputeCapabilityFromName(string gpuName)
        {
            if (string.IsNullOrWhiteSpace(gpuName))
                return 0; // Unknown — caller treats 0 as "do not trust Flash Attention".

            string n = gpuName.ToUpperInvariant();

            // Pascal (6.x): GTX 10-series, Titan X/Xp, Quadro P, Tesla P.
            if (n.Contains("GTX 10") || n.Contains("GTX10") || n.Contains("TITAN X") || n.Contains("TITAN XP")
                || n.Contains("QUADRO P") || n.Contains("TESLA P") || n.Contains(" P100") || n.Contains(" P40"))
                return 6.1;

            // Maxwell (5.x): GTX 9-series, GTX 750, Quadro M, Tesla M.
            if (n.Contains("GTX 9") || n.Contains("GTX 750") || n.Contains("QUADRO M") || n.Contains("TESLA M"))
                return 5.2;

            // Kepler (3.x): GTX 6/7-series, Tesla K.
            if (n.Contains("GTX 6") || n.Contains("GTX 7") || n.Contains("TESLA K"))
                return 3.5;

            // Turing and newer are Flash-Attention-safe: GTX 16xx, RTX 20/30/40/50,
            // and the A/H/L datacenter lines. Report a representative Turing value.
            if (n.Contains("GTX 16") || n.Contains("RTX") || n.Contains("A100") || n.Contains("H100")
                || n.Contains("L40") || n.Contains(" A40") || n.Contains(" T4"))
                return 7.5;

            return 0; // Unknown NVIDIA card — be conservative and skip Flash Attention.
        }

        /// <summary>
        /// Flash Attention is a perf optimization, not a requirement — and it is only safe on
        /// Turing (compute 7.5) and newer NVIDIA GPUs. On older architectures (Pascal/Maxwell/
        /// Kepler) llama.cpp's FA path can produce NaN attention scores → gibberish tokens →
        /// a native GGML abort that kills the whole process. It is ALSO incompatible with the
        /// attention logit soft-capping that Gemma 2/3 rely on, regardless of GPU. In both
        /// cases we disable it: correctness and stability outrank throughput.
        /// </summary>
        /// <summary>
        /// Lightweight "will this model load with flash attention (and therefore KV-cache
        /// quantization) on this machine?" check for context-budget planning. Uses the CACHED GPU
        /// identity (compute capability) and does NOT run the nvidia-smi free-memory probe, so it is
        /// cheap to call from the UI. Callers use it to decide how large a context to aspire to:
        /// with FA the q8_0 KV cache lets a big window fit; without it a big f16 window steals GPU
        /// layers and slows generation, so they stay conservative.
        /// </summary>
        public static bool SupportsFlashAttention(string modelPath)
        {
            HardwareProfile identity;
            lock (_probeLock)
            {
                _cachedGpuIdentity ??= ProbeGraphicsHardware();
                identity = _cachedGpuIdentity;
            }

            return identity.HasNvidiaGpu
                && NativeBackendInit.GpuConfigured
                && ShouldEnableFlashAttention(modelPath, identity);
        }

        public static bool ShouldEnableFlashAttention(string modelPath, HardwareProfile profile)
        {
            string fileName = Path.GetFileNameWithoutExtension(modelPath ?? string.Empty);
            if (IsSoftCappingModel(fileName))
                return false;

            // Compute capability of 0 means "unknown" — treat as unsafe rather than risk
            // a hard crash on an unidentified older card.
            return profile.GpuComputeCapability >= 7.5;
        }

        // Gemma 2 and Gemma 3 use attention/final-logit soft-capping that llama.cpp's Flash
        // Attention kernels skip, exploding the logits into NaNs. Gemma 4 runs via the CLI
        // runner, not this path. The name match is intentionally broad (any non-"gemma-4"
        // gemma) so a renamed GGUF still gets the safe path.
        private static bool IsSoftCappingModel(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            bool isGemma = fileName.Contains("gemma", StringComparison.OrdinalIgnoreCase);
            bool isGemma4 = fileName.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("gemma4", StringComparison.OrdinalIgnoreCase)
                || fileName.Contains("gemma_4", StringComparison.OrdinalIgnoreCase);
            return isGemma && !isGemma4;
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
        private static int CalculateGpuLayerCount(long modelBytes, long freeVramBytes, uint contextSize, long kvBytesPerToken, int? actualLayerCount, bool partialGpuSplitUnsafe, long computeReserve)
        {
            int totalLayers = actualLayerCount ?? EstimateLayerCountFromSize(modelBytes);

            if (freeVramBytes <= 0)
            {
                // VRAM probe failed. Be optimistic only for small models; GPU init
                // failure falls back to CPU at the call sites.
                return modelBytes <= 5L * 1024 * 1024 * 1024 ? totalLayers + 1 : 0;
            }

            long kvTotal = Math.Max(64L * 1024 * 1024, kvBytesPerToken * contextSize);
            // CUDA compute buffers + fragmentation headroom, sized to the chosen micro-batch so a larger
            // batch reserves the VRAM its bigger compute buffer needs instead of risking an OOM.
            // Headroom for short-lived secondary contexts (council base-state vault,
            // KV-state loads). These never coexist with more than one main context.
            // Scaling this with HALF the full KV cache reserved gigabytes on long-context
            // plans and routinely cost 5-10 offloadable layers; the secondary contexts it
            // covers are short-lived and small, so a quarter capped at 512MB is plenty.
            long sessionReserve = Math.Clamp(kvTotal / 4, 192L * 1024 * 1024, 512L * 1024 * 1024);

            long budget = freeVramBytes - computeReserve - sessionReserve;
            if (budget <= 0)
                return 0;

            // +1 accounts for the output/embedding tensors offloaded with the last layer.
            long perLayerWeights = modelBytes / Math.Max(1, totalLayers + 1);
            long kvPerLayer = kvTotal / Math.Max(1, totalLayers);

            // Full offload: all weights plus the whole KV cache fit in the budget.
            if (modelBytes + kvTotal <= budget)
                return totalLayers + 1;

            // Hybrid recurrent (Mamba/SSM) and sliding-window-attention models that do NOT fully
            // fit must go to CPU, not a partial split: splitting their special state/KV caches
            // across CPU and GPU has produced CUDA illegal-memory-access aborts during decode.
            if (partialGpuSplitUnsafe)
                return 0;

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
        /// Picks the physical micro-batch (UBatchSize) for a GPU plan from free-VRAM headroom. A larger
        /// micro-batch processes more prompt tokens per CUDA kernel launch — the main lever for faster
        /// prompt ingestion of documents and long history — but costs a proportionally larger compute
        /// buffer, so it only steps up where there is clearly room. The common 8 GB-class card keeps the
        /// proven 512 (no new OOM risk); only 12 GB+ cards go higher. Large models stay conservative.
        /// </summary>
        private static uint ChooseGpuMicroBatch(HardwareProfile profile, bool gpuCandidate, bool largeModel)
        {
            if (!gpuCandidate)
                return 512u;

            double freeGb = profile.AvailableVramGb;
            if (freeGb <= 0)
                return largeModel ? 256u : 512u;   // unknown free VRAM — be conservative
            if (largeModel || freeGb < 6.0)
                return 256u;
            if (freeGb < 12.0)
                return 512u;                        // 6-12 GB (incl. common 8 GB cards): unchanged
            if (freeGb < 20.0)
                return 1024u;                       // 12-16 GB cards
            return 2048u;                           // 24 GB+ cards
        }

        /// <summary>
        /// VRAM to reserve for the CUDA compute buffer + fragmentation headroom, sized to the chosen
        /// micro-batch. Charged in the layer math so a larger micro-batch can never strand layers on the
        /// CPU or overflow the card. Deliberately generous — under-reserving here is what causes OOMs.
        /// </summary>
        private static long GpuComputeBufferReserveBytes(uint gpuMicroBatch)
        {
            return gpuMicroBatch switch
            {
                <= 512u  => 640L * 1024 * 1024,
                <= 1024u => 1024L * 1024 * 1024,
                _        => 1536L * 1024 * 1024
            };
        }

        /// <summary>
        /// Applies thread, batch, flash-attention, and KV-cache settings tuned to the
        /// chosen offload split. Runs for both CPU and GPU plans.
        /// </summary>
        private static void ApplyPerformanceTuning(ModelParams modelParams, int gpuLayers, bool fullOffload, bool largeModel, bool useKvQuant, bool flashAttention, HardwareProfile profile, uint gpuMicroBatch = 512)
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
                // Larger logical batch improves prompt throughput on CUDA. VRAM cost is driven by the
                // physical micro-batch (UBatchSize), not the logical batch, so a large BatchSize is
                // nearly free and lets CUDA chew through long prompts (documents!) in far fewer kernel
                // launches. The micro-batch was chosen from free-VRAM headroom (ChooseGpuMicroBatch) and
                // its compute buffer was already reserved in the layer math, so a bigger batch here is
                // safe — it speeds prompt processing without stranding layers on the CPU.
                modelParams.BatchSize = Math.Max(2048u, gpuMicroBatch);
                modelParams.UBatchSize = gpuMicroBatch;

                // Only enabled where it is numerically safe — see ShouldEnableFlashAttention.
                modelParams.FlashAttention = flashAttention;

                // A quantized V-cache requires flash attention in llama.cpp; useKvQuant is
                // already gated on flashAttention, but assert the invariant defensively.
                if (useKvQuant && flashAttention)
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

            // Single conversation per context: the app never runs batched/parallel sequences.
            // n_seq_max=64 is harmless for the attention KV cache (sized by tokens) but
            // CATASTROPHIC for hybrid Mamba/SSM models: their recurrent-state ("rs") cache is
            // sized PER SEQUENCE, so 64 sequences inflates it ~64× (a Nemotron-H 4B needed 5.2 GB
            // of rs cache at 64 vs ~80 MB at 1). On full GPU offload that 5.2 GB landed in VRAM and
            // overflowed the card → cudaMalloc OOM at context creation → crash on the very first
            // role. One sequence is all this app uses, and n_seq_max=1 shrinks memory for every
            // model (strictly safe — sequence 0 always exists, so KV save/load still works).
            //
            // IMPORTANT: setting this property is NOT enough. LLamaSharp 0.26.0's
            // ToLlamaContextParams reads SeqMax and then OVERWRITES n_seq_max with
            // clamp(n_ctx/8, 10, 64), so contexts must be created through LlamaContextFactory,
            // which restores n_seq_max=1 after that overwrite. We still set it here so the value is
            // correct if the library is ever fixed, and to document intent at the params level.
            modelParams.SeqMax = 1;

            // Compact the KV cache when fragmentation passes 10% — prevents spurious
            // NoKvSlot failures in long interactive sessions. LLamaSharp 0.26 marks the
            // property obsolete (upstream llama.cpp is deprecating manual defrag), but it is
            // still honored in this version; keep the behavior until the library removes it.
#pragma warning disable CS0612
            modelParams.DefragThreshold = 0.1f;
#pragma warning restore CS0612
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
