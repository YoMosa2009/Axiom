using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed class ChatAttachmentImportResult
    {
        public ChatDocumentAttachment Attachment { get; init; } = new();
        public string SummaryLabel { get; init; } = string.Empty;
    }

    public static class ChatAttachmentImportService
    {
        private static readonly HashSet<string> TextFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".csv", ".log", ".ini", ".config", ".conf", ".env", ".properties",
            ".cs", ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx", ".html", ".htm", ".css", ".scss", ".less", ".sql", ".py", ".java", ".cpp", ".cc", ".c", ".h", ".hpp",
            ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".kts", ".vb", ".r", ".pl", ".lua", ".dart", ".scala", ".groovy", ".m", ".mm",
            ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".bash", ".zsh", ".fish",
            ".xaml", ".axaml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".sln", ".slnx", ".gradle", ".cmake", ".dockerfile", ".editorconfig", ".gitignore", ".gitattributes",
            ".tex", ".bib", ".rst", ".adoc", ".org", ".srt", ".vtt", ".diff", ".patch", ".graphql", ".proto", ".http", ".rest"
        };

        private static readonly HashSet<string> ImageFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"
        };

        private static readonly Dictionary<string, string> MimeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".webp"] = "image/webp",
            [".gif"] = "image/gif",
            [".bmp"] = "image/bmp"
        };

        private const int MaxVisionBytes = 6 * 1024 * 1024;
        private static readonly Regex XmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
        private static readonly Regex MultiWhitespaceRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);
        private static readonly Regex ExcessBlankLinesRegex = new(@"(\r?\n){3,}", RegexOptions.Compiled);

        public static async Task<ChatAttachmentImportResult> ImportAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path is required.", nameof(filePath));

            if (!File.Exists(filePath))
                throw new FileNotFoundException("Attachment file not found.", filePath);

            string extension = Path.GetExtension(filePath);
            string fileName = Path.GetFileName(filePath);
            var info = new FileInfo(filePath);

            if (string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                string text = await PdfExtractor.ExtractTextFromPdfAsync(filePath);
                return new ChatAttachmentImportResult
                {
                    Attachment = new ChatDocumentAttachment
                    {
                        Name = fileName,
                        Content = text,
                        Kind = "text",
                        MimeType = "application/pdf",
                        FileSizeBytes = info.Length,
                        ImportedAt = DateTime.Now
                    },
                    SummaryLabel = "pdf"
                };
            }

            if (ImageFileExtensions.Contains(extension))
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath);
                if (bytes.Length > MaxVisionBytes)
                    throw new InvalidOperationException($"Image is too large for vision import ({bytes.Length / 1024 / 1024.0:F1} MB). Use an image under 6 MB.");

                string mimeType = MimeTypes.TryGetValue(extension, out string? mappedMimeType)
                    ? mappedMimeType
                    : "application/octet-stream";

                return new ChatAttachmentImportResult
                {
                    Attachment = new ChatDocumentAttachment
                    {
                        Name = fileName,
                        Content = BuildImageSummary(fileName, bytes.Length, mimeType),
                        Kind = "image",
                        MimeType = mimeType,
                        Base64Data = Convert.ToBase64String(bytes),
                        FileSizeBytes = bytes.Length,
                        ImportedAt = DateTime.Now
                    },
                    SummaryLabel = "image"
                };
            }

            if (string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase))
            {
                string csv = await File.ReadAllTextAsync(filePath);
                string normalized = ConvertDelimitedTextToStructuredText(fileName, csv, ',');
                return CreateTextResult(fileName, normalized, "text/csv", info.Length, "csv");
            }

            if (string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase))
            {
                string tsv = await File.ReadAllTextAsync(filePath);
                string normalized = ConvertDelimitedTextToStructuredText(fileName, tsv, '\t');
                return CreateTextResult(fileName, normalized, "text/tab-separated-values", info.Length, "tsv");
            }

            if (string.Equals(extension, ".rtf", StringComparison.OrdinalIgnoreCase))
            {
                string rtf = await File.ReadAllTextAsync(filePath);
                string plain = StripRtfControlCodes(rtf);
                return CreateTextResult(fileName, plain, "application/rtf", info.Length, "document");
            }

            if (string.Equals(extension, ".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                string workbookText = await Task.Run(() => ExtractXlsxAsStructuredText(filePath));
                return CreateTextResult(fileName, workbookText, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", info.Length, "spreadsheet");
            }

            if (string.Equals(extension, ".docx", StringComparison.OrdinalIgnoreCase))
            {
                string documentText = await Task.Run(() => ExtractDocxText(filePath));
                return CreateTextResult(fileName, documentText, "application/vnd.openxmlformats-officedocument.wordprocessingml.document", info.Length, "document");
            }

            if (string.Equals(extension, ".pptx", StringComparison.OrdinalIgnoreCase))
            {
                string presentationText = await Task.Run(() => ExtractPptxText(filePath));
                return CreateTextResult(fileName, presentationText, "application/vnd.openxmlformats-officedocument.presentationml.presentation", info.Length, "presentation");
            }

            if (string.Equals(extension, ".odt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".odp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ods", StringComparison.OrdinalIgnoreCase))
            {
                string odfText = await Task.Run(() => ExtractOpenDocumentText(filePath));
                string odfLabel = extension.ToLowerInvariant() switch
                {
                    ".odp" => "presentation",
                    ".ods" => "spreadsheet",
                    _ => "document"
                };
                return CreateTextResult(fileName, odfText, "application/vnd.oasis.opendocument", info.Length, odfLabel);
            }

            if (string.Equals(extension, ".epub", StringComparison.OrdinalIgnoreCase))
            {
                string bookText = await Task.Run(() => ExtractEpubText(filePath));
                return CreateTextResult(fileName, bookText, "application/epub+zip", info.Length, "e-book");
            }

            if (string.Equals(extension, ".ipynb", StringComparison.OrdinalIgnoreCase))
            {
                string notebookJson = await File.ReadAllTextAsync(filePath);
                string notebookText = ExtractNotebookText(notebookJson);
                return CreateTextResult(fileName, notebookText, "application/x-ipynb+json", info.Length, "notebook");
            }

            if (TextFileExtensions.Contains(extension) || string.IsNullOrWhiteSpace(extension))
            {
                string text = await File.ReadAllTextAsync(filePath);
                return CreateTextResult(fileName, text, ResolveTextMimeType(extension), info.Length, "text");
            }

            // Unknown extension: sniff the content and import as plain text when it looks textual,
            // so config files, exports, and source in unrecognized formats still work.
            if (await LooksLikeTextFileAsync(filePath))
            {
                string text = await File.ReadAllTextAsync(filePath);
                return CreateTextResult(fileName, text, "text/plain", info.Length, "text");
            }

            throw new NotSupportedException($"Unsupported file type: {extension} (binary content could not be read as text)");
        }

        private static ChatAttachmentImportResult CreateTextResult(string fileName, string text, string mimeType, long fileSizeBytes, string summaryLabel)
        {
            string normalized = NormalizeExtractedText(text);
            if (string.IsNullOrWhiteSpace(normalized))
                throw new InvalidOperationException("The file did not contain extractable text.");

            return new ChatAttachmentImportResult
            {
                Attachment = new ChatDocumentAttachment
                {
                    Name = fileName,
                    Content = normalized,
                    Kind = "text",
                    MimeType = mimeType,
                    FileSizeBytes = fileSizeBytes,
                    ImportedAt = DateTime.Now
                },
                SummaryLabel = summaryLabel
            };
        }

        private static string ResolveTextMimeType(string extension)
        {
            return extension?.ToLowerInvariant() switch
            {
                ".md" or ".markdown" => "text/markdown",
                ".json" or ".jsonc" => "application/json",
                ".xml" => "application/xml",
                ".yaml" or ".yml" => "application/yaml",
                ".html" or ".htm" => "text/html",
                _ => "text/plain"
            };
        }

        private static readonly Regex RtfControlWordRegex = new(@"\\[a-zA-Z]+-?\d*\s?|[{}]|\\'[0-9a-fA-F]{2}|\\\*", RegexOptions.Compiled);

        private static string StripRtfControlCodes(string rtf)
        {
            if (string.IsNullOrWhiteSpace(rtf))
                return string.Empty;

            // Remove embedded binary/object groups before stripping control words.
            string withoutObjects = Regex.Replace(rtf, @"\{\\\*?\\(fonttbl|colortbl|stylesheet|info|pict|object|themedata)[\s\S]*?\}", " ", RegexOptions.IgnoreCase);
            string text = RtfControlWordRegex.Replace(withoutObjects, " ");
            return NormalizeExtractedText(text);
        }

        private static async Task<bool> LooksLikeTextFileAsync(string filePath)
        {
            try
            {
                const int sampleSize = 8192;
                byte[] buffer = new byte[sampleSize];
                int read;
                await using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, sampleSize, useAsync: true))
                {
                    read = await stream.ReadAsync(buffer.AsMemory(0, sampleSize));
                }

                if (read == 0)
                    return false;

                int suspicious = 0;
                for (int i = 0; i < read; i++)
                {
                    byte b = buffer[i];
                    if (b == 0)
                        return false; // NUL byte: almost certainly binary
                    if (b < 0x09 || (b > 0x0D && b < 0x20))
                        suspicious++;
                }

                return suspicious < read / 50; // tolerate <2% odd control bytes
            }
            catch
            {
                return false;
            }
        }

        private static string BuildImageSummary(string fileName, int byteCount, string mimeType)
        {
            return $"[IMAGE ATTACHMENT]\nName: {fileName}\nType: {mimeType}\nSize: {byteCount / 1024.0:F1} KB\nAnalyzable by a vision-capable model (local mmproj or cloud).";
        }

        private static string ConvertDelimitedTextToStructuredText(string fileName, string content, char delimiter)
        {
            using var reader = new StringReader(content ?? string.Empty);
            var rows = new List<List<string>>();
            string? line;
            while ((line = reader.ReadLine()) != null)
                rows.Add(ParseDelimitedLine(line, delimiter));

            if (rows.Count == 0)
                return string.Empty;

            int columnCount = rows.Max(r => r.Count);
            var headers = rows[0]
                .Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"Column {index + 1}" : value.Trim())
                .ToList();

            while (headers.Count < columnCount)
                headers.Add($"Column {headers.Count + 1}");

            const int maxStructuredRows = 500;
            var builder = new StringBuilder();
            builder.AppendLine($"Spreadsheet import: {fileName}");
            builder.AppendLine($"Rows: {Math.Max(0, rows.Count - 1)}, Columns: {columnCount}");
            if (rows.Count - 1 > maxStructuredRows)
                builder.AppendLine($"(showing first {maxStructuredRows} rows)");
            builder.AppendLine();

            for (int rowIndex = 1; rowIndex < rows.Count && rowIndex <= maxStructuredRows; rowIndex++)
            {
                builder.AppendLine($"Row {rowIndex}:");
                List<string> row = rows[rowIndex];
                for (int colIndex = 0; colIndex < columnCount; colIndex++)
                {
                    string header = headers[colIndex];
                    string value = colIndex < row.Count ? row[colIndex] : string.Empty;
                    builder.AppendLine($"- {header}: {value}");
                }
                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        private static List<string> ParseDelimitedLine(string line, char delimiter)
        {
            var values = new List<string>();
            if (line == null)
                return values;

            var builder = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        builder.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (c == delimiter && !inQuotes)
                {
                    values.Add(builder.ToString().Trim());
                    builder.Clear();
                }
                else
                {
                    builder.Append(c);
                }
            }

            values.Add(builder.ToString().Trim());
            return values;
        }

        private static string ExtractDocxText(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            ZipArchiveEntry? documentEntry = archive.GetEntry("word/document.xml");
            if (documentEntry == null)
                throw new InvalidOperationException("The Word document body could not be found.");

            using var stream = documentEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string xml = reader.ReadToEnd();
            string text = XmlTagRegex.Replace(xml, " ");
            return NormalizeExtractedText(System.Net.WebUtility.HtmlDecode(text));
        }

        private static readonly Regex DrawingTextRunRegex = new(@"<a:t[^>]*>(?<text>[\s\S]*?)</a:t>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TrailingEntryNumberRegex = new(@"(?<number>\d+)\.xml$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static int ExtractTrailingEntryNumber(string entryName)
        {
            Match match = TrailingEntryNumberRegex.Match(entryName ?? string.Empty);
            return match.Success && int.TryParse(match.Groups["number"].Value, out int number) ? number : int.MaxValue;
        }

        private static string ReadZipEntryText(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        private static string ExtractPptxText(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            var slideEntries = archive.Entries
                .Where(e => Regex.IsMatch(e.FullName, @"^ppt/slides/slide\d+\.xml$", RegexOptions.IgnoreCase))
                .OrderBy(e => ExtractTrailingEntryNumber(e.FullName))
                .ToList();

            if (slideEntries.Count == 0)
                throw new InvalidOperationException("The presentation does not contain readable slides.");

            var notesByNumber = archive.Entries
                .Where(e => Regex.IsMatch(e.FullName, @"^ppt/notesSlides/notesSlide\d+\.xml$", RegexOptions.IgnoreCase))
                .ToDictionary(ExtractEntryNumberKey, ReadZipEntryText);

            var builder = new StringBuilder();
            foreach (ZipArchiveEntry slideEntry in slideEntries)
            {
                int slideNumber = ExtractTrailingEntryNumber(slideEntry.FullName);
                string slideText = ExtractDrawingText(ReadZipEntryText(slideEntry));
                if (string.IsNullOrWhiteSpace(slideText))
                    continue;

                builder.AppendLine($"Slide {slideNumber}:");
                builder.AppendLine(slideText);

                if (notesByNumber.TryGetValue(slideNumber, out string? notesXml))
                {
                    string notesText = ExtractDrawingText(notesXml);
                    // Notes bodies repeat the slide number as a page marker; only keep real notes.
                    if (!string.IsNullOrWhiteSpace(notesText) && notesText.Trim() != slideNumber.ToString())
                    {
                        builder.AppendLine($"Slide {slideNumber} speaker notes:");
                        builder.AppendLine(notesText);
                    }
                }

                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        private static int ExtractEntryNumberKey(ZipArchiveEntry entry) => ExtractTrailingEntryNumber(entry.FullName);

        private static string ExtractDrawingText(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml))
                return string.Empty;

            var lines = DrawingTextRunRegex.Matches(xml)
                .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups["text"].Value).Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t));

            return string.Join("\n", lines);
        }

        private static string ExtractOpenDocumentText(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            ZipArchiveEntry? contentEntry = archive.GetEntry("content.xml");
            if (contentEntry == null)
                throw new InvalidOperationException("The OpenDocument file does not contain readable content.");

            string xml = ReadZipEntryText(contentEntry);
            // Preserve paragraph/heading/row boundaries before tags are stripped, so text
            // doesn't fuse into one unreadable line.
            xml = Regex.Replace(xml, @"</text:(p|h)>|</table:table-row>", "\n", RegexOptions.IgnoreCase);
            xml = Regex.Replace(xml, @"<text:tab[^>]*/?>", "\t", RegexOptions.IgnoreCase);
            xml = Regex.Replace(xml, @"<text:line-break[^>]*/?>", "\n", RegexOptions.IgnoreCase);
            string text = XmlTagRegex.Replace(xml, " ");
            return NormalizeExtractedText(System.Net.WebUtility.HtmlDecode(text));
        }

        private static string ExtractEpubText(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            var documentEntries = archive.Entries
                .Where(e => e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase)
                         || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                         || e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (documentEntries.Count == 0)
                throw new InvalidOperationException("The e-book does not contain readable chapters.");

            List<ZipArchiveEntry> orderedEntries = TryOrderEpubEntriesBySpine(archive, documentEntries)
                ?? documentEntries.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase).ToList();

            var builder = new StringBuilder();
            foreach (ZipArchiveEntry entry in orderedEntries)
            {
                string html = ReadZipEntryText(entry);
                html = Regex.Replace(html, @"<(script|style)[^>]*>[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"</(p|div|h[1-6]|li|tr|section|article|blockquote)>|<br[^>]*/?>", "\n", RegexOptions.IgnoreCase);
                string text = NormalizeExtractedText(System.Net.WebUtility.HtmlDecode(XmlTagRegex.Replace(html, " ")));
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                if (builder.Length > 0)
                    builder.AppendLine().AppendLine();
                builder.Append(text);
            }

            return builder.ToString().Trim();
        }

        private static List<ZipArchiveEntry>? TryOrderEpubEntriesBySpine(ZipArchive archive, List<ZipArchiveEntry> documentEntries)
        {
            try
            {
                ZipArchiveEntry? opfEntry = archive.Entries.FirstOrDefault(e => e.FullName.EndsWith(".opf", StringComparison.OrdinalIgnoreCase));
                if (opfEntry == null)
                    return null;

                string opfXml = ReadZipEntryText(opfEntry);
                string opfDirectory = Path.GetDirectoryName(opfEntry.FullName)?.Replace('\\', '/') ?? string.Empty;

                var manifestHrefsById = Regex.Matches(opfXml, "<item[^>]*\\bid=\"(?<id>[^\"]+)\"[^>]*\\bhref=\"(?<href>[^\"]+)\"[^>]*>", RegexOptions.IgnoreCase)
                    .Concat(Regex.Matches(opfXml, "<item[^>]*\\bhref=\"(?<href>[^\"]+)\"[^>]*\\bid=\"(?<id>[^\"]+)\"[^>]*>", RegexOptions.IgnoreCase))
                    .GroupBy(m => m.Groups["id"].Value, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.First().Groups["href"].Value, StringComparer.Ordinal);

                var spineIds = Regex.Matches(opfXml, "<itemref[^>]*\\bidref=\"(?<idref>[^\"]+)\"", RegexOptions.IgnoreCase)
                    .Select(m => m.Groups["idref"].Value)
                    .ToList();

                if (spineIds.Count == 0)
                    return null;

                var ordered = new List<ZipArchiveEntry>();
                foreach (string spineId in spineIds)
                {
                    if (!manifestHrefsById.TryGetValue(spineId, out string? href))
                        continue;

                    string fullPath = string.IsNullOrEmpty(opfDirectory) ? href : $"{opfDirectory}/{href}";
                    ZipArchiveEntry? match = documentEntries.FirstOrDefault(e =>
                        string.Equals(e.FullName, fullPath, StringComparison.OrdinalIgnoreCase)
                        || e.FullName.EndsWith("/" + href, StringComparison.OrdinalIgnoreCase));
                    if (match != null && !ordered.Contains(match))
                        ordered.Add(match);
                }

                return ordered.Count > 0 ? ordered : null;
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractNotebookText(string notebookJson)
        {
            using var document = System.Text.Json.JsonDocument.Parse(notebookJson);
            if (!document.RootElement.TryGetProperty("cells", out System.Text.Json.JsonElement cells)
                || cells.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                throw new InvalidOperationException("The notebook does not contain readable cells.");
            }

            const int maxOutputCharsPerCell = 2000;
            var builder = new StringBuilder();
            int cellNumber = 0;
            foreach (System.Text.Json.JsonElement cell in cells.EnumerateArray())
            {
                cellNumber++;
                string cellType = cell.TryGetProperty("cell_type", out System.Text.Json.JsonElement typeElement)
                    ? typeElement.GetString() ?? "code"
                    : "code";
                string source = ReadNotebookSource(cell);
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                if (string.Equals(cellType, "markdown", StringComparison.OrdinalIgnoreCase))
                {
                    builder.AppendLine(source.Trim());
                }
                else
                {
                    builder.AppendLine($"Code cell {cellNumber}:");
                    builder.AppendLine("```python");
                    builder.AppendLine(source.Trim());
                    builder.AppendLine("```");

                    string outputs = ReadNotebookOutputs(cell, maxOutputCharsPerCell);
                    if (!string.IsNullOrWhiteSpace(outputs))
                    {
                        builder.AppendLine("Output:");
                        builder.AppendLine(outputs);
                    }
                }

                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        private static string ReadNotebookSource(System.Text.Json.JsonElement cell)
        {
            if (!cell.TryGetProperty("source", out System.Text.Json.JsonElement source))
                return string.Empty;

            if (source.ValueKind == System.Text.Json.JsonValueKind.String)
                return source.GetString() ?? string.Empty;

            if (source.ValueKind == System.Text.Json.JsonValueKind.Array)
                return string.Concat(source.EnumerateArray().Select(part => part.GetString() ?? string.Empty));

            return string.Empty;
        }

        private static string ReadNotebookOutputs(System.Text.Json.JsonElement cell, int maxChars)
        {
            if (!cell.TryGetProperty("outputs", out System.Text.Json.JsonElement outputs)
                || outputs.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (System.Text.Json.JsonElement output in outputs.EnumerateArray())
            {
                if (builder.Length >= maxChars)
                    break;

                if (output.TryGetProperty("text", out System.Text.Json.JsonElement textElement))
                {
                    builder.Append(ReadNotebookTextValue(textElement));
                    continue;
                }

                if (output.TryGetProperty("data", out System.Text.Json.JsonElement dataElement)
                    && dataElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && dataElement.TryGetProperty("text/plain", out System.Text.Json.JsonElement plainElement))
                {
                    builder.Append(ReadNotebookTextValue(plainElement));
                }
            }

            string result = builder.ToString().Trim();
            return result.Length <= maxChars ? result : result[..maxChars] + "\n[output truncated]";
        }

        private static string ReadNotebookTextValue(System.Text.Json.JsonElement element)
        {
            if (element.ValueKind == System.Text.Json.JsonValueKind.String)
                return element.GetString() ?? string.Empty;

            if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
                return string.Concat(element.EnumerateArray().Select(part => part.GetString() ?? string.Empty));

            return string.Empty;
        }

        private static string ExtractXlsxAsStructuredText(string filePath)
        {
            using var archive = ZipFile.OpenRead(filePath);
            Dictionary<int, string> sharedStrings = LoadSharedStrings(archive);
            var sheetEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (sheetEntries.Count == 0)
                throw new InvalidOperationException("The spreadsheet does not contain readable worksheet data.");

            var builder = new StringBuilder();
            for (int i = 0; i < sheetEntries.Count; i++)
            {
                string sheetText = ExtractWorksheetText(sheetEntries[i], sharedStrings, i + 1);
                if (string.IsNullOrWhiteSpace(sheetText))
                    continue;

                if (builder.Length > 0)
                    builder.AppendLine().AppendLine();

                builder.Append(sheetText.Trim());
            }

            return NormalizeExtractedText(builder.ToString());
        }

        private static Dictionary<int, string> LoadSharedStrings(ZipArchive archive)
        {
            ZipArchiveEntry? sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStringsEntry == null)
                return new Dictionary<int, string>();

            using var stream = sharedStringsEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string xml = reader.ReadToEnd();
            MatchCollection matches = Regex.Matches(xml, @"<si[\s\S]*?</si>", RegexOptions.IgnoreCase);
            var result = new Dictionary<int, string>();
            int index = 0;
            foreach (Match match in matches)
            {
                string plain = XmlTagRegex.Replace(match.Value, " ");
                result[index++] = NormalizeExtractedText(System.Net.WebUtility.HtmlDecode(plain));
            }
            return result;
        }

        private static string ExtractWorksheetText(ZipArchiveEntry sheetEntry, Dictionary<int, string> sharedStrings, int sheetNumber)
        {
            using var stream = sheetEntry.Open();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string xml = reader.ReadToEnd();

            MatchCollection rowMatches = Regex.Matches(xml, @"<row[^>]*>(?<row>[\s\S]*?)</row>", RegexOptions.IgnoreCase);
            if (rowMatches.Count == 0)
                return string.Empty;

            var rows = new List<List<string>>();
            foreach (Match rowMatch in rowMatches)
            {
                var values = new List<string>();
                MatchCollection cellMatches = Regex.Matches(rowMatch.Groups["row"].Value, @"<c(?<attrs>[^>]*)>(?<body>[\s\S]*?)</c>", RegexOptions.IgnoreCase);
                foreach (Match cellMatch in cellMatches)
                {
                    string attrs = cellMatch.Groups["attrs"].Value;
                    string body = cellMatch.Groups["body"].Value;
                    string type = Regex.Match(attrs, "\\bt=\"(?<type>[^\"]+)\"", RegexOptions.IgnoreCase).Groups["type"].Value;
                    string value = Regex.Match(body, @"<v>(?<value>[\s\S]*?)</v>", RegexOptions.IgnoreCase).Groups["value"].Value;
                    string inline = Regex.Match(body, @"<t[^>]*>(?<value>[\s\S]*?)</t>", RegexOptions.IgnoreCase).Groups["value"].Value;

                    string resolved = type.Equals("s", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int sharedIndex)
                        ? sharedStrings.TryGetValue(sharedIndex, out string? sharedValue) ? sharedValue : value
                        : !string.IsNullOrWhiteSpace(inline) ? inline : value;

                    values.Add(System.Net.WebUtility.HtmlDecode(resolved).Trim());
                }

                rows.Add(values);
            }

            if (rows.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine($"Worksheet {sheetNumber}:");
            builder.Append(ConvertDelimitedRowsToStructuredText(rows));
            return builder.ToString();
        }

        private static string ConvertDelimitedRowsToStructuredText(List<List<string>> rows)
        {
            int columnCount = rows.Max(r => r.Count);
            List<string> headers = rows[0]
                .Select((value, index) => string.IsNullOrWhiteSpace(value) ? $"Column {index + 1}" : value.Trim())
                .ToList();

            while (headers.Count < columnCount)
                headers.Add($"Column {headers.Count + 1}");

            const int maxStructuredRows = 500;
            var builder = new StringBuilder();
            builder.AppendLine($"Rows: {Math.Max(0, rows.Count - 1)}, Columns: {columnCount}");
            if (rows.Count - 1 > maxStructuredRows)
                builder.AppendLine($"(showing first {maxStructuredRows} rows)");
            builder.AppendLine();

            for (int rowIndex = 1; rowIndex < rows.Count && rowIndex <= maxStructuredRows; rowIndex++)
            {
                builder.AppendLine($"Row {rowIndex}:");
                for (int colIndex = 0; colIndex < columnCount; colIndex++)
                {
                    string header = headers[colIndex];
                    string value = colIndex < rows[rowIndex].Count ? rows[rowIndex][colIndex] : string.Empty;
                    builder.AppendLine($"- {header}: {value}");
                }
                builder.AppendLine();
            }

            return builder.ToString().Trim();
        }

        private static string NormalizeExtractedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = MultiWhitespaceRegex.Replace(normalized, " ");
            normalized = ExcessBlankLinesRegex.Replace(normalized, "\n\n");
            return normalized.Trim();
        }
    }
}
