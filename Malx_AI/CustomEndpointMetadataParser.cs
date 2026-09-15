using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Malx_AI
{
    internal readonly record struct CustomEndpointMetadata(
        int? ContextWindowTokens,
        bool? SupportsImageInput,
        string? MatchedModelId);

    internal static class CustomEndpointMetadataParser
    {
        // Ordered by how well the field predicts what the server will ACTUALLY serve, best first.
        // A document often carries several of these at once — LM Studio reports both the model's
        // ceiling and the window it is currently loaded with — and the trained capacity is
        // routinely 16x the served one. Taking the largest match would declare a window the server
        // cannot honour, which is the failure this ordering exists to prevent.
        private static readonly string[][] ContextPropertyTiers =
        [
            // Tier 0 — the window this server is running with right now.
            ["loaded_context_length", "n_ctx", "num_ctx", "context_size"],
            // Tier 1 — the ceiling this engine was configured to allow.
            ["max_model_len", "max_seq_len", "max_context_length", "max_sequence_length", "context_window"],
            // Tier 2 — a catalog figure that may or may not match the loaded runtime.
            ["context_length"],
            // Tier 3 — the architecture's trained capacity. Almost never the served window; used
            // only when a server reports nothing better at all.
            ["n_ctx_train", "train_context_length", "model_max_length", "max_position_embeddings"]
        ];

        public static CustomEndpointMetadata Parse(string? json, string? requestedModelId)
        {
            if (string.IsNullOrWhiteSpace(json))
                return default;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return Parse(document.RootElement, requestedModelId);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        public static CustomEndpointMetadata Parse(JsonElement root, string? requestedModelId)
        {
            JsonElement? matchedModel = FindMatchingModel(root, requestedModelId);
            int? context = null;
            bool? vision = null;
            string? matchedId = requestedModelId;

            if (matchedModel is JsonElement modelElement)
            {
                context = ReadContextWindow(modelElement);
                vision = ReadVisionFlag(modelElement);
                matchedId = ReadModelId(modelElement) ?? requestedModelId;
            }

            context ??= ReadContextWindow(root);
            vision ??= ReadVisionFlag(root);

            if (vision != true && LooksLikeVisionModel(matchedId ?? requestedModelId))
                vision = true;

            return new CustomEndpointMetadata(context, vision, matchedId);
        }

        /// <summary>
        /// Reads a context window from a response whose meaning comes from the endpoint rather
        /// than from any field name -- KoboldCpp answers /api/v1/config/max_context_length with a
        /// bare {"value": N}. Only call this for a probe URL that is known to mean exactly that.
        /// </summary>
        public static bool TryParseScalarContextWindow(string? json, out int tokens)
        {
            tokens = 0;
            if (string.IsNullOrWhiteSpace(json))
                return false;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;
                if (TryReadPositiveInt(root, out tokens))
                    return true;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    foreach (string name in new[] { "value", "result", "max_context_length" })
                    {
                        if (root.TryGetProperty(name, out JsonElement value) && TryReadPositiveInt(value, out tokens))
                            return true;
                    }
                }
            }
            catch (JsonException)
            {
            }

            return false;
        }

        public static bool LooksLikeVisionModel(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return false;

            string text = modelId.Trim();
            return text.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
                || text.Contains("gemma4", StringComparison.OrdinalIgnoreCase)
                || text.Contains("gemma_4", StringComparison.OrdinalIgnoreCase)
                || text.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || text.Contains("-vl", StringComparison.OrdinalIgnoreCase)
                || text.Contains("vl-", StringComparison.OrdinalIgnoreCase)
                || text.Contains("llava", StringComparison.OrdinalIgnoreCase)
                || text.Contains("pixtral", StringComparison.OrdinalIgnoreCase)
                || text.Contains("qwen2.5-vl", StringComparison.OrdinalIgnoreCase)
                || text.Contains("qwen3-vl", StringComparison.OrdinalIgnoreCase)
                || text.Contains("multimodal", StringComparison.OrdinalIgnoreCase);
        }

        public static int ClampContextWindow(int tokens)
        {
            return Math.Clamp(tokens, 2048, 1_048_576);
        }

        private static JsonElement? FindMatchingModel(JsonElement root, string? requestedModelId)
        {
            if (TryGetArray(root, "data", out JsonElement data))
            {
                JsonElement? match = FindInModelArray(data, requestedModelId);
                if (match != null)
                    return match;
            }

            if (TryGetArray(root, "models", out JsonElement models))
            {
                JsonElement? match = FindInModelArray(models, requestedModelId);
                if (match != null)
                    return match;
            }

            if (!string.IsNullOrWhiteSpace(ReadModelId(root)))
                return root;

            return null;
        }

        private static JsonElement? FindInModelArray(JsonElement array, string? requestedModelId)
        {
            JsonElement? first = null;
            foreach (JsonElement item in array.EnumerateArray())
            {
                first ??= item;
                if (ModelIdsMatch(ReadModelId(item), requestedModelId))
                    return item;
            }

            return first;
        }

        private static bool ModelIdsMatch(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                return false;

            string a = NormalizeModelId(left);
            string b = NormalizeModelId(right);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
                || a.EndsWith("/" + b, StringComparison.OrdinalIgnoreCase)
                || b.EndsWith("/" + a, StringComparison.OrdinalIgnoreCase)
                || a.StartsWith(b + ":", StringComparison.OrdinalIgnoreCase)
                || b.StartsWith(a + ":", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeModelId(string modelId)
        {
            string trimmed = modelId.Trim();
            int slash = trimmed.LastIndexOf('/');
            if (slash >= 0 && slash < trimmed.Length - 1)
                trimmed = trimmed[(slash + 1)..];
            return trimmed;
        }

        private static string? ReadModelId(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return null;

            foreach (string name in new[] { "id", "name", "model", "model_name" })
            {
                if (element.TryGetProperty(name, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }

            return null;
        }

        private static int? ReadContextWindow(JsonElement element)
        {
            // Best (lowest) tier wins outright; ties inside one tier take the largest value.
            int bestTier = int.MaxValue;
            int? best = null;
            Walk(element, (name, value) =>
            {
                int tier = GetContextPropertyTier(name);
                if (tier < 0 || tier > bestTier)
                    return;

                if (!TryReadPositiveInt(value, out int tokens))
                    return;

                if (tier < bestTier)
                {
                    bestTier = tier;
                    best = tokens;
                    return;
                }

                best = best == null ? tokens : Math.Max(best.Value, tokens);
            });
            return best;
        }

        private static bool? ReadVisionFlag(JsonElement element)
        {
            bool found = false;
            Walk(element, (name, value) =>
            {
                if (found)
                    return;

                if (name.Contains("input_modalit", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "modalities", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "capability", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "capabilities", StringComparison.OrdinalIgnoreCase)
                    // LM Studio's native catalog states the kind of model directly ("vlm" vs "llm").
                    || string.Equals(name, "type", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "model_type", StringComparison.OrdinalIgnoreCase))
                {
                    if (ContainsImageToken(value))
                        found = true;
                }
                else if (string.Equals(name, "vision", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "multimodal", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "mmproj", StringComparison.OrdinalIgnoreCase))
                {
                    if (value.ValueKind == JsonValueKind.True
                        || (value.ValueKind == JsonValueKind.String
                            && ContainsImageToken(value)))
                    {
                        found = true;
                    }
                }
            });

            return found ? true : null;
        }

        private static bool ContainsImageToken(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    string text = value.GetString() ?? string.Empty;
                    return text.Contains("image", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("vision", StringComparison.OrdinalIgnoreCase)
                        || text.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("vlm", StringComparison.OrdinalIgnoreCase);
                case JsonValueKind.Array:
                    return value.EnumerateArray().Any(ContainsImageToken);
                case JsonValueKind.Object:
                    foreach (JsonProperty property in value.EnumerateObject())
                    {
                        if (ContainsImageToken(property.Value))
                            return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        /// <summary>Tier of a context-window property name, or -1 when it is not one.</summary>
        private static int GetContextPropertyTier(string name)
        {
            for (int tier = 0; tier < ContextPropertyTiers.Length; tier++)
            {
                foreach (string candidate in ContextPropertyTiers[tier])
                {
                    if (string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))
                        return tier;
                    // Namespaced forms such as Ollama's "gemma3.context_length" in model_info.
                    if (name.EndsWith("." + candidate, StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith("_" + candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return tier;
                    }
                }
            }

            return -1;
        }

        private static bool TryReadPositiveInt(JsonElement value, out int tokens)
        {
            tokens = 0;
            try
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (value.TryGetInt64(out long longValue) && longValue >= 2048)
                        {
                            tokens = (int)Math.Clamp(longValue, 2048, 1_048_576);
                            return true;
                        }
                        if (value.TryGetDouble(out double doubleValue) && doubleValue >= 2048)
                        {
                            tokens = (int)Math.Clamp(Math.Round(doubleValue), 2048, 1_048_576);
                            return true;
                        }
                        return false;
                    case JsonValueKind.String:
                        string text = (value.GetString() ?? string.Empty).Trim();
                        if (text.EndsWith("k", StringComparison.OrdinalIgnoreCase)
                            && double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out double kilo)
                            && kilo > 0)
                        {
                            tokens = ClampContextWindow((int)Math.Round(kilo * 1024d));
                            return tokens >= 2048;
                        }
                        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
                            && parsed >= 2048)
                        {
                            tokens = ClampContextWindow((int)parsed);
                            return true;
                        }
                        return false;
                    default:
                        return false;
                }
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetArray(JsonElement root, string name, out JsonElement array)
        {
            array = default;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(name, out JsonElement value)
                && value.ValueKind == JsonValueKind.Array)
            {
                array = value;
                return true;
            }

            return false;
        }

        private static void Walk(JsonElement element, Action<string, JsonElement> visitor, string prefix = "")
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        string name = string.IsNullOrEmpty(prefix) ? property.Name : prefix + "." + property.Name;
                        visitor(property.Name, property.Value);
                        visitor(name, property.Value);
                        Walk(property.Value, visitor, name);
                    }
                    break;
                case JsonValueKind.Array:
                    foreach (JsonElement item in element.EnumerateArray())
                        Walk(item, visitor, prefix);
                    break;
            }
        }
    }
}
