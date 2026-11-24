using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>
    /// "Attach selection as context": text picked from an AI reply that rides along with the
    /// next message, shown as a one-line quote above the input box.
    /// </summary>
    internal static class ReplyContextAttachment
    {
        /// <summary>Longest quote sent with a message; longer selections are cut with a note.</summary>
        public const int MaxQuoteChars = 6000;

        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

        /// <summary>The one-line preview shown above the input: quoted, collapsed, trimmed.</summary>
        public static string Preview(string text, int maxChars = 120)
        {
            string flat = Whitespace.Replace(text ?? string.Empty, " ").Trim();
            if (flat.Length > maxChars)
                flat = flat[..Math.Max(1, maxChars - 4)].TrimEnd() + " ...";
            return "“" + flat + "”";
        }

        /// <summary>
        /// The message the model receives: the quote as a Markdown block quote, then the user's
        /// own words. A block quote reads as "the part of your reply I mean" to every model.
        /// </summary>
        public static string Compose(string quote, string message)
        {
            string trimmedQuote = (quote ?? string.Empty).Trim();
            if (trimmedQuote.Length == 0)
                return message ?? string.Empty;
            bool cut = trimmedQuote.Length > MaxQuoteChars;
            if (cut)
                trimmedQuote = trimmedQuote[..MaxQuoteChars].TrimEnd();

            var builder = new StringBuilder();
            foreach (string line in trimmedQuote.Replace("\r\n", "\n").Split('\n'))
                builder.Append("> ").AppendLine(line.TrimEnd());
            if (cut)
                builder.AppendLine("> (selection shortened)");
            builder.AppendLine();
            builder.Append((message ?? string.Empty).Trim());
            return builder.ToString().TrimEnd();
        }

        /// <summary>True when there is something worth attaching.</summary>
        public static bool IsUsable(string? text) => !string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 1;
    }
}
