using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.Mcp
{
    internal static class McpMentionHelper
    {
        private static readonly Regex MentionRegex = new(
            @"@(?<handle>[A-Za-z][A-Za-z0-9_]*)",
            RegexOptions.Compiled);

        public static IReadOnlyList<McpMentionSpan> FindMentions(string text, IReadOnlyCollection<string> knownHandles)
        {
            if (string.IsNullOrEmpty(text) || knownHandles == null || knownHandles.Count == 0)
                return Array.Empty<McpMentionSpan>();

            var known = new HashSet<string>(knownHandles, StringComparer.OrdinalIgnoreCase);
            var spans = new List<McpMentionSpan>();

            foreach (Match match in MentionRegex.Matches(text))
            {
                string handle = match.Groups["handle"].Value;
                bool complete = known.Contains(handle);
                spans.Add(new McpMentionSpan(match.Index, match.Length, handle, complete));
            }

            return spans;
        }

        public static IReadOnlyList<string> GetCompleteMentionHandles(string text, IReadOnlyCollection<string> knownHandles)
        {
            return FindMentions(text, knownHandles)
                .Where(m => m.IsComplete)
                .Select(m => m.Handle)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Finds an in-progress @token at the caret (or end of text) for autocomplete.
        /// </summary>
        public static bool TryGetActiveMentionQuery(string text, int caretIndex, out int atIndex, out string query)
        {
            atIndex = -1;
            query = string.Empty;
            if (string.IsNullOrEmpty(text))
                return false;

            int index = Math.Clamp(caretIndex, 0, text.Length);
            int scan = index - 1;
            while (scan >= 0)
            {
                char c = text[scan];
                if (c == '@')
                {
                    // Only treat as mention start if @ is at start or preceded by whitespace/punctuation.
                    if (scan > 0)
                    {
                        char prev = text[scan - 1];
                        if (!char.IsWhiteSpace(prev) && prev != '(' && prev != '[' && prev != '{' && prev != ',' && prev != '\n')
                            return false;
                    }

                    atIndex = scan;
                    query = text.Substring(scan + 1, index - scan - 1);
                    // Abort if query contains whitespace (mention finished).
                    if (query.Any(char.IsWhiteSpace))
                        return false;
                    return true;
                }

                if (char.IsWhiteSpace(c) || c == '\n')
                    return false;

                if (!char.IsLetterOrDigit(c) && c != '_')
                    return false;

                scan--;
            }

            return false;
        }

        public static string ApplyMentionCompletion(string text, int atIndex, int caretIndex, string handle)
        {
            if (string.IsNullOrEmpty(handle) || atIndex < 0 || atIndex >= text.Length)
                return text;

            int end = Math.Clamp(caretIndex, atIndex, text.Length);
            string before = text[..atIndex];
            string after = end < text.Length ? text[end..] : string.Empty;
            string insertion = "@" + handle;
            // Keep a trailing space after completion for comfortable typing.
            if (after.Length == 0 || !char.IsWhiteSpace(after[0]))
                insertion += " ";
            return before + insertion + after;
        }

        public static IReadOnlyList<McpConnectorInfo> FilterConnectors(
            IEnumerable<McpConnectorInfo> connectors,
            string query,
            bool connectedOnly)
        {
            IEnumerable<McpConnectorInfo> source = connectors ?? Enumerable.Empty<McpConnectorInfo>();
            if (connectedOnly)
                source = source.Where(c => c.IsConnected);

            if (string.IsNullOrWhiteSpace(query))
                return source.OrderBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();

            string q = query.Trim();
            return source
                .Where(c =>
                    c.Handle.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                    || c.DisplayName.StartsWith(q, StringComparison.OrdinalIgnoreCase)
                    || c.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                    || c.Handle.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Handle.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
