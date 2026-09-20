using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace Malx_AI
{
    /// <summary>A named file the model produced for the Project Canvas.</summary>
    public sealed record CanvasFile(string FileName, string Extension, string Content);

    /// <summary>
    /// Detects "here is a file" output and turns it into a Project Canvas artifact.
    /// </summary>
    /// <remarks>
    /// The existing document detector only fires on Markdown structure (three headings, or a
    /// table), so asking for a requirements.txt or a config.json produced nothing renderable at
    /// all. This recognises an explicitly named file instead, which is what the user asked for
    /// when they typed a filename, and keeps the real name and extension attached so the canvas
    /// header and the Save dialog both show the right thing.
    /// </remarks>
    public static class CanvasFileArtifact
    {
        /// <summary>Formats Axiom cannot author as text. Asking for one is a real error, not a silent miss.</summary>
        private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic", ".avif",
            ".mp3", ".wav", ".flac", ".ogg", ".mp4", ".mov", ".avi", ".mkv", ".webm",
            ".zip", ".rar", ".7z", ".tar", ".gz", ".exe", ".dll", ".msi", ".bin",
            ".pdf", ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".ttf", ".otf", ".woff", ".woff2"
        };

        // "FILE: name.ext" on its own line — the form every model size can produce.
        private static readonly Regex FileHeaderRegex = new(
            // Leading and trailing markdown emphasis is stripped on both sides of the colon:
            // models write "**FILE:** name.ext" about as often as the plain form.
            @"(?im)^[>\s*#]*(?:FILE|FILENAME|PATH)\s*\**\s*[:=]\s*\**\s*(?<name>[^\r\n`""<>|*?]+?)\s*\**\s*$",
            RegexOptions.Compiled);

        // A fenced block, with whatever the model put on the info line.
        private static readonly Regex FenceRegex = new(
            @"```(?<info>[^\r\n]*)\r?\n(?<body>[\s\S]*?)```",
            RegexOptions.Compiled);

        // name=x.json / title="x.json" / filename: x.json inside the info string.
        private static readonly Regex InfoNameRegex = new(
            @"(?:name|title|file|filename)\s*[:=]\s*[""']?(?<name>[^\s""']+\.[A-Za-z0-9]{1,8})[""']?",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // A bare filename sitting in the info string: ```config.json  or  ```json config.json
        private static readonly Regex InfoBareNameRegex = new(
            @"(?<![\w.])(?<name>[\w\-.]+\.[A-Za-z0-9]{1,8})(?![\w.])",
            RegexOptions.Compiled);

        private static readonly MarkdownPipeline MarkdownPipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        /// <summary>Maps a fence language to the extension to use when no filename was given.</summary>
        private static readonly Dictionary<string, string> LanguageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["markdown"] = ".md", ["md"] = ".md", ["text"] = ".txt", ["txt"] = ".txt", ["plaintext"] = ".txt",
            ["json"] = ".json", ["yaml"] = ".yaml", ["yml"] = ".yml", ["xml"] = ".xml", ["csv"] = ".csv",
            ["python"] = ".py", ["py"] = ".py", ["javascript"] = ".js", ["js"] = ".js", ["typescript"] = ".ts",
            ["ts"] = ".ts", ["csharp"] = ".cs", ["cs"] = ".cs", ["java"] = ".java", ["go"] = ".go",
            ["rust"] = ".rs", ["rs"] = ".rs", ["sql"] = ".sql", ["sh"] = ".sh", ["bash"] = ".sh",
            ["powershell"] = ".ps1", ["ps1"] = ".ps1", ["ini"] = ".ini", ["toml"] = ".toml",
            ["css"] = ".css", ["html"] = ".html", ["svg"] = ".svg"
        };

        /// <summary>True when the extension names a format Axiom can only produce as bytes.</summary>
        public static bool IsBinaryFormat(string extensionOrName)
        {
            string value = (extensionOrName ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;
            int dot = value.LastIndexOf('.');
            string extension = dot >= 0 ? value[dot..] : "." + value;
            return BinaryExtensions.Contains(extension);
        }

        /// <summary>
        /// Reads the named file out of a model response.
        /// </summary>
        /// <param name="requestedExtensionHint">
        /// The extension the user asked for, used to name an unnamed block so a bare fenced answer
        /// to "make me a requirements.txt" still arrives as a file.
        /// </param>
        public static bool TryParse(string? modelOutput, string? requestedExtensionHint, out CanvasFile? file)
        {
            file = null;
            if (string.IsNullOrWhiteSpace(modelOutput))
                return false;

            MatchCollection fences = FenceRegex.Matches(modelOutput);
            if (fences.Count == 0)
                return false;

            foreach (Match fence in fences)
            {
                string info = fence.Groups["info"].Value.Trim();
                string body = fence.Groups["body"].Value;
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                string? name = NameFromHeaderAbove(modelOutput, fence.Index)
                               ?? NameFromInfo(info)
                               ?? NameFromHint(info, requestedExtensionHint);

                if (name == null)
                    continue;

                string extension = ExtensionOf(name);
                if (BinaryExtensions.Contains(extension))
                    continue;

                file = new CanvasFile(name, extension, body.TrimEnd('\r', '\n'));
                return true;
            }

            return false;
        }

        /// <summary>Builds the canvas artifact for a parsed file.</summary>
        public static ArtifactRenderInfo ToArtifact(CanvasFile file)
        {
            ArgumentNullException.ThrowIfNull(file);

            // An .html or .svg file is more useful shown than quoted, so those keep the existing
            // visual render and only borrow the real filename.
            string render = file.Extension switch
            {
                ".html" or ".htm" => ArtifactRenderService.BuildOfflineSafeHtmlDocument(file.Content),
                ".svg" => ArtifactRenderService.BuildSvgPreviewDocument(file.Content),
                ".md" or ".markdown" => BuildMarkdownFileDocument(file),
                ".csv" or ".tsv" => BuildTableFileDocument(file),
                _ => BuildPlainFileDocument(file)
            };

            return new ArtifactRenderInfo
            {
                Kind = ArtifactKind.Document,
                RawSource = file.Content,
                RenderSource = render,
                SaveContent = file.Content,
                DisplayTitle = file.FileName,
                SuggestedFileExtension = file.Extension
            };
        }

        private static string? NameFromHeaderAbove(string text, int fenceIndex)
        {
            // Only a header in the few lines immediately above the fence belongs to it.
            int windowStart = Math.Max(0, fenceIndex - 300);
            string window = text[windowStart..fenceIndex];
            MatchCollection matches = FileHeaderRegex.Matches(window);
            if (matches.Count == 0)
                return null;

            string candidate = matches[^1].Groups["name"].Value.Trim().Trim('`', '"', '\'');
            return LooksLikeFileName(candidate) ? LastSegment(candidate) : null;
        }

        private static string? NameFromInfo(string info)
        {
            if (string.IsNullOrWhiteSpace(info))
                return null;

            Match keyed = InfoNameRegex.Match(info);
            if (keyed.Success)
                return LastSegment(keyed.Groups["name"].Value);

            Match bare = InfoBareNameRegex.Match(info);
            return bare.Success && LooksLikeFileName(bare.Groups["name"].Value)
                ? LastSegment(bare.Groups["name"].Value)
                : null;
        }

        private static string? NameFromHint(string info, string? requestedExtensionHint)
        {
            string language = info.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;

            string? extension = null;
            if (!string.IsNullOrWhiteSpace(requestedExtensionHint))
            {
                string hint = requestedExtensionHint.Trim();
                extension = hint.StartsWith('.') ? hint : "." + hint;
            }
            else if (LanguageExtensions.TryGetValue(language, out string? mapped))
            {
                extension = mapped;
            }

            if (extension == null || BinaryExtensions.Contains(extension))
                return null;

            return "untitled" + extension;
        }

        private static bool LooksLikeFileName(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > 160)
                return false;
            string name = LastSegment(candidate);
            int dot = name.LastIndexOf('.');
            return dot > 0
                && dot < name.Length - 1
                && name[(dot + 1)..].All(char.IsLetterOrDigit)
                && name.Length - dot <= 9;
        }

        private static string LastSegment(string path)
        {
            string trimmed = path.Replace('\\', '/').TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        }

        private static string ExtensionOf(string fileName)
        {
            int dot = fileName.LastIndexOf('.');
            return dot >= 0 ? fileName[dot..].ToLowerInvariant() : string.Empty;
        }

        private static string BuildMarkdownFileDocument(CanvasFile file) =>
            Wrap(file, "<article class='md'>" + Markdown.ToHtml(file.Content, MarkdownPipeline) + "</article>");

        private static string BuildTableFileDocument(CanvasFile file)
        {
            char delimiter = file.Extension == ".tsv" ? '\t' : ',';
            string[] lines = file.Content.Split('\n')
                .Select(line => line.TrimEnd('\r'))
                .Where(line => line.Length > 0)
                .Take(500)
                .ToArray();

            if (lines.Length == 0)
                return BuildPlainFileDocument(file);

            var table = new StringBuilder("<table>");
            for (int i = 0; i < lines.Length; i++)
            {
                string cell = i == 0 ? "th" : "td";
                table.Append("<tr>");
                foreach (string value in SplitDelimited(lines[i], delimiter))
                    table.Append('<').Append(cell).Append('>')
                         .Append(WebUtility.HtmlEncode(value))
                         .Append("</").Append(cell).Append('>');
                table.Append("</tr>");
            }
            table.Append("</table>");
            return Wrap(file, table.ToString());
        }

        /// <summary>Splits one delimited row, honouring "quoted, values".</summary>
        internal static IReadOnlyList<string> SplitDelimited(string line, char delimiter)
        {
            var values = new List<string>();
            var current = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == delimiter && !inQuotes)
                {
                    values.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            values.Add(current.ToString());
            return values;
        }

        private static string BuildPlainFileDocument(CanvasFile file) =>
            Wrap(file, "<pre><code>" + WebUtility.HtmlEncode(file.Content) + "</code></pre>");

        private static string Wrap(CanvasFile file, string bodyHtml)
        {
            string lineCount = (file.Content.Count(c => c == '\n') + 1).ToString();
            return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
                 + "<meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"
                 + "html,body{margin:0;background:transparent;}"
                 + "body{color:#D1D3DF;font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;font-size:14px;line-height:1.7;}"
                 + ".hdr{position:sticky;top:0;display:flex;align-items:center;gap:8px;padding:10px 16px;"
                 + "background:#1B1917;border-bottom:1px solid #2D3139;}"
                 + ".hdr .nm{font-weight:600;color:#EDE8E3;font-family:'Cascadia Mono',Consolas,monospace;font-size:12px;}"
                 + ".hdr .meta{margin-left:auto;color:#8A8279;font-size:10px;}"
                 + ".body{padding:16px 18px;}"
                 + "pre{margin:0;background:#171615;border:1px solid #2D3139;border-radius:8px;padding:14px;overflow:auto;}"
                 + "code{font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px;line-height:1.65;color:#D5CFC6;}"
                 + ".md h1,.md h2,.md h3{color:#D5DAD3;line-height:1.3;margin:1.2em 0 .5em;}"
                 + ".md p,.md ul,.md ol,.md table,.md pre{margin:0 0 1em;}.md ul,.md ol{padding-left:1.5em;}"
                 + ".md code{background:#211F1D;border:1px solid #2D3139;border-radius:4px;padding:1px 4px;}"
                 + ".md pre code{background:transparent;border:none;padding:0;}"
                 + "table{border-collapse:collapse;width:100%;font-size:12.5px;}"
                 + "th,td{border:1px solid #2D3139;padding:6px 9px;text-align:left;}th{background:#211F1D;color:#EDE8E3;}"
                 + "a{color:#B8924A;text-decoration:none;}"
                 + "</style></head><body>"
                 + "<div class='hdr'><span class='nm'>" + WebUtility.HtmlEncode(file.FileName) + "</span>"
                 + "<span class='meta'>" + lineCount + " lines</span></div>"
                 + "<div class='body'>" + bodyHtml + "</div></body></html>";
        }
    }
}
