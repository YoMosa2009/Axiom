using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public class DocumentChunk
    {
        public int ChunkId { get; set; }
        public string FileName { get; set; }
        public string Content { get; set; }
        public int TokenCount { get; set; }
        public int StartPosition { get; set; }
        public int EndPosition { get; set; }
    }

    public class DocumentChunker
    {
        private const int TARGET_TOKENS_PER_CHUNK = 400;
        private const double AVG_CHARS_PER_TOKEN = 4.0;
        private const int TARGET_CHARS = (int)(TARGET_TOKENS_PER_CHUNK * AVG_CHARS_PER_TOKEN);

        public static List<DocumentChunk> ChunkDocument(string fileName, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                Debug.WriteLine($"ChunkDocument: Empty content for {fileName}");
                return new List<DocumentChunk>();
            }

            try
            {
                var chunks = new List<DocumentChunk>();
                var sentences = SplitIntoSentences(content);
                
                if (sentences.Count == 0)
                {
                    Debug.WriteLine($"ChunkDocument: No sentences found in {fileName}");
                    return new List<DocumentChunk>();
                }

                var currentChunk = new StringBuilder();
                int chunkId = 0;
                int startPos = 0;

                foreach (var sentence in sentences)
                {
                    string potentialChunk = currentChunk.Length == 0 ? sentence : currentChunk + " " + sentence;

                    if (potentialChunk.Length > TARGET_CHARS && currentChunk.Length > 0)
                    {
                        chunks.Add(new DocumentChunk
                        {
                            ChunkId = chunkId++,
                            FileName = fileName,
                            Content = currentChunk.ToString().Trim(),
                            TokenCount = EstimateTokenCount(currentChunk.ToString()),
                            StartPosition = startPos,
                            EndPosition = startPos + currentChunk.Length
                        });

                        int previousChunkLength = currentChunk.Length;
                        int overlapLength = (int)(previousChunkLength * 0.2);
                        string overlap = currentChunk.ToString().Substring(Math.Max(0, previousChunkLength - overlapLength));
                        currentChunk.Clear();
                        currentChunk.Append(overlap);
                        startPos += previousChunkLength - overlap.Length;
                    }

                    currentChunk.Append(currentChunk.Length == 0 ? sentence : " " + sentence);
                }

                if (currentChunk.Length > 0)
                {
                    chunks.Add(new DocumentChunk
                    {
                        ChunkId = chunkId,
                        FileName = fileName,
                        Content = currentChunk.ToString().Trim(),
                        TokenCount = EstimateTokenCount(currentChunk.ToString()),
                        StartPosition = startPos,
                        EndPosition = startPos + currentChunk.Length
                    });
                }

                Debug.WriteLine($"ChunkDocument: Created {chunks.Count} chunks from {fileName}");
                return chunks;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ChunkDocument error: {ex.Message}");
                return new List<DocumentChunk>();
            }
        }

        private static List<string> SplitIntoSentences(string text)
        {
            try
            {
                var sentences = Regex.Split(text, @"(?<=[.!?])\s+")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();

                if (sentences.Count < 2)
                {
                    sentences = text.Split(new[] { "\n\n", "\r\n\r\n", "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .ToList();
                }

                return sentences;
            }
            catch
            {
                return text.Split(new[] { "\n", "\r\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
        }

        private static int EstimateTokenCount(string text)
        {
            return (int)Math.Ceiling(text.Length / AVG_CHARS_PER_TOKEN);
        }
    }
}
