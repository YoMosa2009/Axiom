using System;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    // This value is consumed by the chat renderers only. Keep it out of generation, history,
    // tool authorization and artifact handling. The prose stored in Persona is never a prompt.
    internal readonly record struct PersonaReplyPresentation(bool HideDecorativeEmoji, bool UseHyphenBullets, bool PlainHeadings)
    {
        private static readonly Regex Fence = new(@"^ {0,3}(?<marker>`{3,}|~{3,})(?<tail>.*)$", RegexOptions.Compiled);
        private static readonly Regex DecorativeEmoji = new(
            @"^(?:[\u2600-\u27BF]|[\uD83C-\uD83E][\uDC00-\uDFFF])[\uFE0E\uFE0F]?[ \t]+(?=\S)", RegexOptions.Compiled);
        private static readonly Regex Bullet = new(@"^(?<indent> {0,3})[+*](?=[ \t])", RegexOptions.Compiled);
        private static readonly Regex Heading = new(@"^ {0,3}#{1,6}[ \t]+(?=\S)", RegexOptions.Compiled);

        public static PersonaReplyPresentation FromNotes(string notes)
        {
            const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
            return new(
                Regex.IsMatch(notes, @"\b(?:no emojis?|avoid emojis?|without emojis?|(?:don['’]t|do not) use emojis?)\b", options),
                Regex.IsMatch(notes, @"\b(?:hyphen|dash)[ -]bullets\b", options),
                Regex.IsMatch(notes, @"\b(?:plain(?:[ -]text)? headings|(?:no|avoid) markdown headings)\b", options));
        }

        public string Apply(string markdown)
        {
            if (this == default || string.IsNullOrEmpty(markdown))
                return markdown;

            // Keep the line endings and content of fenced or indented code byte-for-byte.
            string[] parts = Regex.Split(markdown, "(\r\n|\n|\r)");
            char fenceChar = '\0';
            int fenceLength = 0;
            for (int i = 0; i < parts.Length; i += 2)
            {
                string line = parts[i];
                Match fence = Fence.Match(line);
                if (fence.Success)
                {
                    string marker = fence.Groups["marker"].Value;
                    if (fenceChar == '\0')
                    {
                        fenceChar = marker[0];
                        fenceLength = marker.Length;
                        continue;
                    }
                    if (marker[0] == fenceChar && marker.Length >= fenceLength
                        && string.IsNullOrWhiteSpace(fence.Groups["tail"].Value))
                        fenceChar = '\0';
                    continue;
                }
                if (fenceChar != '\0' || line.StartsWith("    ", StringComparison.Ordinal) || line.StartsWith('\t'))
                    continue;

                if (UseHyphenBullets)
                    line = Bullet.Replace(line, "${indent}-");
                if (PlainHeadings)
                    line = Heading.Replace(line, "");
                if (HideDecorativeEmoji)
                    line = DecorativeEmoji.Replace(line, "");
                parts[i] = line;
            }
            return string.Concat(parts);
        }
    }
}
