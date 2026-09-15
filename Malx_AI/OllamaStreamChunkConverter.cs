using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Malx_AI
{
    /// <summary>
    /// Normalises a streaming line from a self-hosted endpoint into the OpenAI chunk shape.
    /// </summary>
    /// <remarks>
    /// "Hybrid Local" points at whatever OpenAI-compatible server the user runs, and not all of
    /// them speak Server-Sent Events. Ollama and the gateways in front of it answer a streaming
    /// request with newline-delimited JSON — one object per line, no "data:" prefix — using their
    /// own field names (<c>message.content</c>, <c>message.thinking</c>, <c>response</c>).
    /// Converting those lines to the OpenAI chunk shape keeps a single parsing path in the
    /// streaming loop instead of a second, half-maintained one.
    /// </remarks>
    public static class OllamaStreamChunkConverter
    {
        /// <summary>
        /// True when the line is a usable streaming chunk; <paramref name="openAiChunkJson"/> then
        /// holds it in OpenAI form. Lines that already carry "choices" (or a provider error) are
        /// passed through untouched.
        /// </summary>
        public static bool TryConvertLine(string? line, out string openAiChunkJson)
        {
            openAiChunkJson = string.Empty;

            string trimmed = (line ?? string.Empty).Trim();
            if (trimmed.Length == 0 || trimmed[0] != '{')
                return false;

            JsonNode? node;
            try
            {
                node = JsonNode.Parse(trimmed);
            }
            catch (JsonException)
            {
                // A fragment of a pretty-printed body, not a whole chunk. The caller falls back to
                // reading the entire response and parsing it as one document.
                return false;
            }

            if (node is not JsonObject root)
                return false;

            // Already OpenAI-shaped (or an error payload) — hand it back as-is so the existing
            // parser applies its full delta, tool-call and usage handling.
            if (root.ContainsKey("choices") || root.ContainsKey("error"))
            {
                openAiChunkJson = trimmed;
                return true;
            }

            string content = string.Empty;
            string reasoning = string.Empty;

            if (root["message"] is JsonObject message)
            {
                content = ReadString(message, "content");
                // Ollama exposes chain-of-thought as "thinking"; OpenAI-compatible forks of it use
                // "reasoning_content". Neither name is read by the OpenAI delta parser.
                reasoning = ReadString(message, "thinking");
                if (reasoning.Length == 0)
                    reasoning = ReadString(message, "reasoning_content");
                if (reasoning.Length == 0)
                    reasoning = ReadString(message, "reasoning");
            }
            else
            {
                // Native /api/generate shape.
                content = ReadString(root, "response");
                reasoning = ReadString(root, "thinking");
            }

            bool done = root["done"] is JsonValue doneValue
                && doneValue.TryGetValue(out bool doneFlag)
                && doneFlag;

            // A chunk that carries neither text nor a completion marker tells us nothing; treating
            // it as unconvertible keeps the caller free to try its whole-body fallback.
            if (content.Length == 0 && reasoning.Length == 0 && !done)
                return false;

            var delta = new JsonObject { ["role"] = "assistant" };
            if (content.Length > 0)
                delta["content"] = content;
            if (reasoning.Length > 0)
                delta["reasoning"] = reasoning;

            var choice = new JsonObject { ["index"] = 0, ["delta"] = delta };
            if (done)
                choice["finish_reason"] = ReadString(root, "done_reason") is { Length: > 0 } reason ? reason : "stop";

            var chunk = new JsonObject
            {
                ["object"] = "chat.completion.chunk",
                ["choices"] = new JsonArray { choice }
            };

            JsonObject? usage = BuildUsage(root);
            if (usage != null)
                chunk["usage"] = usage;

            openAiChunkJson = chunk.ToJsonString();
            return true;
        }

        // Native completion lines report token counts under their own names; map them so the
        // context meter and latency log stay accurate on self-hosted runs.
        private static JsonObject? BuildUsage(JsonObject root)
        {
            int promptTokens = ReadInt(root, "prompt_eval_count");
            int completionTokens = ReadInt(root, "eval_count");
            if (promptTokens <= 0 && completionTokens <= 0)
                return null;

            return new JsonObject
            {
                ["prompt_tokens"] = promptTokens,
                ["completion_tokens"] = completionTokens,
                ["total_tokens"] = promptTokens + completionTokens
            };
        }

        private static string ReadString(JsonObject source, string property)
            => source[property] is JsonValue value && value.TryGetValue(out string? text) && text != null
                ? text
                : string.Empty;

        private static int ReadInt(JsonObject source, string property)
            => source[property] is JsonValue value && value.TryGetValue(out int number) && number > 0
                ? number
                : 0;
    }
}
