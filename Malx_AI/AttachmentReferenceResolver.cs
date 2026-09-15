using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>
    /// One attached file, numbered both overall and within its own kind, so a prompt can state
    /// "attachment 4 (image 3 of 3)" and a user phrase like "the 3rd attached image" can be
    /// resolved to exactly one file.
    /// </summary>
    public sealed class AttachmentReferenceEntry
    {
        public int Number { get; init; }
        public string Name { get; init; } = string.Empty;
        public string KindLabel { get; init; } = string.Empty;
        public bool IsImage { get; init; }
        public int KindNumber { get; init; }
        public int KindTotal { get; init; }

        public string PositionLabel => IsImage
            ? $"image {KindNumber} of {KindTotal}"
            : $"file {KindNumber} of {KindTotal}";
    }

    /// <summary>A positional phrase found in the user's message and what it resolves to.</summary>
    public sealed class AttachmentReferenceMatch
    {
        public string Phrase { get; init; } = string.Empty;
        public AttachmentReferenceEntry? Target { get; init; }
        public string Explanation { get; init; } = string.Empty;
        public bool IsResolved => Target != null;
    }

    /// <summary>
    /// Resolves positional attachment references ("the first attached image", "attachment 3",
    /// "the last file") to concrete files, and renders the prompt blocks that carry that mapping
    /// to a model. The counting happens here rather than in the model, so the feature behaves the
    /// same on a sub-1B local model as on a large cloud one.
    /// </summary>
    public static class AttachmentReferenceResolver
    {
        private const int MaxReportedMatches = 4;

        private static readonly Dictionary<string, int> OrdinalWords = new(StringComparer.OrdinalIgnoreCase)
        {
            ["first"] = 1,
            ["second"] = 2,
            ["third"] = 3,
            ["fourth"] = 4,
            ["fifth"] = 5,
            ["sixth"] = 6,
            ["seventh"] = 7,
            ["eighth"] = 8,
            ["ninth"] = 9,
            ["tenth"] = 10,
            ["eleventh"] = 11,
            ["twelfth"] = 12
        };

        // "the 3rd attached image", "second picture", "the last file", "my final screenshot"
        private static readonly Regex OrdinalPhraseRegex = new(
            @"\b(?:the|my|that|this)?\s*(?<ord>first|second|third|fourth|fifth|sixth|seventh|eighth|ninth|tenth|eleventh|twelfth|last|final|latest|newest|most\s+recent|\d{1,2}(?:st|nd|rd|th))\s+(?:(?:attached|uploaded|shared|provided|given|new)\s+){0,2}(?<noun>images?|pictures?|photos?|screenshots?|imgs?|pics?|files?|documents?|docs?|pdfs?|spreadsheets?|sheets?|attachments?|ones?|items?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "image 2", "attachment #3", "file number 1"
        private static readonly Regex NumberedPhraseRegex = new(
            @"\b(?<noun>image|picture|photo|screenshot|file|document|doc|pdf|spreadsheet|attachment)\s*(?:number\s*)?#?\s*(?<num>\d{1,2})\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly string[] ImageNouns =
            ["image", "images", "picture", "pictures", "photo", "photos", "screenshot", "screenshots", "img", "imgs", "pic", "pics"];

        private static readonly string[] DocumentNouns =
            ["document", "documents", "doc", "docs", "pdf", "pdfs", "spreadsheet", "spreadsheets", "sheet", "sheets"];

        /// <summary>Numbers the attachments in attach order. Input order is authoritative.</summary>
        public static IReadOnlyList<AttachmentReferenceEntry> BuildIndex(
            IEnumerable<(string Name, bool IsImage, string KindLabel)>? attachments)
        {
            var source = (attachments ?? []).ToList();
            int imageTotal = source.Count(a => a.IsImage);
            int fileTotal = source.Count - imageTotal;

            var index = new List<AttachmentReferenceEntry>(source.Count);
            int imageSeen = 0;
            int fileSeen = 0;
            for (int i = 0; i < source.Count; i++)
            {
                (string name, bool isImage, string kindLabel) = source[i];
                index.Add(new AttachmentReferenceEntry
                {
                    Number = i + 1,
                    Name = string.IsNullOrWhiteSpace(name) ? $"attachment {i + 1}" : name.Trim(),
                    KindLabel = string.IsNullOrWhiteSpace(kindLabel) ? (isImage ? "image" : "file") : kindLabel,
                    IsImage = isImage,
                    KindNumber = isImage ? ++imageSeen : ++fileSeen,
                    KindTotal = isImage ? imageTotal : fileTotal
                });
            }

            return index;
        }

        /// <summary>Numbered manifest of every attachment, for the system prompt.</summary>
        public static string BuildManifest(IReadOnlyList<AttachmentReferenceEntry>? index)
        {
            if (index == null || index.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.Append("[ATTACHMENT INDEX] Files attached to this chat, in the order the user attached them:");
            foreach (AttachmentReferenceEntry entry in index.Take(20))
            {
                builder.Append("\n  ").Append(entry.Number).Append(". ").Append(entry.Name)
                    .Append(" — ").Append(entry.KindLabel)
                    .Append(" (").Append(entry.PositionLabel).Append(')');
            }

            if (index.Count > 20)
                builder.Append("\n  ... plus ").Append(index.Count - 20).Append(" more");

            builder.Append("\nThe user may point at one of these by position — \"the first attached image\", \"the 3rd file\", \"attachment 2\". This numbering is authoritative: count in this order and never renumber it.");
            return builder.ToString();
        }

        /// <summary>Order note for vision models, mapping each supplied image to its position.</summary>
        public static string BuildVisionOrderNote(IEnumerable<string>? imageNamesInSuppliedOrder)
        {
            var names = (imageNamesInSuppliedOrder ?? []).Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
            if (names.Count == 0)
                return string.Empty;

            var builder = new StringBuilder("[IMAGE ORDER] The images are supplied in attachment order: ");
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0)
                    builder.Append("; ");
                builder.Append("image ").Append(i + 1).Append(" = ").Append(names[i].Trim());
            }

            builder.Append('.');
            return builder.ToString();
        }

        public static bool HasPositionalReference(string? message, IReadOnlyList<AttachmentReferenceEntry>? index)
            => Resolve(message, index).Count > 0;

        /// <summary>Finds every positional phrase in the message and resolves each to a file.</summary>
        public static IReadOnlyList<AttachmentReferenceMatch> Resolve(
            string? message,
            IReadOnlyList<AttachmentReferenceEntry>? index)
        {
            if (string.IsNullOrWhiteSpace(message) || index == null || index.Count == 0)
                return [];

            var matches = new List<AttachmentReferenceMatch>();
            var seenPhrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in OrdinalPhraseRegex.Matches(message))
            {
                string ordinal = CollapseWhitespace(match.Groups["ord"].Value);
                string noun = match.Groups["noun"].Value;
                bool wantsLast = IsLastWord(ordinal);
                int position = wantsLast ? -1 : ParseOrdinal(ordinal);
                if (position == 0)
                    continue;

                AddMatch(matches, seenPhrases, CollapseWhitespace(match.Value), noun, position, index);
            }

            foreach (Match match in NumberedPhraseRegex.Matches(message))
            {
                string noun = match.Groups["noun"].Value;
                if (!int.TryParse(match.Groups["num"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int position)
                    || position <= 0)
                {
                    continue;
                }

                AddMatch(matches, seenPhrases, CollapseWhitespace(match.Value), noun, position, index);
            }

            return matches;
        }

        /// <summary>
        /// Prompt block stating what each positional phrase refers to, including an explicit
        /// correction when the user asks for an attachment that does not exist.
        /// </summary>
        public static string BuildResolutionBlock(string? message, IReadOnlyList<AttachmentReferenceEntry>? index)
        {
            IReadOnlyList<AttachmentReferenceMatch> matches = Resolve(message, index);
            if (matches.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.Append("[ATTACHMENT REFERENCE] The user pointed at specific attachments by position:");
            foreach (AttachmentReferenceMatch match in matches.Take(MaxReportedMatches))
                builder.Append("\n  - ").Append(match.Explanation);

            builder.Append("\nUse exactly those files. Do not substitute a different attachment.");
            return builder.ToString();
        }

        private static void AddMatch(
            List<AttachmentReferenceMatch> matches,
            HashSet<string> seenPhrases,
            string phrase,
            string noun,
            int position,
            IReadOnlyList<AttachmentReferenceEntry> index)
        {
            if (matches.Count >= MaxReportedMatches || !seenPhrases.Add(phrase))
                return;

            IReadOnlyList<AttachmentReferenceEntry> scope = SelectScope(noun, index, out string scopeLabel);
            if (scope.Count == 0)
            {
                matches.Add(new AttachmentReferenceMatch
                {
                    Phrase = phrase,
                    Target = null,
                    Explanation = $"\"{phrase}\" cannot be resolved: no {scopeLabel} is attached to this chat. Say so instead of guessing."
                });
                return;
            }

            AttachmentReferenceEntry? target = position == -1
                ? scope[^1]
                : position <= scope.Count ? scope[position - 1] : null;

            if (target == null)
            {
                string available = string.Join(", ", scope.Select(entry => entry.Name));
                string plural = scope.Count == 1 ? $"{scopeLabel} is" : $"{scopeLabel}s are";
                matches.Add(new AttachmentReferenceMatch
                {
                    Phrase = phrase,
                    Target = null,
                    Explanation = $"\"{phrase}\" does not exist: only {scope.Count} {plural} attached ({available}). Tell the user instead of guessing."
                });
                return;
            }

            matches.Add(new AttachmentReferenceMatch
            {
                Phrase = phrase,
                Target = target,
                Explanation = $"\"{phrase}\" = attachment {target.Number}: {target.Name} ({target.KindLabel}, {target.PositionLabel})."
            });
        }

        private static IReadOnlyList<AttachmentReferenceEntry> SelectScope(
            string noun,
            IReadOnlyList<AttachmentReferenceEntry> index,
            out string scopeLabel)
        {
            if (ImageNouns.Contains(noun, StringComparer.OrdinalIgnoreCase))
            {
                scopeLabel = "image";
                return index.Where(entry => entry.IsImage).ToList();
            }

            if (DocumentNouns.Contains(noun, StringComparer.OrdinalIgnoreCase))
            {
                // "the second document" means the second non-image file when any exist; with only
                // images attached the user can only mean the images themselves.
                var documents = index.Where(entry => !entry.IsImage).ToList();
                if (documents.Count > 0)
                {
                    scopeLabel = "file";
                    return documents;
                }
            }

            scopeLabel = "attachment";
            return index;
        }

        private static bool IsLastWord(string ordinal)
            => ordinal.Equals("last", StringComparison.OrdinalIgnoreCase)
                || ordinal.Equals("final", StringComparison.OrdinalIgnoreCase)
                || ordinal.Equals("latest", StringComparison.OrdinalIgnoreCase)
                || ordinal.Equals("newest", StringComparison.OrdinalIgnoreCase)
                || ordinal.Equals("most recent", StringComparison.OrdinalIgnoreCase);

        private static int ParseOrdinal(string ordinal)
        {
            if (OrdinalWords.TryGetValue(ordinal, out int word))
                return word;

            string digits = new(ordinal.TakeWhile(char.IsDigit).ToArray());
            return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0
                ? value
                : 0;
        }

        private static string CollapseWhitespace(string value)
            => Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
    }
}
