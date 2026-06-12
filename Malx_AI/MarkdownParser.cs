using System;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public class MarkdownParser
    {
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
            result.HasCodeBlocks = markdown.Contains("```") || markdown.Contains("```");
            result.HasTables = markdown.Contains("|");

            result.Html = Regex.Replace(result.Html, @"\*\*(.*?)\*\*", "<bold>$1</bold>");
            result.Html = Regex.Replace(result.Html, @"__(.*?)__", "<bold>$1</bold>");

            result.Html = Regex.Replace(result.Html, @"\*(.*?)\*", "<italic>$1</italic>");
            result.Html = Regex.Replace(result.Html, @"_(.*?)_", "<italic>$1</italic>");

            result.Html = Regex.Replace(result.Html, @"`([^`]+)`", "<code>$1</code>");

            result.Html = Regex.Replace(result.Html, @"```(.*?)```", "<codeblock>$1</codeblock>", RegexOptions.Singleline);

            result.Html = Regex.Replace(result.Html, @"^### (.*?)$", "<h3>$1</h3>", RegexOptions.Multiline);
            result.Html = Regex.Replace(result.Html, @"^## (.*?)$", "<h2>$1</h2>", RegexOptions.Multiline);
            result.Html = Regex.Replace(result.Html, @"^# (.*?)$", "<h1>$1</h1>", RegexOptions.Multiline);

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
            output = Regex.Replace(output, @"\*\*(.+?)\*\*", "$1");
            output = Regex.Replace(output, @"__(.+?)__", "$1");
            return output;
        }
    }
}
