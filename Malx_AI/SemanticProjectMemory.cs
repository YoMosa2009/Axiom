using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed class SemanticConcept
    {
        public string Name { get; set; } = "";
        public int Weight { get; set; }
    }

    public sealed class SemanticProjectMemory
    {
        private sealed class IndexedChunk
        {
            public DocumentChunk Chunk { get; set; } = new DocumentChunk();
            public Dictionary<string, double> Vector { get; set; } = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }

        private readonly object _gate = new();
        private readonly Dictionary<string, string> _documentHashCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<SemanticConcept>> _documentConceptCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, IndexedChunk> _chunkIndex = new(StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","and","for","that","with","from","this","there","your","into","using","have","has","had",
            "were","been","they","them","will","would","could","should","about","while","where","when","which",
            "what","only","also","then","than","into","onto","over","under","true","false","null","void",
            "public","private","class","static","return","string","int","var","new","async","await"
        };

        private static readonly Regex ConceptTokenRegex = new(@"\b[A-Za-z][A-Za-z0-9_\-\.]{2,}\b", RegexOptions.Compiled);
        private static readonly Regex VectorTokenRegex = new(@"\b[a-z][a-z0-9_\-\.]{2,}\b", RegexOptions.Compiled);
        private static readonly Regex CamelCaseRegex = new(@"[a-z][A-Z]", RegexOptions.Compiled);

        public async Task IndexDocumentAsync(string filePath, string extractedText, List<DocumentChunk> chunks)
        {
            await Task.Run(() =>
            {
                string hash = ComputeHashToken(extractedText);
                List<SemanticConcept>? concepts = null;

                lock (_gate)
                {
                    if (_documentHashCache.TryGetValue(filePath, out string? existingHash) &&
                        string.Equals(existingHash, hash, StringComparison.Ordinal))
                    {
                        if (_documentConceptCache.TryGetValue(filePath, out var cached))
                        {
                            concepts = cached;
                        }
                    }
                }

                if (concepts == null)
                {
                    concepts = ExtractConcepts(extractedText);
                }

                var localChunkVectors = new Dictionary<string, IndexedChunk>(StringComparer.OrdinalIgnoreCase);
                foreach (var chunk in chunks)
                {
                    string key = BuildChunkKey(chunk.FileName, chunk.ChunkId);
                    localChunkVectors[key] = new IndexedChunk
                    {
                        Chunk = chunk,
                        Vector = Vectorize(chunk.Content)
                    };
                }

                lock (_gate)
                {
                    _documentHashCache[filePath] = hash;
                    _documentConceptCache[filePath] = concepts;

                    foreach (var kvp in localChunkVectors)
                    {
                        _chunkIndex[kvp.Key] = kvp.Value;
                    }
                }
            });
        }

        public async Task<List<SemanticConcept>> GetConceptCloudAsync(int maxCount)
        {
            return await Task.Run(() =>
            {
                var aggregate = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                lock (_gate)
                {
                    foreach (var list in _documentConceptCache.Values)
                    {
                        foreach (var concept in list)
                        {
                            aggregate.TryGetValue(concept.Name, out int weight);
                            aggregate[concept.Name] = weight + concept.Weight;
                        }
                    }
                }

                return aggregate
                    .OrderByDescending(x => x.Value)
                    .Take(Math.Max(0, maxCount))
                    .Select(x => new SemanticConcept { Name = x.Key, Weight = x.Value })
                    .ToList();
            });
        }

        public async Task<List<DocumentChunk>> SearchByConceptAsync(string concept, int maxChunks)
        {
            return await Task.Run(() =>
            {
                if (string.IsNullOrWhiteSpace(concept) || maxChunks <= 0)
                {
                    return new List<DocumentChunk>();
                }

                var conceptVector = Vectorize(concept);
                var scored = new List<(DocumentChunk Chunk, double Score)>();

                lock (_gate)
                {
                    foreach (var item in _chunkIndex.Values)
                    {
                        double score = CosineSimilarity(item.Vector, conceptVector);
                        if (item.Chunk.Content.Contains(concept, StringComparison.OrdinalIgnoreCase))
                        {
                            score += 0.75;
                        }

                        if (score > 0.01)
                        {
                            scored.Add((item.Chunk, score));
                        }
                    }
                }

                return scored
                    .OrderByDescending(s => s.Score)
                    .Take(maxChunks)
                    .Select(s => s.Chunk)
                    .ToList();
            });
        }

        public void Clear()
        {
            lock (_gate)
            {
                _documentHashCache.Clear();
                _documentConceptCache.Clear();
                _chunkIndex.Clear();
            }
        }

        private static List<SemanticConcept> ExtractConcepts(string text)
        {
            var tokens = ConceptTokenRegex.Matches(text)
                .Select(m => m.Value)
                .Where(t => !StopWords.Contains(t))
                .ToList();

            var weighted = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                int boost = 1;

                if (token.Any(char.IsDigit)) boost += 1;
                if (token.Contains('_') || token.Contains('.')) boost += 2;
                if (CamelCaseRegex.IsMatch(token)) boost += 2;
                if (token.Length > 10) boost += 1;

                weighted.TryGetValue(token, out int score);
                weighted[token] = score + boost;
            }

            return weighted
                .OrderByDescending(x => x.Value)
                .Take(80)
                .Select(x => new SemanticConcept { Name = x.Key, Weight = x.Value })
                .ToList();
        }

        private static Dictionary<string, double> Vectorize(string text)
        {
            var tokens = VectorTokenRegex.Matches(text.ToLowerInvariant())
                .Select(m => m.Value)
                .Where(t => !StopWords.Contains(t))
                .ToList();

            var vec = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in tokens)
            {
                vec.TryGetValue(token, out double count);
                vec[token] = count + 1d;
            }

            double norm = Math.Sqrt(vec.Values.Sum(v => v * v));
            if (norm > 0)
            {
                var keys = vec.Keys.ToList();
                foreach (var key in keys)
                {
                    vec[key] /= norm;
                }
            }

            return vec;
        }

        private static double CosineSimilarity(Dictionary<string, double> a, Dictionary<string, double> b)
        {
            double sum = 0;
            foreach (var kv in a)
            {
                if (b.TryGetValue(kv.Key, out double v))
                {
                    sum += kv.Value * v;
                }
            }

            return sum;
        }

        private static string ComputeHashToken(string content)
        {
            return $"{content.Length}:{content.GetHashCode()}";
        }

        private static string BuildChunkKey(string fileName, int chunkId)
        {
            return $"{fileName}:{chunkId}";
        }
    }
}
