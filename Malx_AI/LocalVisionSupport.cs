using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Malx_AI
{
    internal static class LocalVisionSupport
    {
        public static string FindBestProjectorNextToModel(string modelPath)
        {
            string? directory = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return string.Empty;

            return SelectBestProjector(
                modelPath,
                Directory.EnumerateFiles(directory, "*.gguf").Where(IsMmprojFile).ToList());
        }

        public static string SelectBestProjector(string modelPath, IReadOnlyList<string> candidates)
        {
            if (candidates is null || candidates.Count == 0)
                return string.Empty;
            if (candidates.Count == 1)
                return candidates[0];

            string modelStem = Path.GetFileNameWithoutExtension(modelPath).ToLowerInvariant();
            return candidates
                .OrderByDescending(candidate =>
                {
                    string stem = Path.GetFileNameWithoutExtension(candidate).ToLowerInvariant()
                        .Replace("mmproj-", string.Empty)
                        .Replace("mmproj", string.Empty)
                        .Trim('-', '_', '.');
                    int common = 0;
                    while (common < Math.Min(stem.Length, modelStem.Length) && stem[common] == modelStem[common])
                        common++;
                    return common;
                })
                .ThenBy(candidate => candidate, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        public static bool IsMmprojFile(string path)
        {
            string file = Path.GetFileName(path);
            return file.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
                || file.Contains("mmproj-", StringComparison.OrdinalIgnoreCase);
        }

        public static string PrependImageMarkers(string userText, int imageCount)
        {
            if (imageCount <= 0)
                return userText;

            var builder = new StringBuilder();
            for (int index = 0; index < imageCount; index++)
                builder.Append("<image>\n");
            builder.Append(userText);
            return builder.ToString();
        }

        public static string BuildUnavailableNote(int imageCount)
        {
            string noun = imageCount == 1 ? "image was" : "images were";
            return $"[IMAGE ATTACHMENT NOTE]\n{imageCount} attached {noun} supplied, but this Builder model cannot see image pixels because no compatible vision input is available. Do not infer, describe, or invent the image contents; state that limitation if the request depends on them.";
        }

        public static string BuildImageDataUrl(string mimeType, string base64Data)
            => $"data:{mimeType};base64,{base64Data}";
    }
}
