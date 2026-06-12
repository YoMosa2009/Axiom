using System;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public class ReasoningParser
    {
        public const string FinalAnswerDelimiter = "=== FINAL ANSWER ===";
        private static readonly Regex QwenThinkBlockRegex = new(@"<think>\s*(?<content>[\s\S]*?)\s*</think>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public class ParsedResponse
        {
            public string ThinkingContent { get; set; }
            public string Answer { get; set; }
            public bool HasThinking { get; set; }

            // True when Answer was populated purely from the model's reasoning channel because no final
            // content was emitted. Such content is chain-of-thought prose, never a real artifact, so the
            // Project Canvas path must refuse to render it. See RunCloudCouncilRoleToolLoopAsync.
            public bool IsReasoningFallback { get; set; }
        }

        public static string StripThinkTags(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            string cleaned = QwenThinkBlockRegex.Replace(content, string.Empty);
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
            var result = new ParsedResponse
            {
                ThinkingContent = "",
                Answer = content?.Trim() ?? string.Empty,
                HasThinking = false
            };

            if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(delimiter))
                return result;

            int delimiterIndex = content.IndexOf(delimiter, StringComparison.Ordinal);
            if (delimiterIndex < 0)
                return result;

            string thinking = content[..delimiterIndex].Trim();
            string answer = content[(delimiterIndex + delimiter.Length)..].Trim();

            result.ThinkingContent = thinking;
            result.Answer = string.IsNullOrWhiteSpace(answer) ? content.Trim() : answer;
            result.HasThinking = !string.IsNullOrWhiteSpace(thinking) && !string.IsNullOrWhiteSpace(answer);
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

            Match thinkMatch = QwenThinkBlockRegex.Match(content);
            if (thinkMatch.Success)
            {
                string thinking = thinkMatch.Groups["content"].Value.Trim();
                string answer = QwenThinkBlockRegex.Replace(content, string.Empty, 1).TrimStart();

                result.HasThinking = !string.IsNullOrWhiteSpace(thinking);
                result.ThinkingContent = thinking;
                result.Answer = answer.TrimStart();
            }
            else
            {
                // Unclosed <think> block: the model hit its token budget (or an anti-prompt) before
                // emitting </think>, so the regex above never matches and StripThinkTags only removed
                // the literal tag — leaving the entire chain-of-thought as the "answer". That raw
                // reasoning then flowed into chat/canvas as if it were final content. Treat everything
                // after the orphan <think> as thinking instead.
                int openIdx = content.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
                if (openIdx >= 0 && content.IndexOf("</think>", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string before = content[..openIdx].Trim();
                    string thinking = content[(openIdx + "<think>".Length)..].Trim();

                    result.HasThinking = !string.IsNullOrWhiteSpace(thinking);
                    result.ThinkingContent = thinking;
                    result.Answer = before;
                }
            }

            // Reasoning-only output: no final content survived parsing. Mirror the cloud path's
            // behavior — surface the reasoning so chat roles still show something, but flag it so
            // the Project Canvas refuses to render chain-of-thought as an artifact.
            if (string.IsNullOrWhiteSpace(result.Answer) && !string.IsNullOrWhiteSpace(result.ThinkingContent))
            {
                result.Answer = result.ThinkingContent;
                result.IsReasoningFallback = true;
            }

            return result;
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
