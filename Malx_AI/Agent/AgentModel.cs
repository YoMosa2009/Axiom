using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>One completed step: what the agent ran and what came back.</summary>
    public sealed record AgentExchange(AgentToolCall Call, string Observation, string? ImageDataUrl = null);

    /// <summary>
    /// A model that is slow or busy right now. It ends the turn unless the run may wait and ask again.
    /// </summary>
    public sealed class AgentStalledException : Exception
    {
        public AgentStalledException(string message) : base(message) { }
    }

    /// <summary>
    /// A model failure no retry can fix (e.g. the API key's quota is used up). The session stops
    /// at once and shows the message, instead of treating it as a flaky connection.
    /// </summary>
    public sealed class AgentFatalException : Exception
    {
        public AgentFatalException(string message, Exception? inner = null) : base(message, inner) { }
    }

    /// <summary>What the model wants next: either a tool call, or a final answer.</summary>
    public sealed record AgentModelReply(AgentToolCall? Call, string? FinalText, string? ProtocolError = null)
    {
        public static AgentModelReply Tool(AgentToolCall call) => new(call, null);
        public static AgentModelReply Answer(string text) => new(null, text);
        public static AgentModelReply Malformed(string error) => new(null, null, error);

        /// <summary>
        /// What the model said it is doing with this call (its text before the call, or the end
        /// of its reasoning), for the live "doing now" line. Null when it said nothing.
        /// </summary>
        public string? Narration { get; init; }

        /// <summary>
        /// Everything the model wrote to the user alongside this call, untrimmed (null when it
        /// wrote nothing). A status update or an answer to a mid-run question can be longer than
        /// the one-line narration, and cutting it to a sentence and a half lost it
        /// (<see cref="AgentNarration.ForUser"/>).
        /// </summary>
        public string? Said { get; init; }
    }

    /// <summary>
    /// The model behind an agent run.
    /// </summary>
    /// <remarks>
    /// Cloud and local reach the same decision by very different routes: a cloud model returns a
    /// structured tool call through the provider's own function-calling API, while a local GGUF
    /// has no such channel and must be read out of plain text. Hiding that behind one interface
    /// is what lets <see cref="AgentSession"/> own the loop — permissions, approval, execution,
    /// step limits — exactly once, instead of once per backend.
    /// </remarks>
    public interface IAgentModel
    {
        Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token);

        /// <summary>Told to the user when a run cannot start, e.g. a missing key.</summary>
        string? Unavailable { get; }

        /// <summary>Invoked when token usage is recorded during a turn (promptTokens, completionTokens).</summary>
        Action<int, int>? OnTokenUsageRecorded { get => null; set { } }

        /// <summary>
        /// The session replaced part of the history (context compaction). A model that keeps its
        /// own copy of the conversation must rebuild it from the next history it is given.
        /// </summary>
        void HistoryRewritten() { }

        /// <summary>
        /// True once the current turn has started streaming a tool call. A turn still thinking
        /// can be paused for a user message without losing anything; one writing a call is
        /// left to finish so its work is not thrown away.
        /// </summary>
        bool IsComposingToolCall => false;
    }

    /// <summary>
    /// Drives a model that has no native tool calling by asking for a tool call in text and
    /// parsing it back. Used for local GGUF models.
    /// </summary>
    public sealed class TextProtocolAgentModel : IAgentModel, IAgentSummarizer
    {
        private readonly Func<string, string, CancellationToken, Task<string>> _complete;
        private readonly string _systemPrompt;
        private readonly int _observationBudget;
        private readonly IReadOnlyList<(string Role, string Content)>? _priorChatHistory;

        public TextProtocolAgentModel(
            Func<string, string, CancellationToken, Task<string>> complete,
            string systemPrompt,
            int observationBudget,
            IReadOnlyList<(string Role, string Content)>? priorChatHistory = null)
        {
            _complete = complete ?? throw new ArgumentNullException(nameof(complete));
            _systemPrompt = systemPrompt;
            _observationBudget = observationBudget;
            _priorChatHistory = priorChatHistory;
        }

        public string? Unavailable => null;

        public async Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
        {
            string reply = await _complete(_systemPrompt, RenderTranscript(goal, history), token).ConfigureAwait(false);

            if (AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out string? error) && call != null)
                return AgentModelReply.Tool(call) with { Narration = AgentNarration.FromProtocolReply(reply) };

            return error != null
                ? AgentModelReply.Malformed(error)
                : AgentModelReply.Answer((reply ?? string.Empty).Trim());
        }

        /// <summary>
        /// A compaction checkpoint from the same local model: its own transcript, then the
        /// checkpoint instruction in place of the "your reply" line. Only used for 10B+ models.
        /// </summary>
        public async Task<string?> SummarizeAsync(string goal, IReadOnlyList<AgentExchange> history, string instruction, CancellationToken token)
        {
            string transcript = RenderTranscript(goal, history, closing: instruction);
            string reply = await _complete(SummarySystemPrompt, transcript, token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(reply) ? null : reply;
        }

        internal const string SummarySystemPrompt =
            "You write a factual handoff checkpoint of an agent run from its transcript. You never call tools. "
            + "Keep paths, commands and error messages exact, and do not claim anything the transcript does not show.";

        private string RenderTranscript(string goal, IReadOnlyList<AgentExchange> history, string? closing = null)
        {
            var builder = new StringBuilder();

            if (_priorChatHistory != null && _priorChatHistory.Count > 0)
            {
                var recentTurns = _priorChatHistory.Take(Math.Max(0, _priorChatHistory.Count - 1)).TakeLast(4);
                builder.AppendLine("[RECENT CONVERSATION CONTEXT]");
                foreach (var (role, content) in recentTurns)
                {
                    if (string.IsNullOrWhiteSpace(content))
                        continue;
                    string label = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase) ? "User" : "Assistant";
                    builder.AppendLine($"{label}: {content}");
                }
                builder.AppendLine();
            }

            builder.Append("Task: ").AppendLine(goal);

            foreach (AgentExchange exchange in history)
            {
                builder.AppendLine();
                if (string.Equals(exchange.Call.Tool, AgentSession.UserMessageTool, StringComparison.Ordinal)
                    || string.Equals(exchange.Call.Tool, AgentRunCompactor.SummaryTool, StringComparison.Ordinal))
                {
                    builder.AppendLine(exchange.Observation);
                    continue;
                }

                builder.Append("You ran: ").AppendLine(exchange.Call.DescribeForModel());
                string observation = exchange.Observation;
                if (observation.Length > _observationBudget)
                    observation = AgentOutputClip.HeadTail(observation, _observationBudget,
                        "Observation shortened for the prompt. Use recall_context to recover the full tool result.");
                builder.Append("Result: ").AppendLine(observation);
            }

            builder.AppendLine();
            if (closing != null)
            {
                builder.AppendLine(closing);
                return builder.ToString();
            }

            // Both ways to reply are named. "What is your next step?" alone read as a request for
            // a tool call, even on a turn whose task says "answer it directly, in words".
            builder.AppendLine("Your reply: one tool call in the format above (finish when the work is done), or, if the task is a question, the answer in plain words.");
            return builder.ToString();
        }
    }
}
