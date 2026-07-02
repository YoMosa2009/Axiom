using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LLama;
using LLama.Common;
using LLama.Native;

namespace Malx_AI
{
    /// <summary>
    /// Shared local semantic retrieval layer backed by a small GGUF embedding model.
    /// Place a free/local embedding GGUF in %LOCALAPPDATA%\Axiom\EmbeddingModels, or set
    /// AXIOM_EMBEDDING_MODEL to its full path. The app never calls a hosted embedding API.
    /// </summary>
    public sealed class LocalSemanticEmbeddingService : IDisposable
    {
        private const int MaxCacheEntries = 4096;
        private const int MaxInputChars = 1800;

        private static readonly string[] PreferredNameHints =
        [
            "bge-small",
            "bge_small",
            "nomic-embed",
            "nomic_embed",
            "embed"
        ];

        // Two locks: _cacheGate protects the dictionary and stays cheap; _inferenceGate
        // serializes model load and native embedding calls. LLamaEmbedder shares one native
        // context, and concurrent unmanaged access is the same native-abort class
        // (ucrtbase 0xc0000409) the chat/council decode gates exist to prevent.
        private readonly object _cacheGate = new();
        private readonly object _inferenceGate = new();
        private readonly Dictionary<string, float[]> _embeddingCache = new(StringComparer.Ordinal);
        private readonly Queue<string> _cacheOrder = new();

        private bool _loadAttempted;
        private bool _disposed;
        private string _unavailableReason = "";
        private string _loadedModelPath = "";
        private LLamaWeights? _weights;
        private LLamaEmbedder? _embedder;

        public static LocalSemanticEmbeddingService Shared { get; } = new();

        public bool IsAvailable
        {
            get
            {
                lock (_inferenceGate)
                    return EnsureLoadedLocked();
            }
        }

        public string Status
        {
            get
            {
                lock (_inferenceGate)
                    return EnsureLoadedLocked()
                        ? $"Semantic embeddings ready: {Path.GetFileName(_loadedModelPath)}"
                        : _unavailableReason;
            }
        }

        public bool TryGetSimilarity(string? left, string? right, out double similarity)
        {
            similarity = 0;
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            float[]? a = TryGetEmbedding(left);
            float[]? b = TryGetEmbedding(right);
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length)
                return false;

            similarity = Dot(a, b);
            return true;
        }

        public IReadOnlyList<(T Item, double Similarity)> RankBySimilarity<T>(
            string query,
            IEnumerable<T> items,
            Func<T, string> textSelector,
            int maxResults,
            double minSimilarity = 0.20)
        {
            if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
                return Array.Empty<(T, double)>();

            float[]? queryEmbedding = TryGetEmbedding(query);
            if (queryEmbedding == null)
                return Array.Empty<(T, double)>();

            var scored = new List<(T Item, double Similarity)>();
            foreach (T item in items)
            {
                float[]? candidateEmbedding = TryGetEmbedding(textSelector(item));
                if (candidateEmbedding == null || candidateEmbedding.Length != queryEmbedding.Length)
                    continue;

                double similarity = Dot(queryEmbedding, candidateEmbedding);
                if (similarity >= minSimilarity)
                    scored.Add((item, similarity));
            }

            return scored
                .OrderByDescending(s => s.Similarity)
                .Take(maxResults)
                .ToList();
        }

        /// <summary>
        /// Embeds the given texts on a background thread so later query-time ranking is a
        /// cache hit instead of a blocking native inference. Call this at write/index time
        /// (memory writes, document chunking); it is fire-and-forget and failure-tolerant.
        /// </summary>
        public void PrewarmInBackground(IEnumerable<string>? texts)
        {
            if (texts == null)
                return;

            // Snapshot on the caller's thread — the source collection may be mutated later.
            List<string> pending = texts
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Take(MaxCacheEntries / 4)
                .ToList();
            if (pending.Count == 0)
                return;

            _ = Task.Run(() =>
            {
                foreach (string text in pending)
                {
                    if (_disposed)
                        return;
                    TryGetEmbedding(text);
                }
            });
        }

        private float[]? TryGetEmbedding(string text)
        {
            string normalized = NormalizeInput(text);
            if (string.IsNullOrWhiteSpace(normalized))
                return null;

            string key = BuildCacheKey(normalized);
            lock (_cacheGate)
            {
                if (_embeddingCache.TryGetValue(key, out float[]? cached))
                    return cached;
            }

            lock (_inferenceGate)
            {
                // Another caller may have computed it while we waited on the gate.
                lock (_cacheGate)
                {
                    if (_embeddingCache.TryGetValue(key, out float[]? cached))
                        return cached;
                }

                if (!EnsureLoadedLocked() || _embedder == null)
                    return null;

                try
                {
                    // GetEmbeddings is async (Task<IReadOnlyList<float[]>>). Block through
                    // Task.Run so the library's internal awaits resume on the thread pool —
                    // never on a captured UI SynchronizationContext, which would deadlock.
                    IReadOnlyList<float[]> raw = Task
                        .Run(() => _embedder.GetEmbeddings(normalized, CancellationToken.None))
                        .GetAwaiter()
                        .GetResult();

                    float[] vector = NormalizeVector(AverageVectors(raw, _embedder.EmbeddingSize));
                    if (vector.Length == 0)
                        return null;

                    lock (_cacheGate)
                        AddToCacheLocked(key, vector);
                    return vector;
                }
                catch (Exception ex)
                {
                    _unavailableReason = $"Semantic embedding inference skipped after failure: {ex.Message}";
                    Debug.WriteLine("[LocalSemanticEmbeddingService] " + _unavailableReason);
                    return null;
                }
            }
        }

        private bool EnsureLoadedLocked()
        {
            if (_embedder != null)
                return true;
            if (_loadAttempted || _disposed)
                return false;

            _loadAttempted = true;
            string? modelPath = FindModelPath();
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                _unavailableReason = $"No local embedding GGUF found. Put bge-small/nomic-embed GGUF in {AppDataPaths.EmbeddingModels} or set AXIOM_EMBEDDING_MODEL.";
                Debug.WriteLine("[LocalSemanticEmbeddingService] " + _unavailableReason);
                return false;
            }

            try
            {
                var parameters = new ModelParams(modelPath)
                {
                    ContextSize = 512,
                    GpuLayerCount = 0,
                    Embeddings = true,
                    PoolingType = LLamaPoolingType.Mean,
                    AttentionType = LLamaAttentionType.NonCausal,
                    UseMemorymap = true,
                    BatchSize = 512,
                    UBatchSize = 512,
                    Threads = Math.Max(1, Environment.ProcessorCount / 2),
                    BatchThreads = Math.Max(1, Environment.ProcessorCount / 2)
                };

                _weights = LLamaWeights.LoadFromFile(parameters);
                _embedder = new LLamaEmbedder(_weights, parameters, logger: null);
                _loadedModelPath = modelPath;
                _unavailableReason = "";
                Debug.WriteLine($"[LocalSemanticEmbeddingService] Loaded {Path.GetFileName(modelPath)} ({_embedder.EmbeddingSize} dims)");
                return true;
            }
            catch (Exception ex)
            {
                _unavailableReason = $"Failed to load local embedding model '{modelPath}': {ex.Message}";
                Debug.WriteLine("[LocalSemanticEmbeddingService] " + _unavailableReason);
                DisposeLoadedModelLocked();
                return false;
            }
        }

        private static string? FindModelPath()
        {
            string? envPath = Environment.GetEnvironmentVariable("AXIOM_EMBEDDING_MODEL");
            if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
                return Path.GetFullPath(envPath);

            var searchRoots = new[]
            {
                AppDataPaths.EmbeddingModels,
                Path.Combine(AppContext.BaseDirectory, "EmbeddingModels")
            };

            return searchRoots
                .Where(Directory.Exists)
                .SelectMany(root => Directory.EnumerateFiles(root, "*.gguf", SearchOption.TopDirectoryOnly))
                .OrderByDescending(path => PreferredNameHints.Any(hint => Path.GetFileName(path).Contains(hint, StringComparison.OrdinalIgnoreCase)))
                .ThenBy(path => new FileInfo(path).Length)
                .FirstOrDefault();
        }

        private static string NormalizeInput(string text)
        {
            string normalized = Regex.Replace(text ?? string.Empty, @"\s+", " ").Trim();
            return normalized.Length <= MaxInputChars ? normalized : normalized[..MaxInputChars];
        }

        private static string BuildCacheKey(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            byte[] hash = SHA256.HashData(bytes);
            return $"{bytes.Length}:{Convert.ToHexString(hash)}";
        }

        private void AddToCacheLocked(string key, float[] vector)
        {
            if (_embeddingCache.ContainsKey(key))
                return;

            _embeddingCache[key] = vector;
            _cacheOrder.Enqueue(key);

            while (_embeddingCache.Count > MaxCacheEntries && _cacheOrder.Count > 0)
            {
                string oldest = _cacheOrder.Dequeue();
                _embeddingCache.Remove(oldest);
            }
        }

        private static float[] AverageVectors(IReadOnlyList<float[]>? vectors, int embeddingSize)
        {
            if (vectors == null || vectors.Count == 0 || embeddingSize <= 0)
                return Array.Empty<float>();

            // Mean pooling returns one vector for the whole input; per-token results are
            // averaged into a single vector so both shapes rank identically.
            if (vectors.Count == 1 && vectors[0].Length == embeddingSize)
                return vectors[0];

            var pooled = new float[embeddingSize];
            int count = 0;
            foreach (float[] vector in vectors)
            {
                if (vector == null || vector.Length < embeddingSize)
                    continue;

                for (int i = 0; i < embeddingSize; i++)
                    pooled[i] += vector[i];
                count++;
            }

            if (count == 0)
                return Array.Empty<float>();

            for (int i = 0; i < pooled.Length; i++)
                pooled[i] /= count;

            return pooled;
        }

        private static float[] NormalizeVector(float[] vector)
        {
            double norm = Math.Sqrt(vector.Sum(v => (double)v * v));
            if (norm <= 0)
                return Array.Empty<float>();

            var normalized = new float[vector.Length];
            for (int i = 0; i < vector.Length; i++)
                normalized[i] = (float)(vector[i] / norm);

            return normalized;
        }

        private static double Dot(float[] a, float[] b)
        {
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
                sum += a[i] * b[i];
            return Math.Clamp(sum, -1.0, 1.0);
        }

        public void Dispose()
        {
            lock (_inferenceGate)
            {
                _disposed = true;
                DisposeLoadedModelLocked();
            }
        }

        private void DisposeLoadedModelLocked()
        {
            _embedder?.Dispose();
            _weights?.Dispose();
            _embedder = null;
            _weights = null;
            _loadedModelPath = "";
            lock (_cacheGate)
            {
                _embeddingCache.Clear();
                _cacheOrder.Clear();
            }
        }
    }
}
