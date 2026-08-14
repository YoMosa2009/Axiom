using System;
using System.Text;
using System.Text.Json;

namespace Malx_AI
{
    /// <summary>
    /// Separates visible OpenRouter message content from structured reasoning parts.
    /// Plain string content is always an answer; reasoning embedded in content must be explicitly typed.
    /// </summary>
    internal static class OpenRouterContentParts
    {
        internal static string ExtractText(JsonElement content, bool includeReasoningParts)
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;

            if (content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (JsonElement item in content.EnumerateArray())
                {
                    string type = item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("type", out JsonElement typeElement)
                            ? typeElement.GetString() ?? string.Empty
                            : string.Empty;

                    bool isReasoningPart = type.Contains("reason", StringComparison.OrdinalIgnoreCase);
                    if (isReasoningPart != includeReasoningParts)
                        continue;

                    if (item.TryGetProperty("text", out JsonElement textElement))
                        builder.AppendLine(textElement.GetString() ?? string.Empty);
                    else if (item.TryGetProperty("content", out JsonElement contentElement))
                        builder.AppendLine(ExtractText(contentElement, includeReasoningParts));
                }

                return builder.ToString().Trim();
            }

            if (content.ValueKind == JsonValueKind.Object)
            {
                if (content.TryGetProperty("type", out JsonElement typeElement))
                {
                    string type = typeElement.GetString() ?? string.Empty;
                    bool isReasoningPart = type.Contains("reason", StringComparison.OrdinalIgnoreCase);
                    if (isReasoningPart != includeReasoningParts)
                        return string.Empty;
                }

                if (content.TryGetProperty("text", out JsonElement textElement))
                    return textElement.GetString() ?? string.Empty;

                if (content.TryGetProperty("content", out JsonElement nestedContent))
                    return ExtractText(nestedContent, includeReasoningParts);

                if (content.TryGetProperty("summary", out JsonElement summaryElement))
                    return ExtractText(summaryElement, includeReasoningParts);
            }

            return string.Empty;
        }

        internal static string ExtractReasoningFromContent(JsonElement content)
        {
            if (content.ValueKind == JsonValueKind.Array)
                return ExtractText(content, includeReasoningParts: true);

            if (content.ValueKind != JsonValueKind.Object)
                return string.Empty;

            if (content.TryGetProperty("type", out JsonElement typeElement)
                && (typeElement.GetString() ?? string.Empty).Contains("reason", StringComparison.OrdinalIgnoreCase))
            {
                return ExtractText(content, includeReasoningParts: true);
            }

            if (content.TryGetProperty("content", out JsonElement nestedContent))
                return ExtractReasoningFromContent(nestedContent);

            if (content.TryGetProperty("summary", out JsonElement summaryElement))
                return ExtractReasoningFromContent(summaryElement);

            return string.Empty;
        }
    }
}
