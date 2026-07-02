using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public class DocumentRetriever
    {
        private List<DocumentChunk> _chunks = new List<DocumentChunk>();
        // ChunkIds restart at 0 for every file, so cache keys must include the file name
        // or chunks from different documents overwrite each other's keyword sets.
        private readonly Dictionary<string, HashSet<string>> _cachedChunkKeywords = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, int>? _cachedDocumentFrequency;
        private static readonly Dictionary<string, string[]> SemanticAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["bug"] = ["error", "issue", "failure", "problem"],
            ["error"] = ["bug", "failure", "exception", "fault"],
            ["fix"] = ["repair", "resolve", "correct", "patch"],
            ["speed"] = ["performance", "latency", "throughput", "fast"],
            ["performance"] = ["speed", "latency", "throughput", "optimize"],
            ["auth"] = ["authentication", "login", "signin", "identity"],
            ["login"] = ["signin", "authentication", "auth", "credential"],
            ["config"] = ["configuration", "settings", "option", "parameter"],
            ["settings"] = ["configuration", "config", "options", "preferences"],
            ["docs"] = ["document", "documentation", "guide", "manual"],
            ["document"] = ["doc", "docs", "file", "text"],
            ["summary"] = ["summarize", "overview", "synopsis", "brief"],
            ["compare"] = ["difference", "contrast", "versus", "against"]
        };

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "a", "an", "and", "or", "but", "in", "on", "at", "to", "for",
            "of", "is", "are", "was", "were", "be", "been", "being", "have", "has",
            "had", "do", "does", "did", "will", "would", "should", "could", "may",
            "might", "must", "can", "this", "that", "these", "those", "i", "you",
            "he", "she", "it", "we", "they", "what", "which", "who", "when", "where", "why",
            "about", "as", "by", "from", "up", "with", "out", "if", "then", "so",
            "only", "just", "very", "too", "more", "most", "some", "any", "all", "no", "not"
        };

        private static readonly Regex WordSplitRegex = new(@"\W+", RegexOptions.Compiled);

        private static string GetChunkKey(DocumentChunk chunk)
            => $"{chunk.FileName}::{chunk.ChunkId}";

        public void AddChunks(List<DocumentChunk> chunks)
        {
            _chunks.AddRange(chunks);
            foreach (var chunk in chunks)
            {
                string key = GetChunkKey(chunk);
                if (!_cachedChunkKeywords.ContainsKey(key))
                    _cachedChunkKeywords[key] = ExtractKeywords(chunk.Content).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            _cachedDocumentFrequency = null;

            // Embed new chunks in the background now, so query-time semantic ranking is a
            // cache hit instead of a blocking native inference per chunk on the first turn.
            LocalSemanticEmbeddingService.Shared.PrewarmInBackground(chunks.Select(chunk => chunk.Content));
            Debug.WriteLine($"DocumentRetriever: Added {chunks.Count} chunks. Total: {_chunks.Count}");
        }

        public void ClearChunks()
        {
            _chunks.Clear();
            _cachedChunkKeywords.Clear();
            _cachedDocumentFrequency = null;
            Debug.WriteLine("DocumentRetriever: Cleared all chunks");
        }

        /// <summary>
        /// Reconstructs the full in-memory text for a file from its chunks (insertion/document order).
        /// Used as a fallback when on-disk extraction fails or the file is unavailable.
        /// Returns an empty string when no chunks match the file name.
        /// </summary>
        public string GetAllTextForFile(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            var parts = _chunks
                .Where(c => string.Equals(c.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.Content)
                .Where(t => !string.IsNullOrWhiteSpace(t));

            return string.Join("\n", parts).Trim();
        }

        private Dictionary<string, int> GetOrBuildDocumentFrequencyMap()
        {
            if (_cachedDocumentFrequency != null)
                return _cachedDocumentFrequency;

            var frequency = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DocumentChunk chunk in _chunks)
            {
                HashSet<string> seen = _cachedChunkKeywords.TryGetValue(GetChunkKey(chunk), out var kw)
                    ? kw
                    : ExtractKeywords(chunk.Content).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string term in seen)
                    frequency[term] = frequency.TryGetValue(term, out int count) ? count + 1 : 1;
            }
            _cachedDocumentFrequency = frequency;
            return frequency;
        }

        /// <summary>
        /// Retrieves the most relevant chunks based on query similarity
        /// Returns up to maxChunks results based on keyword overlap
        /// </summary>
        public List<DocumentChunk> RetrieveRelevantChunks(string query, int maxChunks)
        {
            return RetrieveRelevantChunks(query, maxChunks, allowFallback: true);
        }

        public List<DocumentChunk> RetrieveRelevantChunks(string query, int maxChunks, bool allowFallback)
        {
            Debug.WriteLine($"RetrieveRelevantChunks: Query='{query}', Available chunks={_chunks.Count}, maxChunks={maxChunks}");

            if (_chunks.Count == 0)
            {
                Debug.WriteLine("RetrieveRelevantChunks: No chunks available");
                return new List<DocumentChunk>();
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                Debug.WriteLine(allowFallback
                    ? "RetrieveRelevantChunks: Empty query, returning first chunks"
                    : "RetrieveRelevantChunks: Empty query, skipping fallback retrieval");
                return allowFallback ? _chunks.Take(maxChunks).ToList() : new List<DocumentChunk>();
            }

            var exactQueryTerms = ExtractKeywords(query);
            var expandedQueryTerms = ExpandSemanticTerms(exactQueryTerms);
            var queryTrigrams = BuildCharacterTrigrams(query);
            var documentFrequency = GetOrBuildDocumentFrequencyMap();
            Debug.WriteLine($"RetrieveRelevantChunks: Extracted {expandedQueryTerms.Count} semantic terms from {exactQueryTerms.Count} keywords: {string.Join(", ", expandedQueryTerms.Take(20))}");

            if (expandedQueryTerms.Count == 0)
            {
                Debug.WriteLine(allowFallback
                    ? "RetrieveRelevantChunks: No keywords extracted, returning first chunks"
                    : "RetrieveRelevantChunks: No keywords extracted, skipping fallback retrieval");
                return allowFallback ? _chunks.Take(maxChunks).ToList() : new List<DocumentChunk>();
            }

            bool semanticAvailable = LocalSemanticEmbeddingService.Shared.IsAvailable;

            // Score each chunk based on semantic similarity plus keyword overlap.
            var scoredChunks = _chunks
                .Select(chunk => new
                {
                    Chunk = chunk,
                    Score = CalculateRelevanceScore(
                        chunk, query, exactQueryTerms, expandedQueryTerms, queryTrigrams, documentFrequency, _chunks.Count,
                        _cachedChunkKeywords.TryGetValue(GetChunkKey(chunk), out var kw) ? kw : null),
                    SemanticScore = semanticAvailable && LocalSemanticEmbeddingService.Shared.TryGetSimilarity(query, chunk.Content, out double sim)
                        ? sim
                        : 0
                })
                .Select(x => new
                {
                    x.Chunk,
                    CombinedScore = x.Score + (int)Math.Round(Math.Max(0, x.SemanticScore - 0.20) * 90),
                    x.Score,
                    x.SemanticScore
                })
                .OrderByDescending(x => x.CombinedScore)
                .ToList();

            // Log scoring results
            var topScored = scoredChunks.Take(3).ToList();
            foreach (var scored in topScored)
            {
                Debug.WriteLine($"  Chunk {scored.Chunk.ChunkId} ({scored.Chunk.FileName}): Score={scored.CombinedScore} lexical={scored.Score} semantic={scored.SemanticScore:0.000}");
            }

            int topScore = scoredChunks.FirstOrDefault()?.CombinedScore ?? 0;
            if (topScore <= 0)
            {
                Debug.WriteLine(allowFallback
                    ? "RetrieveRelevantChunks: No relevant chunks found, using fallback context"
                    : "RetrieveRelevantChunks: No relevant chunks found, returning no chunks");
                return allowFallback ? GetFallbackChunks(maxChunks) : new List<DocumentChunk>();
            }

            // Keep only meaningfully relevant chunks to reduce context pollution.
            int minScoreThreshold = Math.Max(2, topScore / 4);
            var result = scoredChunks
                .Where(x => x.CombinedScore >= minScoreThreshold)
                .Take(maxChunks)
                .Select(x => x.Chunk)
                .ToList();

            if (result.Count == 0)
            {
                if (!allowFallback)
                {
                    Debug.WriteLine("RetrieveRelevantChunks: Threshold filtered all chunks, returning no chunks");
                    return new List<DocumentChunk>();
                }

                Debug.WriteLine("RetrieveRelevantChunks: Threshold filtered all chunks, using top scored chunk only");
                result = scoredChunks
                    .Where(x => x.CombinedScore > 0)
                    .Take(1)
                    .Select(x => x.Chunk)
                    .ToList();
            }

            Debug.WriteLine($"RetrieveRelevantChunks: Returning {result.Count} chunks");
            return result;
        }

        private List<DocumentChunk> GetFallbackChunks(int maxChunks)
        {
            try
            {
                if (_chunks.Count == 0 || maxChunks <= 0)
                    return new List<DocumentChunk>();

                // Prefer broad coverage: one chunk per file first, then fill remaining slots.
                var fallback = _chunks
                    .GroupBy(c => c.FileName)
                    .Select(g => g.OrderBy(c => c.ChunkId).First())
                    .Take(maxChunks)
                    .ToList();

                if (fallback.Count < maxChunks)
                {
                    var existing = new HashSet<string>(fallback.Select(GetChunkKey), StringComparer.OrdinalIgnoreCase);
                    var remainder = _chunks
                        .Where(c => !existing.Contains(GetChunkKey(c)))
                        .OrderBy(c => c.FileName)
                        .ThenBy(c => c.ChunkId)
                        .Take(maxChunks - fallback.Count);

                    fallback.AddRange(remainder);
                }

                Debug.WriteLine($"GetFallbackChunks: Returning {fallback.Count} fallback chunks");
                return fallback;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"GetFallbackChunks error: {ex.Message}");
                return _chunks.Take(Math.Max(1, maxChunks)).ToList();
            }
        }


        private static List<string> ExtractKeywords(string text)
        {
            try
            {
                var words = WordSplitRegex.Split(text.ToLowerInvariant())
                    .Select(NormalizeWord)
                    .Where(w => w.Length > 2 && !StopWords.Contains(w))
                    .Distinct()
                    .OrderByDescending(w => w.Length)
                    .Take(15)
                    .ToList();

                return words;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ExtractKeywords error: {ex.Message}");
                return new List<string>();
            }
        }

        private static HashSet<string> ExpandSemanticTerms(IEnumerable<string> keywords)
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string keyword in keywords)
            {
                if (string.IsNullOrWhiteSpace(keyword))
                    continue;

                expanded.Add(keyword);
                if (SemanticAliases.TryGetValue(keyword, out string[]? aliases))
                {
                    foreach (string alias in aliases)
                        expanded.Add(alias);
                }
            }

            return expanded;
        }

        private static HashSet<string> BuildCharacterTrigrams(string text)
        {
            string normalized = Regex.Replace((text ?? string.Empty).ToLowerInvariant(), @"\s+", " ").Trim();
            var trigrams = new HashSet<string>(StringComparer.Ordinal);
            if (normalized.Length < 3)
            {
                if (normalized.Length > 0)
                    trigrams.Add(normalized);
                return trigrams;
            }

            for (int i = 0; i <= normalized.Length - 3; i++)
                trigrams.Add(normalized.Substring(i, 3));

            return trigrams;
        }

        private static string NormalizeWord(string word)
        {
            if (string.IsNullOrWhiteSpace(word))
                return string.Empty;

            string normalized = word.Trim().ToLowerInvariant();
            if (normalized.Length > 5 && normalized.EndsWith("ing", StringComparison.Ordinal))
                normalized = normalized[..^3];
            else if (normalized.Length > 4 && normalized.EndsWith("ed", StringComparison.Ordinal))
                normalized = normalized[..^2];
            else if (normalized.Length > 4 && normalized.EndsWith("es", StringComparison.Ordinal))
                normalized = normalized[..^2];
            else if (normalized.Length > 3 && normalized.EndsWith("s", StringComparison.Ordinal))
                normalized = normalized[..^1];

            return normalized;
        }

        private static int CalculateRelevanceScore(DocumentChunk chunk, string query, IReadOnlyCollection<string> exactTerms, IReadOnlyCollection<string> expandedTerms, IReadOnlyCollection<string> queryTrigrams, IReadOnlyDictionary<string, int> documentFrequency, int totalChunks, HashSet<string>? cachedChunkKeywords = null)
        {
            try
            {
                int score = 0;
                string chunkText = chunk.Content ?? string.Empty;
                string lowerChunk = chunkText.ToLowerInvariant();
                var chunkTerms = cachedChunkKeywords ?? ExtractKeywords(chunkText).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (string term in exactTerms)
                {
                    if (!chunkTerms.Contains(term))
                        continue;

                    int df = documentFrequency.TryGetValue(term, out int count) ? count : 1;
                    double idf = Math.Log((totalChunks + 1d) / df) + 1d;
                    score += Math.Max(4, (int)Math.Round(idf * 14));
                }

                foreach (string term in expandedTerms)
                {
                    if (exactTerms.Contains(term, StringComparer.OrdinalIgnoreCase) || !chunkTerms.Contains(term))
                        continue;

                    score += 5;
                }

                if (!string.IsNullOrWhiteSpace(chunk.FileName))
                {
                    string normalizedFile = NormalizeWord(Path.GetFileNameWithoutExtension(chunk.FileName));
                    foreach (string term in exactTerms)
                    {
                        if (normalizedFile.Contains(term, StringComparison.OrdinalIgnoreCase))
                            score += 8;
                    }
                }

                string normalizedQuery = Regex.Replace((query ?? string.Empty).Trim(), @"\s+", " ");
                if (normalizedQuery.Length >= 12 && lowerChunk.Contains(normalizedQuery.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase))
                    score += 20;

                var chunkTrigrams = BuildCharacterTrigrams(chunkText.Length > 1400 ? chunkText[..1400] : chunkText);
                if (queryTrigrams.Count > 0 && chunkTrigrams.Count > 0)
                {
                    int overlap = queryTrigrams.Count(t => chunkTrigrams.Contains(t));
                    double similarity = overlap / (double)Math.Max(queryTrigrams.Count, 1);
                    score += (int)Math.Round(similarity * 18);
                }

                return score;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CalculateRelevanceScore error: {ex.Message}");
                return 0;
            }
        }

        /// <summary>
        /// Calculates maximum chunks that can fit given context size and other constraints
        /// </summary>
        public int CalculateMaxChunksForContext(int contextSize)
        {
            // Reserve ~30% of context for user query and response
            int availableContext = (int)(contextSize * 0.7);

            // Average chunk is ~400 tokens, so:
            int maxChunks = availableContext / 400;

            // Minimum 1, reasonable maximum 15 for rich context
            return Math.Max(1, Math.Min(maxChunks, 15));
        }

        /// <summary>
        /// Builds the enhanced prompt with document context injected.
        /// Wraps the result strictly in role tokens for the LLamaSharp engine.
        /// </summary>
        public string BuildEnhancedPrompt(string userQuery, List<DocumentChunk> relevantChunks)
        {
            try
            {
                if (relevantChunks.Count == 0)
                {
                    Debug.WriteLine("BuildEnhancedPrompt: No chunks, using raw query");
                    return $"<|im_start|>system\nYou are a helpful assistant. Execute the user's request directly. If the user asks for an output (summary, rewrite, draft, extraction, translation, analysis), produce the final result immediately. Do not switch to step-by-step guidance unless the user explicitly asks for instructions or a tutorial.<|im_end|>\n<|im_start|>user\n{userQuery}<|im_end|>\n<|im_start|>assistant\n";
                }

                var contentBuilder = new StringBuilder();

                // Grounding + execution instruction to prefer doing the task over explaining how to do it.
                contentBuilder.AppendLine("Use the reference material below to complete the user's request directly.");
                contentBuilder.AppendLine("If the user asks for an output, provide the final output immediately instead of a how-to guide.");
                contentBuilder.AppendLine("Only provide step-by-step instructions when the user explicitly requests instructions/tutorial steps.");
                contentBuilder.AppendLine("If reference data is missing, briefly state what is missing and then provide the best possible result.");
                contentBuilder.AppendLine();

                // Add each chunk with source attribution
                for (int i = 0; i < relevantChunks.Count; i++)
                {
                    var chunk = relevantChunks[i];
                    contentBuilder.AppendLine($"[Reference {i + 1} — {chunk.FileName}]");
                    contentBuilder.AppendLine(chunk.Content.Trim());
                    contentBuilder.AppendLine();
                }

                // Add the actual user question clearly separated
                contentBuilder.AppendLine("---");
                contentBuilder.Append(userQuery);

                // Return ChatML formatted string for Qwen-family instruct models
                string result = $"<|im_start|>system\nYou are a helpful assistant. Execute user tasks directly and return final outputs by default; only give tutorial-style guidance when explicitly requested.<|im_end|>\n<|im_start|>user\n{contentBuilder}<|im_end|>\n<|im_start|>assistant\n";
                Debug.WriteLine($"BuildEnhancedPrompt: Created prompt with {relevantChunks.Count} chunks, ~{result.Length} chars");
                return result;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BuildEnhancedPrompt error: {ex.Message}");
                return $"<|im_start|>system\nYou are a helpful assistant. Execute the user's request directly and provide final outputs by default.<|im_end|>\n<|im_start|>user\n{userQuery}<|im_end|>\n<|im_start|>assistant\n";
            }
        }
    }
}

