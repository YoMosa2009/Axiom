using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    /// <summary>
    /// The one-line "what I'm doing now" the user sees while a model works, instead of only a
    /// list of the commands it ran.
    /// </summary>
    /// <remarks>
    /// Owner request (2026-11-26): the run card showed nothing but tool rows until the final
    /// summary, so there was no way to tell what the model thought it was doing. Claude Code and
    /// Codex show the model's own short text between tool calls; this does the same. In order of
    /// preference: the sentence the model wrote before its tool call, the latest sentence of its
    /// reasoning, then a plain description of the call itself (small local models, which are
    /// not asked to narrate).
    /// </remarks>
    public static class AgentNarration
    {
        /// <summary>Longest narration shown; about a sentence and a half.</summary>
        public const int MaxChars = 200;

        private static readonly Regex Fence = new(@"```[\s\S]*?(```|$)", RegexOptions.Compiled);
        private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex SentenceEnd = new(@"(?<=[.!?])\s+(?=[A-Z""'(\[])", RegexOptions.Compiled);
        private static readonly Regex LeadingLabel = new(@"^(status|now|next|step|plan|action|narration)\s*[:\-–]\s*", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex MarkdownNoise = new(@"(^#+\s*)|(\*\*)|(__)|(^[-*>]\s+)", RegexOptions.Compiled | RegexOptions.Multiline);

        /// <summary>
        /// The model's own words written alongside a tool call (the content before the call),
        /// cut to a sentence and a half. Null when there is nothing a user should read.
        /// </summary>
        public static string? FromModelText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string cleaned = Fence.Replace(text, " ");
            // Tool-call JSON or protocol lines written as text are not narration.
            cleaned = string.Join("\n", cleaned.Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0
                    && !line.StartsWith("{", StringComparison.Ordinal)
                    && !line.StartsWith("TOOL ", StringComparison.Ordinal)
                    && !line.Equals("END", StringComparison.Ordinal)
                    && !Regex.IsMatch(line, @"^[a-z_]+\s*:\s", RegexOptions.None)));
            cleaned = StripThinkBlocks(cleaned);
            return Shorten(cleaned);
        }

        /// <summary>
        /// The latest complete thought in the model's reasoning, when it wrote no text of its own.
        /// The end of the reasoning is what the model decided just before calling the tool.
        /// </summary>
        public static string? FromReasoning(string? reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning))
                return null;

            string flat = Whitespace.Replace(Fence.Replace(reasoning, " "), " ").Trim();
            if (flat.Length == 0)
                return null;

            // Look at the last few sentences only; the start of a long reasoning trace is about
            // understanding the task, not about the step being taken now.
            string tail = flat.Length > 1200 ? flat[^1200..] : flat;
            List<string> sentences = SentenceEnd.Split(tail)
                .Select(s => s.Trim())
                .Where(IsReadableSentence)
                .ToList();
            if (sentences.Count == 0)
                return null;

            // Prefer the last sentence that says what will be done ("I'll", "Let me", "Now", ...).
            string? intent = sentences.LastOrDefault(LooksLikeIntent);
            return Shorten(intent ?? sentences[^1]);
        }

        /// <summary>A plain description of the call, for models that wrote nothing.</summary>
        public static string FromCall(AgentToolCall call)
        {
            ArgumentNullException.ThrowIfNull(call);
            string Quote(string value, int max)
            {
                string flat = Whitespace.Replace(value ?? string.Empty, " ").Trim();
                return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
            }
            string FileName(string path)
            {
                string trimmed = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
                int slash = trimmed.LastIndexOf('/');
                return Quote(slash >= 0 ? trimmed[(slash + 1)..] : trimmed, 40);
            }

            switch (call.Tool.ToLowerInvariant())
            {
                case AgentToolNames.RunCommand:
                    return "Running a command: " + Quote(call.Arg("command"), 70);
                case AgentToolNames.ReadFile:
                    return "Reading " + FileName(call.Arg("path"));
                case AgentToolNames.WriteFile:
                    return "Writing " + FileName(call.Arg("path"));
                case AgentToolNames.AppendFile:
                    return "Adding more to " + FileName(call.Arg("path"));
                case AgentToolNames.EditFile:
                    return "Editing " + FileName(call.Arg("path"));
                case AgentToolNames.ListDirectory:
                    return "Looking at what is in " + FileName(call.Arg("path", "the working folder"));
                case AgentToolNames.FindFiles:
                    return "Looking for files matching " + Quote(call.Arg("pattern"), 40);
                case AgentToolNames.SearchText:
                    return "Searching the files for " + Quote(call.Arg("pattern"), 40);
                case AgentToolNames.StartBackgroundTask:
                    return "Starting a background task: " + Quote(call.Arg("title", call.Arg("command")), 60);
                case AgentToolNames.CheckBackgroundTask:
                    return "Checking on a background task";
                case AgentToolNames.StopBackgroundTask:
                    return "Stopping a background task";
                case AgentToolNames.TakeScreenshot:
                    return "Taking a screenshot to check the result";
                case AgentToolNames.UpdatePlan:
                    return "Updating the plan";
                case AgentToolNames.RecallContext:
                    return "Looking back at earlier work";
                case AgentToolNames.UseSkill:
                    return "Reading the " + Quote(call.Arg("name"), 40) + " skill";
                case AgentToolNames.Finish:
                    return "Wrapping up";
                case AgentToolNames.BrowserControl:
                    string action = call.Arg("action", "open").Trim().ToLowerInvariant();
                    string url = Quote(call.Arg("url"), 60);
                    return action switch
                    {
                        "search" => "Searching the web for " + url,
                        "open" or "navigate" => url.Length > 0 ? "Opening " + url : "Opening a page",
                        "read" or "read_page" or "fetch" => url.Length > 0 ? "Reading " + url : "Reading the open page",
                        "inspect" => "Looking at the controls on the page",
                        "click" => "Clicking a control on the page",
                        "fill" => "Filling in a field on the page",
                        "download" => "Downloading " + url,
                        "install" => "Installing " + url,
                        _ => "Using the browser"
                    };
                default:
                    return "Working on the next step";
            }
        }

        /// <summary>
        /// Narration from a text-protocol reply (local models): the words before the tool call,
        /// else the end of a &lt;think&gt; block. The call itself is never narration.
        /// </summary>
        public static string? FromProtocolReply(string? reply)
        {
            if (string.IsNullOrWhiteSpace(reply))
                return null;

            string text = reply;
            string? reasoning = null;
            Match think = Regex.Match(text, @"<think>([\s\S]*?)</think>", RegexOptions.IgnoreCase);
            if (think.Success)
            {
                reasoning = think.Groups[1].Value;
                text = text.Remove(think.Index, think.Length);
            }
            else
            {
                int close = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (close >= 0)
                {
                    reasoning = text[..close];
                    text = text[(close + "</think>".Length)..];
                }
            }

            int cut = text.Length;
            foreach (string marker in new[] { "```", "TOOL ", "{\"tool\"", "{ \"tool\"" })
            {
                int index = text.IndexOf(marker, StringComparison.Ordinal);
                if (index >= 0 && index < cut)
                    cut = index;
            }
            return FromModelText(text[..cut]) ?? FromReasoning(reasoning);
        }

        /// <summary>A plain description of a Normal chat tool call (web_search, run_python, MCP tools).</summary>
        public static string FromChatTool(string? toolName, string? argumentsJson)
        {
            string name = (toolName ?? string.Empty).Trim();
            string Argument(string key)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
                    return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                        && doc.RootElement.TryGetProperty(key, out var value)
                        && value.ValueKind == System.Text.Json.JsonValueKind.String
                        ? Whitespace.Replace(value.GetString() ?? string.Empty, " ").Trim()
                        : string.Empty;
                }
                catch (System.Text.Json.JsonException)
                {
                    return string.Empty;
                }
            }
            string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)].TrimEnd() + "…";

            return name.ToLowerInvariant() switch
            {
                "web_search" => Argument("query") is { Length: > 0 } query ? "Searching the web for " + Clip(query, 80) : "Searching the web",
                "run_python" => "Running Python to work this out",
                "run_java" => "Running Java to check the code",
                "calculate" => Argument("expression") is { Length: > 0 } expression ? "Calculating " + Clip(expression, 60) : "Calculating",
                "recall_context" => "Looking back at earlier parts of the conversation",
                "" => "Working on it",
                _ => "Using " + name.Replace('_', ' ')
            };
        }

        /// <summary>
        /// The model's text written alongside a tool call, cleaned for the user but NOT shortened:
        /// think blocks, tool-call JSON and protocol lines removed, paragraphs kept. Null when
        /// nothing readable is left.
        /// </summary>
        public static string? ForUser(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            string cleaned = StripThinkBlocks(text);
            cleaned = string.Join("\n", cleaned.Replace("\r\n", "\n").Split('\n')
                .Where(line =>
                {
                    string t = line.Trim();
                    return !t.StartsWith("{\"", StringComparison.Ordinal)
                        && !t.StartsWith("TOOL ", StringComparison.Ordinal)
                        && !t.Equals("END", StringComparison.Ordinal);
                }));
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n").Trim();
            return cleaned.Count(char.IsLetter) < 4 ? null : cleaned;
        }

        /// <summary>
        /// True when the model's words should be shown to the user in full rather than folded
        /// into the one-line narration: always right after the user wrote mid-run (it is the
        /// answer to them), otherwise when the narration would have had to cut it short.
        /// </summary>
        public static bool ShouldShowInFull(string? said, bool answeringUser)
        {
            if (string.IsNullOrWhiteSpace(said))
                return false;
            if (answeringUser)
                return said.Count(char.IsLetter) >= 4;
            string flat = Whitespace.Replace(said, " ").Trim();
            return flat.Length > MaxChars || (said.Contains('\n') && flat.Length > 120);
        }

        /// <summary>Picks the best narration for a step: the model's text, its reasoning, then the call.</summary>
        public static string Pick(string? modelText, string? reasoning, AgentToolCall call)
            => FromModelText(modelText) ?? FromReasoning(reasoning) ?? FromCall(call);

        private static bool IsReadableSentence(string sentence)
        {
            if (sentence.Length < 12 || sentence.Length > 400)
                return false;
            int letters = sentence.Count(char.IsLetter);
            if (letters < sentence.Length * 0.6)
                return false;
            // Code, paths and JSON fragments are not something to show as "what I'm doing".
            return !sentence.Contains('{') && !sentence.Contains("();", StringComparison.Ordinal)
                && !sentence.Contains("=>", StringComparison.Ordinal);
        }

        private static bool LooksLikeIntent(string sentence)
        {
            string lower = sentence.ToLowerInvariant();
            return lower.StartsWith("i'll ", StringComparison.Ordinal) || lower.StartsWith("i will ", StringComparison.Ordinal)
                || lower.StartsWith("let me ", StringComparison.Ordinal) || lower.StartsWith("let's ", StringComparison.Ordinal)
                || lower.StartsWith("now ", StringComparison.Ordinal) || lower.StartsWith("next", StringComparison.Ordinal)
                || lower.StartsWith("i need to ", StringComparison.Ordinal) || lower.StartsWith("i should ", StringComparison.Ordinal)
                || lower.StartsWith("i'm going to ", StringComparison.Ordinal) || lower.StartsWith("first", StringComparison.Ordinal)
                || lower.StartsWith("then ", StringComparison.Ordinal) || lower.StartsWith("so i", StringComparison.Ordinal);
        }

        private static string StripThinkBlocks(string text)
        {
            string result = Regex.Replace(text, @"<think>[\s\S]*?</think>", " ", RegexOptions.IgnoreCase);
            int close = result.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            return close >= 0 ? result[(close + "</think>".Length)..] : result;
        }

        /// <summary>The first sentence, plus the second while it fits: about a sentence and a half.</summary>
        internal static string? Shorten(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            string flat = MarkdownNoise.Replace(text, string.Empty);
            flat = Whitespace.Replace(flat, " ").Trim();
            flat = LeadingLabel.Replace(flat, string.Empty).Trim();
            flat = flat.Replace("`", string.Empty, StringComparison.Ordinal);
            if (flat.Length < 6 || flat.Count(char.IsLetter) < 4)
                return null;

            string[] sentences = SentenceEnd.Split(flat);
            string result = sentences[0].Trim();
            if (sentences.Length > 1 && result.Length + 1 + sentences[1].Length <= MaxChars)
                result += " " + sentences[1].Trim();
            if (result.Length > MaxChars)
            {
                int cut = result.LastIndexOf(' ', MaxChars - 1);
                result = result[..(cut > MaxChars / 2 ? cut : MaxChars - 1)].TrimEnd(',', ';', ':', ' ') + "…";
            }
            return result;
        }
    }
}
