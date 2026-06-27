using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Malx_AI
{
    public sealed class GgufModelMetadata
    {
        public string Architecture { get; init; } = "";
        public int BlockCount { get; init; }
        public int ContextLength { get; init; }
        public int EmbeddingLength { get; init; }
        public int HeadCount { get; init; }
        public int HeadCountKv { get; init; }
        public long? ParameterCount { get; init; }
        public string SizeLabel { get; init; } = "";

        /// <summary>
        /// Number of layers that actually allocate an attention KV cache. Equals BlockCount
        /// for a standard transformer. For HYBRID models (Nemotron-H, Jamba, Falcon-H,
        /// Mamba-2 hybrids) only a few layers use attention — the rest are Mamba/SSM/recurrent
        /// layers whose state does NOT grow with context — so this is much smaller than
        /// BlockCount. Parsed from the per-layer head_count_kv array; 0 when not a hybrid
        /// (the file exposed head_count_kv as a scalar), which callers read as "all layers".
        /// </summary>
        public int AttentionLayerCount { get; init; }

        /// <summary>Per-head key/value dimension in elements, when the file states it explicitly
        /// (some models, esp. hybrids, set head_dim ≠ embedding_length / head_count). 0 = unknown.</summary>
        public int KeyLength { get; init; }
        public int ValueLength { get; init; }

        /// <summary>
        /// Sliding-window size in tokens for interleaved sliding-window-attention (iSWA)
        /// models (e.g. Gemma 2/3). 0 when the model does not use sliding window attention.
        /// </summary>
        public int SlidingWindow { get; init; }

        /// <summary>
        /// True for models that interleave sliding-window and full attention layers. These
        /// allocate TWO KV caches (a full cache + an SWA cache), and partially offloading
        /// them — splitting either cache across CPU and GPU — has produced CUDA illegal
        /// memory access aborts. Such models must be either fully offloaded or run on CPU.
        /// </summary>
        public bool IsSlidingWindowAttention => SlidingWindow > 0;

        /// <summary>
        /// True for HYBRID recurrent models (Mamba/SSM mixed with attention: nemotron_h, jamba,
        /// falcon_h, …) — detected by only some layers carrying an attention KV head count.
        /// These keep a per-sequence recurrent-state ("rs") cache and, like sliding-window
        /// models, are fragile under PARTIAL offload: splitting their SSM layers across CPU and
        /// GPU has produced CUDA illegal-memory-access aborts during decode. They should be fully
        /// offloaded or run on CPU — never partially split.
        /// </summary>
        public bool IsHybridRecurrent => AttentionLayerCount > 0 && AttentionLayerCount < BlockCount;

        /// <summary>
        /// Bytes of KV-cache required per context token at f16 precision (K + V).
        /// Null when the file did not expose enough attention geometry.
        /// </summary>
        public long? KvBytesPerTokenF16
        {
            get
            {
                if (BlockCount <= 0 || HeadCount <= 0)
                    return null;

                int kvHeads = HeadCountKv > 0 ? HeadCountKv : HeadCount;

                // Hybrid Mamba/attention models charge KV only on their attention layers; the
                // rest keep a per-sequence recurrent state that does not scale with context.
                // Counting every block (the old behavior) over-estimated KV by ~the hybrid
                // ratio (~64× on Nemotron-H 4B), which blocked full GPU offload and stranded
                // layers on the CPU. Standard transformers leave AttentionLayerCount == 0 and
                // take the original BlockCount path below — this is byte-identical for them.
                bool hybrid = AttentionLayerCount > 0 && AttentionLayerCount < BlockCount;
                int attnLayers = hybrid ? AttentionLayerCount : BlockCount;

                long kDim, vDim;
                if (hybrid && KeyLength > 0)
                {
                    kDim = KeyLength;
                    vDim = ValueLength > 0 ? ValueLength : KeyLength;
                }
                else
                {
                    if (EmbeddingLength <= 0)
                        return null;
                    kDim = vDim = EmbeddingLength / HeadCount;
                }

                if (kDim <= 0)
                    return null;

                return (long)attnLayers * (kDim + vDim) * kvHeads * 2L /*f16 bytes*/;
            }
        }
    }

    /// <summary>
    /// Minimal GGUF header parser that extracts the metadata needed for accurate
    /// GPU-offload and KV-cache planning (real layer count, trained context length,
    /// attention geometry) without loading the model into memory.
    /// Supports GGUF v2 and v3. Returns null on any parse problem — callers must
    /// fall back to file-size heuristics.
    /// </summary>
    public static class GgufMetadataReader
    {
        private const uint GgufMagic = 0x46554747; // "GGUF" little-endian

        private static readonly ConcurrentDictionary<string, GgufModelMetadata?> Cache =
            new(StringComparer.OrdinalIgnoreCase);

        public static GgufModelMetadata? TryRead(string modelPath)
        {
            try
            {
                var info = new FileInfo(modelPath);
                string cacheKey = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
                return Cache.GetOrAdd(cacheKey, _ => ReadCore(modelPath));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GgufMetadataReader] {ex.Message}");
                return null;
            }
        }

        private static GgufModelMetadata? ReadCore(string modelPath)
        {
            try
            {
                using var stream = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

                if (reader.ReadUInt32() != GgufMagic)
                    return null;

                uint version = reader.ReadUInt32();
                if (version < 2 || version > 3)
                    return null;

                reader.ReadUInt64(); // tensor count — not needed
                ulong kvCount = reader.ReadUInt64();
                if (kvCount == 0 || kvCount > 4096)
                    return null;

                string architecture = "";
                int blockCount = 0, contextLength = 0, embeddingLength = 0, headCount = 0, headCountKv = 0, slidingWindow = 0;
                int attentionLayerCount = 0, keyLength = 0, valueLength = 0;
                long? parameterCount = null;
                string sizeLabel = "";

                for (ulong i = 0; i < kvCount; i++)
                {
                    string key = ReadGgufString(reader);
                    uint valueType = reader.ReadUInt32();

                    if (key == "general.architecture" && valueType == 8)
                    {
                        architecture = ReadGgufString(reader);
                        continue;
                    }

                    if (key == "general.size_label" && valueType == 8)
                    {
                        sizeLabel = ReadGgufString(reader);
                        continue;
                    }

                    if (key == "general.parameter_count" && TryReadIntegerValue(reader, valueType, out long paramValue))
                    {
                        parameterCount = paramValue;
                        continue;
                    }

                    // Hybrid Mamba/attention models (Nemotron-H, Jamba, …) expose
                    // head_count_kv as a PER-LAYER array: 0 for every recurrent/SSM layer,
                    // a nonzero KV-head count for the few real attention layers. It must be
                    // parsed as an array, not an integer — the generic path skipped it TWICE
                    // (TryReadIntegerValue's default skip + the loop's trailing skip), which
                    // desynced the parser and made TryRead return null for the whole model.
                    if (valueType == 9 && key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal))
                    {
                        if (TryReadIntArray(reader, out int[] perLayerKv))
                        {
                            int attn = 0, repKv = 0;
                            foreach (int c in perLayerKv)
                            {
                                if (c > 0) { attn++; repKv = Math.Max(repKv, c); }
                            }
                            attentionLayerCount = attn;
                            if (repKv > 0) headCountKv = repKv;
                        }
                        continue;
                    }

                    bool isWanted =
                        key.EndsWith(".block_count", StringComparison.Ordinal) ||
                        key.EndsWith(".context_length", StringComparison.Ordinal) ||
                        key.EndsWith(".embedding_length", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.head_count", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.key_length", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.value_length", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.sliding_window", StringComparison.Ordinal);

                    if (isWanted && TryReadIntegerValue(reader, valueType, out long value))
                    {
                        if (key.EndsWith(".block_count", StringComparison.Ordinal)) blockCount = (int)value;
                        else if (key.EndsWith(".context_length", StringComparison.Ordinal)) contextLength = (int)value;
                        else if (key.EndsWith(".embedding_length", StringComparison.Ordinal)) embeddingLength = (int)value;
                        else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)) headCountKv = (int)value;
                        else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal)) headCount = (int)value;
                        else if (key.EndsWith(".attention.key_length", StringComparison.Ordinal)) keyLength = (int)value;
                        else if (key.EndsWith(".attention.value_length", StringComparison.Ordinal)) valueLength = (int)value;
                        else if (key.EndsWith(".attention.sliding_window", StringComparison.Ordinal)) slidingWindow = (int)value;
                        continue;
                    }

                    SkipValue(reader, valueType);
                }

                if (blockCount <= 0)
                    return null;

                var meta = new GgufModelMetadata
                {
                    Architecture = architecture,
                    BlockCount = blockCount,
                    ContextLength = contextLength,
                    EmbeddingLength = embeddingLength,
                    HeadCount = headCount,
                    HeadCountKv = headCountKv,
                    AttentionLayerCount = attentionLayerCount,
                    KeyLength = keyLength,
                    ValueLength = valueLength,
                    SlidingWindow = slidingWindow,
                    ParameterCount = parameterCount,
                    SizeLabel = sizeLabel
                };

                Debug.WriteLine($"[GgufMetadataReader] {Path.GetFileName(modelPath)}: arch={architecture}, size={sizeLabel}, params={parameterCount}, layers={blockCount}, attnLayers={attentionLayerCount}, trainedCtx={contextLength}, kv/token={meta.KvBytesPerTokenF16}, swa={slidingWindow}");
                return meta;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GgufMetadataReader] Parse failed for '{modelPath}': {ex.Message}");
                return null;
            }
        }

        private static string ReadGgufString(BinaryReader reader)
        {
            ulong length = reader.ReadUInt64();
            if (length > 1 << 20)
                throw new InvalidDataException($"Unreasonable GGUF string length: {length}");

            byte[] bytes = reader.ReadBytes((int)length);
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool TryReadIntegerValue(BinaryReader reader, uint valueType, out long value)
        {
            switch (valueType)
            {
                case 0: value = reader.ReadByte(); return true;            // uint8
                case 1: value = reader.ReadSByte(); return true;           // int8
                case 2: value = reader.ReadUInt16(); return true;          // uint16
                case 3: value = reader.ReadInt16(); return true;           // int16
                case 4: value = reader.ReadUInt32(); return true;          // uint32
                case 5: value = reader.ReadInt32(); return true;           // int32
                case 10: value = (long)reader.ReadUInt64(); return true;   // uint64
                case 11: value = reader.ReadInt64(); return true;          // int64
                default:
                    SkipValue(reader, valueType);
                    value = 0;
                    return false;
            }
        }

        /// <summary>
        /// Reads a GGUF integer array (the element-type + count header has NOT been consumed
        /// yet — this reads it). Returns the values widened to int. Returns false (and leaves
        /// the stream wherever it got to) only for an implausible length or a non-integer
        /// element type; the sole caller (head_count_kv) is always a small integer array.
        /// </summary>
        private static bool TryReadIntArray(BinaryReader reader, out int[] values)
        {
            values = Array.Empty<int>();

            uint elemType = reader.ReadUInt32();
            ulong count = reader.ReadUInt64();
            if (count > (1 << 20))
                return false;

            // Per-layer arrays are small integers; bail on anything else rather than guess.
            var result = new int[(int)count];
            for (ulong i = 0; i < count; i++)
            {
                switch (elemType)
                {
                    case 0: result[i] = reader.ReadByte(); break;       // uint8
                    case 1: result[i] = reader.ReadSByte(); break;      // int8
                    case 2: result[i] = reader.ReadUInt16(); break;     // uint16
                    case 3: result[i] = reader.ReadInt16(); break;      // int16
                    case 4: result[i] = (int)reader.ReadUInt32(); break; // uint32
                    case 5: result[i] = reader.ReadInt32(); break;       // int32
                    case 10: result[i] = (int)reader.ReadUInt64(); break; // uint64
                    case 11: result[i] = (int)reader.ReadInt64(); break;  // int64
                    default:
                        return false;
                }
            }

            values = result;
            return true;
        }

        private static void SkipValue(BinaryReader reader, uint valueType)
        {
            switch (valueType)
            {
                case 0: case 1: case 7: // uint8, int8, bool
                    reader.BaseStream.Seek(1, SeekOrigin.Current);
                    break;
                case 2: case 3: // uint16, int16
                    reader.BaseStream.Seek(2, SeekOrigin.Current);
                    break;
                case 4: case 5: case 6: // uint32, int32, float32
                    reader.BaseStream.Seek(4, SeekOrigin.Current);
                    break;
                case 10: case 11: case 12: // uint64, int64, float64
                    reader.BaseStream.Seek(8, SeekOrigin.Current);
                    break;
                case 8: // string
                    ulong strLen = reader.ReadUInt64();
                    reader.BaseStream.Seek((long)strLen, SeekOrigin.Current);
                    break;
                case 9: // array
                    uint elemType = reader.ReadUInt32();
                    ulong count = reader.ReadUInt64();
                    long fixedSize = elemType switch
                    {
                        0 or 1 or 7 => 1,
                        2 or 3 => 2,
                        4 or 5 or 6 => 4,
                        10 or 11 or 12 => 8,
                        _ => 0
                    };

                    if (fixedSize > 0)
                    {
                        reader.BaseStream.Seek((long)count * fixedSize, SeekOrigin.Current);
                    }
                    else
                    {
                        // string array (e.g. tokenizer vocab) — skip element by element
                        for (ulong i = 0; i < count; i++)
                        {
                            ulong itemLen = reader.ReadUInt64();
                            reader.BaseStream.Seek((long)itemLen, SeekOrigin.Current);
                        }
                    }
                    break;
                default:
                    throw new InvalidDataException($"Unknown GGUF value type: {valueType}");
            }
        }
    }
}
