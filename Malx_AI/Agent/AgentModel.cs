using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>One completed step: what the agent ran and what came back.</summary>
    public sealed record AgentExchange(AgentToolCall Call, string Observation);

    /// <summary>What the model wants next: either a tool call, or a final answer.</summary>
    public sealed record AgentModelReply(AgentToolCall? Call, string? FinalText, string? ProtocolError = null)
    {
        public static AgentModelReply Tool(AgentToolCall call) => new(call, null);
        public static AgentModelReply Answer(string text) => new(null, text);
        public static AgentModelReply Malformed(string error) => new(null, null, error);
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
    }

    /// <summary>
    /// Drives a model that has no native tool calling by asking for a tool call in text and
    /// parsing it back. Used for local GGUF models.
    /// </summary>
    public sealed class TextProtocolAgentModel : IAgentModel
    {
        private readonly Func<string, string, CancellationToken, Task<string>> _complete;
        private readonly string _systemPrompt;
        private readonly int _observationBudget;

        public TextProtocolAgentModel(
            Func<string, string, CancellationToken, Task<string>> complete,
            string systemPrompt,
            int observationBudget)
        {
            _complete = complete ?? throw new ArgumentNullException(nameof(complete));
            _systemPrompt = systemPrompt;
            _observationBudget = observationBudget;
        }

        public string? Unavailable => null;

        public async Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
        {
            string reply = await _complete(_systemPrompt, RenderTranscript(goal, history), token).ConfigureAwait(false);

            if (AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out string? error) && call != null)
                return AgentModelReply.Tool(call);

            return error != null
                ? AgentModelReply.Malformed(error)
                : AgentModelReply.Answer((reply ?? string.Empty).Trim());
        }

        private string RenderTranscript(string goal, IReadOnlyList<AgentExchange> history)
        {
            var builder = new StringBuilder();
            builder.Append("Task: ").AppendLine(goal);

            foreach (AgentExchange exchange in history)
            {
                builder.AppendLine();
                builder.Append("You ran: ").AppendLine(exchange.Call.DescribeShort());
                string observation = exchange.Observation;
                if (observation.Length > _observationBudget)
                    observation = observation[.._observationBudget] + "\n… truncated.";
                builder.Append("Result: ").AppendLine(observation);
            }

            builder.AppendLine();
            builder.AppendLine("What is your next step?");
            return builder.ToString();
        }
    }
}
