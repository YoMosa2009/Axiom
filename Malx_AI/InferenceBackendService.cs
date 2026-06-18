using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using LLama.Common;
using LLama.Sampling;

namespace Malx_AI
{
    public static class ModelInferenceProfiles
    {
        public const string DefaultQwen3DisplayName = "Axiom Qwen3-4B";
        public const string DefaultQwen3FileName = "Qwen3-4B-Q4_K_M.gguf";

        public static readonly InferenceParams Qwen3NonThinkingParams = new InferenceParams
        {
            MaxTokens = 2048,
            AntiPrompts = new List<string>(),
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.7f,
                TopP = 0.8f,
                TopK = 20,
                MinP = 0.0f,
                RepeatPenalty = 1.05f
            }
        };

        public static readonly InferenceParams Qwen3ThinkingParams = new InferenceParams
        {
            MaxTokens = 2048,
            AntiPrompts = new List<string>(),
            SamplingPipeline = new DefaultSamplingPipeline
            {
                Temperature = 0.6f,
                TopP = 0.95f,
                TopK = 20,
                MinP = 0.0f,
                RepeatPenalty = 1.05f
            }
        };

        public static InferenceParams CreateQwen3InferenceParams(bool thinkingEnabled, int maxTokens, IEnumerable<string>? antiPrompts)
        {
            var template = thinkingEnabled ? Qwen3ThinkingParams : Qwen3NonThinkingParams;
            var pipeline = template.SamplingPipeline as DefaultSamplingPipeline;

            return new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = antiPrompts?.ToList() ?? new List<string>(),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = pipeline?.Temperature ?? 0.7f,
                    TopP = pipeline?.TopP ?? 0.8f,
                    TopK = pipeline?.TopK ?? 20,
                    MinP = pipeline?.MinP ?? 0.0f,
                    RepeatPenalty = pipeline?.RepeatPenalty ?? 1.05f
                }
            };
        }
    }

    public enum InferenceComputeMode
    {
        CpuOnly,
        GpuAccelerated
    }

    public sealed class InferenceBackendPlan
    {
        public required ModelParams Parameters { get; init; }
        public required HardwareProfile HardwareProfile { get; init; }
        public required string BackendName { get; init; }
        public required string Reason { get; init; }
        public bool UsingGpu => Parameters.GpuLayerCount > 0;
    }

    public static class InferenceBackendService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> ScopedInferenceGates = new(StringComparer.OrdinalIgnoreCase);
        public const string ModelLoadScope = "model-load";
        public const string NormalChatScope = "normal-chat";
        public const string WorkplaceCouncilScope = "workplace-council";

        public static InferenceComputeMode CurrentMode { get; set; } = InferenceComputeMode.CpuOnly;

        public static InferenceBackendPlan CreatePlan(string modelPath, uint requestedContext, InferenceComputeMode mode)
        {
            var profile = HardwareProfiler.Capture();
            bool gpuRequested = mode == InferenceComputeMode.GpuAccelerated;
            bool gpuRuntimeAvailable = HardwareProfiler.IsNvidiaRuntimeAvailable();

            // NativeBackendInit.Configure() was already called in App.xaml.cs at startup.
            // The native library is locked in by this point. We only allow GPU layers
            // if the backend was actually configured for CUDA at startup.
            bool allowGpu = gpuRequested && profile.HasNvidiaGpu && gpuRuntimeAvailable && NativeBackendInit.GpuConfigured;

            if (gpuRequested && !NativeBackendInit.GpuConfigured)
            {
                Debug.WriteLine($"[InferenceBackendService] GPU requested but NativeBackendInit did not configure CUDA. Reason: {NativeBackendInit.DiagnosticMessage}");
            }

            var parameters = HardwareProfiler.BuildSafeModelParams(modelPath, requestedContext, profile, allowGpu);

            string backend = parameters.GpuLayerCount > 0 ? "CUDA" : "CPU";
            string reason = parameters.GpuLayerCount > 0
                ? $"GPU mode active on {profile.PrimaryGpuName} (compute {profile.GpuComputeCapability:0.0}, FlashAttn {(parameters.FlashAttention == true ? "on" : "off")})."
                : BuildCpuReason(mode, profile, gpuRuntimeAvailable);

            Debug.WriteLine($"[InferenceBackendService] Plan: backend={backend}, gpuLayers={parameters.GpuLayerCount}, flashAttn={parameters.FlashAttention}, allowGpu={allowGpu}, nativeInit={NativeBackendInit.DiagnosticMessage}");

            return new InferenceBackendPlan
            {
                Parameters = parameters,
                HardwareProfile = profile,
                BackendName = backend,
                Reason = reason
            };
        }

        /// <summary>
        /// Plan for a model whose weights are already loaded (council model cache).
        /// Keeps the cached GPU/CPU layer split and sizes only the context from the
        /// memory that is currently free. See HardwareProfiler.BuildParamsForCachedWeights.
        /// </summary>
        public static InferenceBackendPlan CreatePlanForCachedWeights(string modelPath, uint requestedContext, int cachedGpuLayerCount)
        {
            var profile = HardwareProfiler.Capture();
            var parameters = HardwareProfiler.BuildParamsForCachedWeights(modelPath, requestedContext, profile, cachedGpuLayerCount);

            string backend = parameters.GpuLayerCount > 0 ? "CUDA" : "CPU";
            string reason = parameters.GpuLayerCount > 0
                ? $"Reusing cached weights on {profile.PrimaryGpuName} ({parameters.GpuLayerCount} GPU layers)."
                : "Reusing cached CPU weights.";

            Debug.WriteLine($"[InferenceBackendService] Cached-weights plan: backend={backend}, gpuLayers={parameters.GpuLayerCount}, ctx={parameters.ContextSize}");

            return new InferenceBackendPlan
            {
                Parameters = parameters,
                HardwareProfile = profile,
                BackendName = backend,
                Reason = reason
            };
        }

        public static async Task<T> RunExclusiveAsync<T>(Func<Task<T>> action)
        {
            return await RunScopedExclusiveAsync(ModelLoadScope, action);
        }

        public static async Task<T> RunScopedExclusiveAsync<T>(string scope, Func<Task<T>> action)
        {
            SemaphoreSlim gate = GetScopeGate(scope);
            await gate.WaitAsync();
            try
            {
                return await action();
            }
            finally
            {
                gate.Release();
            }
        }

        public static async Task RunExclusiveAsync(Func<Task> action)
        {
            await RunScopedExclusiveAsync(ModelLoadScope, action);
        }

        public static async Task RunScopedExclusiveAsync(string scope, Func<Task> action)
        {
            SemaphoreSlim gate = GetScopeGate(scope);
            await gate.WaitAsync();
            try
            {
                await action();
            }
            finally
            {
                gate.Release();
            }
        }

        public static string GetRecommendedModeLabel(HardwareProfile profile)
        {
            return HardwareProfiler.GetRecommendedModeLabel(profile);
        }

        private static string BuildCpuReason(InferenceComputeMode mode, HardwareProfile profile, bool gpuRuntimeAvailable)
        {
            if (mode == InferenceComputeMode.CpuOnly)
            {
                return "CPU mode selected by user.";
            }

            if (!NativeBackendInit.GpuConfigured)
            {
                return $"CUDA backend not available ({NativeBackendInit.DiagnosticMessage}); using CPU mode.";
            }

            if (!profile.HasNvidiaGpu)
            {
                if (profile.HasAmdGpu)
                {
                    return "AMD GPU detected. CUDA backend requires NVIDIA; using CPU mode.";
                }

                return "No NVIDIA GPU detected; using CPU mode.";
            }

            if (!gpuRuntimeAvailable)
            {
                return "NVIDIA GPU detected but CUDA runtime unavailable; using CPU mode.";
            }

            return "GPU mode requested, but parameters resolved to CPU safety profile.";
        }

        /// <summary>
        /// Runs a token stream while holding the scope gate for the duration of the
        /// enumeration only. Use this instead of RunScopedExclusiveAsync when the caller
        /// (e.g. the agentic pause loop) may start nested inference between streams —
        /// holding the gate across tool dispatch would deadlock.
        /// </summary>
        public static async IAsyncEnumerable<string> RunScopedExclusiveStream(
            string scope,
            Func<IAsyncEnumerable<string>> streamFactory,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            SemaphoreSlim gate = GetScopeGate(scope);
            await gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await foreach (string piece in streamFactory().WithCancellation(token).ConfigureAwait(false))
                {
                    yield return piece;
                }
            }
            finally
            {
                gate.Release();
            }
        }

        private static SemaphoreSlim GetScopeGate(string scope)
        {
            string normalizedScope = string.IsNullOrWhiteSpace(scope) ? ModelLoadScope : scope.Trim();

            // Normal chat and the workplace council must NEVER decode concurrently:
            // llama.cpp decodes from two surfaces at once oversubscribe CPU threads,
            // spike VRAM, and have produced InvalidInputBatch/abort crashes. Map both
            // scopes onto a single native-decode gate.
            if (string.Equals(normalizedScope, NormalChatScope, StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedScope, WorkplaceCouncilScope, StringComparison.OrdinalIgnoreCase))
            {
                normalizedScope = "native-decode";
            }

            return ScopedInferenceGates.GetOrAdd(normalizedScope, static _ => new SemaphoreSlim(1, 1));
        }
    }
}
