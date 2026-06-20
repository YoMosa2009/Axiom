using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using LLama;
using LLama.Abstractions;
using LLama.Extensions;
using LLama.Native;

namespace Malx_AI
{
    /// <summary>
    /// Creates an <see cref="LLamaContext"/> with <c>n_seq_max</c> forced to a chosen value
    /// (default 1, which is all this single-conversation app ever uses).
    ///
    /// WHY THIS EXISTS — a hard, otherwise-undebuggable crash:
    /// LLamaSharp 0.26.0's <c>IContextParamsExtensions.ToLlamaContextParams</c> reads our
    /// <c>ModelParams.SeqMax</c> into <c>n_seq_max</c> and then UNCONDITIONALLY overwrites it
    /// with <c>clamp(n_ctx / 8, 10, 64)</c> — so for any usable context size <c>n_seq_max</c>
    /// becomes 64 regardless of the SeqMax we set (HardwareProfiler's <c>SeqMax = 1</c> is dead
    /// code against this library). For ordinary transformer models that is merely wasteful (the
    /// attention KV cache is sized per token), but for HYBRID recurrent models (Mamba/SSM:
    /// nemotron_h, jamba, falcon_h, RWKV) the recurrent-state ("rs") cache is sized PER SEQUENCE,
    /// so <c>n_seq_max = 64</c> inflates it ~64×. A real measured case: NVIDIA-Nemotron-3-Nano-4B
    /// needs ~5.2 GB of rs cache at 64 vs ~80 MB at 1. On an 8 GB GPU (2.4 GB weights + 5.2 GB rs)
    /// that overflows the card → <c>cudaMalloc failed: out of memory</c> → the rs-cache buffer
    /// allocation fails inside context creation and the process dies on the very FIRST council
    /// role (Architect), before any decode-forensics marker is written — so the crash never even
    /// self-heals into a CPU fallback. Forcing <c>n_seq_max = 1</c> shrinks the rs cache back to
    /// tens of MB, letting the hybrid fully offload to GPU and run fast and stable.
    ///
    /// We cannot patch the package, and no <see cref="IContextParams"/> value survives the
    /// library's overwrite, so we reproduce its own context creation: build the fully-tuned native
    /// params via the public <c>ToLlamaContextParams</c> extension, restore <c>n_seq_max</c> AFTER
    /// its overwrite, create the native handle directly (a public API), and wrap it in an
    /// <see cref="LLamaContext"/> (whose only public constructor would re-run the overwrite). If
    /// any step fails — e.g. a future LLamaSharp renames an internal field — we fall back to the
    /// stock <see cref="LLamaWeights.CreateContext(IContextParams, Microsoft.Extensions.Logging.ILogger?)"/>,
    /// so callers never break; they only lose this fix.
    /// </summary>
    public static class LlamaContextFactory
    {
        private static int _loggedOnce;

        /// <summary>
        /// Creates a context for <paramref name="weights"/> using <paramref name="contextParams"/>,
        /// overriding the native <c>n_seq_max</c> to <paramref name="seqMax"/>. Falls back to the
        /// stock context creation path if the override cannot be applied.
        /// </summary>
        public static LLamaContext CreateContext(LLamaWeights weights, IContextParams contextParams, uint seqMax = 1)
        {
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            if (contextParams == null) throw new ArgumentNullException(nameof(contextParams));

            try
            {
                contextParams.ToLlamaContextParams(out LLamaContextParams native);

                // Library already produced what we want — nothing to work around.
                if (native.n_seq_max == seqMax)
                    return weights.CreateContext(contextParams);

                uint libraryValue = native.n_seq_max;
                native.n_seq_max = seqMax;

                SafeLLamaContextHandle handle = SafeLLamaContextHandle.Create(weights.NativeHandle, native);

                // LLamaContext's only public constructor would call ToLlamaContextParams again and
                // re-apply the overwrite, so build the object without a constructor and populate the
                // same state the constructor sets (NativeHandle/Params/Encoding/Vocab; _logger=null).
                LLamaContext ctx;
                try
                {
                    ctx = (LLamaContext)RuntimeHelpers.GetUninitializedObject(typeof(LLamaContext));
                    SetBackingField(ctx, "NativeHandle", handle);
                    SetBackingField(ctx, "Params", contextParams);
                    SetBackingField(ctx, "Encoding", contextParams.Encoding);
                    SetBackingField(ctx, "Vocab", weights.Vocab);
                }
                catch
                {
                    // Wrapping failed — don't leak the native handle we just created.
                    handle.Dispose();
                    throw;
                }

                if (Interlocked.Exchange(ref _loggedOnce, 1) == 0)
                    Debug.WriteLine($"[LlamaContextFactory] Forced n_seq_max {libraryValue} -> {seqMax} (LLamaSharp 0.26.0 clamps it to clamp(n_ctx/8,10,64); hybrid recurrent models OOM at 64).");

                return ctx;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[LlamaContextFactory] n_seq_max override unavailable, using stock context: {ex.Message}");
                return weights.CreateContext(contextParams);
            }
        }

        // C# auto-properties with a private/init setter are backed by "<Name>k__BackingField".
        private static void SetBackingField(LLamaContext ctx, string propertyName, object? value)
        {
            FieldInfo? field = typeof(LLamaContext).GetField(
                $"<{propertyName}>k__BackingField",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (field == null)
                throw new MissingFieldException(nameof(LLamaContext), propertyName);

            field.SetValue(ctx, value);
        }
    }
}
