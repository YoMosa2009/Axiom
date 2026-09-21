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

        public CloudAgentModel(
            OpenRouterChatService service,
            string modelId,
            string systemPrompt,
            IReadOnlyList<(string Role, string Content)>? priorChatHistory = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _modelId = modelId;
            _systemPrompt = systemPrompt;

            if (priorChatHistory != null && priorChatHistory.Count > 0)
            {
                // Seed recent turns (excluding the last one if it is the current query)
                var turnsToSeed = priorChatHistory.Take(Math.Max(0, priorChatHistory.Count - 1)).TakeLast(8);
                foreach (var (role, content) in turnsToSeed)
                {
                    if (string.IsNullOrWhiteSpace(content))
                        continue;
                    string normalizedRole = string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
                        ? "user"
                        : "assistant";
                    _messages.Add(new OpenRouterMessage(normalizedRole, content, PreserveFullText: true));
                }
            }
        }

        public string? Unavailable =>
            _service.HasValidKey || _service.HasValidCustomEndpoint
                ? null
                : "Cloud mode needs a valid OpenRouter API key, or a configured Hybrid Local endpoint, in Settings.";

        public async Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
        {
            if (_messages.Count == 0 || _messages[^1].Role != "user")
                _messages.Add(new OpenRouterMessage("user", goal, PreserveFullText: true));

            // Append only what is new, so the assistant/tool message pairing the provider requires
            // stays intact across turns.
            for (int i = _renderedExchanges; i < history.Count; i++)
                AppendExchange(history[i], i);
            _renderedExchanges = history.Count;

            OpenRouterChatResponse? response = await SendWithRetryAsync(token).ConfigureAwait(false);
            if (response == null)
            {
                return AgentModelReply.Malformed(
                    "The model returned nothing usable after several attempts. Try the request again.");
            }

            OpenRouterToolCall? toolCall = response.ToolCalls?.FirstOrDefault();
            if (toolCall == null)
            {
                string text = (response.Text ?? string.Empty).Trim();

                // A turn with neither a tool call nor any text is a dropped turn, not an answer.
                // Treating it as one ended runs instantly with a cheerful "Done." having done
                // nothing at all, which is indistinguishable from the agent refusing to work.
                if (text.Length == 0)
                    return AgentModelReply.Malformed("That reply was empty. Call a tool, or answer the question in words.");

                return AgentModelReply.Answer(text);
            }

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

        /// <summary>Attempts per turn before the turn is reported as failed.</summary>
        private const int MaxAttempts = 3;

        /// <summary>Overridable so tests do not actually wait.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

        /// <summary>
        /// Sends one turn, retrying transport failures and empty responses.
        /// </summary>
        /// <remarks>
        /// Free-tier endpoints drop response bodies and return empty completions often enough that
        /// an agent which gives up on the first one is only "sometimes working". Retrying here
        /// rather than in the session keeps the conversation intact, and the backoff matters: an
        /// immediate retry against a provider that just failed usually fails again.
        /// </remarks>
        private async Task<OpenRouterChatResponse?> SendWithRetryAsync(CancellationToken token)
        {
            Exception? lastFailure = null;

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), token).ConfigureAwait(false);
                    await BackendLogService.LogEventAsync(
                        "ComputerAgent",
                        $"retrying turn (attempt {attempt + 1}/{MaxAttempts}): {lastFailure?.Message ?? "empty response"}")
                        .ConfigureAwait(false);
                }

                try
                {
                    OpenRouterChatResponse response = await _service.SendConversationAsync(
                        _messages,
                        _systemPrompt,
                        thinkingEnabled: false,
                        modelId: _modelId,
                        tools: AgentToolSchemas.All(),
                        cancellationToken: token).ConfigureAwait(false);

                    bool hasToolCall = response.ToolCalls?.Count > 0;
                    bool hasText = !string.IsNullOrWhiteSpace(response.Text);
                    if (hasToolCall || hasText)
                        return response;

                    lastFailure = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastFailure = ex;
                }
            }

            return null;
        }

        private OpenRouterToolCall? _pendingToolCall;

        private void AppendExchange(AgentExchange exchange, int index)
        {
            // Pair the assistant's tool call with its result. When the call came from the provider
            // its real id is reused; a locally synthesised id keeps the shape valid otherwise.
            string argsJson = exchange.Call.Arguments.Count > 0
                ? JsonSerializer.Serialize(exchange.Call.Arguments)
                : "{}";
            OpenRouterToolCall call = _pendingToolCall
                ?? new OpenRouterToolCall($"call_{index}", exchange.Call.Tool, argsJson);
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
