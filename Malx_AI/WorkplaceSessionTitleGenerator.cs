using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>
    /// Automatically derives a clean, concise session title from a user's first Workplace prompt
    /// so sessions show descriptive names instead of generic numbers ("Workplace 1", "Workplace 2").
    /// </summary>
    public static class WorkplaceSessionTitleGenerator
    {
        private static readonly Regex MentionRegex = new(@"@[A-Za-z0-9_.-]+", RegexOptions.Compiled);
        private static readonly Regex FenceRegex = new(@"```[\s\S]*?```", RegexOptions.Compiled);
        private static readonly Regex PathRegex = new(@"[A-Za-z]:[\\/][^\s""']+", RegexOptions.Compiled);
        private static readonly Regex LeadInRegex = new(
            @"^\s*(?:please\s+|could\s+you\s+|can\s+you\s+|would\s+you\s+|i\s+want\s+you\s+to\s+|i\s+need\s+you\s+to\s+|help\s+me\s+|let'?s\s+)+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DefaultNameRegex = new(
            @"^(?:Workplace\s+\d+|New\s+(?:session|workplace|chat)|Session\s+\d+)$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static bool IsPlaceholderName(string? name)
            => string.IsNullOrWhiteSpace(name) || DefaultNameRegex.IsMatch(name.Trim());

        public static string GenerateTitle(string? prompt, string? projectFolder = null, int maxChars = 42)
        {
            string text = (prompt ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                if (!string.IsNullOrWhiteSpace(projectFolder))
                    return ShortProjectLabel(projectFolder);
                return "New session";
            }

            text = FenceRegex.Replace(text, " ");
            text = MentionRegex.Replace(text, " ");
            text = PathRegex.Replace(text, match =>
            {
                string file = Path.GetFileName(match.Value.TrimEnd('\\', '/'));
                return string.IsNullOrWhiteSpace(file) ? string.Empty : file;
            });
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            text = Regex.Replace(text, @"\s+", " ").Trim();
            text = LeadInRegex.Replace(text, string.Empty).Trim();
            text = text.Trim('"', '\'', '.', ',', ':', ';', '!', '?');

            if (text.Length == 0)
            {
                return !string.IsNullOrWhiteSpace(projectFolder)
                    ? ShortProjectLabel(projectFolder)
                    : "New session";
            }

            string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var chosen = new List<string>();
            int length = 0;
            foreach (string word in words.Take(7))
            {
                int nextLen = length + (chosen.Count > 0 ? 1 : 0) + word.Length;
                if (chosen.Count >= 3 && nextLen > maxChars)
                    break;
                chosen.Add(word);
                length = nextLen;
            }

            string title = string.Join(" ", chosen);
            if (title.Length > maxChars)
                title = title[..(maxChars - 1)].TrimEnd() + "…";

            return title.Length == 0
                ? "New session"
                : char.ToUpperInvariant(title[0]) + title[1..];
        }

        public static string ShortProjectLabel(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "This computer";

            string cleaned = path.Trim().TrimEnd('\\', '/');
            if (cleaned.Length == 2 && cleaned[1] == ':')
                return cleaned.ToLowerInvariant(); // e.g. "g:", "h:"

            string name = Path.GetFileName(cleaned);
            return string.IsNullOrWhiteSpace(name) ? cleaned : name;
        }
    }
}
