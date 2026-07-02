using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed class PersonaMemoryService
    {
        private readonly string _filePath;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private string _cachedText = string.Empty;

        public PersonaMemoryService()
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Axiom");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "persona-memory.json");
        }

        public async Task<string> LoadAsync(CancellationToken token = default)
        {
            await _gate.WaitAsync(token);
            try
            {
                if (!File.Exists(_filePath))
                {
                    _cachedText = string.Empty;
                    return _cachedText;
                }

                string json = await File.ReadAllTextAsync(_filePath, token);
                var dto = JsonSerializer.Deserialize<PersonaMemoryDto>(json) ?? new PersonaMemoryDto();
                _cachedText = dto.Text ?? string.Empty;
                return _cachedText;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task SaveAsync(string text, CancellationToken token = default)
        {
            await _gate.WaitAsync(token);
            try
            {
                _cachedText = text ?? string.Empty;
                var dto = new PersonaMemoryDto
                {
                    Text = _cachedText,
                    UpdatedAt = DateTime.UtcNow
                };
                string json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(_filePath, json, token);
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<string> GetRelevantContextAsync(string query, int minTokens = 150, int maxTokens = 350, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(query))
                return string.Empty;

            string memory = _cachedText;
            if (string.IsNullOrWhiteSpace(memory))
                memory = await LoadAsync(token);

            if (string.IsNullOrWhiteSpace(memory))
                return string.Empty;

            return await Task.Run(() =>
            {
                var allWords = Regex.Matches(memory, @"\S+");

                // Short personas are always injected in full — no relevance gate needed.
                // This ensures general preferences ("I prefer concise replies") always reach the model.
                if (allWords.Count <= 250)
                    return ClampToTokenWindow(memory.Trim(), minTokens, maxTokens);

                var queryTerms = ExtractTerms(query);

                // If the query has too few discriminating terms, fall back to the head of the persona.
                if (queryTerms.Count < 2)
                    return ClampToTokenWindow(memory.Trim(), minTokens, maxTokens);

                var sections = SplitSections(memory);
                if (sections.Count == 0)
                    return ClampToTokenWindow(memory.Trim(), minTokens, maxTokens);

                // Multi-topic prompts need stronger relevance to avoid stale-memory bleed.
                var topicBuckets = query
                    .Split(new[] { '\n', '.', ';', '?', '!' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => ExtractTerms(s))
                    .Where(set => set.Count >= 2)
                    .ToList();

                int distinctTopicSignals = topicBuckets.Count;
                double minScore = distinctTopicSignals >= 2 ? 0.16 : 0.08;
                double highConfidenceThreshold = distinctTopicSignals >= 2 ? 0.22 : 0.12;

                bool semanticAvailable = LocalSemanticEmbeddingService.Shared.IsAvailable;
                var ranked = sections
                    .Select(s => new { Text = s, Score = ScoreSection(s, query, queryTerms, semanticAvailable) })
                    .Where(x => x.Score >= minScore)
                    .OrderByDescending(x => x.Score)
                    .Take(4)
                    .ToList();

                // Fall back to head of persona if no section meets the confidence bar.
                if (ranked.Count == 0 || ranked[0].Score < highConfidenceThreshold)
                    return ClampToTokenWindow(memory.Trim(), minTokens, maxTokens);

                string merged = string.Join("\n\n", ranked.Select(x => x.Text)).Trim();
                return ClampToTokenWindow(merged, minTokens, maxTokens);
            }, token);
        }

        private static readonly HashSet<string> _stopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "this", "that", "with", "from", "have", "been", "will", "would", "could", "should",
            "they", "them", "their", "there", "when", "what", "which", "where", "were", "then",
            "also", "just", "like", "more", "some", "into", "over", "about", "after", "before",
            "your", "mine", "ours", "hers", "need", "want", "make", "know", "look", "time",
            "can", "the", "and", "for", "not", "but", "its", "our", "you", "all", "any",
        };

        private static HashSet<string> ExtractTerms(string text)
        {
            return Regex.Matches(text.ToLowerInvariant(), @"\b[a-z0-9_]{3,}\b")
                .Select(m => m.Value)
                .Where(x => !_stopWords.Contains(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static List<string> SplitSections(string text)
        {
            return text.Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 24)
                .ToList();
        }

        private static double ScoreSection(string section, string query, HashSet<string> queryTerms, bool semanticAvailable)
        {
            var sectionTerms = ExtractTerms(section);
            double lexicalScore = 0;
            if (sectionTerms.Count > 0)
            {
                int overlap = sectionTerms.Count(t => queryTerms.Contains(t));
                lexicalScore = overlap / (double)Math.Max(1, queryTerms.Count);
            }

            if (semanticAvailable && LocalSemanticEmbeddingService.Shared.TryGetSimilarity(query, section, out double semanticScore))
            {
                double mappedSemantic = Math.Clamp((semanticScore - 0.20) / 0.55, 0, 1);
                return Math.Max(lexicalScore, mappedSemantic);
            }

            return lexicalScore;
        }

        private static string ClampToTokenWindow(string text, int minTokens, int maxTokens)
        {
            var words = Regex.Matches(text, @"\S+")
                .Select(m => m.Value)
                .ToList();

            if (words.Count <= maxTokens)
                return text;

            int target = Math.Max(minTokens, maxTokens);
            return string.Join(" ", words.Take(target));
        }

        private sealed class PersonaMemoryDto
        {
            public string Text { get; set; } = string.Empty;
            public DateTime UpdatedAt { get; set; }
        }
    }
}
