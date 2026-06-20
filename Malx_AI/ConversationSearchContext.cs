using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    internal readonly record struct ConversationSearchTurn(string Role, string Content);

    internal static class ConversationSearchContext
    {
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex QuotedPhraseRegex = new("\"(?<value>[^\"]{2,90})\"|'(?<value>[^']{2,90})'", RegexOptions.Compiled);
        private static readonly Regex ContextualReferentRegex = new(@"\b(?:the|this|that|same|previous|earlier)\s+(?:movie|film|show|series|book|article|paper|game|company|product|model|album|song|case|topic|issue|story)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PronounFollowUpRegex = new(@"\b(?:it|that|this|they|them|those|these|he|she|him|her|his|their|its|same|again|also)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TypedTitleRegex = new(@"\b(?:the\s+)?(?<type>movie|film|show|series|book|article|paper|game|album|song)\s+(?:called|named|titled)?\s*[""']?(?<title>[A-Z0-9][^?.,;\r\n]{0,90})", RegexOptions.Compiled);

        private static readonly string[] MediaTypes = ["movie", "film", "show", "series", "book", "article", "paper", "game", "album", "song"];
        private static readonly HashSet<string> AnchorStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "and", "are", "as", "at", "about", "be", "by", "can", "did", "do", "does", "for",
            "from", "had", "has", "have", "how", "in", "into", "is", "it", "of", "on", "or", "that",
            "the", "this", "to", "was", "were", "what", "when", "where", "which", "who", "why", "with",
            "movie", "film", "show", "series", "book", "article", "paper", "game", "album", "song",
            "end", "ending", "about", "plot", "story", "tell", "explain", "describe", "give", "find",
            "search", "look", "lookup", "show", "summarize", "summary"
        };

        public static string BuildContextualSearchPrompt(string currentQuery, IEnumerable<ConversationSearchTurn>? recentTurns)
        {
            string current = Normalize(currentQuery);
            if (string.IsNullOrWhiteSpace(current))
                return string.Empty;

            List<ConversationSearchTurn> turns = (recentTurns ?? [])
                .Where(t => !string.IsNullOrWhiteSpace(t.Content))
                .Select(t => new ConversationSearchTurn(t.Role ?? string.Empty, Normalize(t.Content)))
                .Where(t => !string.IsNullOrWhiteSpace(t.Content))
                .ToList();

            if (turns.Count == 0 || !NeedsConversationAnchor(current))
                return current;

            if (turns.Count > 0
                && string.Equals(turns[^1].Role, "user", StringComparison.OrdinalIgnoreCase)
                && string.Equals(turns[^1].Content, current, StringComparison.OrdinalIgnoreCase))
            {
                turns.RemoveAt(turns.Count - 1);
            }

            string anchor = FindRecentAnchor(current, turns);
            if (string.IsNullOrWhiteSpace(anchor) || CurrentAlreadyContainsAnchor(current, anchor))
                return current;

            string combined = Normalize(anchor + " " + current);
            return combined.Length <= 260 ? combined : combined[..260].TrimEnd();
        }

        private static bool NeedsConversationAnchor(string current)
        {
            if (string.IsNullOrWhiteSpace(current))
                return false;

            if (WebSearchService.LooksLikeLowSpecificitySearchQuery(current))
                return true;

            if (ContextualReferentRegex.IsMatch(current))
                return !HasExplicitTypedTitle(current);

            if (current.Length <= 140 && PronounFollowUpRegex.IsMatch(current))
                return true;

            return false;
        }

        private static bool HasExplicitTypedTitle(string text)
        {
            foreach (Match match in TypedTitleRegex.Matches(text ?? string.Empty))
            {
                string title = CleanTitle(match.Groups["title"].Value);
                if (!string.IsNullOrWhiteSpace(title))
                    return true;
            }

            return false;
        }

        private static string FindRecentAnchor(string current, IReadOnlyList<ConversationSearchTurn> turns)
        {
            if (turns.Count == 0)
                return string.Empty;

            var recent = turns.TakeLast(10).Reverse().ToList();
            foreach (ConversationSearchTurn turn in recent.Where(t => string.Equals(t.Role, "user", StringComparison.OrdinalIgnoreCase)))
            {
                if (TryExtractAnchor(turn.Content, current, out string anchor))
                    return anchor;
            }

            foreach (ConversationSearchTurn turn in recent)
            {
                if (TryExtractAnchor(turn.Content, current, out string anchor))
                    return anchor;
            }

            return string.Empty;
        }

        private static bool TryExtractAnchor(string text, string current, out string anchor)
        {
            anchor = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string? requestedType = ResolveRequestedMediaType(current);
            if (!string.IsNullOrWhiteSpace(requestedType))
            {
                foreach (Match quote in QuotedPhraseRegex.Matches(text))
                {
                    string quoted = CleanTitle(quote.Groups["value"].Value);
                    if (!string.IsNullOrWhiteSpace(quoted) && IsNearTypeAlias(text, quote.Index, requestedType))
                    {
                        anchor = requestedType + " " + quoted;
                        return true;
                    }
                }

                foreach (Match match in TypedTitleRegex.Matches(text))
                {
                    string type = match.Groups["type"].Value;
                    if (!TypeMatches(type, requestedType))
                        continue;

                    string title = CleanTitle(match.Groups["title"].Value);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        anchor = requestedType + " " + title;
                        return true;
                    }
                }

                if (ContainsUnresolvedTypedReference(text, requestedType))
                    return false;
            }

            foreach (Match quote in QuotedPhraseRegex.Matches(text))
            {
                string quoted = CleanTitle(quote.Groups["value"].Value);
                if (!string.IsNullOrWhiteSpace(quoted))
                {
                    anchor = quoted;
                    return true;
                }
            }

            string capitalized = ExtractCapitalizedAnchor(text);
            if (!string.IsNullOrWhiteSpace(capitalized))
            {
                anchor = capitalized;
                return true;
            }

            return false;
        }

        private static bool ContainsUnresolvedTypedReference(string text, string requestedType)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(requestedType))
                return false;

            string typePattern = string.Equals(requestedType, "movie", StringComparison.OrdinalIgnoreCase)
                ? @"(?:movie|film)"
                : Regex.Escape(requestedType);

            return Regex.IsMatch(
                text,
                $@"\b(?:the|this|that|same|previous|earlier)?\s*{typePattern}\b",
                RegexOptions.IgnoreCase);
        }

        private static string? ResolveRequestedMediaType(string current)
        {
            foreach (string type in MediaTypes)
            {
                if (Regex.IsMatch(current ?? string.Empty, $@"\b{Regex.Escape(type)}\b", RegexOptions.IgnoreCase))
                    return type is "film" ? "movie" : type;
            }

            return null;
        }

        private static bool TypeMatches(string found, string requested)
        {
            string normalizedFound = string.Equals(found, "film", StringComparison.OrdinalIgnoreCase) ? "movie" : found;
            string normalizedRequested = string.Equals(requested, "film", StringComparison.OrdinalIgnoreCase) ? "movie" : requested;
            return string.Equals(normalizedFound, normalizedRequested, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsNearTypeAlias(string text, int quoteIndex, string requestedType)
        {
            int start = Math.Max(0, quoteIndex - 90);
            int length = Math.Min(text.Length - start, 180);
            string window = text.Substring(start, length);
            if (string.Equals(requestedType, "movie", StringComparison.OrdinalIgnoreCase))
                return Regex.IsMatch(window, @"\b(movie|film)\b", RegexOptions.IgnoreCase);

            return Regex.IsMatch(window, $@"\b{Regex.Escape(requestedType)}\b", RegexOptions.IgnoreCase);
        }

        private static string CleanTitle(string value)
        {
            string cleaned = Normalize(value).Trim(' ', '"', '\'', '.', '?', '!', ',', ':', ';', ')', '(');
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;

            cleaned = Regex.Split(cleaned, @"\b(?:is|are|was|were|about|where|which|that|who|with|starring|from|by|called|named|titled)\b", RegexOptions.IgnoreCase)[0];
            cleaned = cleaned.Trim(' ', '"', '\'', '.', '?', '!', ',', ':', ';', ')', '(');

            if (cleaned.Length < 2)
                return string.Empty;

            string[] words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length > 8)
                cleaned = string.Join(' ', words.Take(8));

            return cleaned;
        }

        private static string ExtractCapitalizedAnchor(string text)
        {
            foreach (Match match in Regex.Matches(text ?? string.Empty, @"\b[A-Z][A-Za-z0-9'&.-]*(?:\s+[A-Z][A-Za-z0-9'&.-]*){0,5}\b"))
            {
                string candidate = CleanTitle(match.Value);
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                string[] terms = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (terms.All(t => AnchorStopWords.Contains(t)))
                    continue;

                return candidate;
            }

            return string.Empty;
        }

        private static bool CurrentAlreadyContainsAnchor(string current, string anchor)
        {
            List<string> anchorTerms = Regex.Matches(anchor ?? string.Empty, @"\b[A-Za-z0-9][A-Za-z0-9'&.-]*\b")
                .Select(m => m.Value)
                .Where(t => t.Length >= 2 && !AnchorStopWords.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (anchorTerms.Count == 0)
                return true;

            HashSet<string> currentTerms = Regex.Matches(current ?? string.Empty, @"\b[A-Za-z0-9][A-Za-z0-9'&.-]*\b")
                .Select(m => m.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            int overlap = anchorTerms.Count(currentTerms.Contains);
            return overlap >= Math.Max(1, anchorTerms.Count / 2);
        }

        private static string Normalize(string text)
        {
            return WhitespaceRegex.Replace(text ?? string.Empty, " ").Trim();
        }
    }
}
