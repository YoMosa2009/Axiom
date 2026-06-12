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

        /// <summary>
        /// Bytes of KV-cache required per context token at f16 precision (K + V).
        /// Null when the file did not expose enough attention geometry.
        /// </summary>
        public long? KvBytesPerTokenF16
        {
            get
            {
                if (BlockCount <= 0 || EmbeddingLength <= 0 || HeadCount <= 0)
                    return null;

                int kvHeads = HeadCountKv > 0 ? HeadCountKv : HeadCount;
                long headDim = EmbeddingLength / HeadCount;
                return 2L /*K+V*/ * BlockCount * headDim * kvHeads * 2 /*f16 bytes*/;
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
                int blockCount = 0, contextLength = 0, embeddingLength = 0, headCount = 0, headCountKv = 0;

                for (ulong i = 0; i < kvCount; i++)
                {
                    string key = ReadGgufString(reader);
                    uint valueType = reader.ReadUInt32();

                    if (key == "general.architecture" && valueType == 8)
                    {
                        architecture = ReadGgufString(reader);
                        continue;
                    }

                    bool isWanted =
                        key.EndsWith(".block_count", StringComparison.Ordinal) ||
                        key.EndsWith(".context_length", StringComparison.Ordinal) ||
                        key.EndsWith(".embedding_length", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.head_count", StringComparison.Ordinal) ||
                        key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal);

                    if (isWanted && TryReadIntegerValue(reader, valueType, out long value))
                    {
                        if (key.EndsWith(".block_count", StringComparison.Ordinal)) blockCount = (int)value;
                        else if (key.EndsWith(".context_length", StringComparison.Ordinal)) contextLength = (int)value;
                        else if (key.EndsWith(".embedding_length", StringComparison.Ordinal)) embeddingLength = (int)value;
                        else if (key.EndsWith(".attention.head_count_kv", StringComparison.Ordinal)) headCountKv = (int)value;
                        else if (key.EndsWith(".attention.head_count", StringComparison.Ordinal)) headCount = (int)value;
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
                    HeadCountKv = headCountKv
                };

                Debug.WriteLine($"[GgufMetadataReader] {Path.GetFileName(modelPath)}: arch={architecture}, layers={blockCount}, trainedCtx={contextLength}, kv/token={meta.KvBytesPerTokenF16}");
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
