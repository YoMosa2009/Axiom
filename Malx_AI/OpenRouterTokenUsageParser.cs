using System;
using System.Globalization;
using System.Text.Json;

namespace Malx_AI
{
    internal static class OpenRouterTokenUsageParser
    {
        public static OpenRouterTokenUsage? TryParse(JsonElement root)
        {
            if (!TryGetPropertyIgnoreCase(root, "usage", out JsonElement usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            bool hasPrompt = TryReadTokenCount(usage, "prompt_tokens", out int promptTokens);
            bool hasCompletion = TryReadTokenCount(usage, "completion_tokens", out int completionTokens);
            bool hasTotal = TryReadTokenCount(usage, "total_tokens", out int totalTokens);
            if (!hasPrompt && !hasCompletion && !hasTotal)
                return null;

            if (!hasTotal)
                totalTokens = promptTokens + completionTokens;
            if (promptTokens == 0 && completionTokens == 0 && totalTokens == 0)
                return null;

            return new OpenRouterTokenUsage(promptTokens, completionTokens, totalTokens);
        }

        private static bool TryReadTokenCount(JsonElement usage, string propertyName, out int value)
        {
            value = 0;
            if (!TryGetPropertyIgnoreCase(usage, propertyName, out JsonElement element))
                return false;

            if (element.ValueKind == JsonValueKind.Number)
            {
                if (element.TryGetInt32(out int intValue))
                {
                    value = Math.Max(0, intValue);
                    return true;
                }

                if (element.TryGetInt64(out long longValue))
                {
                    value = (int)Math.Clamp(longValue, 0, int.MaxValue);
                    return true;
                }
            }

            if (element.ValueKind == JsonValueKind.String
                && int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                value = Math.Max(0, parsed);
                return true;
            }

            return false;
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }
    }
}
