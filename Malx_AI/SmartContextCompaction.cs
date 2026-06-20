using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    // ═══════════════════════════════════════════════
    // Importance tier for context compaction
    // ═══════════════════════════════════════════════
    public enum MessageImportance
    {
        Low,
        High
    }

    // ═══════════════════════════════════════════════
    // Compaction summary entry produced during compression
    // ═══════════════════════════════════════════════
    public sealed class CompactionSummaryEntry
    {
        public string TopicLabel { get; set; } = "";
        public int OriginalMessageCount { get; set; }
        public string Summary { get; set; } = "";
    }

    // ═══════════════════════════════════════════════
    // Result of a compaction operation
    // ═══════════════════════════════════════════════
    public sealed class CompactionResult
    {
        public bool Executed { get; set; }
        public int MessagesCompressed { get; set; }
        public int TokensBefore { get; set; }
        public int TokensAfter { get; set; }
        public List<CompactionSummaryEntry> Summaries { get; set; } = new();
    }

    // ═══════════════════════════════════════════════
    // Per-model context reliability ceiling
    // ═══════════════════════════════════════════════
    public sealed class ContextProfile
    {
        public int ReliabilityCeilingPercent { get; set; } = 75;

        public int GetEffectiveContextLimit(int rawContextSize)
        {
            return Math.Max(256, (int)(rawContextSize * (ReliabilityCeilingPercent / 100.0)));
        }
    }

    // ═══════════════════════════════════════════════
    // Persisted toggle + profile state
    // ═══════════════════════════════════════════════
    public sealed class SmartCompactionSettings
    {
        public bool Enabled { get; set; } = true;
        public Dictionary<string, int> ModelCeilings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string FilePath = Path.Combine(AppDataPaths.ChatHistory, "smart_compaction_settings.json");
        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.ChatHistory);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, WriteOptions));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SmartCompactionSettings.Save error: {ex.Message}");
            }
        }

        public static SmartCompactionSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    return JsonSerializer.Deserialize<SmartCompactionSettings>(File.ReadAllText(FilePath)) ?? new();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SmartCompactionSettings.Load error: {ex.Message}");
            }
            return new SmartCompactionSettings();
        }
    }

    // ═══════════════════════════════════════════════
    // Core Smart Context Compaction Engine
    // ═══════════════════════════════════════════════
    public sealed class SmartContextCompactionEngine
    {
        private const double AvgCharsPerToken = 4.0;
        private const double CompactionThresholdPercent = 80.0;
        private const int MinKeywordLength = 4;
        private const int KeywordOverlapThreshold = 3;

        private SmartCompactionSettings _settings;
        private bool _compactionPending;

        public bool IsEnabled => _settings.Enabled;
        public bool CompactionPending => _compactionPending;

        public SmartContextCompactionEngine()
        {
            _settings = SmartCompactionSettings.Load();
        }

        public SmartCompactionSettings Settings => _settings;

        public void SetEnabled(bool enabled)
        {
            _settings.Enabled = enabled;
            _settings.Save();
        }

        public void ReloadSettings()
        {
            _settings = SmartCompactionSettings.Load();
        }

        // ═══════════════════════════════════════════════
        // Per-model reliability ceiling resolution
        // ═══════════════════════════════════════════════
        public ContextProfile GetContextProfile(string modelName)
        {
            if (!string.IsNullOrWhiteSpace(modelName) && _settings.ModelCeilings.TryGetValue(modelName, out int ceiling))
            {
                return new ContextProfile { ReliabilityCeilingPercent = ceiling };
            }

            int defaultCeiling = ResolveDefaultCeiling(modelName);
            return new ContextProfile { ReliabilityCeilingPercent = defaultCeiling };
        }

        public void SetModelCeiling(string modelName, int ceilingPercent)
        {
            ceilingPercent = Math.Clamp(ceilingPercent, 30, 100);
            _settings.ModelCeilings[modelName] = ceilingPercent;
            _settings.Save();
        }

        private static int ResolveDefaultCeiling(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
                return 75;

            string lower = modelName.ToLowerInvariant();

            if (lower.Contains("qwen"))
                return 75;
            if (lower.Contains("nemotron"))
                return 70;
            if (lower.Contains("gemma") && (lower.Contains("4") || lower.Contains("-4")))
                return 80;

            return 75;
        }

        // ═══════════════════════════════════════════════
        // Threshold check — returns true if compaction should trigger
        // ═══════════════════════════════════════════════
        public bool ShouldCompact(int estimatedTokens, int rawContextSize, string modelName)
        {
            if (!_settings.Enabled)
                return false;

            var profile = GetContextProfile(modelName);
            int effectiveLimit = profile.GetEffectiveContextLimit(rawContextSize);
            double threshold = effectiveLimit * (CompactionThresholdPercent / 100.0);
            return estimatedTokens >= threshold;
        }

        public void FlagCompactionPending()
        {
            _compactionPending = true;
        }

        public void ClearCompactionPending()
        {
            _compactionPending = false;
        }

        // ═══════════════════════════════════════════════
        // Context health metrics for UI
        // ═══════════════════════════════════════════════
        public (int usedTokens, int effectiveLimit, double thresholdPoint, string healthLabel) ComputeContextHealth(
            int estimatedTokens, int rawContextSize, string modelName)
        {
            var profile = GetContextProfile(modelName);
            int effectiveLimit = profile.GetEffectiveContextLimit(rawContextSize);
            double threshold = effectiveLimit * (CompactionThresholdPercent / 100.0);
            double usagePct = effectiveLimit <= 0 ? 0 : (estimatedTokens * 100.0 / effectiveLimit);

            string label;
            if (_compactionPending)
                label = "Compaction pending";
            else if (usagePct >= CompactionThresholdPercent)
                label = "Compacting";
            else if (usagePct >= 60)
                label = "Moderate";
            else
                label = "Healthy";

            return (estimatedTokens, effectiveLimit, threshold, label);
        }

        // ═══════════════════════════════════════════════
        // Importance classification
        // ═══════════════════════════════════════════════
        public static MessageImportance ClassifyImportance(string role, string content, bool isPinned)
        {
            if (isPinned)
                return MessageImportance.High;

            if (string.IsNullOrWhiteSpace(content))
                return MessageImportance.Low;

            // Always high: code blocks, errors, stack traces
            if (content.Contains("```") || content.Contains("Traceback") || content.Contains("Exception:") ||
                content.Contains("Error:") || content.Contains("at line"))
                return MessageImportance.High;

            // Always high: explicit requirements/constraints
            string lower = content.ToLowerInvariant();
            string[] requirementMarkers = ["must ", "should ", "need to ", "require", "constraint", "do not ", "don't ", "never "];
            if (requirementMarkers.Any(m => lower.Contains(m)))
                return MessageImportance.High;

            // Always high: Architect plan or Critic findings
            if (lower.Contains("architect plan complete") || lower.Contains("critic review complete") ||
                lower.Contains("builder output complete"))
                return MessageImportance.High;

            // Role-based defaults
            if (string.Equals(role, "architect", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "critic", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(role, "critic-final", StringComparison.OrdinalIgnoreCase))
                return MessageImportance.High;

            // Greetings, affirmations, short messages
            if (content.Length < 60)
            {
                string[] lowSignals = ["hello", "hi ", "thanks", "thank you", "ok", "okay", "sure", "got it",
                    "sounds good", "great", "nice", "cool", "alright", "yes", "no", "yep", "nope"];
                if (lowSignals.Any(s => lower.Contains(s)))
                    return MessageImportance.Low;
            }

            // System messages that are informational
            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (lower.Contains("tokens:") || lower.Contains("tok/s") || lower.Contains("initialized") ||
                    lower.Contains("context pressure") || lower.Contains("context optimized"))
                    return MessageImportance.Low;
            }

            // Default: anything with substance is High
            if (content.Length > 200)
                return MessageImportance.High;

            return MessageImportance.Low;
        }

        // ═══════════════════════════════════════════════
        // Keyword extraction for semantic grouping
        // ═══════════════════════════════════════════════
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "with", "that", "this", "from", "into", "about", "would", "should",
            "could", "must", "need", "have", "has", "are", "was", "were", "will", "been", "being",
            "does", "did", "not", "but", "also", "just", "more", "some", "than", "then", "when",
            "what", "which", "where", "there", "their", "they", "them", "these", "those", "your",
            "you", "can", "how", "here", "very", "much", "each", "only", "such", "like", "make",
            "made", "well", "back", "even", "most", "other", "after", "before", "over", "under"
        };

        public static HashSet<string> ExtractKeywords(string text)
        {
            var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
                return keywords;

            var matches = Regex.Matches(text, @"\b[A-Za-z_][A-Za-z0-9_]{3,}\b");
            foreach (Match m in matches)
            {
                string word = m.Value;
                if (word.Length >= MinKeywordLength && !StopWords.Contains(word))
                    keywords.Add(word.ToLowerInvariant());
            }

            return keywords;
        }

        // ═══════════════════════════════════════════════
        // Semantic grouping by keyword overlap
        // ═══════════════════════════════════════════════
        public static List<List<int>> GroupByTopic(List<(int Index, string Content)> candidates)
        {
            if (candidates.Count == 0)
                return new();

            var keywordSets = new Dictionary<int, HashSet<string>>();
            foreach (var c in candidates)
            {
                keywordSets[c.Index] = ExtractKeywords(c.Content);
            }

            // Union-find for grouping
            var parent = new Dictionary<int, int>();
            foreach (var c in candidates)
                parent[c.Index] = c.Index;

            int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            void Union(int a, int b) { parent[Find(a)] = Find(b); }

            for (int i = 0; i < candidates.Count; i++)
            {
                for (int j = i + 1; j < candidates.Count; j++)
                {
                    int overlap = keywordSets[candidates[i].Index]
                        .Intersect(keywordSets[candidates[j].Index], StringComparer.OrdinalIgnoreCase)
                        .Count();
                    if (overlap >= KeywordOverlapThreshold)
                    {
                        Union(candidates[i].Index, candidates[j].Index);
                    }
                }
            }

            var groups = new Dictionary<int, List<int>>();
            foreach (var c in candidates)
            {
                int root = Find(c.Index);
                if (!groups.ContainsKey(root))
                    groups[root] = new List<int>();
                groups[root].Add(c.Index);
            }

            return groups.Values.ToList();
        }

        // ═══════════════════════════════════════════════
        // Build the compression prompt for a topic group
        // ═══════════════════════════════════════════════
        public static string BuildCompressionPrompt(List<string> messages)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Compress the following conversation messages into a single concise paragraph.");
            sb.AppendLine("Capture every factual conclusion, decision, or piece of information from these messages that would be needed to continue the conversation coherently.");
            sb.AppendLine("Discard pleasantries, repetition, and exploratory content that led nowhere.");
            sb.AppendLine("Output ONLY the compressed paragraph — no headings, no labels, no commentary.");
            sb.AppendLine();
            for (int i = 0; i < messages.Count; i++)
            {
                sb.AppendLine($"[Message {i + 1}]");
                sb.AppendLine(messages[i].Trim());
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ═══════════════════════════════════════════════
        // Fallback summary when no model is available
        // ═══════════════════════════════════════════════
        public static string BuildFallbackSummary(List<string> messages)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
            {
                string truncated = msg.Length > 100 ? msg[..100] + "..." : msg;
                sb.Append(truncated.Trim()).Append(' ');
            }
            return sb.ToString().Trim();
        }

        // ═══════════════════════════════════════════════
        // Quality validation — ensure topics from pinned messages and requirements are present
        // ═══════════════════════════════════════════════
        public static List<string> ValidateCompaction(
            List<string> pinnedTopics,
            List<string> requirements,
            List<string> retainedHighContents,
            List<CompactionSummaryEntry> summaries)
        {
            var allCompactedText = new StringBuilder();
            foreach (var content in retainedHighContents)
                allCompactedText.AppendLine(content);
            foreach (var summary in summaries)
                allCompactedText.AppendLine(summary.Summary);

            string fullText = allCompactedText.ToString().ToLowerInvariant();
            var missingTopics = new List<string>();

            foreach (var topic in pinnedTopics.Concat(requirements).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(topic) || topic.Length < 3)
                    continue;

                // Extract significant words from the topic
                var words = Regex.Matches(topic, @"\b[A-Za-z]{4,}\b")
                    .Select(m => m.Value.ToLowerInvariant())
                    .Where(w => !StopWords.Contains(w))
                    .Take(4)
                    .ToList();

                bool covered = words.Count == 0 || words.Any(w => fullText.Contains(w));
                if (!covered)
                    missingTopics.Add(topic);
            }

            return missingTopics;
        }

        // ═══════════════════════════════════════════════
        // Topic label generation from keyword cluster
        // ═══════════════════════════════════════════════
        public static string GenerateTopicLabel(List<string> messages)
        {
            var allKeywords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var msg in messages)
            {
                foreach (var kw in ExtractKeywords(msg))
                {
                    allKeywords[kw] = allKeywords.GetValueOrDefault(kw) + 1;
                }
            }

            var topTerms = allKeywords
                .OrderByDescending(kv => kv.Value)
                .Take(3)
                .Select(kv => kv.Key)
                .ToList();

            return topTerms.Count > 0 ? string.Join(", ", topTerms) : "general discussion";
        }

        // ═══════════════════════════════════════════════
        // Estimate tokens for a string
        // ═══════════════════════════════════════════════
        public static int EstimateTokens(string text)
        {
            return string.IsNullOrWhiteSpace(text) ? 0 : (int)Math.Ceiling(text.Length / AvgCharsPerToken);
        }
    }
}
