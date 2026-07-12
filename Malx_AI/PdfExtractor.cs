using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Malx_AI
{
    public class PdfExtractor
    {
        /// <summary>
        /// Extracts text from PDF file asynchronously with reading-order reconstruction
        /// (words grouped into lines by baseline, lines ordered top-to-bottom).
        /// </summary>
        public static async Task<string> ExtractTextFromPdfAsync(string filePath)
        {
            return await Task.Run(() => ExtractTextFromPdf(filePath));
        }

        private static string ExtractTextFromPdf(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException($"PDF file not found: {filePath}");

            try
            {
                var builder = new StringBuilder();
                using var document = PdfDocument.Open(filePath);
                int pageCount = document.NumberOfPages;

                foreach (var page in document.GetPages())
                {
                    if (pageCount > 1)
                        builder.AppendLine($"[Page {page.Number}]");

                    builder.AppendLine(ExtractPageText(page));
                    builder.AppendLine();
                }

                string text = builder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("PDF appears to have no extractable text (it may be a scanned/image-only PDF).");
                }

                return text;
            }
            catch (Exception ex) when (ex is not InvalidOperationException)
            {
                Debug.WriteLine($"PDF extraction failed: {ex.Message}");
                throw new InvalidOperationException($"Failed to extract text from PDF: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Rebuilds page text from positioned words: raw page.Text concatenates glyphs in content
        /// stream order, which scrambles columns and drops line breaks. Grouping words by baseline
        /// and reading lines top-to-bottom keeps sentences and table rows coherent for the model.
        /// </summary>
        private static string ExtractPageText(Page page)
        {
            List<Word> words;
            try
            {
                words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            }
            catch
            {
                return page.Text;
            }

            if (words.Count == 0)
                return page.Text;

            // Words are processed top-to-bottom, so a word either continues the most recent
            // line or starts a new one. Scanning every line per word (the previous behavior)
            // was O(n²) on dense pages and could attach a word to a visually distant line
            // whose first word happened to sit within tolerance.
            var lines = new List<List<Word>>();
            foreach (Word word in words.OrderByDescending(w => w.BoundingBox.Bottom))
            {
                double wordBaseline = word.BoundingBox.Bottom;
                double tolerance = Math.Max(2.0, word.BoundingBox.Height * 0.6);

                List<Word>? line = lines.Count > 0
                    && Math.Abs(lines[^1][0].BoundingBox.Bottom - wordBaseline) <= tolerance
                    ? lines[^1]
                    : null;

                if (line == null)
                {
                    line = new List<Word>();
                    lines.Add(line);
                }

                line.Add(word);
            }

            var pageBuilder = new StringBuilder();
            foreach (List<Word> line in lines)
            {
                pageBuilder.AppendLine(string.Join(" ", line.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text)));
            }

            return pageBuilder.ToString().TrimEnd();
        }

        /// <summary>
        /// Validates if a PDF file is readable
        /// </summary>
        public static async Task<bool> ValidatePdfAsync(string filePath)
        {
            return await Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(filePath))
                        return false;

                    using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
                    byte[] header = new byte[5];
                    int headerBytesRead = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
                    return headerBytesRead >= 4
                        && Encoding.ASCII.GetString(header, 0, headerBytesRead).StartsWith("%PDF", StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            });
        }
    }
}
