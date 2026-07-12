using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public enum SessionHippocampusSource
    {
        StudySession,
        ArchitectOutput,
        BuilderOutput,
        CriticOutput
    }

    public enum SessionHippocampusTag
    {
        DomainDefinition,
        Concept,
        Summary,
        ErrorPattern,
        SolutionPattern
    }

    public sealed class SessionHippocampusEntry
    {
        private HashSet<string>? _keywordCache;
        private string? _keywordCacheContent;

        public string Content { get; set; } = "";
        public SessionHippocampusSource Source { get; set; }
        public SessionHippocampusTag Tag { get; set; }
        public int Priority { get; set; }
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public int SessionRunIndex { get; set; }
        public int AccessCount { get; set; }
        public DateTime? LastAccessedTimestamp { get; set; }

        // Keyword extraction is regex work proportional to content size, and the store's query,
        // consolidation, and dedup paths all need the same set repeatedly. Cache it per content
        // value (Content mutates on merge, so the cache is keyed to the exact string instance/value).
        internal HashSet<string> GetKeywordSet(Func<string, HashSet<string>> extractor)
        {
            if (_keywordCache != null && string.Equals(_keywordCacheContent, Content, StringComparison.Ordinal))
                return _keywordCache;

            _keywordCache = extractor(Content);
            _keywordCacheContent = Content;
            return _keywordCache;
        }
    }

    public sealed class SessionHippocampusMetadata
    {
        public int TotalEntryCount { get; set; }
        public Dictionary<SessionHippocampusSource, int> SourceCounts { get; set; } = new();
        public Dictionary<SessionHippocampusTag, int> TagCounts { get; set; } = new();
        public DateTime? MostRecentWriteTimestamp { get; set; }
        public bool StudySessionCompleted { get; set; }
        public int TotalEstimatedTokens { get; set; }
    }

    public sealed class SessionHippocampus
    {
        private static readonly int MaxContentTokens = 420;
        private static readonly int QueryBudgetTokens = 640;
        private static readonly int MaxEntries = 160;
        private static readonly int MaxEntriesPerSource = 80;
        private static readonly double AvgCharsPerToken = 4.0;

        private readonly List<SessionHippocampusEntry> _entries = new();
        private bool _studySessionCompleted;

        public event EventHandler? StoreChanged;

        public void Write(SessionHippocampusEntry entry)
        {
            WriteInternal(entry, raiseStoreChanged: true);
        }

        public List<SessionHippocampusEntry> ExportEntries()
        {
            return _entries
                .OrderByDescending(e => e.Priority)
                .ThenByDescending(e => e.Timestamp)
                .Select(CloneEntry)
                .ToList();
        }

        public void Restore(IEnumerable<SessionHippocampusEntry>? entries, bool studySessionCompleted)
        {
            _entries.Clear();
            _studySessionCompleted = studySessionCompleted;

            if (entries != null)
            {
                foreach (SessionHippocampusEntry entry in entries.OrderBy(e => e.Timestamp))
                {
                    WriteInternal(CloneEntry(entry), raiseStoreChanged: false);
                }
            }

            EnforceCapacity();
            RaiseStoreChanged();
        }

        public static string BuildPromptContext(IEnumerable<SessionHippocampusEntry> entries, int maxTokens = 360)
        {
            if (entries == null)
            {
                return "";
            }

            int maxChars = Math.Max(160, (int)(maxTokens * AvgCharsPerToken));
            var sb = new StringBuilder();
            int index = 0;

            foreach (SessionHippocampusEntry entry in entries)
            {
                string compact = CompactForPrompt(entry);
                if (string.IsNullOrWhiteSpace(compact))
                {
                    continue;
                }

                string block = $"[{++index}] [{GetSourceLabel(entry.Source)}] [{GetTagLabel(entry.Tag)}] [P{entry.Priority}]\n{compact}\n\n";
                if (sb.Length + block.Length > maxChars)
                {
                    if (sb.Length == 0)
                    {
                        sb.Append(block[..Math.Min(block.Length, maxChars)].TrimEnd());
                    }

                    break;
                }

                sb.Append(block);
            }

            return sb.ToString().Trim();
        }

        public List<SessionHippocampusEntry> Query(string query, int maxResults = 5)
        {
            if (_entries.Count == 0 || string.IsNullOrWhiteSpace(query) || maxResults <= 0)
            {
                return new List<SessionHippocampusEntry>();
            }

            string normalizedQuery = NormalizeInlineWhitespace(query);
            HashSet<string> queryKeywords = ExtractKeywords(normalizedQuery);
            bool semanticAvailable = LocalSemanticEmbeddingService.Shared.IsAvailable;
            if (queryKeywords.Count == 0 && !semanticAvailable)
            {
                return new List<SessionHippocampusEntry>();
            }

            bool queryLooksLikeError = QueryLooksLikeError(normalizedQuery, queryKeywords);
            bool queryLooksLikeDefinition = QueryLooksLikeDefinition(normalizedQuery, queryKeywords);
            var scored = new List<(SessionHippocampusEntry Entry, double Score, int Order)>();

            for (int i = 0; i < _entries.Count; i++)
            {
                SessionHippocampusEntry entry = _entries[i];
                HashSet<string> contentKeywords = entry.GetKeywordSet(ExtractKeywords);
                double semanticBoost = 0;
                double semanticScore = 0;
                bool semanticMatch = semanticAvailable
                    && LocalSemanticEmbeddingService.Shared.TryGetSimilarity(normalizedQuery, entry.Content, out semanticScore)
                    && semanticScore >= 0.26;

                if (semanticMatch)
                    semanticBoost = Math.Clamp((semanticScore - 0.20) / 0.55, 0, 1) * 7.0;

                if (contentKeywords.Count == 0 && !semanticMatch)
                {
                    continue;
                }

                int overlap = contentKeywords.Count(k => queryKeywords.Contains(k));
                if (overlap == 0 && !semanticMatch)
                {
                    continue;
                }

                double coverage = queryKeywords.Count == 0 ? 0 : (double)overlap / queryKeywords.Count;
                double density = contentKeywords.Count == 0 ? 0 : (double)overlap / contentKeywords.Count;
                double weighted = (coverage * 5.0) + (density * 2.5) + semanticBoost + PriorityMultiplier(entry.Priority);

                if (entry.Content.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                {
                    weighted += 2.5;
                }

                string leadingTerm = ExtractLeadingTerm(entry.Content);
                if (!string.IsNullOrWhiteSpace(leadingTerm)
                    && normalizedQuery.Contains(leadingTerm, StringComparison.OrdinalIgnoreCase))
                {
                    weighted += 3.0;
                }

                if (entry.Tag == SessionHippocampusTag.DomainDefinition && queryLooksLikeDefinition)
                {
                    weighted += 2.0;
                }

                if (entry.Tag == SessionHippocampusTag.ErrorPattern && queryLooksLikeError)
                {
                    weighted += 2.5;
                }
                else if (entry.Tag == SessionHippocampusTag.SolutionPattern && !queryLooksLikeError)
                {
                    weighted += 0.8;
                }

                if (entry.Source == SessionHippocampusSource.StudySession && _studySessionCompleted)
                {
                    weighted += 0.75;
                }

                if (entry.AccessCount > 0)
                {
                    weighted += Math.Min(1.25, entry.AccessCount * 0.12);
                }

                double hoursOld = Math.Max(0, (DateTime.Now - entry.Timestamp).TotalHours);
                weighted += Math.Max(0, 1.2 - (hoursOld / 72.0));

                scored.Add((entry, weighted, i));
            }

            var ranked = scored
                .OrderByDescending(s => s.Score)
                .ThenByDescending(s => s.Entry.Priority)
                .ThenByDescending(s => s.Entry.Timestamp)
                .ThenBy(s => s.Order)
                .Take(maxResults)
                .Select(s => s.Entry)
                .ToList();

            int totalTokens = ranked.Sum(e => EstimateTokens(e.Content));
            while (totalTokens > QueryBudgetTokens && ranked.Count > 0)
            {
                SessionHippocampusEntry tail = ranked[^1];
                ranked.RemoveAt(ranked.Count - 1);
                totalTokens -= EstimateTokens(tail.Content);
            }

            DateTime accessStamp = DateTime.Now;
            foreach (SessionHippocampusEntry entry in ranked)
            {
                entry.AccessCount++;
                entry.LastAccessedTimestamp = accessStamp;
            }

            return ranked;
        }

        public void Consolidate()
        {
            if (_entries.Count < 2)
            {
                EnforceCapacity();
                RaiseStoreChanged();
                return;
            }

            bool mergedAny;
            do
            {
                mergedAny = false;
                for (int i = 0; i < _entries.Count - 1; i++)
                {
                    for (int j = i + 1; j < _entries.Count; j++)
                    {
                        double overlap = KeywordOverlap(_entries[i], _entries[j]);
                        if (overlap <= 0.70)
                        {
                            continue;
                        }

                        SessionHippocampusEntry a = _entries[i];
                        SessionHippocampusEntry b = _entries[j];
                        SessionHippocampusEntry preferred = a.Priority >= b.Priority ? a : b;

                        var merged = new SessionHippocampusEntry
                        {
                            Source = preferred.Source,
                            Tag = preferred.Tag,
                            Priority = preferred.Priority,
                            Timestamp = a.Timestamp <= b.Timestamp ? a.Timestamp : b.Timestamp,
                            SessionRunIndex = Math.Max(a.SessionRunIndex, b.SessionRunIndex),
                            AccessCount = Math.Max(a.AccessCount, b.AccessCount),
                            LastAccessedTimestamp = a.LastAccessedTimestamp >= b.LastAccessedTimestamp ? a.LastAccessedTimestamp : b.LastAccessedTimestamp,
                            Content = MergeContent(a.Content, b.Content)
                        };

                        _entries.RemoveAt(j);
                        _entries.RemoveAt(i);
                        _entries.Add(merged);
                        mergedAny = true;
                        break;
                    }

                    if (mergedAny)
                    {
                        break;
                    }
                }
            }
            while (mergedAny && _entries.Count > 1);

            EnforceCapacity();
            RaiseStoreChanged();
        }

        public void Clear(bool confirm)
        {
            if (!confirm)
            {
                Debug.WriteLine("[SessionHippocampus] Clear skipped: confirmation missing.");
                return;
            }

            _entries.Clear();
            _studySessionCompleted = false;
            RaiseStoreChanged();
        }

        public SessionHippocampusMetadata GetMetadata()
        {
            var bySource = Enum.GetValues(typeof(SessionHippocampusSource))
                .Cast<SessionHippocampusSource>()
                .ToDictionary(s => s, _ => 0);

            var byTag = Enum.GetValues(typeof(SessionHippocampusTag))
                .Cast<SessionHippocampusTag>()
                .ToDictionary(t => t, _ => 0);

            foreach (SessionHippocampusEntry entry in _entries)
            {
                bySource[entry.Source]++;
                byTag[entry.Tag]++;
            }

            return new SessionHippocampusMetadata
            {
                TotalEntryCount = _entries.Count,
                SourceCounts = bySource,
                TagCounts = byTag,
                MostRecentWriteTimestamp = _entries.Count == 0 ? null : _entries.Max(e => e.Timestamp),
                StudySessionCompleted = _studySessionCompleted,
                TotalEstimatedTokens = _entries.Sum(e => EstimateTokens(e.Content))
            };
        }

        public void ClearBySource(SessionHippocampusSource source)
        {
            _entries.RemoveAll(e => e.Source == source);
            if (source == SessionHippocampusSource.StudySession)
            {
                _studySessionCompleted = false;
            }

            RaiseStoreChanged();
        }

        internal void MarkStudySessionCompleted()
        {
            _studySessionCompleted = true;
            RaiseStoreChanged();
        }

        private void WriteInternal(SessionHippocampusEntry entry, bool raiseStoreChanged)
        {
            if (entry == null)
            {
                Debug.WriteLine("[SessionHippocampus] Write rejected: entry is null.");
                return;
            }

            entry.Content = NormalizeContent(entry.Content);
            if (string.IsNullOrWhiteSpace(entry.Content))
            {
                Debug.WriteLine("[SessionHippocampus] Write rejected: content is empty.");
                return;
            }

            if (!Enum.IsDefined(typeof(SessionHippocampusSource), entry.Source))
            {
                Debug.WriteLine("[SessionHippocampus] Write rejected: invalid source.");
                return;
            }

            int tokenCount = EstimateTokens(entry.Content);
            if (tokenCount > MaxContentTokens)
            {
                entry.Content = BuildCappedContent(entry.Content, MaxContentTokens);
            }

            if (!Enum.IsDefined(typeof(SessionHippocampusTag), entry.Tag))
            {
                Debug.WriteLine("[SessionHippocampus] Write rejected: invalid tag.");
                return;
            }

            entry.Priority = Math.Clamp(entry.Priority, 1, 3);
            entry.AccessCount = Math.Max(0, entry.AccessCount);
            if (entry.Timestamp == default)
            {
                entry.Timestamp = DateTime.Now;
            }

            SessionHippocampusEntry? existing = FindNearDuplicate(entry);
            if (existing != null)
            {
                existing.Content = MergeContent(existing.Content, entry.Content);
                existing.Priority = Math.Max(existing.Priority, entry.Priority);
                existing.Timestamp = existing.Timestamp >= entry.Timestamp ? existing.Timestamp : entry.Timestamp;
                existing.SessionRunIndex = Math.Max(existing.SessionRunIndex, entry.SessionRunIndex);
                existing.AccessCount = Math.Max(existing.AccessCount, entry.AccessCount);
                existing.LastAccessedTimestamp = existing.LastAccessedTimestamp >= entry.LastAccessedTimestamp
                    ? existing.LastAccessedTimestamp
                    : entry.LastAccessedTimestamp;
            }
            else
            {
                _entries.Add(entry);
            }

            // Embed at write time so Query()'s semantic pass is a cache hit instead of a
            // blocking native inference per entry on the first lookup.
            LocalSemanticEmbeddingService.Shared.PrewarmInBackground([(existing ?? entry).Content]);

            EnforceCapacity();

            if (raiseStoreChanged)
            {
                RaiseStoreChanged();
            }
        }

        private static double PriorityMultiplier(int priority)
        {
            return priority switch
            {
                3 => 2.0,
                2 => 1.4,
                _ => 1.0
            };
        }

        private static int EstimateTokens(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return 0;
            }

            return (int)Math.Ceiling(content.Length / AvgCharsPerToken);
        }

        private static string BuildCappedContent(string content, int maxTokens)
        {
            int maxChars = (int)(maxTokens * AvgCharsPerToken);
            string normalized = NormalizeContent(content);
            return normalized.Length <= maxChars ? normalized : normalized[..maxChars].TrimEnd();
        }

        private static HashSet<string> ExtractKeywords(string text)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(text))
            {
                return set;
            }

            foreach (Match match in Regex.Matches(text.ToLowerInvariant(), @"\b[a-z0-9_\-]{3,}\b"))
            {
                set.Add(match.Value);
            }

            return set;
        }

        private static string NormalizeContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            var lines = content
                .Split('\n')
                .Select(line => NormalizeInlineWhitespace(line.Trim()))
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            return string.Join(Environment.NewLine, lines);
        }

        private static string NormalizeInlineWhitespace(string text)
        {
            return string.IsNullOrWhiteSpace(text)
                ? ""
                : Regex.Replace(text.Trim(), @"\s+", " ");
        }

        private static bool QueryLooksLikeError(string query, HashSet<string> keywords)
        {
            return keywords.Contains("error")
                || keywords.Contains("bug")
                || keywords.Contains("fix")
                || keywords.Contains("failed")
                || keywords.Contains("failure")
                || keywords.Contains("exception")
                || query.Contains("what broke", StringComparison.OrdinalIgnoreCase);
        }

        private static bool QueryLooksLikeDefinition(string query, HashSet<string> keywords)
        {
            return keywords.Contains("define")
                || keywords.Contains("meaning")
                || keywords.Contains("term")
                || query.Contains("what is", StringComparison.OrdinalIgnoreCase)
                || query.Contains("what does", StringComparison.OrdinalIgnoreCase);
        }

        private SessionHippocampusEntry? FindNearDuplicate(SessionHippocampusEntry candidate)
        {
            string candidateTerm = ExtractLeadingTerm(candidate.Content);

            foreach (SessionHippocampusEntry entry in _entries)
            {
                if (entry.Source != candidate.Source || entry.Tag != candidate.Tag)
                {
                    continue;
                }

                if (string.Equals(entry.Content, candidate.Content, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }

                if (!string.IsNullOrWhiteSpace(candidateTerm)
                    && string.Equals(candidateTerm, ExtractLeadingTerm(entry.Content), StringComparison.OrdinalIgnoreCase)
                    && candidate.Tag == SessionHippocampusTag.DomainDefinition)
                {
                    return entry;
                }

                if (KeywordOverlap(entry, candidate) >= 0.92)
                {
                    return entry;
                }
            }

            return null;
        }

        private void EnforceCapacity()
        {
            foreach (SessionHippocampusSource source in Enum.GetValues(typeof(SessionHippocampusSource)).Cast<SessionHippocampusSource>())
            {
                while (_entries.Count(e => e.Source == source) > MaxEntriesPerSource)
                {
                    SessionHippocampusEntry removable = _entries
                        .Where(e => e.Source == source)
                        .OrderBy(e => PreserveWeight(e))
                        .ThenBy(e => e.Priority)
                        .ThenBy(e => e.AccessCount)
                        .ThenBy(e => e.Timestamp)
                        .First();
                    _entries.Remove(removable);
                }
            }

            while (_entries.Count > MaxEntries)
            {
                SessionHippocampusEntry removable = _entries
                    .OrderBy(e => PreserveWeight(e))
                    .ThenBy(e => e.Priority)
                    .ThenBy(e => e.AccessCount)
                    .ThenBy(e => e.Timestamp)
                    .First();
                _entries.Remove(removable);
            }
        }

        private static int PreserveWeight(SessionHippocampusEntry entry)
        {
            int weight = entry.Priority;
            if (entry.Source == SessionHippocampusSource.StudySession)
            {
                weight += 1;
            }

            if (entry.Tag == SessionHippocampusTag.DomainDefinition)
            {
                weight += 2;
            }

            return weight;
        }

        private static double KeywordOverlap(SessionHippocampusEntry left, SessionHippocampusEntry right)
        {
            HashSet<string> a = left.GetKeywordSet(ExtractKeywords);
            HashSet<string> b = right.GetKeywordSet(ExtractKeywords);
            if (a.Count == 0 || b.Count == 0)
            {
                return 0;
            }

            int overlap = a.Count(k => b.Contains(k));
            int max = Math.Max(a.Count, b.Count);
            return max == 0 ? 0 : (double)overlap / max;
        }

        private static SessionHippocampusEntry CloneEntry(SessionHippocampusEntry entry)
        {
            return new SessionHippocampusEntry
            {
                Content = entry.Content,
                Source = entry.Source,
                Tag = entry.Tag,
                Priority = entry.Priority,
                Timestamp = entry.Timestamp,
                SessionRunIndex = entry.SessionRunIndex,
                AccessCount = entry.AccessCount,
                LastAccessedTimestamp = entry.LastAccessedTimestamp
            };
        }

        private static string CompactForPrompt(SessionHippocampusEntry entry)
        {
            int maxLines = entry.Tag == SessionHippocampusTag.DomainDefinition ? 2 : 4;
            var selected = new List<string>();

            foreach (string rawLine in NormalizeContent(entry.Content).Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)
                    || line.Equals("Summary:", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("Memory:", StringComparison.OrdinalIgnoreCase)
                    || line.Equals("Concepts:", StringComparison.OrdinalIgnoreCase)
                    || Regex.IsMatch(line, @"^Q\d+\s*:", RegexOptions.IgnoreCase)
                    || Regex.IsMatch(line, @"^A\d+\s*:", RegexOptions.IgnoreCase))
                {
                    continue;
                }

                string cleaned = Regex.Replace(line, @"^(?:[-*]|\d+[\.)])\s*", "").Trim();
                if (string.IsNullOrWhiteSpace(cleaned))
                {
                    continue;
                }

                selected.Add(cleaned);
                if (selected.Count >= maxLines)
                {
                    break;
                }
            }

            if (selected.Count == 0)
            {
                return BuildCappedContent(entry.Content, 120);
            }

            var sb = new StringBuilder();
            for (int i = 0; i < selected.Count; i++)
            {
                if (i == 0 && entry.Tag == SessionHippocampusTag.DomainDefinition)
                {
                    sb.AppendLine(selected[i]);
                }
                else
                {
                    sb.AppendLine($"- {selected[i]}");
                }
            }

            return BuildCappedContent(sb.ToString(), entry.Tag == SessionHippocampusTag.DomainDefinition ? 90 : 120);
        }

        private static string ExtractLeadingTerm(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            string firstLine = NormalizeContent(content).Split('\n').FirstOrDefault()?.Trim() ?? "";
            int colon = firstLine.IndexOf(':');
            if (colon > 0)
            {
                firstLine = firstLine[..colon];
            }

            return NormalizeInlineWhitespace(firstLine);
        }

        private static string GetSourceLabel(SessionHippocampusSource source)
        {
            return source == SessionHippocampusSource.StudySession
                ? "STUDIED REFERENCE"
                : "PRIOR SESSION CONTEXT";
        }

        private static string GetTagLabel(SessionHippocampusTag tag)
        {
            return tag switch
            {
                SessionHippocampusTag.DomainDefinition => "DOMAIN DEFINITION",
                SessionHippocampusTag.ErrorPattern => "ERROR PATTERN",
                SessionHippocampusTag.SolutionPattern => "SOLUTION PATTERN",
                SessionHippocampusTag.Summary => "SUMMARY",
                _ => "CONCEPT"
            };
        }

        private static string MergeContent(string first, string second)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void addParts(string text)
            {
                foreach (string line in text.Split('\n'))
                {
                    string t = line.Trim();
                    if (string.IsNullOrWhiteSpace(t))
                    {
                        continue;
                    }

                    if (seen.Add(t))
                    {
                        parts.Add(t);
                    }
                }
            }

            addParts(first);
            addParts(second);

            var sb = new StringBuilder();
            foreach (string part in parts)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(part);
                if (EstimateTokens(sb.ToString()) >= MaxContentTokens)
                {
                    string limited = sb.ToString();
                    int maxChars = (int)(MaxContentTokens * AvgCharsPerToken);
                    return limited.Length <= maxChars ? limited : limited[..maxChars];
                }
            }

            return sb.ToString();
        }

        private void RaiseStoreChanged()
        {
            StoreChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
