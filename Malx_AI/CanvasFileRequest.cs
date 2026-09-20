using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>What the user asked Axiom to produce as a file, if anything.</summary>
    public sealed record CanvasFileRequestInfo(string FileName, string Extension)
    {
        public bool HasExplicitName => !string.Equals(FileName, "untitled" + Extension, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recognises "make me a requirements.txt" style requests and builds the instruction that
    /// makes the model hand back a named file the Project Canvas can present.
    /// </summary>
    public static class CanvasFileRequest
    {
        // A filename written out in the request: notes.md, docker-compose.yml, App.config
        private static readonly Regex ExplicitFileNameRegex = new(
            @"(?<![\w.\-/\\])(?<name>[A-Za-z0-9][\w\-.]{0,48}\.(?<ext>[A-Za-z0-9]{1,8}))(?![\w.])",
            RegexOptions.Compiled);

        // "a .csv", "as markdown file", "in json format", "a yaml file"
        private static readonly Regex BareExtensionRegex = new(
            @"(?:\.|\bas\s+(?:an?\s+)?|\bin\s+|\ban?\s+)(?<ext>txt|text|md|markdown|json|csv|tsv|xml|yaml|yml|toml|ini|cfg|conf|log|sql|html|htm|css|js|ts|py|cs|java|go|rs|rb|php|sh|bash|ps1|bat|env|gitignore|dockerfile|makefile)\b(?:\s+(?:file|format|document))?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Dictionary<string, string> ExtensionAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = "txt",
            ["markdown"] = "md"
        };

        /// <summary>Words that look like filenames but are ordinary prose or code references.</summary>
        private static readonly HashSet<string> NotFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "e.g", "i.e", "etc", "vs", "js", "ts"
        };

        /// <summary>
        /// Finds the file the request is asking for. Returns false for a request that names an
        /// image or another binary format, with <paramref name="rejectedBinaryFormat"/> set so the
        /// caller can say why rather than silently producing nothing.
        /// </summary>
        public static bool TryDetect(string? userQuery, out CanvasFileRequestInfo? request, out string? rejectedBinaryFormat)
        {
            request = null;
            rejectedBinaryFormat = null;
            if (string.IsNullOrWhiteSpace(userQuery))
                return false;

            foreach (Match match in ExplicitFileNameRegex.Matches(userQuery))
            {
                string name = match.Groups["name"].Value;
                string extension = "." + match.Groups["ext"].Value.ToLowerInvariant();

                if (NotFileExtensions.Contains(match.Groups["ext"].Value))
                    continue;

                if (CanvasFileArtifact.IsBinaryFormat(extension))
                {
                    rejectedBinaryFormat = extension;
                    continue;
                }

                request = new CanvasFileRequestInfo(name, extension);
                return true;
            }

            Match bare = BareExtensionRegex.Match(userQuery);
            if (bare.Success)
            {
                string raw = bare.Groups["ext"].Value.ToLowerInvariant();
                string normalized = ExtensionAliases.TryGetValue(raw, out string? alias) ? alias : raw;
                string extension = "." + normalized;

                if (CanvasFileArtifact.IsBinaryFormat(extension))
                {
                    rejectedBinaryFormat = extension;
                    return false;
                }

                request = new CanvasFileRequestInfo("untitled" + extension, extension);
                return true;
            }

            // A request that only names an image format still needs to be reported.
            Match image = Regex.Match(
                userQuery,
                @"(?:\.|\bas\s+(?:an?\s+)?|\ban?\s+)(?<ext>png|jpe?g|gif|bmp|webp|ico|tiff?|mp3|mp4|zip|exe|pdf|docx|xlsx|pptx)\b",
                RegexOptions.IgnoreCase);
            if (image.Success)
                rejectedBinaryFormat = "." + image.Groups["ext"].Value.ToLowerInvariant();

            return false;
        }

        /// <summary>
        /// The prompt block that makes the model return one named file. Sized to the model: a
        /// small model gets the shape and nothing else, because extra prose is what makes it
        /// wander off the format.
        /// </summary>
        public static string BuildInstruction(CanvasFileRequestInfo request, bool compactModel)
        {
            ArgumentNullException.ThrowIfNull(request);

            var builder = new StringBuilder();
            builder.AppendLine("[PROJECT CANVAS FILE]");
            builder.AppendLine("The user asked for a file. Return it in exactly this shape, and nothing else after it:");
            builder.AppendLine();
            builder.AppendLine("FILE: " + request.FileName);
            builder.AppendLine("```" + request.Extension.TrimStart('.'));
            builder.AppendLine("<the complete file contents>");
            builder.AppendLine("```");
            builder.AppendLine();

            if (!compactModel)
            {
                builder.AppendLine("Rules:");
                builder.AppendLine("- The FILE: line carries the real filename, including its extension. Keep the name the user asked for when they gave one.");
                builder.AppendLine("- Put the entire file inside the one fenced block. Never split it, and never add commentary inside the fence.");
                builder.AppendLine("- Write real, complete content. No placeholders, no \"...\", no \"rest of file unchanged\".");
                builder.AppendLine("- At most one short sentence before the FILE: line.");
            }
            else
            {
                builder.AppendLine("Write the whole file inside the fence. No placeholders. Nothing after the closing fence.");
            }

            builder.Append("[/PROJECT CANVAS FILE]");
            return builder.ToString();
        }

        /// <summary>The message shown when the request names a format Axiom can only emit as bytes.</summary>
        public static string DescribeBinaryRefusal(string extension) =>
            $"Axiom's Project Canvas presents text files. It cannot generate a {extension} — that format is binary. "
            + "Ask for a text format (.md, .txt, .csv, .json, .html, source code, and so on) and it will appear in the canvas.";
    }
}
