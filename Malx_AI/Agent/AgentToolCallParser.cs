using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Reads a tool call out of whatever the model produced.
    /// </summary>
    /// <remarks>
    /// Two formats are accepted on purpose. A 4B+ or cloud model emits JSON reliably and JSON
    /// carries multi-line file content without escaping games. A sub-4B model does not: it drops
    /// braces, forgets to escape quotes, and trails commentary after the object. Those models are
    /// asked for a flat <c>TOOL name</c> + <c>key: value</c> block instead, which they hit far more
    /// often. Both land in the same <see cref="AgentToolCall"/>.
    /// </remarks>
    public static class AgentToolCallParser
    {
        private static readonly Regex FencedBlockRegex =
            new(@"```(?:json)?\s*(?<body>\{[\s\S]*?\})\s*```", RegexOptions.Compiled);
        /// <summary>Identifies a candidate object as a tool call rather than incidental JSON.</summary>
        private static readonly Regex BareObjectRegex =
            new(@"""(?:tool|name)""\s*:\s*""[a-z_]+""", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ToolHeaderRegex =
            new(@"(?im)^\s*(?:TOOL|ACTION)\s*[:\-]?\s*(?<tool>[a-z_]{3,30})\s*$", RegexOptions.Compiled);
        private static readonly Regex KeyValueRegex =
            new(@"(?im)^\s*(?<key>[a-z_]{2,24})\s*:\s*(?<value>.*)$", RegexOptions.Compiled);
        private static readonly Regex HeredocRegex =
            new(@"(?s)<<<\s*\r?\n(?<body>.*?)\r?\n\s*>>>", RegexOptions.Compiled);

        /// <summary>Attempts to read the next tool call. Returns false when the model is answering, not acting.</summary>
        public static bool TryParse(string? text, out AgentToolCall? call, out string? error)
        {
            call = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (TryParseJson(text, out call, out error))
                return true;

            // A malformed JSON object is a real failure worth reporting back to the model, but a
            // response with no object at all is simply a final answer.
            if (error != null)
                return false;

            return TryParseFlat(text, out call, out error);
        }

        private static readonly Regex ToolCallTagRegex =
            new(@"<tool_call>\s*(?<body>\{[\s\S]*?\})\s*</tool_call>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static bool TryParseJson(string text, out AgentToolCall? call, out string? error)
        {
            call = null;
            error = null;

            Match tagMatch = ToolCallTagRegex.Match(text);
            Match fenced = FencedBlockRegex.Match(text);
            string candidateText = tagMatch.Success
                ? tagMatch.Groups["body"].Value
                : (fenced.Success ? fenced.Groups["body"].Value : text);
            // Brace matching rather than a regex: "arguments" is itself an object, and any
            // non-greedy pattern stops at its closing brace and hands back invalid JSON.
            string? body = TryExtractJsonObject(candidateText);

            if (body == null)
            {
                // Text that was clearly reaching for a tool call but does not parse is a failure the
                // model can fix, not a final answer — returning it verbatim would print raw JSON at
                // the user.
                if (tagMatch.Success || fenced.Success || BareObjectRegex.IsMatch(text))
                    error = "That tool call was not valid JSON. Send one complete JSON object with balanced braces.";
                return false;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return false;

                string? tool = ReadString(root, "tool") ?? ReadString(root, "name");
                if (string.IsNullOrWhiteSpace(tool))
                    return false;

                var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("arguments", out JsonElement args))
                {
                    if (args.ValueKind == JsonValueKind.Object)
                    {
                        CopyProperties(args, arguments);
                    }
                    else if (args.ValueKind == JsonValueKind.String)
                    {
                        string str = args.GetString() ?? string.Empty;
                        try
                        {
                            using JsonDocument parsedArgs = JsonDocument.Parse(str);
                            if (parsedArgs.RootElement.ValueKind == JsonValueKind.Object)
                                CopyProperties(parsedArgs.RootElement, arguments);
                        }
                        catch { /* fall through to raw string */ }
                    }
                }
                else if (root.TryGetProperty("args", out JsonElement shortArgs))
                {
                    if (shortArgs.ValueKind == JsonValueKind.Object)
                    {
                        CopyProperties(shortArgs, arguments);
                    }
                    else if (shortArgs.ValueKind == JsonValueKind.String)
                    {
                        string str = shortArgs.GetString() ?? string.Empty;
                        try
                        {
                            using JsonDocument parsedArgs = JsonDocument.Parse(str);
                            if (parsedArgs.RootElement.ValueKind == JsonValueKind.Object)
                                CopyProperties(parsedArgs.RootElement, arguments);
                        }
                        catch { /* fall through to raw string */ }
                    }
                }
                else
                {
                    CopyProperties(root, arguments, skip: ["tool", "name"]);
                }

                if (!AgentToolNames.IsKnown(tool))
                {
                    error = $"'{tool}' is not an available tool. Use one of: {string.Join(", ", AgentToolNames.All)}.";
                    return false;
                }

                call = new AgentToolCall(tool!.ToLowerInvariant(), arguments);
                return true;
            }
            catch (JsonException ex)
            {
                error = "The tool call was not valid JSON: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Returns the first balanced <c>{...}</c> that names a tool, tracking string literals so a
        /// brace inside a value (a command, a file path) does not end the object early.
        /// </summary>
        private static string? TryExtractJsonObject(string text)
        {
            for (int start = text.IndexOf('{'); start >= 0; start = text.IndexOf('{', start + 1))
            {
                int depth = 0;
                bool inString = false;
                bool escaped = false;

                for (int i = start; i < text.Length; i++)
                {
                    char c = text[i];

                    if (escaped) { escaped = false; continue; }
                    if (c == '\\' && inString) { escaped = true; continue; }
                    if (c == '"') { inString = !inString; continue; }
                    if (inString) continue;

                    if (c == '{')
                        depth++;
                    else if (c == '}' && --depth == 0)
                    {
                        string candidate = text[start..(i + 1)];
                        if (BareObjectRegex.IsMatch(candidate))
                            return candidate;
                        break;
                    }
                }
            }

            return null;
        }

        private static bool TryParseFlat(string text, out AgentToolCall? call, out string? error)
        {
            call = null;
            error = null;

            Match header = ToolHeaderRegex.Match(text);
            if (!header.Success)
                return false;

            string tool = header.Groups["tool"].Value.ToLowerInvariant();
            if (!AgentToolNames.IsKnown(tool))
            {
                error = $"'{tool}' is not an available tool. Use one of: {string.Join(", ", AgentToolNames.All)}.";
                return false;
            }

            string remainder = text[(header.Index + header.Length)..];
            int terminator = remainder.IndexOf("\nEND", StringComparison.OrdinalIgnoreCase);
            if (terminator >= 0)
                remainder = remainder[..terminator];

            var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Pull heredoc bodies out first so their contents are never scanned for "key: value".
            remainder = HeredocRegex.Replace(remainder, match =>
            {
                arguments["content"] = match.Groups["body"].Value;
                return string.Empty;
            });

            foreach (Match pair in KeyValueRegex.Matches(remainder))
            {
                string key = pair.Groups["key"].Value.ToLowerInvariant();
                if (key is "tool" or "action" or "end")
                    continue;
                if (!arguments.ContainsKey(key))
                    arguments[key] = pair.Groups["value"].Value.Trim().Trim('"');
            }

            if (arguments.Count == 0 && !string.Equals(tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase))
            {
                error = $"The {tool} call had no arguments.";
                return false;
            }

            call = new AgentToolCall(tool, arguments);
            return true;
        }

        private static void CopyProperties(
            JsonElement source,
            Dictionary<string, string> target,
            IReadOnlyCollection<string>? skip = null)
        {
            foreach (JsonProperty property in source.EnumerateObject())
            {
                if (skip != null && skip.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                target[property.Name] = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                    JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                    _ => property.Value.ToString()
                };
            }
        }

        private static string? ReadString(JsonElement element, string name) =>
            element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
