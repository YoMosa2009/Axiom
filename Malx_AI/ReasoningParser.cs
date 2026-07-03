using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public class ReasoningParser
    {
        public const string FinalAnswerDelimiter = "=== FINAL ANSWER ===";
        private static readonly Regex QwenThinkBlockRegex = new(@"<think>\s*(?<content>[\s\S]*?)\s*</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // GPT-OSS "harmony" channel format. When the GGUF template's special tokens survive into
        // the text stream the final answer is announced by <|channel|>final<|message|>; when the
        // decoder skips special tokens the same structure degrades to the bare-text signature
        // "analysis...assistantfinal..." — both must be recognized or the whole chain-of-thought
        // is treated as the answer.
        private static readonly Regex HarmonyFinalMarkerRegex = new(@"<\|channel\|>\s*final\s*<\|message\|>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HarmonyAnalysisSegmentRegex = new(@"<\|channel\|>\s*(?:analysis|commentary)\s*<\|message\|>(?<t>[\s\S]*?)(?=<\|end\|>|<\|start\|>|<\|channel\|>|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex HarmonyBareTextRegex = new(@"^\s*(?:assistant)?analysis(?<t>[\s\S]*?)assistant\s*final\s*(?<a>[\s\S]*)$", RegexOptions.Compiled);

        // A codebase patch envelope is a byte-exact deliverable: patched file content may itself
        // legitimately contain <think>/</think> text (any file that deals with LLM output does).
        // Reasoning extraction must therefore never run over envelope bytes — only over the text
        // BEFORE a line-leading envelope marker.
        private const string CodebasePatchEnvelopeMarker = "[[AXIOM_CODEBASE_PATCH]]";
        private static readonly Regex HarmonyStructuralTokenRegex = new(@"<\|(?:start|end|message|channel|return|call|constrain)\|>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Reasoning-tag dialects seen across local GGUF families, normalized to canonical
        // <think>/</think> before parsing. Each entry is (open, close). Kept to unambiguous
        // reasoning markers only — never generic words — so deliverable content is not rewritten.
        private static readonly (string Open, string Close)[] ThinkTagAliases =
        {
            ("<thinking>", "</thinking>"),                       // various fine-tunes
            ("<thought>", "</thought>"),                         // EXAONE Deep
            ("<reasoning>", "</reasoning>"),                     // reasoning fine-tunes
            ("[THINK]", "[/THINK]"),                             // Magistral
            ("<seed:think>", "</seed:think>"),                   // Seed-OSS
            ("◁think▷", "◁/think▷"),                             // Kimi
            ("<|begin_of_thought|>", "<|end_of_thought|>"),      // OpenThoughts family
        };

        public class ParsedResponse
        {
            public string ThinkingContent { get; set; }
            public string Answer { get; set; }
            public bool HasThinking { get; set; }

            // True when Answer was populated purely from the model's reasoning channel because no final
            // content was emitted. Such content is chain-of-thought prose, never a real artifact, so the
            // Project Canvas path must refuse to render it. See RunCloudCouncilRoleToolLoopAsync.
            public bool IsReasoningFallback { get; set; }

            // True when the model opened a reasoning block and never closed it — it burned its whole
            // generation budget (or hit an anti-prompt) while still thinking. Callers can use this to
            // retry with an explicit "keep reasoning short" instruction or a larger budget instead of
            // a generic format retry that will fail the same way.
            public bool TruncatedInsideThinking { get; set; }
        }

        // Rewrites known reasoning-tag dialects to canonical <think>/</think> so one parser
        // handles every family. Case-insensitive; ordinal (no culture surprises).
        public static string NormalizeReasoningMarkers(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content ?? string.Empty;

            string normalized = content;
            foreach (var (open, close) in ThinkTagAliases)
            {
                // Cheap containment probe before the replace calls; most outputs have no aliases.
                if (normalized.Contains(open, StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(close, StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Replace(open, "<think>", StringComparison.OrdinalIgnoreCase);
                    normalized = normalized.Replace(close, "</think>", StringComparison.OrdinalIgnoreCase);
                }
            }

            // OpenThoughts wraps the real answer in solution markers; they are presentation-only.
            normalized = normalized.Replace("<|begin_of_solution|>", string.Empty, StringComparison.OrdinalIgnoreCase);
            normalized = normalized.Replace("<|end_of_solution|>", string.Empty, StringComparison.OrdinalIgnoreCase);
            return normalized;
        }

        public static string StripThinkTags(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            // Envelope guard (fail-closed): never rewrite anything at or after a codebase patch
            // envelope marker — patched file content may legitimately contain think-tag text.
            int envelopeIdx = content.IndexOf(CodebasePatchEnvelopeMarker, StringComparison.OrdinalIgnoreCase);
            if (envelopeIdx > 0)
                return (StripThinkTags(content[..envelopeIdx]) + content[envelopeIdx..]).TrimStart();
            if (envelopeIdx == 0)
                return content.TrimStart();

            string cleaned = NormalizeReasoningMarkers(content);
            cleaned = QwenThinkBlockRegex.Replace(cleaned, string.Empty);

            // Closing-only </think>: R1-style templates pre-open the think block inside the PROMPT,
            // so the model's output stream is "reasoning...</think>answer" with no opening tag.
            // Removing just the literal tag (the old behavior) leaked the entire chain-of-thought
            // into the answer. Everything before the first closer is reasoning.
            int closeIdx = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (closeIdx >= 0)
            {
                int openIdx = cleaned.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (openIdx < 0 || openIdx > closeIdx)
                    cleaned = cleaned[(closeIdx + "</think>".Length)..];
            }

            cleaned = cleaned.Replace("<think>", string.Empty, StringComparison.OrdinalIgnoreCase);
            cleaned = cleaned.Replace("</think>", string.Empty, StringComparison.OrdinalIgnoreCase);
            return cleaned.TrimStart();
        }

        public static ParsedResponse Parse(string content)
        {
            return Parse(content, false);
        }

        public static ParsedResponse ParseFinalAnswerDelimited(string content)
        {
            return ParseFinalAnswerDelimited(content, FinalAnswerDelimiter);
        }

        public static ParsedResponse ParseFinalAnswerDelimited(string content, string delimiter)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return new ParsedResponse
                {
                    ThinkingContent = "",
                    Answer = content?.Trim() ?? string.Empty,
                    HasThinking = false
                };
            }

            int delimiterIndex = string.IsNullOrWhiteSpace(delimiter)
                ? -1
                : content.IndexOf(delimiter, StringComparison.Ordinal);
            if (delimiterIndex < 0)
            {
                // The model ignored the delimiter protocol. Fall back to native reasoning-tag
                // parsing instead of returning raw chain-of-thought as the answer — reasoning
                // models routinely answer with their own <think> dialect regardless of protocol.
                return Parse(content);
            }

            string thinking = content[..delimiterIndex].Trim();
            string answerPart = content[(delimiterIndex + delimiter.Length)..].Trim();

            // The post-delimiter answer can still carry native think blocks; run it through the
            // normal parser so those never leak into the final answer.
            ParsedResponse answerParsed = Parse(answerPart);
            string answer = answerParsed.Answer;
            if (!string.IsNullOrWhiteSpace(answerParsed.ThinkingContent))
            {
                thinking = string.IsNullOrWhiteSpace(thinking)
                    ? answerParsed.ThinkingContent
                    : thinking + "\n" + answerParsed.ThinkingContent;
            }

            var result = new ParsedResponse
            {
                ThinkingContent = thinking,
                Answer = string.IsNullOrWhiteSpace(answer) ? content.Trim() : answer,
                HasThinking = !string.IsNullOrWhiteSpace(thinking) && !string.IsNullOrWhiteSpace(answer)
            };
            return result;
        }

        public static ParsedResponse Parse(string content, bool parseGemmaChannel)
        {
            var result = new ParsedResponse
            {
                ThinkingContent = "",
                Answer = StripThinkTags(content),
                HasThinking = false
            };

            if (string.IsNullOrEmpty(content))
                return result;

            // Envelope guard: when a line-leading [[AXIOM_CODEBASE_PATCH]] marker is present,
            // reasoning extraction runs on the text BEFORE the envelope only and the envelope is
            // carried through verbatim. Without this, an envelope whose patched file content
            // contains think-tag text would be rewritten/mangled by the strip passes.
            int envelopeIdx = FindLeadingLineMarkerIndex(content, CodebasePatchEnvelopeMarker);
            if (envelopeIdx < 0)
            {
                // No line-leading marker, but the envelope may be glued mid-line after prose
                // ("Here is the patch: [[AXIOM_CODEBASE_PATCH]]…"). Only accept the mid-line
                // occurrence when what follows actually looks like an envelope body, so a mere
                // MENTION of the marker inside reasoning prose does not hijack the answer.
                int anyIdx = content.IndexOf(CodebasePatchEnvelopeMarker, StringComparison.OrdinalIgnoreCase);
                if (anyIdx >= 0)
                {
                    string probe = content[anyIdx..Math.Min(content.Length, anyIdx + 400)];
                    if (probe.Contains("FILE:", StringComparison.OrdinalIgnoreCase)
                        || content.IndexOf("[[END AXIOM_CODEBASE_PATCH]]", anyIdx, StringComparison.OrdinalIgnoreCase) >= 0)
                        envelopeIdx = anyIdx;
                }
            }
            if (envelopeIdx >= 0)
            {
                string envelopeTail = content[envelopeIdx..].Trim();
                // Anything after the LAST end sentinel is trailing junk (turn terminators,
                // harmony <|return|>, narration) — never deliverable bytes. Using the last
                // occurrence keeps an end-sentinel string inside patched file content safe.
                int endSentinelIdx = envelopeTail.LastIndexOf("[[END AXIOM_CODEBASE_PATCH]]", StringComparison.OrdinalIgnoreCase);
                if (endSentinelIdx >= 0)
                    envelopeTail = envelopeTail[..(endSentinelIdx + "[[END AXIOM_CODEBASE_PATCH]]".Length)];
                if (envelopeIdx == 0)
                {
                    result.Answer = envelopeTail;
                    return result;
                }

                // The head cannot contain another line-leading marker (this is the first), so the
                // recursion runs plain reasoning extraction over it.
                ParsedResponse headParsed = Parse(content[..envelopeIdx], parseGemmaChannel);
                string headAnswer = headParsed.IsReasoningFallback ? string.Empty : headParsed.Answer?.Trim() ?? string.Empty;
                result.ThinkingContent = headParsed.ThinkingContent;
                result.HasThinking = headParsed.HasThinking;
                // Even if the head was an unclosed think block, the model DID deliver the
                // envelope — never report this as thinking-consumed-the-budget.
                result.TruncatedInsideThinking = false;
                result.Answer = string.IsNullOrWhiteSpace(headAnswer)
                    ? envelopeTail
                    : headAnswer + "\n" + envelopeTail;
                return result;
            }

            if (parseGemmaChannel)
            {
                // Gemma 4 thinking channel format:
                // <|channel|>thought\n ... <|/channel|>
                const string gemmaOpen = "<|channel|>thought";
                const string gemmaClose = "<|/channel|>";

                int gemmaOpenIndex = content.IndexOf(gemmaOpen, StringComparison.OrdinalIgnoreCase);
                int gemmaCloseIndex = gemmaOpenIndex >= 0
                    ? content.IndexOf(gemmaClose, gemmaOpenIndex, StringComparison.OrdinalIgnoreCase)
                    : -1;

                if (gemmaOpenIndex >= 0 && gemmaCloseIndex >= gemmaOpenIndex)
                {
                    int thinkingStart = gemmaOpenIndex + gemmaOpen.Length;
                    string thinking = content[thinkingStart..gemmaCloseIndex];
                    thinking = thinking.TrimStart('\r', '\n').TrimEnd();

                    int answerStart = gemmaCloseIndex + gemmaClose.Length;
                    string answer = answerStart < content.Length
                        ? content[answerStart..]
                        : string.Empty;

                    result.HasThinking = !string.IsNullOrWhiteSpace(thinking);
                    result.ThinkingContent = thinking;
                    result.Answer = answer.Trim();
                    if (string.IsNullOrWhiteSpace(result.Answer) && !string.IsNullOrWhiteSpace(thinking))
                    {
                        result.Answer = thinking;
                        result.IsReasoningFallback = true;
                    }
                    return result;
                }
            }

            if (TryParseHarmonyChannels(content, result))
                return FinishParse(result);

            string normalized = NormalizeReasoningMarkers(content);

            var closedBlocks = QwenThinkBlockRegex.Matches(normalized);
            if (closedBlocks.Count > 0)
            {
                // Remove EVERY closed think block from the answer — a model that re-enters
                // thinking mid-response used to leak its second block verbatim because only the
                // first was stripped.
                var thinkingParts = new StringBuilder();
                foreach (Match block in closedBlocks)
                {
                    string part = block.Groups["content"].Value.Trim();
                    if (part.Length == 0)
                        continue;
                    if (thinkingParts.Length > 0)
                        thinkingParts.Append('\n');
                    thinkingParts.Append(part);
                }

                string answer = QwenThinkBlockRegex.Replace(normalized, string.Empty).TrimStart();

                // Trailing re-entry: "...</think>answer<think>more reasoning[cut off]".
                int orphanOpen = answer.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (orphanOpen >= 0 && answer.IndexOf("</think>", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string orphanThinking = answer[(orphanOpen + "<think>".Length)..].Trim();
                    if (orphanThinking.Length > 0)
                    {
                        if (thinkingParts.Length > 0)
                            thinkingParts.Append('\n');
                        thinkingParts.Append(orphanThinking);
                    }
                    answer = answer[..orphanOpen];
                    result.TruncatedInsideThinking = true;
                }

                string thinkingText = thinkingParts.ToString().Trim();
                result.HasThinking = !string.IsNullOrWhiteSpace(thinkingText);
                result.ThinkingContent = thinkingText;
                result.Answer = answer.Trim();
                return FinishParse(result);
            }

            // Closing-only </think>: the chat template opened the think block inside the prompt
            // (DeepSeek-R1 distills, Qwen3-Thinking, QwQ variants), so the output contains only
            // the closer. Everything before it is chain-of-thought.
            int closeOnlyIdx = normalized.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (closeOnlyIdx >= 0)
            {
                string thinking = normalized[..closeOnlyIdx].Trim();
                string answer = normalized[(closeOnlyIdx + "</think>".Length)..].Trim();

                // Re-entry after the closer: "...</think>answer<think>more reasoning[cut off]".
                int reentryOpen = answer.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (reentryOpen >= 0 && answer.IndexOf("</think>", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string reentryThinking = answer[(reentryOpen + "<think>".Length)..].Trim();
                    if (reentryThinking.Length > 0)
                        thinking = string.IsNullOrWhiteSpace(thinking) ? reentryThinking : thinking + "\n" + reentryThinking;
                    answer = answer[..reentryOpen].TrimEnd();
                    result.TruncatedInsideThinking = true;
                }

                result.HasThinking = !string.IsNullOrWhiteSpace(thinking);
                result.ThinkingContent = thinking;
                result.Answer = answer;
                return FinishParse(result);
            }

            // Unclosed <think> block: the model hit its token budget (or an anti-prompt) before
            // emitting </think>, so the regex above never matches and StripThinkTags only removed
            // the literal tag — leaving the entire chain-of-thought as the "answer". That raw
            // reasoning then flowed into chat/canvas as if it were final content. Treat everything
            // after the orphan <think> as thinking instead.
            int openIdx = normalized.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openIdx >= 0)
            {
                string before = normalized[..openIdx].Trim();
                string thinking = normalized[(openIdx + "<think>".Length)..].Trim();

                result.HasThinking = !string.IsNullOrWhiteSpace(thinking);
                result.ThinkingContent = thinking;
                result.Answer = before;
                result.TruncatedInsideThinking = true;
            }

            return FinishParse(result);
        }

        // Reasoning-only output: no final content survived parsing. Mirror the cloud path's
        // behavior — surface the reasoning so chat roles still show something, but flag it so
        // the Project Canvas refuses to render chain-of-thought as an artifact.
        private static ParsedResponse FinishParse(ParsedResponse result)
        {
            if (string.IsNullOrWhiteSpace(result.Answer) && !string.IsNullOrWhiteSpace(result.ThinkingContent))
            {
                result.Answer = result.ThinkingContent;
                result.IsReasoningFallback = true;
            }

            return result;
        }

        private static bool TryParseHarmonyChannels(string content, ParsedResponse result)
        {
            Match finalMarker = HarmonyFinalMarkerRegex.Match(content);
            if (finalMarker.Success)
            {
                // Use the LAST final marker — earlier ones can appear inside quoted reasoning.
                Match last = finalMarker;
                for (Match m = finalMarker.NextMatch(); m.Success; m = m.NextMatch())
                    last = m;

                string answer = content[(last.Index + last.Length)..];
                int answerEnd = IndexOfAny(answer, new[] { "<|return|>", "<|end|>", "<|start|>", "<|call|>" });
                if (answerEnd >= 0)
                    answer = answer[..answerEnd];

                var thinkingParts = new StringBuilder();
                foreach (Match segment in HarmonyAnalysisSegmentRegex.Matches(content[..last.Index]))
                {
                    string part = segment.Groups["t"].Value.Trim();
                    if (part.Length == 0)
                        continue;
                    if (thinkingParts.Length > 0)
                        thinkingParts.Append('\n');
                    thinkingParts.Append(part);
                }

                string thinking = thinkingParts.Length > 0
                    ? thinkingParts.ToString()
                    : HarmonyStructuralTokenRegex.Replace(content[..last.Index], string.Empty)
                        .Replace("assistant", string.Empty, StringComparison.Ordinal)
                        .Trim();

                result.ThinkingContent = thinking.Trim();
                result.HasThinking = !string.IsNullOrWhiteSpace(result.ThinkingContent);
                result.Answer = HarmonyStructuralTokenRegex.Replace(answer, string.Empty).Trim();
                return true;
            }

            // Special tokens were skipped by the decoder: "analysis...assistantfinal...".
            // Case-sensitive on purpose — ordinary prose starting with "Analysis" must not match.
            if (content.Contains("<|channel|>", StringComparison.OrdinalIgnoreCase))
                return false;
            Match bare = HarmonyBareTextRegex.Match(content);
            if (bare.Success)
            {
                result.ThinkingContent = bare.Groups["t"].Value.Trim();
                result.HasThinking = !string.IsNullOrWhiteSpace(result.ThinkingContent);
                result.Answer = bare.Groups["a"].Value.Trim();
                return true;
            }

            return false;
        }

        // First occurrence of the marker that starts a line (optionally indented). Mid-line
        // mentions — e.g. reasoning prose quoting the contract — do not count.
        private static int FindLeadingLineMarkerIndex(string content, string marker)
        {
            int idx = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            while (idx >= 0)
            {
                int lineStart = idx;
                while (lineStart > 0 && (content[lineStart - 1] == ' ' || content[lineStart - 1] == '\t'))
                    lineStart--;
                if (lineStart == 0 || content[lineStart - 1] == '\n' || content[lineStart - 1] == '\r')
                    return idx;
                idx = idx + marker.Length >= content.Length
                    ? -1
                    : content.IndexOf(marker, idx + 1, StringComparison.OrdinalIgnoreCase);
            }

            return -1;
        }

        private static int IndexOfAny(string text, string[] needles)
        {
            int best = -1;
            foreach (string needle in needles)
            {
                int idx = text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (best < 0 || idx < best))
                    best = idx;
            }

            return best;
        }

        public static string FormatThinkingBox(string thinkingContent, bool isDarkMode = true)
        {
            if (string.IsNullOrWhiteSpace(thinkingContent))
                return "";

            // Create a visual box for thinking content
            string boxChar = "═";
            string cornerChar = "╔";
            string endChar = "╗";
            string sideChar = "║";
            string bottomLeft = "╚";
            string bottomRight = "╝";

            var lines = thinkingContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            int maxLength = 0;
            foreach (var line in lines)
            {
                if (line.Length > maxLength)
                    maxLength = line.Length;
            }

            // Ensure minimum width
            maxLength = Math.Max(maxLength, 40);

            var builder = new StringBuilder();
            builder.AppendLine(cornerChar + new string(boxChar[0], maxLength + 2) + endChar);
            builder.AppendLine(sideChar + " Thinking Process ".PadRight(maxLength + 1) + sideChar);
            builder.AppendLine(sideChar + new string(boxChar[0], maxLength + 1) + sideChar);

            foreach (var line in lines)
            {
                builder.AppendLine(sideChar + " " + line.PadRight(maxLength) + " " + sideChar);
            }

            builder.AppendLine(bottomLeft + new string(boxChar[0], maxLength + 2) + bottomRight);

            return builder.ToString();
        }
    }
}
