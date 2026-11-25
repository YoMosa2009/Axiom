using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Finds where an edit_file old_string belongs, forgiving the mistakes small models make
    /// (wrong line endings, trailing spaces, different indentation, curly quotes and long dashes)
    /// while staying fail-closed: a match must be unique at the first tier that finds one, and a
    /// miss returns the closest region so the next attempt can copy the real text.
    /// OpenCode and Codex use the same tiered idea; a miss otherwise costs a whole agent step.
    /// Dependency-free so the tests can link it.
    /// </summary>
    public static class AgentEditMatcher
    {
        public sealed record Result(bool Found, int Start, int Length, string Replacement, string Tier, string? Error);

        public static Result Find(string content, string oldText, string newText)
        {
            if (string.IsNullOrEmpty(oldText))
                return Fail("old_string was empty. Use write_file to replace a whole file.");

            string newline = content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

            int exact = Count(content, oldText);
            if (exact == 1)
                return new Result(true, content.IndexOf(oldText, StringComparison.Ordinal), oldText.Length, newText, "exact", null);
            if (exact > 1)
                return Fail($"old_string appears {exact} times. Include more surrounding lines so it is unique.");

            // Same text, other line endings.
            string oldConverted = ToNewlines(oldText, newline);
            int converted = Count(content, oldConverted);
            if (converted == 1)
                return new Result(true, content.IndexOf(oldConverted, StringComparison.Ordinal), oldConverted.Length,
                    ToNewlines(newText, newline), "line endings", null);
            if (converted > 1)
                return Fail($"old_string appears {converted} times. Include more surrounding lines so it is unique.");

            List<Line> fileLines = SplitLines(content);
            string[] oldLines = TrimBlankEdges(oldText.Replace("\r\n", "\n").Split('\n'));
            if (oldLines.Length == 0)
                return Fail("old_string held only blank lines. Include the text to replace.");

            var tiers = new (string Name, Func<string, string> Key)[]
            {
                ("ignoring trailing whitespace", s => s.TrimEnd()),
                ("ignoring indentation", s => s.Trim()),
                ("ignoring indentation and typographic quotes/dashes", s => NormalizeTypography(s.Trim())),
            };

            foreach (var (name, key) in tiers)
            {
                string[] wanted = oldLines.Select(key).ToArray();
                var hits = new List<int>();
                for (int i = 0; i + wanted.Length <= fileLines.Count; i++)
                {
                    bool all = true;
                    for (int j = 0; j < wanted.Length && all; j++)
                        all = key(fileLines[i + j].Text) == wanted[j];
                    if (all)
                        hits.Add(i);
                }

                if (hits.Count > 1)
                    return Fail($"old_string matches {hits.Count} places ({name}). Include more surrounding lines so it is unique.");
                if (hits.Count == 1)
                {
                    Line first = fileLines[hits[0]];
                    Line last = fileLines[hits[0] + wanted.Length - 1];
                    int start = first.Start;
                    int end = last.Start + last.Text.Length;
                    string replacement = Reindent(newText, oldLines, first.Text, newline);
                    return new Result(true, start, end - start, replacement, name, null);
                }
            }

            return Fail("old_string was not found in the file. " + ClosestRegionHint(fileLines, oldLines));
        }

        private static Result Fail(string error) => new(false, -1, 0, "", "", error);

        private readonly record struct Line(int Start, string Text);

        private static List<Line> SplitLines(string content)
        {
            var lines = new List<Line>();
            int start = 0;
            for (int i = 0; i <= content.Length; i++)
            {
                if (i == content.Length || content[i] == '\n')
                {
                    int end = i;
                    if (end > start && content[end - 1] == '\r')
                        end--;
                    lines.Add(new Line(start, content[start..end]));
                    start = i + 1;
                }
            }
            return lines;
        }

        private static string[] TrimBlankEdges(string[] lines)
        {
            int first = 0, last = lines.Length - 1;
            while (first <= last && string.IsNullOrWhiteSpace(lines[first])) first++;
            while (last >= first && string.IsNullOrWhiteSpace(lines[last])) last--;
            return first > last ? [] : lines[first..(last + 1)];
        }

        /// <summary>
        /// When the model's old_string had different indentation from the file, its new_string
        /// almost always shares that wrong indentation. Shift it by the same amount.
        /// </summary>
        private static string Reindent(string newText, string[] oldLines, string fileFirstLine, string newline)
        {
            string[] newLines = TrimBlankEdges(newText.Replace("\r\n", "\n").Split('\n'));
            if (newLines.Length == 0)
                return "";
            string oldIndent = LeadingWhitespace(oldLines[0]);
            string fileIndent = LeadingWhitespace(fileFirstLine);
            if (oldIndent != fileIndent)
            {
                for (int i = 0; i < newLines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(newLines[i]))
                        continue;
                    if (newLines[i].StartsWith(oldIndent, StringComparison.Ordinal))
                        newLines[i] = fileIndent + newLines[i][oldIndent.Length..];
                }
            }
            return string.Join(newline, newLines);
        }

        private static string LeadingWhitespace(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            return line[..i];
        }

        internal static string NormalizeTypography(string text)
        {
            var builder = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                builder.Append(c switch
                {
                    '‘' or '’' or '‚' or '′' => '\'',
                    '“' or '”' or '„' or '″' => '"',
                    '‐' or '‑' or '‒' or '–' or '—' or '−' => '-',
                    ' ' or ' ' or ' ' => ' ',
                    _ => c,
                });
            }
            return builder.ToString();
        }

        private static string ClosestRegionHint(List<Line> fileLines, string[] oldLines)
        {
            if (fileLines.Count == 0)
                return "The file is empty.";
            string[] wanted = oldLines.Select(l => NormalizeTypography(l.Trim())).ToArray();
            int window = Math.Min(wanted.Length, fileLines.Count);
            int bestStart = -1, bestScore = 0;
            for (int i = 0; i + window <= fileLines.Count; i++)
            {
                int score = 0;
                for (int j = 0; j < window; j++)
                    score += Similarity(NormalizeTypography(fileLines[i + j].Text.Trim()), wanted[j]);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestStart = i;
                }
            }
            if (bestStart < 0)
                return "Read the file and copy the exact text.";

            int shown = Math.Min(window, 12);
            var builder = new StringBuilder();
            builder.Append($"The closest text is at lines {bestStart + 1}-{bestStart + shown}; copy it exactly:\n");
            for (int j = 0; j < shown; j++)
            {
                string text = fileLines[bestStart + j].Text;
                builder.Append(text.Length > 200 ? text[..200] + "…" : text).Append('\n');
            }
            return builder.ToString().TrimEnd('\n');
        }

        /// <summary>Shared-prefix plus shared-suffix length: cheap, and good at spotting a line with one changed token.</summary>
        private static int Similarity(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0)
                return 0;
            if (a == b)
                return a.Length * 2;
            int prefix = 0;
            while (prefix < a.Length && prefix < b.Length && a[prefix] == b[prefix]) prefix++;
            int suffix = 0;
            while (suffix < a.Length - prefix && suffix < b.Length - prefix && a[a.Length - 1 - suffix] == b[b.Length - 1 - suffix]) suffix++;
            return prefix + suffix;
        }

        private static string ToNewlines(string text, string newline) =>
            text.Replace("\r\n", "\n").Replace("\n", newline);

        private static int Count(string text, string value)
        {
            int count = 0, index = 0;
            while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }
}
