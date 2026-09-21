using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Runs the agent on a cloud or Hybrid Local endpoint using the provider's own function
    /// calling, with the agent's tools and nothing else.
    /// </summary>
    /// <remarks>
    /// Deliberately does not go through the Workplace council executor. That path rewrites the
    /// system prompt with council role identity and advertises a different tool set
    /// (web_search, run_python, calculate), which is why the agent previously answered
    /// "I can't open Notepad, I'm running in a cloud environment" instead of calling a tool: the
    /// model was being told it was a council Builder with no machine access.
    /// </remarks>
    public sealed class CloudAgentModel : IAgentModel
    {
        private readonly OpenRouterChatService _service;
        private readonly string _modelId;
        private readonly string _systemPrompt;
        private readonly List<OpenRouterMessage> _messages = new();
        private int _renderedExchanges;

        public CloudAgentModel(OpenRouterChatService service, string modelId, string systemPrompt)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _modelId = modelId;
            _systemPrompt = systemPrompt;
        }

        public string? Unavailable =>
            _service.HasValidKey || _service.HasValidCustomEndpoint
                ? null
                : "Cloud mode needs a valid OpenRouter API key, or a configured Hybrid Local endpoint, in Settings.";

        public async Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
        {
            if (_messages.Count == 0)
                _messages.Add(new OpenRouterMessage("user", goal, PreserveFullText: true));

            // Append only what is new, so the assistant/tool message pairing the provider requires
            // stays intact across turns.
            for (int i = _renderedExchanges; i < history.Count; i++)
                AppendExchange(history[i], i);
            _renderedExchanges = history.Count;

            OpenRouterChatResponse response = await _service.SendConversationAsync(
                _messages,
                _systemPrompt,
                thinkingEnabled: false,
                modelId: _modelId,
                tools: AgentToolSchemas.All(),
                cancellationToken: token).ConfigureAwait(false);

            OpenRouterToolCall? toolCall = response.ToolCalls?.FirstOrDefault();
            if (toolCall == null)
                return AgentModelReply.Answer((response.Text ?? string.Empty).Trim());

            if (!AgentToolNames.IsKnown(toolCall.Name))
            {
                return AgentModelReply.Malformed(
                    $"'{toolCall.Name}' is not an available tool. Use one of: {string.Join(", ", AgentToolNames.All)}.");
            }

            if (!TryReadArguments(toolCall.ArgumentsJson, out Dictionary<string, string> arguments, out string? error))
                return AgentModelReply.Malformed($"The arguments for {toolCall.Name} were not valid JSON: {error}");

            _pendingToolCall = toolCall;
            return AgentModelReply.Tool(new AgentToolCall(toolCall.Name.ToLowerInvariant(), arguments));
        }

        private OpenRouterToolCall? _pendingToolCall;

        private void AppendExchange(AgentExchange exchange, int index)
        {
            // Pair the assistant's tool call with its result. When the call came from the provider
            // its real id is reused; a locally synthesised id keeps the shape valid otherwise.
            OpenRouterToolCall call = _pendingToolCall
                ?? new OpenRouterToolCall($"call_{index}", exchange.Call.Tool, "{}");
            _pendingToolCall = null;

            _messages.Add(new OpenRouterMessage("assistant", string.Empty, ToolCalls: [call]));
            _messages.Add(new OpenRouterMessage("tool", exchange.Observation, ToolCallId: call.Id));
        }

        private static bool TryReadArguments(string json, out Dictionary<string, string> arguments, out string? error)
        {
            arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            error = null;
            if (string.IsNullOrWhiteSpace(json))
                return true;

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                    return true;

                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                {
                    arguments[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
                        _ => property.Value.ToString()
                    };
                }

                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}
