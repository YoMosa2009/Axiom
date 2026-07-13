using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public class MarkdownParser
    {
        // ToDisplayText runs on every workplace card render (including live streaming updates),
        // so its regexes are compiled once instead of re-parsed per call.
        private static readonly Regex BoldAsteriskRegex = new(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        private static readonly Regex BoldUnderscoreRegex = new(@"__(.+?)__", RegexOptions.Compiled);

        public class ParsedMarkdown
        {
            public string Html { get; set; }
            public bool HasCodeBlocks { get; set; }
            public bool HasTables { get; set; }
        }

        public static ParsedMarkdown Parse(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return new ParsedMarkdown { Html = "", HasCodeBlocks = false, HasTables = false };

            var result = new ParsedMarkdown { Html = markdown };
            result.HasCodeBlocks = markdown.Contains("```");
            result.HasTables = markdown.Contains("|");

            var fencedBlocks = new List<string>();
            result.Html = Regex.Replace(
                result.Html,
                @"```(?:[^\r\n`]*)?(?:\r?\n)?(?<code>[\s\S]*?)```",
                match =>
                {
                    int index = fencedBlocks.Count;
                    fencedBlocks.Add(match.Groups["code"].Value.TrimEnd('\r', '\n'));
                    return $"\uE000{index}\uE001";
                });

            result.Html = Regex.Replace(result.Html, @"\*\*(.*?)\*\*", "<bold>$1</bold>");
            result.Html = Regex.Replace(result.Html, @"__(.*?)__", "<bold>$1</bold>");

            result.Html = Regex.Replace(result.Html, @"\*(.*?)\*", "<italic>$1</italic>");
            result.Html = Regex.Replace(result.Html, @"_(.*?)_", "<italic>$1</italic>");

            result.Html = Regex.Replace(result.Html, @"`([^`]+)`", "<code>$1</code>");

            result.Html = Regex.Replace(result.Html, @"^### (.*?)$", "<h3>$1</h3>", RegexOptions.Multiline);
            result.Html = Regex.Replace(result.Html, @"^## (.*?)$", "<h2>$1</h2>", RegexOptions.Multiline);
            result.Html = Regex.Replace(result.Html, @"^# (.*?)$", "<h1>$1</h1>", RegexOptions.Multiline);

            for (int index = 0; index < fencedBlocks.Count; index++)
                result.Html = result.Html.Replace($"\uE000{index}\uE001", $"<codeblock>{fencedBlocks[index]}</codeblock>", StringComparison.Ordinal);

            return result;
        }

        public static bool IsCodeBlock(string text)
        {
            return text.StartsWith("```") && text.EndsWith("```");
        }

        public static string ExtractCodeFromBlock(string codeBlock)
        {
            if (!IsCodeBlock(codeBlock))
                return codeBlock;

            var lines = codeBlock.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            if (lines.Length < 2)
                return codeBlock;

            return string.Join("\n", lines, 1, lines.Length - 2);
        }

        public static string ToDisplayText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            string output = text;
            output = BoldAsteriskRegex.Replace(output, "$1");
            output = BoldUnderscoreRegex.Replace(output, "$1");
            return output;
        }
    }
}
