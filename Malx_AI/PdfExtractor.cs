using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace Malx_AI
{
    public class PdfExtractor
    {
        /// <summary>
        /// Extracts text from PDF file asynchronously
        /// Note: This uses a basic approach. For production, integrate iTextSharp or similar.
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

                foreach (var page in document.GetPages())
                {
                    builder.AppendLine(page.Text);
                }

                string text = builder.ToString().Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw new InvalidOperationException("PDF appears to have no extractable text.");
                }

                return text;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PDF extraction failed: {ex.Message}");
                throw new InvalidOperationException($"Failed to extract text from PDF: {ex.Message}", ex);
            }
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
                    fs.Read(header, 0, 5);
                    return Encoding.ASCII.GetString(header).StartsWith("%PDF", StringComparison.Ordinal);
                }
                catch
                {
                    return false;
                }
            });
        }
    }
}
