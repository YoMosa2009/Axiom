using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed record ChatPipelineRequest(
        string SystemPrompt,
        string UserMessage,
        IReadOnlyList<OpenRouterMessage>? ConversationHistory = null,
        bool ThinkingEnabled = false,
        IReadOnlyList<OpenRouterToolDefinition>? Tools = null);

    public sealed record ChatPipelineResult(
        string ResponseText,
        string ReasoningText,
        IReadOnlyList<OpenRouterToolCall>? ToolCalls = null);

    // Unified abstraction over cloud and local inference backends.
    // Both Normal Chat and Workplace Council can target this interface,
    // eliminating scattered if (_cloudModeActive) branches at call sites.
    public interface IChatPipeline
    {
        Task<ChatPipelineResult> ExecuteAsync(
            ChatPipelineRequest request,
            Action<string>? onToken,
            CancellationToken cancellationToken);
    }

    // Cloud implementation backed by OpenRouter.
    public sealed class CloudChatPipeline : IChatPipeline
    {
        private readonly OpenRouterChatService _service;
        private readonly string _modelId;

        public CloudChatPipeline(OpenRouterChatService service, string modelId)
        {
            _service = service;
            _modelId = modelId;
        }

        public async Task<ChatPipelineResult> ExecuteAsync(
            ChatPipelineRequest request,
            Action<string>? onToken,
            CancellationToken cancellationToken)
        {
            var messages = new List<OpenRouterMessage>(request.ConversationHistory ?? Array.Empty<OpenRouterMessage>());
            messages.Add(new OpenRouterMessage("user", request.UserMessage ?? string.Empty));

            OpenRouterChatResponse response = onToken != null
                ? await _service.SendConversationStreamAsync(
                    messages, request.SystemPrompt, request.ThinkingEnabled, _modelId,
                    request.Tools, onToken, cancellationToken)
                : await _service.SendConversationAsync(
                    messages, request.SystemPrompt, request.ThinkingEnabled, _modelId,
                    request.Tools, cancellationToken);

            return new ChatPipelineResult(
                response.Text ?? string.Empty,
                response.Reasoning ?? string.Empty,
                response.ToolCalls);
        }
    }
}
