using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed record OpenRouterToolDefinition(string Name, string Description, JsonObject ParametersSchema);

    public sealed record OpenRouterToolCall(string Id, string Name, string ArgumentsJson);

    public sealed record OpenRouterMessage(
        string Role,
        string Text,
        string? ToolCallId = null,
        IReadOnlyList<OpenRouterToolCall>? ToolCalls = null,
        // Marks the active task payload inside agentic tool loops. Once a tool-call turn is
        // appended, the payload message is no longer the *final* message, and history trimming
        // previously capped it at the per-message limit — silently severing artifact iteration,
        // document grounding, and council role context from iteration 2 onward.
        bool PreserveFullText = false);

    public sealed record OpenRouterChatResponse(
        string Text,
        string Reasoning,
        IReadOnlyList<OpenRouterToolCall> ToolCalls);

    // Thrown when every model in the fallback chain is rate-limited (429) and there is nowhere
    // left to fall back to. Carries the provider-suggested retry delay so callers can wait briefly
    // and retry instead of treating a transient free-tier throttle as a hard failure.
    public sealed class OpenRouterRateLimitedException : Exception
    {
        public int RetryAfterSeconds { get; }

        public OpenRouterRateLimitedException(string modelLabel, int retryAfterSeconds)
            : base($"OpenRouter rate-limited {modelLabel} (429). Check your OpenRouter usage/credits or try again shortly.")
        {
            RetryAfterSeconds = Math.Max(0, retryAfterSeconds);
        }
    }

    public sealed record OpenRouterInferenceSettingsSnapshot(
        double Temperature,
        double TopP,
        int ContextWindowTokens,
        string ModelLabel);

    public sealed record OpenRouterKeyInfo(
        int RequestsUsed,
        int RequestsLimit,
        bool FetchSucceeded,
        bool IsUnlimited = false);

    public sealed record OpenRouterModelProfile(
        string AliasId,
        string AliasLabel,
        string PrimaryApiModelId,
        string[] AlternativeApiModelIds,
            bool IsCodeSpecialized,
            int ApproximateContextWindowTokens)
    {
        public IEnumerable<string> AllApiModelIds
        {
            get
            {
                yield return PrimaryApiModelId;
                foreach (string modelId in AlternativeApiModelIds ?? [])
                    yield return modelId;
            }
        }
    }

    public enum OpenRouterConnectionTestFailureReason
    {
        None,
        InvalidKey,
        RequestFormatError,
        RateLimited,
        ProviderUnavailable,
        NetworkError,
        Failed
    }

    public sealed class OpenRouterChatService
    {
        private static readonly HttpClient Http = new();
        private static readonly string[] CodingRequestSignals =
        [
            "write", "code", "script", "function", "program", "generate", "build", "create", "implement",
            "python", "javascript", "c#", "java", "html", "css", "sql", "debug", "fix this", "error", "bug"
        ];
        private static readonly string[] PythonRequestSignals =
        [
            "python", "python 3", "main.py", "online python compiler", "python compiler", "python interpreter"
        ];
        // Model IDs below were selected against the most popular free models being permanently
        // rate-limited (429) or down (503) on OpenRouter's free tier. The models chosen here were
        // verified live to respond reliably with clean content. Each profile lists multiple working
        // free models as alternatives so availability resolution can pick a live one.
        private static readonly OpenRouterModelProfile[] SupportedModelProfiles =
        [
            new(
                AliasId: "eidos-1",
                AliasLabel: "Eidos 1",
                PrimaryApiModelId: "google/gemma-4-26b-a4b-it:free",
                AlternativeApiModelIds:
                [
                    "nvidia/nemotron-nano-12b-v2-vl:free",
                    "nvidia/nemotron-3-super-120b-a12b:free",
                    "meta-llama/llama-3.3-70b-instruct:free"
                ],
                IsCodeSpecialized: false,
                ApproximateContextWindowTokens: 131072),
            new(
                AliasId: "hepha-1",
                AliasLabel: "Hepha 1",
                PrimaryApiModelId: "nvidia/nemotron-3-super-120b-a12b:free",
                AlternativeApiModelIds:
                [
                    "nvidia/nemotron-nano-12b-v2-vl:free",
                    "qwen/qwen3-coder:free",
                    "google/gemma-4-26b-a4b-it:free"
                ],
                IsCodeSpecialized: true,
                ApproximateContextWindowTokens: 131072)
        ];
        private static readonly OpenRouterModelProfile[] WorkplaceOnlyModelProfiles =
        [
            new(
                AliasId: WorkplaceCouncilDefaultModelId,
                AliasLabel: WorkplaceCouncilDefaultModelLabel,
                PrimaryApiModelId: "qwen/qwen3-coder:free",
                AlternativeApiModelIds:
                [
                    "nvidia/nemotron-3-super-120b-a12b:free",
                    "meta-llama/llama-3.3-70b-instruct:free",
                    "google/gemma-4-26b-a4b-it:free"
                ],
                IsCodeSpecialized: true,
                ApproximateContextWindowTokens: 131072)
        ];
        private const string AuthKeyUrl = "https://openrouter.ai/api/v1/auth/key";
        private const string KeyInfoUrl = "https://openrouter.ai/api/v1/key";
        private const string ModelsUrl = "https://openrouter.ai/api/v1/models";
        private const string ChatCompletionsUrl = "https://openrouter.ai/api/v1/chat/completions";
        private static readonly Regex TokenWordRegex = new(@"[A-Za-z0-9_]+|[^\sA-Za-z0-9_]", RegexOptions.Compiled);
        private static readonly Regex RetryAfterSecondsRegex = new("\"retry_after_seconds\"\\s*:\\s*(?<value>\\d+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const int TransientOpenRouterRetryLimit = 1;
        // Context budgets. All routed models expose ~131k-token windows, so these caps exist only to
        // stop runaway growth — not to shave intentional pipeline content. The old caps (3,200-char
        // system prompt / 2,400 chars per message / 14,000 chars total) silently severed council role
        // boosts, document context, and artifact-iteration payloads that the app deliberately built.
        private const int SystemPromptCharacterLimit = 12000;
        private const int PerHistoryMessageCharacterLimit = 8000;
        private const int ConversationHistoryCharacterBudget = 48000;
        private const int ConversationHistoryMessageLimit = 24;
        private string _apiKey = string.Empty;
        private List<(string Id, string Label, bool IsFree)> _availableModels = new();
        private readonly Dictionary<string, HashSet<string>> _modelSupportedParameters = new(StringComparer.OrdinalIgnoreCase);

        static OpenRouterChatService()
        {
            // 90s covers typical free-tier TTFT queuing without being catastrophically long on failure.
            Http.Timeout = TimeSpan.FromSeconds(90);
        }

        public const string Eidos1ModelId = "eidos-1";
        public const string Eidos1ModelLabel = "Eidos 1";
        public const string Hepha1ModelId = "hepha-1";
        public const string Hepha1ModelLabel = "Hepha 1";
        public const string WorkplaceCouncilDefaultModelId = "workplace-gpt-oss-20b";
        public const string WorkplaceCouncilDefaultModelLabel = "Qwen3 Coder 480B (free)";
        public const string DefaultModelId = Eidos1ModelId;
        public const string DefaultModelLabel = Eidos1ModelLabel;
        public static string WorkplaceCouncilDisplayLabel => SupportedModelProfiles
            .Concat(WorkplaceOnlyModelProfiles)
            .FirstOrDefault(profile => string.Equals(profile.AliasId, WorkplaceCouncilDefaultModelId, StringComparison.OrdinalIgnoreCase))?.AliasLabel
            ?? WorkplaceCouncilDefaultModelLabel;

        public OpenRouterConnectionTestFailureReason LastTestFailureReason { get; private set; }
        public string DetectedModelId { get; private set; } = DefaultModelId;
        public string DetectedModelLabel { get; private set; } = DefaultModelLabel;
        public IReadOnlyList<(string Id, string Label)> SelectableModels => SupportedModelProfiles.Select(profile => (profile.AliasId, profile.AliasLabel)).ToArray();

        public string ResolveModelLabel(string modelId)
        {
            OpenRouterModelProfile profile = FindModelProfile(modelId);
            return profile?.AliasLabel ?? DefaultModelLabel;
        }

        public string NormalizeSelectableModelId(string modelId)
        {
            return NormalizeModelId(modelId);
        }

        public bool IsSelectableModelAvailable(string modelId)
        {
            if (_availableModels.Count == 0)
                return true;

            OpenRouterModelProfile profile = FindModelProfile(modelId);
            return profile != null && ResolveAvailableApiModelId(profile) != null;
        }

        public string DescribeModelSelection(string modelId)
        {
            OpenRouterModelProfile profile = FindModelProfile(modelId) ?? SupportedModelProfiles[0];
            return profile.IsCodeSpecialized
                ? "Code-specialized cloud profile tuned for coding, agentic tool use, debugging, and built-in reasoning-aware execution."
                : "General reasoning cloud profile tuned for tool outputs, structured context blocks, and direct execution-focused answers.";
        }

        public int GetApproximateContextWindowTokens(string modelId)
        {
            OpenRouterModelProfile profile = FindModelProfile(modelId) ?? SupportedModelProfiles[0];
            return Math.Max(32768, profile.ApproximateContextWindowTokens);
        }

        public OpenRouterInferenceSettingsSnapshot GetInferenceSettingsSnapshot(string modelId, bool isCodingRequest = false, bool isPythonRequest = false)
        {
            OpenRouterModelProfile profile = FindModelProfile(modelId) ?? SupportedModelProfiles[0];
            return new OpenRouterInferenceSettingsSnapshot(
                ResolveTemperature(profile, isCodingRequest, isPythonRequest),
                ResolveTopP(profile, isCodingRequest),
                Math.Max(32768, profile.ApproximateContextWindowTokens),
                profile.AliasLabel);
        }

        public int EstimateTokenCount(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            int matches = TokenWordRegex.Matches(text).Count;
            int newlineOverhead = text.Count(c => c == '\n');
            int codeWeight = text.Count(c => c is '{' or '}' or '(' or ')' or '[' or ']' or ';' or '<' or '>' or '=' or '_' or '/' or '\\');
            return Math.Max(1, matches + newlineOverhead + (codeWeight / 6));
        }

        public int EstimateConversationTokens(IEnumerable<OpenRouterMessage>? messages, string? systemPrompt = null)
        {
            int total = EstimateTokenCount(systemPrompt);

            foreach (OpenRouterMessage message in messages ?? [])
            {
                if (message == null)
                    continue;

                total += 4;
                total += EstimateTokenCount(message.Role);
                total += EstimateTokenCount(message.Text);
                total += EstimateTokenCount(message.ToolCallId);

                if (message.ToolCalls?.Count > 0)
                {
                    foreach (OpenRouterToolCall toolCall in message.ToolCalls)
                    {
                        total += 8;
                        total += EstimateTokenCount(toolCall?.Name);
                        total += EstimateTokenCount(toolCall?.ArgumentsJson);
                    }
                }
            }

            return Math.Max(0, total);
        }

        public string BuildSystemPromptForModel(string selectedModelId, string baseSystemPrompt)
        {
            OpenRouterModelProfile profile = FindModelProfile(selectedModelId);
            if (profile == null)
                return baseSystemPrompt ?? string.Empty;

            const string gptOssLatexInstruction = "When expressing mathematical formulas, equations, or expressions, use LaTeX syntax with dollar sign delimiters for inline math and double dollar sign delimiters for block equations.";
            const string qwenLatexInstruction = "When writing mathematical expressions, use LaTeX notation. Use single dollar signs for inline math and double dollar signs for standalone equations on their own line.";

            string prompt = (baseSystemPrompt ?? string.Empty).TrimEnd();

            // Qwen3 models use /no_think to disable extended chain-of-thought, keeping
            // council pipeline outputs clean and direct without thinking-token overhead.
            bool isQwen3CloudModel = profile.AllApiModelIds.Any(id =>
                id.Contains("qwen3", StringComparison.OrdinalIgnoreCase));

            if (isQwen3CloudModel && !prompt.Contains("/no_think", StringComparison.Ordinal))
                prompt = prompt + "\n/no_think";

            string instruction = string.Equals(profile.AliasId, Eidos1ModelId, StringComparison.OrdinalIgnoreCase)
                ? gptOssLatexInstruction
                : string.Equals(profile.AliasId, WorkplaceCouncilDefaultModelId, StringComparison.OrdinalIgnoreCase)
                    ? qwenLatexInstruction
                    : string.Equals(profile.AliasId, Hepha1ModelId, StringComparison.OrdinalIgnoreCase)
                        ? qwenLatexInstruction
                        : string.Empty;

            if (string.IsNullOrWhiteSpace(instruction))
                return prompt;

            if (prompt.Contains(instruction, StringComparison.Ordinal))
                return prompt;

            return string.IsNullOrWhiteSpace(prompt)
                ? instruction
                : prompt + "\n\n" + instruction;
        }

        public void SetApiKey(string apiKey)
        {
            _apiKey = (apiKey ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(_apiKey))
                ResetDetectedModel();
        }

        public bool HasValidKey => !string.IsNullOrWhiteSpace(_apiKey) && _apiKey.Length > 10;

        public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
        {
            if (!HasValidKey)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.InvalidKey;
                ResetDetectedModel();
                return false;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, AuthKeyUrl);
                ApplyHeaders(request);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                switch (response.StatusCode)
                {
                    case HttpStatusCode.OK:
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.None;
                        await TryDetectPreferredModelAsync(cancellationToken);
                        return true;
                    case HttpStatusCode.BadRequest:
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.RequestFormatError;
                        await BackendLogService.LogEventAsync("OpenRouterTest400", responseBody);
                        return false;
                    case HttpStatusCode.TooManyRequests:
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.RateLimited;
                        await BackendLogService.LogEventAsync("OpenRouterTest429", responseBody);
                        return false;
                    case HttpStatusCode.Unauthorized:
                    case HttpStatusCode.Forbidden:
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.InvalidKey;
                        await BackendLogService.LogEventAsync("OpenRouterTestAuthFail", responseBody);
                        return false;
                    default:
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.Failed;
                        await BackendLogService.LogEventAsync("OpenRouterTestFailed", $"Status: {(int)response.StatusCode} ({response.StatusCode})\n{responseBody}");
                        return false;
                }
            }
            catch (HttpRequestException ex)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.NetworkError;
                await BackendLogService.LogErrorAsync("OpenRouterTestFailed", ex);
                return false;
            }
            catch (TaskCanceledException ex)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.NetworkError;
                await BackendLogService.LogErrorAsync("OpenRouterTestFailed", ex);
                return false;
            }
            catch (Exception ex)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.Failed;
                await BackendLogService.LogErrorAsync("OpenRouterTestFailed", ex);
                return false;
            }
        }

        public async Task<OpenRouterKeyInfo> FetchKeyInfoAsync(CancellationToken cancellationToken = default)
        {
            if (!HasValidKey)
                return new OpenRouterKeyInfo(0, 0, false);

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, KeyInfoUrl);
                ApplyHeaders(request);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    await BackendLogService.LogEventAsync("OpenRouterKeyInfoFailed", $"Status: {(int)response.StatusCode} ({response.StatusCode})\n{Truncate(responseBody, 800)}");
                    return new OpenRouterKeyInfo(0, 0, false);
                }

                if (string.IsNullOrWhiteSpace(responseBody))
                    return new OpenRouterKeyInfo(0, 0, false);

                using JsonDocument document = JsonDocument.Parse(responseBody);

                // Free-tier keys return a "data" object with "limit": null (no credit/request cap).
                // That's a valid, healthy key — treat it as a successful "unlimited" fetch rather than
                // a parse failure, so the UI stops falsely reporting "Unable to fetch usage."
                if (IsUnlimitedFreeTierKey(document.RootElement))
                    return new OpenRouterKeyInfo(0, 0, true, IsUnlimited: true);

                if (!TryExtractDailyRequests(document.RootElement, out int requestsUsed, out int requestsLimit))
                {
                    await BackendLogService.LogEventAsync("OpenRouterKeyInfoParseFailed", Truncate(responseBody, 800));
                    return new OpenRouterKeyInfo(0, 0, false);
                }

                if (requestsUsed < 0 || requestsLimit <= 0)
                    return new OpenRouterKeyInfo(0, 0, false);

                return new OpenRouterKeyInfo(requestsUsed, requestsLimit, true);
            }
            catch
            {
                return new OpenRouterKeyInfo(0, 0, false);
            }
        }

        public async Task<string> SendMessageAsync(
            string userMessage,
            List<OpenRouterMessage> conversationHistory,
            string systemPrompt = null,
            bool thinkingEnabled = false,
            string modelId = DefaultModelId,
            CancellationToken cancellationToken = default)
        {
            OpenRouterChatResponse response = await SendConversationAsync(
                BuildConversation(conversationHistory, userMessage),
                systemPrompt,
                thinkingEnabled,
                modelId,
                null,
                cancellationToken);

            return response.Text;
        }

        public async Task SendMessageStreamAsync(
            string userMessage,
            List<OpenRouterMessage> conversationHistory,
            string systemPrompt = null,
            bool thinkingEnabled = false,
            string modelId = DefaultModelId,
            Action<string>? onToken = null,
            CancellationToken cancellationToken = default)
        {
            await SendConversationStreamAsync(
                BuildConversation(conversationHistory, userMessage),
                systemPrompt,
                thinkingEnabled,
                modelId,
                null,
                onToken,
                cancellationToken);
        }

        public async Task<OpenRouterChatResponse> SendConversationAsync(
            List<OpenRouterMessage> messages,
            string systemPrompt = null,
            bool thinkingEnabled = false,
            string modelId = DefaultModelId,
            IReadOnlyList<OpenRouterToolDefinition>? tools = null,
            CancellationToken cancellationToken = default)
        {
            return await SendMessageInternalAsync(
                messages,
                systemPrompt,
                thinkingEnabled,
                modelId,
                tools,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }

        public async Task<OpenRouterChatResponse> SendConversationStreamAsync(
            List<OpenRouterMessage> messages,
            string systemPrompt = null,
            bool thinkingEnabled = false,
            string modelId = DefaultModelId,
            IReadOnlyList<OpenRouterToolDefinition>? tools = null,
            Action<string>? onToken = null,
            CancellationToken cancellationToken = default)
        {
            return await SendMessageStreamInternalAsync(
                messages,
                systemPrompt,
                thinkingEnabled,
                modelId,
                tools,
                onToken,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                cancellationToken);
        }

        public async Task<bool> ValidateModelAvailabilityAsync(string modelId, CancellationToken cancellationToken = default)
        {
            if (!HasValidKey)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.InvalidKey;
                return false;
            }

            try
            {
                if (_availableModels.Count == 0)
                    await TryDetectPreferredModelAsync(cancellationToken);

                OpenRouterModelProfile requestedModelProfile = ResolveRequestedModelProfile(modelId);
                foreach (OpenRouterModelProfile candidateProfile in GetValidationProfiles(requestedModelProfile))
                {
                    string effectiveModelId = ResolveRequestedModelId(candidateProfile);
                    (HttpStatusCode statusCode, string responseBody) = await ProbeModelAvailabilityAsync(effectiveModelId, cancellationToken);

                    if (statusCode == HttpStatusCode.OK)
                    {
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.None;
                        SetDetectedModel(candidateProfile.AliasId, candidateProfile.AliasLabel);
                        return true;
                    }

                    if (ContainsIgnoredProvidersMessage(responseBody) || IsTransientProviderFailure(statusCode, responseBody))
                    {
                        LastTestFailureReason = OpenRouterConnectionTestFailureReason.ProviderUnavailable;
                        await BackendLogService.LogEventAsync("OpenRouterModelValidationFailed", $"Model:{candidateProfile.AliasLabel}\nStatus:{(int)statusCode} ({statusCode})\nBody:{Truncate(responseBody, 500)}");
                        continue;
                    }

                    LastTestFailureReason = statusCode switch
                    {
                        HttpStatusCode.BadRequest => OpenRouterConnectionTestFailureReason.RequestFormatError,
                        HttpStatusCode.TooManyRequests => OpenRouterConnectionTestFailureReason.RateLimited,
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => OpenRouterConnectionTestFailureReason.InvalidKey,
                        HttpStatusCode.NotFound => OpenRouterConnectionTestFailureReason.ProviderUnavailable,
                        _ => OpenRouterConnectionTestFailureReason.Failed
                    };

                    await BackendLogService.LogEventAsync("OpenRouterModelValidationFailed", $"Model:{candidateProfile.AliasLabel}\nStatus:{(int)statusCode} ({statusCode})\nBody:{Truncate(responseBody, 500)}");
                    return false;
                }

                LastTestFailureReason = OpenRouterConnectionTestFailureReason.ProviderUnavailable;
                return false;
            }
            catch (HttpRequestException ex)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.NetworkError;
                await BackendLogService.LogErrorAsync("OpenRouterModelValidationFailed", ex);
                return false;
            }
            catch (TaskCanceledException ex)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.NetworkError;
                await BackendLogService.LogErrorAsync("OpenRouterModelValidationFailed", ex);
                return false;
            }
            catch (Exception ex)
            {
                LastTestFailureReason = OpenRouterConnectionTestFailureReason.Failed;
                await BackendLogService.LogErrorAsync("OpenRouterModelValidationFailed", ex);
                return false;
            }
        }

        private async Task<OpenRouterChatResponse> SendMessageInternalAsync(
            List<OpenRouterMessage> messages,
            string systemPrompt,
            bool thinkingEnabled,
            string modelId,
            IReadOnlyList<OpenRouterToolDefinition>? tools,
            HashSet<string> attemptedModelIds,
            CancellationToken cancellationToken,
            string? originalModelLabel = null)
        {
            if (!HasValidKey)
                throw new InvalidOperationException("A valid OpenRouter API key is required.");

            if (_availableModels.Count == 0)
                await TryDetectPreferredModelAsync(cancellationToken);

            systemPrompt = Truncate(systemPrompt, SystemPromptCharacterLimit);
            messages = TrimConversationHistory(messages, ConversationHistoryMessageLimit, ConversationHistoryCharacterBudget);
            string requestSignalText = string.Join("\n", messages.Select(m => m?.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

            OpenRouterModelProfile requestedModelProfile = ResolveRequestedModelProfile(modelId);
            string displayModelLabel = originalModelLabel ?? requestedModelProfile.AliasLabel;
            attemptedModelIds.Add(requestedModelProfile.AliasId);
            string effectiveModelId = ResolveRequestedModelId(requestedModelProfile);
            bool isCodingRequest = DetectCodingRequest(requestSignalText) || DetectCodingRequest(systemPrompt);
            bool isPythonRequest = DetectPythonRequest(requestSignalText) || DetectPythonRequest(systemPrompt);
            double temperature = ResolveTemperature(requestedModelProfile, isCodingRequest, isPythonRequest);
            double topP = ResolveTopP(requestedModelProfile, isCodingRequest);

            HttpStatusCode finalStatusCode = HttpStatusCode.OK;
            string responseBody = string.Empty;
            for (int retryAttempt = 0; ; retryAttempt++)
            {
                using var request = BuildChatRequest(
                    messages,
                    systemPrompt,
                    effectiveModelId,
                    thinkingEnabled,
                    temperature,
                    topP,
                    8192,
                    tools);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                finalStatusCode = response.StatusCode;
                responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

                if (finalStatusCode == HttpStatusCode.OK)
                    break;

                if (IsServerTransientStatus(finalStatusCode) && retryAttempt < TransientOpenRouterRetryLimit)
                {
                    TimeSpan retryDelay = GetRetryDelay(response, responseBody, retryAttempt);
                    await BackendLogService.LogEventAsync("OpenRouterTransientRetry", $"Model:{requestedModelProfile.AliasLabel}\nStatus:{(int)finalStatusCode} ({finalStatusCode})\nRetryInSeconds:{retryDelay.TotalSeconds:F1}\nAttempt:{retryAttempt + 1}");
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                break;
            }

            if (finalStatusCode != HttpStatusCode.OK)
            {
                if (finalStatusCode == HttpStatusCode.TooManyRequests)
                    LastTestFailureReason = OpenRouterConnectionTestFailureReason.RateLimited;

                if (ContainsIgnoredProvidersMessage(responseBody) || IsTransientProviderFailure(finalStatusCode, responseBody))
                {
                    LastTestFailureReason = OpenRouterConnectionTestFailureReason.ProviderUnavailable;
                    if (ShouldRetryWithFallback(finalStatusCode, effectiveModelId))
                    {
                        string fallbackModelId = GetFallbackModelId(requestedModelProfile.AliasId, attemptedModelIds);
                        if (!string.IsNullOrWhiteSpace(fallbackModelId))
                        {
                            await BackendLogService.LogEventAsync("OpenRouterFallback", $"Primary:{requestedModelProfile.AliasLabel}\nFallback:{ResolveModelLabel(fallbackModelId)}\nStatus:{(int)finalStatusCode} ({finalStatusCode})\nBody:{Truncate(responseBody, 400)}");
                            return await SendMessageInternalAsync(messages, systemPrompt, thinkingEnabled, fallbackModelId, tools, attemptedModelIds, cancellationToken, displayModelLabel);
                        }
                    }

                    // Fallback chain exhausted. Treat a 429 as a typed, retryable throttle.
                    if (finalStatusCode == HttpStatusCode.TooManyRequests)
                        throw new OpenRouterRateLimitedException(displayModelLabel, ParseRetryAfterSeconds(null, responseBody));

                    throw new InvalidOperationException(ContainsIgnoredProvidersMessage(responseBody)
                        ? $"OpenRouter cannot route {displayModelLabel} because your account is ignoring all available providers for this model. Open OpenRouter Settings > Privacy and remove provider ignores for this model, then try again."
                        : $"OpenRouter provider for {displayModelLabel} is temporarily unavailable. Try again shortly or switch models.");
                }

                if (ShouldRetryWithFallback(finalStatusCode, effectiveModelId))
                {
                    string fallbackModelId = GetFallbackModelId(requestedModelProfile.AliasId, attemptedModelIds);
                    if (!string.IsNullOrWhiteSpace(fallbackModelId))
                    {
                        await BackendLogService.LogEventAsync("OpenRouterFallback", $"Primary:{requestedModelProfile.AliasLabel}\nFallback:{ResolveModelLabel(fallbackModelId)}\nStatus:{(int)finalStatusCode} ({finalStatusCode})\nBody:{Truncate(responseBody, 400)}");
                        return await SendMessageInternalAsync(messages, systemPrompt, thinkingEnabled, fallbackModelId, tools, attemptedModelIds, cancellationToken, displayModelLabel);
                    }
                }

                string truncatedBody = Truncate(responseBody, 280);
                if (finalStatusCode == HttpStatusCode.TooManyRequests)
                    throw new OpenRouterRateLimitedException(displayModelLabel, ParseRetryAfterSeconds(null, responseBody));

                throw new InvalidOperationException($"OpenRouter request for {displayModelLabel} failed with status {(int)finalStatusCode} ({finalStatusCode}): {truncatedBody}");
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(responseBody);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("choices", out JsonElement choices)
                    && choices.ValueKind == JsonValueKind.Array
                    && choices.GetArrayLength() > 0)
                {
                    JsonElement firstChoice = choices[0];
                    if (firstChoice.TryGetProperty("message", out JsonElement message))
                    {
                        SetDetectedModel(requestedModelProfile.AliasId, requestedModelProfile.AliasLabel);
                        return new OpenRouterChatResponse(
                            ExtractMessageContent(message),
                            ExtractReasoningContent(firstChoice, message),
                            ExtractToolCalls(message));
                    }
                }

                await BackendLogService.LogEventAsync("OpenRouterEmptyResponse", responseBody.Length <= 500 ? responseBody : responseBody[..500]);
                return new OpenRouterChatResponse(string.Empty, string.Empty, Array.Empty<OpenRouterToolCall>());
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("OpenRouterEmptyResponse", ex);
                return new OpenRouterChatResponse(string.Empty, string.Empty, Array.Empty<OpenRouterToolCall>());
            }
        }

        private async Task<OpenRouterChatResponse> SendMessageStreamInternalAsync(
            List<OpenRouterMessage> messages,
            string systemPrompt,
            bool thinkingEnabled,
            string modelId,
            IReadOnlyList<OpenRouterToolDefinition>? tools,
            Action<string>? onToken,
            HashSet<string> attemptedModelIds,
            CancellationToken cancellationToken,
            string? originalModelLabel = null)
        {
            if (!HasValidKey)
                throw new InvalidOperationException("A valid OpenRouter API key is required.");

            if (_availableModels.Count == 0)
                await TryDetectPreferredModelAsync(cancellationToken);

            systemPrompt = Truncate(systemPrompt, SystemPromptCharacterLimit);
            messages = TrimConversationHistory(messages, ConversationHistoryMessageLimit, ConversationHistoryCharacterBudget);
            string requestSignalText = string.Join("\n", messages.Select(m => m?.Text).Where(t => !string.IsNullOrWhiteSpace(t)));

            OpenRouterModelProfile requestedModelProfile = ResolveRequestedModelProfile(modelId);
            string displayModelLabel = originalModelLabel ?? requestedModelProfile.AliasLabel;
            attemptedModelIds.Add(requestedModelProfile.AliasId);
            string effectiveModelId = ResolveRequestedModelId(requestedModelProfile);
            bool isCodingRequest = DetectCodingRequest(requestSignalText) || DetectCodingRequest(systemPrompt);
            bool isPythonRequest = DetectPythonRequest(requestSignalText) || DetectPythonRequest(systemPrompt);
            double temperature = ResolveTemperature(requestedModelProfile, isCodingRequest, isPythonRequest);
            double topP = ResolveTopP(requestedModelProfile, isCodingRequest);

            HttpResponseMessage? response = null;
            for (int retryAttempt = 0; ; retryAttempt++)
            {
                response?.Dispose();
                using var request = BuildChatRequest(
                    messages,
                    systemPrompt,
                    effectiveModelId,
                    thinkingEnabled,
                    temperature,
                    topP,
                    8192,
                    tools,
                    stream: true);

                response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    break;

                string retryBody = await response.Content.ReadAsStringAsync(cancellationToken);

                // Only do a short in-place retry for genuine server transients (502/503/504).
                // 429 rate-limits are NOT retried in place — we fall straight through to fallback,
                // which switches to a different working model instead of blocking on a throttled one.
                if (IsServerTransientStatus(response.StatusCode) && retryAttempt < TransientOpenRouterRetryLimit)
                {
                    TimeSpan retryDelay = GetRetryDelay(response, retryBody, retryAttempt);
                    await BackendLogService.LogEventAsync("OpenRouterTransientRetry", $"Model:{requestedModelProfile.AliasLabel}\nStatus:{(int)response.StatusCode} ({response.StatusCode})\nRetryInSeconds:{retryDelay.TotalSeconds:F1}\nAttempt:{retryAttempt + 1}");
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    LastTestFailureReason = OpenRouterConnectionTestFailureReason.RateLimited;

                if (ContainsIgnoredProvidersMessage(retryBody) || IsTransientProviderFailure(response.StatusCode, retryBody))
                {
                    LastTestFailureReason = OpenRouterConnectionTestFailureReason.ProviderUnavailable;
                    if (ShouldRetryWithFallback(response.StatusCode, effectiveModelId))
                    {
                        string fallbackModelId = GetFallbackModelId(requestedModelProfile.AliasId, attemptedModelIds);
                        if (!string.IsNullOrWhiteSpace(fallbackModelId))
                        {
                            await BackendLogService.LogEventAsync("OpenRouterFallback", $"Primary:{requestedModelProfile.AliasLabel}\nFallback:{ResolveModelLabel(fallbackModelId)}\nStatus:{(int)response.StatusCode} ({response.StatusCode})\nBody:{Truncate(retryBody, 400)}");
                            return await SendMessageStreamInternalAsync(messages, systemPrompt, thinkingEnabled, fallbackModelId, tools, onToken, attemptedModelIds, cancellationToken, displayModelLabel);
                        }
                    }

                    // Fallback chain exhausted. A 429 here is a transient throttle (free-tier models all
                    // share the limit window), so surface it as a typed, retryable exception.
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                        throw new OpenRouterRateLimitedException(displayModelLabel, ParseRetryAfterSeconds(response, retryBody));

                    throw new InvalidOperationException(ContainsIgnoredProvidersMessage(retryBody)
                        ? $"OpenRouter cannot route {displayModelLabel} because your account is ignoring all available providers for this model. Open OpenRouter Settings > Privacy and remove provider ignores for this model, then try again."
                        : $"OpenRouter provider for {displayModelLabel} is temporarily unavailable. Try again shortly or switch models.");
                }

                string truncatedBody = Truncate(retryBody, 280);
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    throw new OpenRouterRateLimitedException(displayModelLabel, ParseRetryAfterSeconds(response, retryBody));

                throw new InvalidOperationException($"OpenRouter request for {displayModelLabel} failed with status {(int)response.StatusCode} ({response.StatusCode}): {truncatedBody}");
            }

            var textBuilder = new StringBuilder();
            var reasoningBuilder = new StringBuilder();
            var toolCallAccumulators = new Dictionary<int, StreamingToolCallAccumulator>();

            try
            {
                using (response)
                {
                await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var reader = new StreamReader(responseStream);

                while (true)
                {
                    string? line = await reader.ReadLineAsync(cancellationToken);
                    if (line == null)
                        break;

                    if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string payload = line[5..].Trim();
                    if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                        break;

                    if (string.IsNullOrWhiteSpace(payload))
                        continue;

                    using JsonDocument chunkDocument = JsonDocument.Parse(payload);
                    JsonElement root = chunkDocument.RootElement;
                    if (!root.TryGetProperty("choices", out JsonElement choices)
                        || choices.ValueKind != JsonValueKind.Array
                        || choices.GetArrayLength() == 0)
                    {
                        continue;
                    }

                    JsonElement firstChoice = choices[0];
                    if (!firstChoice.TryGetProperty("delta", out JsonElement delta)
                        || delta.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    if (delta.TryGetProperty("content", out JsonElement contentElement))
                    {
                        string contentDelta = ExtractContentText(contentElement, includeReasoningParts: false);
                        if (!string.IsNullOrEmpty(contentDelta))
                        {
                            textBuilder.Append(contentDelta);
                            onToken?.Invoke(contentDelta);
                        }

                        string reasoningFromContent = ExtractReasoningPartsFromContent(contentElement);
                        if (!string.IsNullOrEmpty(reasoningFromContent))
                            reasoningBuilder.Append(reasoningFromContent);
                    }

                    // OpenRouter delivers the same chain-of-thought in both "reasoning" (plain string)
                    // and "reasoning_details" (structured array) on each chunk. Appending both
                    // double-counts every token and produces interleaved garbage ("WeWe need toneed to").
                    // The non-streaming path dedupes whole strings via .Distinct(); replicate that here at
                    // the per-chunk level by preferring one source and only taking the other when it differs.
                    string reasoningDelta = delta.TryGetProperty("reasoning", out JsonElement reasoningElement)
                        ? ExtractContentText(reasoningElement, includeReasoningParts: true)
                        : string.Empty;
                    string reasoningDetailsDelta = delta.TryGetProperty("reasoning_details", out JsonElement reasoningDetailsElement)
                        ? ExtractContentText(reasoningDetailsElement, includeReasoningParts: true)
                        : string.Empty;

                    if (!string.IsNullOrEmpty(reasoningDelta))
                        reasoningBuilder.Append(reasoningDelta);
                    if (!string.IsNullOrEmpty(reasoningDetailsDelta)
                        && !string.Equals(reasoningDetailsDelta, reasoningDelta, StringComparison.Ordinal))
                        reasoningBuilder.Append(reasoningDetailsDelta);

                    AppendStreamingToolCalls(delta, toolCallAccumulators);
                }
                }

                SetDetectedModel(requestedModelProfile.AliasId, requestedModelProfile.AliasLabel);
                return new OpenRouterChatResponse(
                    textBuilder.ToString(),
                    reasoningBuilder.ToString(),
                    BuildStreamingToolCalls(toolCallAccumulators));
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("OpenRouterStreamFailed", ex);
                throw;
            }
        }

        private async Task TryDetectPreferredModelAsync(CancellationToken cancellationToken)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, ModelsUrl);
                ApplyHeaders(request);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    await BackendLogService.LogEventAsync("OpenRouterModelsFailed", $"Status: {(int)response.StatusCode} ({response.StatusCode})\n{responseBody}");
                    ResetDetectedModel();
                    return;
                }

                using JsonDocument document = JsonDocument.Parse(responseBody);
                JsonElement root = document.RootElement;
                if (!root.TryGetProperty("data", out JsonElement data)
                    || data.ValueKind != JsonValueKind.Array)
                {
                    ResetDetectedModel();
                    return;
                }

                var models = new List<(string Id, string Label, bool IsFree)>();
                _modelSupportedParameters.Clear();
                foreach (JsonElement item in data.EnumerateArray())
                {
                    if (!item.TryGetProperty("id", out JsonElement idElement))
                        continue;

                    string id = idElement.GetString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(id))
                        continue;

                    string label = item.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString() ?? id
                        : id;
                    bool isFree = id.Contains(":free", StringComparison.OrdinalIgnoreCase);
                    models.Add((id, label, isFree));

                    if (item.TryGetProperty("supported_parameters", out JsonElement supportedParametersElement)
                        && supportedParametersElement.ValueKind == JsonValueKind.Array)
                    {
                        var supportedParameters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (JsonElement parameterElement in supportedParametersElement.EnumerateArray())
                        {
                            string parameter = parameterElement.GetString() ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(parameter))
                                supportedParameters.Add(parameter.Trim());
                        }

                        _modelSupportedParameters[id] = supportedParameters;
                    }
                }

                _availableModels = models;

                if (models.Count == 0)
                {
                    ResetDetectedModel();
                    return;
                }

                OpenRouterModelProfile preferredProfile = SupportedModelProfiles.FirstOrDefault(IsProfileAvailable);
                if (preferredProfile != null)
                {
                    SetDetectedModel(preferredProfile.AliasId, preferredProfile.AliasLabel);
                    return;
                }

                ResetDetectedModel();
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("OpenRouterModelsFailed", ex);
                ResetDetectedModel();
            }
        }

        private HttpRequestMessage BuildChatRequest(
            List<OpenRouterMessage> messages,
            string systemPrompt,
            string modelId,
            bool thinkingEnabled,
            double temperature,
            double topP,
            int maxTokens,
            IReadOnlyList<OpenRouterToolDefinition>? tools,
            bool stream = false)
        {
            JsonArray messagePayload = BuildMessages(messages, systemPrompt);
            JsonObject payload = new()
            {
                ["model"] = modelId,
                ["messages"] = messagePayload,
                ["max_tokens"] = maxTokens,
                ["stream"] = stream
            };

            if (SupportsParameter(modelId, "temperature"))
                payload["temperature"] = temperature;

            if (SupportsParameter(modelId, "top_p"))
                payload["top_p"] = topP;

            // Only send the reasoning parameter when the user actually enables thinking mode.
            // Sending reasoning:{effort:"low"} on every request causes providers that don't
            // support the field to reject the request with an immediate "Provider returned error".
            if (thinkingEnabled && SupportsParameter(modelId, "reasoning"))
            {
                payload["reasoning"] = new JsonObject { ["effort"] = "high" };
            }

            if (tools != null && tools.Count > 0 && SupportsParameter(modelId, "tools"))
            {
                payload["tools"] = BuildTools(tools);
                if (SupportsParameter(modelId, "tool_choice"))
                    payload["tool_choice"] = "auto";
            }

            var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsUrl)
            {
                Content = new StringContent(payload.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json")
            };
            ApplyHeaders(request);
            return request;
        }

        private static JsonArray BuildMessages(List<OpenRouterMessage> conversationMessages, string systemPrompt)
        {
            var messages = new JsonArray();
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                messages.Add(new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = systemPrompt
                });
            }

            foreach (OpenRouterMessage message in conversationMessages ?? [])
            {
                if (message == null)
                    continue;

                string normalizedRole = NormalizeRole(message.Role);
                JsonObject messageObject = new()
                {
                    ["role"] = normalizedRole
                };

                if (message.ToolCalls?.Count > 0)
                    messageObject["tool_calls"] = BuildToolCallPayload(message.ToolCalls);

                if (!string.IsNullOrWhiteSpace(message.ToolCallId))
                    messageObject["tool_call_id"] = message.ToolCallId;

                if (string.Equals(normalizedRole, "assistant", StringComparison.OrdinalIgnoreCase)
                    && message.ToolCalls?.Count > 0
                    && string.IsNullOrWhiteSpace(message.Text))
                {
                    messageObject["content"] = JsonValue.Create((string?)null);
                }
                else
                {
                    messageObject["content"] = message.Text ?? string.Empty;
                }

                messages.Add(messageObject);
            }

            return messages;
        }

        private static JsonArray BuildTools(IReadOnlyList<OpenRouterToolDefinition> tools)
        {
            var payload = new JsonArray();
            foreach (OpenRouterToolDefinition tool in tools)
            {
                if (tool == null || string.IsNullOrWhiteSpace(tool.Name))
                    continue;

                payload.Add(new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description ?? string.Empty,
                        ["parameters"] = tool.ParametersSchema?.DeepClone() ?? new JsonObject()
                    }
                });
            }

            return payload;
        }

        private static JsonArray BuildToolCallPayload(IReadOnlyList<OpenRouterToolCall> toolCalls)
        {
            var payload = new JsonArray();
            foreach (OpenRouterToolCall toolCall in toolCalls ?? [])
            {
                if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                    continue;

                payload.Add(new JsonObject
                {
                    ["id"] = string.IsNullOrWhiteSpace(toolCall.Id) ? Guid.NewGuid().ToString("N") : toolCall.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = toolCall.Name,
                        ["arguments"] = string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson
                    }
                });
            }

            return payload;
        }

        private static List<OpenRouterMessage> TrimConversationHistory(List<OpenRouterMessage> conversationHistory, int maxMessages, int maxChars)
        {
            if (conversationHistory == null || conversationHistory.Count == 0)
                return new List<OpenRouterMessage>();

            var candidates = conversationHistory.Where(m => m != null).ToList();
            if (candidates.Count == 0)
                return new List<OpenRouterMessage>();

            var trimmed = new List<OpenRouterMessage>();
            int totalChars = 0;
            int startIndex = Math.Max(0, candidates.Count - Math.Max(1, maxMessages));
            for (int i = startIndex; i < candidates.Count; i++)
            {
                OpenRouterMessage message = candidates[i];
                bool isFinalMessage = i == candidates.Count - 1;

                string text = (message.Text ?? string.Empty).Trim();
                // The final message carries the active request payload (council role payloads bundle
                // the architect plan, current artifact, and document context in one user message).
                // Capping it silently severed artifact iteration and document grounding, so the
                // final message is always passed through whole. Messages flagged PreserveFullText get
                // the same exemption: in tool-calling loops the active payload stops being the final
                // message as soon as a tool turn is appended, and truncating it mid-loop destroyed
                // the very context the tools were called to act on.
                bool preserveWhole = isFinalMessage || message.PreserveFullText;
                if (!preserveWhole && text.Length > PerHistoryMessageCharacterLimit)
                    text = text[..PerHistoryMessageCharacterLimit];

                int toolChars = message.ToolCalls?.Sum(call => (call.Name?.Length ?? 0) + (call.ArgumentsJson?.Length ?? 0)) ?? 0;
                if (!preserveWhole && trimmed.Count > 0 && totalChars + text.Length + toolChars > maxChars)
                    continue;

                trimmed.Add(new OpenRouterMessage(message.Role, text, message.ToolCallId, message.ToolCalls, message.PreserveFullText));
                totalChars += text.Length + toolChars;
            }

            return RepairToolCallPairing(trimmed);
        }

        // Trimming can strand a tool-result message from the assistant tool_calls turn that produced
        // it (or leave an assistant tool_calls turn whose results were dropped). OpenAI-compatible
        // endpoints reject both shapes with a 400, so repair the pairing after trimming.
        private static List<OpenRouterMessage> RepairToolCallPairing(List<OpenRouterMessage> messages)
        {
            if (messages.Count == 0)
                return messages;

            var toolResultIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (OpenRouterMessage message in messages)
            {
                if (string.Equals(NormalizeRole(message.Role), "tool", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(message.ToolCallId))
                {
                    toolResultIds.Add(message.ToolCallId);
                }
            }

            var seenToolCallIds = new HashSet<string>(StringComparer.Ordinal);
            var repaired = new List<OpenRouterMessage>(messages.Count);
            foreach (OpenRouterMessage message in messages)
            {
                if (string.Equals(NormalizeRole(message.Role), "tool", StringComparison.Ordinal))
                {
                    // Drop tool results whose originating assistant tool_calls turn is not present earlier.
                    if (string.IsNullOrWhiteSpace(message.ToolCallId) || !seenToolCallIds.Contains(message.ToolCallId))
                        continue;

                    repaired.Add(message);
                    continue;
                }

                if (message.ToolCalls?.Count > 0)
                {
                    bool allResultsPresent = message.ToolCalls.All(call =>
                        call != null && !string.IsNullOrWhiteSpace(call.Id) && toolResultIds.Contains(call.Id));

                    if (!allResultsPresent)
                    {
                        // Results were trimmed away — keep any salvageable text, drop the tool calls.
                        if (string.IsNullOrWhiteSpace(message.Text))
                            continue;

                        repaired.Add(message with { ToolCalls = null });
                        continue;
                    }

                    foreach (OpenRouterToolCall call in message.ToolCalls)
                        seenToolCallIds.Add(call.Id);
                }

                repaired.Add(message);
            }

            return repaired;
        }

        private static string ExtractMessageContent(JsonElement message)
        {
            if (!message.TryGetProperty("content", out JsonElement content))
                return string.Empty;

            return ExtractContentText(content, includeReasoningParts: false);
        }

        private static string ExtractReasoningContent(JsonElement firstChoice, JsonElement message)
        {
            var parts = new List<string>();

            if (message.TryGetProperty("reasoning", out JsonElement reasoning))
                parts.Add(ExtractContentText(reasoning, includeReasoningParts: true));

            if (message.TryGetProperty("reasoning_details", out JsonElement reasoningDetails))
                parts.Add(ExtractContentText(reasoningDetails, includeReasoningParts: true));

            if (firstChoice.TryGetProperty("reasoning", out JsonElement choiceReasoning))
                parts.Add(ExtractContentText(choiceReasoning, includeReasoningParts: true));

            if (firstChoice.TryGetProperty("reasoning_details", out JsonElement choiceReasoningDetails))
                parts.Add(ExtractContentText(choiceReasoningDetails, includeReasoningParts: true));

            if (message.TryGetProperty("content", out JsonElement content))
                parts.Add(ExtractReasoningPartsFromContent(content));

            return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal));
        }

        private static IReadOnlyList<OpenRouterToolCall> ExtractToolCalls(JsonElement message)
        {
            var toolCalls = new List<OpenRouterToolCall>();
            if (!message.TryGetProperty("tool_calls", out JsonElement toolCallsElement)
                || toolCallsElement.ValueKind != JsonValueKind.Array)
            {
                return toolCalls;
            }

            foreach (JsonElement toolCallElement in toolCallsElement.EnumerateArray())
            {
                string id = toolCallElement.TryGetProperty("id", out JsonElement idElement)
                    ? idElement.GetString() ?? Guid.NewGuid().ToString("N")
                    : Guid.NewGuid().ToString("N");

                if (!toolCallElement.TryGetProperty("function", out JsonElement functionElement))
                    continue;

                string name = functionElement.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                string argumentsJson = functionElement.TryGetProperty("arguments", out JsonElement argumentsElement)
                    ? argumentsElement.GetString() ?? "{}"
                    : "{}";

                if (string.IsNullOrWhiteSpace(name))
                    continue;

                toolCalls.Add(new OpenRouterToolCall(id, name, string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson));
            }

            return toolCalls;
        }

        private static void AppendStreamingToolCalls(JsonElement delta, IDictionary<int, StreamingToolCallAccumulator> accumulators)
        {
            if (!delta.TryGetProperty("tool_calls", out JsonElement toolCallsElement)
                || toolCallsElement.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            int fallbackIndex = 0;
            foreach (JsonElement toolCallElement in toolCallsElement.EnumerateArray())
            {
                int index = toolCallElement.TryGetProperty("index", out JsonElement indexElement) && indexElement.TryGetInt32(out int parsedIndex)
                    ? parsedIndex
                    : fallbackIndex;

                if (!accumulators.TryGetValue(index, out StreamingToolCallAccumulator? accumulator))
                {
                    accumulator = new StreamingToolCallAccumulator();
                    accumulators[index] = accumulator;
                }

                if (toolCallElement.TryGetProperty("id", out JsonElement idElement))
                {
                    string id = idElement.GetString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(id))
                        accumulator.Id = id;
                }

                if (toolCallElement.TryGetProperty("function", out JsonElement functionElement)
                    && functionElement.ValueKind == JsonValueKind.Object)
                {
                    if (functionElement.TryGetProperty("name", out JsonElement nameElement))
                    {
                        string namePart = nameElement.GetString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(namePart))
                            accumulator.Name.Append(namePart);
                    }

                    if (functionElement.TryGetProperty("arguments", out JsonElement argumentsElement))
                    {
                        string argumentsPart = argumentsElement.GetString() ?? string.Empty;
                        if (!string.IsNullOrEmpty(argumentsPart))
                            accumulator.Arguments.Append(argumentsPart);
                    }
                }

                fallbackIndex++;
            }
        }

        private static IReadOnlyList<OpenRouterToolCall> BuildStreamingToolCalls(IDictionary<int, StreamingToolCallAccumulator> accumulators)
        {
            if (accumulators == null || accumulators.Count == 0)
                return Array.Empty<OpenRouterToolCall>();

            return accumulators
                .OrderBy(pair => pair.Key)
                .Select(pair => new OpenRouterToolCall(
                    string.IsNullOrWhiteSpace(pair.Value.Id) ? Guid.NewGuid().ToString("N") : pair.Value.Id,
                    pair.Value.Name.ToString(),
                    string.IsNullOrWhiteSpace(pair.Value.Arguments.ToString()) ? "{}" : pair.Value.Arguments.ToString()))
                .Where(call => !string.IsNullOrWhiteSpace(call.Name))
                .ToArray();
        }

        private sealed class StreamingToolCallAccumulator
        {
            public string Id { get; set; } = string.Empty;
            public StringBuilder Name { get; } = new();
            public StringBuilder Arguments { get; } = new();
        }

        private static string ExtractContentText(JsonElement content, bool includeReasoningParts)
        {
            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? string.Empty;

            if (content.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (JsonElement item in content.EnumerateArray())
                {
                    string type = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("type", out JsonElement typeElement)
                        ? typeElement.GetString() ?? string.Empty
                        : string.Empty;

                    bool isReasoningPart = type.Contains("reason", StringComparison.OrdinalIgnoreCase);
                    if (isReasoningPart != includeReasoningParts)
                        continue;

                    if (item.TryGetProperty("text", out JsonElement textElement))
                        builder.AppendLine(textElement.GetString() ?? string.Empty);
                    else if (item.TryGetProperty("content", out JsonElement contentElement))
                        builder.AppendLine(ExtractContentText(contentElement, includeReasoningParts));
                }

                return builder.ToString().Trim();
            }

            if (content.ValueKind == JsonValueKind.Object)
            {
                if (content.TryGetProperty("text", out JsonElement textElement))
                    return textElement.GetString() ?? string.Empty;

                if (content.TryGetProperty("content", out JsonElement nestedContent))
                    return ExtractContentText(nestedContent, includeReasoningParts);

                if (content.TryGetProperty("summary", out JsonElement summaryElement))
                    return ExtractContentText(summaryElement, includeReasoningParts);
            }

            return string.Empty;
        }

        private static string ExtractReasoningPartsFromContent(JsonElement content)
        {
            return ExtractContentText(content, includeReasoningParts: true);
        }

        private static List<OpenRouterMessage> BuildConversation(List<OpenRouterMessage> conversationHistory, string userMessage)
        {
            var messages = conversationHistory?.Where(m => m != null).ToList() ?? new List<OpenRouterMessage>();
            messages.Add(new OpenRouterMessage("user", userMessage ?? string.Empty));
            return messages;
        }

        private static bool IsUnlimitedFreeTierKey(JsonElement root)
        {
            // Matches OpenRouter's /key response: { "data": { "limit": null, ... } }
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetPropertyIgnoreCase(root, "data", out JsonElement data)
                || data.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // A present-but-null "limit" means there is no usage cap on this key.
            return TryGetPropertyIgnoreCase(data, "limit", out JsonElement limit)
                && limit.ValueKind == JsonValueKind.Null;
        }

        private static bool TryExtractDailyRequests(JsonElement element, out int requestsUsed, out int requestsLimit)
        {
            requestsUsed = 0;
            requestsLimit = 0;

            if (TryExtractRequestUsageFromStructuredObject(element, out requestsUsed, out requestsLimit))
                return true;

            if (!TryFindNumericProperty(element, new[] { "requests_used", "used_requests", "daily_requests_used", "daily_used_requests" }, out requestsUsed)
                && !TryFindNumericProperty(element, new[] { "used", "usage" }, out requestsUsed))
            {
                return false;
            }

            if (!TryFindNumericProperty(element, new[] { "requests_limit", "daily_requests_limit", "daily_limit_requests", "limit_requests" }, out requestsLimit)
                && !TryFindNumericProperty(element, new[] { "limit", "max_requests", "requests_max" }, out requestsLimit))
            {
                if (TryFindNumericProperty(element, new[] { "remaining", "requests_remaining", "remaining_requests" }, out int requestsRemaining)
                    && requestsRemaining >= 0
                    && requestsRemaining >= requestsUsed)
                {
                    requestsLimit = requestsRemaining;
                    requestsUsed = 0;
                    return true;
                }

                return false;
            }

            return true;
        }

        private static bool TryExtractRequestUsageFromStructuredObject(JsonElement element, out int requestsUsed, out int requestsLimit)
        {
            requestsUsed = 0;
            requestsLimit = 0;

            if (element.ValueKind != JsonValueKind.Object)
            {
                if (element.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        if (TryExtractRequestUsageFromStructuredObject(item, out requestsUsed, out requestsLimit))
                            return true;
                    }
                }

                return false;
            }

            if (TryExtractUsageLimitPair(element, out requestsUsed, out requestsLimit))
                return true;

            foreach (string containerName in new[] { "data", "daily", "requests", "request", "usage", "limit", "limits", "rate_limit" })
            {
                if (TryGetPropertyIgnoreCase(element, containerName, out JsonElement nested)
                    && TryExtractRequestUsageFromStructuredObject(nested, out requestsUsed, out requestsLimit))
                {
                    return true;
                }
            }

            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (TryExtractRequestUsageFromStructuredObject(property.Value, out requestsUsed, out requestsLimit))
                    return true;
            }

            return false;
        }

        private static bool TryExtractUsageLimitPair(JsonElement element, out int requestsUsed, out int requestsLimit)
        {
            requestsUsed = 0;
            requestsLimit = 0;

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            if (TryGetPropertyIgnoreCase(element, "usage", out JsonElement usageElement)
                && TryGetPropertyIgnoreCase(element, "limit", out JsonElement limitElement)
                && TryExtractRequestCount(usageElement, out requestsUsed)
                && TryExtractRequestCount(limitElement, out requestsLimit))
            {
                return requestsLimit > 0;
            }

            if (TryGetPropertyIgnoreCase(element, "used", out JsonElement usedElement)
                && TryGetPropertyIgnoreCase(element, "limit", out JsonElement directLimitElement)
                && TryExtractRequestCount(usedElement, out requestsUsed)
                && TryExtractRequestCount(directLimitElement, out requestsLimit))
            {
                return requestsLimit > 0;
            }

            if (TryGetPropertyIgnoreCase(element, "used", out usedElement)
                && TryGetPropertyIgnoreCase(element, "remaining", out JsonElement remainingElement)
                && TryExtractRequestCount(usedElement, out requestsUsed)
                && TryExtractRequestCount(remainingElement, out int requestsRemaining))
            {
                requestsLimit = requestsUsed + Math.Max(0, requestsRemaining);
                return requestsLimit > 0;
            }

            if (TryGetPropertyIgnoreCase(element, "requests", out JsonElement requestsElement)
                && TryExtractUsageLimitPair(requestsElement, out requestsUsed, out requestsLimit))
            {
                return true;
            }

            if (TryGetPropertyIgnoreCase(element, "daily", out JsonElement dailyElement)
                && TryExtractUsageLimitPair(dailyElement, out requestsUsed, out requestsLimit))
            {
                return true;
            }

            return false;
        }

        private static bool TryExtractRequestCount(JsonElement element, out int value)
        {
            value = 0;

            if (TryReadInt(element, out value))
                return true;

            if (element.ValueKind != JsonValueKind.Object)
                return false;

            return TryFindNumericProperty(element, new[] { "requests", "count", "value", "used", "limit", "remaining" }, out value);
        }

        private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                    {
                        value = property.Value;
                        return true;
                    }
                }
            }

            value = default;
            return false;
        }

        private static bool TryFindNumericProperty(JsonElement element, IReadOnlyCollection<string> propertyNames, out int value)
        {
            value = 0;

            if (element.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (propertyNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                        && TryReadInt(property.Value, out value))
                    {
                        return true;
                    }

                    if (TryFindNumericProperty(property.Value, propertyNames, out value))
                        return true;
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (TryFindNumericProperty(item, propertyNames, out value))
                        return true;
                }
            }

            return false;
        }

        private static bool TryReadInt(JsonElement element, out int value)
        {
            value = 0;

            try
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (element.TryGetInt32(out value))
                            return true;

                        if (element.TryGetInt64(out long longValue))
                        {
                            value = (int)Math.Clamp(longValue, int.MinValue, int.MaxValue);
                            return true;
                        }

                        if (element.TryGetDouble(out double doubleValue))
                        {
                            value = (int)Math.Round(doubleValue);
                            return true;
                        }

                        break;

                    case JsonValueKind.String:
                        return int.TryParse(element.GetString(), out value);
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private void ApplyHeaders(HttpRequestMessage request)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://axiom.local");
            request.Headers.TryAddWithoutValidation("X-Title", "Axiom");
        }

        private void SetDetectedModel(string modelId, string modelLabel)
        {
            DetectedModelId = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId.Trim();
            DetectedModelLabel = string.IsNullOrWhiteSpace(modelLabel) ? DetectedModelId : modelLabel.Trim();
        }

        private void ResetDetectedModel()
        {
            DetectedModelId = DefaultModelId;
            DetectedModelLabel = DefaultModelLabel;
            _availableModels = new List<(string Id, string Label, bool IsFree)>();
            _modelSupportedParameters.Clear();
        }

        private bool SupportsParameter(string modelId, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
                return true;

            if (_modelSupportedParameters.TryGetValue(modelId ?? string.Empty, out HashSet<string>? supportedParameters)
                && supportedParameters.Count > 0)
            {
                return supportedParameters.Contains(parameterName);
            }

            string normalizedModelId = (modelId ?? string.Empty).Trim();
            if (normalizedModelId.StartsWith("openai/gpt-oss-", StringComparison.OrdinalIgnoreCase))
            {
                // gpt-oss models don't support top_p or the reasoning field.
                return !string.Equals(parameterName, "top_p", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(parameterName, "reasoning", StringComparison.OrdinalIgnoreCase);
            }

            if (normalizedModelId.StartsWith("qwen/qwen3-coder", StringComparison.OrdinalIgnoreCase))
            {
                return !string.Equals(parameterName, "reasoning", StringComparison.OrdinalIgnoreCase);
            }

            if (normalizedModelId.StartsWith("meta-llama/", StringComparison.OrdinalIgnoreCase))
            {
                return !string.Equals(parameterName, "reasoning", StringComparison.OrdinalIgnoreCase);
            }

            return true;
        }

        private static bool ContainsIgnoredProvidersMessage(string responseBody)
        {
            return !string.IsNullOrWhiteSpace(responseBody)
                && responseBody.Contains("All providers have been ignored", StringComparison.OrdinalIgnoreCase);
        }

        private bool ShouldRetryWithFallback(HttpStatusCode statusCode, string modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return false;

            return statusCode == HttpStatusCode.NotFound
                || statusCode == HttpStatusCode.TooManyRequests
                || statusCode == HttpStatusCode.BadGateway
                || statusCode == HttpStatusCode.ServiceUnavailable
                || statusCode == HttpStatusCode.GatewayTimeout;
        }

        private IEnumerable<OpenRouterModelProfile> GetValidationProfiles(OpenRouterModelProfile requestedModelProfile)
        {
            if (requestedModelProfile != null)
                yield return requestedModelProfile;

            foreach (OpenRouterModelProfile candidate in AllKnownModelProfiles)
            {
                if (candidate == null)
                    continue;
                if (requestedModelProfile != null && string.Equals(candidate.AliasId, requestedModelProfile.AliasId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!IsSelectableModelAvailable(candidate.AliasId))
                    continue;

                yield return candidate;
            }
        }

        private static bool IsTransientProviderFailure(HttpStatusCode statusCode, string responseBody)
        {
            if (statusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                return true;

            return !string.IsNullOrWhiteSpace(responseBody)
                && (responseBody.Contains("Provider returned error", StringComparison.OrdinalIgnoreCase)
                    || responseBody.Contains("no healthy upstream", StringComparison.OrdinalIgnoreCase)
                    || responseBody.Contains("temporarily unavailable", StringComparison.OrdinalIgnoreCase));
        }

        private string GetFallbackModelId(string currentModelId, ISet<string> attemptedModelIds)
        {
            // Each alias maps to a distinct, verified-working free model, so cascading through all
            // three on a rate-limit/transient failure gives the request three different live models
            // to try before giving up. (Eidos=gemma, Hepha=nemotron-super, Workplace=nemotron-nano.)
            IEnumerable<OpenRouterModelProfile> fallbackSequence = currentModelId switch
            {
                Eidos1ModelId =>
                    [FindModelProfile(Hepha1ModelId), FindModelProfile(WorkplaceCouncilDefaultModelId)],
                Hepha1ModelId =>
                    [FindModelProfile(Eidos1ModelId), FindModelProfile(WorkplaceCouncilDefaultModelId)],
                WorkplaceCouncilDefaultModelId =>
                    [FindModelProfile(Eidos1ModelId), FindModelProfile(Hepha1ModelId)],
                _ => AllKnownModelProfiles
            };

            foreach (OpenRouterModelProfile candidate in fallbackSequence)
            {
                if (candidate == null)
                    continue;
                if (string.Equals(candidate.AliasId, currentModelId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (attemptedModelIds?.Contains(candidate.AliasId) == true)
                    continue;
                if (!IsSelectableModelAvailable(candidate.AliasId))
                    continue;

                return candidate.AliasId;
            }

            return string.Empty;
        }

        private async Task<(HttpStatusCode StatusCode, string ResponseBody)> ProbeModelAvailabilityAsync(string effectiveModelId, CancellationToken cancellationToken)
        {
            for (int retryAttempt = 0; ; retryAttempt++)
            {
                using var request = BuildChatRequest(
                    BuildConversation(new List<OpenRouterMessage>(), "Reply with OK."),
                    "You are validating model connectivity. Reply with OK.",
                    effectiveModelId,
                    false,
                    0.1,
                    0.8,
                    8,
                    null);

                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                string responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.StatusCode == HttpStatusCode.OK)
                    return (response.StatusCode, responseBody);

                bool shouldRetry = (response.StatusCode == HttpStatusCode.TooManyRequests || IsTransientProviderFailure(response.StatusCode, responseBody))
                    && retryAttempt < TransientOpenRouterRetryLimit;
                if (!shouldRetry)
                    return (response.StatusCode, responseBody);

                TimeSpan retryDelay = GetRetryDelay(response, responseBody, retryAttempt);
                await BackendLogService.LogEventAsync("OpenRouterTransientRetry", $"Model:{effectiveModelId}\nStatus:{(int)response.StatusCode} ({response.StatusCode})\nRetryInSeconds:{retryDelay.TotalSeconds:F1}\nAttempt:{retryAttempt + 1}");
                await Task.Delay(retryDelay, cancellationToken);
            }
        }

        // Hard cap on any in-place retry wait. Free-tier 429s report retry_after of 30s, but blocking
        // a chat for 30s is unacceptable — we'd rather fall back to a different working model fast.
        private static readonly TimeSpan MaxInPlaceRetryDelay = TimeSpan.FromSeconds(4);

        private static bool IsServerTransientStatus(HttpStatusCode statusCode)
            => statusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout;

        private static TimeSpan GetRetryDelay(HttpResponseMessage response, string responseBody, int retryAttempt)
        {
            TimeSpan delay = TimeSpan.FromSeconds(Math.Min(8, 2 + (retryAttempt * 2)));

            if (response?.Headers?.RetryAfter?.Delta is TimeSpan retryAfterDelta && retryAfterDelta > TimeSpan.Zero)
                delay = retryAfterDelta;
            else if (response?.Headers?.RetryAfter?.Date is DateTimeOffset retryAfterDate
                && retryAfterDate - DateTimeOffset.UtcNow is TimeSpan untilRetry && untilRetry > TimeSpan.Zero)
                delay = untilRetry;
            else
            {
                Match retryAfterMatch = RetryAfterSecondsRegex.Match(responseBody ?? string.Empty);
                if (retryAfterMatch.Success && int.TryParse(retryAfterMatch.Groups["value"].Value, out int retryAfterSeconds) && retryAfterSeconds > 0)
                    delay = TimeSpan.FromSeconds(retryAfterSeconds);
            }

            return delay > MaxInPlaceRetryDelay ? MaxInPlaceRetryDelay : delay;
        }

        // Uncapped retry-delay extraction (seconds) for the rate-limit exception. The caller decides
        // how long it is willing to wait; here we just surface what the provider asked for.
        private static int ParseRetryAfterSeconds(HttpResponseMessage? response, string? responseBody)
        {
            if (response?.Headers?.RetryAfter?.Delta is TimeSpan retryAfterDelta && retryAfterDelta > TimeSpan.Zero)
                return (int)Math.Ceiling(retryAfterDelta.TotalSeconds);

            if (response?.Headers?.RetryAfter?.Date is DateTimeOffset retryAfterDate
                && retryAfterDate - DateTimeOffset.UtcNow is TimeSpan untilRetry && untilRetry > TimeSpan.Zero)
                return (int)Math.Ceiling(untilRetry.TotalSeconds);

            Match retryAfterMatch = RetryAfterSecondsRegex.Match(responseBody ?? string.Empty);
            if (retryAfterMatch.Success && int.TryParse(retryAfterMatch.Groups["value"].Value, out int retryAfterSeconds) && retryAfterSeconds > 0)
                return retryAfterSeconds;

            return 0;
        }

        private string ResolveRequestedModelId(OpenRouterModelProfile profile)
        {
            return ResolveAvailableApiModelId(profile) ?? profile.PrimaryApiModelId;
        }

        private OpenRouterModelProfile ResolveRequestedModelProfile(string modelId)
        {
            string normalized = NormalizeModelId(modelId);
            // Always honour the caller's selection — silent model switching causes wrong-model routing.
            // Fallback after an actual request failure is handled by the retry/fallback loop.
            return FindModelProfile(normalized) ?? SupportedModelProfiles[0];
        }

        private bool IsProfileAvailable(OpenRouterModelProfile profile)
        {
            return ResolveAvailableApiModelId(profile) != null;
        }

        private string ResolveAvailableApiModelId(OpenRouterModelProfile profile)
        {
            if (profile == null)
                return null;

            foreach (string apiModelId in profile.AllApiModelIds)
            {
                if (_availableModels.Any(m => string.Equals(m.Id, apiModelId, StringComparison.OrdinalIgnoreCase)))
                    return apiModelId;
            }

            return null;
        }

        private static OpenRouterModelProfile FindModelProfile(string modelId)
        {
            string normalized = (modelId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return SupportedModelProfiles[0];

            return AllKnownModelProfiles.FirstOrDefault(profile =>
                string.Equals(profile.AliasId, normalized, StringComparison.OrdinalIgnoreCase)
                || profile.AllApiModelIds.Any(apiModelId => string.Equals(apiModelId, normalized, StringComparison.OrdinalIgnoreCase)));
        }

        private static IEnumerable<OpenRouterModelProfile> AllKnownModelProfiles => SupportedModelProfiles.Concat(WorkplaceOnlyModelProfiles);

        private static double ResolveTemperature(OpenRouterModelProfile profile, bool isCodingRequest, bool isPythonRequest)
        {
            if (profile?.IsCodeSpecialized == true)
                return isCodingRequest ? (isPythonRequest ? 0.08 : 0.12) : 0.2;

            return isCodingRequest ? (isPythonRequest ? 0.1 : 0.16) : 0.55;
        }

        private static double ResolveTopP(OpenRouterModelProfile profile, bool isCodingRequest)
        {
            if (profile?.IsCodeSpecialized == true)
                return isCodingRequest ? 0.72 : 0.82;

            return isCodingRequest ? 0.8 : 0.9;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            string trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private bool DetectCodingRequest(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return false;

            return CodingRequestSignals.Any(signal => userMessage.Contains(signal, StringComparison.OrdinalIgnoreCase));
        }

        private bool DetectPythonRequest(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return false;

            return PythonRequestSignals.Any(signal => userMessage.Contains(signal, StringComparison.OrdinalIgnoreCase));
        }

        private static string NormalizeRole(string role)
        {
            if (string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase)
                || string.Equals(role, "model", StringComparison.OrdinalIgnoreCase))
                return "assistant";

            if (string.Equals(role, "system", StringComparison.OrdinalIgnoreCase))
                return "system";

            if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
                return "tool";

            return "user";
        }

        private static string NormalizeModelId(string modelId)
        {
            string normalized = (modelId ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return DefaultModelId;

            OpenRouterModelProfile profile = FindModelProfile(normalized);
            return profile?.AliasId ?? DefaultModelId;
        }
    }
}
