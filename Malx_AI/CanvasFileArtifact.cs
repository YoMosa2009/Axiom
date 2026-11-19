using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Json;
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
            IReadOnlyList<CanvasFile> all = ParseAll(modelOutput, requestedExtensionHint);
            file = all.Count > 0 ? all[0] : null;
            return file != null;
        }

        /// <summary>Extracts every explicitly named (or requested-format) text file from a response.</summary>
        public static IReadOnlyList<CanvasFile> ParseAll(string? modelOutput, string? requestedExtensionHint = null)
        {
            if (string.IsNullOrWhiteSpace(modelOutput))
                return Array.Empty<CanvasFile>();

            MatchCollection fences = FenceRegex.Matches(modelOutput);
            if (fences.Count == 0)
                return Array.Empty<CanvasFile>();

            var result = new List<CanvasFile>();
            var seenNames = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (Match fence in fences)
            {
                string info = fence.Groups["info"].Value.Trim();
                string body = fence.Groups["body"].Value;
                if (string.IsNullOrWhiteSpace(body))
                    continue;

                string? name = NameFromHeaderAbove(modelOutput, fence.Index)
                               ?? NameFromInfo(info)
                               ?? NameFromHint(info, result.Count == 0 ? requestedExtensionHint : null);

                if (name == null)
                    continue;

                string extension = ExtensionOf(name);
                if (BinaryExtensions.Contains(extension))
                    continue;

                var parsed = new CanvasFile(name, extension, body.TrimEnd('\r', '\n'));
                if (seenNames.TryGetValue(name, out int existingIndex))
                    result[existingIndex] = parsed;
                else
                {
                    seenNames[name] = result.Count;
                    result.Add(parsed);
                }
            }

            return result;
        }

        /// <summary>Loads a text file from disk for formatted Project Canvas inspection.</summary>
        public static bool TryFromDiskFile(string filePath, string? displayName, out CanvasFile? file, out string? error)
        {
            file = null;
            error = null;
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                error = "The file could not be found on disk.";
                return false;
            }

            string name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(filePath) : Path.GetFileName(displayName);
            string extension = ExtensionOf(name);
            if (BinaryExtensions.Contains(extension))
            {
                error = $"Binary format '{extension}' cannot be previewed as text in Project Canvas.";
                return false;
            }

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > 1_048_576)
                {
                    error = $"'{name}' is too large ({stream.Length / 1024} KB) for formatted canvas preview.";
                    return false;
                }

                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string content = reader.ReadToEnd();
                if (content.Contains('\0'))
                {
                    error = $"'{name}' appears to be a binary file and cannot be rendered as text.";
                    return false;
                }

                file = new CanvasFile(name, extension, content);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                error = $"Could not read '{name}': {ex.Message}";
                return false;
            }
        }

        /// <summary>Returns a concise human-readable format badge for a filename or extension.</summary>
        public static string GetFileTypeBadge(string? extensionOrName)
        {
            string ext = (extensionOrName ?? string.Empty).Trim().ToLowerInvariant();
            if (ext.Contains('.'))
                ext = ExtensionOf(ext);
            else if (ext.Length > 0 && ext[0] != '.')
                ext = "." + ext;

            return ext switch
            {
                ".md" or ".markdown" => "Markdown",
                ".json" or ".jsonc" => "JSON",
                ".xml" or ".xaml" or ".csproj" or ".config" => "XML",
                ".yaml" or ".yml" => "YAML",
                ".toml" => "TOML",
                ".ini" or ".cfg" or ".env" => "Config",
                ".csv" => "CSV",
                ".tsv" => "TSV",
                ".html" or ".htm" => "HTML",
                ".svg" => "SVG",
                ".cs" => "C#",
                ".py" => "Python",
                ".js" or ".jsx" or ".mjs" => "JavaScript",
                ".ts" or ".tsx" => "TypeScript",
                ".css" or ".scss" => "CSS",
                ".sql" => "SQL",
                ".ps1" => "PowerShell",
                ".sh" or ".bash" => "Shell",
                ".java" => "Java",
                ".go" => "Go",
                ".rs" => "Rust",
                ".c" or ".cpp" or ".h" or ".hpp" => "C/C++",
                ".txt" => "Text",
                ".log" => "Log",
                "" => "File",
                _ => ext.TrimStart('.').ToUpperInvariant()
            };
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
                ".json" or ".jsonc" => BuildJsonFileDocument(file),
                ".xml" or ".xaml" or ".csproj" or ".config" => BuildXmlFileDocument(file),
                ".yaml" or ".yml" or ".toml" or ".ini" or ".cfg" or ".env" => BuildConfigFileDocument(file),
                ".cs" or ".py" or ".js" or ".jsx" or ".mjs" or ".ts" or ".tsx" or ".css" or ".scss"
                    or ".sql" or ".ps1" or ".sh" or ".bash" or ".java" or ".go" or ".rs"
                    or ".c" or ".cpp" or ".h" or ".hpp" => BuildCodeFileDocument(file),
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
            Wrap(file, BuildNumberedCodeHtml(file.Content, static raw => WebUtility.HtmlEncode(raw)));

        private static string BuildJsonFileDocument(CanvasFile file)
        {
            string formatted = file.Content;
            if (file.Extension == ".json")
            {
                try
                {
                    using var doc = JsonDocument.Parse(file.Content);
                    formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (JsonException)
                {
                    formatted = file.Content;
                }
            }

            var lineColorizer = static (string line) => HighlightJsonLine(line);
            return Wrap(file, BuildNumberedCodeHtml(formatted, lineColorizer), formattedLineCount: formatted.Count(c => c == '\n') + 1);
        }

        private static string BuildXmlFileDocument(CanvasFile file) =>
            Wrap(file, BuildNumberedCodeHtml(file.Content, static line => HighlightXmlLine(line)));

        private static string BuildConfigFileDocument(CanvasFile file) =>
            Wrap(file, BuildNumberedCodeHtml(file.Content, static line => HighlightConfigLine(line)));

        private static string BuildCodeFileDocument(CanvasFile file)
        {
            string ext = file.Extension;
            return Wrap(file, BuildNumberedCodeHtml(file.Content, line => HighlightCodeLine(line, ext)));
        }

        private static string BuildNumberedCodeHtml(string text, Func<string, string> formatLine)
        {
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder("<div class='codewrap'>");
            for (int i = 0; i < lines.Length; i++)
            {
                sb.Append("<div class='cl'><span class='ln'>")
                  .Append(i + 1)
                  .Append("</span><span class='lc'>")
                  .Append(formatLine(lines[i]))
                  .Append("</span></div>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        private static readonly Regex JsonTokenRegex = new(
            @"//[^\r\n]*|""(?:\\.|[^""\\])*""(?=\s*:)|""(?:\\.|[^""\\])*""|-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|\b(?:true|false|null)\b|[{}\[\],:]",
            RegexOptions.Compiled);

        private static string HighlightJsonLine(string line)
            => ReplaceTokensSafely(line, JsonTokenRegex, static (match, src) =>
            {
                string val = match.Value;
                if (val.StartsWith("//", StringComparison.Ordinal))
                    return WrapToken("tk-com", val);
                if (val.StartsWith('"'))
                {
                    int after = match.Index + match.Length;
                    bool isKey = after < src.Length && src[after..].TrimStart().StartsWith(':');
                    return WrapToken(isKey ? "tk-key" : "tk-str", val);
                }
                if (val is "true" or "false" or "null")
                    return WrapToken("tk-kw", val);
                if (val.Length == 1 && val[0] is '{' or '}' or '[' or ']' or ',' or ':')
                    return WrapToken("tk-punc", val);
                return WrapToken("tk-num", val);
            });

        private static readonly Regex XmlTokenRegex = new(
            @"<!--[\s\S]*?-->|</?[A-Za-z_][\w:.-]*|/?>|[A-Za-z_][\w:.-]*(?=\s*=)|""[^""]*""|'[^']*'",
            RegexOptions.Compiled);

        private static string HighlightXmlLine(string line)
            => ReplaceTokensSafely(line, XmlTokenRegex, static (match, _) =>
            {
                string val = match.Value;
                if (val.StartsWith("<!--", StringComparison.Ordinal))
                    return WrapToken("tk-com", val);
                if (val.StartsWith('<') || val is ">" or "/>")
                    return WrapToken("tk-tag", val);
                if (val.StartsWith('"') || val.StartsWith('\''))
                    return WrapToken("tk-str", val);
                return WrapToken("tk-key", val);
            });

        private static readonly Regex ConfigTokenRegex = new(
            @"(?:^|\s)[#;].*$|^\[[^\]]+\]|^[\s-]*(?<k>[A-Za-z0-9_.\-/""]+)(?=\s*[:=])|""(?:\\.|[^""\\])*""|'[^']*'|(?<![\w.])\b(?:true|false|yes|no|null|on|off|\d+(?:\.\d+)?)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static string HighlightConfigLine(string line)
            => ReplaceTokensSafely(line, ConfigTokenRegex, static (match, _) =>
            {
                string val = match.Value;
                string trimmed = val.TrimStart();
                if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
                    return WebUtility.HtmlEncode(val[..(val.Length - trimmed.Length)]) + WrapToken("tk-com", trimmed);
                if (trimmed.StartsWith('['))
                    return WrapToken("tk-sec", val);
                if (match.Groups["k"].Success)
                    return WrapToken("tk-key", val);
                if (trimmed.StartsWith('"') || trimmed.StartsWith('\''))
                    return WrapToken("tk-str", val);
                return char.IsDigit(trimmed[0]) ? WrapToken("tk-num", val) : WrapToken("tk-kw", val);
            });

        private static readonly Regex CodeTokenRegex = new(
            @"//.*$|/\*[\s\S]*?\*/|#.*$|""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])*'|`[^`]*`|\b(?:abstract|as|async|await|bool|break|case|catch|class|const|continue|def|default|do|elif|else|enum|export|extends|false|finally|for|from|func|function|if|implements|import|in|interface|internal|is|let|match|namespace|new|null|None|override|package|private|protected|public|readonly|record|return|sealed|select|static|struct|switch|throw|true|True|False|try|type|using|var|void|while|with|yield)\b|\b\d+(?:\.\d+)?\b",
            RegexOptions.Compiled);

        private static string HighlightCodeLine(string line, string extension)
            => ReplaceTokensSafely(line, CodeTokenRegex, (match, _) =>
            {
                string val = match.Value;
                if (val.StartsWith("//", StringComparison.Ordinal) || val.StartsWith("/*", StringComparison.Ordinal))
                    return WrapToken("tk-com", val);
                if (val.StartsWith('#'))
                {
                    bool isHashComment = extension is ".py" or ".sh" or ".bash" or ".ps1";
                    return WrapToken(isHashComment ? "tk-com" : "tk-kw", val);
                }
                if (val.StartsWith('"') || val.StartsWith('\'') || val.StartsWith('`'))
                    return WrapToken("tk-str", val);
                if (char.IsDigit(val[0]))
                    return WrapToken("tk-num", val);
                return WrapToken("tk-kw", val);
            });

        private static string ReplaceTokensSafely(string line, Regex tokenRegex, Func<Match, string, string> renderToken)
        {
            if (string.IsNullOrEmpty(line))
                return string.Empty;

            var sb = new StringBuilder(line.Length + 32);
            int last = 0;
            foreach (Match m in tokenRegex.Matches(line))
            {
                if (m.Index < last)
                    continue;
                if (m.Index > last)
                    sb.Append(WebUtility.HtmlEncode(line[last..m.Index]));
                sb.Append(renderToken(m, line));
                last = m.Index + m.Length;
            }
            if (last < line.Length)
                sb.Append(WebUtility.HtmlEncode(line[last..]));
            return sb.ToString();
        }

        private static string WrapToken(string cssClass, string rawValue)
            => "<span class='" + cssClass + "'>" + WebUtility.HtmlEncode(rawValue) + "</span>";

        private static string Wrap(CanvasFile file, string bodyHtml, int? formattedLineCount = null)
        {
            string lineCount = (formattedLineCount ?? (file.Content.Count(c => c == '\n') + 1)).ToString();
            string typeBadge = GetFileTypeBadge(file.Extension);
            return "<!DOCTYPE html><html><head><meta charset='utf-8'>"
                 + "<meta name='viewport' content='width=device-width, initial-scale=1.0'><style>"
                 + "html,body{margin:0;background:transparent;}"
                 + "body{color:#E4DFD8;font-family:'Segoe UI Variable Text','Segoe UI',sans-serif;font-size:14px;line-height:1.68;}"
                 + ".hdr{position:sticky;top:0;z-index:2;display:flex;align-items:center;gap:8px;padding:10px 16px;"
                 + "background:#1B1917;border-bottom:1px solid #2D3139;}"
                 + ".hdr .nm{font-weight:600;color:#EDE8E3;font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px;}"
                 + ".hdr .badge{background:#2A241B;color:#F5D591;border:1px solid #4A3E2A;border-radius:999px;padding:1px 8px;font-size:10px;font-weight:600;}"
                 + ".hdr .meta{margin-left:auto;color:#8A8279;font-size:10.5px;}"
                 + ".body{padding:16px 18px;}"
                 + ".codewrap{background:#151412;border:1px solid #2D3139;border-radius:10px;padding:10px 0;overflow:auto;font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px;line-height:1.6;}"
                 + ".cl{display:flex;padding:0 14px 0 0;min-height:1.6em;white-space:pre;}"
                 + ".cl:hover{background:rgba(255,255,255,0.03);}"
                 + ".ln{user-select:none;min-width:38px;padding:0 12px 0 10px;text-align:right;color:#5D5750;flex:0 0 auto;}"
                 + ".lc{color:#E4DFD8;white-space:pre;flex:1 1 auto;}"
                 + ".tk-key{color:#93C5FD;}.tk-str{color:#86EFAC;}.tk-num{color:#FCA5A5;}.tk-kw{color:#F5D591;}.tk-punc{color:#9A9288;}.tk-com{color:#6E675F;font-style:italic;}.tk-tag{color:#F5D591;}.tk-sec{color:#C4B5FD;font-weight:600;}"
                 + "pre{margin:0 0 1em;background:#151412;border:1px solid #2D3139;border-radius:8px;padding:12px 14px;overflow:auto;}"
                 + "code{font-family:'Cascadia Mono',Consolas,monospace;font-size:12.5px;line-height:1.6;color:#D5CFC6;}"
                 + ".md h1,.md h2,.md h3{color:#F5E6C8;line-height:1.3;margin:1.1em 0 .45em;}.md h1:first-child,.md h2:first-child{margin-top:0;}"
                 + ".md p,.md ul,.md ol,.md table,.md pre,.md blockquote{margin:0 0 .9em;}.md ul,.md ol{padding-left:1.5em;}"
                 + ".md blockquote{border-left:3px solid #B8924A;background:#1E1C1A;padding:8px 12px;color:#CFC7BD;}"
                 + ".md code{background:#211F1D;border:1px solid #2D3139;border-radius:4px;padding:1px 5px;color:#F5D591;}"
                 + ".md pre code{background:transparent;border:none;padding:0;color:#E4DFD8;}"
                 + "table{border-collapse:collapse;width:100%;font-size:13px;}"
                 + "th,td{border:1px solid #2D3139;padding:7px 10px;text-align:left;}th{background:#211F1D;color:#F5E6C8;}"
                 + "a{color:#F5D591;text-decoration:underline;}"
                 + "</style></head><body>"
                 + "<div class='hdr'><span class='nm'>" + WebUtility.HtmlEncode(file.FileName) + "</span>"
                 + "<span class='badge'>" + WebUtility.HtmlEncode(typeBadge) + "</span>"
                 + "<span class='meta'>" + lineCount + " lines</span></div>"
                 + "<div class='body'>" + bodyHtml + "</div></body></html>";
        }
    }
}
