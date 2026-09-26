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
        private readonly IReadOnlyList<string>? _imageDataUrls;
        private readonly List<OpenRouterMessage> _messages = new();
        private int _renderedExchanges;

        public CloudAgentModel(
            OpenRouterChatService service,
            string modelId,
            string systemPrompt,
            IReadOnlyList<(string Role, string Content)>? priorChatHistory = null,
            IReadOnlyList<string>? imageDataUrls = null)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _modelId = modelId;
            _systemPrompt = systemPrompt;
            _imageDataUrls = imageDataUrls;

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

        public Action<int, int>? OnTokenUsageRecorded { get; set; }

        public async Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
        {
            if (_messages.Count == 0 || _messages[^1].Role != "user")
                _messages.Add(new OpenRouterMessage("user", goal, PreserveFullText: true, ImageDataUrls: _imageDataUrls));

            // Append only what is new, so the assistant/tool message pairing the provider requires
            // stays intact across turns.
            for (int i = _renderedExchanges; i < history.Count; i++)
                AppendExchange(history[i], i);
            _renderedExchanges = history.Count;

            OpenRouterChatResponse? response = await SendWithRetryAsync(token).ConfigureAwait(false);
            if (response == null)
            {
                if (_lastTurnWasTruncatedToolCall)
                    return AgentModelReply.Malformed(TruncatedToolCallGuidance(null));

                // Every attempt this turn was throttled. Stop with the real reason rather than
                // spending more of the key's daily free requests on a model that is busy.
                if (_lastTurnWasRateLimited)
                {
                    throw new AgentFatalException(
                        "The Workplace model is busy on OpenRouter's free tier right now (rate-limited). "
                        + "Anything already done is listed above - try again in a minute or two.");
                }

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
            {
                // Almost always a call cut off at the output limit mid-way through a file's
                // content. Resending the same request reproduces it, so ask for smaller parts.
                return AgentModelReply.Malformed(LooksTruncated(toolCall.ArgumentsJson)
                    ? TruncatedToolCallGuidance(toolCall.Name)
                    : $"The arguments for {toolCall.Name} were not valid JSON: {error}");
            }

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
            _lastTurnWasTruncatedToolCall = false;
            _lastTurnWasRateLimited = false;

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
                    // Streamed, not a single blocking request: a turn that writes a whole file can
                    // take minutes, and a blocking call showed nothing but "Thinking" for all of it
                    // (then hit the body-read timeout). Streaming keeps the connection observably
                    // alive and lets the UI show the tool call as it forms. The Workplace runs on
                    // exactly one model, so there is no cross-model fallback here.
                    OpenRouterChatResponse response = await _service.SendConversationStreamAsync(
                        _messages,
                        _systemPrompt,
                        thinkingEnabled: false,
                        modelId: _modelId,
                        tools: AgentToolSchemas.All(),
                        onToken: OnText == null ? null : chunk => OnText(chunk),
                        cancellationToken: token,
                        maxTokensOverride: AgentTurnMaxTokens,
                        allowModelFallback: false,
                        onToolCallProgress: OnToolCallProgress).ConfigureAwait(false);

                    _lastTurnWasRateLimited = false;
                    bool hasToolCall = response.ToolCalls?.Count > 0;
                    bool hasText = !string.IsNullOrWhiteSpace(response.Text);
                    if (hasToolCall || hasText)
                    {
                        if (response.Usage != null)
                        {
                            OnTokenUsageRecorded?.Invoke(response.Usage.PromptTokens, response.Usage.CompletionTokens);
                        }
                        return response;
                    }

                    lastFailure = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (OpenRouterKeyExhaustedException exhausted)
                {
                    throw new AgentFatalException(exhausted.Message, exhausted);
                }
                catch (OpenRouterRateLimitedException rateLimited)
                {
                    // Free models share an upstream pool that throttles in bursts. Wait what the
                    // provider suggests (bounded) rather than burning the retries in two seconds.
                    lastFailure = rateLimited;
                    _lastTurnWasRateLimited = true;
                    if (attempt < MaxAttempts - 1)
                    {
                        int waitSeconds = Math.Clamp(rateLimited.RetryAfterSeconds > 0 ? rateLimited.RetryAfterSeconds : 8, 4, 30);
                        OnWaiting?.Invoke($"Model is rate-limited, retrying in {waitSeconds}s");
                        await Delay(TimeSpan.FromSeconds(waitSeconds), token).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    lastFailure = ex;

                    // Self-hosted servers (llama-server behind Ollama) reject a tool call that was
                    // cut off at the output limit with a 500 "invalid tool call arguments ...
                    // unexpected end of JSON input". The same request fails the same way every
                    // time, so stop retrying and have the model write in smaller parts instead.
                    if (IsTruncatedToolCallFailure(ex.Message))
                    {
                        _lastTurnWasTruncatedToolCall = true;
                        return null;
                    }
                }
            }

            return null;
        }

        /// <summary>Output budget for one agent turn: enough for a sizeable file in one call.</summary>
        internal const int AgentTurnMaxTokens = 16384;

        /// <summary>Streamed reply text, for a live view of the agent's final answer.</summary>
        public Action<string>? OnText { get; set; }

        /// <summary>(tool name, argument characters so far) while a tool call streams in.</summary>
        public Action<string, int>? OnToolCallProgress { get; set; }

        /// <summary>A short status while the turn is waiting (e.g. on a rate limit).</summary>
        public Action<string>? OnWaiting { get; set; }

        private bool _lastTurnWasTruncatedToolCall;
        private bool _lastTurnWasRateLimited;

        internal static bool IsTruncatedToolCallFailure(string? message)
        {
            string text = message ?? string.Empty;
            return text.Contains("invalid tool call arguments", StringComparison.OrdinalIgnoreCase)
                || text.Contains("unexpected end of JSON", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when tool-call JSON stops part-way, i.e. it hit the output limit.</summary>
        internal static bool LooksTruncated(string? json)
        {
            string trimmed = (json ?? string.Empty).TrimEnd();
            if (trimmed.Length == 0)
                return false;

            int depth = 0;
            bool inString = false;
            bool escaped = false;
            foreach (char c in trimmed)
            {
                if (escaped) { escaped = false; continue; }
                if (inString)
                {
                    if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }

                if (c == '"') inString = true;
                else if (c is '{' or '[') depth++;
                else if (c is '}' or ']') depth--;
            }

            return inString || depth > 0;
        }

        internal static string TruncatedToolCallGuidance(string? toolName) =>
            $"Your {(string.IsNullOrWhiteSpace(toolName) ? "last tool call" : toolName + " call")} was cut off because it was too long for one reply, so nothing was written. "
            + "Write long files in parts: call write_file with the first part (about 150 lines at most), "
            + "then call append_file with each following part until the file is complete.";

        private OpenRouterToolCall? _pendingToolCall;

        private void AppendExchange(AgentExchange exchange, int index)
        {
            // Session notes ("(invalid)", "(transport)", "(verification)") are not real tool calls.
            // Sending them as function calls named "(verification)" is rejected by providers that
            // validate function names, so they go to the model as a plain message instead.
            if (exchange.Call.Tool.StartsWith('('))
            {
                _pendingToolCall = null;
                _messages.Add(new OpenRouterMessage("user", "[Axiom] " + exchange.Observation));
                return;
            }

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
