using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Text.RegularExpressions;
using System.Net.Http;
using ICSharpCode.AvalonEdit.Highlighting;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace Malx_AI
{
    public partial class WorkplaceView : UserControl
    {
        private static string WorkplaceCloudRoleDisplayName => OpenRouterChatService.WorkplaceCouncilDisplayLabel;

        private enum CouncilRole
        {
            Architect,
            Builder,
            Critic
        }

        public event Action<bool>? CouncilPetToggleRequested;
        public event Action<string, string>? CouncilPetStatusChanged;

        public void SetCouncilPetEnabled(bool enabled)
        {
            _isCouncilPetEnabled = enabled;
            RefreshCouncilPetToggleUi();
            PublishCouncilPetStatus(enabled ? "Council Bit" : "Council", enabled ? "Watching the council." : "Hidden.");
        }

        private void RefreshCouncilPetToggleUi()
        {
            if (FindName("CouncilPetToggleButton") is not Button petButton)
                return;

            petButton.Content = _isCouncilPetEnabled ? "Bit On" : "Bit";
            petButton.Opacity = _isCouncilPetEnabled ? 1.0 : 0.68;
            petButton.ToolTip = _isCouncilPetEnabled
                ? "Hide the Council Bit status companion"
                : "Show the draggable Council Bit status companion";
        }

        private void PublishCouncilPetStatus(string role, string message)
        {
            if (_isCouncilPetEnabled)
                CouncilPetStatusChanged?.Invoke(role, message);
        }

        private void RefreshWorkplaceWebToggleUi()
        {
            if (WebSearchToggleButton == null)
                return;

            WebSearchToggleButton.Content = "Web";
            WebSearchToggleButton.Opacity = _isWebSearchEnabled ? 1.0 : 0.45;
            WebSearchToggleButton.ToolTip = _isWebSearchEnabled
                ? "Web Search tool enabled"
                : "Web Search tool disabled";
        }

        private void RefreshCodebaseAccessUi()
        {
            bool enabled = _connectedWorkspace.CodebaseEditAccessEnabled;
            string lockedMode = string.IsNullOrWhiteSpace(_connectedWorkspace.LockedMode)
                ? (_isCloudModeEnabled ? WorkspaceAgentMode.Cloud.ToString() : WorkspaceAgentMode.Local.ToString())
                : _connectedWorkspace.LockedMode;

            if (CodebaseAccessModeText != null)
                CodebaseAccessModeText.Text = enabled ? lockedMode : "Off";

            if (CodebaseEditAccessButton != null)
            {
                CodebaseEditAccessButton.Content = enabled ? "Enabled - locked for this chat" : "1. Enable for this chat";
                CodebaseEditAccessButton.IsEnabled = !enabled && !_isProcessing;
                CodebaseEditAccessButton.ToolTip = enabled
                    ? "Locked for this Workplace chat. Start a new Workplace chat to disable or change mode."
                    : "Enable codebase access and lock this Workplace chat to the current local/cloud mode.";
            }

            if (ConnectWorkspaceFolderButton != null)
                ConnectWorkspaceFolderButton.IsEnabled = enabled && !_isProcessing;
            if (ConnectWorkspaceFilesButton != null)
                ConnectWorkspaceFilesButton.IsEnabled = enabled && !_isProcessing;
            if (ConnectWorkspaceRepositoryButton != null)
                ConnectWorkspaceRepositoryButton.IsEnabled = enabled && !_isProcessing;
            if (CloneWorkspaceRepositoryButton != null)
            {
                bool canClone = enabled
                    && !_isProcessing
                    && !string.IsNullOrWhiteSpace(_connectedWorkspace.RepositoryUrl)
                    && string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath);
                CloneWorkspaceRepositoryButton.IsEnabled = canClone;
                CloneWorkspaceRepositoryButton.Visibility = enabled && !string.IsNullOrWhiteSpace(_connectedWorkspace.RepositoryUrl)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (CodebaseAutoApplyToggle != null)
            {
                CodebaseAutoApplyToggle.IsEnabled = enabled && !_isProcessing;
                CodebaseAutoApplyToggle.IsChecked = _connectedWorkspace.AutoApplyCodebaseChanges;
                CodebaseAutoApplyToggle.Opacity = enabled ? 1.0 : 0.45;
            }

            if (ConnectedWorkspaceStatusBlock != null)
            {
                bool hasConnectedCode = !string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath)
                    || _connectedWorkspace.ConnectedFiles.Count > 0;
                ConnectedWorkspaceStatusBlock.Text = !enabled
                    ? "Step 1: enable access. This locks the chat to the current local/cloud mode."
                    : _hasPendingCodebaseChanges
                        ? "Review pending code changes in Project Canvas."
                        : hasConnectedCode
                            ? $"Ready in {lockedMode} mode. Ask for a small code change to get a reviewable patch."
                            : $"Enabled in {lockedMode} mode. Step 2: open a local folder or clone a repo.";
            }

            string details = "No code connected yet.";
            if (!string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath))
            {
                details = $"Connected: {_connectedWorkspace.DisplayName}\n{_connectedWorkspace.RootPath}\nIndexed {_connectedWorkspace.IndexedFileCount:n0} file(s)";
                if (_connectedWorkspace.IndexedByteCount > 0)
                    details += $" / {FormatByteCount(_connectedWorkspace.IndexedByteCount)}";
            }
            else if (_connectedWorkspace.ConnectedFiles.Count > 0)
            {
                details = $"Connected: {_connectedWorkspace.DisplayName}\n{_connectedWorkspace.ConnectedFiles.Count:n0} selected file(s)\nIndexed {_connectedWorkspace.IndexedFileCount:n0} file(s)";
                if (_connectedWorkspace.IndexedByteCount > 0)
                    details += $" / {FormatByteCount(_connectedWorkspace.IndexedByteCount)}";
            }
            else if (!string.IsNullOrWhiteSpace(_connectedWorkspace.RepositoryUrl))
            {
                details = $"Repo URL saved: {_connectedWorkspace.DisplayName}\n{_connectedWorkspace.RepositoryUrl}\nChoose a destination folder to clone before editing.";
            }

            if (!string.IsNullOrWhiteSpace(_connectedWorkspace.StatusMessage))
                details += "\n" + _connectedWorkspace.StatusMessage;

            if (ConnectedWorkspaceDetailBlock != null)
                ConnectedWorkspaceDetailBlock.Text = details;

            if (ConnectedWorkspaceConnectHintBlock != null)
                ConnectedWorkspaceConnectHintBlock.Text = enabled
                    ? "Step 2: open local code, pick files, or clone a GitHub repo in one step."
                    : "Step 2 unlocks after access is enabled.";

            if (CodebaseReviewHintBlock != null)
                CodebaseReviewHintBlock.Text = _hasPendingCodebaseChanges
                    ? "Patch ready: inspect Project Canvas, then accept or reject."
                    : _lastCodebaseUndo != null
                        ? "Last patch can be undone from Project Canvas or this panel."
                    : _connectedWorkspace.AutoApplyCodebaseChanges
                        ? "Step 3: ask for a change. Valid patches will be applied automatically."
                        : "Step 3: ask for a change. Proposed edits appear in Project Canvas for review.";

            if (CodebaseAutoApplyHintBlock != null)
                CodebaseAutoApplyHintBlock.Text = _connectedWorkspace.AutoApplyCodebaseChanges
                    ? "Auto mode: valid patches are written after parsing, path, and file checks."
                    : "Manual mode: review patches before writing files.";

            Visibility reviewVisibility = enabled && (!_connectedWorkspace.AutoApplyCodebaseChanges || _hasPendingCodebaseChanges)
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (AcceptCodebaseChangesButton != null)
            {
                AcceptCodebaseChangesButton.Visibility = reviewVisibility;
                AcceptCodebaseChangesButton.IsEnabled = _hasPendingCodebaseChanges;
            }
            if (RejectCodebaseChangesButton != null)
            {
                RejectCodebaseChangesButton.Visibility = reviewVisibility;
                RejectCodebaseChangesButton.IsEnabled = _hasPendingCodebaseChanges;
            }
            Visibility undoVisibility = enabled && _lastCodebaseUndo != null && !_hasPendingCodebaseChanges
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (UndoCodebaseChangesButton != null)
            {
                UndoCodebaseChangesButton.Visibility = undoVisibility;
                UndoCodebaseChangesButton.IsEnabled = undoVisibility == Visibility.Visible;
            }
            if (UndoCodebaseChangesSidebarButton != null)
            {
                UndoCodebaseChangesSidebarButton.Visibility = undoVisibility;
                UndoCodebaseChangesSidebarButton.IsEnabled = undoVisibility == Visibility.Visible;
            }
            if (CodebaseReviewActionGrid != null)
                CodebaseReviewActionGrid.Visibility = reviewVisibility;
            if (AcceptCodebaseChangesSidebarButton != null)
                AcceptCodebaseChangesSidebarButton.IsEnabled = _hasPendingCodebaseChanges;
            if (RejectCodebaseChangesSidebarButton != null)
                RejectCodebaseChangesSidebarButton.IsEnabled = _hasPendingCodebaseChanges;
        }

        private static string FormatByteCount(long bytes)
        {
            string[] units = ["B", "KB", "MB", "GB"];
            double value = Math.Max(0, bytes);
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.0} {units[unit]}";
        }

        private ConnectedWorkspaceState CloneConnectedWorkspaceState()
        {
            return new ConnectedWorkspaceState
            {
                CodebaseEditAccessEnabled = _connectedWorkspace.CodebaseEditAccessEnabled,
                AutoApplyCodebaseChanges = _connectedWorkspace.AutoApplyCodebaseChanges,
                LockedMode = _connectedWorkspace.LockedMode,
                ConnectionKind = _connectedWorkspace.ConnectionKind,
                RootPath = _connectedWorkspace.RootPath,
                RepositoryUrl = _connectedWorkspace.RepositoryUrl,
                DisplayName = _connectedWorkspace.DisplayName,
                IndexedFileCount = _connectedWorkspace.IndexedFileCount,
                IndexedByteCount = _connectedWorkspace.IndexedByteCount,
                EnabledAt = _connectedWorkspace.EnabledAt,
                IndexedAt = _connectedWorkspace.IndexedAt,
                StatusMessage = _connectedWorkspace.StatusMessage,
                ConnectedFiles = _connectedWorkspace.ConnectedFiles.ToList()
            };
        }

        private void RefreshWorkplaceCloudModeUi()
        {
            if (FindName("WorkplaceCloudModeButton") is not Button cloudModeButton)
                return;

            if (_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                cloudModeButton.Content = _isCloudModeEnabled ? "Cloud On" : "Cloud Off";
                cloudModeButton.Opacity = 0.55;
                cloudModeButton.IsEnabled = false;
                cloudModeButton.ToolTip = "Cloud/local mode is locked while Codebase Edit Access is enabled in this Workplace chat. Start a new Workplace chat to change it.";
            }
            else
            {
                cloudModeButton.IsEnabled = true;
                cloudModeButton.Content = _isCloudModeEnabled ? "Cloud On" : "Cloud Off";
                cloudModeButton.Opacity = _isCloudModeEnabled ? 1.0 : 0.75;
                cloudModeButton.ToolTip = _isCloudModeEnabled
                    ? $"Council cloud mode enabled ({OpenRouterChatService.WorkplaceCouncilDisplayLabel})"
                    : "Use local council models";
            }

            bool localControlsEnabled = !_isCloudModeEnabled;
            if (FindName("LoadArchitectModelButton") is Button architectLoadButton) architectLoadButton.IsEnabled = localControlsEnabled;
            if (FindName("LoadBuilderModelButton") is Button builderLoadButton) builderLoadButton.IsEnabled = localControlsEnabled;
            if (FindName("LoadCriticModelButton") is Button criticLoadButton) criticLoadButton.IsEnabled = localControlsEnabled;
            if (ArchitectFormatCombo != null) ArchitectFormatCombo.IsEnabled = localControlsEnabled;
            if (BuilderFormatCombo != null) BuilderFormatCombo.IsEnabled = localControlsEnabled;
            if (CriticFormatCombo != null) CriticFormatCombo.IsEnabled = localControlsEnabled;

            if (AutoOptimizeContextToggle != null)
            {
                AutoOptimizeContextToggle.IsEnabled = localControlsEnabled;
                AutoOptimizeContextToggle.Visibility = localControlsEnabled ? Visibility.Visible : Visibility.Collapsed;
            }

            // ContextSlidersPanel is a named StackPanel in XAML — hide sliders in cloud mode
            if (ContextSlidersPanel != null)
                ContextSlidersPanel.Visibility = localControlsEnabled ? Visibility.Visible : Visibility.Collapsed;

            // Cloud context info block: show effective context when cloud mode is on
            if (CloudContextInfoBlock != null)
            {
                if (_isCloudModeEnabled)
                {
                    int cloudWindow = _openRouterChatService.GetApproximateContextWindowTokens(OpenRouterChatService.WorkplaceCouncilDefaultModelId);
                    int builderCtx = cloudWindow;
                    int otherCtx = cloudWindow / 2;
                    CloudContextInfoBlock.Text = $"Provider-managed: {builderCtx / 1024}K window · Builder {builderCtx / 1024}K · Architect & Critic {otherCtx / 1024}K";
                    CloudContextInfoBlock.Visibility = Visibility.Visible;
                }
                else
                {
                    CloudContextInfoBlock.Visibility = Visibility.Collapsed;
                    // Re-derive local context when returning from cloud mode
                    if (_autoOptimizeRoleContexts)
                    {
                        ApplyOptimizedRoleContexts();
                        SyncContextControls();
                    }
                }
            }
        }

        private bool CanUseCloudCouncil => _isCloudModeEnabled && _openRouterChatService.HasValidKey;

        private string GetCouncilDisplayName(CouncilRole role)
        {
            return _isCloudModeEnabled
                ? $"{WorkplaceCloudRoleDisplayName} · {role}"
                : _council[role].DisplayName;
        }

        private string GetCouncilStatusDescription()
        {
            if (_isCloudModeEnabled)
            {
                return CanUseCloudCouncil
                    ? $"Cloud council · {WorkplaceCloudRoleDisplayName}"
                    : "Cloud council unavailable · add OpenRouter key in Settings";
            }

            return "Adaptive mode";
        }

        private OpenRouterConnectionTestFailureReason ResolveCloudFailureReason(Exception ex)
        {
            if (ex is InvalidOperationException invalid)
            {
                string message = invalid.Message ?? string.Empty;
                if (message.Contains("rate-limited", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("quota-limited", StringComparison.OrdinalIgnoreCase))
                    return OpenRouterConnectionTestFailureReason.RateLimited;
                if (message.Contains("provider", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("cannot route", StringComparison.OrdinalIgnoreCase))
                    return OpenRouterConnectionTestFailureReason.ProviderUnavailable;
                if (message.Contains("valid OpenRouter API key", StringComparison.OrdinalIgnoreCase))
                    return OpenRouterConnectionTestFailureReason.InvalidKey;
            }

            if (ex is HttpRequestException || ex is TaskCanceledException)
                return OpenRouterConnectionTestFailureReason.NetworkError;

            return OpenRouterConnectionTestFailureReason.Failed;
        }

        private string BuildCloudModeErrorMessage(OpenRouterConnectionTestFailureReason reason)
        {
            return reason switch
            {
                OpenRouterConnectionTestFailureReason.InvalidKey => "Workplace cloud mode needs a valid OpenRouter API key from Settings.",
                OpenRouterConnectionTestFailureReason.RateLimited => "OpenRouter cloud mode is temporarily rate-limited. Try again shortly.",
                OpenRouterConnectionTestFailureReason.ProviderUnavailable => "The workplace cloud model is unavailable on OpenRouter right now.",
                OpenRouterConnectionTestFailureReason.NetworkError => "Unable to reach OpenRouter for workplace cloud mode.",
                OpenRouterConnectionTestFailureReason.RequestFormatError => "OpenRouter rejected the workplace cloud request format.",
                _ => "Workplace cloud mode failed. Check OpenRouter settings and try again."
            };
        }

        private async Task<ReasoningParser.ParsedResponse> ExecuteCouncilRoleCloudAsync(
            CouncilRole role,
            string systemPrompt,
            string userPayload,
            CancellationToken token,
            bool showLiveCard = true)
        {
            if (!CanUseCloudCouncil)
                throw new InvalidOperationException("A valid OpenRouter API key is required for workplace cloud mode.");

            string roleName = role.ToString();
            string roleKey = roleName.ToLowerInvariant();
            LogActivity($"{roleName}: OpenRouter cloud execution started (streaming).");

            // Create a streaming placeholder card so tokens appear in the UI in real-time.
            // If a previous card exists for this role (retry scenario), remove it first.
            // Internal pipeline steps (summarizer, study session, python auto-retry) pass
            // showLiveCard=false so they never surface a visible card for hidden work.
            var streamingCard = new WorkplaceChatMessage { Role = roleKey };
            if (showLiveCard)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_streamingCouncilCards.TryGetValue(roleKey, out var stale))
                    {
                        _chatCards.Remove(stale);
                        _streamingCouncilCards.Remove(roleKey);
                    }
                    _chatCards.Add(streamingCard);
                    _streamingCouncilCards[roleKey] = streamingCard;
                    ChatScrollViewer?.ScrollToEnd();
                }, DispatcherPriority.Background);
            }

            // Cloud roles use real OpenRouter tool-calling rather than the local-only [PAUSE:] protocol.
            // Swap the agentic-pause instructions for the native-tools note so the prompt is coherent
            // with the tools we actually provide below, and align the environment briefing's local
            // tool references (pause tools, WEB_SEARCH) with the cloud tool names.
            string cloudSystemPrompt = (systemPrompt ?? string.Empty)
                .Replace(AgenticPauseRule, BuildCloudCouncilToolsNote(), StringComparison.Ordinal)
                .Replace("use the pause tools below", "use the tools provided", StringComparison.Ordinal)
                .Replace("except through WEB_SEARCH", "except through the web_search tool", StringComparison.Ordinal);
            cloudSystemPrompt += BuildCloudCouncilIntelligenceNote(role, _activeCouncilRunContext ?? _lastRunContext);
            if (role == CouncilRole.Builder)
            {
                cloudSystemPrompt += "\n\n[CLOUD BUILDER EXECUTION RULE]\n" +
                    "You MAY call the provided tools (web_search, run_python, calculate, search_session_memory) BEFORE you write your deliverable, " +
                    "whenever you need a real fact, number, computation, conversion, or current detail. Never guess or fabricate values you could verify with a tool. " +
                    "If proactive web evidence is partial, off-topic, or missing the user's named entities, call a narrower web_search before writing the final deliverable instead of treating the mismatched evidence as a reason to refuse the whole answer. " +
                    "For stable non-current background context, you may use the prompt, council plan, project knowledge, session memory, or general knowledge when not contradicted by source evidence. " +
                    "Gather every tool result you need first, then produce EXACTLY ONE final Builder deliverable that already incorporates those results. " +
                    "Once you begin writing the final deliverable, stop calling tools — do not restart, revise, repeat, or continue after it is complete.";
            }
            string adaptedSystemPrompt = _openRouterChatService.BuildSystemPromptForModel(
                OpenRouterChatService.WorkplaceCouncilDefaultModelId,
                cloudSystemPrompt);

            // Every cloud role — Builder included — now gets the full tool surface so it can ground
            // facts/math through the Agentic Pause tools. The remake-loop that originally forced the
            // Builder tool-free is contained by the substantial-deliverable guard in the tool loop,
            // which ignores tool calls emitted after a real Builder answer has already streamed.
            IReadOnlyList<OpenRouterToolDefinition> tools = BuildCouncilCloudToolDefinitions();

            // Bounded retry around transient free-tier rate limits. When every fallback model is 429,
            // the service throws OpenRouterRateLimitedException; rather than killing the whole relay we
            // wait the provider-suggested delay (capped) and retry, so a transient throttle does not
            // abort artifact iteration.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return await RunCloudCouncilRoleToolLoopAsync(role, roleName, adaptedSystemPrompt, tools, userPayload, streamingCard, token);
                }
                catch (OpenRouterRateLimitedException rateLimited)
                    when (attempt < CloudCouncilRateLimitRetryLimit && !token.IsCancellationRequested)
                {
                    int waitSeconds = Math.Clamp(
                        rateLimited.RetryAfterSeconds > 0 ? rateLimited.RetryAfterSeconds : DefaultCloudCouncilRateLimitWaitSeconds,
                        2,
                        MaxCloudCouncilRateLimitWaitSeconds);

                    LogActivity($"{roleName}: rate-limited (429). Waiting {waitSeconds}s before retry {attempt + 1}/{CloudCouncilRateLimitRetryLimit}.");
                    await BackendLogService.LogEventAsync("Workplace.CloudRateLimitRetry", $"Role:{roleName}\nWaitSeconds:{waitSeconds}\nAttempt:{attempt + 1}/{CloudCouncilRateLimitRetryLimit}");
                    await Dispatcher.InvokeAsync(() =>
                    {
                        RelayStatusBlock.Text = $"Relay: Rate-limited — retrying in {waitSeconds}s...";
                        streamingCard.Content = $"⏳ Provider rate-limited. Retrying in {waitSeconds}s (attempt {attempt + 1}/{CloudCouncilRateLimitRetryLimit})...";
                    }, DispatcherPriority.Background);

                    await Task.Delay(TimeSpan.FromSeconds(waitSeconds), token);
                }
            }
        }

        // Runs one cloud council role through the native tool-calling loop, streaming into the card.
        // Separated from ExecuteCouncilRoleCloudAsync so the rate-limit retry wrapper can re-invoke it.
        private async Task<ReasoningParser.ParsedResponse> RunCloudCouncilRoleToolLoopAsync(
            CouncilRole role,
            string roleName,
            string adaptedSystemPrompt,
            IReadOnlyList<OpenRouterToolDefinition> tools,
            string userPayload,
            WorkplaceChatMessage streamingCard,
            CancellationToken token)
        {
            var messages = new List<OpenRouterMessage>
            {
                // PreserveFullText: the council payload (architect plan, current artifact, document
                // context) must survive history trimming on tool-loop iterations 2+, where it is no
                // longer the final message.
                new("user", userPayload ?? string.Empty, PreserveFullText: true)
            };

            var reasoningParts = new List<string>();
            string finalText = string.Empty;
            int toolCallCount = 0;
            IReadOnlyList<string> stopSequences = BuildCloudCouncilRoleStopSequences(role);
            int maxTokens = ResolveCloudCouncilRoleMaxTokens(role, _activeCouncilRunContext ?? _lastRunContext);
            bool forceNoToolsSynthesisAfterBuilderGrounding = false;
            int executedToolCount = 0;
            var executedToolSignatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int iteration = 0; iteration < CloudCouncilToolLoopIterationLimit; iteration++)
            {
                // Fresh buffer per iteration so intermediate (tool-call) turns don't leave stale
                // partial text on the card before the final answer streams in.
                var textBuilder = new StringBuilder();
                IReadOnlyList<OpenRouterToolDefinition>? toolsForThisPass = forceNoToolsSynthesisAfterBuilderGrounding
                    || executedToolCount >= CloudCouncilToolExecutionLimit
                    ? null
                    : tools;
                OpenRouterChatResponse response = await _openRouterChatService.SendConversationStreamAsync(
                    messages,
                    adaptedSystemPrompt,
                    false,
                    OpenRouterChatService.WorkplaceCouncilDefaultModelId,
                    toolsForThisPass,
                    onToken: t =>
                    {
                        // Padding sentinels ("<pad>" spam) are already suppressed at the stream source,
                        // so the accumulated text is safe to show live without per-token cleanup.
                        textBuilder.Append(t);
                        string current = textBuilder.ToString();
                        int generatedTokens = EstimateTokenCount(current);
                        _pipelineTokenCount = Math.Max(_pipelineTokenCount, generatedTokens);
                        RecordCouncilRoleGeneratedTokens(role, generatedTokens);
                        string preview = BuildLiveStreamPreview(current);
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            streamingCard.Content = preview;
                            ChatScrollViewer?.ScrollToEnd();
                        }, DispatcherPriority.Background);
                    },
                    token,
                    maxTokens,
                    stopSequences);

                if (!string.IsNullOrWhiteSpace(response.Reasoning))
                    reasoningParts.Add(response.Reasoning.Trim());

                if (response.ToolCalls == null || response.ToolCalls.Count == 0)
                {
                    finalText = response.Text ?? string.Empty;
                    break;
                }

                bool builderHasGroundingToolCall = role == CouncilRole.Builder
                    && response.ToolCalls.Any(IsBuilderGroundingToolCall);

                if (role == CouncilRole.Builder
                    && !builderHasGroundingToolCall
                    && (response.Text?.Trim().Length ?? 0) >= BuilderFinalDeliverableMinChars)
                {
                    // Builder is the deliverable-producing role. Some OpenRouter models stream a
                    // complete answer and then also emit tool calls; treating that as an intermediate
                    // tool turn clears the visible Builder card and asks the model to remake the same
                    // output. Once a SUBSTANTIAL deliverable has streamed, keep it and ignore the late
                    // non-grounding tool calls. Grounding calls (web/calculator/python) must still run:
                    // otherwise the Builder can keep a hallucinated draft and skip the evidence it asked for.
                    finalText = response.Text ?? string.Empty;
                    LogActivity($"{roleName}: ignored {response.ToolCalls.Count} late tool call(s) after substantial Builder output ({response.Text?.Trim().Length ?? 0} chars).");
                    break;
                }

                // Tool-call turn: record the assistant turn (with its tool calls), run each tool,
                // and feed the results back so the model can continue with grounded data.
                toolCallCount += response.ToolCalls.Count;
                messages.Add(new OpenRouterMessage("assistant", response.Text ?? string.Empty, null, response.ToolCalls));

                foreach (OpenRouterToolCall toolCall in response.ToolCalls)
                {
                    if (toolCall == null)
                        continue;

                    OpenRouterToolCall nonNullToolCall = toolCall;
                    string signature = (nonNullToolCall.Name ?? string.Empty).Trim() + "\n" + (nonNullToolCall.ArgumentsJson ?? string.Empty).Trim();
                    if (!executedToolSignatures.Add(signature))
                    {
                        messages.Add(new OpenRouterMessage(
                            "tool",
                            "Duplicate tool call suppressed. Use the prior observation and continue toward the final deliverable.",
                            nonNullToolCall.Id));
                        LogActivity($"{roleName}: duplicate cloud tool call suppressed ({nonNullToolCall.Name}).");
                        continue;
                    }

                    if (executedToolCount >= CloudCouncilToolExecutionLimit)
                    {
                        messages.Add(new OpenRouterMessage(
                            "tool",
                            $"Cloud tool execution budget exhausted ({CloudCouncilToolExecutionLimit}). Use existing observations and produce the final role output.",
                            nonNullToolCall.Id));
                        continue;
                    }

                    executedToolCount++;
                    UpdateCloudCouncilToolStatus(nonNullToolCall, executedToolCount);
                    string toolResult = await ExecuteCouncilCloudToolAsync(nonNullToolCall, messages, token);
                    messages.Add(new OpenRouterMessage("tool", BuildCouncilCloudToolResultMessage(toolResult, nonNullToolCall.Name), nonNullToolCall.Id));
                    UpdateAgenticPauseStatus($"Resuming generation - {Math.Min(executedToolCount, CloudCouncilToolExecutionLimit)}/{CloudCouncilToolExecutionLimit} tools used");
                }

                if (builderHasGroundingToolCall)
                {
                    // The text before a grounding tool call is only a draft. Force the next pass to
                    // synthesize from the returned evidence without more tool calls, instead of using
                    // the pre-tool draft as the final answer or entering a search/remake loop.
                    messages.Add(new OpenRouterMessage(
                        "user",
                        "Use the tool result(s) above to produce the final Builder deliverable now. Do not call more tools. Do not repeat the tool payload. Integrate only claims supported by the sources/results, and state any remaining evidence gap plainly."));
                    forceNoToolsSynthesisAfterBuilderGrounding = true;
                    finalText = string.Empty;
                }
                else
                {
                    // Carry the last turn's visible text forward as a fallback if the loop is exhausted.
                    finalText = response.Text ?? finalText;
                }
            }

            if (toolCallCount > 0)
                LogActivity($"{roleName}: cloud tool-calling used {toolCallCount} tool call(s).");

            // Tool-loop budget exhausted with no final answer text: force one synthesis pass with
            // tools disabled so the role still produces usable output from the gathered evidence,
            // instead of handing the pipeline an empty answer that trips the reasoning fallback.
            if (string.IsNullOrWhiteSpace(finalText) && (toolCallCount > 0 || role is CouncilRole.Builder or CouncilRole.Critic))
            {
                LogActivity($"{roleName}: tool loop ended without final text — running forced no-tools synthesis pass.");
                if (role == CouncilRole.Builder && toolCallCount == 0)
                {
                    messages.Add(new OpenRouterMessage(
                        "user",
                        "Your previous Builder turn produced no deliverable or only a completion marker. Produce the final Builder deliverable now. Do not call tools. Do not output only 'BUILDER OUTPUT COMPLETE'."));
                }
                else if (role == CouncilRole.Critic)
                {
                    messages.Add(new OpenRouterMessage(
                        "user",
                        "Your previous Critic turn produced no visible structured review. Produce the final Critic review now. Do not call tools. Do not output thinking, hidden reasoning, analysis notes, or deliberation. Output only either 'No issues found.' or a numbered actionable findings list with Location/Reference, Severity, Problem, and Fix."));
                }
                var forcedTextBuilder = new StringBuilder();
                OpenRouterChatResponse forcedFinal = await _openRouterChatService.SendConversationStreamAsync(
                    messages,
                    adaptedSystemPrompt,
                    false,
                    OpenRouterChatService.WorkplaceCouncilDefaultModelId,
                    null,
                    onToken: t =>
                    {
                        forcedTextBuilder.Append(t);
                        string current = forcedTextBuilder.ToString();
                        int generatedTokens = EstimateTokenCount(current);
                        _pipelineTokenCount = Math.Max(_pipelineTokenCount, generatedTokens);
                        RecordCouncilRoleGeneratedTokens(role, generatedTokens);
                        string preview = BuildLiveStreamPreview(current);
                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            streamingCard.Content = preview;
                            ChatScrollViewer?.ScrollToEnd();
                        }, DispatcherPriority.Background);
                    },
                    token,
                    maxTokens,
                    stopSequences);

                if (!string.IsNullOrWhiteSpace(forcedFinal.Reasoning))
                    reasoningParts.Add(forcedFinal.Reasoning.Trim());

                finalText = forcedFinal.Text ?? string.Empty;
            }

            string content = NormalizeCloudCouncilRoleOutput(role, StripAgenticMarkers(finalText)).Trim();
            string reasoning = string.Join("\n\n", reasoningParts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal));
            bool reasoningFallback = false;
            if (string.IsNullOrWhiteSpace(content) && !string.IsNullOrWhiteSpace(reasoning))
            {
                // Model emitted only chain-of-thought (no final content). Builder has a downstream
                // canvas-specific suppressor; Critic output is used as repair instructions, so never
                // promote hidden reasoning into visible/actionable review text.
                content = role == CouncilRole.Critic ? string.Empty : reasoning;
                reasoningFallback = true;
            }

            // Ensure the card reflects the cleaned final answer (markers stripped, tool turns settled).
            string cardContent = string.IsNullOrWhiteSpace(content) && role == CouncilRole.Critic && reasoningFallback
                ? "Critic produced internal reasoning but no structured review; retrying validation..."
                : content;
            await Dispatcher.InvokeAsync(() =>
            {
                streamingCard.Content = cardContent;
                ChatScrollViewer?.ScrollToEnd();
            }, DispatcherPriority.Background);
            UpdateAgenticPauseStatus(string.Empty);

            return new ReasoningParser.ParsedResponse
            {
                Answer = content,
                ThinkingContent = reasoningFallback ? reasoning : string.Empty,
                HasThinking = reasoningFallback,
                IsReasoningFallback = reasoningFallback
            };
        }

        private static bool IsBuilderGroundingToolCall(OpenRouterToolCall toolCall)
        {
            string name = toolCall?.Name?.Trim() ?? string.Empty;
            return name.Equals("web_search", StringComparison.OrdinalIgnoreCase)
                || name.Equals("calculate", StringComparison.OrdinalIgnoreCase)
                || name.Equals("run_python", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateCloudCouncilToolStatus(OpenRouterToolCall toolCall, int currentToolCount)
        {
            string name = toolCall?.Name?.Trim() ?? "tool";
            string detail = ExtractCloudToolStatusDetail(toolCall);
            string label = name.ToLowerInvariant() switch
            {
                "web_search" => "Searching the web" + detail,
                "run_python" => "Running Python sandbox",
                "calculate" => "Calculating" + detail,
                "search_session_memory" => "Searching session memory" + detail,
                _ => "Running " + name + detail
            };

            UpdateAgenticPauseStatus($"Agentic Pause {Math.Min(currentToolCount, CloudCouncilToolLoopIterationLimit)}/{CloudCouncilToolLoopIterationLimit} - {label}");
        }

        private static string ExtractCloudToolStatusDetail(OpenRouterToolCall? toolCall)
        {
            if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.ArgumentsJson))
                return string.Empty;

            try
            {
                using JsonDocument doc = JsonDocument.Parse(toolCall.ArgumentsJson);
                JsonElement root = doc.RootElement;
                string value = string.Empty;
                if (root.TryGetProperty("query", out JsonElement query))
                    value = query.GetString() ?? string.Empty;
                else if (root.TryGetProperty("expression", out JsonElement expression))
                    value = expression.GetString() ?? string.Empty;

                value = value.Trim();
                if (value.Length == 0)
                    return string.Empty;
                if (value.Length > 48)
                    value = value[..48] + "...";
                return ": " + value;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static IReadOnlyList<string> BuildCloudCouncilRoleStopSequences(CouncilRole role)
        {
            string marker = GetRoleCompletionMarker(role);
            var stops = new List<string>
            {
                HandoffEndToken,
                "<|endoftext|>",
                "<|eot_id|>",
                "<end_of_turn>"
            };

            if (!string.IsNullOrWhiteSpace(marker))
            {
                stops.Add(marker);
                stops.Add("\n" + marker);
            }

            return stops;
        }

        private static string NormalizeCloudCouncilRoleOutput(CouncilRole role, string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string cleaned = StripSpecialTokenText(text).Trim();
            string marker = GetRoleCompletionMarker(role);
            if (!string.IsNullOrWhiteSpace(marker) && TryExtractWithMarker(cleaned, marker, out string markerCleaned))
                cleaned = markerCleaned;

            return TrimRepeatedRestartTail(cleaned).Trim();
        }

        // Cloud-only tool surface for council roles. web_search is advertised only when the user's
        // web toggle is on, preserving the existing toggle as the single web gate. run_python and
        // calculate are always available (offline-safe, sandboxed).
        private IReadOnlyList<OpenRouterToolDefinition> BuildCouncilCloudToolDefinitions()
        {
            var defs = new List<OpenRouterToolDefinition>();

            if (_isWebSearchEnabled)
            {
                defs.Add(new OpenRouterToolDefinition(
                    "web_search",
                    "Search the web for relevant evidence, definitions, explanations, comparisons, documentation, or current information, then return grounded snippets. Use conversation and council payload context to make the query standalone before searching.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "A concise standalone search query focused on the user's actual question, preserving the requested relation and including any prior-turn or council-payload entity needed to disambiguate it."
                            }
                        },
                        ["required"] = new JsonArray("query"),
                        ["additionalProperties"] = false
                    }));
            }

            defs.Add(new OpenRouterToolDefinition(
                "run_python",
                "Execute Python 3 code in the offline sandbox and return its printed output.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["code"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The Python code to execute. Use print() for the final answer."
                        }
                    },
                    ["required"] = new JsonArray("code"),
                    ["additionalProperties"] = false
                }));

            defs.Add(new OpenRouterToolDefinition(
                "calculate",
                "Evaluate a math or unit-conversion expression with the built-in calculator.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["expression"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "The expression or conversion to evaluate."
                        }
                    },
                    ["required"] = new JsonArray("expression"),
                    ["additionalProperties"] = false
                }));

            // Mirrors the local SEARCH_HIPPOCAMPUS pause tool so cloud roles can recall facts,
            // prior plans, and outputs stored earlier in the session on demand (offline-safe).
            defs.Add(new OpenRouterToolDefinition(
                "search_session_memory",
                "Search this workplace session's stored memory (prior plans, builder outputs, study notes, recorded facts) and return the most relevant entries.",
                new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["query"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["description"] = "What to look up in session memory."
                        }
                    },
                    ["required"] = new JsonArray("query"),
                    ["additionalProperties"] = false
                }));

            return defs;
        }

        // Routes a cloud tool call to the council's existing tool implementations.
        private IReadOnlyList<ConversationSearchTurn> BuildCouncilCloudSearchContextTurns(IReadOnlyList<OpenRouterMessage>? messages, string currentQuery)
        {
            var turns = new List<ConversationSearchTurn>(BuildCouncilSearchContextTurns(currentQuery));

            if (messages != null)
            {
                turns.AddRange(messages
                    .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                             || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                    .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                    .TakeLast(12)
                    .Select(m => new ConversationSearchTurn(m.Role, m.Text)));
            }

            return turns;
        }

        private async Task<string> ExecuteCouncilCloudToolAsync(OpenRouterToolCall toolCall, IReadOnlyList<OpenRouterMessage> conversationMessages, CancellationToken token)
        {
            if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                return "Tool call was empty.";

            string name = toolCall.Name.Trim();
            try
            {
                using JsonDocument argsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
                JsonElement root = argsDocument.RootElement;

                if (string.Equals(name, "web_search", StringComparison.OrdinalIgnoreCase))
                {
                    // Enforce the user's web toggle even if the model emits a web_search call
                    // that was never advertised — the toggle is the single web gate.
                    if (!_isWebSearchEnabled)
                        return "Web search is disabled by the user. Answer from your existing knowledge and the provided context, and state this limitation if it affects the answer.";
                    string query = root.TryGetProperty("query", out JsonElement q) ? q.GetString() ?? string.Empty : string.Empty;
                    string contextualQuery = ConversationSearchContext.BuildContextualSearchPrompt(
                        query,
                        BuildCouncilCloudSearchContextTurns(conversationMessages, query));
                    if (string.IsNullOrWhiteSpace(contextualQuery))
                        contextualQuery = query;

                    await BackendLogService.LogEventAsync("Workplace.CloudWebSearch", $"ToolQuery:{query}\nContextualQuery:{contextualQuery}");
                    string data = await ExecuteWebSearchAsync(contextualQuery, token);
                    if (HasWebSearchEvidence(data))
                    {
                        _latestCouncilReactiveWebContext = MergeCouncilWebContexts(_latestCouncilReactiveWebContext, data);
                        if (_activeCouncilRunContext != null)
                            _activeCouncilRunContext.WebContext = MergeCouncilWebContexts(_activeCouncilRunContext.WebContext, data);
                    }
                    return string.IsNullOrWhiteSpace(data) ? "No web results were found." : data;
                }

                if (string.Equals(name, "search_session_memory", StringComparison.OrdinalIgnoreCase))
                {
                    string memoryQuery = root.TryGetProperty("query", out JsonElement mq) ? mq.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(memoryQuery))
                        return "No memory query was provided.";
                    var memoryEntries = _sessionHippocampus.Query(memoryQuery, 4);
                    if (memoryEntries.Count == 0)
                        return "No relevant entries were found in session memory.";
                    var memoryText = new StringBuilder();
                    foreach (var entry in memoryEntries)
                        memoryText.AppendLine(entry.Content.Trim());
                    return memoryText.ToString().Trim();
                }

                if (string.Equals(name, "run_python", StringComparison.OrdinalIgnoreCase))
                {
                    string code = root.TryGetProperty("code", out JsonElement c) ? c.GetString() ?? string.Empty : string.Empty;
                    if (string.IsNullOrWhiteSpace(code))
                        return "No Python code was provided.";
                    string output = await ExecutePythonMathAsync(code, token);
                    return string.IsNullOrWhiteSpace(output) ? "Python completed with no printed output. Use print() to emit the answer." : output;
                }

                if (string.Equals(name, "calculate", StringComparison.OrdinalIgnoreCase))
                {
                    string expression = root.TryGetProperty("expression", out JsonElement e) ? e.GetString() ?? string.Empty : string.Empty;
                    return CalculatorToolAgent.TryEvaluateExpression(expression, out string resultText)
                        ? resultText
                        : "Calculator could not evaluate the requested expression.";
                }

                return $"Unsupported tool: {name}";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await BackendLogService.LogEventAsync("Workplace.CloudToolError", $"Tool:{name}\nError:{ex.Message}");
                return $"Tool execution failed: {ex.Message}";
            }
        }

        // Bounds a tool result so a large payload can't blow out the council context window.
        private static string BuildCouncilCloudToolResultMessage(string toolResult, string? toolName)
        {
            string normalized = (toolResult ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return "Tool returned no output. Summarize that no evidence or result was produced.";

            bool isWebSearch = string.Equals(toolName?.Trim(), "web_search", StringComparison.OrdinalIgnoreCase);
            if (isWebSearch && HasWebSearchEvidence(normalized))
            {
                normalized =
                    "[WEB TOOL RESULT RULE]\n" +
                    "Use this web_search result as source evidence only for claims it directly supports. Verify that the evidence covers the user's named entities and the requested relation from the council task. Cite source titles/hosts or URLs for current/source-backed claims. If results are partial or off-topic and another tool call is still available, call one narrower standalone web_search query; otherwise answer the supported portion and state the exact evidence gap. Do not add unsupported current facts from memory, and do not treat off-topic results as support.\n\n" +
                    normalized;
            }

            if (normalized.Length <= CloudCouncilToolResultCharacterLimit)
                return normalized;

            string displayName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
            string truncated = normalized[..CloudCouncilToolResultCharacterLimit].TrimEnd();
            return truncated + $"\n\n[TOOL RESULT TRUNCATED] The {displayName} output exceeded {CloudCouncilToolResultCharacterLimit} characters. Use the retained portion above as the primary evidence instead of re-requesting the same large output.";
        }

        // Defensive: remove any stray local-protocol markers a cloud model might emit out of habit
        // (or that linger in replayed sessions), since nothing intercepts them in cloud mode.
        private static string StripAgenticMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text ?? string.Empty;

            string cleaned = Regex.Replace(text, @"\[(?:PAUSE|RESULT)\s*:[^\]]*\]", string.Empty, RegexOptions.IgnoreCase);
            // Collapse blank lines left behind by removed markers.
            cleaned = Regex.Replace(cleaned, @"\n{3,}", "\n\n");
            return cleaned;
        }

        private enum CouncilTaskType
        {
            General,
            Coding,
            Research,
            Analysis,
            Document
        }

        private enum TaskComplexity
        {
            Simple,
            Moderate,
            Complex
        }

        private enum PromptFormat
        {
            ChatML,
            Llama3,
            Alpaca,
            Gemma4
        }

        private sealed class CouncilModelConfig
        {
            public string? ModelPath { get; set; }
            public string DisplayName { get; set; } = "No model selected";
            public PromptFormat Format { get; set; } = PromptFormat.ChatML;
        }

        private sealed class StageMetadata
        {
            public string StageName { get; set; } = "";
            public bool RequiredReformatRetry { get; set; }
            public bool TruncationDetected { get; set; }
            public int SchemaValidationPasses { get; set; } = 1;
        }

        private sealed class CouncilRunContext
        {
            public string UserPrompt { get; init; } = "";
            public string Objective { get; init; } = "";
            public string CalculatorContext { get; set; } = "";
            public bool CalculatorUsed { get; set; }
            public CouncilTaskType TaskType { get; init; }
            public bool IsArtifactCanvasRequest { get; set; }
            public bool IsProjectCanvasIteration { get; set; }
            public ArtifactKind ExistingCanvasArtifactKind { get; set; }
            public string PreferredArtifactFormatHint { get; set; } = "";
            // Live-measured usable pixel area of the canvas artifact viewport at request time,
            // quoted to the models so sizing guidance matches the real pane instead of a stale
            // constant. Defaults match the expanded pane at its 560px design width.
            public int CanvasViewportWidth { get; set; } = 526;
            public int CanvasViewportHeight { get; set; } = 600;
            // The current canvas artifact when the user is iterating ("improve/remake this"); fed to
            // the Builder so it edits the existing artifact instead of regenerating blind. Empty on
            // first generation or non-canvas turns.
            public string CurrentArtifactForIteration { get; set; } = "";
            public bool CurrentArtifactForIterationWasTruncated { get; set; }
            public bool CanvasMutationFailed { get; set; }
            public bool BuilderRoutedToCanvas { get; set; }
            public string PreviousArtifactRequest { get; set; } = "";
            public string PreviousArtifactFormatHint { get; set; } = "";
            public string ArchitectOutput { get; set; } = "";
            public string BuilderOutput { get; set; } = "";
            public string CriticReview { get; set; } = "";
            public string WebContext { get; set; } = "";
            public bool WebGroundingRequired { get; set; }
            public string ArchitectThinking { get; set; } = "";
            public string BuilderThinking { get; set; } = "";
            public string CriticThinking { get; set; } = "";
            public bool RevisionTriggered { get; set; }
            public bool BuilderOutputTruncated { get; set; }
            public bool BuilderProducedCode { get; set; }
            public int ArchitectStepCount { get; set; }
            public PreFlightDecomposition? Decomposition { get; set; }
            public CouncilGoalContract? GoalContract { get; set; }
            public List<string> StaticValidationFindings { get; set; } = new();
            public bool IsCalculationTask { get; set; }
            public bool IsDocumentTask { get; set; }
            public string DocumentContent { get; set; } = "";
            public List<string> DocumentFileNames { get; set; } = new();
            public bool IsWorkspaceTask { get; set; }
            public string WorkspaceContext { get; set; } = "";
            public List<string> WorkspaceFilesRead { get; set; } = new();
            public bool WorkspaceAutoApply { get; set; }
            public bool ArchitectDriftCorrected { get; set; }
            public bool BuilderDriftCorrected { get; set; }
            public bool BuilderTruncationRecovery { get; set; }
            public bool FinalVerificationFailed { get; set; }
            public bool StaticValidationIssuesFound { get; set; }
            public bool SandboxExceptionsFound { get; set; }
            public TaskComplexity Complexity { get; set; } = TaskComplexity.Moderate;
            public Dictionary<string, string> SharedVocabulary { get; set; } = new(StringComparer.OrdinalIgnoreCase);
            public List<FormulaChecklistItem> FormulaChecklist { get; set; } = new();
            public List<StageMetadata> PipelineMetadata { get; set; } = new();
            public List<string> CompletedBuilderSteps { get; set; } = new();
            public bool PythonSandboxEligible { get; set; }
            public int PythonSandboxScore { get; set; }
            public string PythonSandboxPreamble { get; set; } = "";
            public string PythonSystemPromptInjection { get; set; } = "";
            // True when this run executes on the cloud council (~131k-token windows). Lets payload
            // builders use larger evidence budgets without changing local sizing.
            public bool IsCloudExecution { get; init; }
        }

        private sealed record CodebasePatchValidationResult(bool IsValid, IReadOnlyList<string> Reasons)
        {
            public static CodebasePatchValidationResult Pass { get; } = new(true, Array.Empty<string>());
        }

        private sealed record CodebaseUndoFileSnapshot(
            string RelativePath,
            string TargetPath,
            bool Existed,
            string PreviousContent);

        private sealed record CodebaseUndoSnapshot(
            IReadOnlyList<CodebaseUndoFileSnapshot> Files,
            IReadOnlyList<string> ChangedFiles,
            bool WasAutomatic,
            DateTime AppliedAt);

        private sealed class PreFlightDecomposition
        {
            public string ProblemStatement { get; set; } = "";
            public List<string> Requirements { get; set; } = new();
            public List<string> Constraints { get; set; } = new();
            public string DocumentContent { get; set; } = "";
        }

        private sealed class FormulaChecklistItem
        {
            public string StepReference { get; set; } = "";
            public string Formula { get; set; } = "";
            public string UnitConversions { get; set; } = "";
            public string ExpectedRange { get; set; } = "";
        }

        private sealed class SandboxVariableSeed
        {
            public string Name { get; init; } = string.Empty;
            public string DisplayValue { get; init; } = string.Empty;
            public string PythonAssignment { get; init; } = string.Empty;
        }

        private sealed class SandboxExecutionDisplay
        {
            public string CriticContextPayload { get; init; } = string.Empty;
            public string ChatDisplayPayload { get; init; } = string.Empty;
        }

        private sealed class PromptInjectionBlockInfo
        {
            public string Label { get; init; } = string.Empty;
            public int TurnAge { get; init; }
            public bool IsCurrentTurn { get; init; }
            public bool IsCurrentPreflight { get; init; }
        }

        private sealed class SessionMemoryState
        {
            public string ArchitectPlan { get; set; } = "";
            public string BuilderOutput { get; set; } = "";
            public string CriticSummary { get; set; } = "";
            public string TaskDescription { get; set; } = "";
            public CouncilTaskType TaskType { get; set; }
        }

        private sealed class ContextStateObject
        {
            public string TaskContract { get; set; } = "";
            public string UserPrompt { get; set; } = "";
            public string Objective { get; set; } = "";
            public string CalculatorContext { get; set; } = "";
            public string WebContext { get; set; } = "";
            public string WorkspaceContext { get; set; } = "";
            public string ArchitectOutput { get; set; } = "";
            public string BuilderOutput { get; set; } = "";
            public string CriticOutput { get; set; } = "";
            public string SandboxLogs { get; set; } = "";
        }

        private sealed class CouncilBaseStateVault
        {
            public string SharedPayload { get; set; } = "";
            public Dictionary<CouncilRole, string> RoleStatePaths { get; } = new();
        }

        private async Task<string> BuildPromptWebContextAsync(string userQuery, string objective, CancellationToken token)
        {
            if (!_isWebSearchEnabled)
                return string.Empty;

            // Build a focused strategic query from the combined user prompt + objective
            string combined = string.IsNullOrWhiteSpace(objective)
                ? userQuery
                : $"{userQuery} {objective}";
            string contextualPrompt = ConversationSearchContext.BuildContextualSearchPrompt(
                combined,
                BuildCouncilSearchContextTurns(combined));
            if (string.IsNullOrWhiteSpace(contextualPrompt))
                contextualPrompt = combined;

            string strategicQuery = _webSearchService.BuildStrategicSearchQuery(contextualPrompt);
            if (string.IsNullOrWhiteSpace(strategicQuery))
                strategicQuery = contextualPrompt;

            bool requiresGrounding = _webSearchService.RequiresFreshOrSourceBackedGrounding(contextualPrompt);
            string data = await ExecuteWebSearchAsync(strategicQuery, token);
            if (string.IsNullOrWhiteSpace(data)
                || data.Contains("no usable results", StringComparison.OrdinalIgnoreCase)
                || data.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
                || data.Contains("disabled by user", StringComparison.OrdinalIgnoreCase))
            {
                return requiresGrounding
                    ? BuildWebSearchStatusBlock(strategicQuery, string.IsNullOrWhiteSpace(data) ? "No usable web evidence returned." : data)
                    : string.Empty;
            }

            // ExecuteWebSearchAsync already applies PreparePromptContext when data > 4200.
            // Avoid calling it again here — double-truncation would discard the structural
            // markers and corrupt the priority-ordered evidence digest.

            return "[WEB GROUNDING RULE] Web search is enabled and the evidence below is the authoritative source material for current, online, source-backed, or recently changed claims that it actually covers. Current UTC date: " + DateTime.UtcNow.ToString("yyyy-MM-dd") + ". Prefer High confidence sources over Medium confidence ones, and ignore Low confidence sources unless the user explicitly asks for them. Do not add current/source-backed claims from model training data, prior memory, or assumptions when they are not present in the sources. Stable non-current background context may come from the prompt, council plan, project knowledge, or general knowledge when it is not contradicted by the web evidence. Do not treat off-topic web results as evidence for the user's named entities; if the evidence is partial or mismatched, answer the supported portions first and briefly state what remains unconfirmed. For broad current-information requests, summarize the strongest supported developments from the provided sources instead of declaring the web data unusable. If sources conflict, report the conflict explicitly instead of guessing. Cite source titles or hosts naturally for source-backed/current claims, and do not make unsupported current claims that cannot be tied to a provided source.\n" + data;
        }

        private string BuildConnectedWorkspaceContext(string userQuery, string objective, CouncilRunContext runContext)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
                return string.Empty;

            string combined = string.IsNullOrWhiteSpace(objective)
                ? userQuery
                : userQuery + "\n" + objective;
            bool promptNamesFile = Regex.IsMatch(combined, @"(?<![\w.-])[\w./\\-]+\.(?:cs|xaml|csproj|slnx|py|js|ts|tsx|jsx|json|jsonc|css|scss|html|htm|md|txt|xml|yaml|yml|toml)(?![\w.-])", RegexOptions.IgnoreCase);
            int localWorkspaceBudget = promptNamesFile
                ? Math.Clamp((((int)GetRoleContextSize(CouncilRole.Builder)) - 2500) * 4, 18000, 36000)
                : 14000;
            int maxChars = runContext.IsCloudExecution ? 60000 : localWorkspaceBudget;
            WorkspaceContextResult context = _workspaceAccessService.BuildContextPacket(_connectedWorkspace, combined, maxChars);
            runContext.IsWorkspaceTask = true;
            runContext.WorkspaceAutoApply = _connectedWorkspace.AutoApplyCodebaseChanges;
            runContext.WorkspaceContext = context.Packet;
            runContext.WorkspaceFilesRead = context.FilesRead.ToList();

            if (context.FilesRead.Count > 0)
                LogActivity($"Connected workspace context attached: {context.FilesRead.Count} file(s).");
            else
                LogActivity("Connected workspace access is enabled, but no readable local files were attached.");

            return context.Packet;
        }

        private static string BuildCodebasePatchOutputContract()
        {
            return "Connected workspace changes must be proposed as one structured full-file patch review. " +
                "Do not output standalone code fences, prose-only instructions, diffs, or claims that files were changed. " +
                "Output exactly one [[AXIOM_CODEBASE_PATCH]] envelope. Each file block must include FILE, ACTION, one complete fenced replacement/create file, and [[END FILE]]. " +
                "Use relative paths only. Use ACTION: replace for existing files and ACTION: create for new files. Do not use delete actions. " +
                "Use the exact connected workspace path for the target file; do not rename file extensions. " +
                "Always wrap file content in a fenced code block immediately after ACTION; do not put raw HTML/CSS/JSON directly after ACTION. " +
                "For .html/.htm replacements, return the complete file through all closing script/style/body/html tags; never return a partial document. " +
                "Example:\n" +
                "[[AXIOM_CODEBASE_PATCH]]\n" +
                "FILE: path/from/workspace.ext\n" +
                "ACTION: replace\n" +
                "```language\n" +
                "complete replacement file content\n" +
                "```\n" +
                "[[END FILE]]\n" +
                "[[END AXIOM_CODEBASE_PATCH]]";
        }

        private IReadOnlyList<ConversationSearchTurn> BuildCouncilSearchContextTurns(string currentQuery)
        {
            var turns = new List<ConversationSearchTurn>();

            if (!string.IsNullOrWhiteSpace(_activeCouncilWebPrompt)
                && !string.Equals(_activeCouncilWebPrompt.Trim(), currentQuery?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                turns.Add(new ConversationSearchTurn("user", _activeCouncilWebPrompt));
            }

            if (_activeCouncilRunContext != null)
            {
                if (!string.IsNullOrWhiteSpace(_activeCouncilRunContext.UserPrompt))
                    turns.Add(new ConversationSearchTurn("user", _activeCouncilRunContext.UserPrompt));
                if (!string.IsNullOrWhiteSpace(_activeCouncilRunContext.Objective))
                    turns.Add(new ConversationSearchTurn("user", _activeCouncilRunContext.Objective));
            }

            turns.AddRange(_chatHistory
                .Where(h => h.Role is "user" or "architect" or "builder")
                .Where(h => !string.IsNullOrWhiteSpace(h.Content))
                .TakeLast(10)
                .Select(h => new ConversationSearchTurn(h.Role, h.Content)));

            return turns;
        }

        private static bool HasWebSearchEvidence(string webContext)
        {
            return !string.IsNullOrWhiteSpace(webContext)
                && webContext.Contains("[[WEB SEARCH DATA]]", StringComparison.OrdinalIgnoreCase)
                && webContext.Contains("[[END WEB SEARCH DATA]]", StringComparison.OrdinalIgnoreCase);
        }

        private bool HasCouncilWebEvidenceForRun(CouncilRunContext runContext)
        {
            return HasWebSearchEvidence(runContext?.WebContext ?? string.Empty)
                || HasWebSearchEvidence(_latestCouncilReactiveWebContext);
        }

        private static string MergeCouncilWebContexts(string existingContext, string newContext)
        {
            string existing = (existingContext ?? string.Empty).Trim();
            string incoming = (newContext ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(incoming))
                return existing;
            if (string.IsNullOrWhiteSpace(existing))
                return incoming;
            if (existing.Contains(incoming, StringComparison.OrdinalIgnoreCase))
                return existing;
            return existing + "\n\n" + incoming;
        }

        private static string BuildWebSearchStatusBlock(string query, string status)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Search attempted: " + (query ?? string.Empty).Trim());
            sb.AppendLine("Status: " + (status ?? string.Empty).Trim());
            sb.AppendLine("No authoritative [[WEB SEARCH DATA]] is available for current or source-backed factual claims.");
            return BuildLabeledBlock("WEB SEARCH STATUS", sb.ToString());
        }

        private string BuildCouncilWebSystemNote(string webContext, bool webGroundingRequired)
        {
            string webLookupInstruction = _isCloudModeEnabled
                ? "use the web_search tool when it is available"
                : "use WEB_SEARCH through Agentic Pause";

            if (HasWebSearchEvidence(webContext))
            {
                return "\n[REAL-TIME DATA RULE] [WEB SEARCH DATA] is provided in payload. It overrides background knowledge for current, online, source-backed, or recently changed claims that it actually covers. Prefer High confidence sources over Medium confidence ones, ignore Low confidence sources unless explicitly requested, and do not invent current/source-backed facts beyond the provided evidence. Verify that the evidence is about the user's named entities before using it; off-topic results are not support. Stable non-current background context may come from the prompt, council plan, project knowledge, or general knowledge when it is not contradicted by the web evidence. If the provided web evidence does not cover a required current/source-backed fact, " + webLookupInstruction + " before finalizing. If the evidence is partial, conflicting, or mismatched, answer the confirmed portions and explicitly describe the gap or conflict. For broad current-information requests, summarize the strongest supported developments from the available web evidence.\n";
            }

            if (_isWebSearchEnabled && webGroundingRequired)
            {
                return "\n[WEB EVIDENCE REQUIRED] This request needs current, online, official, or source-backed facts, but no authoritative [[WEB SEARCH DATA]] is present yet. Before answering those current/source-backed claims, " + webLookupInstruction + ". If the lookup still returns no usable evidence, do not answer those current/source-backed claims from memory; state that the web lookup did not return usable evidence and answer only stable background or non-current portions supported by the prompt or general knowledge.\n";
            }

            return _isWebSearchEnabled
                ? "\n[WEB SEARCH AVAILABLE] The Web Search button is enabled. If the task requires current, online, recently changed, obscure, technical, legal, financial, medical, documentation, pricing, version, release, policy, or source-backed information not already present in the payload, " + webLookupInstruction + " before answering those claims. Do not guess or use stale background knowledge for such details, but stable non-current background context is allowed when it is not contradicted by sources.\n"
                : "";
        }

        private string BuildBuilderWebPauseSystemNote()
        {
            return _isWebSearchEnabled && !_isCloudModeEnabled
                ? "\n[BUILDER AGENTIC PAUSE RULE] Builder deliverables may require current online or source-backed facts. Before producing the final Builder output, check whether every current/source-backed detail needed by the Architect plan is already supported by [[WEB SEARCH DATA]] or the payload. If not, output exactly one internal pause command on its own line: [PAUSE: WEB_SEARCH | concise query focused on the missing fact]. After the result returns, continue with the deliverable grounded in that result. Stable non-current background context may come from the prompt, council plan, project knowledge, or general knowledge when not contradicted by source evidence. This internal pause command is allowed even though the final visible Builder answer must not contain tool syntax.\n"
                : "";
        }

        private static bool BuilderStatesWebEvidenceUnavailable(string output)
        {
            string lower = (output ?? string.Empty).ToLowerInvariant();
            return lower.Contains("web lookup did not return usable evidence", StringComparison.Ordinal)
                || lower.Contains("web search did not return usable evidence", StringComparison.Ordinal)
                || lower.Contains("could not verify", StringComparison.Ordinal)
                || lower.Contains("not confirmed by the web evidence", StringComparison.Ordinal)
                || lower.Contains("no usable web evidence", StringComparison.Ordinal);
        }

        private static string BuildWebEvidenceUnavailableBuilderFallback(CouncilRunContext context)
        {
            var sb = new StringBuilder();
            sb.AppendLine("I could not produce a source-grounded current answer because the web lookup did not return usable evidence for this request.");
            sb.AppendLine();
            sb.AppendLine("What is safe to say: the prompt requires current or source-backed facts, and no authoritative web evidence was available to the Builder in this run.");
            sb.AppendLine("What is not safe to add: current facts, release details, prices, policies, legal/medical/financial claims, URLs, documentation details, or dates from model memory.");
            if (!string.IsNullOrWhiteSpace(context.UserPrompt))
                sb.AppendLine("Requested topic: " + context.UserPrompt.Trim());
            return sb.ToString().Trim();
        }

        private static void AppendCouncilWebContext(StringBuilder payload, CouncilRunContext runContext)
        {
            if (payload == null || string.IsNullOrWhiteSpace(runContext?.WebContext))
                return;

            payload.AppendLine(runContext.WebContext.Trim());
            if (HasWebSearchEvidence(runContext.WebContext))
            {
                payload.AppendLine(BuildLabeledBlock("WEB ANSWERING CONTRACT",
                    "Use [[WEB SEARCH DATA]] as the authority for current, online, source-backed, or recently changed claims that it actually covers. " +
                    "Use the source titles/hosts, URLs, and dates attached to the evidence for those claims. Do not use off-topic web results as support for the user's named entities. " +
                    "Stable non-current background context may come from the prompt, council plan, project knowledge, or general knowledge when not contradicted by web evidence. " +
                    "If a required current/source-backed detail is not confirmed, run a narrower lookup when tools are available; otherwise state the specific gap instead of filling it in."));
            }
            else if (runContext.WebGroundingRequired)
            {
                payload.AppendLine(BuildLabeledBlock("WEB ANSWERING CONTRACT",
                    "No usable web evidence is available yet for a source-backed/current request. " +
                    "Do not provide current facts, release details, prices, policies, legal/medical/financial claims, URLs, or documentation details from memory. " +
                    "Use a web lookup once if possible; if it still fails, state the evidence gap instead of guessing. Stable non-current background context remains allowed when clearly separate from unsupported current claims."));
            }
        }

        private sealed class CachedModelEntry : IDisposable
        {
            public required string ModelPath { get; init; }
            public required LLamaWeights Weights { get; init; }
            public int GpuLayerCount { get; init; }
            public long SizeBytes { get; init; }
            public DateTime LastUsed { get; set; } = DateTime.Now;

            public void Dispose()
            {
                try { Weights?.Dispose(); } catch { }
            }
        }

        private static long GetModelFileSizeSafe(string modelPath)
        {
            try { return new FileInfo(modelPath).Length; }
            catch { return 0; }
        }

        /// <summary>
        /// Evicts least-recently-used cached council models when the incoming weights
        /// would not fit alongside them. With three roles configured to different models,
        /// the cache previously kept all of them resident simultaneously — exhausting
        /// VRAM/RAM and aborting the process inside native code on the next allocation.
        /// </summary>
        private void EvictModelCacheForLoad(string incomingPath, long incomingBytes)
        {
            var others = _modelCache
                .Where(kv => !string.Equals(kv.Key, incomingPath, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Value.LastUsed)
                .ToList();

            if (others.Count == 0 || incomingBytes <= 0)
                return;

            var profile = HardwareProfiler.Capture();
            bool gpuMode = InferenceBackendService.CurrentMode == InferenceComputeMode.GpuAccelerated
                && profile.HasNvidiaGpu
                && profile.AvailableVramBytes > 0;

            long freeBytes = gpuMode
                ? profile.AvailableVramBytes
                : (long)(profile.AvailableRamBytes * 0.70);

            // Weights plus headroom for the KV cache and compute buffers.
            long required = (long)(incomingBytes * 1.30);

            foreach (var kv in others)
            {
                if (freeBytes >= required)
                    break;

                kv.Value.Dispose();
                _modelCache.Remove(kv.Key);
                freeBytes += kv.Value.SizeBytes;
                LogActivity($"Evicted cached model '{Path.GetFileName(kv.Key)}' to free memory for '{Path.GetFileName(incomingPath)}'.");
            }
        }

        // Council-local self-healing for native GPU aborts. A llama.cpp CUDA illegal-memory-access
        // during decode (the documented Pascal/8GB instability) kills the whole process with no
        // catchable exception, so we cannot recover in-flight — we recover on the NEXT load. When a
        // model has any recorded GPU strike (left by this role's decode-forensics marker), load this
        // council role on CPU, which is stable everywhere. The council is intentionally more
        // conservative than normal chat's reduced-GPU ladder: it loads several 8B role models on one
        // 8GB card, where the weights — not the KV cache — dominate VRAM, so shrinking the context
        // barely helps and tends to just crash again. Going straight to CPU stops the crash on the
        // very next run instead of after another 1–2 process deaths. Pure decision: it adjusts ONLY
        // this role's plan inputs and never mutates the global compute mode (normal chat stays GPU).
        // The strike clears when the user re-selects GPU Accelerated in Settings (normal-chat path),
        // which lets them retry the council on GPU deliberately.
        private (InferenceComputeMode Mode, uint Context) ResolveCouncilCrashRecovery(string modelPath, uint requestedContext)
        {
            if (InferenceBackendService.CurrentMode != InferenceComputeMode.GpuAccelerated
                || !NativeBackendInit.GpuConfigured
                || string.IsNullOrWhiteSpace(modelPath))
                return (InferenceBackendService.CurrentMode, requestedContext);

            if (NativeCrashLedger.GetGpuStrikes(modelPath) <= 0)
                return (InferenceComputeMode.GpuAccelerated, requestedContext);

            LogActivity($"Council: '{Path.GetFileName(modelPath)}' has a prior GPU crash strike; loading this role on CPU for stability.");
            AppendChat("warning", $"'{Path.GetFileName(modelPath)}' crashed on the GPU before — loading this council role on CPU for stability (no more hard crashes). Re-select GPU Accelerated in Settings to retry it on GPU.");
            return (InferenceComputeMode.CpuOnly, requestedContext);
        }

        private async Task<string> ExecuteBuilderPythonWithSingleRetryAsync(string code, string errorText, CouncilRunContext runContext, CancellationToken token)
        {
            string retrySystem = GetEmbeddedSystemPrompt(CouncilRole.Builder)
                + "\n[PYTHON RETRY MODE] Rewrite the code fixing the specific error. Keep it minimal. Do not change variable names already declared in the environment.";
            if (IsQwen3Model(GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath ?? string.Empty))
                retrySystem = BuildQwen3SystemPrompt(retrySystem, false);

            string availableVariables = string.Join(", ", ExtractDeclaredVariableNames(runContext.PythonSandboxPreamble));

            var retryPayload = new StringBuilder();
            retryPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
            retryPayload.AppendLine(BuildLabeledBlock("FAILED CODE", code));
            retryPayload.AppendLine(BuildLabeledBlock("PYTHON ERROR", errorText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? errorText));
            retryPayload.AppendLine($"Available variables in the environment are: {(string.IsNullOrWhiteSpace(availableVariables) ? "(none)" : availableVariables)}");
            retryPayload.AppendLine("Return only corrected executable Python code.");

            var retryResult = await ExecuteCouncilRoleAsync(CouncilRole.Builder, retrySystem, retryPayload.ToString(), token, 0.2f, showLiveCard: false);
            string corrected = PostProcessBuilderOutput(retryResult.Answer, runContext);
            return corrected;
        }

        public WorkplaceSessionSnapshot CaptureSnapshot()
        {
            return CaptureSessionSnapshotForPersistence();
        }

        private void ResetWorkspaceTransientState(bool clearSessionCollections, bool resetCanvas)
        {
            StopPipelineProgress();
            _lastRolePromptTokenEstimates.Clear();
            _lastRoleGeneratedTokenCounts.Clear();
            _pipelineTokenCount = 0;
            _submittedRunPrompt = string.Empty;
            _lastCancelledRunPrompt = string.Empty;
            _lastRunContext = null;
            _activeCouncilRunContext = null;
            _activeCouncilWebPrompt = string.Empty;
            _latestCouncilReactiveWebContext = string.Empty;
            _lastCriticReport = new CriticReport();
            _lastCriticRawOutput = string.Empty;
            _lastSandboxOutput = string.Empty;
            _lastFinalOutput = string.Empty;
            _lastConfidenceLabel = "Moderate Confidence";
            _activeTaskComplexity = TaskComplexity.Moderate;
            _isRefinementMode = false;
            _activeHistorySelection = null;
            _streamingCouncilCards.Clear();
            _stageStopwatch.Reset();
            _activeStageRole = null;
            _lastArchitectDuration = 0;
            _lastBuilderDuration = 0;
            _lastCriticDuration = 0;

            if (clearSessionCollections)
            {
                _chatCards.Clear();
                _systemNotifications.Clear();
                _chatHistory.Clear();
                _documents.Clear();
                _conceptTags.Clear();
                _taskHistory.Clear();
                _performanceLog.Clear();
                _sessionHippocampus.Clear(true);
                _completedCouncilRunCount = 0;
                _studySessionProcessedDocumentCount = 0;
                _studySessionDomainDefinitionCount = 0;
                _documentContextEngaged = false;
                _documentRetriever.ClearChunks();
                _semanticMemory.Clear();
                _nextPromptPriorityChunks.Clear();
                _nextPromptPriorityConcept = null;
                _sessionMemory = null;
                _connectedWorkspace = new ConnectedWorkspaceState();
                _hasPendingCodebaseChanges = false;
                _pendingCodebasePatch = null;
                _lastCodebaseUndo = null;
            }

            QueryInput.Text = string.Empty;
            MemoryFocusBlock.Text = "Memory Focus: None";
            RelayStatusBlock.Text = "Relay: Idle";
            PipelineProgressBlock.Text = string.Empty;
            RevisionNoticeBlock.Visibility = Visibility.Collapsed;
            RevisionNoticeBlock.Text = "Issues were found and the output was revised.";
            CouncilConfidenceBanner.Visibility = Visibility.Collapsed;
            RefineButton.Visibility = Visibility.Collapsed;
            RefinementModeBanner.Visibility = Visibility.Collapsed;
            WorkplaceErrorNotificationBar.Visibility = Visibility.Collapsed;
            WorkplaceErrorNotificationText.Text = string.Empty;
            NotificationDropdownPanel.Visibility = Visibility.Collapsed;
            _isNotificationPanelOpen = false;
            _unreadNotificationCount = 0;
            NotificationBadge.Visibility = Visibility.Collapsed;
            NotificationBadgeText.Text = "0";
            StudySessionNotificationBar.Visibility = Visibility.Collapsed;
            StudySessionProgressBar.Value = 0;
            StudySessionProgressLabel.Text = "0%";
            StudySessionPhaseText.Text = "Idle";
            StudySessionEntryCountText.Text = "0";

            UpdateTaskTypeBadge(CouncilTaskType.General);
            UpdateStageIndicator(null, false, false, false);
            ClearCanvasDiff();
            UpdatePerformanceAggregate();
            UpdateSessionHippocampusIndicator();

            if (resetCanvas)
            {
                ProjectCanvasEditor.Text = "// Builder output appears here\n";
                SetCanvasHighlighting("markdown");
                _canvasArtifact = ArtifactRenderInfo.None(string.Empty);
                _isCanvasPreviewMode = false;
                RefreshCanvasArtifact(ProjectCanvasEditor.Text, string.Empty);
            }

            UpdateContextPressureLabel(0, (int)_contextSize, false);
            ResetWorkplaceTokenUsageIndicator();
            RefreshCodebaseAccessUi();
            RefreshWorkplaceCloudModeUi();
        }

        private void RestoreWorkspaceCollections(WorkplaceSessionSnapshot snapshot)
        {
            _chatCards.Clear();
            _systemNotifications.Clear();
            foreach (var msg in (snapshot.ChatCards ?? []).TakeLast(120))
            {
                var message = new WorkplaceChatMessage
                {
                    Role = msg.Role,
                    Content = msg.Content,
                    Timestamp = msg.Timestamp == default ? DateTime.Now : msg.Timestamp
                };

                if (NotificationRoles.Contains(message.Role))
                    _systemNotifications.Add(message);
                else
                    _chatCards.Add(message);
            }

            // System notifications are intentionally session-local. Persisting and restoring them
            // makes old validation errors and model status events look current in unrelated workspaces.

            _documents.Clear();
            foreach (var doc in snapshot.Documents ?? [])
            {
                _documents.Add(new DocumentInfo
                {
                    Name = doc.Name,
                    FilePath = doc.FilePath,
                    Type = doc.Type,
                    Info = doc.Info,
                    ChunkCount = doc.ChunkCount
                });
            }

            _taskHistory.Clear();
            foreach (var entry in (snapshot.TaskHistory ?? []).OrderByDescending(t => t.Timestamp))
                _taskHistory.Add(entry);

            _performanceLog.Clear();
            foreach (var entry in (snapshot.PerformanceLog ?? []).OrderByDescending(p => p.Timestamp))
                _performanceLog.Add(entry);
        }

        private void RestoreCanvasDiff(WorkplaceSessionSnapshot snapshot)
        {
            _canvasDiffBaseSource = snapshot.CanvasDiffBaseSource ?? string.Empty;
            _canvasDiffCurrentSource = snapshot.CanvasDiffCurrentSource ?? string.Empty;
            string currentCanvasSource = ProjectCanvasEditor?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_canvasDiffCurrentSource)
                && !CanvasSourcesEquivalent(_canvasDiffCurrentSource, currentCanvasSource))
            {
                ClearCanvasDiff();
                return;
            }

            _canvasDiffAdditionCount = Math.Max(0, snapshot.CanvasDiffAdditionCount);
            _canvasDiffRemovalCount = Math.Max(0, snapshot.CanvasDiffRemovalCount);
            _isDiffViewActive = false;
            DiffViewerLines.Items.Clear();
            DiffViewerScroller.Visibility = Visibility.Collapsed;
            ShowDiffButton.Content = "Diff";

            bool hasChanges = !string.IsNullOrWhiteSpace(_canvasDiffBaseSource)
                && !string.IsNullOrWhiteSpace(_canvasDiffCurrentSource)
                && (_canvasDiffAdditionCount > 0 || _canvasDiffRemovalCount > 0);
            ShowDiffButton.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
            CanvasDiffSummaryBlock.Text = hasChanges
                ? $"Latest revision: +{_canvasDiffAdditionCount} / -{_canvasDiffRemovalCount} lines"
                : string.Empty;
            CanvasDiffSummaryBlock.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        }

        public void RestoreSnapshot(WorkplaceSessionSnapshot snapshot)
        {
            if (snapshot == null)
                return;

            ResetWorkspaceTransientState(clearSessionCollections: true, resetCanvas: true);
            _restoredIsolatedRunState = snapshot.IsRunStateIsolated;
            _isCloudModeEnabled = snapshot.CloudModeEnabled;

            ProjectCanvasEditor.Text = snapshot.ProjectCanvasText ?? "";
            SetCanvasHighlighting(DetectLanguage(ProjectCanvasEditor.Text));
            _lastSandboxOutput = snapshot.LastSandboxOutput ?? string.Empty;
            _lastFinalOutput = snapshot.LastFinalOutput ?? string.Empty;
            _lastConfidenceLabel = string.IsNullOrWhiteSpace(snapshot.LastConfidenceLabel)
                ? "Moderate Confidence"
                : snapshot.LastConfidenceLabel;
            RefreshCanvasArtifact(ProjectCanvasEditor.Text, _lastSandboxOutput);
            RestoreCanvasDiff(snapshot);
            _connectedWorkspace = snapshot.ConnectedWorkspace ?? new ConnectedWorkspaceState();
            _hasPendingCodebaseChanges = false;
            _pendingCodebasePatch = null;
            _lastCodebaseUndo = null;

            _contextSize = snapshot.GlobalContextSize <= 0 ? _contextSize : Math.Clamp(snapshot.GlobalContextSize, MinRoleContext, MaxRoleContext);
            _architectContextSize = snapshot.ArchitectContextSize <= 0 ? _contextSize : Math.Clamp(snapshot.ArchitectContextSize, MinRoleContext, MaxRoleContext);
            _builderContextSize = snapshot.BuilderContextSize <= 0 ? _contextSize : Math.Clamp(snapshot.BuilderContextSize, MinRoleContext, MaxRoleContext);
            _criticContextSize = snapshot.CriticContextSize <= 0 ? _contextSize : Math.Clamp(snapshot.CriticContextSize, MinRoleContext, MaxRoleContext);
            _autoOptimizeRoleContexts = snapshot.AutoOptimizeRoleContexts;
            if (_autoOptimizeRoleContexts)
                ApplyOptimizedRoleContexts();
            SyncContextControls();

            RestoreWorkspaceCollections(snapshot);
            _sessionHippocampus.Restore(snapshot.HippocampusEntries ?? [], snapshot.StudySessionCompleted);
            _studySessionProcessedDocumentCount = Math.Max(0, snapshot.StudySessionProcessedDocumentCount);
            _completedCouncilRunCount = Math.Max(0, snapshot.CompletedCouncilRunCount);

            var councilModels = snapshot.CouncilModels ?? new Dictionary<string, WorkplaceCouncilModelDto>(StringComparer.OrdinalIgnoreCase);
            if (councilModels.TryGetValue("Architect", out var arch))
            {
                _council[CouncilRole.Architect].ModelPath = string.IsNullOrWhiteSpace(arch.ModelPath) ? null : arch.ModelPath;
                    _council[CouncilRole.Architect].DisplayName = string.IsNullOrWhiteSpace(arch.DisplayName)
                        ? (IsQwen3Model(arch.ModelPath) ? ModelInferenceProfiles.DefaultQwen3DisplayName : "No model selected")
                        : arch.DisplayName;
                _council[CouncilRole.Architect].Format = ParsePromptFormatByName(arch.Format);
            }
            if (councilModels.TryGetValue("Builder", out var builder))
            {
                _council[CouncilRole.Builder].ModelPath = string.IsNullOrWhiteSpace(builder.ModelPath) ? null : builder.ModelPath;
                    _council[CouncilRole.Builder].DisplayName = string.IsNullOrWhiteSpace(builder.DisplayName)
                        ? (IsQwen3Model(builder.ModelPath) ? ModelInferenceProfiles.DefaultQwen3DisplayName : "No model selected")
                        : builder.DisplayName;
                _council[CouncilRole.Builder].Format = ParsePromptFormatByName(builder.Format);
            }
            if (councilModels.TryGetValue("Critic", out var critic))
            {
                _council[CouncilRole.Critic].ModelPath = string.IsNullOrWhiteSpace(critic.ModelPath) ? null : critic.ModelPath;
                    _council[CouncilRole.Critic].DisplayName = string.IsNullOrWhiteSpace(critic.DisplayName)
                        ? (IsQwen3Model(critic.ModelPath) ? ModelInferenceProfiles.DefaultQwen3DisplayName : "No model selected")
                        : critic.DisplayName;
                _council[CouncilRole.Critic].Format = ParsePromptFormatByName(critic.Format);
            }

            if (_autoOptimizeRoleContexts)
                ApplyOptimizedRoleContexts();
            SyncContextControls();

            UpdateCouncilBlocks();
            UpdateContextInfo();
            RefreshWorkplaceCloudModeUi();
            RefreshWorkplaceWebToggleUi();
            RefreshCodebaseAccessUi();
            UpdateSessionHippocampusIndicator();
            UpdatePerformanceAggregate();
            UpdateWorkplaceTokenUsageIndicator();
        }

        public void ResetWorkspaceSession()
        {
            ResetWorkspaceTransientState(clearSessionCollections: true, resetCanvas: true);
            _restoredIsolatedRunState = true;
            SessionMemoryStatusBlock.Text = "No prior run stored.";
            _contextSize = 8192;
            _architectContextSize = 6144;
            _builderContextSize = 8192;
            _criticContextSize = 4096;
            _autoOptimizeRoleContexts = true;
            ApplyOptimizedRoleContexts();
            SyncContextControls();
            _isWebSearchEnabled = true;
            _isCloudModeEnabled = false;
            _connectedWorkspace = new ConnectedWorkspaceState();
            _hasPendingCodebaseChanges = false;
            _pendingCodebasePatch = null;
            _lastCodebaseUndo = null;
            LoadOpenRouterKeyForWorkplace();
            RefreshWorkplaceCloudModeUi();
            RefreshWorkplaceWebToggleUi();
            RefreshCodebaseAccessUi();
            UpdateContextInfo();
            UpdateSessionHippocampusIndicator();
            UpdateWorkplaceTokenUsageIndicator();
            SavePersistedSession();
        }

        public IReadOnlyList<SessionHippocampusEntry> QueryHippocampus(string query, int maxResults = 3)
        {
            if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
                return Array.Empty<SessionHippocampusEntry>();
            return _sessionHippocampus.Query(query, maxResults);
        }

        public sealed class ConceptTagViewModel
        {
            public string Name { get; set; } = "";
            public int Weight { get; set; }
            public string DisplayLabel => $"{Name} ({Weight})";
        }

        public class DocumentInfo
        {
            public string Name { get; set; } = "";
            public string FilePath { get; set; } = "";
            public string Type { get; set; } = "";
            public string Info { get; set; } = "";
            public int ChunkCount { get; set; }
        }

        private sealed class StudyChunk
        {
            public string DocumentName { get; set; } = "";
            public int ChunkIndex { get; set; }
            public string Content { get; set; } = "";
            public int TokenEstimate { get; set; }
        }

        private sealed class StudyChunkResult
        {
            public string Summary { get; set; } = "";
            public List<string> Concepts { get; set; } = new();
            public List<(string Question, string Answer)> QuestionAnswers { get; set; } = new();
        }

        private sealed class StudySessionProgressEventArgs : EventArgs
        {
            public string PhaseName { get; init; } = "";
            public int Current { get; init; }
            public int Total { get; init; }
            public int EntriesWritten { get; init; }
            public string Message { get; init; } = "";
        }

        private enum CriticSensitivityLevel
        {
            Standard,
            Strict,
            CriticalOnly
        }

        private readonly DocumentRetriever _documentRetriever = new();
        private readonly SemanticProjectMemory _semanticMemory = new();
        private readonly ObservableCollection<DocumentInfo> _documents = new();
        // Sticky document grounding: once a turn has used the attached document(s), later turns keep
        // grounding in them even when the follow-up doesn't name the file or use a doc keyword
        // ("what about the budget section?", "tell me more about that"). Reset when documents are
        // cleared. Without this, follow-ups silently lost the document and the model answered blind.
        private bool _documentContextEngaged;
        private readonly ObservableCollection<ConceptTagViewModel> _conceptTags = new();
        private readonly ObservableCollection<string> _activityLogs = new();
        private readonly Dictionary<CouncilRole, CouncilModelConfig> _council = new();
        private readonly PersonaMemoryService _personaMemoryService = new();
        private readonly WebSearchService _webSearchService = new();
        private readonly PythonExecutionService _pythonExecutionService = new();
        private readonly WorkspaceAccessService _workspaceAccessService = new();
        private ConnectedWorkspaceState _connectedWorkspace = new();
        private bool _hasPendingCodebaseChanges;
        private WorkspacePatchProposal? _pendingCodebasePatch;
        private CodebaseUndoSnapshot? _lastCodebaseUndo;
        private string _builderPythonSandboxPreamble = "";
        private string _activePythonSandboxPreamble = "";
        private bool _activePythonSessionForTurn;
        private string _submittedRunPrompt = string.Empty;
        private string _lastCancelledRunPrompt = string.Empty;

        private readonly List<DocumentChunk> _nextPromptPriorityChunks = new();
        private string? _nextPromptPriorityConcept;

        private CancellationTokenSource? _cancellationTokenSource;
        private uint _contextSize = 8192;
        private uint _architectContextSize = 8192;
        private uint _builderContextSize = 8192;
        private uint _criticContextSize = 8192;
        private readonly Dictionary<CouncilRole, uint> _effectiveLocalRoleContextSizes = new();
        private readonly Dictionary<CouncilRole, int> _lastRolePromptTokenEstimates = new();
        private readonly Dictionary<CouncilRole, int> _lastRoleGeneratedTokenCounts = new();
        private bool _autoOptimizeRoleContexts = true;
        private static readonly uint MinRoleContext = 2048;
        private static readonly uint MaxRoleContext = 32768;
        private bool _isProcessing;
        private bool _isProjectCanvasExpanded = true;
        private bool _isProjectCanvasAutoCollapsed;
        private bool _isProjectCanvasExplicitlyExpandedInCompactLayout;
        private bool _isCodeOutputExpanded = true;
        private const double ProjectCanvasExpandedWidthRatio = 0.30;
        private const int ContextCompressionThreshold = 3000;
        private const double AvgCharsPerToken = 4.0;
        private readonly List<(string Role, string Content)> _chatHistory = new();
        private SessionMemoryState? _sessionMemory;
        private const string SegmentCompletionMarker = "// @@SEGMENT_COMPLETE@@";
        private const string ArchitectCompletionMarker = "ARCHITECT PLAN COMPLETE";
        private const string BuilderCompletionMarker = "BUILDER OUTPUT COMPLETE";
        private const string CriticCompletionMarker = "CRITIC REVIEW COMPLETE";
        private const string HandoffEndToken = "<|im_end|>";
        private const int MaxBuilderRetryAttempts = 2;
        private const int SandboxEligibilityThreshold = 4;
        // Cloud council native tool-calling guards (mirrors the Normal Chat cloud loop limits).
        // 6 rounds: with four advertised tools (web, python, calculator, session memory) a role can
        // legitimately need a research → compute → verify chain; the forced no-tools synthesis pass
        // still guarantees usable output if the budget is exhausted.
        private const int CloudCouncilToolLoopIterationLimit = 6;
        private const int CloudCouncilToolExecutionLimit = 6;
        private const int CloudCouncilToolResultCharacterLimit = 14000;
        // Above this many visible characters, Builder text accompanying tool calls is treated as the
        // finished deliverable (late tool calls ignored). Below it, the text is a pre-tool lead-in and
        // the tool call is allowed to run so the Builder can ground facts/math before writing.
        private const int BuilderFinalDeliverableMinChars = 400;
        // Bounded retry for transient free-tier 429s so a throttle does not abort the whole relay.
        private const int CloudCouncilRateLimitRetryLimit = 2;
        private const int DefaultCloudCouncilRateLimitWaitSeconds = 6;
        private const int MaxCloudCouncilRateLimitWaitSeconds = 22;
        private static readonly Regex ModelParamBillionsRegex = new(@"(\d+(?:\.\d+)?)\s*[bB]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private const string ProjectCanvasManualTrigger = "@ProjectCanvas";
        private static readonly Regex ProjectCanvasManualTriggerRegex = new(@"(?<![A-Za-z0-9_])@ProjectCanvas(?![A-Za-z0-9_])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ArchitectNumberedStepRegex = new(@"^\s*\d{1,3}[\.|\)]\s+", RegexOptions.Compiled);
        private static readonly string[] SandboxUnitWords =
        [
            "kilometers", "km", "meters", "miles", "kilograms", "kg", "pounds", "grams", "liters", "gallons",
            "seconds", "minutes", "hours", "degrees", "percent", "dollars", "euros", "watts", "volts", "amps",
            "newtons", "joules"
        ];
        private static readonly string[] SandboxQuantityPhrases =
        [
            "how many", "how much", "how far", "how long", "how fast", "what is the total", "what is the average",
            "what is the rate", "what is the ratio", "convert", "calculate", "compute", "solve for"
        ];
        private static readonly string[] SandboxDomainWords =
        [
            "velocity", "acceleration", "force", "mass", "momentum", "energy", "power", "voltage", "resistance",
            "frequency", "probability", "interest", "compound", "principal", "depreciation", "density", "pressure",
            "temperature", "distance", "area", "volume"
        ];
        private static readonly Regex SandboxExpressionRegex = new(@"\d+(?:\.\d+)?\s*[\+\-\*/\^]\s*\d+(?:\.\d+)?", RegexOptions.Compiled);
        private static readonly Regex BuilderCodeFenceRegex = new(@"```(?:[A-Za-z0-9_+#\.-]+)?\s*(?<code>[\s\S]*?)```", RegexOptions.Compiled);
        private static readonly Regex BuilderTypedCodeFenceRegex = new(@"```(?<language>[A-Za-z0-9_+#\.-]+)\s*\r?\n(?<code>[\s\S]*?)```", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SandboxNumberWithUnitRegex = new(@"(?<label>[A-Za-z][A-Za-z0-9_-]*)?\s*(?<number>-?\d+(?:\.\d+)?)\s*(?<unit>kilometers|km|meters|miles|kilograms|kg|pounds|grams|liters|gallons|seconds|minutes|hours|degrees|percent|dollars|euros|watts|volts|amps|newtons|joules)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PythonResultLineRegex = new(@"^(?<label>[^:=]+?)\s*[:=]\s*(?<value>-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?(?:\s*[%A-Za-z/]+)?)$", RegexOptions.Compiled);
        private static readonly Regex DigitLetterMultiplicationRegex = new(@"(?<digit>\d)(?<letter>[A-Za-z])", RegexOptions.Compiled);
        private static readonly Regex CaretExponentRegex = new(@"(?<left>[A-Za-z0-9_\)\.]+)\s*\^\s*(?<right>[A-Za-z0-9_\(\.]+)", RegexOptions.Compiled);
        private static readonly Regex SqrtRegex = new(@"\bsqrt\s*(?=\(|[A-Za-z0-9])", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex PiRegex = new(@"\bpi\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NumericOnlyLineRegex = new(@"^(?<value>-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)$", RegexOptions.Compiled);
        private static readonly Regex NumericWithOptionalLabelRegex = new(@"^(?:(?<label>[^:=]+?)\s*[:=]\s*)?(?<value>-?\d+(?:\.\d+)?(?:[eE][\+\-]?\d+)?)(?<suffix>\s*[%A-Za-z/]+)?$", RegexOptions.Compiled);
        private static readonly HashSet<string> CappedInjectionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "WEB SEARCH DATA",
            "PROJECT KNOWLEDGE BASE",
            "PRIOR KNOWLEDGE",
            "CALCULATOR TOOL RESULTS"
        };
        private static readonly HashSet<string> StalePrunableInjectionLabels = new(StringComparer.OrdinalIgnoreCase)
        {
            "WEB SEARCH DATA",
            "PRIOR KNOWLEDGE",
            "CALCULATOR TOOL RESULTS",
            "PYTHON RESULT"
        };
        private static readonly Regex LabeledInjectionBlockRegex = new(@"\[\[(?<label>[A-Z0-9 _-]+)\]\]\s*(?<body>[\s\S]*?)\s*\[\[END \k<label>\]\]", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly string[] DynamicInputIntentPhrases =
        [
            "user inputs", "user enters", "ask the user", "prompt the user", "takes input"
        ];

        private readonly ObservableCollection<WorkplaceChatMessage> _chatCards = new();
        private readonly Dictionary<string, WorkplaceChatMessage> _streamingCouncilCards = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CachedModelEntry> _modelCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly string CouncilKvStateFolder = Path.Combine(AppDataPaths.ChatHistory, "CouncilKvStates");
        private const string DefaultFirstRunQwen3Guidance = "First run: load Qwen3-4B-Q4_K_M.gguf from Hugging Face (about 2.5 GB) to use the default Axiom Qwen3-4B workplace model profile.";
        private readonly System.Windows.Threading.DispatcherTimer _progressTimer = new();
        private readonly Stopwatch _pipelineStopwatch = new();
        private readonly WorkplaceSessionPersistence _workplacePersistence = new();
        private readonly SessionHippocampus _sessionHippocampus = new();
        private readonly SemaphoreSlim _stateSaveGate = new(1, 1);
        private readonly SemaphoreSlim _notificationGate = new(1, 1);
        private int _stateSaveRequested;
        private int _stateSaveWorkerRunning;
        private int _pipelineTokenCount;
        private CouncilRunContext? _lastRunContext;
        private CriticReport _lastCriticReport = new();
        private string _lastCriticRawOutput = "";
        private string _lastSandboxOutput = "";
        private string _lastFinalOutput = "";
        private bool _isCouncilPetEnabled;
        private ArtifactRenderInfo _canvasArtifact = ArtifactRenderInfo.None(string.Empty);
        private bool _isCanvasPreviewMode;
        private bool _suppressCanvasNativePreviewForOverlay;
        private bool _canvasArtifactWebViewReady;
        private string _canvasArtifactNavSource = string.Empty;
        private bool _canvasArtifactNavRetried;
        private bool _canvasArtifactNavOk;
        private TaskComplexity _activeTaskComplexity = TaskComplexity.Moderate;
        private int _completedCouncilRunCount;

        // Per-stage timing for ETA estimation
        private readonly Stopwatch _stageStopwatch = new();
        private CouncilRole? _activeStageRole;
        private double _lastArchitectDuration;
        private double _lastBuilderDuration;
        private double _lastCriticDuration;
        private bool _isStudySessionRunning;
        private bool _studySessionCancelRequested;
        private CancellationTokenSource? _studySessionCts;
        private int _studySessionProcessedDocumentCount;
        private int _studySessionDomainDefinitionCount;
        private event EventHandler<StudySessionProgressEventArgs>? StudySessionProgress;
        private readonly WorkspaceAdvancedStatePersistence _advancedStatePersistence = new();
        private readonly ObservableCollection<CouncilTaskHistoryEntry> _taskHistory = new();
        private readonly ObservableCollection<ModelPerformanceLogEntry> _performanceLog = new();
        private readonly ObservableCollection<WorkspaceTemplateEntry> _workspaceTemplates = new();
        private CriticSensitivityLevel _criticSensitivity = CriticSensitivityLevel.Standard;
        private bool _criticSensitivityAutoSet;
        private bool _criticSensitivityManuallyChangedThisSession;
        private bool _suppressCriticSensitivitySelectionChanged;
        private bool _isRefinementMode;
        private CouncilTaskHistoryEntry? _activeHistorySelection;
        private string _lastConfidenceLabel = "Moderate Confidence";
        private string _canvasDiffBaseSource = "";
        private string _canvasDiffCurrentSource = "";
        private int _canvasDiffAdditionCount;
        private int _canvasDiffRemovalCount;
        private bool _isDiffViewActive;
        private bool _restoredIsolatedRunState;

        private readonly ObservableCollection<WorkplaceChatMessage> _systemNotifications = new();
        private bool _isNotificationPanelOpen;
        private int _unreadNotificationCount;
        private readonly OpenRouterChatService _openRouterChatService = new();
        private bool _isCloudModeEnabled;

        private static readonly HashSet<string> NotificationRoles = new(StringComparer.OrdinalIgnoreCase)
        {
            "system", "error", "warning", "memory"
        };

        private AgenticPauseEngine? _agenticPauseEngine;
        private bool _isWebSearchEnabled = true;
        private string _activeCouncilWebPrompt = "";
        private string _latestCouncilReactiveWebContext = "";
        private CouncilRunContext? _activeCouncilRunContext;
        private DateTime _lastWebStatusStamp = DateTime.MinValue;
        private const string AttachedDocumentHeaderLine = "ATTACHED DOCUMENT — YOU MUST READ AND USE THIS";
        private const string AttachedDocumentEndLine = "END OF DOCUMENT";
        private const string AttachedDocumentRequiredReferenceInstruction = "The user has attached a document. Your response must directly reference and use the content of the attached document. If the user's request relates to the document in any way, base your answer on the document content. Do not ignore the document.";

        // Shared grounding directive emitted next to the document text in every document-task
        // Builder payload (single-pass, document-retrieval, and long-form continuation paths) so the
        // grounding rule is identical everywhere. It must do TWO jobs at once: keep the answer faithful
        // to the source (no fabricated facts) AND elicit genuine synthesis. Earlier wording ("base your
        // answer ONLY on the text", "you may quote short phrases verbatim") over-anchored small local
        // models, which then transcribed source spans instead of summarizing — the "grab and drop"
        // failure. This phrasing instead tells the model to read, understand, and answer in its OWN
        // words while staying grounded, and explicitly forbids copying long passages.
        private const string DocumentGroundingInstruction =
            "GROUNDING RULE: Read and understand the document text above, then answer in your OWN words as a real, " +
            "coherent summary/analysis. Do NOT copy, paste, or transcribe whole sentences or long passages from the " +
            "document, and do not return disconnected fragments of it — synthesize and explain the content instead. " +
            "Keep every statement faithful to the source: use the real names, facts, figures, dates, and terminology " +
            "from the text, and do NOT introduce any fact, name, date, number, event, or claim that the document does " +
            "not support. If the document lacks something needed to answer, say so plainly instead of inventing it.";

        private static bool IsQwen3Model(string modelFilePath)
        {
            if (string.IsNullOrWhiteSpace(modelFilePath))
                return false;

            string fileName = Path.GetFileName(modelFilePath);
            return fileName.Contains("qwen3", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildQwen3SystemPrompt(string baseSystemPrompt, bool thinkingEnabled)
        {
            string prompt = (baseSystemPrompt ?? string.Empty).TrimEnd();
            if (prompt.EndsWith("/no_think", StringComparison.OrdinalIgnoreCase))
                prompt = prompt[..^"/no_think".Length].TrimEnd();
            else if (prompt.EndsWith("/think", StringComparison.OrdinalIgnoreCase))
                prompt = prompt[..^"/think".Length].TrimEnd();

            string controlToken = thinkingEnabled ? "/think" : "/no_think";
            return string.IsNullOrWhiteSpace(prompt)
                ? controlToken
                : prompt + "\n" + controlToken;
        }

        private static InferenceParams CreateGenericInferenceParams(int maxTokens, IEnumerable<string> antiPrompts, float temperature, float minP)
        {
            return new InferenceParams
            {
                MaxTokens = maxTokens,
                AntiPrompts = antiPrompts?.ToList() ?? new List<string>(),
                SamplingPipeline = new DefaultSamplingPipeline
                {
                    Temperature = temperature,
                    MinP = minP,
                    // Bounded top-k/top-p instead of full-vocabulary sampling (was -1 / 1.0).
                    // Quality-neutral, but much cheaper per token on large-vocab models — a
                    // direct win for every council role on both CPU and GPU.
                    TopP = 0.95f,
                    TopK = 40,
                    RepeatPenalty = 1.1f
                }
            };
        }

        private static InferenceParams CreateRoleInferenceParams(
            string modelPath,
            int maxTokens,
            IEnumerable<string> antiPrompts,
            float temperature,
            float minP,
            Grammar? grammar = null,
            bool useSubOneBProfile = false)
        {
            return IsQwen3Model(modelPath)
                ? ModelInferenceProfiles.CreateQwen3InferenceParams(false, maxTokens, antiPrompts, grammar)
                : new InferenceParams
                {
                    MaxTokens = maxTokens,
                    AntiPrompts = antiPrompts?.ToList() ?? new List<string>(),
                    SamplingPipeline = new DefaultSamplingPipeline
                    {
                        Temperature = useSubOneBProfile ? Math.Min(temperature, 0.15f) : temperature,
                        MinP = useSubOneBProfile ? Math.Min(minP, 0.03f) : minP,
                        TopP = useSubOneBProfile ? 0.9f : 0.95f,
                        TopK = useSubOneBProfile ? 20 : 40,
                        RepeatPenalty = useSubOneBProfile ? 1.18f : 1.1f,
                        Grammar = grammar,
                        GrammarOptimization = grammar == null
                            ? DefaultSamplingPipeline.GrammarOptimizationMode.None
                            : DefaultSamplingPipeline.GrammarOptimizationMode.Extended
                    }
                };
        }

        private void ApplyQwen3DefaultCouncilProfile(CouncilRole role, string modelPath)
        {
            _council[role].DisplayName = IsQwen3Model(modelPath)
                ? ModelInferenceProfiles.DefaultQwen3DisplayName
                : Path.GetFileNameWithoutExtension(modelPath);
            _council[role].Format = IsGemma4Model(modelPath)
                ? PromptFormat.Gemma4
                : PromptFormat.ChatML;
        }

        private bool ShouldEmitWebStatus()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastWebStatusStamp).TotalMilliseconds < 900)
                return false;

            _lastWebStatusStamp = now;
            return true;
        }

        private void LoadOpenRouterKeyForWorkplace()
        {
            try
            {
                using var database = new DatabaseService();
                _openRouterChatService.SetApiKey(database.LoadOpenRouterApiKey() ?? string.Empty);
            }
            catch
            {
                _openRouterChatService.SetApiKey(string.Empty);
            }
        }

        public WorkplaceView()
        {
            InitializeComponent();
            QueryInput.TextArea.TextView.LineTransformers.Add(new ProjectCanvasMentionColorizer());
            QueryInput.TextChanged += (_, _) => UpdateWorkplaceTokenUsageIndicator();
            Directory.CreateDirectory(CouncilKvStateFolder);
            LoadOpenRouterKeyForWorkplace();

            _council[CouncilRole.Architect] = new CouncilModelConfig();
            _council[CouncilRole.Builder] = new CouncilModelConfig();
            _council[CouncilRole.Critic] = new CouncilModelConfig();

            DocumentListBox.ItemsSource = _documents;
            ConceptCloudItemsControl.ItemsSource = _conceptTags;
            ChatCardsList.ItemsSource = _chatCards;
            TaskHistoryListBox.ItemsSource = _taskHistory;
            PerformanceLogListBox.ItemsSource = _performanceLog;
            WorkspaceTemplateListBox.ItemsSource = _workspaceTemplates;
            NotificationList.ItemsSource = _systemNotifications;

            _progressTimer.Interval = TimeSpan.FromSeconds(1);
            _progressTimer.Tick += ProgressTimer_Tick;
            _sessionHippocampus.StoreChanged += SessionHippocampus_StoreChanged;
            StudySessionProgress += StudySessionProgress_Progressed;
            Unloaded += (_, _) =>
            {
                SavePersistedSession();
                _studySessionCts?.Cancel();
                _sessionHippocampus.Clear(true);
                _sessionHippocampus.StoreChanged -= SessionHippocampus_StoreChanged;
                StudySessionProgress -= StudySessionProgress_Progressed;
                DisposeModelCache();
            };

            ProjectCanvasEditor.Text = "// Builder output appears here\n";
            SetCanvasHighlighting("markdown");
            RefreshCanvasArtifact(ProjectCanvasEditor.Text, _lastSandboxOutput);

            UpdateContextInfo();
            UpdateCouncilBlocks();
            UpdateTaskTypeBadge(CouncilTaskType.General);
            UpdateStageIndicator(null, false, false, false);
            UpdateContextPressureLabel(0, (int)_contextSize, false);
            UpdateSessionHippocampusIndicator();
            ApplyOptimizedRoleContexts();
            SyncContextControls();
            ArchitectContextSlider.IsEnabled = !_autoOptimizeRoleContexts;
            BuilderContextSlider.IsEnabled = !_autoOptimizeRoleContexts;
            CriticContextSlider.IsEnabled = !_autoOptimizeRoleContexts;
            LoadPersistedSession();
            LoadAdvancedState();
            UpdateCriticSensitivityBadge();
            UpdatePerformanceAggregate();
            _agenticPauseEngine = new AgenticPauseEngine(
                _sessionHippocampus,
                (code, lang) => ExecuteCodeSandboxAsync(code, lang),
                (query, ct) => ExecuteWebSearchAsync(query, ct),
                (code, ct) => ExecutePythonMathAsync(code, ct),
                msg => LogActivity(msg),
                msg => UpdateAgenticPauseStatus(msg));
            RefreshWorkplaceCloudModeUi();
            RefreshWorkplaceWebToggleUi();
            RefreshCodebaseAccessUi();
            RefreshCouncilPetToggleUi();
            UpdateWorkplaceTokenUsageIndicator();
            Loaded += WorkplaceView_Loaded;
            SizeChanged += (_, _) => ApplyDesktopLayout(ActualWidth);
            ProjectCanvasPane.SizeChanged += (_, _) => UpdateCanvasHeaderLayout();
        }

        private sealed class ProjectCanvasMentionColorizer : DocumentColorizingTransformer
        {
            private static readonly SolidColorBrush MentionBrush = new(Color.FromRgb(92, 190, 255));

            protected override void ColorizeLine(DocumentLine line)
            {
                string text = CurrentContext.Document.GetText(line);
                foreach (Match match in ProjectCanvasManualTriggerRegex.Matches(text))
                {
                    int startOffset = line.Offset + match.Index;
                    int endOffset = startOffset + ProjectCanvasManualTrigger.Length;
                    ChangeLinePart(startOffset, endOffset, element =>
                    {
                        element.TextRunProperties.SetForegroundBrush(MentionBrush);
                    });
                }
            }
        }

        private async void WorkplaceView_Loaded(object sender, RoutedEventArgs e)
        {
            await EnsureCanvasArtifactWebViewInitializedAsync();
            RefreshCanvasArtifactUi();
        }

        private void ProjectCanvasToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProjectCanvasAutoCollapsed)
            {
                _isProjectCanvasAutoCollapsed = false;
                _isProjectCanvasExplicitlyExpandedInCompactLayout = true;
                _isProjectCanvasExpanded = true;
                AnimateProjectCanvasPane(true);
                return;
            }

            _isProjectCanvasExpanded = !_isProjectCanvasExpanded;
            _isProjectCanvasExplicitlyExpandedInCompactLayout = false;
            AnimateProjectCanvasPane(_isProjectCanvasExpanded);
        }

        internal void ApplyDesktopLayout(double availableWidth)
        {
            if (!IsLoaded || availableWidth <= 0)
                return;

            bool compactCanvasLayout = availableWidth < 1120;
            CouncilSidebarColumn.Width = new GridLength(availableWidth < 1200 ? 270 : availableWidth < 1700 ? 290 : 310);

            if (compactCanvasLayout && _isProjectCanvasExpanded &&
                !_isProjectCanvasAutoCollapsed && !_isProjectCanvasExplicitlyExpandedInCompactLayout)
            {
                _isProjectCanvasAutoCollapsed = true;
                SetProjectCanvasVisibilityInstant(false);
            }
            else if (!compactCanvasLayout)
            {
                _isProjectCanvasExplicitlyExpandedInCompactLayout = false;
                if (_isProjectCanvasAutoCollapsed)
                {
                    _isProjectCanvasAutoCollapsed = false;
                    SetProjectCanvasVisibilityInstant(_isProjectCanvasExpanded);
                }
                else if (_isProjectCanvasExpanded && ProjectCanvasPane.Visibility == Visibility.Visible)
                {
                    ProjectCanvasPane.Width = GetResponsiveProjectCanvasWidth();
                }
            }

            double canvasWidth = ProjectCanvasPane.Visibility == Visibility.Visible
                ? ProjectCanvasPane.ActualWidth > 0 ? ProjectCanvasPane.ActualWidth : GetResponsiveProjectCanvasWidth()
                : ProjectCanvasCollapsedHandle.Visibility == Visibility.Visible ? 36 : 0;
            double centerWidth = Math.Max(0, availableWidth - CouncilSidebarColumn.Width.Value - canvasWidth);
            double inputWidth = InputAreaContainer.ActualWidth > 0 ? InputAreaContainer.ActualWidth : centerWidth;
            bool canvasVisible = ProjectCanvasPane.Visibility == Visibility.Visible;
            bool stackRunSummary = inputWidth < 940 || (canvasVisible && inputWidth < 1040);

            Grid.SetRow(WorkplaceTokenUsagePanel, stackRunSummary ? 1 : 0);
            Grid.SetColumn(WorkplaceTokenUsagePanel, stackRunSummary ? 0 : 1);
            Grid.SetColumnSpan(WorkplaceTokenUsagePanel, stackRunSummary ? 2 : 1);
            WorkplaceTokenUsagePanel.Margin = stackRunSummary
                ? new Thickness(0, 8, 0, 0)
                : new Thickness(14, 0, 0, 0);
            WorkplaceTokenUsagePanel.Padding = stackRunSummary
                ? new Thickness(0, 8, 0, 0)
                : new Thickness(14, 1, 0, 1);
            WorkplaceTokenUsagePanel.BorderThickness = stackRunSummary
                ? new Thickness(0, 1, 0, 0)
                : new Thickness(1, 0, 0, 0);

            StageActionsPanel.HorizontalAlignment = stackRunSummary ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
            StageActionsPanel.MaxWidth = stackRunSummary ? double.PositiveInfinity : 460;
            InputAreaContainer.Padding = centerWidth < 650
                ? new Thickness(12, 10, 12, 10)
                : new Thickness(24, 12, 24, 12);
            UpdateCanvasHeaderLayout();
        }

        private void SetProjectCanvasVisibilityInstant(bool visible)
        {
            ProjectCanvasPane.BeginAnimation(WidthProperty, null);
            ProjectCanvasPane.BeginAnimation(OpacityProperty, null);
            ProjectCanvasPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ProjectCanvasPane.Opacity = visible ? 1 : 0;
            ProjectCanvasPane.Width = visible ? GetResponsiveProjectCanvasWidth() : 0;
            ProjectCanvasCollapsedHandle.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
            ProjectCanvasToggleButton.Content = visible ? "›" : "‹";
            ProjectCanvasToggleButton.ToolTip = visible ? "Hide project canvas" : "Show project canvas";
        }

        // ── Agentic Pause UI ──────────────────────────────────────────────────
        /// <summary>
        /// Shows or hides the Agentic Pause status banner with a smooth opacity animation.
        /// Parses the current pause count from messages of the form "⏸  Agentic Pause N/3 …"
        /// to keep the budget pill accurate.
        /// Must be called from any thread; dispatches to UI automatically.
        /// </summary>
        private void UpdateAgenticPauseStatus(string message)
        {
            Dispatcher.InvokeAsync(() =>
            {
                AgenticPauseStatusBlock.Text = message;

                // Parse pause budget (N/3) from the message for the pill display
                var budgetMatch = System.Text.RegularExpressions.Regex.Match(message, @"(\d+)\s*/\s*(\d+)\s+pauses\s+used|Pause\s+(\d+)/(\d+)");
                if (budgetMatch.Success)
                {
                    int used = int.TryParse(budgetMatch.Groups[1].Value, out int u1) ? u1
                             : int.TryParse(budgetMatch.Groups[3].Value, out int u2) ? u2 : 0;
                    int max  = int.TryParse(budgetMatch.Groups[2].Value, out int m1) ? m1
                             : int.TryParse(budgetMatch.Groups[4].Value, out int m2) ? m2 : 3;
                    AgenticPauseBudgetText.Text = $"{used} / {max}";
                    AgenticPauseBudgetText.Foreground = used >= max
                        ? new SolidColorBrush(Color.FromRgb(255, 59, 59))
                        : new SolidColorBrush(Color.FromRgb(138, 130, 121));
                }
                else if (string.IsNullOrWhiteSpace(message))
                {
                    // Reset pill on hide
                    AgenticPauseBudgetText.Text = "0 / 3";
                    AgenticPauseBudgetText.Foreground = new SolidColorBrush(Color.FromRgb(138, 130, 121));
                }

                if (string.IsNullOrWhiteSpace(message))
                {
                    // Fade out and collapse the banner
                    var fadeOut = new DoubleAnimation(AgenticPauseBanner.Opacity, 0.0,
                        TimeSpan.FromMilliseconds(350));
                    fadeOut.Completed += (_, _) => AgenticPauseBanner.Visibility = Visibility.Collapsed;
                    AgenticPauseBanner.BeginAnimation(UIElement.OpacityProperty, fadeOut);
                }
                else
                {
                    // Fade in the banner (gentle pulse to signal active processing)
                    AgenticPauseBanner.Visibility = Visibility.Visible;
                    var fadeIn = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(220));
                    AgenticPauseBanner.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                    // Pulse the dot to draw the eye without distracting flicker
                    var dotPulse = new DoubleAnimation(1.0, 0.35, TimeSpan.FromMilliseconds(650))
                    {
                        AutoReverse = true,
                        RepeatBehavior = RepeatBehavior.Forever
                    };
                    AgenticPulseDot.BeginAnimation(UIElement.OpacityProperty, dotPulse);
                }
            });
        }

        private void CopyCanvasButton_Click(object sender, RoutedEventArgs e)
        {
            string text = ProjectCanvasEditor.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendChat("system", "Project Canvas is empty — nothing to copy.");
                return;
            }

            try
            {
                Clipboard.SetText(text);
                AppendChat("system", "Project Canvas content copied to clipboard.");
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Copy failed: {ex.Message}");
            }
        }
        private void CodeOutputToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isCodeOutputExpanded = !_isCodeOutputExpanded;
            AnimateCodeOutputPanel(_isCodeOutputExpanded);
            RefreshCanvasArtifactUi();
        }

        private void CanvasMoreActionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (CanvasMoreActionsButton.ContextMenu == null)
                return;

            CanvasMoreActionsButton.ContextMenu.PlacementTarget = CanvasMoreActionsButton;
            CanvasMoreActionsButton.ContextMenu.IsOpen = true;
        }

        private void CanvasMoreCopyItem_Click(object sender, RoutedEventArgs e)
        {
            CopyCanvasButton_Click(sender, e);
        }

        private void CanvasMoreDiffItem_Click(object sender, RoutedEventArgs e)
        {
            ShowDiffButton_Click(sender, e);
        }

        private void CanvasMoreOutputItem_Click(object sender, RoutedEventArgs e)
        {
            CodeOutputToggleButton_Click(sender, e);
        }

        private void AnimateProjectCanvasPane(bool expand)
        {
            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            double targetWidth = expand ? GetResponsiveProjectCanvasWidth() : 0;

            if (expand)
            {
                ProjectCanvasCollapsedHandle.Visibility = Visibility.Collapsed;
                ProjectCanvasPane.Visibility = Visibility.Visible;
                ProjectCanvasToggleButton.Content = "›";
                ProjectCanvasToggleButton.ToolTip = "Hide project canvas";
            }

            var widthAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(200),
                To = targetWidth,
                EasingFunction = ease
            };

            var fadeAnim = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(160),
                To = expand ? 1 : 0,
                EasingFunction = ease
            };

            fadeAnim.Completed += (_, _) =>
            {
                if (!expand)
                {
                    ProjectCanvasPane.Visibility = Visibility.Collapsed;
                    ProjectCanvasCollapsedHandle.Visibility = Visibility.Visible;
                    ProjectCanvasToggleButton.Content = "‹";
                    ProjectCanvasToggleButton.ToolTip = "Show project canvas";
                }
            };

            ProjectCanvasPane.BeginAnimation(WidthProperty, widthAnim);
            ProjectCanvasPane.BeginAnimation(OpacityProperty, fadeAnim);
        }

        private double GetResponsiveProjectCanvasWidth()
        {
            double viewportWidth = ActualWidth > 0 ? ActualWidth : 1600;
            double candidate = viewportWidth * ProjectCanvasExpandedWidthRatio;
            return Math.Max(ProjectCanvasPane.MinWidth, Math.Min(candidate, ProjectCanvasPane.MaxWidth));
        }

        private void ChatScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
                return;

            double nextOffset = scrollViewer.VerticalOffset - (e.Delta / 3.0);
            nextOffset = Math.Max(0, Math.Min(nextOffset, scrollViewer.ScrollableHeight));
            scrollViewer.ScrollToVerticalOffset(nextOffset);
            e.Handled = true;
        }

        private void SidebarScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not ScrollViewer scrollViewer)
                return;

            double nextOffset = scrollViewer.VerticalOffset - (e.Delta / 3.0);
            nextOffset = Math.Max(0, Math.Min(nextOffset, scrollViewer.ScrollableHeight));
            scrollViewer.ScrollToVerticalOffset(nextOffset);
            e.Handled = true;
        }

        private void AnimateCodeOutputPanel(bool expand)
        {
            if (!_isProjectCanvasExpanded)
            {
                return;
            }

            var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };

            if (expand)
            {
                CodeOutputPanel.Visibility = Visibility.Visible;
                CodeOutputToggleButton.Content = "⌄";
                CodeOutputToggleButton.ToolTip = "Hide code output";
            }
            else
            {
                CodeOutputToggleButton.Content = "⌃";
                CodeOutputToggleButton.ToolTip = "Show code output";
            }

            var opacityAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(170),
                To = expand ? 1 : 0,
                EasingFunction = ease
            };

            opacityAnimation.Completed += (_, _) =>
            {
                if (!expand)
                {
                    CodeOutputPanel.Visibility = Visibility.Collapsed;
                }
            };

            CodeOutputPanel.BeginAnimation(OpacityProperty, opacityAnimation);
        }

        // Set by the host (MainWindow). On a single GPU the Normal-Chat model and the council
        // role models compete for the same VRAM/RAM, so a LOCAL council run asks the host to
        // release the chat model before loading its own role models — otherwise loading a second
        // large model on top of the resident chat model fails with a "Failed to load model"
        // relay error. The host restores the chat model when the user returns to the chat view.
        public Func<CancellationToken, Task>? ReleaseHostChatModelAsync { get; set; }

        public void InitializeWithSession(InteractiveExecutor executor, uint contextSize)
        {
            _contextSize = Math.Clamp(Math.Max(contextSize, MinRoleContext), MinRoleContext, MaxRoleContext);
            _architectContextSize = _contextSize;
            _builderContextSize = _contextSize;
            _criticContextSize = _contextSize;
            if (AutoOptimizeContextToggle != null && AutoOptimizeContextToggle.IsChecked == true)
            {
                ApplyOptimizedRoleContexts();
            }
            SyncContextControls();
            UpdateContextInfo();
        }

        private void SyncContextControls()
        {
            if (GlobalContextSlider == null)
                return;

            GlobalContextSlider.Value = _contextSize;
            ArchitectContextSlider.Value = _architectContextSize;
            BuilderContextSlider.Value = _builderContextSize;
            CriticContextSlider.Value = _criticContextSize;
            AutoOptimizeContextToggle.IsChecked = _autoOptimizeRoleContexts;

            GlobalContextValueText.Text = $"{_contextSize} tokens";
            ArchitectContextValueText.Text = $"{_architectContextSize} tokens";
            BuilderContextValueText.Text = $"{_builderContextSize} tokens";
            CriticContextValueText.Text = $"{_criticContextSize} tokens";
            UpdateWorkplaceTokenUsageIndicator();
        }

        private void ApplyOptimizedRoleContexts()
        {
            _effectiveLocalRoleContextSizes.Clear();
            _architectContextSize = GetOptimizedContextForModel(CouncilRole.Architect, _contextSize);
            _builderContextSize = GetOptimizedContextForModel(CouncilRole.Builder, _contextSize);
            _criticContextSize = GetOptimizedContextForModel(CouncilRole.Critic, _contextSize);
        }

        private uint GetOptimizedContextForModel(CouncilRole role, uint fallback)
        {
            _council.TryGetValue(role, out var cfg);
            string? modelPath = cfg?.ModelPath;
            string modelName = modelPath ?? cfg?.DisplayName ?? "";

            // Largest context worth REQUESTING for this model file. Bigger files leave less room for
            // the KV cache, so larger models aspire to less. These are deliberately far more generous
            // than the old fixed 6144/8192/4096 values, which capped the window well below what modern
            // models and typical hardware support and were a primary source of mid-conversation context
            // loss (truncated documents/history → hallucination). The REAL ceiling is still enforced
            // downstream by HardwareProfiler's memory math (weights + KV priced against actual free
            // VRAM/RAM), and on flash-attention GPUs the q8_0 KV cache lets far more of this window
            // actually fit — so requesting big here is safe: it can only be granted when it fits.
            uint fileSizeCeiling = MaxRoleContext;
            if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
            {
                long fileBytes = new FileInfo(modelPath).Length;
                fileSizeCeiling = fileBytes switch
                {
                    > 16L * 1024 * 1024 * 1024 => 6144u,   // >16 GB (70B-class)
                    > 8L * 1024 * 1024 * 1024  => 12288u,  // >8 GB  (13-34B-class)
                    > 4L * 1024 * 1024 * 1024  => 24576u,  // >4 GB  (7-9B-class)
                    _                           => MaxRoleContext  // ≤4 GB (1-4B-class): let the trained window govern
                };
            }

            // Aspire to the model's OWN trained context window — never beyond it (running past the
            // trained length needs RoPE/YaRN scaling and degrades quality), and never beyond the
            // file-size ceiling above.
            uint trainedCtx = ResolveTrainedContextLength(modelPath, modelName);
            uint aspiration = Math.Min(trainedCtx, fileSizeCeiling);

            // Without flash attention (CPU, or a pre-Turing GPU) the KV cache stays f16, so a very
            // large window steals GPU layers / RAM and slows generation. Stay near the proven
            // GPU-friendly size there; only FA-capable setups push the window high.
            bool faCapable = !string.IsNullOrWhiteSpace(modelPath) && HardwareProfiler.SupportsFlashAttention(modelPath);
            if (!faCapable)
                aspiration = Math.Min(aspiration, 12288u);

            // Role weighting as a FRACTION of the aspiration so it scales with the model: the Builder
            // needs the most (documents, code, prior output); the Critic the least (bounded review);
            // the Architect sits between. Roles load sequentially, so this trades per-role speed, not
            // shared VRAM.
            double roleFraction = role switch
            {
                CouncilRole.Builder => 1.00,
                CouncilRole.Architect => 0.75,
                CouncilRole.Critic => 0.50,
                _ => 0.75
            };
            uint optimized = (uint)Math.Round(aspiration * roleFraction);

            return Math.Clamp(optimized, MinRoleContext, MaxRoleContext);
        }

        // Reads the model's trained context length from its GGUF header (the authoritative value),
        // falling back to a conservative modern default when the header cannot be read. Bounded by
        // MaxRoleContext. Lets the per-role window size to the model itself rather than a fixed guess.
        private static uint ResolveTrainedContextLength(string? modelPath, string modelName)
        {
            if (!string.IsNullOrWhiteSpace(modelPath) && File.Exists(modelPath))
            {
                try
                {
                    GgufModelMetadata? meta = GgufMetadataReader.TryRead(modelPath);
                    if (meta != null && meta.ContextLength >= 2048)
                        return (uint)Math.Min(meta.ContextLength, (int)MaxRoleContext);
                }
                catch
                {
                    // Header unreadable — fall through to the size-based default.
                }
            }

            // No header: most current local instruct models train at >=32k, but stay a little
            // conservative for very small models where the header was the only strong signal.
            if (TryGetModelParamBillions(modelName, out double sizeB) && sizeB <= 2.0)
                return 16384u;
            return Math.Min(32768u, MaxRoleContext);
        }

        private static bool TryGetModelParamBillions(string modelPath, out double billions)
        {
            billions = 0;
            if (string.IsNullOrWhiteSpace(modelPath))
                return false;

            var match = ModelParamBillionsRegex.Match(modelPath);
            if (!match.Success)
                return false;

            return double.TryParse(match.Groups[1].Value, out billions);
        }

        private void GlobalContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded)
                return;

            _contextSize = (uint)Math.Clamp((int)e.NewValue, (int)MinRoleContext, (int)MaxRoleContext);
            _effectiveLocalRoleContextSizes.Clear();

            if (_autoOptimizeRoleContexts)
            {
                ApplyOptimizedRoleContexts();
            }
            else
            {
                _architectContextSize = _contextSize;
                _builderContextSize = _contextSize;
                _criticContextSize = _contextSize;
            }

            SyncContextControls();
            UpdateContextInfo();
        }

        private void RoleContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || _autoOptimizeRoleContexts)
                return;

            _architectContextSize = (uint)Math.Clamp((int)ArchitectContextSlider.Value, (int)MinRoleContext, (int)MaxRoleContext);
            _builderContextSize = (uint)Math.Clamp((int)BuilderContextSlider.Value, (int)MinRoleContext, (int)MaxRoleContext);
            _criticContextSize = (uint)Math.Clamp((int)CriticContextSlider.Value, (int)MinRoleContext, (int)MaxRoleContext);
            _effectiveLocalRoleContextSizes.Clear();
            _contextSize = (uint)Math.Clamp((int)new[] { _architectContextSize, _builderContextSize, _criticContextSize }.Max(), (int)MinRoleContext, (int)MaxRoleContext);

            SyncContextControls();
            UpdateContextInfo();
        }

        private void AutoOptimizeContextToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
                return;

            _autoOptimizeRoleContexts = AutoOptimizeContextToggle.IsChecked == true;
            if (_autoOptimizeRoleContexts)
            {
                ApplyOptimizedRoleContexts();
            }

            ArchitectContextSlider.IsEnabled = !_autoOptimizeRoleContexts;
            BuilderContextSlider.IsEnabled = !_autoOptimizeRoleContexts;
            CriticContextSlider.IsEnabled = !_autoOptimizeRoleContexts;

            SyncContextControls();
            UpdateContextInfo();
        }

        private void UpdateContextInfo()
        {
            if (_isCloudModeEnabled)
            {
                int cloudWindow = _openRouterChatService.GetApproximateContextWindowTokens(OpenRouterChatService.WorkplaceCouncilDefaultModelId);
                int builderCtx = cloudWindow;
                int otherCtx = cloudWindow / 2;
                int maxChunks = _documentRetriever.CalculateMaxChunksForContext(builderCtx);
                ContextInfoBlock.Text = $"Cloud context · A:{otherCtx / 1024}K  B:{builderCtx / 1024}K  C:{otherCtx / 1024}K | Max chunks: {maxChunks}";
            }
            else
            {
                uint maxRoleContext = Math.Max(_architectContextSize, Math.Max(_builderContextSize, _criticContextSize));
                int maxChunks = _documentRetriever.CalculateMaxChunksForContext((int)maxRoleContext);
                string mode = _autoOptimizeRoleContexts ? "auto" : "manual";
                ContextInfoBlock.Text = $"Context A/B/C: {_architectContextSize}/{_builderContextSize}/{_criticContextSize}t ({mode}) | Max chunks: {maxChunks}";
            }
            HardwareInfoBlock.Text = GetCouncilStatusDescription();
            UpdateWorkplaceTokenUsageIndicator();
        }

        private void UpdateContextPressurePreview(string userQuery, string objective, int chunkCount)
        {
            int estimatedPromptTokens = EstimateTokenCount(userQuery)
                + EstimateTokenCount(objective)
                + Math.Max(0, chunkCount) * 300
                + 220;

            UpdateContextPressureLabel(estimatedPromptTokens, (int)_contextSize, true);
        }

        private void UpdateContextPressureLabel(int usedTokens, int capacityTokens, bool includeRatio)
        {
            if (capacityTokens <= 0)
            {
                ContextPressureBlock.Text = "Context pressure: --";
                return;
            }

            int percent = (int)Math.Round(usedTokens * 100d / capacityTokens);
            string level = percent switch
            {
                >= 90 => "High",
                >= 70 => "Moderate",
                _ => "Low"
            };

            ContextPressureBlock.Text = includeRatio
                ? $"Context pressure: {level} ({usedTokens}/{capacityTokens}t)"
                : $"Context pressure: {level}";
        }

        private int EstimateWorkplaceContextTokens()
        {
            int historyTokens = _chatHistory
                .Where(h => h.Role is "user" or "architect" or "builder" or "critic" or "builder-patch" or "builder-revision")
                .Sum(h => EstimateTokenCount(h.Content));

            int currentInputTokens = EstimateTokenCount(QueryInput?.Text ?? string.Empty);
            int currentCanvasTokens = 0;
            string currentCanvas = ProjectCanvasEditor?.Text ?? string.Empty;
            if (_lastRunContext?.IsArtifactCanvasRequest == true
                && !string.IsNullOrWhiteSpace(currentCanvas)
                && !currentCanvas.TrimStart().StartsWith("// Builder output appears here", StringComparison.OrdinalIgnoreCase))
            {
                currentCanvasTokens = EstimateTokenCount(currentCanvas);
            }

            return Math.Max(0, historyTokens + currentInputTokens + currentCanvasTokens);
        }

        private int GetWorkplaceRoleContextCapacity(CouncilRole role)
        {
            if (_isCloudModeEnabled)
            {
                int cloudWindow = _openRouterChatService.GetApproximateContextWindowTokens(OpenRouterChatService.WorkplaceCouncilDefaultModelId);
                return role == CouncilRole.Builder ? cloudWindow : cloudWindow / 2;
            }

            if (_effectiveLocalRoleContextSizes.TryGetValue(role, out uint effectiveContext))
                return Math.Max(512, (int)effectiveContext);

            return Math.Max(512, (int)GetRoleContextSize(role));
        }

        private static string FormatCompactTokenCount(int tokens)
        {
            if (tokens >= 1024)
            {
                double kibTokens = tokens / 1024d;
                return kibTokens >= 100 ? $"{kibTokens:F0}K" : $"{kibTokens:F1}K";
            }

            return tokens.ToString("N0");
        }

        private static void UpdateWorkplaceRoleTokenMeter(
            CouncilRole role,
            int used,
            int capacity,
            TextBlock label,
            ProgressBar bar)
        {
            double percent = capacity <= 0 ? 0 : Math.Clamp(used * 100d / capacity, 0, 100);
            label.Text = $"{FormatCompactTokenCount(used)} / {FormatCompactTokenCount(capacity)}";
            bar.Value = percent;

            Color meterColor = percent switch
            {
                >= 90 => Color.FromRgb(201, 106, 91),
                >= 75 => Color.FromRgb(184, 146, 74),
                _ => Color.FromRgb(95, 175, 125)
            };
            var brush = new SolidColorBrush(meterColor);
            bar.Foreground = brush;
            label.Foreground = percent >= 75
                ? brush
                : new SolidColorBrush(Color.FromRgb(138, 130, 121));

            string details = $"{role}: approximately {used:N0} of {capacity:N0} tokens ({percent:F0}%).";
            label.ToolTip = details;
            bar.ToolTip = details;
        }

        private void RecordCouncilRolePromptUsage(CouncilRole role, string systemPrompt, string userPayload)
        {
            int estimatedTokens = Math.Max(0, EstimateTokenCount(systemPrompt) + EstimateTokenCount(userPayload));

            void ApplyUsage()
            {
                _lastRolePromptTokenEstimates[role] = estimatedTokens;
                UpdateWorkplaceTokenUsageIndicator();
            }

            if (Dispatcher.CheckAccess())
                ApplyUsage();
            else
                _ = Dispatcher.InvokeAsync(ApplyUsage, DispatcherPriority.Background);
        }

        private void RecordCouncilRoleGeneratedTokens(CouncilRole role, int generatedTokens)
        {
            int safeGeneratedTokens = Math.Max(0, generatedTokens);

            void ApplyUsage()
            {
                _lastRoleGeneratedTokenCounts[role] = safeGeneratedTokens;
                UpdateWorkplaceTokenUsageIndicator();
            }

            if (Dispatcher.CheckAccess())
                ApplyUsage();
            else
                _ = Dispatcher.InvokeAsync(ApplyUsage, DispatcherPriority.Background);
        }

        private void ResetWorkplaceTokenUsageIndicator()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(ResetWorkplaceTokenUsageIndicator, DispatcherPriority.Background);
                return;
            }

            if (FindName("WorkplaceTokenUsagePanel") is not Border panel
                || FindName("ArchitectTokenUsageProgressBar") is not ProgressBar architectBar
                || FindName("BuilderTokenUsageProgressBar") is not ProgressBar builderBar
                || FindName("CriticTokenUsageProgressBar") is not ProgressBar criticBar
                || FindName("ArchitectTokenUsageLabel") is not TextBlock architectLabel
                || FindName("BuilderTokenUsageLabel") is not TextBlock builderLabel
                || FindName("CriticTokenUsageLabel") is not TextBlock criticLabel)
            {
                return;
            }

            panel.Visibility = Visibility.Collapsed;
            UpdateWorkplaceRoleTokenMeter(CouncilRole.Architect, 0, GetWorkplaceRoleContextCapacity(CouncilRole.Architect), architectLabel, architectBar);
            UpdateWorkplaceRoleTokenMeter(CouncilRole.Builder, 0, GetWorkplaceRoleContextCapacity(CouncilRole.Builder), builderLabel, builderBar);
            UpdateWorkplaceRoleTokenMeter(CouncilRole.Critic, 0, GetWorkplaceRoleContextCapacity(CouncilRole.Critic), criticLabel, criticBar);
        }

        private void UpdateWorkplaceTokenUsageIndicator()
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(UpdateWorkplaceTokenUsageIndicator, DispatcherPriority.Background);
                return;
            }

            if (FindName("WorkplaceTokenUsagePanel") is not Border panel
                || FindName("ArchitectTokenUsageProgressBar") is not ProgressBar architectBar
                || FindName("BuilderTokenUsageProgressBar") is not ProgressBar builderBar
                || FindName("CriticTokenUsageProgressBar") is not ProgressBar criticBar
                || FindName("ArchitectTokenUsageLabel") is not TextBlock architectLabel
                || FindName("BuilderTokenUsageLabel") is not TextBlock builderLabel
                || FindName("CriticTokenUsageLabel") is not TextBlock criticLabel)
            {
                return;
            }

            int conversationEstimate = EstimateWorkplaceContextTokens();
            int pendingInputTokens = EstimateTokenCount(QueryInput?.Text ?? string.Empty);
            int architectGenerated = _lastRoleGeneratedTokenCounts.TryGetValue(CouncilRole.Architect, out int architectGeneratedTokens) ? architectGeneratedTokens : 0;
            int builderGenerated = _lastRoleGeneratedTokenCounts.TryGetValue(CouncilRole.Builder, out int builderGeneratedTokens) ? builderGeneratedTokens : 0;
            int criticGenerated = _lastRoleGeneratedTokenCounts.TryGetValue(CouncilRole.Critic, out int criticGeneratedTokens) ? criticGeneratedTokens : 0;
            int architectUsed = _lastRolePromptTokenEstimates.TryGetValue(CouncilRole.Architect, out int architectPromptTokens)
                ? architectPromptTokens + pendingInputTokens + architectGenerated
                : conversationEstimate + architectGenerated;
            int builderUsed = _lastRolePromptTokenEstimates.TryGetValue(CouncilRole.Builder, out int builderPromptTokens)
                ? builderPromptTokens + pendingInputTokens + builderGenerated
                : conversationEstimate + builderGenerated;
            int criticUsed = _lastRolePromptTokenEstimates.TryGetValue(CouncilRole.Critic, out int criticPromptTokens)
                ? criticPromptTokens + pendingInputTokens + criticGenerated
                : conversationEstimate + criticGenerated;
            panel.Visibility = Visibility.Visible;
            panel.ToolTip = _isCloudModeEnabled
                ? "Approximate retained workplace context against the configured cloud model windows."
                : "Approximate retained workplace context against each local role's effective model and hardware-aware window.";

            UpdateWorkplaceRoleTokenMeter(CouncilRole.Architect, architectUsed, GetWorkplaceRoleContextCapacity(CouncilRole.Architect), architectLabel, architectBar);
            UpdateWorkplaceRoleTokenMeter(CouncilRole.Builder, builderUsed, GetWorkplaceRoleContextCapacity(CouncilRole.Builder), builderLabel, builderBar);
            UpdateWorkplaceRoleTokenMeter(CouncilRole.Critic, criticUsed, GetWorkplaceRoleContextCapacity(CouncilRole.Critic), criticLabel, criticBar);
        }

        private void UpdateCouncilBlocks()
        {
            ArchitectModelBlock.Text = GetCouncilDisplayName(CouncilRole.Architect);
            BuilderModelBlock.Text = GetCouncilDisplayName(CouncilRole.Builder);
            CriticModelBlock.Text = GetCouncilDisplayName(CouncilRole.Critic);
        }

        private void SessionHippocampus_StoreChanged(object? sender, EventArgs e)
        {
            Dispatcher.Invoke(UpdateSessionHippocampusIndicator);
        }

        private void UpdateSessionHippocampusIndicator()
        {
            var metadata = _sessionHippocampus.GetMetadata();
            int studyEntries = metadata.SourceCounts.TryGetValue(SessionHippocampusSource.StudySession, out int s) ? s : 0;
            int councilEntries = (metadata.SourceCounts.TryGetValue(SessionHippocampusSource.ArchitectOutput, out int a) ? a : 0)
                + (metadata.SourceCounts.TryGetValue(SessionHippocampusSource.BuilderOutput, out int b) ? b : 0)
                + (metadata.SourceCounts.TryGetValue(SessionHippocampusSource.CriticOutput, out int c) ? c : 0);

            if (studyEntries == 0 && councilEntries == 0)
            {
                HippocampusLineOneBlock.Text = "No prior knowledge loaded.";
                HippocampusLineTwoBlock.Text = "";
                return;
            }

            var lines = new List<string>();
            if (studyEntries > 0)
            {
                int docs = Math.Max(1, _studySessionProcessedDocumentCount);
                lines.Add($"Studied material available · {docs} document(s) · {studyEntries} memory item(s)");
            }

            if (councilEntries > 0)
            {
                lines.Add($"Session memory active · {_completedCouncilRunCount} completed run(s) · {councilEntries} pattern(s)");
            }

            HippocampusLineOneBlock.Text = lines.Count > 0 ? lines[0] : "No prior knowledge loaded.";
            HippocampusLineTwoBlock.Text = lines.Count > 1 ? lines[1] : "";
        }

        private async Task HideStudySessionNotificationBarAsync(int delayMs = 2200)
        {
            if (delayMs > 0)
            {
                await Task.Delay(delayMs);
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_isStudySessionRunning)
                {
                    return;
                }

                StudySessionNotificationBar.Visibility = Visibility.Collapsed;
            }, DispatcherPriority.Background);
        }

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                DropZone.Background = new SolidColorBrush(Color.FromRgb(42, 40, 38));
            }

            e.Handled = true;
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            DropZone.Background = new SolidColorBrush(Color.FromRgb(23, 22, 21));
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                var files = (string[])e.Data.GetData(DataFormats.FileDrop);
                ProcessFilesAsync(files);
            }
            e.Handled = true;
        }

        private void BrowseFiles_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "All Supported (*.pdf;*.docx;*.xlsx;*.rtf;*.txt;*.md;code;data)|*.pdf;*.docx;*.xlsx;*.rtf;*.txt;*.md;*.markdown;*.cs;*.py;*.js;*.ts;*.jsx;*.tsx;*.json;*.jsonc;*.xml;*.yaml;*.yml;*.toml;*.html;*.htm;*.css;*.csv;*.tsv;*.log;*.ini;*.sql;*.java;*.cpp;*.c;*.h;*.go;*.rs;*.rb;*.php;*.ps1;*.bat;*.sh;*.tex;*.srt;*.vtt|PDF Files (*.pdf)|*.pdf|Office Documents (*.docx;*.xlsx;*.rtf)|*.docx;*.xlsx;*.rtf|Text Files (*.txt;*.md)|*.txt;*.md|Code Files (*.cs;*.py;*.js;*.ts;*.css;*.html)|*.cs;*.py;*.js;*.ts;*.css;*.html|Data Files (*.json;*.xml;*.yaml;*.yml;*.csv;*.tsv)|*.json;*.xml;*.yaml;*.yml;*.csv;*.tsv|All files (*.*)|*.*",
                Multiselect = true
            };

            if (dialog.ShowDialog() == true)
            {
                ProcessFilesAsync(dialog.FileNames);
            }
        }

        private async void ProcessFilesAsync(string[] filePaths)
        {
            SendButton.IsEnabled = false;
            AppendChat("system", "Processing files...");
            LogActivity("Parsing project assets in background...");

            try
            {
                foreach (var filePath in filePaths)
                {
                    await ProcessSingleFileAsync(filePath);
                }

                await RefreshConceptCloudAsync();
                AppendChat("system", $"✓ Loaded {_documents.Count} document(s)");
                LogActivity($"Indexed {_documents.Count} documents.");
                UpdateContextInfo();
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                AppendChat("error", $"File processing failed: {ex.Message}");
                if (ex is OutOfMemoryException || ex is IOException)
                {
                    _ = ShowNonIntrusiveErrorAsync($"File processing error: {ex.Message}");
                }
            }
            finally
            {
                SendButton.IsEnabled = true;
            }
        }

        private async Task ProcessSingleFileAsync(string filePath)
        {
            if (!File.Exists(filePath))
            {
                AppendChat("error", $"File not found: {filePath}");
                return;
            }

            string fileName = Path.GetFileName(filePath);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            string? extractedText;

            AppendChat("system", $"Reading: {fileName}...");

            if (extension == ".pdf")
            {
                try
                {
                    extractedText = await PdfExtractor.ExtractTextFromPdfAsync(filePath);
                }
                catch (Exception ex)
                {
                    AppendChat("warning", $"PDF extraction failed: {ex.Message}");
                    if (ex is OutOfMemoryException || ex is IOException)
                    {
                        _ = ShowNonIntrusiveErrorAsync($"Document read error: {ex.Message}");
                    }
                    return;
                }
            }
            else if (IsPlainTextExtension(extension))
            {
                extractedText = await Task.Run(() =>
                {
                    try
                    {
                        return File.ReadAllText(filePath, Encoding.UTF8);
                    }
                    catch
                    {
                        return File.ReadAllText(filePath, Encoding.Default);
                    }
                });
            }
            else
            {
                // docx/xlsx/rtf/tsv and unknown-but-textual files go through the shared importer.
                try
                {
                    ChatAttachmentImportResult imported = await ChatAttachmentImportService.ImportAsync(filePath);
                    if (imported.Attachment.IsImage)
                    {
                        AppendChat("warning", $"Images are not supported as workplace knowledge documents: {fileName}");
                        return;
                    }

                    extractedText = imported.Attachment.Content;
                }
                catch (Exception ex)
                {
                    AppendChat("warning", $"Unsupported file type {extension}: {ex.Message}");
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(extractedText))
            {
                AppendChat("warning", $"{fileName} has no extractable text");
                return;
            }

            var chunks = await Task.Run(() => DocumentChunker.ChunkDocument(fileName, extractedText));
            if (chunks.Count == 0)
            {
                AppendChat("warning", $"No chunks created for {fileName}");
                return;
            }

            _documentRetriever.AddChunks(chunks);
            await _semanticMemory.IndexDocumentAsync(filePath, extractedText, chunks);

            _documents.Add(new DocumentInfo
            {
                Name = fileName,
                FilePath = filePath,
                Type = extension.TrimStart('.').ToUpperInvariant(),
                ChunkCount = chunks.Count,
                Info = $"{chunks.Count} chunks • {extractedText.Length} chars • ~{chunks.Sum(c => c.TokenCount)} tokens"
            });

            AppendChat("system", $"✓ {fileName} loaded ({chunks.Count} chunks)");
        }

        private async Task<(string Content, List<string> FileNames)> ResolveWorkspaceDocumentContentAsync()
        {
            var sb = new StringBuilder();
            var fileNames = new List<string>();

            foreach (var doc in _documents)
            {
                if (string.IsNullOrWhiteSpace(doc.FilePath) || !File.Exists(doc.FilePath))
                    continue;

                string fileName = string.IsNullOrWhiteSpace(doc.Name) ? Path.GetFileName(doc.FilePath) : doc.Name;
                string ext = Path.GetExtension(doc.FilePath).ToLowerInvariant();
                string? text = null;

                try
                {
                    if (ext == ".pdf")
                    {
                        text = await PdfExtractor.ExtractTextFromPdfAsync(doc.FilePath);
                    }
                    else if (IsPlainTextExtension(ext))
                    {
                        text = await Task.Run(() =>
                        {
                            try { return File.ReadAllText(doc.FilePath, Encoding.UTF8); }
                            catch { return File.ReadAllText(doc.FilePath, Encoding.Default); }
                        });
                    }
                    else
                    {
                        ChatAttachmentImportResult imported = await ChatAttachmentImportService.ImportAsync(doc.FilePath);
                        text = imported.Attachment.IsImage ? null : imported.Attachment.Content;
                    }
                }
                catch
                {
                    text = null;
                }

                // Fall back to the indexed chunks when on-disk extraction fails (file moved/locked).
                if (string.IsNullOrWhiteSpace(text))
                    text = _documentRetriever.GetAllTextForFile(fileName);

                if (string.IsNullOrWhiteSpace(text))
                    continue;

                fileNames.Add(fileName);
                sb.AppendLine($"═══ {fileName} ═══");
                sb.AppendLine(text.Trim());
                sb.AppendLine();
            }

            return (sb.ToString().Trim(), fileNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        private static string BuildAttachedDocumentSystemPromptBlock(string documentContent, IReadOnlyCollection<string> documentFileNames, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(documentContent))
                return string.Empty;

            string fileName = documentFileNames != null && documentFileNames.Count > 0
                ? string.Join(", ", documentFileNames.Where(name => !string.IsNullOrWhiteSpace(name)).Take(6))
                : "AttachedDocument.txt";

            // Cap the document copy that goes into the SYSTEM prompt. An uncapped document
            // dominates the system message, which (a) pushes the role text and the user's actual
            // request out of the model's focus — small local models then echo the role/environment
            // briefing instead of answering — and (b) can overflow the context window outright.
            string body = documentContent.Trim();
            if (maxChars > 0 && body.Length > maxChars)
                body = body[..maxChars] + "\n[...document truncated here; the full text is also provided in the task payload]";

            return string.Join("\n",
                AttachedDocumentHeaderLine,
                fileName,
                body,
                AttachedDocumentEndLine).Trim();
        }

        // Char budget for the document copy embedded in a role's SYSTEM prompt, scaled to the role's
        // context window. The Builder receives the full (separately capped) document in its USER payload
        // via BuildDocumentContentBlock, so it needs no system-prompt copy at all — a second uncapped
        // copy there only doubles context pressure and crowds out the actual task.
        private int GetSystemPromptDocumentBudgetChars(CouncilRole role)
        {
            int ctxTokens = (int)GetRoleContextSize(role);
            double fraction = role switch
            {
                CouncilRole.Architect => 0.40,
                CouncilRole.Critic => 0.22,
                _ => 0.0 // Builder: document lives in the user payload instead.
            };
            return (int)(ctxTokens * fraction * AvgCharsPerToken);
        }

        private static string ComposeCouncilSystemPrompt(string systemPrompt, CouncilRole role, CouncilRunContext? context, int documentCharBudget)
        {
            string prompt = (systemPrompt ?? string.Empty).Trim();
            if (context == null || string.IsNullOrWhiteSpace(context.DocumentContent))
                return prompt;

            // When the budget is zero (e.g. the Builder, which gets the document in its user payload)
            // the document body is omitted from the system prompt, but the "a document is attached"
            // instruction is still emitted so the role knows to ground its answer in it.
            string documentBlock = documentCharBudget > 0
                ? BuildAttachedDocumentSystemPromptBlock(context.DocumentContent, context.DocumentFileNames, documentCharBudget)
                : string.Empty;

            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(documentBlock))
                sb.Append(documentBlock);

            if (!string.IsNullOrWhiteSpace(prompt))
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine();
                }
                sb.Append(prompt);
            }

            if (role is CouncilRole.Architect or CouncilRole.Builder)
            {
                sb.AppendLine();
                sb.AppendLine();
                sb.Append(AttachedDocumentRequiredReferenceInstruction);
            }

            return sb.ToString().Trim();
        }

        private async Task RefreshConceptCloudAsync()
        {
            var concepts = await _semanticMemory.GetConceptCloudAsync(60);
            _conceptTags.Clear();

            foreach (var concept in concepts)
            {
                _conceptTags.Add(new ConceptTagViewModel
                {
                    Name = concept.Name,
                    Weight = concept.Weight
                });
            }

            if (_conceptTags.Count == 0)
            {
                AppendChat("system", "Semantic memory has no concepts yet.");
            }
        }

        private async void ConceptTag_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string concept || string.IsNullOrWhiteSpace(concept))
            {
                return;
            }

            RelayStatusBlock.Text = "Relay: Semantic search...";
            LogActivity($"Filtering semantic memory with tag '{concept}'...");

            try
            {
                int maxChunks = Math.Max(1, _documentRetriever.CalculateMaxChunksForContext((int)_contextSize) / 2);
                var focused = await _semanticMemory.SearchByConceptAsync(concept, maxChunks);

                _nextPromptPriorityChunks.Clear();
                _nextPromptPriorityChunks.AddRange(focused);
                _nextPromptPriorityConcept = concept;

                MemoryFocusBlock.Text = focused.Count > 0
                    ? $"Memory Focus: {concept} ({focused.Count} chunks primed)"
                    : $"Memory Focus: {concept} (no focused chunks found)";

                AppendChat("memory", $"Focused context primed from tag '{concept}'.");
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Semantic memory search failed: {ex.Message}");
            }
            finally
            {
                RelayStatusBlock.Text = "Relay: Idle";
            }
        }

        private void ClearDocuments_Click(object sender, RoutedEventArgs e)
        {
            _documents.Clear();
            _documentContextEngaged = false;
            _conceptTags.Clear();
            _documentRetriever.ClearChunks();
            _semanticMemory.Clear();
            _nextPromptPriorityChunks.Clear();
            _nextPromptPriorityConcept = null;
            MemoryFocusBlock.Text = "Memory Focus: None";
            AppendChat("system", "All documents and semantic memory cleared.");
            LogActivity("Project assets and memory cleared.");
            UpdateContextInfo();
        }

        private void LoadArchitectModel_Click(object sender, RoutedEventArgs e) => SelectCouncilModel(CouncilRole.Architect, ArchitectFormatCombo);
        private void LoadBuilderModel_Click(object sender, RoutedEventArgs e) => SelectCouncilModel(CouncilRole.Builder, BuilderFormatCombo);
        private void LoadCriticModel_Click(object sender, RoutedEventArgs e) => SelectCouncilModel(CouncilRole.Critic, CriticFormatCombo);

        private void SelectCouncilModel(CouncilRole role, ComboBox combo)
        {
            var dialog = new OpenFileDialog { Filter = "GGUF files (*.gguf)|*.gguf" };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            if (IsMmprojFile(dialog.FileName))
            {
                AppendChat("error", "Selected file is an mmproj projector, not a text model. Select the main non-mmproj .gguf model file.");
                return;
            }

            string? previousModelPath = _council[role].ModelPath;
            if (!string.IsNullOrWhiteSpace(previousModelPath)
                && !string.Equals(previousModelPath, dialog.FileName, StringComparison.OrdinalIgnoreCase)
                && _modelCache.TryGetValue(previousModelPath, out var previousEntry))
            {
                previousEntry.Dispose();
                _modelCache.Remove(previousModelPath);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
            }

            _council[role].ModelPath = dialog.FileName;
            _effectiveLocalRoleContextSizes.Remove(role);
            ApplyQwen3DefaultCouncilProfile(role, dialog.FileName);
            if (!IsQwen3Model(dialog.FileName))
            {
                _council[role].Format = IsGemma4Model(dialog.FileName)
                    ? PromptFormat.Gemma4
                    : ParsePromptFormat(combo.SelectedItem as ComboBoxItem);
            }

            if (_autoOptimizeRoleContexts)
            {
                ApplyOptimizedRoleContexts();
                SyncContextControls();
                UpdateContextInfo();
            }

            UpdateCouncilBlocks();
            AppendChat("system", $"{role} model set: {_council[role].DisplayName}");
            LogActivity($"{role} model configured: {_council[role].DisplayName}");
            SavePersistedSession();
        }

        private void ClearSessionMemory_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Clear prior run memory and Session Hippocampus entries for this session?",
                "Clear Session Memory",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            _sessionMemory = null;
            _completedCouncilRunCount = 0;
            _studySessionProcessedDocumentCount = 0;
            _studySessionDomainDefinitionCount = 0;
            _sessionHippocampus.Clear(true);
            SessionMemoryStatusBlock.Text = "No prior run stored.";
            LogActivity("Session memory cleared by user.");
            AppendChat("system", "Council session memory cleared.");
            SavePersistedSession();
        }

        private static PromptFormat ParsePromptFormat(ComboBoxItem? selected)
        {
            string label = selected?.Content?.ToString() ?? "ChatML";
            return label switch
            {
                "Gemma 4" => PromptFormat.Gemma4,
                "Llama 3" => PromptFormat.Llama3,
                "Alpaca" => PromptFormat.Alpaca,
                _ => PromptFormat.ChatML
            };
        }

        private static readonly HashSet<string> PlainTextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".md", ".markdown", ".cs", ".py", ".json", ".jsonc", ".xml", ".yaml", ".yml", ".toml", ".html", ".htm", ".css", ".scss", ".js", ".mjs", ".ts", ".jsx", ".tsx",
            ".csv", ".tsv", ".log", ".ini", ".config", ".conf", ".sql", ".java", ".cpp", ".cc", ".c", ".h", ".hpp", ".go", ".rs", ".rb", ".php", ".swift", ".kt", ".vb", ".r", ".pl", ".lua",
            ".ps1", ".psm1", ".bat", ".cmd", ".sh", ".bash", ".xaml", ".csproj", ".props", ".targets", ".gradle", ".cmake", ".dockerfile", ".editorconfig", ".gitignore",
            ".tex", ".bib", ".rst", ".adoc", ".srt", ".vtt", ".diff", ".patch", ".graphql", ".proto"
        };

        private static bool IsPlainTextExtension(string extension)
        {
            return PlainTextExtensions.Contains(extension);
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            await SendQueryAsync();
        }

        private void StopMessage_Click(object sender, RoutedEventArgs e)
        {
            RelayStatusBlock.Text = "Relay: Stopping...";
            PublishCouncilPetStatus("Council", "Stopping the run.");
            StopButton.IsEnabled = false;
            _cancellationTokenSource?.Cancel();
        }

        private void WebSearchToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isWebSearchEnabled = !_isWebSearchEnabled;

            RefreshWorkplaceWebToggleUi();

            AppendChat("system", _isWebSearchEnabled
                ? "Web Search tool enabled. Future requests will automatically use strategic web lookup when relevant."
                : "Web Search tool disabled for Agentic Pause.");
        }

        private void CouncilPetToggleButton_Click(object sender, RoutedEventArgs e)
        {
            CouncilPetToggleRequested?.Invoke(!_isCouncilPetEnabled);
        }

        private void CodebaseEditAccessButton_Click(object sender, RoutedEventArgs e)
        {
            if (_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Codebase Edit Access is already locked for this Workplace chat. Start a new Workplace chat to change or disable it.");
                return;
            }

            string lockedMode = _isCloudModeEnabled
                ? WorkspaceAgentMode.Cloud.ToString()
                : WorkspaceAgentMode.Local.ToString();

            _connectedWorkspace.CodebaseEditAccessEnabled = true;
            _connectedWorkspace.LockedMode = lockedMode;
            _connectedWorkspace.EnabledAt = DateTime.Now;
            _connectedWorkspace.StatusMessage = $"Enabled {DateTime.Now:HH:mm}; mode locked to {lockedMode}.";
            RefreshCodebaseAccessUi();
            RefreshWorkplaceCloudModeUi();
            AppendChat("system", $"Codebase Edit Access enabled. This Workplace chat is locked to {lockedMode} mode until you start a new Workplace chat.");
            SavePersistedSession();
        }

        private void CodebaseAutoApplyToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                if (CodebaseAutoApplyToggle != null)
                    CodebaseAutoApplyToggle.IsChecked = false;
                return;
            }

            bool enabled = CodebaseAutoApplyToggle?.IsChecked == true;
            if (_connectedWorkspace.AutoApplyCodebaseChanges == enabled)
                return;

            _connectedWorkspace.AutoApplyCodebaseChanges = enabled;
            _connectedWorkspace.StatusMessage = enabled
                ? "Auto mode enabled. Valid patches will be applied after Builder produces them."
                : "Auto mode disabled. New patches will wait for Accept or Reject.";
            RefreshCodebaseAccessUi();
            AppendChat("system", enabled
                ? "Auto mode enabled for Codebase Edit Access. Valid patches will be written automatically after parsing and path checks."
                : "Auto mode disabled. Future codebase patches will wait for manual Accept or Reject.");
            SavePersistedSession();
        }

        private void ConnectWorkspaceFolderButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Enable Codebase Edit Access before connecting a folder.");
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Connect Codebase Folder"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                WorkspaceIndexResult index = _workspaceAccessService.IndexWorkspace(dialog.FolderName);
                _connectedWorkspace.ConnectionKind = WorkspaceConnectionKind.Folder.ToString();
                _connectedWorkspace.RootPath = index.RootPath;
                _connectedWorkspace.RepositoryUrl = string.Empty;
                _connectedWorkspace.ConnectedFiles.Clear();
                _connectedWorkspace.DisplayName = index.DisplayName;
                _connectedWorkspace.IndexedFileCount = index.Files.Count;
                _connectedWorkspace.IndexedByteCount = index.TotalBytes;
                _connectedWorkspace.IndexedAt = DateTime.Now;
                WorkspaceGitStatus gitStatus = _workspaceAccessService.GetGitStatus(index.RootPath);
                string gitSuffix = gitStatus.IsRepository
                    ? $" Git branch: {(string.IsNullOrWhiteSpace(gitStatus.Branch) ? "detached HEAD" : gitStatus.Branch)}."
                    : "";
                _connectedWorkspace.StatusMessage = $"Indexed {index.Files.Count:n0} candidate source files at {_connectedWorkspace.IndexedAt:HH:mm}.{gitSuffix}";
                RefreshCodebaseAccessUi();
                AppendChat("system", $"Connected workspace '{index.DisplayName}' with {index.Files.Count:n0} indexed file(s).");
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                _connectedWorkspace.StatusMessage = "Folder connection failed: " + ex.Message;
                RefreshCodebaseAccessUi();
                AppendChat("error", $"Workspace connection failed: {ex.Message}");
            }
        }

        private void ConnectWorkspaceFilesButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Enable Codebase Edit Access before connecting files.");
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "Code and text files|*.cs;*.xaml;*.csproj;*.sln;*.slnx;*.py;*.js;*.ts;*.jsx;*.tsx;*.json;*.jsonc;*.xml;*.yaml;*.yml;*.toml;*.html;*.htm;*.css;*.md;*.txt;*.sql;*.java;*.cpp;*.c;*.h;*.go;*.rs;*.rb;*.php;*.ps1;*.bat;*.sh|All files (*.*)|*.*",
                Multiselect = true,
                Title = "Connect Codebase Files"
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                WorkspaceIndexResult index = _workspaceAccessService.IndexFiles(dialog.FileNames);
                _connectedWorkspace.ConnectionKind = WorkspaceConnectionKind.Files.ToString();
                _connectedWorkspace.RootPath = string.Empty;
                _connectedWorkspace.RepositoryUrl = string.Empty;
                _connectedWorkspace.ConnectedFiles = dialog.FileNames.Select(Path.GetFullPath).ToList();
                _connectedWorkspace.DisplayName = index.DisplayName;
                _connectedWorkspace.IndexedFileCount = index.Files.Count;
                _connectedWorkspace.IndexedByteCount = index.TotalBytes;
                _connectedWorkspace.IndexedAt = DateTime.Now;
                _connectedWorkspace.StatusMessage = $"Connected {index.Files.Count:n0} file(s) at {_connectedWorkspace.IndexedAt:HH:mm}.";
                RefreshCodebaseAccessUi();
                AppendChat("system", $"Connected {index.Files.Count:n0} codebase file(s).");
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                _connectedWorkspace.StatusMessage = "File connection failed: " + ex.Message;
                RefreshCodebaseAccessUi();
                AppendChat("error", $"Workspace file connection failed: {ex.Message}");
            }
        }

        private async void ConnectWorkspaceRepositoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Enable Codebase Edit Access before connecting a repository.");
                return;
            }

            string? url = ShowTextInputDialog(
                "Clone GitHub/Repo",
                "Paste a GitHub, GitLab, Bitbucket, or git repository URL. Next, choose where to clone it.",
                _connectedWorkspace.RepositoryUrl);
            if (string.IsNullOrWhiteSpace(url))
                return;

            url = url.Trim();
            if (!_workspaceAccessService.LooksLikeRepositoryUrl(url))
            {
                AppendChat("error", "That does not look like a repository URL. Use an https://, git://, or ssh:// URL.");
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Choose Where To Clone The Repository"
            };

            if (dialog.ShowDialog() != true)
                return;

            _connectedWorkspace.ConnectionKind = WorkspaceConnectionKind.GitRepository.ToString();
            _connectedWorkspace.RepositoryUrl = url;
            _connectedWorkspace.RootPath = string.Empty;
            _connectedWorkspace.ConnectedFiles.Clear();
            _connectedWorkspace.DisplayName = BuildRepositoryDisplayName(url);
            _connectedWorkspace.IndexedFileCount = 0;
            _connectedWorkspace.IndexedByteCount = 0;
            _connectedWorkspace.IndexedAt = DateTime.Now;
            await CloneConnectedRepositoryToFolderAsync(url, dialog.FolderName, _connectedWorkspace.DisplayName);
        }

        private async void CloneWorkspaceRepositoryButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Enable Codebase Edit Access before cloning a repository.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_connectedWorkspace.RepositoryUrl))
            {
                AppendChat("system", "Connect a GitHub/repo URL before cloning.");
                return;
            }

            if (!string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath))
            {
                AppendChat("system", "This repository already has a local folder connected.");
                return;
            }

            var dialog = new OpenFolderDialog
            {
                Title = "Choose Where To Clone The Repository"
            };

            if (dialog.ShowDialog() != true)
                return;

            await CloneConnectedRepositoryToFolderAsync(
                _connectedWorkspace.RepositoryUrl,
                dialog.FolderName,
                _connectedWorkspace.DisplayName);
        }

        private async Task CloneConnectedRepositoryToFolderAsync(string repositoryUrl, string parentFolder, string displayName)
        {
            SetCodebaseConnectionButtonsEnabled(false);
            string previousStatus = _connectedWorkspace.StatusMessage;
            _connectedWorkspace.StatusMessage = "Cloning repository...";
            RefreshCodebaseAccessUi();
            AppendChat("system", "Cloning repository. If it is private, Git may ask for credentials through your normal Git/GitHub sign-in.");

            try
            {
                var progress = new Progress<string>(line =>
                {
                    string compact = line.Length > 120 ? line[..120] + "..." : line;
                    _connectedWorkspace.StatusMessage = compact;
                    RefreshCodebaseAccessUi();
                });

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                WorkspaceCloneResult clone = await _workspaceAccessService.CloneRepositoryAsync(
                    repositoryUrl,
                    parentFolder,
                    displayName,
                    progress,
                    cts.Token);

                WorkspaceIndexResult index = _workspaceAccessService.IndexWorkspace(clone.LocalPath);
                _connectedWorkspace.ConnectionKind = WorkspaceConnectionKind.GitRepository.ToString();
                _connectedWorkspace.RepositoryUrl = repositoryUrl;
                _connectedWorkspace.RootPath = index.RootPath;
                _connectedWorkspace.ConnectedFiles.Clear();
                _connectedWorkspace.DisplayName = index.DisplayName;
                _connectedWorkspace.IndexedFileCount = index.Files.Count;
                _connectedWorkspace.IndexedByteCount = index.TotalBytes;
                _connectedWorkspace.IndexedAt = DateTime.Now;
                WorkspaceGitStatus gitStatus = _workspaceAccessService.GetGitStatus(index.RootPath);
                string gitSuffix = gitStatus.IsRepository
                    ? $" Git branch: {(string.IsNullOrWhiteSpace(gitStatus.Branch) ? "detached HEAD" : gitStatus.Branch)}."
                    : "";
                _connectedWorkspace.StatusMessage = $"Cloned and indexed {index.Files.Count:n0} candidate source files at {_connectedWorkspace.IndexedAt:HH:mm}.{gitSuffix}";
                RefreshCodebaseAccessUi();
                AppendChat("system", $"Repository cloned to {index.RootPath} and indexed with {index.Files.Count:n0} file(s).");
                SavePersistedSession();
            }
            catch (OperationCanceledException)
            {
                _connectedWorkspace.StatusMessage = "Clone timed out after 10 minutes. Try again or connect an existing local clone folder.";
                RefreshCodebaseAccessUi();
                AppendChat("error", _connectedWorkspace.StatusMessage);
            }
            catch (Exception ex)
            {
                _connectedWorkspace.StatusMessage = string.IsNullOrWhiteSpace(previousStatus)
                    ? "Clone failed."
                    : previousStatus;
                RefreshCodebaseAccessUi();
                AppendChat("error", "Repository clone failed: " + ex.Message);
            }
            finally
            {
                SetCodebaseConnectionButtonsEnabled(true);
                RefreshCodebaseAccessUi();
            }
        }

        private void SetCodebaseConnectionButtonsEnabled(bool enabled)
        {
            if (CodebaseEditAccessButton != null)
                CodebaseEditAccessButton.IsEnabled = enabled && !_connectedWorkspace.CodebaseEditAccessEnabled && !_isProcessing;
            if (ConnectWorkspaceFolderButton != null)
                ConnectWorkspaceFolderButton.IsEnabled = enabled && _connectedWorkspace.CodebaseEditAccessEnabled && !_isProcessing;
            if (ConnectWorkspaceFilesButton != null)
                ConnectWorkspaceFilesButton.IsEnabled = enabled && _connectedWorkspace.CodebaseEditAccessEnabled && !_isProcessing;
            if (ConnectWorkspaceRepositoryButton != null)
                ConnectWorkspaceRepositoryButton.IsEnabled = enabled && _connectedWorkspace.CodebaseEditAccessEnabled && !_isProcessing;
            if (CloneWorkspaceRepositoryButton != null)
                CloneWorkspaceRepositoryButton.IsEnabled = enabled
                    && _connectedWorkspace.CodebaseEditAccessEnabled
                    && !_isProcessing
                    && !string.IsNullOrWhiteSpace(_connectedWorkspace.RepositoryUrl)
                    && string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath);
        }

        private static string BuildRepositoryDisplayName(string url)
        {
            try
            {
                var uri = new Uri(url);
                string name = uri.Segments.LastOrDefault()?.Trim('/') ?? uri.Host;
                if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    name = name[..^4];
                return string.IsNullOrWhiteSpace(name) ? uri.Host : name;
            }
            catch
            {
                string trimmed = url.TrimEnd('/');
                int slash = trimmed.LastIndexOf('/');
                string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
                return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            }
        }

        private static string? ShowTextInputDialog(string title, string prompt, string initialValue)
        {
            var window = new Window
            {
                Title = title,
                Width = 440,
                Height = 178,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(23, 22, 21)),
                Foreground = new SolidColorBrush(Color.FromRgb(237, 232, 227)),
                ShowInTaskbar = false
            };

            var root = new Grid { Margin = new Thickness(16) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = prompt,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(label, 0);
            root.Children.Add(label);

            var input = new TextBox
            {
                Text = initialValue ?? string.Empty,
                MinWidth = 380,
                Height = 28,
                Margin = new Thickness(0, 0, 0, 14)
            };
            Grid.SetRow(input, 1);
            root.Children.Add(input);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 74, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsCancel = true };
            var ok = new Button { Content = "Connect", MinWidth = 82, Height = 28, IsDefault = true };
            ok.Click += (_, _) => window.DialogResult = true;
            buttons.Children.Add(cancel);
            buttons.Children.Add(ok);
            Grid.SetRow(buttons, 2);
            root.Children.Add(buttons);

            window.Content = root;
            input.SelectAll();
            input.Focus();
            return window.ShowDialog() == true ? input.Text : null;
        }

        private bool TryCaptureCodebasePatchProposal(string builderOutput, CouncilRunContext runContext)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled || string.IsNullOrWhiteSpace(builderOutput))
                return false;

            bool hasPatchMarker = builderOutput.Contains("[[AXIOM_CODEBASE_PATCH]]", StringComparison.OrdinalIgnoreCase);
            if (!_workspaceAccessService.TryParsePatchProposal(builderOutput, out WorkspacePatchProposal? proposal, out string error))
            {
                if (TryRecoverCodebasePatchProposal(builderOutput, runContext, out WorkspacePatchProposal? recoveredProposal, out string recoveryReason)
                    && recoveredProposal != null)
                {
                    proposal = recoveredProposal;
                    LogActivity("Codebase patch recovered from Builder output: " + recoveryReason);
                    AppendChat("system", "Recovered a reviewable codebase patch from the Builder's full-file output.");
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(recoveryReason))
                        LogActivity("Codebase patch recovery skipped: " + recoveryReason);

                    if (hasPatchMarker)
                    {
                        string reason = string.IsNullOrWhiteSpace(error) ? "the patch envelope could not be parsed." : error;
                        AppendChat("warning", "Builder attempted a codebase patch, but it needs revision: " + reason);
                        LogActivity("Codebase patch parse failed: " + reason);
                    }

                    return false;
                }
            }

            if (proposal == null)
                return false;

            proposal = PreferPromptNamedPatchTargets(proposal, runContext);

            try
            {
                foreach (WorkspaceFilePatch patch in proposal.Files)
                    _workspaceAccessService.ResolvePatchTargetPath(_connectedWorkspace, patch);
            }
            catch (Exception ex)
            {
                string reason = "the patch target could not be resolved: " + ex.Message;
                _pendingCodebasePatch = proposal;
                _hasPendingCodebaseChanges = true;
                RenderCodebasePatchReview(proposal, autoMode: false);
                AddCodebasePatchNoticeRow("Path check failed. No files were changed.\n- " + reason);
                RefreshCodebaseAccessUi();
                SavePersistedSession();
                AppendChat("warning", "Builder produced a patch, but it is pending manual review because " + reason);
                LogActivity("Codebase patch pending manual review: " + reason);
                return true;
            }

            _pendingCodebasePatch = proposal;
            _hasPendingCodebaseChanges = true;
            RenderCodebasePatchReview(proposal, _connectedWorkspace.AutoApplyCodebaseChanges);
            RefreshCodebaseAccessUi();
            SavePersistedSession();

            string summary = _connectedWorkspace.AutoApplyCodebaseChanges
                ? proposal.Files.Count == 1
                    ? "Builder proposed 1 codebase file change. Auto mode is applying it now."
                    : $"Builder proposed {proposal.Files.Count:n0} codebase file changes. Auto mode is applying them now."
                : proposal.Files.Count == 1
                    ? "Builder proposed 1 codebase file change. Review it in Project Canvas, then Accept or Reject."
                    : $"Builder proposed {proposal.Files.Count:n0} codebase file changes. Review them in Project Canvas, then Accept or Reject.";
            AppendChat("builder", summary);
            LogActivity($"Codebase patch captured for review ({proposal.Files.Count:n0} file(s)).");
            if (_connectedWorkspace.AutoApplyCodebaseChanges)
                TryApplyPendingCodebaseChanges(automatic: true);
            return true;
        }

        private void RenderCodebasePatchReview(WorkspacePatchProposal proposal, bool autoMode)
        {
            string review = BuildCodebasePatchReviewText(proposal);
            ExitCanvasDiffView();
            ClearCanvasDiff();
            ProjectCanvasEditor.Text = review;
            _canvasArtifact = ArtifactRenderInfo.None(review);
            _isCanvasPreviewMode = false;
            SetCanvasHighlighting("markdown");
            RenderCodebasePatchVisualDiff(proposal, autoMode);
            RefreshCanvasArtifactUi();
            CanvasTitleBlock.Text = "Codebase Patch Review";
            CanvasSubtitleBlock.Text = autoMode
                ? $"{proposal.Files.Count:n0} file change(s). Auto mode will apply after checks."
                : $"{proposal.Files.Count:n0} pending file change(s). Review, then accept or reject.";
            UpdateWorkplaceTokenUsageIndicator();
        }

        private void RenderCodebasePatchVisualDiff(WorkspacePatchProposal proposal, bool autoMode)
        {
            DiffViewerLines.Items.Clear();
            int totalAdditions = 0;
            int totalRemovals = 0;
            var fileSummaries = new List<(WorkspaceFilePatch Patch, string Original, IReadOnlyList<LineDiffEntry> Diff, string? Error)>();

            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                string original = string.Empty;
                string? error = null;
                try
                {
                    string target = _workspaceAccessService.ResolvePatchTargetPath(_connectedWorkspace, patch);
                    if (File.Exists(target))
                        original = File.ReadAllText(target);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                }

                IReadOnlyList<LineDiffEntry> diff = error == null
                    ? LineDiff.Build(original, patch.Content)
                    : Array.Empty<LineDiffEntry>();
                totalAdditions += diff.Count(entry => entry.Kind == LineDiffKind.Added);
                totalRemovals += diff.Count(entry => entry.Kind == LineDiffKind.Removed);
                fileSummaries.Add((patch, original, diff, error));
            }

            AddCodebasePatchSummaryRow(proposal.Files.Count, totalAdditions, totalRemovals, autoMode);
            foreach (var (patch, _, diff, error) in fileSummaries)
            {
                AddCodebasePatchFileHeader(patch, diff, error);
                if (!string.IsNullOrWhiteSpace(error))
                {
                    AddCodebasePatchNoticeRow("Unable to render this file diff: " + error);
                    continue;
                }

                AddCodebasePatchDiffRows(diff);
            }

            _isDiffViewActive = true;
            DiffViewerScroller.Visibility = Visibility.Visible;
            ProjectCanvasEditor.Visibility = Visibility.Collapsed;
        }

        private void AddCodebasePatchSummaryRow(int fileCount, int additions, int removals, bool autoMode)
        {
            var panel = new StackPanel { Margin = new Thickness(14, 14, 14, 12) };
            panel.Children.Add(new TextBlock
            {
                Text = $"{fileCount:n0} file change(s)",
                Foreground = new SolidColorBrush(Color.FromRgb(237, 232, 227)),
                FontWeight = FontWeights.SemiBold,
                FontSize = 14
            });
            panel.Children.Add(new TextBlock
            {
                Text = autoMode
                    ? $"Auto mode is on; the app will apply this after parsing and path checks.  +{additions:n0} / -{removals:n0} lines"
                    : $"Review below, then use Accept Changes or Reject.  +{additions:n0} / -{removals:n0} lines",
                Foreground = new SolidColorBrush(Color.FromRgb(168, 160, 150)),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            });
            DiffViewerLines.Items.Add(panel);
        }

        private void AddCodebasePatchFileHeader(WorkspaceFilePatch patch, IReadOnlyList<LineDiffEntry> diff, string? error)
        {
            int additions = diff.Count(entry => entry.Kind == LineDiffKind.Added);
            int removals = diff.Count(entry => entry.Kind == LineDiffKind.Removed);
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(27, 26, 24)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(58, 54, 48)),
                BorderThickness = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(14, 10, 14, 10),
                Margin = new Thickness(0, 8, 0, 0)
            };

            var row = new DockPanel { LastChildFill = true };
            var summary = new TextBlock
            {
                Text = error == null ? $"+{additions:n0} / -{removals:n0}" : "diff unavailable",
                Foreground = error == null
                    ? new SolidColorBrush(Color.FromRgb(120, 220, 140))
                    : new SolidColorBrush(Color.FromRgb(255, 180, 120)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            DockPanel.SetDock(summary, Dock.Right);
            row.Children.Add(summary);
            row.Children.Add(new TextBlock
            {
                Text = $"{patch.RelativePath}  ({patch.Action})",
                Foreground = new SolidColorBrush(Color.FromRgb(237, 232, 227)),
                FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            border.Child = row;
            DiffViewerLines.Items.Add(border);
        }

        private void AddCodebasePatchNoticeRow(string text)
        {
            DiffViewerLines.Items.Add(new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(255, 180, 120)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                Margin = new Thickness(14, 8, 14, 8),
                TextWrapping = TextWrapping.Wrap
            });
        }

        private void AddCodebasePatchDiffRows(IReadOnlyList<LineDiffEntry> diff)
        {
            if (diff.Count == 0)
            {
                AddCodebasePatchNoticeRow("No textual differences detected.");
                return;
            }

            const int contextLines = 3;
            const int maxVisibleLines = 900;
            var visibleIndices = new HashSet<int>();
            for (int i = 0; i < diff.Count; i++)
            {
                if (diff[i].Kind == LineDiffKind.Unchanged)
                    continue;

                int start = Math.Max(0, i - contextLines);
                int end = Math.Min(diff.Count - 1, i + contextLines);
                for (int index = start; index <= end; index++)
                    visibleIndices.Add(index);
            }

            if (visibleIndices.Count == 0)
            {
                AddCodebasePatchNoticeRow("No textual differences detected.");
                return;
            }

            int previousVisibleIndex = -2;
            int rendered = 0;
            foreach (int index in visibleIndices.OrderBy(value => value))
            {
                if (rendered >= maxVisibleLines)
                {
                    AddCodebasePatchNoticeRow("Diff truncated for review.");
                    break;
                }

                if (index > previousVisibleIndex + 1)
                    AddDiffOmissionRow();

                AddDiffRow(diff[index]);
                previousVisibleIndex = index;
                rendered++;
            }
        }

        private void RenderCodebasePatchFailureReview(string rejectedOutput, string reason)
        {
            _pendingCodebasePatch = null;
            _hasPendingCodebaseChanges = false;

            var review = new StringBuilder();
            review.AppendLine("# Codebase Patch Review");
            review.AppendLine();
            review.AppendLine("No files were changed because the Builder output could not be applied as a codebase patch.");
            review.AppendLine();
            review.AppendLine("## Specific Reason");
            review.AppendLine(reason);
            review.AppendLine();
            review.AppendLine("## What to try next");
            review.AppendLine("- Ask for a smaller change to one specific file.");
            review.AppendLine("- Name the target file, for example `index.html` or `styles.css`.");
            review.AppendLine("- Keep the request focused on editing the connected repo, not generating a standalone canvas artifact.");
            review.AppendLine();
            if (!string.IsNullOrWhiteSpace(rejectedOutput))
            {
                string capped = rejectedOutput.Length > 4000
                    ? rejectedOutput[..4000] + "\n\n[...rejected output truncated...]"
                    : rejectedOutput;
                review.AppendLine("## Rejected Builder Output");
                review.AppendLine("````text");
                review.AppendLine(capped);
                review.AppendLine("````");
            }

            ExitCanvasDiffView();
            ClearCanvasDiff();
            ProjectCanvasEditor.Text = review.ToString().TrimEnd();
            _canvasArtifact = ArtifactRenderInfo.None(ProjectCanvasEditor.Text);
            _isCanvasPreviewMode = false;
            SetCanvasHighlighting("markdown");
            RefreshCanvasArtifactUi();
            RefreshCodebaseAccessUi();
            CanvasTitleBlock.Text = "Codebase Patch Review";
            CanvasSubtitleBlock.Text = "No files changed.";
            UpdateWorkplaceTokenUsageIndicator();
            SavePersistedSession();
        }

        private bool TryRecoverCodebasePatchProposal(string builderOutput, CouncilRunContext runContext, out WorkspacePatchProposal? proposal, out string reason)
        {
            proposal = null;
            reason = string.Empty;
            if (!_connectedWorkspace.CodebaseEditAccessEnabled || string.IsNullOrWhiteSpace(builderOutput))
                return false;

            string? targetPath = ResolveRequestedWorkspacePatchPath(runContext, builderOutput);
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                reason = "No single target file could be identified from the request.";
                return false;
            }

            string content = ExtractRecoverableWorkspaceFileContent(builderOutput, targetPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                reason = "The Builder output did not contain recoverable full-file content.";
                return false;
            }

            string target = _workspaceAccessService.ResolvePatchTargetPath(
                _connectedWorkspace,
                new WorkspaceFilePatch(targetPath, "replace", content));
            bool exists = File.Exists(target);
            string action = exists ? "replace" : "create";
            if (!IsPlausibleRecoveredFileContent(targetPath, content, exists ? File.ReadAllText(target) : string.Empty))
            {
                reason = $"Recovered content did not look like a complete replacement for {targetPath}.";
                return false;
            }

            string rawPatch = BuildRecoveredCodebasePatchEnvelope(targetPath, action, content);
            if (!_workspaceAccessService.TryParsePatchProposal(rawPatch, out proposal, out string parseError) || proposal == null)
            {
                reason = string.IsNullOrWhiteSpace(parseError)
                    ? "Recovered patch could not be parsed."
                    : parseError;
                proposal = null;
                return false;
            }

            reason = $"Recovered a structured patch for {targetPath} from full-file Builder output.";
            return true;
        }

        private string? ResolveRequestedWorkspacePatchPath(CouncilRunContext runContext, string builderOutput)
        {
            string combined = $"{runContext.UserPrompt}\n{runContext.Objective}\n{builderOutput}";
            foreach (Match match in Regex.Matches(combined, @"(?<![\w.-])(?<path>[\w./\\-]+\.(?:cs|xaml|csproj|slnx|py|js|ts|tsx|jsx|json|jsonc|css|scss|html|htm|md|txt|xml|yaml|yml|toml))(?![\w.-])", RegexOptions.IgnoreCase))
            {
                string candidate = match.Groups["path"].Value.Trim().Replace('\\', '/');
                string? contextPath = ResolvePromptNamedWorkspaceFilePath(runContext, candidate);
                if (!string.IsNullOrWhiteSpace(contextPath) && CanResolveWorkspacePatchPath(contextPath))
                    return contextPath;
                if (CanResolveWorkspacePatchPath(candidate))
                    return candidate;
            }

            string recoveredContent = StripChatFromCode(builderOutput ?? string.Empty);
            string recoveredExtension = GuessRecoveredContentExtension(recoveredContent);
            var readableFiles = runContext.WorkspaceFilesRead
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (!string.IsNullOrWhiteSpace(recoveredExtension))
            {
                var extensionMatches = readableFiles
                    .Where(path => IsRecoveredExtensionCompatible(Path.GetExtension(path), recoveredExtension))
                    .Where(CanResolveWorkspacePatchPath)
                    .ToList();
                if (extensionMatches.Count == 1)
                    return extensionMatches[0];
            }

            var resolvable = readableFiles.Where(CanResolveWorkspacePatchPath).ToList();
            return resolvable.Count == 1 ? resolvable[0] : null;
        }

        private WorkspacePatchProposal PreferPromptNamedPatchTargets(WorkspacePatchProposal proposal, CouncilRunContext runContext)
        {
            if (proposal.Files.Count == 0 || runContext.WorkspaceFilesRead.Count == 0)
                return proposal;

            var renamed = new List<WorkspaceFilePatch>(proposal.Files.Count);
            bool changed = false;
            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                string relativePath = (patch.RelativePath ?? string.Empty).Replace('\\', '/');
                string fileName = Path.GetFileName(relativePath);
                if (!relativePath.Contains('/', StringComparison.Ordinal)
                    && PromptNamesWorkspaceFile(runContext, fileName))
                {
                    var matches = runContext.WorkspaceFilesRead
                        .Select(path => path.Replace('\\', '/'))
                        .Where(path => string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    if (matches.Count == 1 && !string.Equals(matches[0], relativePath, StringComparison.OrdinalIgnoreCase))
                    {
                        renamed.Add(new WorkspaceFilePatch(matches[0], patch.Action, patch.Content));
                        changed = true;
                        LogActivity($"Codebase patch target adjusted from {relativePath} to prompt-named context file {matches[0]}.");
                        continue;
                    }
                }

                renamed.Add(patch);
            }

            return changed ? new WorkspacePatchProposal(renamed, proposal.RawText) : proposal;
        }

        private static bool PromptNamesWorkspaceFile(CouncilRunContext runContext, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            string combined = $"{runContext.UserPrompt}\n{runContext.Objective}";
            return Regex.Matches(combined, @"(?<![\w.-])(?<path>[\w./\\-]+\.(?:cs|xaml|csproj|slnx|py|js|ts|tsx|jsx|json|jsonc|css|scss|html|htm|md|txt|xml|yaml|yml|toml))(?![\w.-])", RegexOptions.IgnoreCase)
                .Select(match => Path.GetFileName(match.Groups["path"].Value.Trim().Replace('\\', '/')))
                .Any(mentioned => string.Equals(mentioned, fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static string? ResolvePromptNamedWorkspaceFilePath(CouncilRunContext runContext, string candidate)
        {
            string normalized = (candidate ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(normalized) || runContext.WorkspaceFilesRead.Count == 0)
                return null;

            var readableFiles = runContext.WorkspaceFilesRead
                .Select(path => path.Replace('\\', '/'))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var exact = readableFiles
                .Where(path => string.Equals(path, normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (exact.Count == 1)
                return exact[0];

            if (normalized.Contains('/', StringComparison.Ordinal))
                return null;

            var fileNameMatches = readableFiles
                .Where(path => string.Equals(Path.GetFileName(path), normalized, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return fileNameMatches.Count == 1 ? fileNameMatches[0] : null;
        }

        private bool CanResolveWorkspacePatchPath(string relativePath)
        {
            try
            {
                string target = _workspaceAccessService.ResolvePatchTargetPath(
                    _connectedWorkspace,
                    new WorkspaceFilePatch(relativePath, "replace", string.Empty));
                return !string.IsNullOrWhiteSpace(target);
            }
            catch
            {
                return false;
            }
        }

        private static string ExtractRecoverableWorkspaceFileContent(string builderOutput, string targetPath)
        {
            string content = StripChatFromCode(builderOutput ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            content = content
                .Replace(BuilderCompletionMarker, "", StringComparison.OrdinalIgnoreCase)
                .Replace("[[CODEBASE PATCH FORMAT ERROR]]", "", StringComparison.OrdinalIgnoreCase)
                .Trim();

            string extension = Path.GetExtension(targetPath).ToLowerInvariant();
            if (extension is ".html" or ".htm")
            {
                int doctype = content.IndexOf("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
                int html = content.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
                int start = doctype >= 0 ? doctype : html;
                if (start > 0)
                    content = content[start..].Trim();
            }

            return content;
        }

        private static bool IsPlausibleRecoveredFileContent(string relativePath, string content, string originalContent)
        {
            if (string.IsNullOrWhiteSpace(content) || content.Length < 8)
                return false;

            string extension = Path.GetExtension(relativePath).ToLowerInvariant();
            string lower = content.ToLowerInvariant();
            if (extension is ".html" or ".htm")
                return lower.Contains("<html", StringComparison.Ordinal) || lower.Contains("<!doctype", StringComparison.Ordinal);
            if (extension == ".css")
                return content.Contains('{', StringComparison.Ordinal) && content.Contains('}', StringComparison.Ordinal);
            if (extension is ".json" or ".jsonc")
                return content.TrimStart().StartsWith("{", StringComparison.Ordinal) || content.TrimStart().StartsWith("[", StringComparison.Ordinal);
            if (extension is ".md" or ".txt")
                return true;

            if (!string.IsNullOrWhiteSpace(originalContent) && content.Length < Math.Min(80, originalContent.Length / 3))
                return false;

            return true;
        }

        private static string GuessRecoveredContentExtension(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;

            string trimmed = content.TrimStart();
            if (trimmed.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return ".html";
            if (trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal))
                return ".json";
            if (trimmed.Contains('{', StringComparison.Ordinal) && trimmed.Contains('}', StringComparison.Ordinal))
                return ".css";
            return string.Empty;
        }

        private static bool IsRecoveredExtensionCompatible(string candidateExtension, string recoveredExtension)
        {
            if (string.IsNullOrWhiteSpace(candidateExtension) || string.IsNullOrWhiteSpace(recoveredExtension))
                return false;

            if (string.Equals(candidateExtension, recoveredExtension, StringComparison.OrdinalIgnoreCase))
                return true;

            return (candidateExtension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                    && recoveredExtension.Equals(".html", StringComparison.OrdinalIgnoreCase))
                || (candidateExtension.Equals(".html", StringComparison.OrdinalIgnoreCase)
                    && recoveredExtension.Equals(".htm", StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildRecoveredCodebasePatchEnvelope(string relativePath, string action, string content)
        {
            string fence = content.Contains("````", StringComparison.Ordinal) ? "`````" : "````";
            string language = GuessFenceLanguage(relativePath);
            var sb = new StringBuilder();
            sb.AppendLine("[[AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("FILE: " + relativePath.Replace('\\', '/'));
            sb.AppendLine("ACTION: " + action);
            sb.AppendLine(fence + language);
            sb.AppendLine(content.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd());
            sb.AppendLine(fence);
            sb.AppendLine("[[END FILE]]");
            sb.AppendLine("[[END AXIOM_CODEBASE_PATCH]]");
            return sb.ToString();
        }

        private static string GuessFenceLanguage(string relativePath)
        {
            return Path.GetExtension(relativePath).ToLowerInvariant() switch
            {
                ".html" or ".htm" => "html",
                ".css" or ".scss" => "css",
                ".js" or ".jsx" or ".mjs" => "javascript",
                ".ts" or ".tsx" => "typescript",
                ".cs" => "csharp",
                ".py" => "python",
                ".json" or ".jsonc" => "json",
                ".xml" or ".xaml" => "xml",
                ".md" => "markdown",
                ".yml" or ".yaml" => "yaml",
                _ => ""
            };
        }

        private string BuildCodebasePatchReviewText(WorkspacePatchProposal proposal)
        {
            var sb = new StringBuilder();
            string mode = _connectedWorkspace.LockedMode?.ToString()
                ?? (_isCloudModeEnabled ? WorkspaceAgentMode.Cloud.ToString() : WorkspaceAgentMode.Local.ToString());
            string source = !string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath)
                ? _connectedWorkspace.RootPath
                : !string.IsNullOrWhiteSpace(_connectedWorkspace.RepositoryUrl)
                    ? _connectedWorkspace.RepositoryUrl
                    : _connectedWorkspace.DisplayName;

            sb.AppendLine("# Codebase Patch Review");
            sb.AppendLine();
            sb.AppendLine($"Pending changes: {proposal.Files.Count:n0} file(s)");
            sb.AppendLine($"Locked mode: {mode}");
            sb.AppendLine($"Connected workspace: {source}");
            if (_connectedWorkspace.IndexedFileCount > 0)
                sb.AppendLine($"Indexed context: {_connectedWorkspace.IndexedFileCount:n0} file(s), {FormatByteCount(_connectedWorkspace.IndexedByteCount)}");
            sb.AppendLine();
            sb.AppendLine(_connectedWorkspace.AutoApplyCodebaseChanges
                ? "Auto mode is enabled. The app will write these changes after parsing and path checks."
                : "Use Accept to write these changes to the connected codebase, or Reject to discard the proposal.");
            sb.AppendLine();

            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                sb.AppendLine($"## {patch.RelativePath} ({patch.Action})");
                sb.AppendLine();
                sb.AppendLine("````diff");
                sb.AppendLine($"--- {patch.RelativePath}");
                sb.AppendLine($"+++ {patch.RelativePath}");
                sb.AppendLine(BuildPatchFileDiff(patch));
                sb.AppendLine("````");
                sb.AppendLine();
            }

            return sb.ToString().TrimEnd();
        }

        private string BuildPatchFileDiff(WorkspaceFilePatch patch)
        {
            string original = string.Empty;
            try
            {
                string target = _workspaceAccessService.ResolvePatchTargetPath(_connectedWorkspace, patch);
                if (File.Exists(target))
                    original = File.ReadAllText(target);
            }
            catch (Exception ex)
            {
                return $"! Unable to read current file for diff: {ex.Message}";
            }

            IReadOnlyList<LineDiffEntry> diff = LineDiff.Build(original, patch.Content);
            if (diff.Count == 0)
                return "  No differences.";

            const int contextLines = 3;
            const int maxVisibleLines = 700;
            var visibleIndices = new HashSet<int>();

            for (int i = 0; i < diff.Count; i++)
            {
                if (diff[i].Kind == LineDiffKind.Unchanged)
                    continue;

                int start = Math.Max(0, i - contextLines);
                int end = Math.Min(diff.Count - 1, i + contextLines);
                for (int index = start; index <= end; index++)
                    visibleIndices.Add(index);
            }

            if (visibleIndices.Count == 0)
                return "  No differences.";

            var diffText = new StringBuilder();
            int previousVisibleIndex = -2;
            int rendered = 0;
            foreach (int index in visibleIndices.OrderBy(value => value))
            {
                if (rendered >= maxVisibleLines)
                {
                    diffText.AppendLine("... diff truncated for review ...");
                    break;
                }

                if (index > previousVisibleIndex + 1)
                    diffText.AppendLine("@@ ...");

                LineDiffEntry entry = diff[index];
                string prefix = entry.Kind switch
                {
                    LineDiffKind.Removed => "-",
                    LineDiffKind.Added => "+",
                    _ => " "
                };
                string text = entry.Text ?? string.Empty;
                if (text.Length > 1000)
                    text = text[..1000] + "...";
                diffText.AppendLine(prefix + text);
                previousVisibleIndex = index;
                rendered++;
            }

            return diffText.ToString().TrimEnd();
        }

        private void AcceptCodebaseChanges_Click(object sender, RoutedEventArgs e)
        {
            TryApplyPendingCodebaseChanges(automatic: false);
        }

        private bool TryApplyPendingCodebaseChanges(bool automatic)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Codebase Edit Access is not enabled in this Workplace chat.");
                return false;
            }

            if (!_hasPendingCodebaseChanges)
            {
                AppendChat("system", "No proposed codebase changes are waiting to accept.");
                return false;
            }

            if (_pendingCodebasePatch == null)
            {
                AppendChat("system", "No parsed codebase patch is available to apply.");
                return false;
            }

            try
            {
                WorkspacePatchProposal proposal = _pendingCodebasePatch;
                CodebasePatchValidationResult validation = ValidateCodebasePatchProposal(proposal);
                if (!validation.IsValid)
                {
                    if (automatic)
                    {
                        HandleCodebasePreApplyFailure(proposal, automatic, validation.Reasons, allowManualOverride: true);
                        return false;
                    }

                    AppendChat("warning", "Applying reviewed codebase patch despite validation warnings. Undo will be available.\n" +
                        string.Join("\n", validation.Reasons.Select(reason => "- " + reason)));
                }

                if (!TryBuildCodebaseUndoSnapshot(proposal, automatic, out CodebaseUndoSnapshot? undoSnapshot, out IReadOnlyList<string> snapshotReasons))
                {
                    HandleCodebasePreApplyFailure(proposal, automatic, snapshotReasons, allowManualOverride: false);
                    return false;
                }
                if (undoSnapshot == null)
                {
                    HandleCodebasePreApplyFailure(proposal, automatic, ["Could not capture undo state before applying the patch."], allowManualOverride: false);
                    return false;
                }

                WorkspaceGitStatus gitBefore = GetConnectedWorkspaceGitStatus();
                WorkspacePatchApplyResult result = _workspaceAccessService.ApplyPatchProposal(_connectedWorkspace, proposal);
                _lastCodebaseUndo = undoSnapshot with { ChangedFiles = result.ChangedFiles.ToList() };
                _pendingCodebasePatch = null;
                _hasPendingCodebaseChanges = false;
                RefreshConnectedWorkspaceIndexAfterPatch();
                WorkspaceGitStatus gitAfter = GetConnectedWorkspaceGitStatus();
                RenderCodebaseApplySummaryView(result, proposal, automatic, gitBefore, gitAfter, validation.Reasons);
                RefreshCodebaseAccessUi();
                AppendChat("system", BuildCodebaseApplySummary(result, proposal, automatic, gitBefore, gitAfter, validation.Reasons));
                SavePersistedSession();
                return true;
            }
            catch (Exception ex)
            {
                AppendChat("error", automatic
                    ? "Auto apply failed, so the patch is still pending for manual review: " + ex.Message
                    : "Accepting codebase changes failed: " + ex.Message);
                RefreshCodebaseAccessUi();
                SavePersistedSession();
                return false;
            }
        }

        private CodebasePatchValidationResult ValidateCodebasePatchProposal(WorkspacePatchProposal proposal)
        {
            var reasons = new List<string>();
            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                string extension = Path.GetExtension(patch.RelativePath).ToLowerInvariant();
                string content = patch.Content ?? string.Empty;
                switch (extension)
                {
                    case ".html":
                    case ".htm":
                        string originalHtml = string.Empty;
                        try
                        {
                            string target = _workspaceAccessService.ResolvePatchTargetPath(_connectedWorkspace, patch);
                            if (File.Exists(target))
                                originalHtml = File.ReadAllText(target);
                        }
                        catch
                        {
                            // Path resolution is reported separately by the undo snapshot preflight.
                        }
                        reasons.AddRange(ValidateHtmlPatchContent(patch.RelativePath, content, originalHtml, patch.Action));
                        break;
                    case ".json":
                    case ".jsonc":
                        string? jsonError = ValidateJsonPatchContent(patch.RelativePath, content, extension == ".jsonc");
                        if (!string.IsNullOrWhiteSpace(jsonError))
                            reasons.Add(jsonError);
                        break;
                    case ".css":
                    case ".scss":
                        string? cssError = ValidateCssBraceBalance(patch.RelativePath, content);
                        if (!string.IsNullOrWhiteSpace(cssError))
                            reasons.Add(cssError);
                        break;
                }
            }

            return reasons.Count == 0
                ? CodebasePatchValidationResult.Pass
                : new CodebasePatchValidationResult(false, reasons);
        }

        private static IEnumerable<string> ValidateHtmlPatchContent(string relativePath, string content, string originalContent, string action)
        {
            string trimmed = (content ?? string.Empty).Trim();
            if (trimmed.Length == 0)
            {
                yield return $"{relativePath}: HTML content is empty.";
                yield break;
            }

            string lower = trimmed.ToLowerInvariant();
            string originalLower = (originalContent ?? string.Empty).ToLowerInvariant();
            bool originalLooksLikeFullDocument = string.IsNullOrWhiteSpace(originalContent)
                || originalLower.Contains("<!doctype", StringComparison.Ordinal)
                || originalLower.Contains("<html", StringComparison.Ordinal)
                || originalLower.Contains("<body", StringComparison.Ordinal);
            bool replacementLooksLikeFullDocument = lower.Contains("<!doctype", StringComparison.Ordinal)
                || lower.Contains("<html", StringComparison.Ordinal)
                || lower.Contains("<body", StringComparison.Ordinal);
            bool requireFullDocumentTags = string.Equals(action, "create", StringComparison.OrdinalIgnoreCase)
                || originalLooksLikeFullDocument
                || replacementLooksLikeFullDocument;

            if (trimmed.Contains("[...file truncated for context budget]", StringComparison.OrdinalIgnoreCase)
                || trimmed.Contains("[...workspace context budget exhausted]", StringComparison.OrdinalIgnoreCase))
            {
                yield return $"{relativePath}: HTML replacement contains a workspace context truncation marker; regenerate the patch from the complete file.";
            }

            if (requireFullDocumentTags && !lower.Contains("<html", StringComparison.Ordinal))
                yield return $"{relativePath}: HTML replacement is missing an <html> tag.";
            if (requireFullDocumentTags && !lower.Contains("</html>", StringComparison.Ordinal))
                yield return $"{relativePath}: HTML replacement is missing a closing </html> tag.";
            if (requireFullDocumentTags && !lower.Contains("<body", StringComparison.Ordinal))
                yield return $"{relativePath}: HTML replacement is missing a <body> tag.";
            if (requireFullDocumentTags && !lower.Contains("</body>", StringComparison.Ordinal))
                yield return $"{relativePath}: HTML replacement is missing a closing </body> tag.";
            if (trimmed.EndsWith("<", StringComparison.Ordinal) || Regex.IsMatch(trimmed, @"<\s*/?\s*[A-Za-z][^>]*$"))
                yield return $"{relativePath}: HTML appears truncated at the final tag.";
            if (CountCaseInsensitive(lower, "<script") > CountCaseInsensitive(lower, "</script>"))
                yield return $"{relativePath}: HTML appears to have an unclosed <script> block.";
            if (CountCaseInsensitive(lower, "<style") > CountCaseInsensitive(lower, "</style>"))
                yield return $"{relativePath}: HTML appears to have an unclosed <style> block.";
        }

        private static string? ValidateJsonPatchContent(string relativePath, string content, bool allowJsonc)
        {
            try
            {
                var options = new JsonDocumentOptions
                {
                    AllowTrailingCommas = allowJsonc,
                    CommentHandling = allowJsonc ? JsonCommentHandling.Skip : JsonCommentHandling.Disallow
                };
                using JsonDocument _ = JsonDocument.Parse(content ?? string.Empty, options);
                return null;
            }
            catch (JsonException ex)
            {
                long line = (ex.LineNumber ?? 0) + 1;
                long bytePosition = (ex.BytePositionInLine ?? 0) + 1;
                return $"{relativePath}: JSON parse failed at line {line}, byte {bytePosition}: {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"{relativePath}: JSON parse failed: {ex.Message}";
            }
        }

        private static string? ValidateCssBraceBalance(string relativePath, string content)
        {
            int depth = 0;
            bool inSingleQuote = false;
            bool inDoubleQuote = false;
            bool inComment = false;
            bool escaped = false;

            string text = content ?? string.Empty;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                char next = i + 1 < text.Length ? text[i + 1] : '\0';

                if (inComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inComment = false;
                        i++;
                    }
                    continue;
                }

                if (!inSingleQuote && !inDoubleQuote && c == '/' && next == '*')
                {
                    inComment = true;
                    i++;
                    continue;
                }

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if ((inSingleQuote || inDoubleQuote) && c == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (!inDoubleQuote && c == '\'')
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (!inSingleQuote && c == '"')
                {
                    inDoubleQuote = !inDoubleQuote;
                    continue;
                }

                if (inSingleQuote || inDoubleQuote)
                    continue;

                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth < 0)
                        return $"{relativePath}: CSS has an extra closing brace.";
                }
            }

            if (inComment)
                return $"{relativePath}: CSS has an unclosed comment.";
            if (inSingleQuote || inDoubleQuote)
                return $"{relativePath}: CSS has an unclosed string.";
            return depth == 0 ? null : $"{relativePath}: CSS brace balance is off ({depth:n0} unclosed brace(s)).";
        }

        private static int CountCaseInsensitive(string value, string needle)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(needle))
                return 0;

            int count = 0;
            int index = 0;
            while ((index = value.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private bool TryBuildCodebaseUndoSnapshot(
            WorkspacePatchProposal proposal,
            bool automatic,
            out CodebaseUndoSnapshot? snapshot,
            out IReadOnlyList<string> reasons)
        {
            snapshot = null;
            var failures = new List<string>();
            var files = new List<CodebaseUndoFileSnapshot>();

            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                try
                {
                    string target = _workspaceAccessService.ResolvePatchTargetPath(_connectedWorkspace, patch);
                    bool exists = File.Exists(target);
                    if (patch.Action == "replace" && !exists)
                        failures.Add($"{patch.RelativePath}: cannot replace because the target file does not exist.");
                    if (patch.Action == "create" && exists)
                        failures.Add($"{patch.RelativePath}: cannot create because the target file already exists.");

                    string previousContent = exists ? File.ReadAllText(target) : string.Empty;
                    files.Add(new CodebaseUndoFileSnapshot(patch.RelativePath, target, exists, previousContent));
                }
                catch (Exception ex)
                {
                    failures.Add($"{patch.RelativePath}: path check failed: {ex.Message}");
                }
            }

            if (failures.Count > 0)
            {
                reasons = failures;
                return false;
            }

            snapshot = new CodebaseUndoSnapshot(
                files,
                proposal.Files.Select(file => file.RelativePath).ToList(),
                automatic,
                DateTime.Now);
            reasons = Array.Empty<string>();
            return true;
        }

        private void HandleCodebasePreApplyFailure(WorkspacePatchProposal proposal, bool automatic, IReadOnlyList<string> reasons, bool allowManualOverride)
        {
            _pendingCodebasePatch = proposal;
            _hasPendingCodebaseChanges = true;
            RenderCodebasePatchReview(proposal, autoMode: false);
            string reasonText = reasons.Count == 0
                ? "Pre-apply validation failed."
                : string.Join("\n", reasons.Select(reason => "- " + reason));
            CanvasTitleBlock.Text = "Codebase Patch Needs Review";
            CanvasSubtitleBlock.Text = "Pre-apply checks failed; no files changed.";
            AddCodebasePatchNoticeRow("Pre-apply checks failed. No files were changed.\n" + reasonText +
                (allowManualOverride
                    ? "\n\nAuto mode paused. Review the diff, then use Accept to apply anyway or Reject to discard it."
                    : "\n\nThe patch is still pending for review, but this issue must be corrected before it can be safely written."));
            AppendChat("warning", (automatic
                ? "Auto apply paused; the patch is still pending for manual review because pre-apply checks failed:\n"
                : "Codebase changes were not applied because pre-apply checks failed:\n") + reasonText);
            LogActivity("Codebase pre-apply validation failed: " + string.Join("; ", reasons));
            RefreshCodebaseAccessUi();
            SavePersistedSession();
        }

        private WorkspaceGitStatus GetConnectedWorkspaceGitStatus()
        {
            if (string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath))
                return new WorkspaceGitStatus(false, string.Empty, 0, string.Empty, string.Empty);

            return _workspaceAccessService.GetGitStatus(_connectedWorkspace.RootPath);
        }

        private static string BuildCodebaseApplySummary(
            WorkspacePatchApplyResult result,
            WorkspacePatchProposal proposal,
            bool automatic,
            WorkspaceGitStatus gitBefore,
            WorkspaceGitStatus gitAfter,
            IReadOnlyList<string> validationWarnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine(automatic ? "Auto applied codebase changes." : result.Summary);
            sb.AppendLine();
            sb.AppendLine("Changed files:");
            foreach (string file in result.ChangedFiles)
                sb.AppendLine("- " + file);

            if (validationWarnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Applied after manual validation override:");
                foreach (string warning in validationWarnings)
                    sb.AppendLine("- " + warning);
            }

            string gitLine = BuildGitStatusTransition(gitBefore, gitAfter);
            if (!string.IsNullOrWhiteSpace(gitLine))
            {
                sb.AppendLine();
                sb.AppendLine(gitLine);
                string statusPreview = BuildGitStatusPreview(gitAfter);
                if (!string.IsNullOrWhiteSpace(statusPreview))
                    sb.AppendLine(statusPreview);
            }

            if (automatic && proposal.Files.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Auto mode remains enabled. Turn it off in Connected Workspace to return to manual review.");
            }

            return sb.ToString().TrimEnd();
        }

        private void RenderCodebaseApplySummaryView(
            WorkspacePatchApplyResult result,
            WorkspacePatchProposal proposal,
            bool automatic,
            WorkspaceGitStatus gitBefore,
            WorkspaceGitStatus gitAfter,
            IReadOnlyList<string> validationWarnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Codebase Changes Applied");
            sb.AppendLine();
            sb.AppendLine(validationWarnings.Count > 0
                ? "Accepted patch applied after manual review despite validation warnings."
                : automatic ? "Auto mode applied the patch after all pre-apply checks passed." : "Accepted patch applied after all pre-apply checks passed.");
            sb.AppendLine();
            sb.AppendLine("## Changed Files");
            foreach (string file in result.ChangedFiles)
                sb.AppendLine("- " + file);

            if (validationWarnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("## Validation Warnings");
                foreach (string warning in validationWarnings)
                    sb.AppendLine("- " + warning);
            }

            string gitLine = BuildGitStatusTransition(gitBefore, gitAfter);
            if (!string.IsNullOrWhiteSpace(gitLine))
            {
                sb.AppendLine();
                sb.AppendLine("## Git Checkpoint");
                sb.AppendLine(gitLine);
                string statusPreview = BuildGitStatusPreview(gitAfter);
                if (!string.IsNullOrWhiteSpace(statusPreview))
                {
                    sb.AppendLine();
                    sb.AppendLine("```text");
                    sb.AppendLine(statusPreview);
                    sb.AppendLine("```");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Undo is available for this last applied patch.");

            ExitCanvasDiffView();
            ClearCanvasDiff();
            ProjectCanvasEditor.Text = sb.ToString().TrimEnd();
            _canvasArtifact = ArtifactRenderInfo.None(ProjectCanvasEditor.Text);
            _isCanvasPreviewMode = false;
            SetCanvasHighlighting("markdown");
            RefreshCanvasArtifactUi();
            CanvasTitleBlock.Text = automatic ? "Codebase Patch Applied" : "Codebase Patch Applied";
            CanvasSubtitleBlock.Text = $"{result.ChangedFiles.Count:n0} file change(s) applied. Undo is available.";
            UpdateWorkplaceTokenUsageIndicator();
        }

        private static string BuildGitStatusTransition(WorkspaceGitStatus before, WorkspaceGitStatus after)
        {
            if (!before.IsRepository && !after.IsRepository)
                return string.Empty;

            WorkspaceGitStatus status = after.IsRepository ? after : before;
            string branch = string.IsNullOrWhiteSpace(status.Branch) ? "detached HEAD" : status.Branch;
            string beforeCount = before.IsRepository ? before.ChangedFileCount.ToString("n0") : "unknown";
            string afterCount = after.IsRepository ? after.ChangedFileCount.ToString("n0") : "unknown";
            string suffix = string.IsNullOrWhiteSpace(after.Error) ? string.Empty : $" ({after.Error})";
            return $"Git checkpoint: branch {branch}, changed files {beforeCount} -> {afterCount}{suffix}.";
        }

        private static string BuildGitStatusPreview(WorkspaceGitStatus status)
        {
            if (!status.IsRepository || string.IsNullOrWhiteSpace(status.ShortStatus))
                return string.Empty;

            string[] lines = status.ShortStatus
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Take(8)
                .ToArray();
            if (lines.Length == 0)
                return string.Empty;

            string preview = "Git status:\n" + string.Join("\n", lines.Select(line => "- " + line.TrimEnd()));
            int total = status.ShortStatus.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;
            if (total > lines.Length)
                preview += $"\n- ...and {total - lines.Length:n0} more";
            return preview;
        }

        private void UndoCodebaseChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_lastCodebaseUndo == null)
            {
                AppendChat("system", "No applied codebase patch is available to undo.");
                return;
            }

            try
            {
                CodebaseUndoSnapshot undo = _lastCodebaseUndo;
                foreach (CodebaseUndoFileSnapshot file in undo.Files)
                {
                    string target = Path.GetFullPath(file.TargetPath);
                    if (!string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath)
                        && Directory.Exists(_connectedWorkspace.RootPath)
                        && !_workspaceAccessService.IsPathInsideWorkspace(_connectedWorkspace.RootPath, target))
                    {
                        throw new InvalidOperationException($"Undo target is outside the connected workspace: {file.RelativePath}");
                    }

                    if (file.Existed)
                    {
                        string? directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrWhiteSpace(directory))
                            Directory.CreateDirectory(directory);
                        AtomicFileWriter.WriteAllText(target, file.PreviousContent ?? string.Empty);
                    }
                    else if (File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }

                _lastCodebaseUndo = null;
                RefreshConnectedWorkspaceIndexAfterPatch();
                RenderCodebaseUndoSummaryView(undo);
                RefreshCodebaseAccessUi();
                AppendChat("system", "Undid the last applied codebase patch:\n- " + string.Join("\n- ", undo.ChangedFiles));
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                AppendChat("error", "Undo failed: " + ex.Message);
                RefreshCodebaseAccessUi();
            }
        }

        private void RenderCodebaseUndoSummaryView(CodebaseUndoSnapshot undo)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Codebase Patch Undone");
            sb.AppendLine();
            sb.AppendLine($"Restored previous file contents from {undo.AppliedAt:HH:mm}.");
            sb.AppendLine();
            sb.AppendLine("## Restored Files");
            foreach (string file in undo.ChangedFiles)
                sb.AppendLine("- " + file);

            ExitCanvasDiffView();
            ClearCanvasDiff();
            ProjectCanvasEditor.Text = sb.ToString().TrimEnd();
            _canvasArtifact = ArtifactRenderInfo.None(ProjectCanvasEditor.Text);
            _isCanvasPreviewMode = false;
            SetCanvasHighlighting("markdown");
            RefreshCanvasArtifactUi();
            CanvasTitleBlock.Text = "Codebase Patch Undone";
            CanvasSubtitleBlock.Text = $"{undo.ChangedFiles.Count:n0} file(s) restored.";
            UpdateWorkplaceTokenUsageIndicator();
        }

        private void RejectCodebaseChanges_Click(object sender, RoutedEventArgs e)
        {
            if (!_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Codebase Edit Access is not enabled in this Workplace chat.");
                return;
            }

            if (!_hasPendingCodebaseChanges)
            {
                AppendChat("system", "No proposed codebase changes are waiting to reject.");
                return;
            }

            _pendingCodebasePatch = null;
            _hasPendingCodebaseChanges = false;
            _lastCodebaseUndo = null;
            RefreshCodebaseAccessUi();
            AppendChat("system", "Proposed codebase changes rejected.");
            SavePersistedSession();
        }

        private void RefreshConnectedWorkspaceIndexAfterPatch()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_connectedWorkspace.RootPath)
                    && Directory.Exists(_connectedWorkspace.RootPath))
                {
                    WorkspaceIndexResult index = _workspaceAccessService.IndexWorkspace(_connectedWorkspace.RootPath);
                    _connectedWorkspace.DisplayName = index.DisplayName;
                    _connectedWorkspace.IndexedFileCount = index.Files.Count;
                    _connectedWorkspace.IndexedByteCount = index.TotalBytes;
                    _connectedWorkspace.IndexedAt = DateTime.Now;
                    _connectedWorkspace.StatusMessage = $"Applied patch and re-indexed {index.Files.Count:n0} files at {_connectedWorkspace.IndexedAt:HH:mm}.";
                }
                else if (_connectedWorkspace.ConnectedFiles.Count > 0)
                {
                    WorkspaceIndexResult index = _workspaceAccessService.IndexFiles(_connectedWorkspace.ConnectedFiles);
                    _connectedWorkspace.DisplayName = index.DisplayName;
                    _connectedWorkspace.IndexedFileCount = index.Files.Count;
                    _connectedWorkspace.IndexedByteCount = index.TotalBytes;
                    _connectedWorkspace.IndexedAt = DateTime.Now;
                    _connectedWorkspace.StatusMessage = $"Applied patch and re-indexed {index.Files.Count:n0} connected file(s) at {_connectedWorkspace.IndexedAt:HH:mm}.";
                }
            }
            catch (Exception ex)
            {
                _connectedWorkspace.StatusMessage = "Patch applied, but re-indexing failed: " + ex.Message;
            }
        }

        private async void WorkplaceCloudModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                AppendChat("system", "Cannot switch workplace cloud mode during an active council run.");
                return;
            }

            if (_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "Cloud/local mode is locked because Codebase Edit Access is enabled. Start a new Workplace chat to change it.");
                RefreshWorkplaceCloudModeUi();
                return;
            }

            if (_isCloudModeEnabled)
            {
                _isCloudModeEnabled = false;
                RefreshWorkplaceCloudModeUi();
                UpdateCouncilBlocks();
                UpdateContextInfo();
                AppendChat("system", "Workplace cloud mode disabled. Council returned to local models.");
                SavePersistedSession();
                return;
            }

            LoadOpenRouterKeyForWorkplace();
            if (!_openRouterChatService.HasValidKey)
            {
                AppendChat("error", "Workplace cloud mode needs a valid OpenRouter API key in Settings.");
                return;
            }

            double sidebarOffset = CouncilSidebarScrollViewer?.VerticalOffset ?? 0;
            if (FindName("WorkplaceCloudModeButton") is Button cloudModeButton)
            {
                cloudModeButton.IsEnabled = false;
                cloudModeButton.Content = "Validating";
                cloudModeButton.Opacity = 1.0;
                cloudModeButton.ToolTip = "Validating workplace cloud mode...";
            }
            RelayStatusBlock.Text = "Relay: Validating cloud council...";
            try
            {
                bool available = await _openRouterChatService.ValidateModelAvailabilityAsync(OpenRouterChatService.WorkplaceCouncilDefaultModelId);
                if (!available && _openRouterChatService.LastTestFailureReason != OpenRouterConnectionTestFailureReason.ProviderUnavailable)
                {
                    AppendChat("error", BuildCloudModeErrorMessage(_openRouterChatService.LastTestFailureReason));
                    return;
                }

                _isCloudModeEnabled = true;
                RefreshWorkplaceCloudModeUi();
                UpdateCouncilBlocks();
                UpdateContextInfo();
                AppendChat("system", available
                    ? $"Workplace cloud mode enabled. Architect, Builder, and Critic now use {OpenRouterChatService.WorkplaceCouncilDisplayLabel}."
                    : $"Workplace cloud mode enabled. {OpenRouterChatService.WorkplaceCouncilDisplayLabel} is currently unstable, so cloud runs will automatically retry and fall back if OpenRouter providers are temporarily unavailable.");
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                AppendChat("error", BuildCloudModeErrorMessage(ResolveCloudFailureReason(ex)));
            }
            finally
            {
                if (FindName("WorkplaceCloudModeButton") is Button cloudModeButtonFinal)
                    cloudModeButtonFinal.IsEnabled = true;
                RefreshWorkplaceCloudModeUi();
                if (CouncilSidebarScrollViewer != null)
                {
                    _ = Dispatcher.InvokeAsync(
                        () => CouncilSidebarScrollViewer.ScrollToVerticalOffset(sidebarOffset),
                        DispatcherPriority.Background);
                }
                RelayStatusBlock.Text = "Relay: Idle";
            }
        }

        private async Task<string> ExecuteWebSearchAsync(string query, CancellationToken token)
        {
            if (!_isWebSearchEnabled)
                return "Web search is disabled by user.";

            try
            {
                string displayQuery = (query ?? string.Empty).Trim();
                if (displayQuery.Length > 72)
                    displayQuery = displayQuery[..72] + "...";

                if (ShouldEmitWebStatus())
                {
                    await Dispatcher.InvokeAsync(() =>
                        AppendChat("memory", string.IsNullOrWhiteSpace(displayQuery)
                            ? "🌐 Web search active (tool enabled)."
                            : $"🌐 Web search active (tool enabled): {displayQuery}"),
                        DispatcherPriority.Background);
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    RelayStatusBlock.Text = "Relay: Web search in progress...";
                    HardwareInfoBlock.Text = "Runtime: web tool engaged by council";
                }, DispatcherPriority.Background);

                string rawQuery = ConversationSearchContext.BuildContextualSearchPrompt(
                    (query ?? string.Empty).Trim(),
                    BuildCouncilSearchContextTurns(query ?? string.Empty));
                if (string.IsNullOrWhiteSpace(rawQuery))
                    rawQuery = (query ?? string.Empty).Trim();

                if (WebSearchService.LooksLikeLowSpecificitySearchQuery(rawQuery)
                    && !string.IsNullOrWhiteSpace(_activeCouncilWebPrompt))
                {
                    rawQuery = (_activeCouncilWebPrompt + " " + rawQuery).Trim();
                    LogActivity("Web search query expanded with active council prompt for specificity.");
                }

                string searchQuery = _webSearchService.BuildStrategicSearchQuery(rawQuery);
                if (string.IsNullOrWhiteSpace(searchQuery))
                    searchQuery = rawQuery;

                LogActivity($"Web search query: {(searchQuery.Length > 96 ? searchQuery[..96] + "..." : searchQuery)}");
                string data = await _webSearchService.SearchTopSnippetsAsync(searchQuery, token);
                if (string.IsNullOrWhiteSpace(data)
                    || data.Contains("No web results", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("Web search unavailable", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    await BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:WorkplaceWebSearch\nQuery:{searchQuery}\nStatus:No usable results");
                }

                if (string.IsNullOrWhiteSpace(data)
                    || data.Contains("No web results", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("Web search unavailable", StringComparison.OrdinalIgnoreCase)
                    || data.Contains("timed out", StringComparison.OrdinalIgnoreCase))
                {
                    LogActivity("Web search returned no usable results.");
                    if (ShouldEmitWebStatus())
                    {
                        await Dispatcher.InvokeAsync(() => AppendChat("memory", "🌐 Web search returned no usable results."), DispatcherPriority.Background);
                    }
                    return "Web search returned no usable results.";
                }

                int promptContextLimit = CanUseCloudCouncil ? 12000 : 4200;
                if (data.Length > promptContextLimit)
                    data = _webSearchService.PreparePromptContext(data, promptContextLimit);

                LogActivity("Web search data fetched.");
                if (ShouldEmitWebStatus())
                {
                    await Dispatcher.InvokeAsync(() => AppendChat("memory", "🌐 Web search data fetched and injected into council context."), DispatcherPriority.Background);
                }
                return data;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                await BackendLogService.LogEventAsync("ToolFailReadOnly", $"Tool:WorkplaceWebSearch\nQuery:{query}\nError:{ex.Message}");
                LogActivity("Web search unavailable right now.");
                if (ShouldEmitWebStatus())
                {
                    await Dispatcher.InvokeAsync(() => AppendChat("memory", "🌐 Web search unavailable right now."), DispatcherPriority.Background);
                }
                return "Web search unavailable right now.";
            }
        }

        private async Task<string> ExecutePythonMathAsync(string code, CancellationToken token)
        {
            var result = await _pythonExecutionService.ExecuteMathScriptAsync(code, _activePythonSandboxPreamble, 10000, token).ConfigureAwait(false);
            return AppendUnitToNumericOutput(result.Output, (_lastRunContext?.UserPrompt ?? string.Empty) + "\n" + (_lastRunContext?.Objective ?? string.Empty));
        }

        private void QueryInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != ModifierKeys.Shift)
            {
                e.Handled = true;
                if (!SendButton.IsEnabled || _isProcessing)
                    return;

                _ = Dispatcher.BeginInvoke(async () =>
                {
                    try
                    {
                        await SendQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        await BackendLogService.LogErrorAsync("Workplace.SendFromKeyDown", ex);
                        AppendChat("error", ex.Message);
                    }
                }, DispatcherPriority.Background);
            }
        }

        // The Objective input row was removed from the UI; the pipeline plumbing that consumed
        // it (run context, prompts, history entries) still accepts an objective string, so this
        // stub keeps every call site working with "no objective".
        private static string ReadObjectiveText() => string.Empty;

        private static CouncilTaskType DetectTaskType(string userQuery, string objective, bool? knownArtifactCanvasIntent = null)
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            bool artifactCanvasIntent = knownArtifactCanvasIntent ?? DetectArtifactCanvasIntent(userQuery, objective);

            // Strong coding signals — phrases that unambiguously mean "write code"
            string[] strongCoding =
            [
                "write code", "write a script", "write a program", "write a function",
                "create a script", "create a program", "create a function",
                "implement a function", "implement a class", "implement a method",
                "code that", "code to", "code for", "code which",
                "python script", "python program", "python code",
                "javascript code", "c# code", "java code", "html page",
                "debug this", "debug the", "refactor this", "refactor the",
                "fix this code", "fix the code", "fix the bug",
                "compile", "executable", "source code", "codebase",
                "def ", "class ", "function(", "void ", "int ", "public static",
                "console.writeline", "print(", "system.out", "#include",
                "import os", "import sys", "import json"
            ];

            // Research signals — knowledge synthesis, explanation, study
            string[] research =
            [
                "research", "summarize", "literature", "explain", "overview",
                "study", "report", "essay", "paper", "article",
                "describe", "what is", "what are", "how does", "how do",
                "tell me about", "teach me", "history of", "background on",
                "learn", "learned", "what did you learn", "what have you learned",
                "define", "definition", "meaning of", "concept of",
                "write about", "write a report", "write a summary", "write an essay",
                "write a paper", "write a study", "create a report", "create a study",
                "create a summary", "pros and cons", "advantages and disadvantages",
                "in-depth", "comprehensive", "thorough", "detailed explanation"
            ];

            // Analysis signals — evaluation, comparison, data interpretation
            string[] analysis =
            [
                "analyze", "analyse", "compare", "evaluate", "interpret",
                "breakdown", "break down", "assessment", "critique",
                "strengths and weaknesses", "swot", "trade-off", "tradeoff",
                "correlat", "trend", "metric", "benchmark",
                "root cause", "cause and effect", "impact of",
                "what went wrong", "why did", "difference between"
            ];

            // Check research and analysis FIRST — they are the most commonly mis-classified
            bool hasResearch = research.Any(k => combined.Contains(k));
            bool hasAnalysis = analysis.Any(k => combined.Contains(k));
            bool hasStrongCoding = strongCoding.Any(k => combined.Contains(k));
            bool hasExplicitCodingImplementation = IsExplicitCodingImplementationRequest(userQuery, objective);

            if (hasExplicitCodingImplementation)
                return CouncilTaskType.Coding;

            if (artifactCanvasIntent && !hasResearch && !hasAnalysis)
                return CouncilTaskType.Coding;

            // Strong coding signals override everything — these are unambiguous
            if (hasStrongCoding && !hasResearch && !hasAnalysis)
                return CouncilTaskType.Coding;

            // If both coding and research/analysis signals exist, research/analysis wins
            // (e.g., "research about building rockets" should NOT be coding)
            if (hasResearch) return CouncilTaskType.Research;
            if (hasAnalysis) return CouncilTaskType.Analysis;
            if (hasStrongCoding) return CouncilTaskType.Coding;

            return CouncilTaskType.General;
        }

        private static bool IsExplicitCodingImplementationRequest(string userQuery, string objective = "")
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(combined))
                return false;

            bool namesCodeArtifact = Regex.IsMatch(combined, @"(?<!\w)(?:c#|csharp|\.cs|csproj|xunit|nunit|mstest|unit test|unit tests|executable tests|compilable|compile|source file|source code|class file|implementation file)(?!\w)", RegexOptions.IgnoreCase)
                || Regex.IsMatch(combined, @"\b(?:python|javascript|typescript|java|html|css|sql|powershell|bash)\s+(?:file|script|program|code|implementation|tests?)\b", RegexOptions.IgnoreCase);
            bool asksToImplement = Regex.IsMatch(combined, @"\b(?:implement|write|create|build|generate|produce|code|program|develop)\b", RegexOptions.IgnoreCase);
            bool asksForTests = Regex.IsMatch(combined, @"\b(?:test|tests|unit tests|executable tests|passing tests)\b", RegexOptions.IgnoreCase);

            if (namesCodeArtifact && (asksToImplement || asksForTests))
                return true;

            return Regex.IsMatch(combined, @"\b(?:write|create|build|generate|produce)\s+(?:a\s+|an\s+|the\s+)?(?:compilable\s+)?(?:c#|csharp|\.cs|source|code|program|class|implementation)(?!\w)", RegexOptions.IgnoreCase)
                || Regex.IsMatch(combined, @"\b(?:implement|code)\s+(?:this|the|a|an)\b", RegexOptions.IgnoreCase);
        }

        private static bool LooksLikeWorkspaceCodingRequest(string userQuery, string objective = "")
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(combined))
                return false;

            return Regex.IsMatch(combined, @"\b(?:fix|debug|repair|refactor|implement|add|update|change|modify|wire|connect|remove|rename|test|tests|build|compile|review)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(combined, @"\b(?:bug|error|exception|failing|failure|codebase|repo|repository|project|solution|class|method|component|view|service|controller|xaml|csproj)\b", RegexOptions.IgnoreCase)
                || Regex.IsMatch(combined, @"\.(?:cs|xaml|csproj|slnx|py|js|ts|tsx|jsx|json|css|html|htm)\b", RegexOptions.IgnoreCase);
        }

        private static bool DetectArtifactCanvasIntent(string userQuery, string objective)
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(combined))
                return false;

            if (HasProjectCanvasManualTrigger(userQuery, objective))
                return true;

            // Explicit canvas mention is an unambiguous override
            if (combined.Contains("project canvas", StringComparison.Ordinal)
                || combined.Contains("canvas artifact", StringComparison.Ordinal)
                || combined.Contains("for canvas", StringComparison.Ordinal)
                || combined.Contains("on canvas", StringComparison.Ordinal))
                return true;

            // Direct visualization verbs are self-evidently artifact requests
            if (combined.Contains("visualize ", StringComparison.Ordinal)
                || combined.Contains("visualise ", StringComparison.Ordinal))
                return true;

            string[] creationPhrases =
            [
                "make me", "make a", "create", "build", "generate", "render",
                "show me", "show a", "show the",
                "design", "produce", "give me", "give me a", "craft", "construct",
                "draw", "draw me", "write me", "can you make", "generate me",
                "put together", "display a", "display the",
                "i need a", "need a", "want a"
            ];

            // Strong artifact targets are self-evidently visual or interactive — a creation phrase alone is sufficient
            string[] strongArtifactTargets =
            [
                "chart", "graph", "dashboard", "visualization", "visualisation",
                "diagram", "svg", "html", "canvas", "web page", "webpage",
                "login screen", "signup screen", "sign up screen", "login form",
                "signup form", "sign up form", "landing page", "settings screen",
                "profile screen", "admin panel", "control panel", "modal", "dialog",
                "navbar", "navigation", "sidebar", "menu",
                "coordinate plane", "coordinate graph", "plotter",
                "datasheet", "data sheet", "spreadsheet", "spread sheet",
                "infographic", "info graphic", "heatmap", "heat map",
                "timeline", "time line", "flowchart", "flow chart",
                "wireframe", "wire frame", "calculator", "animation",
                "prototype", "mockup", "widget", "calendar", "tracker",
                "mind map", "site map", "road map", "treemap", "plot"
            ];

            // Weak artifact targets need an additional implementation hint or strong visual signal
            string[] weakArtifactTargets =
            [
                "artifact", "preview", "component", "ui", "interface", "screen",
                "page", "layout", "form", "table", "app", "template",
                "grid", "matrix", "schedule", "report"
            ];

            string[] implementationHints =
            [
                "with html", "in html", "as html", "using html", "html document", "html page",
                "with svg", "in svg", "as svg", "using svg",
                "with javascript", "in javascript", "using javascript", "with js",
                "webview", "self-contained", "offline", "interactive",
                "x and y", "x & y", "x/y", "coordinates", "coordinate", "input", "inputs",
                "visual", "visually", "render", "rendered", "display", "displays"
            ];

            bool hasCreationPhrase = creationPhrases.Any(phrase => combined.Contains(phrase, StringComparison.Ordinal));
            bool hasStrongTarget = strongArtifactTargets.Any(target => combined.Contains(target, StringComparison.Ordinal));
            bool hasWeakTarget = weakArtifactTargets.Any(target => combined.Contains(target, StringComparison.Ordinal));
            bool hasImplementationHint = implementationHints.Any(hint => combined.Contains(hint, StringComparison.Ordinal));
            bool hasStrongVisualDeliverable = combined.Contains("graph", StringComparison.Ordinal)
                || combined.Contains("chart", StringComparison.Ordinal)
                || combined.Contains("dashboard", StringComparison.Ordinal)
                || combined.Contains("visualization", StringComparison.Ordinal)
                || combined.Contains("visualisation", StringComparison.Ordinal)
                || combined.Contains("diagram", StringComparison.Ordinal)
                || combined.Contains("svg", StringComparison.Ordinal)
                || combined.Contains("html", StringComparison.Ordinal);

            bool hasInteractivePlotIntent = (combined.Contains("plot", StringComparison.Ordinal)
                    || combined.Contains("graph", StringComparison.Ordinal)
                    || combined.Contains("chart", StringComparison.Ordinal))
                && (combined.Contains("input", StringComparison.Ordinal)
                    || combined.Contains("interactive", StringComparison.Ordinal)
                    || combined.Contains("coordinates", StringComparison.Ordinal)
                    || combined.Contains("coordinate", StringComparison.Ordinal)
                    || combined.Contains("x and y", StringComparison.Ordinal)
                    || combined.Contains("x & y", StringComparison.Ordinal)
                    || combined.Contains("x/y", StringComparison.Ordinal));

            // Creation phrase + strong target — strong targets are self-evidently visual
            if (hasCreationPhrase && hasStrongTarget)
                return true;

            // Creation phrase + weak target — requires an additional qualifying signal
            if (hasCreationPhrase && hasWeakTarget && (hasImplementationHint || hasStrongVisualDeliverable))
                return true;

            // Interactive plot intent
            if (hasCreationPhrase && hasInteractivePlotIntent)
                return true;

            // Triple-signal: artifact + implementation hint + strong visual (no creation phrase needed)
            bool hasAnyTarget = hasStrongTarget || hasWeakTarget;
            if (hasAnyTarget && hasImplementationHint && hasStrongVisualDeliverable)
                return true;

            return false;
        }

        /// <summary>
        /// Measures the usable pixel area of the Project Canvas artifact viewport (the editor /
        /// WebView2 host inside CodeOutputPanel) so prompt guidance can quote real numbers.
        /// Falls back to an expanded-pane estimate when the pane is collapsed or not yet laid
        /// out, since the pane auto-expands when a renderable artifact arrives.
        /// </summary>
        private (int Width, int Height) GetCanvasArtifactViewportSize()
        {
            double width = 0, height = 0;
            if (FindName("CodeOutputPanel") is Border panel && panel.ActualWidth > 0)
            {
                width = panel.ActualWidth - 2;   // 1px border on each side
                height = panel.ActualHeight - 2;
            }

            if (width < 100)
            {
                // Pane collapsed or not laid out yet — estimate from the expanded-pane sizing
                // rules (window-width ratio clamped to the pane's XAML Min/MaxWidth), minus the
                // 16px pane padding and 1px border on each side.
                double viewport = ActualWidth > 0 ? ActualWidth : 1600;
                double pane = Math.Clamp(viewport * ProjectCanvasExpandedWidthRatio, 340, 760);
                width = pane - 34;
            }
            if (height < 100)
                height = Math.Max(420, (ActualHeight > 0 ? ActualHeight : 900) - 320);

            return ((int)width, (int)height);
        }

        private static string GetPreferredArtifactFormatHint(string userQuery, string objective, int canvasWidth)
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();

            // Shared sizing constraint appended to every hint, quoting the live-measured viewport.
            string sizingRule =
                $" SIZING: The canvas viewport is currently {canvasWidth}px wide and user-resizable. " +
                "Use width:100% and box-sizing:border-box on all containers. " +
                $"Never use fixed pixel widths wider than {Math.Max(300, canvasWidth - 24)}px on outer layout elements. " +
                "Ensure the artifact fills the available width without horizontal scrolling.";

            bool explicitSvgRequest = combined.Contains("svg", StringComparison.Ordinal);
            bool dynamicVisualRequest =
                combined.Contains("simulate", StringComparison.Ordinal)
                || combined.Contains("simulation", StringComparison.Ordinal)
                || combined.Contains("animated", StringComparison.Ordinal)
                || combined.Contains("animation", StringComparison.Ordinal)
                || combined.Contains("sun cycle", StringComparison.Ordinal)
                || combined.Contains("time of day", StringComparison.Ordinal)
                || combined.Contains("slider", StringComparison.Ordinal)
                || combined.Contains("playback", StringComparison.Ordinal)
                || combined.Contains("interactive", StringComparison.Ordinal)
                || combined.Contains("input", StringComparison.Ordinal)
                || combined.Contains("calculate", StringComparison.Ordinal)
                || combined.Contains("calculator", StringComparison.Ordinal)
                || combined.Contains("energy", StringComparison.Ordinal)
                || combined.Contains("generation", StringComparison.Ordinal);

            if (!explicitSvgRequest && dynamicVisualRequest)
            {
                return "Output a complete self-contained HTML document with inline CSS and inline JavaScript. " +
                       "This is a dynamic visual artifact, so do NOT use standalone SVG as the top-level artifact. " +
                       "Use native browser APIs such as HTML elements, CSS, JavaScript, and optionally HTML5 Canvas for the scene/plot. " +
                       "Implement the requested simulation/calculation in JavaScript with visible controls or animated state, numeric readouts, labels, and a clear visual relationship between inputs and output. " +
                       "Do NOT import external libraries, fonts, CSS, icons, or JavaScript via CDN. Must render fully offline in WebView2. " +
                       "For HTML5 Canvas: initialize canvas.width and canvas.height from the container's clientWidth in JavaScript, not hardcoded numbers." + sizingRule;
            }

            if (!combined.Contains("svg", StringComparison.Ordinal)
                && (combined.Contains("login", StringComparison.Ordinal)
                    || combined.Contains("signup", StringComparison.Ordinal)
                    || combined.Contains("sign up", StringComparison.Ordinal)
                    || combined.Contains("screen", StringComparison.Ordinal)
                    || combined.Contains("page", StringComparison.Ordinal)
                    || combined.Contains("layout", StringComparison.Ordinal)
                    || combined.Contains("ui", StringComparison.Ordinal)
                    || combined.Contains("interface", StringComparison.Ordinal)
                    || combined.Contains("modal", StringComparison.Ordinal)
                    || combined.Contains("dialog", StringComparison.Ordinal)
                    || combined.Contains("navbar", StringComparison.Ordinal)
                    || combined.Contains("navigation", StringComparison.Ordinal)
                    || combined.Contains("sidebar", StringComparison.Ordinal)
                    || combined.Contains("admin panel", StringComparison.Ordinal)
                    || combined.Contains("control panel", StringComparison.Ordinal)))
            {
                return "Output a complete self-contained HTML document with inline CSS and inline JavaScript only when behavior is needed. " +
                       "This is a UI/screen artifact, so prefer HTML over standalone SVG. SVG may be used only inside the HTML for icons or illustrations. " +
                       "Include all visible states the user requested and make the first viewport look like a finished app screen, not a diagram. " +
                       "Do NOT import external fonts, CSS, icons, or JavaScript via CDN. Must render fully offline in WebView2." + sizingRule;
            }

            if (explicitSvgRequest
                || combined.Contains("diagram", StringComparison.Ordinal)
                || combined.Contains("vector", StringComparison.Ordinal)
                || combined.Contains("flowchart", StringComparison.Ordinal)
                || combined.Contains("flow chart", StringComparison.Ordinal))
            {
                return "Prefer inline SVG for this artifact unless the request explicitly needs richer HTML interaction. " +
                       "SVG SIZING: always include a viewBox attribute on the root <svg> element; " +
                       "set width='100%' and omit any fixed height attribute so the SVG scales to fit the canvas." + sizingRule;
            }

            if (combined.Contains("python", StringComparison.Ordinal)
                || combined.Contains("matplotlib", StringComparison.Ordinal)
                || combined.Contains("plotly", StringComparison.Ordinal))
            {
                return "Prefer Python code that produces a chart artifact through the sandbox capture flow. " +
                       $"Set figure size to fit a ~{canvasWidth}px wide display: use figsize=({Math.Max(5.0, canvasWidth / 90.0):0.#}, 4) for matplotlib or equivalent. " +
                       "Use tight_layout() or bbox_inches='tight' to avoid clipping. " +
                       "Use font sizes of at least 10pt for axis labels, tick labels, and legends so text stays legible at display size.";
            }

            if (combined.Contains("datasheet", StringComparison.Ordinal)
                || combined.Contains("data sheet", StringComparison.Ordinal)
                || combined.Contains("spreadsheet", StringComparison.Ordinal)
                || combined.Contains("schedule", StringComparison.Ordinal)
                || combined.Contains("calendar", StringComparison.Ordinal)
                || combined.Contains("tracker", StringComparison.Ordinal)
                || combined.Contains("report", StringComparison.Ordinal)
                || combined.Contains("template", StringComparison.Ordinal))
            {
                return "Output a complete self-contained HTML document. " +
                       "Use an HTML <table> with inline CSS for styled rows and columns — do NOT output a plain Markdown table. " +
                       "Set table { width:100%; border-collapse:collapse; } and use word-break:break-word on cells. " +
                       "Do NOT import any external library, font, or stylesheet via a CDN link. " +
                       "The HTML must render correctly offline in WebView2 with no network requests." + sizingRule;
            }

            if (combined.Contains("mind map", StringComparison.Ordinal)
                || combined.Contains("site map", StringComparison.Ordinal)
                || combined.Contains("road map", StringComparison.Ordinal)
                || combined.Contains("treemap", StringComparison.Ordinal)
                || combined.Contains("heatmap", StringComparison.Ordinal)
                || combined.Contains("heat map", StringComparison.Ordinal)
                || combined.Contains("infographic", StringComparison.Ordinal)
                || combined.Contains("info graphic", StringComparison.Ordinal)
                || combined.Contains("animation", StringComparison.Ordinal)
                || combined.Contains("wireframe", StringComparison.Ordinal))
            {
                return "Prefer a complete self-contained HTML document with inline CSS/JS and inline SVG where appropriate. " +
                       "Do NOT use external CDN libraries. Must render fully offline in WebView2 with no network requests. " +
                       "SVG elements inside HTML: include viewBox, set width='100%', omit fixed height attributes." + sizingRule;
            }

            if (combined.Contains("calculator", StringComparison.Ordinal)
                || combined.Contains("interactive tool", StringComparison.Ordinal)
                || combined.Contains("interactive app", StringComparison.Ordinal)
                || combined.Contains("interactive form", StringComparison.Ordinal)
                || combined.Contains("form", StringComparison.Ordinal)
                || combined.Contains("input field", StringComparison.Ordinal)
                || combined.Contains("interactive", StringComparison.Ordinal))
            {
                return "Output a complete self-contained HTML document with inline CSS/JS. " +
                       "Include visible input fields, buttons, and a result display area. " +
                       "Must work fully offline in WebView2. " +
                       "Style inputs with width:100% or max-width:100% so they don't overflow the canvas." + sizingRule;
            }

            if (combined.Contains("chart", StringComparison.Ordinal)
                || combined.Contains("graph", StringComparison.Ordinal)
                || combined.Contains("dashboard", StringComparison.Ordinal)
                || combined.Contains("html", StringComparison.Ordinal)
                || combined.Contains("plot", StringComparison.Ordinal)
                || combined.Contains("coordinate", StringComparison.Ordinal)
                || combined.Contains("table", StringComparison.Ordinal)
                || combined.Contains("ui", StringComparison.Ordinal)
                || combined.Contains("interface", StringComparison.Ordinal)
                || combined.Contains("timeline", StringComparison.Ordinal)
                || combined.Contains("grid", StringComparison.Ordinal)
                || combined.Contains("matrix", StringComparison.Ordinal))
            {
                return "Output a complete self-contained HTML document with inline CSS/JS. " +
                       "Use only native browser APIs (HTML5 Canvas API or inline SVG) for any drawing — " +
                       "do NOT import Chart.js, D3.js, Plotly.js, ECharts, or any library via a CDN <script> tag. " +
                       "For HTML5 Canvas: initialize canvas.width and canvas.height from the container's clientWidth in JavaScript, not hardcoded numbers. " +
                       "Must render correctly offline in WebView2 with zero external network requests." + sizingRule;
            }

            return "Prefer a self-contained visual artifact implementation that renders directly in Project Canvas." + sizingRule;
        }

        private static bool ArtifactRequestNeedsDynamicHtml(string userQuery, string objective)
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            if (combined.Contains("svg", StringComparison.Ordinal))
                return false;

            return combined.Contains("simulate", StringComparison.Ordinal)
                || combined.Contains("simulation", StringComparison.Ordinal)
                || combined.Contains("animated", StringComparison.Ordinal)
                || combined.Contains("animation", StringComparison.Ordinal)
                || combined.Contains("sun cycle", StringComparison.Ordinal)
                || combined.Contains("time of day", StringComparison.Ordinal)
                || combined.Contains("slider", StringComparison.Ordinal)
                || combined.Contains("playback", StringComparison.Ordinal)
                || combined.Contains("interactive", StringComparison.Ordinal)
                || combined.Contains("input", StringComparison.Ordinal)
                || combined.Contains("calculate", StringComparison.Ordinal)
                || combined.Contains("calculator", StringComparison.Ordinal)
                || combined.Contains("energy", StringComparison.Ordinal)
                || combined.Contains("generation", StringComparison.Ordinal);
        }

        private static bool IsDynamicArtifactStaticSvgMismatch(CouncilRunContext context, string builderOutput)
        {
            if (!context.IsArtifactCanvasRequest
                || !ArtifactRequestNeedsDynamicHtml(context.UserPrompt, context.Objective))
            {
                return false;
            }

            return ArtifactRenderService.DetectForCanvas(builderOutput, null).Kind == ArtifactKind.Svg;
        }

        private static bool RequestsExplicitArtifactFormatChange(string userQuery)
        {
            string query = (userQuery ?? string.Empty).ToLowerInvariant();
            string[] signals =
            [
                "convert to html", "convert it to html", "as html", "into html",
                "convert to svg", "convert it to svg", "as svg", "into svg",
                "convert to markdown", "convert it to markdown", "as markdown", "into markdown",
                "convert to javascript", "convert it to javascript", "as javascript", "into javascript",
                "convert to python", "convert it to python", "as python", "into python"
            ];
            return signals.Any(signal => query.Contains(signal, StringComparison.Ordinal));
        }

        private static string GetCanvasIterationFormatHint(ArtifactKind kind, int canvasWidth)
        {
            string sizing = $" Preserve the current artifact type. Keep the output fluid within the {canvasWidth}px-wide, user-resizable canvas.";
            return kind switch
            {
                ArtifactKind.Html => "Return a complete self-contained HTML document with inline CSS/JavaScript and no external resources." + sizing,
                ArtifactKind.Svg => "Return one complete inline SVG with a viewBox and width='100%' on the root element." + sizing,
                ArtifactKind.Chart => "Return the complete Python chart source so the sandbox capture flow can regenerate the chart." + sizing,
                ArtifactKind.Document => "Return the complete Markdown document, preserving its heading/table structure. Use one ```markdown code fence and no prose outside it." + sizing,
                ArtifactKind.InteractiveJavaScript => "Return the complete interactive JavaScript source in one ```javascript code fence. Preserve the DOM/canvas behavior and do not convert it to HTML unless explicitly requested." + sizing,
                _ => "Return a complete replacement in the same language and format as the current Project Canvas source." + sizing
            };
        }

        /// <summary>
        /// Single source of truth for the canvas rendering environment quoted to the models.
        /// Quotes the live-measured viewport (not a hardcoded constant) and pins down the host
        /// theme and legibility minimums so models stop producing default-styled output that is
        /// oversized, cramped, or invisible on the dark pane.
        /// </summary>
        private static string BuildCanvasEnvironmentSpec(int viewportWidth, int viewportHeight)
        {
            return
                $"CANVAS ENVIRONMENT: The artifact renders inside a {viewportWidth}px wide × {viewportHeight}px tall WebView2 pane. " +
                "The pane is user-resizable (roughly 300–730px wide), so the layout must be fluid: width:100%, max-width:100%, box-sizing:border-box on all containers; never fix outer widths in pixels. " +
                $"Vertical scrolling is allowed, but the most important content must be visible within the first {viewportHeight}px. " +
                "THEME: The host pane behind the artifact is DARK (#171615). ALWAYS set an explicit background-color and text color on body — never rely on browser defaults (default black text becomes invisible on the dark backdrop). " +
                "Either design a dark theme matching the host (background #171615 or #211F1D, text #D1D3DF, headings #D5DAD3, borders #2D3139, accent #B8924A) or a deliberate light card (background #ffffff, text #1a1a1a). " +
                "LEGIBILITY: base font-size at least 14px, line-height at least 1.5, font-family 'Segoe UI', sans-serif as the default; 12–16px padding inside the root container; keep at least 4.5:1 contrast between text and its background; make headings clearly larger than body text. " +
                "CONTROLS: buttons and inputs at least 32px tall with visible borders or fills and width:100%/max-width:100% so they never overflow.";
        }

        private static string BuildArtifactCanvasBoost(CouncilRole role, string preferredFormatHint, CouncilRunContext runContext)
        {
            string formatHint = string.IsNullOrWhiteSpace(preferredFormatHint)
                ? "Prefer a self-contained offline artifact implementation."
                : preferredFormatHint.Trim();
            int viewportWidth = runContext.CanvasViewportWidth;
            int viewportHeight = runContext.CanvasViewportHeight;

            return role switch
            {
                CouncilRole.Architect =>
                    "\n[PROJECT CANVAS ARTIFACT REQUEST] The user is asking for a renderable artifact deliverable for Project Canvas. " +
                    "Produce a compact build contract, not a procedural recipe and not a numbered step plan. " +
                    "Name the artifact type, required output format, explicit user requirements, hard constraints, Builder instruction, and acceptance tests. " +
                    $"The artifact will display in a {viewportWidth}px wide pane on a dark background — plan sections that stack vertically and fit a narrow column, not wide multi-column desktop layouts. " +
                    "The Builder should receive a crisp source-of-truth handoff that tells it to return complete renderable code only. " +
                    formatHint,
                CouncilRole.Builder =>
                    "\n[PROJECT CANVAS ARTIFACT REQUEST] The final deliverable must be a renderable artifact for Project Canvas. " +
                    "CRITICAL FORMAT RULE: Wrap the entire artifact in exactly ONE code fence using the correct language tag (```html, ```svg, ```python, ```javascript, ```markdown, etc.). " +
                    "Output NOTHING outside that single code fence — no preamble, no explanation, no notes after the closing ```. " +
                    "Do not output multiple alternatives or prose commentary around the artifact. " +
                    "If the request is visual and no language is forced, favor a complete HTML/CSS/JS document; use standalone SVG only for explicitly requested SVG or clearly static diagrams. " +
                    "If the artifact needs animation, simulation, user controls, formulas, calculations, time/state changes, or dynamic readouts, use HTML with inline JavaScript rather than standalone SVG. " +
                    "Any HTML or JavaScript must be self-contained and work offline with inline CSS/JS only. " +
                    BuildCanvasEnvironmentSpec(viewportWidth, viewportHeight) + " " +
                    "For SVG: always include a viewBox attribute and set width='100%' with no fixed height on the root <svg> element. " +
                    "For HTML5 Canvas: set canvas dimensions in JavaScript using the parent element's clientWidth, not hardcoded numbers. " +
                    "For tables and grids: use width:100% with word-wrap so content never overflows horizontally. " +
                    formatHint,
                CouncilRole.Critic =>
                    "\n[PROJECT CANVAS ARTIFACT REQUEST] Review the Builder output as a renderable artifact deliverable. " +
                    $"It will render in a {viewportWidth}px wide pane whose host background is dark (#171615). " +
                    "The Builder source may be raw HTML/SVG/JS because the pipeline strips code fences before rendering; do not fail valid raw artifact source solely for missing markdown fences. " +
                    "Check that: (1) the output is one complete artifact source, not prose or multiple alternatives, " +
                    "(2) it matches the requested visual/task outcome, " +
                    "(3) it is self-contained and does not rely on external internet resources, " +
                    "(4) it uses responsive/fluid sizing — flag SVG root elements with hardcoded width/height but no viewBox, " +
                    $"flag HTML containers with fixed pixel widths wider than {Math.Max(300, viewportWidth - 24)}px, " +
                    "and flag HTML5 Canvas elements initialized with hardcoded dimensions instead of container-relative sizes, " +
                    "(5) it is legible — flag a body with no explicit background-color or text color (browser-default black text is invisible on the dark host), " +
                    "flag base font sizes under 12px, and flag low-contrast text/background color pairs. " +
                    "If the requested artifact involves simulation, animation, calculation, user controls, or changing readouts, standalone SVG is insufficient unless it contains working script behavior; prefer HTML/JS. " +
                    "Severity guidance: CRITICAL for non-rendering output, broken syntax/runtime, or mathematically wrong results; HIGH for missing a core requested behavior; MEDIUM for responsiveness/legibility issues that materially hurt use; LOW for subjective polish. " +
                    "Only report evidence-backed failures. Do not request a Builder rewrite for LOW-only subjective style preferences when deterministic sandbox checks pass and the artifact renders, fulfills the request, and is usable. " +
                    "If the output contains prose commentary instead of implementation, treat that as a failure.",
                _ => string.Empty
            };
        }

        private static bool IsSmallLocalCouncilModel(string? modelNameOrPath, bool isCloudModeEnabled)
        {
            if (isCloudModeEnabled || string.IsNullOrWhiteSpace(modelNameOrPath))
                return false;

            if (!TryGetModelParamBillions(modelNameOrPath, out double sizeB))
                return false;

            return sizeB > 0 && sizeB <= 10.0;
        }

        private static string BuildSmallModelArtifactAssist(CouncilRole role, string preferredFormatHint, CouncilRunContext runContext)
        {
            string formatHint = string.IsNullOrWhiteSpace(preferredFormatHint)
                ? "Prefer one small self-contained offline HTML artifact."
                : preferredFormatHint.Trim();
            int viewportWidth = runContext.CanvasViewportWidth;
            string compactFormatRule = runContext.ExistingCanvasArtifactKind switch
            {
                ArtifactKind.Document => "Preserve the current Markdown document format and structure. ",
                ArtifactKind.InteractiveJavaScript => "Preserve the current interactive JavaScript format and DOM/canvas behavior. ",
                ArtifactKind.Svg => "Preserve the current single-SVG format. ",
                ArtifactKind.Chart => "Preserve the current Python chart-source format. ",
                _ => "Prefer a compact HTML file with inline CSS and inline JavaScript. "
            };

            return role switch
            {
                CouncilRole.Architect =>
                    "\n[SMALL MODEL ARTIFACT MODE] Keep the ARCHITECT_HANDOFF very short and literal. " +
                    "Use concise bullets inside the required handoff sections. " +
                    "Plan for exactly one final artifact file only. " +
                    "Prefer simple HTML with inline CSS/JS over multi-file or framework solutions. " +
                    formatHint,
                CouncilRole.Builder =>
                    "\n[SMALL MODEL ARTIFACT MODE] Output one complete artifact only. " +
                    "Start your ENTIRE response with the opening code fence (e.g., ```html) and end with the closing ```. " +
                    "Do NOT write any text before the opening ``` or after the closing ```. " +
                    compactFormatRule +
                    "Use plain DOM APIs and simple controls like input, button, svg, canvas, and div. " +
                    "Avoid external libraries, build tools, frameworks, and complex abstractions. " +
                    $"SIZING: the display pane is {viewportWidth}px wide. Set body {{ width:100%; margin:0; padding:12px; box-sizing:border-box; }}. " +
                    "COLORS: always set body { background:#ffffff; color:#1a1a1a; font-family:'Segoe UI',sans-serif; font-size:14px; } or a dark equivalent — never leave background and text colors unset. " +
                    "For SVG, include a viewBox and use width='100%' on the root element — never use fixed pixel widths. " +
                    "If the request mentions graph, chart, plot, coordinates, x/y, simulation, energy, time of day, sun movement, or user input, include visible controls, readouts, and behavior in the HTML itself. " +
                    formatHint,
                CouncilRole.Critic =>
                    "\n[SMALL MODEL ARTIFACT MODE] Be strict about prose drift. " +
                    "If the output is not one concrete renderable artifact, report that as a failure. " +
                    "Prefer compact, offline-safe, directly renderable output.",
                _ => string.Empty
            };
        }

        private static string BuildLocalBuilderCognitionBoost(CouncilTaskType taskType, CouncilRunContext runContext)
        {
            string outputKind = runContext.IsArtifactCanvasRequest
                ? "one complete renderable Project Canvas artifact"
                : taskType == CouncilTaskType.Coding
                    ? "one complete working code/script deliverable"
                    : "one complete final deliverable that directly answers the request";

            return
                "\n[LOCAL SMALL-MODEL BUILDER BOOST]\n" +
                "You are running as a local model, so use this deterministic execution loop internally before writing the answer:\n" +
                "1. Read the latest user request and the approved Architect specification before acting. For code/canvas work, read [[BUILDER IMPLEMENTATION CAPSULE]] as the compact source of truth.\n" +
                "2. Convert every requirement into a concrete output behavior or content change. Do not merely describe a change that the deliverable does not contain.\n" +
                "3. Decide whether a tool is actually needed BEFORE drafting: SEARCH_HIPPOCAMPUS for prior-session facts; CALCULATE for one expression; PYTHON_MATH or RUN_SANDBOX for executable verification; WEB_SEARCH only for current or source-backed facts. Use the exact [PAUSE: TOOL | query] protocol and never invent a tool name.\n" +
                "4. Call all needed tools before the final deliverable. After [RESULT: ...], incorporate the result and do not expose tool commands, scratch work, or raw tool output. If a tool fails, use a supported alternative or state the exact limitation; never claim the tool succeeded.\n" +
                "5. Choose the simplest implementation that satisfies the request. For a canvas iteration, modify the supplied current source and preserve every unaffected part.\n" +
                "6. Before finalizing, check: each requested change is materially present, syntax closes, required controls/functions exist, values that needed tools were verified, and output-format rules are followed.\n" +
                $"7. Output {outputKind} only. No plan, no change log, and no claim that a change was made unless the returned deliverable actually contains it.\n" +
                "If a detail is underspecified, choose the simplest reasonable default and keep the implementation coherent.";
        }

        private static string BuildBuilderImplementationCapsule(CouncilRunContext runContext)
        {
            var decomp = runContext.Decomposition ?? new PreFlightDecomposition
            {
                ProblemStatement = runContext.UserPrompt
            };

            var capsule = new StringBuilder();
            capsule.AppendLine("Purpose: " + (string.IsNullOrWhiteSpace(decomp.ProblemStatement) ? runContext.UserPrompt : decomp.ProblemStatement.Trim()));
            capsule.AppendLine("Output target: " + (runContext.IsWorkspaceTask
                ? "Connected codebase patch proposal"
                : runContext.IsArtifactCanvasRequest ? "Project Canvas renderable artifact" : "Working code/script"));
            capsule.AppendLine("Task type: " + runContext.TaskType);

            if (runContext.IsArtifactCanvasRequest && !string.IsNullOrWhiteSpace(runContext.PreferredArtifactFormatHint))
                capsule.AppendLine("Artifact format: " + runContext.PreferredArtifactFormatHint.Trim());

            if (decomp.Requirements.Count > 0)
            {
                capsule.AppendLine("Required behavior:");
                for (int i = 0; i < Math.Min(decomp.Requirements.Count, 8); i++)
                    capsule.AppendLine($"- {decomp.Requirements[i]}");
            }

            if (decomp.Constraints.Count > 0)
            {
                capsule.AppendLine("Constraints:");
                for (int i = 0; i < Math.Min(decomp.Constraints.Count, 8); i++)
                    capsule.AppendLine($"- {decomp.Constraints[i]}");
            }

            if (!string.IsNullOrWhiteSpace(runContext.ArchitectOutput))
            {
                capsule.AppendLine("Architect steps to implement:");
                foreach (string step in ExtractArchitectStepLines(runContext.ArchitectOutput).Take(8))
                    capsule.AppendLine("- " + step);
            }

            capsule.AppendLine("Final self-check before output:");
            capsule.AppendLine("- Every required behavior above is represented in the code/artifact.");
            capsule.AppendLine("- No placeholder TODOs, no missing event handlers, no broken closing tags/braces.");
            capsule.AppendLine(runContext.IsWorkspaceTask
                ? "- Output is a valid [[AXIOM_CODEBASE_PATCH]] envelope with complete file content, not standalone code or prose."
                : "- Output is one complete file/artifact, not a fragment.");

            return BuildLabeledBlock("BUILDER IMPLEMENTATION CAPSULE", capsule.ToString().Trim());
        }

        private static IEnumerable<string> ExtractArchitectStepLines(string architectOutput)
        {
            if (string.IsNullOrWhiteSpace(architectOutput))
                yield break;

            if (IsArchitectArtifactHandoff(architectOutput))
            {
                foreach (string item in ExtractArchitectHandoffBullets(architectOutput, "requirements", "must_include", "acceptance_tests"))
                    yield return item;
                yield break;
            }

            foreach (string line in architectOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (ArchitectNumberedStepRegex.IsMatch(trimmed))
                    yield return ArchitectNumberedStepRegex.Replace(trimmed, string.Empty).Trim();
            }
        }

        private static IEnumerable<string> ExtractArchitectHandoffBullets(string architectOutput, params string[] sectionNames)
        {
            if (string.IsNullOrWhiteSpace(architectOutput) || sectionNames.Length == 0)
                yield break;

            var wanted = new HashSet<string>(sectionNames, StringComparer.OrdinalIgnoreCase);
            string currentSection = string.Empty;
            foreach (string line in architectOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string trimmed = line.Trim();
                if (trimmed.EndsWith(":", StringComparison.Ordinal))
                {
                    currentSection = trimmed.TrimEnd(':').Trim();
                    continue;
                }

                if (wanted.Contains(currentSection) && trimmed.StartsWith("- ", StringComparison.Ordinal))
                {
                    string item = trimmed[2..].Trim();
                    if (!string.IsNullOrWhiteSpace(item))
                        yield return item;
                }
            }
        }

        /// <summary>
        /// Multi-stage recovery pipeline for artifact canvas requests where the Builder drifted
        /// to prose instead of producing a renderable artifact.
        ///
        /// Stage 1 — Passthrough: output is already renderable, return as-is.
        /// Stage 2 — Prose extraction: Builder included a valid artifact inside its prose
        ///            (e.g., "Here is your chart: ```html …"). Strip the surrounding text and
        ///            return just the artifact portion so ArtifactRenderService can detect it.
        /// Stage 3 — Deterministic fallback: for well-known request patterns (XY plotter, etc.)
        ///            inject a pre-built deterministic artifact when no other recovery succeeds.
        ///
        /// Returns the recovered artifact string (may still be raw output), or empty string when
        /// no recovery was possible. Callers should re-run DetectForCanvas on the return value.
        /// </summary>
        private static string TryBuildDeterministicArtifactRecovery(CouncilRunContext context, string builderOutput)
        {
            if (!context.IsArtifactCanvasRequest)
                return string.Empty;

            string existing = builderOutput ?? string.Empty;

            // Stage 1 — already renderable
            if (ArtifactRenderService.DetectForCanvas(existing, null).SupportsPreview)
                return existing;

            // Stage 2 — attempt prose extraction: look for a fenced code block containing
            // HTML, SVG, or JavaScript buried inside explanatory prose output.
            string extracted = TryExtractArtifactFromProse(existing);
            if (!string.IsNullOrWhiteSpace(extracted)
                && ArtifactRenderService.DetectForCanvas(extracted, null).SupportsPreview)
                return extracted;

            // Stage 3 — deterministic fallbacks for specific well-known request patterns
            string combined = $"{context.UserPrompt} {context.Objective}".ToLowerInvariant();

            bool wantsPlotInputs = (combined.Contains("graph", StringComparison.Ordinal)
                    || combined.Contains("chart", StringComparison.Ordinal)
                    || combined.Contains("plot", StringComparison.Ordinal))
                && (combined.Contains("coordinate", StringComparison.Ordinal)
                    || combined.Contains("x and y", StringComparison.Ordinal)
                    || combined.Contains("x & y", StringComparison.Ordinal)
                    || combined.Contains("x/y", StringComparison.Ordinal)
                    || combined.Contains("input", StringComparison.Ordinal)
                    || combined.Contains("point", StringComparison.Ordinal));

            if (wantsPlotInputs)
                return "```html\n<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<title>Interactive XY Plotter</title>\n<style>body{font-family:Segoe UI,sans-serif;margin:0;padding:16px;background:#f5f5f5;color:#1f2937}h1{font-size:18px;margin:0 0 12px}p{margin:0 0 12px;color:#4b5563}.panel{display:flex;gap:10px;flex-wrap:wrap;align-items:end;margin-bottom:14px}.field{display:flex;flex-direction:column;gap:6px}.field label{font-size:12px;color:#374151}input{padding:8px 10px;border:1px solid #cbd5e1;border-radius:8px;min-width:110px}button{padding:9px 14px;border:none;border-radius:8px;background:#2563eb;color:#fff;cursor:pointer}button.secondary{background:#64748b}#pointsList{margin-top:12px;font-size:13px;color:#374151}svg{width:100%;height:420px;background:#fff;border:1px solid #cbd5e1;border-radius:12px}text{font-size:11px;fill:#475569}.axis{stroke:#334155;stroke-width:2}.grid{stroke:#e2e8f0;stroke-width:1}.point{fill:#ef4444;stroke:#991b1b;stroke-width:1.5}</style>\n</head>\n<body>\n<h1>Interactive X/Y Graph</h1>\n<p>Enter X and Y values, then add points to the graph.</p>\n<div class=\"panel\">\n<div class=\"field\"><label for=\"xValue\">X value</label><input id=\"xValue\" type=\"number\" step=\"any\" value=\"0\"></div>\n<div class=\"field\"><label for=\"yValue\">Y value</label><input id=\"yValue\" type=\"number\" step=\"any\" value=\"0\"></div>\n<button id=\"addPointButton\" type=\"button\">Add point</button>\n<button id=\"clearButton\" type=\"button\" class=\"secondary\">Clear</button>\n</div>\n<svg id=\"chart\" viewBox=\"0 0 720 420\" aria-label=\"XY coordinate plot\"></svg>\n<div id=\"pointsList\">No points yet.</div>\n<script>const svg=document.getElementById('chart');const pointsList=document.getElementById('pointsList');const xInput=document.getElementById('xValue');const yInput=document.getElementById('yValue');const points=[];function createSvg(tag,attrs){const el=document.createElementNS('http://www.w3.org/2000/svg',tag);for(const key in attrs){el.setAttribute(key,String(attrs[key]));}return el;}function draw(){svg.innerHTML='';const width=720,height=420,pad=40;const values=points.flatMap(p=>[p.x,p.y]);const maxAbs=Math.max(10,...values.map(v=>Math.abs(v)));const scale=(Math.min(width,height)-pad*2)/(maxAbs*2||1);for(let i=-Math.ceil(maxAbs);i<=Math.ceil(maxAbs);i++){const gx=width/2+i*scale;const gy=height/2-i*scale;svg.appendChild(createSvg('line',{x1:gx,y1:pad,x2:gx,y2:height-pad,class:'grid'}));svg.appendChild(createSvg('line',{x1:pad,y1:gy,x2:width-pad,y2:gy,class:'grid'}));if(i!==0){const tx=createSvg('text',{x:gx+2,y:height/2-4});tx.textContent=i;svg.appendChild(tx);const ty=createSvg('text',{x:width/2+6,y:gy-2});ty.textContent=i;svg.appendChild(ty);}}svg.appendChild(createSvg('line',{x1:pad,y1:height/2,x2:width-pad,y2:height/2,class:'axis'}));svg.appendChild(createSvg('line',{x1:width/2,y1:pad,x2:width/2,y2:height-pad,class:'axis'}));points.forEach((p,index)=>{const cx=width/2+p.x*scale;const cy=height/2-p.y*scale;svg.appendChild(createSvg('circle',{cx,cy,r:5,class:'point'}));const label=createSvg('text',{x:cx+8,y:cy-8});label.textContent=`P${index+1} (${p.x}, ${p.y})`;svg.appendChild(label);});pointsList.textContent=points.length?points.map((p,i)=>`P${i+1}: (${p.x}, ${p.y})`).join(' | '):'No points yet.';}document.getElementById('addPointButton').addEventListener('click',()=>{const x=Number(xInput.value);const y=Number(yInput.value);if(Number.isFinite(x)&&Number.isFinite(y)){points.push({x,y});draw();}});document.getElementById('clearButton').addEventListener('click',()=>{points.length=0;draw();});draw();</script>\n</body>\n</html>\n```";

            return string.Empty;
        }

        /// <summary>
        /// Attempts to extract a single renderable artifact block from prose-wrapped Builder
        /// output. Scans for the largest fenced code block whose language tag indicates a
        /// renderable type (html, svg, javascript, js, python) and returns it wrapped in its
        /// original fence so ArtifactRenderService can detect it normally.
        /// Returns empty string if no plausible artifact block is found.
        /// </summary>
        private static string TryExtractArtifactFromProse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            // Find all fenced code blocks and pick the largest renderable one
            var fenceRegex = new System.Text.RegularExpressions.Regex(
                @"```(?<lang>[^\r\n`]*)\r?\n(?<code>[\s\S]*?)```",
                System.Text.RegularExpressions.RegexOptions.Compiled);

            string bestBlock = string.Empty;
            int bestLength = 0;

            foreach (System.Text.RegularExpressions.Match m in fenceRegex.Matches(raw))
            {
                string lang = m.Groups["lang"].Value.Trim().ToLowerInvariant();
                string code = m.Groups["code"].Value.Trim();

                bool isRenderable = lang is "html" or "svg" or "javascript" or "js" or "python"
                    || code.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
                    || code.Contains("<html", StringComparison.OrdinalIgnoreCase)
                    || code.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase);

                if (isRenderable && code.Length > bestLength)
                {
                    bestLength = code.Length;
                    string langTag = string.IsNullOrWhiteSpace(lang) ? "html" : lang;
                    bestBlock = $"```{langTag}\n{code}\n```";
                }
            }

            return bestBlock;
        }

        private static bool DetectCalculationTask(string userQuery, string objective)
        {
            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            string[] calcKeywords =
            [
                "calculate", "compute", "simulate", "estimate", "measure", "convert",
                "formula", "equation", "unit", "physics", "engineering", "finance",
                "rainfall", "temperature", "pressure", "velocity", "acceleration",
                "interest", "compound", "mortgage", "distance", "area", "volume",
                "watts", "joules", "newtons", "meters per", "kilometers",
                "miles per", "gallons", "liters", "celsius", "fahrenheit",
                "kilogram", "pounds", "conversion factor", "square meters",
                "cubic", "bmi", "density", "flow rate", "energy"
            ];
            return calcKeywords.Any(k => combined.Contains(k));
        }

        private bool ShouldUseDocumentContext(string userQuery, string objective)
        {
            if (_documents.Count == 0)
                return false;

            string combined = $"{userQuery} {objective}".ToLowerInvariant();
            string[] docIntent =
            [
                "document", "pdf", "file", "attached", "uploaded", "from the text", "in the file",
                "summarize", "summarise", "key points", "analyze", "analyse", "extract", "review"
            ];

            if (docIntent.Any(k => combined.Contains(k)))
                return true;

            foreach (var doc in _documents)
            {
                string stem = Path.GetFileNameWithoutExtension(doc.Name).ToLowerInvariant();
                if (!string.IsNullOrWhiteSpace(stem) && combined.Contains(stem, StringComparison.Ordinal))
                    return true;
            }

            // Sticky grounding: the user deliberately attached the document and a prior turn already
            // worked with it, so keep referencing it for natural follow-ups that don't repeat a doc
            // keyword or the filename. The user removes the attachment when they're done with it.
            if (_documentContextEngaged)
                return true;

            return false;
        }

        private static string BuildCalculatorContext(string input)
        {
            return CalculatorToolAgent.TryBuildContext(input, out var context, out _)
                ? context
                : "";
        }

        private static int ScoreSandboxEligibility(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return 0;

            int score = SandboxExpressionRegex.Matches(message).Count * 3;
            string lower = message.ToLowerInvariant();

            foreach (string unit in SandboxUnitWords)
                score += Regex.Matches(lower, $@"\b{Regex.Escape(unit)}\b").Count * 2;

            foreach (string phrase in SandboxQuantityPhrases)
                score += Regex.Matches(lower, Regex.Escape(phrase)).Count * 2;

            foreach (string word in SandboxDomainWords)
                score += Regex.Matches(lower, $@"\b{Regex.Escape(word)}\b").Count;

            return score;
        }

        private static bool DetectDynamicUserInputIntent(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            string lower = message.ToLowerInvariant();
            return DynamicInputIntentPhrases.Any(lower.Contains);
        }

        private static List<SandboxVariableSeed> ExtractSandboxVariableSeeds(string message, bool skipDynamicInputs = false)
        {
            var seeds = new List<SandboxVariableSeed>();
            if (string.IsNullOrWhiteSpace(message))
                return seeds;

            var nameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in SandboxNumberWithUnitRegex.Matches(message))
            {
                string number = match.Groups["number"].Value;
                if (string.IsNullOrWhiteSpace(number))
                    continue;

                string unit = match.Groups["unit"].Value.Trim();
                string label = match.Groups["label"].Value.Trim();
                if (skipDynamicInputs && string.IsNullOrWhiteSpace(unit) && IsLikelyDynamicInputLabel(label))
                    continue;

                string baseName = BuildSandboxVariableName(label, unit, seeds.Count + 1);
                if (nameCounts.TryGetValue(baseName, out int count))
                {
                    count++;
                    nameCounts[baseName] = count;
                    baseName = $"{baseName}_{count}";
                }
                else
                {
                    nameCounts[baseName] = 1;
                }

                string assignment = BuildSandboxAssignment(baseName, number, unit);
                if (string.IsNullOrWhiteSpace(assignment))
                    continue;

                string displayValue = unit.Equals("percent", StringComparison.OrdinalIgnoreCase)
                    ? $"{number}%"
                    : string.IsNullOrWhiteSpace(unit) ? number : $"{number} {unit}";

                seeds.Add(new SandboxVariableSeed
                {
                    Name = baseName,
                    DisplayValue = displayValue,
                    PythonAssignment = assignment
                });
            }

            return seeds;
        }

        private static bool IsLikelyDynamicInputLabel(string label)
        {
            if (string.IsNullOrWhiteSpace(label))
                return true;

            string lower = label.ToLowerInvariant();
            string[] dynamicHints = ["user", "input", "enter", "prompt", "value", "number", "amount", "quantity", "count", "hours", "time", "duration", "cost", "price", "rate", "sold"];
            return dynamicHints.Any(lower.Contains);
        }

        private static string BuildSandboxVariableName(string label, string unit, int fallbackIndex)
        {
            string candidate = string.IsNullOrWhiteSpace(label) ? unit : label;
            candidate = Regex.Replace(candidate?.ToLowerInvariant() ?? string.Empty, @"[^a-z0-9]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(candidate))
                candidate = $"value_{fallbackIndex}";
            if (char.IsDigit(candidate[0]))
                candidate = "value_" + candidate;

            return candidate switch
            {
                "invest" => "invest_amount",
                "at" when unit.Equals("percent", StringComparison.OrdinalIgnoreCase) => "rate",
                _ => candidate
            };
        }

        private static string BuildSandboxAssignment(string variableName, string number, string unit)
        {
            if (!double.TryParse(number, out _))
                return string.Empty;

            if (unit.Equals("percent", StringComparison.OrdinalIgnoreCase))
                return $"{variableName} = {number} / 100.0";

            return $"{variableName} = {number}";
        }

        private static string BuildPythonPreamble(List<SandboxVariableSeed> seeds)
        {
            if (seeds.Count == 0)
                return string.Empty;

            return string.Join("\n", seeds.Select(s => s.PythonAssignment));
        }

        private static string BuildSandboxSystemPromptInjection(List<SandboxVariableSeed> seeds, bool userInputIntentDetected)
        {
            var lines = new List<string>
            {
                "[PYTHON SANDBOX] A Python 3 execution environment is available and will run automatically.",
                "Do not perform arithmetic or calculations in prose.",
                "Write one clean executable Python script that computes the answer.",
                "Print each intermediate result with a descriptive label before computing the next step.",
                "For each calculation step, print the step name, the formula used, and the result on separate lines in this format: Step: [name], Formula: [expression], Result: [value]",
                "Do not use input() in your code. All variable values will be pre-declared in the environment and are already available by name.",
                "The verified output of that script will be shown as the final result."
            };

            if (userInputIntentDetected)
                lines.Add("The sandbox will substitute placeholder values for user input variables to verify the code runs correctly, and the actual program will accept real input when the user runs it outside the sandbox.");

            if (seeds.Count > 0)
                lines.Add("Declared variables: " + string.Join(", ", seeds.Select(s => $"{s.Name}={s.DisplayValue}")));

            return string.Join("\n", lines);
        }

        private static string NormalizeMathExpressionForPython(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            string normalized = message;
            normalized = Regex.Replace(normalized, @"\bone\s+half\b", "0.5", RegexOptions.IgnoreCase);
            normalized = Regex.Replace(normalized, @"\bone\s+over\s+two\b", "1/2", RegexOptions.IgnoreCase);
            normalized = DigitLetterMultiplicationRegex.Replace(normalized, "${digit}*${letter}");
            normalized = CaretExponentRegex.Replace(normalized, "${left}**${right}");
            normalized = SqrtRegex.Replace(normalized, "math.sqrt");
            normalized = PiRegex.Replace(normalized, "math.pi");
            return normalized.Trim();
        }

        private static string BuildNormalizedExpressionNote(string message)
        {
            string normalized = NormalizeMathExpressionForPython(message);
            if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, message?.Trim(), StringComparison.Ordinal))
                return string.Empty;

            return "Normalized math expression: " + normalized;
        }

        private static string ExtractRelevantUnit(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return string.Empty;

            string lower = message.ToLowerInvariant();
            foreach (string unit in SandboxUnitWords)
            {
                if (Regex.IsMatch(lower, $@"\b{Regex.Escape(unit)}\b", RegexOptions.IgnoreCase))
                    return unit;
            }

            return string.Empty;
        }

        private static string CanonicalizeUnit(string unit)
        {
            string lower = unit?.Trim().ToLowerInvariant() ?? string.Empty;
            return lower switch
            {
                "kilometers" => "km",
                "meters" => "m",
                "kilograms" => "kg",
                "percent" => "%",
                "seconds" => "s",
                "minutes" => "min",
                "hours" => "hr",
                _ => lower
            };
        }

        private static string AppendUnitToNumericOutput(string output, string originalMessage)
        {
            output = ArtifactRenderService.RemoveChartOutputLines(output);
            if (string.IsNullOrWhiteSpace(output))
                return output;

            string unit = ExtractRelevantUnit(originalMessage);
            if (string.IsNullOrWhiteSpace(unit))
                return output;

            string canonicalUnit = CanonicalizeUnit(unit);
            var lines = new List<string>();
            foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.None))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    lines.Add(rawLine);
                    continue;
                }

                Match simple = NumericOnlyLineRegex.Match(line);
                if (simple.Success)
                {
                    lines.Add(line + " " + canonicalUnit);
                    continue;
                }

                Match labeled = NumericWithOptionalLabelRegex.Match(line);
                if (labeled.Success && string.IsNullOrWhiteSpace(labeled.Groups["suffix"].Value))
                {
                    string prefix = string.IsNullOrWhiteSpace(labeled.Groups["label"].Value)
                        ? string.Empty
                        : labeled.Groups["label"].Value.Trim() + ": ";
                    lines.Add(prefix + labeled.Groups["value"].Value + " " + canonicalUnit);
                    continue;
                }

                lines.Add(rawLine);
            }

            return string.Join("\n", lines);
        }

        private static string FormatPythonResultBlock(string output)
        {
            output = ArtifactRenderService.RemoveChartOutputLines(output);
            if (string.IsNullOrWhiteSpace(output))
                return string.Empty;

            var lines = new List<string>();
            foreach (string rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var match = PythonResultLineRegex.Match(line);
                lines.Add(match.Success
                    ? $"- {match.Groups["label"].Value.Trim()}: {match.Groups["value"].Value.Trim()}"
                    : $"- {line}");
            }

            return lines.Count == 0 ? string.Empty : "[[PYTHON RESULT]]\n" + string.Join("\n", lines) + "\n[[END PYTHON RESULT]]";
        }

        private static string BuildSandboxTimeoutResultBlock()
        {
            return "[[PYTHON TIMEOUT]]Sandbox Timeout\nThe script took too long to execute.\n[[END PYTHON TIMEOUT]]";
        }

        private static SandboxExecutionDisplay BuildSandboxExecutionDisplay(string sandboxResult)
        {
            string cleanedSandboxResult = ArtifactRenderService.RemoveChartOutputLines(sandboxResult);
            if (string.IsNullOrWhiteSpace(sandboxResult))
                return new SandboxExecutionDisplay();

            if (sandboxResult.StartsWith("[[PYTHON TIMEOUT]]", StringComparison.OrdinalIgnoreCase))
            {
                string chatDisplay = "<div class=\"sandbox-timeout-block\"><div class=\"sandbox-timeout-label\">Sandbox Timeout</div><div class=\"sandbox-timeout-note\">The script took too long to execute.</div></div>";
                return new SandboxExecutionDisplay
                {
                    CriticContextPayload = sandboxResult,
                    ChatDisplayPayload = chatDisplay
                };
            }

            return new SandboxExecutionDisplay
            {
                CriticContextPayload = sandboxResult,
                ChatDisplayPayload = cleanedSandboxResult
            };
        }

        private static List<string> ExtractDeclaredVariableNames(string preamble)
        {
            var names = new List<string>();
            if (string.IsNullOrWhiteSpace(preamble))
                return names;

            foreach (string rawLine in preamble.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    continue;

                string candidate = line[..eq].Trim();
                if (Regex.IsMatch(candidate, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                    names.Add(candidate);
            }

            return names;
        }

        private static string BuildPythonContextLabel(string pythonResultBlock)
        {
            if (string.IsNullOrWhiteSpace(pythonResultBlock))
                return string.Empty;

            return pythonResultBlock
                .Replace("[[PYTHON RESULT]]", "[[PYTHON RESULT]]", StringComparison.Ordinal)
                .Replace("[[END PYTHON RESULT]]", "[[END PYTHON RESULT]]", StringComparison.Ordinal);
        }

        /// <summary>
        /// Artifact-specific task type boost used in place of GetTaskTypeBoost when
        /// IsArtifactCanvasRequest is true.  Eliminates the direct contradiction that
        /// occurs when Research/Analysis boosts tell models "Do NOT use code fences"
        /// while the canvas boost tells them "wrap in a code fence."
        /// </summary>
        private static string BuildArtifactTaskTypeBoost(CouncilRole role, CouncilRunContext runContext)
        {
            int viewportWidth = runContext.CanvasViewportWidth;
            return role switch
            {
                CouncilRole.Architect =>
                    "\n[VISUAL ARTIFACT TASK] Plan a visual or interactive artifact — not a prose document and not a multi-file project. " +
                    "Name the artifact type, required source format, hard constraints, explicit must-include items, Builder instruction, and acceptance tests. " +
                    "Do NOT describe function signatures, class hierarchies, server-side code, or generic build steps. " +
                    "The handoff must tell Builder to return one complete renderable source only, with no explanatory prose. " +
                    "'Output the complete implementation as a single ```html … ``` code block with no text outside the fence.'",
                CouncilRole.Builder =>
                    "\n[VISUAL ARTIFACT TASK] This response is a renderable artifact — not a prose explanation and not a research document. " +
                    "ANTI-DRIFT: You are the BUILDER — do NOT output a numbered plan or step list before the artifact. " +
                    "Code fences ARE required and mandatory. " +
                    "Your ENTIRE response must be the artifact wrapped in exactly one correctly tagged code fence such as ```html, ```svg, ```python, ```javascript, or ```markdown. " +
                    "Do NOT output any text, explanation, or commentary before the opening fence or after the closing fence. " +
                    "For dynamic visuals, simulations, calculators, animated scenes, or artifacts with changing numeric readouts, use ```html with inline CSS/JS; standalone ```svg is only for static or explicitly SVG deliverables. " +
                    $"SIZING: The canvas viewport is currently {viewportWidth}px wide and user-resizable — use width:100% and box-sizing:border-box throughout; " +
                    "SVG root must have viewBox and width='100%'; HTML5 Canvas must size from container clientWidth. " +
                    "THEME: the host pane is dark (#171615) — always set an explicit background-color and text color on body; never rely on browser defaults.",
                CouncilRole.Critic =>
                    "\n[VISUAL ARTIFACT TASK] The Builder output must be a renderable artifact — not prose text. " +
                    "The pipeline may strip markdown fences before rendering, so accept raw complete HTML/SVG/JS/Python/Markdown source. " +
                    "Do NOT penalize code output or suggest prose alternatives. " +
                    "Flag: external CDN library imports, incomplete implementation, prose instead of artifact source, multiple alternative artifacts, " +
                    "or illegible styling (no explicit body background/text color, base font under 12px, fixed widths wider than the canvas pane). " +
                    "For simulation, animation, calculators, time/state changes, or dynamic readouts, flag standalone static SVG as a format mismatch unless the user explicitly requested SVG.",
                _ => string.Empty
            };
        }

        /// <summary>
        /// Builds a structured anchor block that is injected as the FIRST item in the Builder's
        /// payload. Because models read context sequentially, placing the output-format contract
        /// at the very start of the message — before the architect plan and user prompt — makes
        /// the format constraint the dominant context frame rather than a late footnote.
        ///
        /// This is the recommended pipeline improvement: it reduces "prose drift" in models that
        /// correctly understand the architect plan but forget the output format by the time they
        /// reach the implementation phase. The block has zero cost when canvas is not active.
        /// </summary>
        private static bool HintPrefersStandaloneSvg(string hint)
        {
            if (string.IsNullOrWhiteSpace(hint))
                return false;

            return hint.Contains("Prefer inline SVG", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("complete inline SVG", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("single SVG", StringComparison.OrdinalIgnoreCase)
                || hint.Contains("one complete SVG", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCanvasArtifactAnchorBlock(CouncilRunContext runContext)
        {
            string hint = runContext.PreferredArtifactFormatHint ?? string.Empty;
            bool standaloneSvgHint = HintPrefersStandaloneSvg(hint);

            // Infer a human-readable artifact type label from the format hint
            string artifactLabel = runContext.ExistingCanvasArtifactKind == ArtifactKind.Document ? "Markdown document"
                : runContext.ExistingCanvasArtifactKind == ArtifactKind.InteractiveJavaScript ? "interactive JavaScript artifact"
                : standaloneSvgHint ? "SVG diagram / vector graphic"
                : hint.Contains("Python", StringComparison.OrdinalIgnoreCase) ? "Python chart (sandbox capture)"
                : hint.Contains("<table>", StringComparison.OrdinalIgnoreCase) || hint.Contains("table", StringComparison.OrdinalIgnoreCase) ? "HTML data table / structured document"
                : hint.Contains("input field", StringComparison.OrdinalIgnoreCase) || hint.Contains("calculator", StringComparison.OrdinalIgnoreCase) ? "interactive HTML tool / calculator"
                : "self-contained HTML artifact";

            string fence = runContext.ExistingCanvasArtifactKind == ArtifactKind.Document ? "```markdown"
                : runContext.ExistingCanvasArtifactKind == ArtifactKind.InteractiveJavaScript ? "```javascript"
                : standaloneSvgHint ? "```svg"
                : hint.Contains("Python", StringComparison.OrdinalIgnoreCase) ? "```python"
                : "```html";

            var block = new System.Text.StringBuilder();
            block.AppendLine("[[PROJECT CANVAS ARTIFACT]]");
            block.AppendLine($"Artifact type : {artifactLabel}");
            block.AppendLine($"Output format : Single {fence} … ``` code fence — NOTHING outside it");
            block.AppendLine("Offline-safe  : No CDN links, no external <script> tags, no network requests");
            block.AppendLine("Completeness  : Fully functional — no placeholders, no TODO comments");
            block.AppendLine("Constraint    : Do NOT write any text before the opening fence or after the closing fence");
            block.AppendLine($"Viewport      : {runContext.CanvasViewportWidth}px wide × {runContext.CanvasViewportHeight}px tall, user-resizable — use width:100% / box-sizing:border-box; SVG needs viewBox + width='100%'; Canvas sizes from clientWidth");
            block.AppendLine("Theme         : Host pane is dark #171615 — set explicit body background + text color (dark: bg #171615, text #D1D3DF, accent #B8924A; or light card: bg #ffffff, text #1a1a1a); base font ≥14px 'Segoe UI'");
            return block.ToString().TrimEnd();
        }

        private static string GetTaskTypeBoost(CouncilTaskType taskType, CouncilRole role)
        {
            return taskType switch
            {
                CouncilTaskType.Coding => role switch
                {
                    CouncilRole.Architect =>
                        "\n[CODING TASK] For every step state: function/component name, input parameters, " +
                        "return value, and what it does in one sentence. Do NOT use words like handle, manage, " +
                        "process, or deal with — describe the exact operation.",
                    CouncilRole.Builder =>
                        "\n[CODING TASK] Write real, complete, executable code only. " +
                        "ANTI-DRIFT: You are the BUILDER — do NOT output a numbered plan, step list, or outline. Start directly with code. " +
                        "Never use placeholders like '// TODO', '...', 'pass', or 'implement here'. " +
                        "Declare all variables before use. Never reference a function that is not defined. " +
                        "Keep each function small and single-purpose. " +
                        "If the request asks for a visual or interactive deliverable, prefer a complete self-contained HTML/SVG artifact or chart-producing implementation instead of prose. " +
                        "Before writing each function, mentally trace its execution path with representative inputs " +
                        "to catch edge cases, off-by-one errors, and type mismatches. " +
                        "Output code only — no explanations, no greetings, no numbered lists, no commentary.",
                    CouncilRole.Critic =>
                        "\n[CODING TASK] Perform a code review. Check: (1) syntax errors, (2) every referenced " +
                        "function exists and is spelled consistently, (3) every variable is defined before use, " +
                        "(4) each function's logic matches the Architect step, (5) visual artifact requests produce a concrete renderable implementation rather than prose, (6) the combined output fulfills " +
                        "the user request. Do NOT rewrite code — output ONLY the numbered findings list.",
                    _ => ""
                },

                CouncilTaskType.Research => role switch
                {
                    CouncilRole.Architect =>
                        "\n[RESEARCH TASK] Each step is one subtopic or question to address. " +
                        "State what information is needed and why. Use ONLY the provided [[PROJECT KNOWLEDGE BASE]] when available. " +
                        "Do NOT instruct opening files, OCR tools, external viewers, or external data sources.",
                    CouncilRole.Builder =>
                        "\n[RESEARCH TASK] Write a well-organized, thorough research document in plain prose. " +
                        "ANTI-DRIFT: You are the BUILDER — do NOT output a numbered plan or step list. Start directly with the research content. " +
                        "Cover every subtopic from the plan in order. Use paragraphs, headings, and clear language. " +
                        "For each subtopic: state the key finding, cite the supporting evidence from the source material, " +
                        "explain the significance, then transition to the next subtopic. " +
                        "Use ONLY the provided [[PROJECT KNOWLEDGE BASE]] and prompt context. Do NOT invent external sources. " +
                        "Do NOT write any code, scripts, or programs. Do NOT use code fences. " +
                        "Output only the research text — no code blocks, no programming constructs.",
                    CouncilRole.Critic =>
                        "\n[RESEARCH TASK] Review the research output for accuracy, completeness, and clarity. " +
                        "Check for gaps, inaccuracies, missing subtopics, and whether the response answers the original question. " +
                        "Do NOT request code or suggest code implementations.",
                    _ => ""
                },

                CouncilTaskType.Analysis => role switch
                {
                    CouncilRole.Architect =>
                        "\n[ANALYSIS TASK] Define the analytical framework and dimensions for evaluation. " +
                        "Use ONLY the provided context and [[PROJECT KNOWLEDGE BASE]] when available. " +
                        "Do NOT plan any code or scripts.",
                    CouncilRole.Builder =>
                        "\n[ANALYSIS TASK] Write a structured analytical document in plain prose. " +
                        "ANTI-DRIFT: You are the BUILDER — do NOT output a numbered plan or step list. Start directly with the analysis content. " +
                        "Apply the framework rigorously. For each analytical dimension: " +
                        "(1) state the evaluation criterion, (2) present the relevant evidence, " +
                        "(3) reason through what the evidence implies, (4) state the conclusion for that dimension. " +
                        "Support every claim with reasoning. " +
                        "Use ONLY the provided context and [[PROJECT KNOWLEDGE BASE]]; do not invent external sources. " +
                        "Do NOT write any code, scripts, or programs. Do NOT use code fences. " +
                        "Output only the analysis text — no code blocks, no programming constructs.",
                    CouncilRole.Critic =>
                        "\n[ANALYSIS TASK] Review the analysis for logical rigor and completeness. " +
                        "Challenge weak reasoning, flag unsupported conclusions, and assess completeness. " +
                        "Do NOT request code or suggest code implementations.",
                    _ => ""
                },

                CouncilTaskType.Document => role switch
                {
                    CouncilRole.Architect =>
                        "\n[DOCUMENT TASK] The user has uploaded document(s) whose FULL TEXT is provided in [[DOCUMENT CONTENT]]. " +
                        "Your plan must describe specific operations on the provided content — for example: " +
                        "'Extract the key findings from the introduction section', 'Synthesize the conclusions from the final paragraphs'. " +
                        "Each step must reference what part of the content to use and what to produce. " +
                        "You MUST NOT produce a generic how-to guide about document handling. " +
                        "You MUST NOT include steps about opening, reading, loading, parsing, or accessing files. " +
                        "Producing a procedural guide instead of a content-specific plan is a CRITICAL ROLE FAILURE.",
                    CouncilRole.Builder =>
                        "\n[DOCUMENT TASK] The document text is provided in [[DOCUMENT CONTENT]] in your payload. " +
                        "ANTI-DRIFT: You are the BUILDER — do NOT output a numbered plan or step list. Start directly with your content. " +
                        "Read and understand the document, then carry out the user's request by writing a genuine summary/answer " +
                        "in your OWN words — synthesize and explain the content. Do NOT copy, paste, or transcribe sentences or " +
                        "long passages from the source, and do not just stitch together fragments of it. " +
                        "Write your response as prose or structured content — NOT as code unless explicitly requested. " +
                        "You MUST NOT claim the document was not provided, cannot be accessed, or is unavailable. " +
                        "The full text IS in your payload. Any claim otherwise is a hallucination. " +
                        "Keep every statement faithful to the document and do not fabricate content not present in it.",
                    CouncilRole.Critic =>
                        "\n[DOCUMENT TASK] The document text is provided in [[DOCUMENT CONTENT]] in your payload. " +
                        "Verify the Builder's output in this priority order: " +
                        "(1) No fabricated facts absent from the source document — this is CRITICAL. " +
                        "(2) Output fulfills the user's original instruction. " +
                        "(3) All Architect plan steps are addressed. " +
                        "(4) No significant relevant content from the document was omitted. " +
                        "Hallucination of document content overrides all other findings in severity.",
                    _ => ""
                },

                // General tasks default to prose
                _ => role switch
                {
                    CouncilRole.Builder =>
                        "\n[GENERAL TASK] Write your response in clear, well-organized prose. " +
                        "ANTI-DRIFT: You are the BUILDER — do NOT output a numbered plan or step list. Write your response content directly. " +
                        "Do NOT write code unless the user explicitly asked for code. " +
                        "For each point you make, ensure it directly addresses the user's question. " +
                        "For calculations, show your work step by step with clear formulas and verify each result. " +
                        "Use paragraphs and headings as appropriate.",
                    _ => ""
                }
            };
        }

        private static string GetCalculationBoost(CouncilRole role, CouncilTaskType taskType = CouncilTaskType.Coding)
        {
            return role switch
            {
                CouncilRole.Architect =>
                    "\n[CALCULATION TASK] This task involves domain-specific calculations, formulas, or unit conversions. " +
                    "For EVERY calculation the program must perform, you MUST include ALL of the following in your plan step: " +
                    "(a) The exact formula in plain mathematical notation (e.g., volume_liters = (rainfall_mm / 1000) * area_m2 * 1000). " +
                    "(b) Every unit involved on both the input side and the output side. " +
                    "(c) Every unit conversion required with its exact conversion factor and direction " +
                    "(e.g., 'millimeters to meters: divide by 1000', 'cubic meters to liters: multiply by 1000'). " +
                    "(d) The expected realistic output range for typical inputs " +
                    "(e.g., 'for 50mm rainfall on a 100m² roof, expect ~5000 liters'). " +
                    "If you cannot determine the exact formula for a required calculation, explicitly flag that step as " +
                    "'FORMULA UNKNOWN — Builder must not guess' rather than leaving it undefined. " +
                    "A missing or vague formula will always cause the Builder to invent an incorrect implementation.",

                CouncilRole.Builder when taskType == CouncilTaskType.Coding =>
                    "\n[CALCULATION TASK] This task involves formulas and unit conversions. Follow these rules strictly: " +
                    "(1) Every formula you implement MUST match EXACTLY what the Architect specified, including all " +
                    "conversion factors in the correct order and direction. Do not invent or modify any formula. " +
                    "(2) On every line that performs a unit conversion, add an inline comment stating " +
                    "what unit is coming in and what unit is going out (e.g., '# mm -> m: divide by 1000'). " +
                    "(3) After each major calculation, add a sanity-check that compares the result against the " +
                    "Architect's stated expected range. If the result falls outside that range, print a warning: " +
                    "'WARNING: result {value} is outside expected range {range}, check inputs or formula'. " +
                    "(4) If the Architect flagged a step as FORMULA UNKNOWN, output a clear error message for that step " +
                    "instead of guessing a formula.",

                CouncilRole.Builder =>
                    "\n[CALCULATION TASK] This task involves calculations, formulas, or unit conversions. " +
                    "Present ALL mathematical work in clear natural language — NOT as code, pseudo-code, or programming syntax. " +
                    "For every calculation: " +
                    "(1) State the formula in plain notation (e.g., 'Volume = Rainfall depth × Roof area'). " +
                    "(2) Substitute the actual numbers with units (e.g., 'Volume = 0.05 m × 100 m² = 5 m³'). " +
                    "(3) Show each unit conversion as a sentence (e.g., 'Converting cubic meters to liters: 5 m³ × 1000 = 5,000 liters'). " +
                    "(4) State the final answer clearly with correct units. " +
                    "Do NOT use variable assignments (x = ...), function definitions, print statements, " +
                    "inline code comments, or any programming constructs. Write as you would in a math textbook.",

                CouncilRole.Critic =>
                    "\n[CALCULATION TASK] This task involves domain-specific formulas and unit conversions. " +
                    "In addition to standard code review, you MUST perform these two checks: " +
                    "(A) FORMULA TRACE: For each formula in the Builder's code, pick the example input values from the " +
                    "user's prompt (or use simple representative values if none given). Manually compute what the output " +
                    "should be step by step using the Architect's specified formula. Then determine what the Builder's " +
                    "code would actually produce given the same inputs. If the results differ, report this as a CRITICAL " +
                    "finding — the code is mathematically wrong even if it runs without errors. " +
                    "(B) UNIT CONVERSION AUDIT: For every unit conversion in the code, verify: " +
                    "(i) the conversion factor is correct, (ii) it is applied in the correct direction " +
                    "(e.g., mm→m is divide by 1000, NOT multiply), (iii) no conversion step is missing. " +
                    "A syntactically correct program that applies a conversion in the wrong direction will run " +
                    "perfectly and produce completely wrong output. Flag each incorrect or missing conversion explicitly.",

                _ => ""
            };
        }

        private void UpdateTaskTypeBadge(CouncilTaskType taskType)
        {
            TaskTypeBadgeText.Text = taskType switch
            {
                CouncilTaskType.Coding => "Coding Task",
                CouncilTaskType.Research => "Research Task",
                CouncilTaskType.Analysis => "Analysis Task",
                CouncilTaskType.Document => "Document Task",
                _ => "General Task"
            };
        }

        private void UpdateStageIndicator(CouncilRole? activeRole, bool architectDone, bool builderDone, bool criticDone)
        {
            // Record timing when a stage completes
            if (_activeStageRole.HasValue && _activeStageRole != activeRole && _stageStopwatch.IsRunning)
            {
                double elapsed = _stageStopwatch.Elapsed.TotalSeconds;
                switch (_activeStageRole.Value)
                {
                    case CouncilRole.Architect: _lastArchitectDuration = elapsed; break;
                    case CouncilRole.Builder: _lastBuilderDuration = elapsed; break;
                    case CouncilRole.Critic: _lastCriticDuration = elapsed; break;
                }
                _stageStopwatch.Reset();
            }

            // Start timing for new active stage
            if (activeRole.HasValue && activeRole != _activeStageRole)
            {
                _stageStopwatch.Restart();
            }
            _activeStageRole = activeRole;

            ApplyStageVisual(ArchitectStageIndicator, activeRole == CouncilRole.Architect, architectDone);
            ApplyStageVisual(BuilderStageIndicator, activeRole == CouncilRole.Builder, builderDone);
            ApplyStageVisual(CriticStageIndicator, activeRole == CouncilRole.Critic, criticDone);

            string architectLabel = activeRole == CouncilRole.Architect ? "Running" : architectDone ? $"Done {FormatStageDuration(_lastArchitectDuration)}" : "Idle";
            string builderLabel = activeRole == CouncilRole.Builder ? "Running" : builderDone ? $"Done {FormatStageDuration(_lastBuilderDuration)}" : "Idle";
            string criticLabel = activeRole == CouncilRole.Critic ? "Running" : criticDone ? $"Done {FormatStageDuration(_lastCriticDuration)}" : "Idle";

            ArchitectStageText.Text = $"Architect · {architectLabel}";
            BuilderStageText.Text = $"Builder · {builderLabel}";
            CriticStageText.Text = $"Critic · {criticLabel}";

            if (activeRole == CouncilRole.Architect)
                PublishCouncilPetStatus("Architect", "Planning the handoff.");
            else if (activeRole == CouncilRole.Builder)
                PublishCouncilPetStatus("Builder", _canvasArtifact.SupportsPreview ? "Updating Project Canvas." : "Building the answer.");
            else if (activeRole == CouncilRole.Critic)
                PublishCouncilPetStatus("Critic", "Checking the result.");
            else if (architectDone && builderDone && criticDone)
                PublishCouncilPetStatus("Council", "Run complete.");
        }

        private static string FormatStageDuration(double seconds)
        {
            if (seconds <= 0) return "";
            return seconds < 60 ? $"({seconds:F0}s)" : $"({seconds / 60:F1}m)";
        }

        private static void ApplyStageVisual(Border border, bool isActive, bool isDone)
        {
            if (isActive)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(255, 59, 59));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(255, 59, 59));
                return;
            }

            if (isDone)
            {
                border.Background = new SolidColorBrush(Color.FromRgb(48, 48, 46));
                border.BorderBrush = new SolidColorBrush(Color.FromRgb(64, 68, 75));
                return;
            }

            border.Background = new SolidColorBrush(Color.FromRgb(38, 38, 36));
            border.BorderBrush = new SolidColorBrush(Color.FromRgb(58, 58, 56));
        }

        private static string BuildPipelineStateHeader(string architectSummary, string builderSummary)
        {
            // Compact header — only include when there's actual state to convey
            if (string.IsNullOrWhiteSpace(architectSummary) && string.IsNullOrWhiteSpace(builderSummary))
                return "";

            var sb = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(architectSummary))
            {
                sb.AppendLine("[Architect Summary]");
                sb.AppendLine(architectSummary);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(builderSummary))
            {
                sb.AppendLine("[Builder Summary]");
                sb.AppendLine(builderSummary);
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static string BuildArchitectContract(CouncilTaskType taskType, bool isArtifactCanvasRequest = false, bool isWorkspaceTask = false)
        {
            if (isWorkspaceTask)
                return "\n[STRUCTURED OUTPUT CONTRACT] Output exactly this connected-codebase handoff shape, with concise values and no prose before or after:\n" +
                       "ARCHITECT_HANDOFF\n" +
                       "artifact_type: connected_codebase_patch\n" +
                       "output_target: ProjectCanvasPatchReview\n" +
                       "required_output_format: valid [[AXIOM_CODEBASE_PATCH]] envelope with relative FILE paths, ACTION create/replace, complete fenced file content, and [[END FILE]] per file\n" +
                       "requirements:\n" +
                       "- explicit user requirements as implementation outcomes\n" +
                       "constraints:\n" +
                       "- preserve unrelated code and connected workspace paths\n" +
                       "- no standalone canvas artifact unless the user explicitly requested a new canvas-only artifact outside the connected codebase\n" +
                       "builder_instruction:\n" +
                       "Return only a valid [[AXIOM_CODEBASE_PATCH]] envelope for the connected workspace. Do not return standalone HTML/code outside the patch envelope.\n" +
                       "acceptance_tests:\n" +
                       "- patch targets the prompt-named or connected file path\n" +
                       "- changed file content is complete and coherent, not a fragment\n" +
                       "- requested controls/functions/behaviors are wired to working logic\n" +
                       "- no unrelated files or unrelated behavior are changed\n" +
                       "END_ARCHITECT_HANDOFF\n" +
                       $"End with '{ArchitectCompletionMarker}' on its own line.";

            if (isArtifactCanvasRequest)
                return "\n[STRUCTURED OUTPUT CONTRACT] Output exactly this artifact handoff shape, with concise values and no prose before or after:\n" +
                       "ARCHITECT_HANDOFF\n" +
                       "artifact_type: standalone_html\n" +
                       "output_target: ProjectCanvas\n" +
                       "required_output_format: one complete self-contained offline-renderable artifact source\n" +
                       "requirements:\n" +
                       "- explicit user requirements as visible or interactive outcomes\n" +
                       "constraints:\n" +
                       "- embedded CSS\n" +
                       "- embedded JavaScript when interactivity/animation is requested\n" +
                       "- no external libraries or remote assets\n" +
                       "- one complete offline-renderable source\n" +
                       "- no prose in Builder output\n" +
                       "builder_instruction:\n" +
                       "Return only one complete standalone HTML document unless the user explicitly required a different renderable artifact format.\n" +
                       "acceptance_tests:\n" +
                       "- contains <!DOCTYPE html>\n" +
                       "- contains <style>\n" +
                       "- contains <script> when interactivity is requested\n" +
                       "- has no external script/link/CDN references\n" +
                       "- renders the requested UI elements\n" +
                       "- requested controls change visible state\n" +
                       "END_ARCHITECT_HANDOFF\n" +
                       $"End with '{ArchitectCompletionMarker}' on its own line.";

            if (taskType == CouncilTaskType.Coding)
                return "\n[STRUCTURED OUTPUT CONTRACT] Output only a numbered plan. Every line must follow 'N. concrete implementation step'. " +
                           "When the request implies a renderable artifact, explicitly plan toward a single HTML, SVG, or chart implementation that will render in Project Canvas. " +
                           "No prose paragraphs, no code, no markdown fences, and no echoed payload labels. " +
                       $"End with '{ArchitectCompletionMarker}' on its own line.";

            if (taskType == CouncilTaskType.Document)
                return "\n[STRUCTURED OUTPUT CONTRACT] Output only a numbered plan where each step describes a specific operation on the document content " +
                       "provided in [[DOCUMENT CONTENT]]. Each step must name what content to extract, analyze, or synthesize. " +
                       "Do NOT include steps about opening files, reading documents, or using tools. No prose paragraphs, no code, and no echoed payload labels. " +
                       $"End with '{ArchitectCompletionMarker}' on its own line.";

            return "\n[STRUCTURED OUTPUT CONTRACT] Output only a numbered plan where each step names a CONTENT SECTION or SUBTOPIC to cover " +
                   "using the source material already provided in [[PROJECT KNOWLEDGE BASE]]. " +
                   "Do NOT include procedural steps like opening files, reading documents, extracting text, or using tools — " +
                   "the document text is already provided. Plan what to WRITE, not how to READ. No prose paragraphs, no code, and no echoed payload labels. " +
                   $"End with '{ArchitectCompletionMarker}' on its own line.";
        }

        private static string BuildBuilderContract(CouncilTaskType taskType, bool isArtifactCanvasRequest = false)
        {
            const string antiEcho = "Do NOT echo, restate, or reproduce any input labels, headers, [[BLOCK]] markers, or pipeline metadata in your output. " +
                "Start directly with your implementation content.";

            // An artifact-canvas request is a code-fenced renderable deliverable even when DetectTaskType
            // classified it as Research/Analysis/General (e.g. "visualize the analysis", "make a report
            // dashboard"). It MUST use the code/artifact contract — the prose contract below would tell it
            // "no code fences", which fights the artifact boost and makes the Builder emit a plan/prose
            // instead of a renderable artifact.
            if (taskType == CouncilTaskType.Coding || isArtifactCanvasRequest)
                return $"\n[STRUCTURED OUTPUT CONTRACT] Output exactly one executable implementation in markdown code fences with no prose before or after. " +
                       $"If the user asked for a visual artifact, output one complete self-contained artifact implementation such as HTML, SVG, or chart-producing code rather than prose. " +
                       $"For C# requests, output one complete compilable .cs file; for test requests, include executable tests/assertions in the implementation deliverable. " +
                       $"Do not output architecture diagrams, analysis documents, or explanations as substitutes for code. " +
                       $"No summaries, no explanations, no bullet lists, and no duplicate alternative versions. {antiEcho} " +
                       $"End with '{BuilderCompletionMarker}' on its own line.";

            if (taskType == CouncilTaskType.Document)
                return $"\n[STRUCTURED OUTPUT CONTRACT] Output the requested content derived from the provided document text. " +
                       $"Write prose or structured content only — no code unless explicitly requested. " +
                       $"Every claim must be grounded in the provided document text. No generic disclaimers and no tool/file-operation narration. {antiEcho} " +
                       $"End with '{BuilderCompletionMarker}' on its own line.";

            return $"\n[STRUCTURED OUTPUT CONTRACT] Output task-aligned prose only — no code fences, no source code, no variable assignments, " +
                   $"no function definitions, and no programming syntax unless the user explicitly requested code. " +
                   $"For calculations, use plain mathematical notation and natural language (e.g., 'Volume = 5 × 100 = 500 liters'). {antiEcho} " +
                   $"End with '{BuilderCompletionMarker}' on its own line.";
        }

        private static string BuildCriticContract(CouncilTaskType taskType, bool isArtifactCanvasRequest = false)
        {
            if (isArtifactCanvasRequest)
                return "\n[OUTPUT CONTRACT] Output exactly this evidence-backed artifact review shape. Do not output vague warnings or unsupported speculation:\n" +
                       "CRITIC_HANDOFF\n" +
                       "requirement_checks:\n" +
                       "- requirement: embedded CSS\n" +
                       "  status: pass/fail\n" +
                       "  evidence: exact tag, selector, snippet, or sandbox field checked\n" +
                       "- requirement: no external libraries\n" +
                       "  status: pass/fail\n" +
                       "  evidence: cite external URL found, or cite sandbox external_dependencies_found:false\n" +
                       "issues:\n" +
                       "- severity: low|medium|high|critical\n" +
                       "  evidence: exact source or sandbox evidence\n" +
                       "  exact_builder_fix: exact change Builder should make\n" +
                       "verified_passes:\n" +
                       "- evidence-backed pass item\n" +
                       "overall: pass/fail\n" +
                       "END_CRITIC_HANDOFF\n" +
                       "If all current sandbox checks pass and you have no exact failing evidence, set overall: pass and leave issues empty. " +
                       "Do not fail raw HTML merely because markdown fences are absent. " +
                       $"End with '{CriticCompletionMarker}' on its own line.";

            if (taskType == CouncilTaskType.Coding)
                return "\n[OUTPUT CONTRACT] Output EITHER a numbered findings list where every item contains: Location, Severity (LOW|MEDIUM|HIGH|CRITICAL), Problem, Fix; " +
                       "Only report findings that are actionable. Do not report subjective preferences unless they materially affect correctness, rendering, usability, or the user's stated goal. " +
                       "Do not output thinking, hidden reasoning, analysis notes, scratch work, or deliberation. " +
                       "OR a single line 'No issues found.'. " +
                       $"End with '{CriticCompletionMarker}' on its own line.";

            if (taskType == CouncilTaskType.Document)
                return "\n[OUTPUT CONTRACT] Output EITHER a numbered findings list where each item contains: " +
                       "Reference (section/paragraph of document or builder output), Issue (with severity), Fix; " +
                       "OR a single line 'No issues found.'. " +
                       "Severity levels: CRITICAL (hallucinated content not in document), HIGH (user request not fulfilled), " +
                       "MEDIUM (plan step missed), LOW (minor omission). " +
                       "Do not output thinking, hidden reasoning, analysis notes, scratch work, or deliberation. " +
                       $"End with '{CriticCompletionMarker}' on its own line.";

            return "\n[OUTPUT CONTRACT] Output EITHER a numbered findings list where each item contains: Reference (topic/section/step), Severity (LOW|MEDIUM|HIGH|CRITICAL), Issue, Fix; " +
                   "Only report actionable issues. Do not report subjective preferences unless they materially affect correctness, usability, or the user's stated goal. " +
                   "Do not output thinking, hidden reasoning, analysis notes, scratch work, or deliberation. " +
                   "OR a single line 'No issues found.'. " +
                   $"End with '{CriticCompletionMarker}' on its own line.";
        }

        private static bool TryExtractWithMarker(string text, string marker, out string cleaned)
        {
            cleaned = text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            int idx = text.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0)
            {
                return false;
            }

            cleaned = text[..idx].Trim();
            return true;
        }

        private static bool IsArchitectArtifactHandoff(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("ARCHITECT_HANDOFF", StringComparison.OrdinalIgnoreCase)
                && text.Contains("END_ARCHITECT_HANDOFF", StringComparison.OrdinalIgnoreCase)
                && text.Contains("artifact_type:", StringComparison.OrdinalIgnoreCase)
                && text.Contains("builder_instruction:", StringComparison.OrdinalIgnoreCase)
                && text.Contains("acceptance_tests:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCriticArtifactHandoff(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("CRITIC_HANDOFF", StringComparison.OrdinalIgnoreCase)
                && text.Contains("END_CRITIC_HANDOFF", StringComparison.OrdinalIgnoreCase)
                && text.Contains("overall:", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyCodeOutput(string output)
            => DetectCodeOutput(output).IsCode;

        private readonly record struct CodeOutputDetection(bool IsCode, string Language, int ConfidenceScore, bool HasTypedFence);

        private static CodeOutputDetection DetectCodeOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return new CodeOutputDetection(false, "markdown", 0, false);

            string trimmed = output.Trim();
            string candidate = trimmed;
            string? fencedLanguage = null;
            Match typedFence = BuilderTypedCodeFenceRegex.Match(trimmed);
            if (typedFence.Success)
            {
                fencedLanguage = typedFence.Groups["language"].Value.Trim().ToLowerInvariant();
                candidate = typedFence.Groups["code"].Value.Trim();
            }

            string language = NormalizeCodeLanguage(fencedLanguage) ?? DetectLanguage(candidate);
            bool knownCodeFence = fencedLanguage is
                "c" or "cpp" or "c++" or "csharp" or "cs" or "java" or "kotlin" or
                "python" or "py" or "javascript" or "js" or "typescript" or "ts" or
                "html" or "css" or "scss" or "sql" or "bash" or "sh" or "powershell" or "ps1" or
                "rust" or "go" or "ruby" or "php" or "swift" or "dart" or "lua" or "r" or
                "json" or "xml" or "yaml" or "yml" or "toml" or "dockerfile";

            int score = knownCodeFence ? 6 : 0;
            ArtifactKind artifactKind = ArtifactRenderService.DetectForCanvas(trimmed, null).Kind;
            if (artifactKind is ArtifactKind.Html or ArtifactKind.Svg or ArtifactKind.Chart or ArtifactKind.InteractiveJavaScript)
                score = Math.Max(score, 7);

            if (Regex.IsMatch(candidate, @"(?m)^\s*(?:def|class)\s+[A-Za-z_]\w*|^\s*(?:from\s+\S+\s+import|import\s+\S+)", RegexOptions.IgnoreCase)) score += 3;
            if (Regex.IsMatch(candidate, @"(?m)^\s*(?:public|private|protected|internal|static)\s+(?:class|struct|interface|enum|void|[A-Za-z_]\w*[<\[]?)", RegexOptions.IgnoreCase)) score += 3;
            if (Regex.IsMatch(candidate, @"\b(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*=|\bfunction\s+[A-Za-z_$][\w$]*\s*\(|=>", RegexOptions.IgnoreCase)) score += 3;
            if (candidate.Contains("{", StringComparison.Ordinal) && candidate.Contains("}", StringComparison.Ordinal)) score += 1;
            if (candidate.Contains(";", StringComparison.Ordinal) && candidate.Contains("(", StringComparison.Ordinal)) score += 1;
            if (Regex.IsMatch(candidate, @"(?is)^\s*(?:select\s+.+\s+from\s+|insert\s+into\s+|update\s+\S+\s+set\s+|create\s+(?:table|view|index)\s+)", RegexOptions.IgnoreCase)) score += 5;
            if (Regex.IsMatch(candidate, @"(?m)^\s*(?:#!\s*/|param\s*\(|function\s+\w+[\s\{]|\$[A-Za-z_]\w*\s*=)", RegexOptions.IgnoreCase)) score += 3;
            if (Regex.IsMatch(candidate, "(?m)^\\s*(?:#include\\s*[<\"]|package\\s+\\w+|fn\\s+\\w+\\s*\\(|func\\s+\\w+\\s*\\()", RegexOptions.IgnoreCase)) score += 4;
            if (Regex.IsMatch(candidate, "(?m)^\\s*(?:[.#]?[A-Za-z][\\w\\s>+~:#.\\[\\]=\"'-]*)\\s*\\{\\s*$") && candidate.Contains(':')) score += 3;
            if (Regex.IsMatch(candidate, @"(?is)^\s*<\?xml\b|^\s*<[A-Za-z][^>]*>[\s\S]*</[A-Za-z][^>]*>\s*$")) score += 4;

            if ((candidate.StartsWith('{') && candidate.EndsWith('}'))
                || (candidate.StartsWith('[') && candidate.EndsWith(']')))
            {
                try
                {
                    using JsonDocument _ = JsonDocument.Parse(candidate);
                    score += 5;
                    language = "json";
                }
                catch (JsonException)
                {
                }
            }

            int codeLikeLines = candidate.Split('\n').Count(line =>
                Regex.IsMatch(line, @"^\s*(?:[A-Za-z_$][\w$]*\s*=|(?:if|for|while|switch|try|catch)\s*[\(]|return\b|}\s*;?)", RegexOptions.IgnoreCase));
            if (codeLikeLines >= 2) score += 2;

            return new CodeOutputDetection(score >= 4, language, score, typedFence.Success);
        }

        private static string? NormalizeCodeLanguage(string? language)
        {
            return language?.Trim().ToLowerInvariant() switch
            {
                "cs" or "csharp" => "c#",
                "py" or "python" => "python",
                "js" or "javascript" => "javascript",
                "ts" or "typescript" => "typescript",
                "cpp" or "c++" => "cpp",
                "sh" or "bash" => "bash",
                "ps1" or "powershell" => "powershell",
                "yml" or "yaml" => "yaml",
                "md" or "markdown" or "text" or "txt" => null,
                { Length: > 0 } value => value,
                _ => null
            };
        }

        private static string GetRoleCompletionMarker(CouncilRole role)
        {
            return role switch
            {
                CouncilRole.Architect => ArchitectCompletionMarker,
                CouncilRole.Builder => BuilderCompletionMarker,
                CouncilRole.Critic => CriticCompletionMarker,
                _ => string.Empty
            };
        }

        private static bool ArchitectHasRoleDrift(string output)
        {
            if (string.IsNullOrWhiteSpace(output)) return true;
            if (IsArchitectArtifactHandoff(output)) return false;
            string lower = output.ToLowerInvariant();
            return lower.Contains("```")
                   || lower.Contains("public class ")
                   || lower.Contains("def ")
                   || lower.Contains("import ")
                   || lower.Contains("using ") && lower.Contains("namespace ");
        }

        private static bool ArchitectHasDetachedFileOperationSteps(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string lower = output.ToLowerInvariant();
            string[] forbidden =
            [
                "open the attached", "open the file", "open the pdf", "pdf viewer",
                "optical character recognition", "ocr",
                "extract text from the pdf", "read the file", "load the file",
                "download the file", "install", "use an external tool"
            ];

            return forbidden.Any(f => lower.Contains(f));
        }

        private static string SanitizeArchitectPlan(string plan, CouncilTaskType taskType)
        {
            if (taskType == CouncilTaskType.Coding || string.IsNullOrWhiteSpace(plan))
                return plan;

            string[] forbiddenPatterns =
            [
                "open the", "read the file", "read the document", "load the file",
                "extract text", "pdf viewer", "ocr", "optical character recognition",
                "download the", "install ", "use an external", "open a ",
                "launch ", "import the file", "parse the pdf", "parse the file",
                "scan the document", "use a tool", "using software",
                "access the file", "retrieve the file",
                "utilize natural language processing", "use natural language processing",
                "review the generated summary for accuracy",
                "check for any missing information",
                "format the summary into", "finalize the summary",
                "initialize", "summarization tool", "processing tool", "analysis tool",
                "from the attached", "from the uploaded",
                "attached pdf", "attached document", "attached file",
                "uploaded pdf", "uploaded document", "uploaded file",
                "ensure accuracy and completeness", "for readability and clarity",
                "extract key information from"
            ];

            var lines = plan.Split('\n');
            var cleanedSteps = new List<string>();

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (trimmed.Length <= 1) continue;

                bool isNumbered = char.IsDigit(trimmed[0])
                    && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4;

                if (!isNumbered) continue;

                string lower = trimmed.ToLowerInvariant();
                bool isForbidden = forbiddenPatterns.Any(f => lower.Contains(f));

                if (!isForbidden)
                    cleanedSteps.Add(trimmed);
            }

            if (cleanedSteps.Count == 0)
                return "";

            var renumbered = new List<string>();
            for (int i = 0; i < cleanedSteps.Count; i++)
            {
                string step = cleanedSteps[i];
                int dotPos = step.IndexOfAny(['.', ')']);
                if (dotPos > 0)
                {
                    char separator = step[dotPos];
                    string content = step[(dotPos + 1)..].Trim();
                    renumbered.Add($"{i + 1}{separator} {content}");
                }
                else
                {
                    renumbered.Add($"{i + 1}. {step}");
                }
            }

            return string.Join("\n", renumbered);
        }

        private static string BuildFallbackDocumentPlan(List<DocumentChunk> chunks)
        {
            return BuildDocumentTaskPlan("summarize", chunks);
        }

        private static string BuildArtifactArchitectHandoff(CouncilRunContext context)
        {
            string request = (context.UserPrompt + "\n" + context.Objective).ToLowerInvariant();
            bool workspaceMode = context.IsWorkspaceTask;
            bool wantsHtml = request.Contains("html", StringComparison.Ordinal)
                || request.Contains("dashboard", StringComparison.Ordinal)
                || request.Contains("interactive", StringComparison.Ordinal)
                || request.Contains("button", StringComparison.Ordinal)
                || request.Contains("javascript", StringComparison.Ordinal);
            string artifactType = workspaceMode
                ? "connected_codebase_patch"
                : wantsHtml ? "standalone_html" : "project_canvas_artifact";

            var body = new StringBuilder();
            body.AppendLine("ARCHITECT_HANDOFF");
            body.AppendLine("artifact_type: " + artifactType);
            body.AppendLine("output_target: " + (workspaceMode ? "ProjectCanvasPatchReview" : "ProjectCanvas"));
            body.AppendLine("required_output_format: " + (workspaceMode
                ? "valid [[AXIOM_CODEBASE_PATCH]] envelope with relative FILE paths, ACTION create/replace, complete fenced file content, and [[END FILE]] per file"
                : "one complete offline-renderable source; prefer raw complete HTML or one html code fence that the pipeline can strip"));
            if (workspaceMode && context.WorkspaceFilesRead.Count > 0)
            {
                body.AppendLine("connected_context_files:");
                foreach (string file in context.WorkspaceFilesRead.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
                    body.AppendLine("- " + file);
            }

            body.AppendLine("requirements:");

            var requirements = new List<string>();
            if (context.GoalContract?.Requirements.Count > 0)
                requirements.AddRange(context.GoalContract.Requirements);
            else if (context.Decomposition?.Requirements.Count > 0)
                requirements.AddRange(context.Decomposition.Requirements);
            else if (!string.IsNullOrWhiteSpace(context.UserPrompt))
                requirements.Add(context.UserPrompt.Trim());

            if (!workspaceMode)
            {
                if (wantsHtml || request.Contains("css", StringComparison.Ordinal) || request.Contains("style", StringComparison.Ordinal))
                    requirements.Add("embedded CSS");
                if (request.Contains("javascript", StringComparison.Ordinal) || request.Contains("interactive", StringComparison.Ordinal) || request.Contains("button", StringComparison.Ordinal) || request.Contains("animated", StringComparison.Ordinal))
                    requirements.Add("embedded JavaScript");
            }

            foreach (string item in requirements.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
                body.AppendLine("- " + item.Trim());

            body.AppendLine("constraints:");
            if (context.GoalContract?.Constraints.Count > 0)
            {
                foreach (string constraint in context.GoalContract.Constraints.Distinct(StringComparer.OrdinalIgnoreCase).Take(8))
                    body.AppendLine("- " + constraint.Trim());
            }
            if (workspaceMode)
            {
                body.AppendLine("- preserve unrelated code and workspace files");
                body.AppendLine("- target the prompt-named file when one is named");
                body.AppendLine("- no standalone Project Canvas artifact output outside the patch envelope");
            }
            else
            {
                body.AppendLine("- one complete self-contained artifact source");
                body.AppendLine("- no external libraries, CDN links, remote scripts, or remote assets");
                body.AppendLine("- responsive inside Project Canvas with explicit body background and text color");
            }
            body.AppendLine("- no prose, commentary, alternatives, TODOs, or pseudo-code in Builder output");
            body.AppendLine("builder_instruction:");
            body.AppendLine(workspaceMode
                ? "Return only a valid [[AXIOM_CODEBASE_PATCH]] envelope for the connected workspace. Do not return standalone HTML/code outside the patch envelope."
                : "Return only one complete standalone HTML document unless the user explicitly required a different renderable artifact format.");
            body.AppendLine("acceptance_tests:");

            var checks = workspaceMode
                ? new List<string>
                {
                    "Builder output is a valid [[AXIOM_CODEBASE_PATCH]] envelope",
                    "patch targets the prompt-named or connected workspace file path",
                    "changed file content is complete and coherent, not a fragment",
                    "requested controls/functions/behaviors are wired to working logic",
                    "no unrelated files or unrelated behavior are changed"
                }
                : new List<string>
                {
                    "contains <!DOCTYPE html> for standalone HTML",
                    "contains <style> with embedded CSS when styling is requested",
                    "contains <script> with embedded JavaScript when interactivity or animation is requested",
                    "has no external script/link/CDN references",
                    "renders the requested UI elements",
                    "requested controls change visible state"
                };
            if (context.GoalContract?.AcceptanceChecks.Count > 0)
                checks.AddRange(context.GoalContract.AcceptanceChecks);
            foreach (string check in checks.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
                body.AppendLine("- " + check.Trim());

            body.AppendLine("END_ARCHITECT_HANDOFF");
            return body.ToString().Trim();
        }

        // Last-resort Architect plan synthesized from the deterministic pre-flight decomposition.
        // Converts what used to be a hard relay abort (two consecutive invalid Architect outputs)
        // into a degraded-but-working run: the Builder still receives a concrete numbered plan
        // grounded in the user's own stated requirements.
        private static string BuildFallbackPlanFromDecomposition(CouncilRunContext context)
        {
            var sb = new StringBuilder();
            int step = 1;
            foreach (string requirement in context.Decomposition?.Requirements ?? new List<string>())
            {
                string trimmedRequirement = requirement?.Trim() ?? string.Empty;
                if (trimmedRequirement.Length == 0)
                    continue;
                sb.AppendLine($"{step++}. Implement this requirement exactly as stated: {trimmedRequirement}");
                if (step > 7)
                    break;
            }

            if (step == 1)
            {
                string statement = !string.IsNullOrWhiteSpace(context.Decomposition?.ProblemStatement)
                    ? context.Decomposition!.ProblemStatement.Trim()
                    : context.UserPrompt.Trim();
                if (statement.Length > 400)
                    statement = statement[..400];
                if (statement.Length == 0)
                    return string.Empty;
                sb.AppendLine($"1. Produce a complete, direct implementation of the user's request: {statement}");
                step = 2;
            }

            sb.AppendLine($"{step}. Review the full output against the original request and correct any omission or inconsistency before finishing.");
            return sb.ToString().Trim();
        }

        private static bool IsDocumentSynthesisTask(string userQuery, CouncilTaskType taskType)
        {
            if (taskType == CouncilTaskType.Coding)
                return false;

            string lower = userQuery.ToLowerInvariant();

            string[] documentRefs =
            [
                "attached", "uploaded", "the file", "the pdf", "the document",
                "this file", "this pdf", "this document", "the txt",
                "the text file", "my file", "my pdf", "my document"
            ];

            string[] taskVerbs =
            [
                "summarize", "summarise", "summary", "explain", "describe",
                "review", "outline", "extract", "list the", "what does",
                "what is in", "what's in", "tell me about", "break down",
                "key points", "main points", "highlights", "overview",
                "what are", "what is", "contents of"
            ];

            bool refsDocument = documentRefs.Any(r => lower.Contains(r));
            bool hasTaskVerb = taskVerbs.Any(v => lower.Contains(v));

            // Explicit document reference + task verb = definite match
            if (refsDocument && hasTaskVerb)
                return true;

            // When documents are loaded (caller already verified via isDocumentGrounded),
            // strong synthesis verbs alone are sufficient — the documents are the implied subject.
            // This catches cases like just "summarize" without "the file/pdf" qualifiers.
            string[] strongVerbs = ["summarize", "summarise", "summary", "overview", "key points", "main points", "highlights"];
            return strongVerbs.Any(v => lower.Contains(v));
        }

        private static string BuildDocumentTaskPlan(string userQuery, List<DocumentChunk> chunks)
        {
            var docNames = chunks.Select(c => c.FileName).Distinct().Take(5).ToList();
            string docRef = docNames.Count > 0
                ? string.Join(", ", docNames)
                : "the uploaded document(s)";

            string lower = userQuery.ToLowerInvariant();

            if (lower.Contains("summarize") || lower.Contains("summarise") || lower.Contains("summary"))
            {
                return $"1. Identify the main topic, purpose, and scope of {docRef} from the text in [[DOCUMENT CONTENT]].\n" +
                       "2. Extract and present the key points, findings, and core arguments from the source material.\n" +
                       "3. Include supporting details, data points, and notable examples that reinforce the main points.\n" +
                       "4. Synthesize all extracted information into a coherent, well-structured summary.";
            }

            if (lower.Contains("explain"))
            {
                return $"1. Identify what {docRef} is about from the text in [[DOCUMENT CONTENT]].\n" +
                       "2. Break down the main concepts and ideas into clear, understandable sections.\n" +
                       "3. Explain any technical terms, relationships, or processes described in the text.\n" +
                       "4. Provide a comprehensive explanation that addresses the user's request.";
            }

            if (lower.Contains("analyze") || lower.Contains("analyse") || lower.Contains("analysis"))
            {
                return $"1. Identify the main claims, arguments, and themes in {docRef} from [[DOCUMENT CONTENT]].\n" +
                       "2. Evaluate the strength of evidence and reasoning presented in the source material.\n" +
                       "3. Identify patterns, themes, strengths, weaknesses, or gaps in the content.\n" +
                       "4. Synthesize findings into a structured analytical assessment.";
            }

            if (lower.Contains("key points") || lower.Contains("main points") || lower.Contains("highlights"))
            {
                return $"1. Read through the full text of {docRef} from [[DOCUMENT CONTENT]].\n" +
                       "2. Identify each distinct key point or highlight from the material.\n" +
                       "3. Organize the key points by importance or thematic grouping.\n" +
                       "4. Present the key points in a clear, numbered or bulleted format.";
            }

            return $"1. Read and understand the content of {docRef} from the text in [[DOCUMENT CONTENT]].\n" +
                   "2. Identify the information most relevant to the user's request.\n" +
                   "3. Organize the relevant information into a clear structure.\n" +
                   "4. Write a complete response addressing the user's request using only the document content.";
        }

        private static string BuildDocumentTaskPlan(string userQuery, List<DocumentChunk> chunks, int documentTokens, int builderContextBudget)
        {
            // If document is large relative to builder context, produce a sectioned plan
            int promptOverhead = 800; // system prompt + role primers + plan text
            int availableForDoc = builderContextBudget - promptOverhead;
            bool needsSegmented = documentTokens > (int)(availableForDoc * 0.6);

            var docNames = chunks.Select(c => c.FileName).Distinct().Take(5).ToList();
            string docRef = docNames.Count > 0
                ? string.Join(", ", docNames)
                : "the uploaded document(s)";

            string basePlan = BuildDocumentTaskPlan(userQuery, chunks);

            if (!needsSegmented)
                return basePlan;

            // For large documents, add segmentation instruction and synthesis step
            return basePlan + "\n" +
                   $"{CountArchitectSteps(basePlan) + 1}. [SEGMENTED PROCESSING] The document is large. Process it section by section, " +
                   "maintaining continuity between segments via [[PRIOR OUTPUT]] headers.\n" +
                   $"{CountArchitectSteps(basePlan) + 2}. Synthesize all section outputs into a single coherent final response.";
        }

        private static bool IsLowValueDocumentOutput(string output, List<string> docFileNames)
        {
            if (string.IsNullOrWhiteSpace(output))
                return true;

            string trimmed = output.Trim();
            if (trimmed.Length < 80)
                return true;

            string lower = trimmed.ToLowerInvariant();
            if (lower is "n/a" or "none" or "not available")
                return true;

            if (docFileNames.Count > 0)
            {
                string firstDoc = docFileNames[0].ToLowerInvariant();
                string fileStem = Path.GetFileNameWithoutExtension(firstDoc);
                if (!string.IsNullOrWhiteSpace(fileStem)
                    && lower.StartsWith(fileStem, StringComparison.Ordinal)
                    && lower.Count(c => c == '-' || c == '—' || c == '=') > 5
                    && trimmed.Length < 220)
                {
                    return true;
                }
            }

            int alphaNum = trimmed.Count(char.IsLetterOrDigit);
            double density = trimmed.Length == 0 ? 0 : (double)alphaNum / trimmed.Length;
            if (density < 0.45)
                return true;

            int sentenceCount = Regex.Matches(trimmed, @"[\.!?]").Count;
            int bulletCount = Regex.Matches(trimmed, @"^\s*[-*•]\s+", RegexOptions.Multiline).Count;
            return sentenceCount == 0 && bulletCount < 2;
        }

        private static string BuildDeterministicDocumentResponse(string userQuery, string documentContent, List<string> fileNames)
        {
            if (string.IsNullOrWhiteSpace(documentContent))
            {
                return "Unable to summarize because no document text was resolved from the loaded files.";
            }

            string lowerQuery = userQuery.ToLowerInvariant();
            var sentences = Regex.Split(documentContent, @"(?<=[\.!?])\s+")
                .Select(s => s.Trim())
                .Where(s => s.Length >= 40 && s.Length <= 320)
                .Where(s => s.Any(char.IsLetter))
                .Take(240)
                .ToList();

            if (sentences.Count == 0)
            {
                string fallback = documentContent.Length > 1200 ? documentContent[..1200] + "..." : documentContent;
                return fallback.Trim();
            }

            var scored = sentences
                .Select((s, i) => new
                {
                    Text = s,
                    Index = i,
                    Score = Regex.Matches(s, @"\b[A-Za-z]{5,}\b").Count + (s.Any(char.IsDigit) ? 2 : 0)
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Index)
                .Take(12)
                .OrderBy(x => x.Index)
                .Select(x => x.Text)
                .ToList();

            string docLabel = fileNames.Count == 0
                ? "loaded document(s)"
                : string.Join(", ", fileNames.Take(3)) + (fileNames.Count > 3 ? ", ..." : "");

            if (lowerQuery.Contains("key points") || lowerQuery.Contains("highlights") || lowerQuery.Contains("main points"))
            {
                var bullets = scored.Take(8).Select(s => $"- {s}");
                return $"Source: {docLabel}\n\nSource-grounded fallback key points:\n" +
                       string.Join("\n", bullets) +
                       "\n\nNote: The local model did not produce a reliable synthesized answer, so this fallback shows representative source-supported points instead of inventing content.";
            }

            var summarySentences = scored.Take(6).ToList();
            string summary = string.Join(" ", summarySentences);

            if (lowerQuery.Contains("analy") || lowerQuery.Contains("evaluate") || lowerQuery.Contains("critique"))
            {
                return $"Source: {docLabel}\n\nSource-grounded fallback analysis:\n{summary}\n\nObserved source points:\n" +
                       string.Join("\n", scored.Skip(2).Take(4).Select(s => $"- {s}")) +
                       "\n\nNote: The local model did not produce a reliable synthesized answer, so this fallback stays close to the source text instead of inventing unsupported analysis.";
            }

            return $"Source: {docLabel}\n\nSource-grounded fallback summary:\n{summary}\n\nNote: The local model did not produce a reliable synthesized answer, so this fallback shows representative source-supported content instead of inventing a summary.";
        }

        private static string BuildDeterministicDocumentCritic(CouncilRunContext context)
        {
            var findings = new List<string>();

            if (string.IsNullOrWhiteSpace(context.BuilderOutput))
            {
                findings.Add("Builder output is empty. The response does not fulfill the request.");
            }
            else
            {
                if (IsLowValueDocumentOutput(context.BuilderOutput, context.DocumentFileNames))
                {
                    findings.Add("Builder output is low-information or placeholder text and does not provide a usable summary/analysis.");
                }

                if (context.BuilderOutput.Length < 140)
                {
                    findings.Add("Builder output is too short to reliably cover the document request.");
                }
            }

            foreach (var warning in DetectDocumentHallucinations(context.BuilderOutput, context.DocumentContent).Take(6))
            {
                findings.Add(warning);
            }

            if (findings.Count == 0)
            {
                return "No issues found.";
            }

            var sb = new StringBuilder();
            for (int i = 0; i < findings.Count; i++)
            {
                sb.AppendLine($"{i + 1}. Reference: Builder output");
                sb.AppendLine($"   Issue: {findings[i]}");
                sb.AppendLine("   Fix: Regenerate the response grounded in [[DOCUMENT CONTENT]] with concrete summary points.");
            }

            return sb.ToString().Trim();
        }

        // Last-resort recovery for document answers. ONLY substitutes when the model produced nothing
        // usable (empty or placeholder/low-value text); a real answer — even a short, concise summary —
        // is returned exactly as the model wrote it.
        //
        // This deliberately no longer "expands" a short-but-good answer by appending verbatim source
        // sentences ("Additional verified source synthesis: ..."). That padding was the user-visible
        // "grab and drop": any concise summary under a word threshold got real document sentences stapled
        // onto it. Length targets are now satisfied by re-prompting the MODEL (the long-form continuation
        // pass), never by pasting raw source text.
        private static string BuildDeterministicDocumentQualityPass(CouncilRunContext context)
        {
            if (string.IsNullOrWhiteSpace(context.BuilderOutput))
                return BuildDeterministicDocumentResponse(context.UserPrompt, context.DocumentContent, context.DocumentFileNames);

            string output = context.BuilderOutput.Trim();

            // Only genuinely empty/placeholder output is replaced; a usable summary stays untouched.
            if (IsLowValueDocumentOutput(output, context.DocumentFileNames))
            {
                string deterministic = BuildDeterministicDocumentResponse(context.UserPrompt, context.DocumentContent, context.DocumentFileNames).Trim();
                return string.IsNullOrWhiteSpace(deterministic) ? output : deterministic;
            }

            return output;
        }

        private static int TryGetRequestedWordTarget(string userQuery, string objective)
        {
            string combined = $"{userQuery} {objective}";
            if (string.IsNullOrWhiteSpace(combined))
                return 0;

            var range = Regex.Match(combined, @"(\d{2,4})\s*[-–—]\s*(\d{2,4})\s*words?", RegexOptions.IgnoreCase);
            if (range.Success
                && int.TryParse(range.Groups[1].Value, out int lo)
                && int.TryParse(range.Groups[2].Value, out int hi))
            {
                return Math.Max(0, (lo + hi) / 2);
            }

            var single = Regex.Match(combined, @"~?\s*(\d{2,4})\s*words?", RegexOptions.IgnoreCase);
            if (single.Success && int.TryParse(single.Groups[1].Value, out int target))
                return target;

            if (Regex.IsMatch(combined, @"\b(one|a)\s+page\b|\b1\s+page\b", RegexOptions.IgnoreCase))
                return 450;

            if (Regex.IsMatch(combined, @"\bessay\b|\bresearch paper\b|\barticle\b|\blong[-\s]?form\b", RegexOptions.IgnoreCase))
                return 650;

            return 0;
        }

        private static int CountWords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0;

            return Regex.Matches(text, @"\b[\p{L}\p{N}][\p{L}\p{N}'’\-]*\b").Count;
        }

        private static string ResolveDocumentContent(List<DocumentChunk> chunks)
        {
            if (chunks.Count == 0)
                return "";

            // Reconstruct full document text from chunks in order, grouped by file
            var sb = new StringBuilder();
            var byFile = chunks
                .GroupBy(c => c.FileName)
                .OrderBy(g => g.Key);

            foreach (var group in byFile)
            {
                sb.AppendLine($"═══ {group.Key} ═══");
                foreach (var chunk in group.OrderBy(c => c.ChunkId))
                {
                    sb.AppendLine(chunk.Content.Trim());
                }
                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        private static PreFlightDecomposition DecomposeDocumentTask(string userQuery, string objective, string documentContent, List<string> docFileNames)
        {
            var decomp = new PreFlightDecomposition();
            string docRef = docFileNames.Count > 0
                ? string.Join(", ", docFileNames.Take(3))
                : "the uploaded document(s)";

            decomp.ProblemStatement = $"The user wants to perform an operation on {docRef}. " +
                $"The document text ({documentContent.Length} characters) is provided in full. " +
                $"User instruction: {(userQuery.Length > 150 ? userQuery[..150] + "..." : userQuery)}";

            // Requirements come from what the user wants done TO the document
            string combined = $"{userQuery} {objective}".Trim();
            var requirementMarkers = new[] { "must ", "should ", "need to ", "wants ", "want ", "create ", "produce ", "generate ", "write " };
            foreach (var line in combined.Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string trimLine = line.Trim();
                if (trimLine.Length < 5) continue;
                string trimLower = trimLine.ToLowerInvariant();
                if (requirementMarkers.Any(m => trimLower.Contains(m)))
                    decomp.Requirements.Add(trimLine);
            }

            if (decomp.Requirements.Count == 0)
                decomp.Requirements.Add(combined.Length > 200 ? combined[..200] : combined);

            decomp.Constraints.Add("All output must be grounded in the provided document text.");
            decomp.Constraints.Add("Do not fabricate information not present in the document.");

            decomp.DocumentContent = documentContent;

            return decomp;
        }

        private static string BuildDocumentContentBlock(string documentContent, int maxChars = 0)
        {
            string content = maxChars > 0 && documentContent.Length > maxChars
                ? documentContent[..maxChars] + "\n[...document truncated to fit context window]"
                : documentContent;

            return BuildLabeledBlock("DOCUMENT CONTENT", content);
        }

        private static List<string> DetectDocumentHallucinations(string builderOutput, string documentContent)
        {
            var warnings = new List<string>();
            if (string.IsNullOrWhiteSpace(builderOutput) || string.IsNullOrWhiteSpace(documentContent))
                return warnings;

            string lowerOutput = builderOutput.ToLowerInvariant();
            string lowerDoc = documentContent.ToLowerInvariant();

            // Check for references to structures not in the document
            string[] phantomReferences =
            [
                "page ", "figure ", "fig. ", "table ", "chart ", "graph ", "appendix ",
                "section ", "chapter "
            ];

            foreach (var refType in phantomReferences)
            {
                var matches = System.Text.RegularExpressions.Regex.Matches(lowerOutput, $@"{refType}\d+");
                foreach (System.Text.RegularExpressions.Match match in matches)
                {
                    if (!lowerDoc.Contains(match.Value))
                    {
                        warnings.Add($"CONTENT ACCURACY WARNING: Reference to '{match.Value}' not found in source document.");
                    }
                }
            }

            // Check for claims the document was not provided
            string[] unavailabilityClaims =
            [
                "document was not provided", "file was not provided", "cannot access the",
                "no document was", "no file was", "unable to access", "i don't have access",
                "i cannot read", "i cannot open", "document is not available",
                "file is not available", "i cannot see the", "i don't have the"
            ];

            foreach (var claim in unavailabilityClaims)
            {
                if (lowerOutput.Contains(claim))
                {
                    warnings.Add($"CONTENT ACCURACY WARNING: Builder falsely claims document is unavailable ('{claim}'). This is a hallucination — the document text was provided in full.");
                }
            }

            // Check for section header references not present in document text
            var sectionHeaderMatches = System.Text.RegularExpressions.Regex.Matches(
                builderOutput,
                "section\\s+[\"'“”‘’]([^\"'“”‘’]{2,80})[\"'“”‘’]",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in sectionHeaderMatches)
            {
                if (m.Groups.Count > 1)
                {
                    string referencedHeader = m.Groups[1].Value.Trim().ToLowerInvariant();
                    if (referencedHeader.Length > 0 && !lowerDoc.Contains(referencedHeader))
                    {
                        warnings.Add($"CONTENT ACCURACY WARNING: Referenced section header '{m.Groups[1].Value}' not found in source document.");
                    }
                }
            }

            // Basic contradiction checks against known document content
            var contradictionMatches = System.Text.RegularExpressions.Regex.Matches(
                lowerOutput,
                @"does not mention\s+([a-z0-9\-\s]{3,60})|contains no\s+([a-z0-9\-\s]{3,60})");
            foreach (System.Text.RegularExpressions.Match m in contradictionMatches)
            {
                string term = m.Groups[1].Success ? m.Groups[1].Value.Trim() : m.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(term) && lowerDoc.Contains(term, StringComparison.Ordinal))
                {
                    warnings.Add($"CONTENT ACCURACY WARNING: Potential contradiction — output states absence of '{term}', but the term appears in the stored document content.");
                }
            }

            return warnings;
        }

        // Faithfulness guard for document answers. Returns true ONLY when the Builder output explicitly
        // (and falsely) claims the document is unavailable — a definite hallucination, since the full text
        // was provided in the payload, and one where substituting a grounded extract is genuinely better
        // than delivering a false refusal.
        //
        // A previous version ALSO flagged answers whose substantive vocabulary barely overlapped the
        // source (a lexical-containment ratio) and replaced them with a verbatim sentence dump. That
        // remedy was worse than the disease: a faithful abstractive summary can legitimately score low,
        // and the "replacement" was itself the copy-and-paste-fragments behavior users complained about.
        // Normal Chat ships the model's answer with no such guard and summarizes reliably, so the routine
        // synthesis is now trusted; only the unambiguous false-unavailability claim is intercepted here.
        private static bool IsUngroundedDocumentOutput(string builderOutput, string documentContent)
        {
            if (string.IsNullOrWhiteSpace(builderOutput) || string.IsNullOrWhiteSpace(documentContent))
                return false;

            return DetectDocumentHallucinations(builderOutput, documentContent)
                .Any(w => w.Contains("falsely claims document is unavailable", StringComparison.OrdinalIgnoreCase));
        }

        private static List<(string Section, int TokenEstimate)> SplitDocumentForSegmentedProcessing(string documentContent, int maxTokensPerSegment)
        {
            var segments = new List<(string Section, int TokenEstimate)>();
            if (string.IsNullOrWhiteSpace(documentContent))
                return segments;

            int maxCharsPerSegment = (int)(maxTokensPerSegment * AvgCharsPerToken);
            var paragraphs = documentContent.Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries);

            var current = new StringBuilder();
            foreach (var para in paragraphs)
            {
                if (current.Length + para.Length > maxCharsPerSegment && current.Length > 0)
                {
                    string section = current.ToString().Trim();
                    segments.Add((section, (int)Math.Ceiling(section.Length / AvgCharsPerToken)));
                    current.Clear();
                }
                current.AppendLine(para);
                current.AppendLine();
            }

            if (current.Length > 0)
            {
                string section = current.ToString().Trim();
                segments.Add((section, (int)Math.Ceiling(section.Length / AvgCharsPerToken)));
            }

            return segments;
        }

        private static bool TryNormalizeArchitectPlan(string raw, out string normalizedPlan, out bool markerFound)
        {
            normalizedPlan = "";
            markerFound = TryExtractWithMarker(raw, ArchitectCompletionMarker, out string markerCleaned);

            string candidate = markerFound ? markerCleaned : (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            if (IsArchitectArtifactHandoff(candidate))
            {
                normalizedPlan = candidate.Trim();
                return true;
            }

            var numbered = new List<string>();
            var bullets = new List<string>();

            foreach (var line in candidate.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                bool isNumbered = trimmed.Length > 1
                    && char.IsDigit(trimmed[0])
                    && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4;

                if (isNumbered)
                {
                    numbered.Add(trimmed);
                    continue;
                }

                if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
                {
                    bullets.Add(trimmed[2..].Trim());
                }
            }

            if (numbered.Count == 0 && bullets.Count > 0)
            {
                for (int i = 0; i < bullets.Count; i++)
                {
                    numbered.Add($"{i + 1}. {bullets[i]}");
                }
            }

            // Last resort: treat each non-empty line as a potential step
            // This makes unstructured model output (e.g., all-caps instructions) processable by the sanitizer
            if (numbered.Count == 0)
            {
                var plainLines = candidate.Split('\n')
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 5)
                    .Take(12)
                    .ToList();

                if (plainLines.Count == 0)
                    return false;

                for (int i = 0; i < plainLines.Count; i++)
                {
                    numbered.Add($"{i + 1}. {plainLines[i]}");
                }
            }

            normalizedPlan = string.Join("\n", numbered);
            return true;
        }

        private static bool BuilderHasRoleDrift(string output, CouncilTaskType taskType)
        {
            if (string.IsNullOrWhiteSpace(output)) return true;
            string lower = output.ToLowerInvariant();

            if (taskType == CouncilTaskType.Coding)
            {
                // For coding tasks, output MUST contain code signals
                bool hasCodeSignal = lower.Contains("```")
                    || lower.Contains("def ")
                    || lower.Contains("class ")
                    || lower.Contains("public ")
                    || lower.Contains("function ")
                    || lower.Contains("=>")
                    || lower.Contains(";")
                    || lower.Contains("{")
                    || lower.Contains("<html")
                    || lower.Contains("<!doctype")
                    || lower.Contains("<svg")
                    || lower.Contains("<div") && lower.Contains("</div>");
                return !hasCodeSignal;
            }

            // For non-coding tasks, only flag STRONG code patterns.
            // Individual tokens like ';', '{', 'class ', 'public ' appear routinely in English prose
            // (e.g., "the working class", "public sector", "items; namely").
            // Require multiple strong indicators to avoid false positives.
            int codeWeight = 0;
            if (lower.Contains("```")) codeWeight += 3;
            if (lower.Contains("def ") && lower.Contains("return ")) codeWeight += 2;
            if (lower.Contains("public static ")) codeWeight += 2;
            if (lower.Contains("import ") && lower.Contains("from ")) codeWeight += 2;
            if (lower.Contains("function ") && lower.Contains("{")) codeWeight += 2;
            if (lower.Contains("<html") || lower.Contains("<!doctype")) codeWeight += 3;
            if (lower.Contains("<svg")) codeWeight += 3;
            if (lower.Contains("<div") && lower.Contains("</div>")) codeWeight += 2;
            if (lower.Contains("#include")) codeWeight += 2;
            if (lower.Contains("console.writeline") || lower.Contains("system.out.println") || lower.Contains("print(")) codeWeight += 2;
            if (lower.Contains("namespace ") && lower.Contains("using ")) codeWeight += 2;
            if (lower.Contains("void ") && lower.Contains("(") && lower.Contains("{")) codeWeight += 2;

            // Detect pseudo-code formatting patterns common in format-drifted non-coding output
            if (lower.Contains("if __name__")) codeWeight += 3;
            if (lower.Contains(">>> ")) codeWeight += 2;

            // Detect heavy variable-assignment style (code-like math formatting)
            int assignmentLines = output.Split('\n').Count(l =>
            {
                string t = l.Trim();
                return t.Length > 3
                    && Regex.IsMatch(t, @"^[a-z_]\w*\s*=\s*\S", RegexOptions.IgnoreCase)
                    && !t.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    && !t.Contains("==");
            });
            if (assignmentLines >= 3) codeWeight += 2;
            if (assignmentLines >= 5) codeWeight += 2;

            // Detect Python-style block structure (keyword lines ending with colon)
            int colonBlockLines = output.Split('\n').Count(l =>
            {
                string t = l.TrimEnd();
                return t.EndsWith(':') && Regex.IsMatch(t, @"^\s*(def |if |for |while |class |elif |else:)", RegexOptions.IgnoreCase);
            });
            if (colonBlockLines >= 2) codeWeight += 3;

            return codeWeight >= 3;
        }

        private static bool CriticHasRoleDrift(string output, CouncilTaskType taskType)
        {
            if (string.IsNullOrWhiteSpace(output)) return true;
            string lower = output.ToLowerInvariant();
            bool isClearPass = lower.Trim().StartsWith("no issues found", StringComparison.OrdinalIgnoreCase);
            bool hasReference = taskType == CouncilTaskType.Coding
                ? lower.Contains("line ") || lower.Contains("function ") || lower.Contains("step ") || lower.Contains("method ") || lower.Contains("class ")
                : lower.Contains("section") || lower.Contains("topic") || lower.Contains("step ")
                  || lower.Contains("requirement") || lower.Contains("reference")
                  || lower.Contains("summary") || lower.Contains("content") || lower.Contains("document")
                  || lower.Contains("paragraph") || lower.Contains("point") || lower.Contains("finding")
                  || lower.Contains("accuracy") || lower.Contains("completeness") || lower.Contains("coverage")
                  || lower.Contains("missing") || lower.Contains("addressed") || lower.Contains("covered");

            return !isClearPass && !hasReference;
        }

        private static bool IsClearPassCriticOutput(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string trimmed = output.Trim();
            return trimmed.StartsWith("No issues found", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("No major issues found", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("No significant issues found", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("No material issues found", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeCriticFindingsLayout(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return string.Empty;

            var lines = candidate.Split('\n')
                .Select(l => l.Trim())
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            if (lines.Count == 0)
                return string.Empty;

            bool hasNumbered = lines.Any(line => line.Length > 1
                && char.IsDigit(line[0])
                && line.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4);
            if (hasNumbered)
                return string.Join("\n", lines);

            var bulletItems = lines
                .Where(line => line.StartsWith("- ") || line.StartsWith("* ") || line.StartsWith("• "))
                .Select(line => line[2..].Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (bulletItems.Count > 0)
                return string.Join("\n", bulletItems.Select((item, index) => $"{index + 1}. {item}"));

            bool hasStructuredFields = lines.Any(line => line.StartsWith("Reference:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Issue:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Fix:", StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("Severity:", StringComparison.OrdinalIgnoreCase));
            if (hasStructuredFields)
            {
                var grouped = new List<string>();
                var current = new StringBuilder();
                foreach (string line in lines)
                {
                    bool startsNewGroup = line.StartsWith("Reference:", StringComparison.OrdinalIgnoreCase)
                        && current.Length > 0;
                    if (startsNewGroup)
                    {
                        grouped.Add(current.ToString().Trim());
                        current.Clear();
                    }

                    if (current.Length > 0)
                        current.Append(' ');
                    current.Append(line);
                }

                if (current.Length > 0)
                    grouped.Add(current.ToString().Trim());

                if (grouped.Count > 0)
                    return string.Join("\n", grouped.Select((item, index) => $"{index + 1}. {item}"));
            }

            return lines.Count == 1
                ? $"1. {lines[0]}"
                : string.Join("\n", lines.Select((line, index) => $"{index + 1}. {line}"));
        }

        private static bool TryNormalizeBuilderOutput(string raw, CouncilTaskType taskType, out string normalized, out bool markerFound)
        {
            normalized = "";
            markerFound = TryExtractWithMarker(raw, BuilderCompletionMarker, out string markerCleaned);
            string candidate = markerFound ? markerCleaned : (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            // Strip pipeline metadata before validation so echoed blocks don't affect drift/quality checks
            candidate = StripPipelineMetadata(candidate);
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            normalized = candidate.Trim();
            if (taskType == CouncilTaskType.Coding)
            {
                Match fenceMatch = BuilderCodeFenceRegex.Match(normalized);
                if (fenceMatch.Success)
                {
                    string fencedCode = fenceMatch.Groups["code"].Value.Trim();
                    string outside = BuilderCodeFenceRegex.Replace(normalized, string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(outside))
                        return false;

                    normalized = fencedCode;
                    return HasStrongCodeSignal(fencedCode);
                }

                return HasStrongCodeSignal(normalized) && !LooksLikeBuilderProsePrelude(normalized);
            }

            return true;
        }

        private static bool HasStrongCodeSignal(string output)
            => DetectCodeOutput(output).IsCode;

        private static bool LooksLikeBuilderProsePrelude(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string trimmed = output.TrimStart();
            return trimmed.StartsWith("here", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("this ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("the following", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("i ", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("below", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsDegenerateBuilderOutput(string output, CouncilTaskType taskType)
        {
            if (string.IsNullOrWhiteSpace(output))
                return true;

            string trimmed = output.Trim();
            if (LooksLikeCompletionMarkerOnlyText(trimmed))
                return true;

            if (trimmed.Length < 8)
                return true;

            int alphaNumeric = trimmed.Count(char.IsLetterOrDigit);
            int minAlphaNumeric = taskType == CouncilTaskType.Coding ? 6 : 20;
            if (alphaNumeric < minAlphaNumeric)
                return true;

            if (taskType != CouncilTaskType.Coding)
            {
                if (trimmed is "[" or "]" or "{}" or "()" or "```" or "\"\"")
                    return true;
            }

            return false;
        }

        private static bool HasProjectCanvasManualTrigger(string userQuery, string objective)
            => ProjectCanvasManualTriggerRegex.IsMatch(userQuery ?? string.Empty)
                || ProjectCanvasManualTriggerRegex.IsMatch(objective ?? string.Empty);

        private static bool DetectArtifactCanvasFollowUpIntent(string userQuery, CouncilRunContext? lastRunContext, string? currentCanvas)
        {
            string query = (userQuery ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(query))
                return false;

            bool priorCanvasOutput = lastRunContext?.IsArtifactCanvasRequest == true
                || lastRunContext?.TaskType == CouncilTaskType.Coding;
            bool currentCanvasHasContent = CanvasHasRealContent(currentCanvas);
            bool currentCanvasHasRenderableArtifact = ArtifactRenderService
                .DetectForCanvas(currentCanvas ?? string.Empty, null)
                .SupportsPreview;
            if (!currentCanvasHasContent || (!priorCanvasOutput && !currentCanvasHasRenderableArtifact))
                return false;

            string lower = query.ToLowerInvariant();
            string[] editSignals =
            [
                "add", "remove", "delete", "change", "update", "modify", "edit",
                "fix", "improve", "refine", "iterate", "revise", "redo", "remake",
                "make it", "make this", "make the", "turn it", "turn this",
                "resize", "restyle", "style", "color", "colour", "animate",
                "align", "move", "replace", "keep", "preserve", "continue",
                "build on", "build further", "extend", "expand"
            ];
            string[] artifactReferences =
            [
                "it", "this", "that", "artifact", "canvas", "project canvas",
                "screen", "page", "layout", "ui", "interface", "form", "button",
                "input", "card", "panel", "section", "header", "footer", "design",
                "preview", "render", "component", "background", "text", "title", "font",
                "spacing", "size", "width", "height", "code", "script", "document", "table",
                "chart", "graph", "diagram", "svg", "html", "markdown"
            ];
            string[] proseAnswerSignals =
            [
                "explain", "why", "what is", "what are", "describe", "summarize",
                "summarise", "tell me about", "review", "critique"
            ];

            bool hasEditSignal = editSignals.Any(signal => Regex.IsMatch(
                lower,
                $@"(?<![a-z0-9_]){Regex.Escape(signal)}(?![a-z0-9_])",
                RegexOptions.IgnoreCase));
            bool referencesArtifact = artifactReferences.Any(signal => Regex.IsMatch(
                lower,
                $@"(?<![a-z0-9_]){Regex.Escape(signal)}(?![a-z0-9_])",
                RegexOptions.IgnoreCase));
            bool asksForProseOnly = proseAnswerSignals.Any(signal => lower.StartsWith(signal, StringComparison.Ordinal))
                && !hasEditSignal;

            // A short imperative such as "change the background to blue" normally omits the word
            // "artifact". Once a real canvas output exists, the edit verb itself is sufficient;
            // longer prompts still need an explicit canvas/content reference to avoid hijacking an
            // unrelated new request after a canvas run.
            bool conciseEditDirective = query.Length <= 240 && hasEditSignal;
            return (conciseEditDirective || (hasEditSignal && referencesArtifact)) && !asksForProseOnly;
        }

        private static bool CanvasHasRealContent(string? content)
        {
            string trimmed = (content ?? string.Empty).Trim();
            return trimmed.Length > 0
                && !trimmed.StartsWith("// Builder output appears here", StringComparison.OrdinalIgnoreCase);
        }

        private static bool CanvasSourcesEquivalent(string? existingCanvas, string? candidateOutput)
        {
            static string Normalize(string value) => value
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            string existing = Normalize(existingCanvas ?? string.Empty);
            string candidate = Normalize(StripChatFromCode(candidateOutput ?? string.Empty));
            return string.Equals(existing, candidate, StringComparison.Ordinal);
        }

        private static bool LooksLikeCompletionMarkerOnlyText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return true;

            string normalized = Regex.Replace(text.Trim(), @"[\[\]\(\)\{\}`'""\s_\-:.!]+", " ").Trim();
            normalized = Regex.Replace(normalized, @"\s+", " ").ToUpperInvariant();
            if (normalized.Length == 0)
                return true;

            return normalized is ArchitectCompletionMarker or BuilderCompletionMarker or CriticCompletionMarker
                || normalized == "BUILDER OUTPUT IS COMPLETE"
                || normalized == "BUILDER COMPLETE"
                || normalized == "OUTPUT COMPLETE";
        }

        private static string StripCriticReasoningFromVisibleText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string cleaned = StripSpecialTokenText(text);
            cleaned = Regex.Replace(cleaned, @"<think>[\s\S]*?</think>", string.Empty, RegexOptions.IgnoreCase);

            int openThink = cleaned.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            if (openThink >= 0)
                cleaned = cleaned[..openThink];

            const string gemmaOpen = "<|channel|>thought";
            const string gemmaClose = "<|/channel|>";
            int safety = 0;
            while (safety++ < 5)
            {
                int open = cleaned.IndexOf(gemmaOpen, StringComparison.OrdinalIgnoreCase);
                if (open < 0)
                    break;

                int close = cleaned.IndexOf(gemmaClose, open, StringComparison.OrdinalIgnoreCase);
                if (close < 0)
                {
                    cleaned = cleaned[..open];
                    break;
                }

                cleaned = cleaned.Remove(open, close + gemmaClose.Length - open);
            }

            return cleaned.Trim();
        }

        private static bool CriticContainsReasoningLeak(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string raw = output.Trim();
            if (raw.Contains("<think", StringComparison.OrdinalIgnoreCase)
                || raw.Contains("<|channel|>thought", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            string head = StripSpecialTokenText(raw).TrimStart();
            if (head.Length > 800)
                head = head[..800];

            string firstLine = head.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? string.Empty;
            string firstLineWithoutNumber = Regex.Replace(firstLine, @"^\s*\d+[\.\)]\s*", string.Empty).Trim();
            string lowerHead = head.ToLowerInvariant();
            string lowerFirst = firstLineWithoutNumber.ToLowerInvariant();

            string[] reasoningOpeners =
            [
                "thinking:", "thought:", "thoughts:", "reasoning:", "analysis:",
                "we need to", "we should", "we have to", "let's", "let me",
                "i need to", "i should", "i will", "i'll", "i must",
                "okay, so", "okay so", "ok, so", "hmm", "the user wants",
                "the user asked", "the task is", "first, i", "my plan"
            ];

            if (reasoningOpeners.Any(open => lowerFirst.StartsWith(open, StringComparison.Ordinal)))
                return true;

            return lowerHead.Contains("\nanalysis:", StringComparison.Ordinal)
                || lowerHead.Contains("\nreasoning:", StringComparison.Ordinal)
                || lowerHead.Contains("\nthinking:", StringComparison.Ordinal);
        }

        private static bool TryNormalizeCriticReview(string raw, CouncilTaskType taskType, out string normalized, out bool markerFound)
        {
            normalized = "";
            markerFound = TryExtractWithMarker(raw, CriticCompletionMarker, out string markerCleaned);
            string candidate = markerFound ? markerCleaned : (raw ?? "").Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            candidate = StripCriticReasoningFromVisibleText(candidate);
            if (string.IsNullOrWhiteSpace(candidate) || CriticContainsReasoningLeak(candidate))
                return false;

            candidate = StripPipelineMetadata(candidate).Trim();
            if (string.IsNullOrWhiteSpace(candidate) || CriticContainsReasoningLeak(candidate))
                return false;

            if (IsCriticArtifactHandoff(candidate))
            {
                normalized = candidate.Trim();
                return true;
            }

            if (IsClearPassCriticOutput(candidate))
            {
                normalized = "No issues found.";
                return true;
            }

            normalized = NormalizeCriticFindingsLayout(candidate);
            if (string.IsNullOrWhiteSpace(normalized))
                return false;

            int findings = normalized.Split('\n').Count(line =>
            {
                string t = line.TrimStart();
                return t.Length > 1 && char.IsDigit(t[0]) && t.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4;
            });

            return findings > 0 && !CriticHasRoleDrift(normalized, taskType);
        }

        private static bool IsRepetitionLoop(string newOutput, string previousOutput)
        {
            if (string.IsNullOrWhiteSpace(newOutput) || string.IsNullOrWhiteSpace(previousOutput))
                return false;

            string a = newOutput.Trim();
            string b = previousOutput.Trim();
            int lenA = Math.Min(200, a.Length);
            int lenB = Math.Min(200, b.Length);
            if (lenA == 0 || lenB == 0)
                return false;

            string sA = a[..lenA];
            string sB = b[..lenB];
            int same = 0;
            int compare = Math.Min(sA.Length, sB.Length);
            for (int i = 0; i < compare; i++)
            {
                if (char.ToLowerInvariant(sA[i]) == char.ToLowerInvariant(sB[i]))
                    same++;
            }

            double similarity = compare == 0 ? 0 : (double)same / compare;
            return similarity >= 0.85;
        }

        private static string BuildArchitectSummaryFromPlan(string architectOutput)
        {
            if (string.IsNullOrWhiteSpace(architectOutput))
                return "";

            if (IsArchitectArtifactHandoff(architectOutput))
            {
                string type = ExtractSimpleHandoffField(architectOutput, "artifact_type");
                string target = ExtractSimpleHandoffField(architectOutput, "output_target");
                string instruction = ExtractSimpleHandoffField(architectOutput, "builder_instruction");
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(type)) parts.Add("Artifact type: " + type);
                if (!string.IsNullOrWhiteSpace(target)) parts.Add("Target: " + target);
                if (!string.IsNullOrWhiteSpace(instruction)) parts.Add(instruction);
                return string.Join(". ", parts);
            }

            var sentences = new List<string>();
            foreach (var line in architectOutput.Split('\n'))
            {
                string trimmed = line.Trim();
                if (!(trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4))
                    continue;

                string content = trimmed[(sep + 1)..].Trim();
                int end = content.IndexOfAny(['.', '!', '?']);
                string firstSentence = end > 0 ? content[..(end + 1)].Trim() : content;
                if (!string.IsNullOrWhiteSpace(firstSentence))
                    sentences.Add(firstSentence);
            }

            return string.Join(" ", sentences);
        }

        private static string ExtractSimpleHandoffField(string text, string fieldName)
        {
            Match match = Regex.Match(text ?? string.Empty, @"(?im)^\s*" + Regex.Escape(fieldName) + @":\s*(?<value>.+?)\s*$");
            return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
        }

        private static string BuildBuilderSummaryFromCode(string builderOutput)
        {
            if (string.IsNullOrWhiteSpace(builderOutput))
                return "";

            var lines = builderOutput.Split('\n');
            var picks = new List<string>();
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                bool isFunction = line.StartsWith("def ", StringComparison.Ordinal)
                                  || line.Contains(" function ", StringComparison.Ordinal)
                                  || line.StartsWith("function ", StringComparison.Ordinal)
                                  || (line.Contains("(") && line.Contains(")") && line.Contains("{") && (line.Contains("public ") || line.Contains("private ") || line.Contains("static ")));
                if (!isFunction)
                    continue;

                string found = "";
                for (int j = i + 1; j < Math.Min(i + 8, lines.Length); j++)
                {
                    string next = lines[j].Trim();
                    if (next.StartsWith("//") || next.StartsWith("#") || next.StartsWith("/*") || next.StartsWith("\"\"\"") || next.StartsWith("'''"))
                    {
                        found = next.Trim('/', '#', '*', ' ', '"', '\'');
                        break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(found))
                    picks.Add(found);
            }

            return string.Join(" ", picks);
        }

        private static string BuildPipelineHealthSection(CouncilRunContext context)
        {
            // Compact: only include flags that are actually set to reduce token waste
            var flags = new List<string>();
            if (context.ArchitectDriftCorrected) flags.Add("Architect corrected");
            if (context.BuilderDriftCorrected) flags.Add("Builder corrected");
            if (context.BuilderTruncationRecovery) flags.Add("Builder truncation recovered");
            if (context.StaticValidationIssuesFound) flags.Add("Static validation issues");
            if (context.SandboxExceptionsFound) flags.Add("Sandbox exceptions");
            if (context.WebGroundingRequired && HasWebSearchEvidence(context.WebContext)) flags.Add("Web evidence attached");
            if (context.WebGroundingRequired && !HasWebSearchEvidence(context.WebContext)) flags.Add("Web evidence required but unavailable");

            return flags.Count == 0
                ? ""
                : $"[Pipeline Flags] {string.Join("; ", flags)}\n";
        }

        private static string BuildLabeledBlock(string label, string content)
        {
            return $"[[{label}]]\n{content.Trim()}\n[[END {label}]]\n";
        }

        private static string BuildRoleIsolatedPayload(CouncilRole role, ContextStateObject state)
        {
            var sb = new StringBuilder();

            // Keep the compact goal/capability contract at the beginning, where small models are
            // most likely to retain it. The role-specific closing anchor repeats the action at the
            // end, avoiding the weak "important facts lost in the middle" layout.
            if (!string.IsNullOrWhiteSpace(state.TaskContract))
                sb.AppendLine(state.TaskContract.Trim());

            if (!string.IsNullOrWhiteSpace(state.Objective))
                sb.AppendLine(BuildLabeledBlock("OBJECTIVE", state.Objective));

            // Cap user prompt to prevent document-embedded prompts from consuming all context
            string userPrompt = state.UserPrompt;
            if (role == CouncilRole.Critic && userPrompt.Length > 2000)
                userPrompt = userPrompt[..2000] + "\n[...prompt truncated for Critic context budget]";
            sb.AppendLine(BuildLabeledBlock("USER PROMPT", userPrompt));

            if (!string.IsNullOrWhiteSpace(state.WorkspaceContext))
            {
                sb.AppendLine(BuildLabeledBlock("CONNECTED CODEBASE CONTEXT", state.WorkspaceContext));
            }

            if (!string.IsNullOrWhiteSpace(state.CalculatorContext))
            {
                sb.AppendLine(state.CalculatorContext.Trim());
            }

            if (role != CouncilRole.Builder && !string.IsNullOrWhiteSpace(state.WebContext))
            {
                sb.AppendLine(state.WebContext.Trim());
                sb.AppendLine(HasWebSearchEvidence(state.WebContext)
                    ? BuildLabeledBlock("WEB ANSWERING CONTRACT", "Use [[WEB SEARCH DATA]] as authority for current, online, source-backed, or recently changed claims that it actually covers. Do not use prior council outputs, memory, or background knowledge to add unsupported current/source-backed details. Stable non-current background context is allowed when not contradicted by the sources. If evidence is incomplete or off-topic, provide supported facts first and clearly note the gap. For broad current-information requests, synthesize the strongest supported developments from available sources. If evidence conflicts, report the conflict instead of guessing.")
                    : BuildLabeledBlock("WEB ANSWERING CONTRACT", "Web search was attempted but no usable evidence is present. Flag any current, source-backed, legal, medical, financial, release, pricing, policy, URL, or documentation claim that the Builder states from memory. Do not penalize clearly separated stable non-current background context unless it conflicts with provided evidence."));
            }

            if (role == CouncilRole.Builder)
            {
                sb.AppendLine(BuildLabeledBlock("APPROVED ARCHITECTURE", state.ArchitectOutput));
                // Hard boundary: the model must cross this line to begin generating.
                // Placing "YOUR IMPLEMENTATION STARTS HERE" immediately after the spec
                // prevents the model from continuing the Architect's numbered-list pattern.
                sb.AppendLine("▸▸▸ YOUR IMPLEMENTATION STARTS HERE — do NOT re-list, re-plan, or re-state the steps above. Write the implementation directly. ◂◂◂\n");
            }
            else if (role == CouncilRole.Critic)
            {
                // Cap builder output for Critic to prevent context overflow on small windows
                string builderForCritic = state.BuilderOutput;
                if (builderForCritic.Length > 4000)
                    builderForCritic = builderForCritic[..2800] + "\n[...middle truncated for context budget...]\n" + builderForCritic[^1200..];
                sb.AppendLine(BuildLabeledBlock("BUILDER OUTPUT", builderForCritic));
                if (!string.IsNullOrWhiteSpace(state.SandboxLogs))
                {
                    string sandboxForCritic = state.SandboxLogs.Length > 1500
                        ? state.SandboxLogs[..1500] + "\n[...truncated]"
                        : state.SandboxLogs;
                    sb.AppendLine(BuildLabeledBlock("SANDBOX LOGS", sandboxForCritic));
                }
            }

            return sb.ToString();
        }

        private static Dictionary<string, string> BuildSharedVocabulary(string userPrompt, string objective)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string combined = $"{userPrompt}\n{objective}";
            if (string.IsNullOrWhiteSpace(combined))
            {
                return result;
            }

            string[] stop = ["the", "and", "for", "with", "that", "this", "from", "into", "about", "would", "should", "could", "must", "need", "have", "has", "are", "was", "were"];
            var stopSet = new HashSet<string>(stop, StringComparer.OrdinalIgnoreCase);

            var matches = System.Text.RegularExpressions.Regex.Matches(combined, @"\b[A-Za-z_][A-Za-z0-9_]{2,}\b")
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(t => !stopSet.Contains(t))
                .Where(t => t.Contains('_') || t.Any(char.IsDigit) || char.IsUpper(t[0]) || t.Contains("calculate", StringComparison.OrdinalIgnoreCase) || t.Contains("compute", StringComparison.OrdinalIgnoreCase) || t.Contains("function", StringComparison.OrdinalIgnoreCase) || t.Contains("model", StringComparison.OrdinalIgnoreCase))
                .Take(32)
                .ToList();

            var sentences = combined.Split(['.', '\n', '!', '?'], StringSplitOptions.RemoveEmptyEntries);
            foreach (var term in matches)
            {
                string meaning = sentences.FirstOrDefault(s => s.Contains(term, StringComparison.OrdinalIgnoreCase))?.Trim() ?? "Term referenced by user requirement.";
                if (meaning.Length > 140)
                    meaning = meaning[..140] + "...";
                result[term] = meaning;
            }

            return result;
        }

        private static string BuildSharedVocabularySection(Dictionary<string, string> vocabulary)
        {
            if (vocabulary.Count == 0)
            {
                return BuildLabeledBlock("SHARED VOCABULARY", "No explicit domain terms identified.");
            }

            var sb = new StringBuilder();
            foreach (var kvp in vocabulary)
            {
                sb.AppendLine($"- {kvp.Key}: {kvp.Value}");
            }

            return BuildLabeledBlock("SHARED VOCABULARY", sb.ToString());
        }

        private static string BuildPriorKnowledgeBlock(List<SessionHippocampusEntry> entries)
        {
            if (entries.Count == 0)
            {
                return "";
            }

            string compact = SessionHippocampus.BuildPromptContext(entries, 340);
            return string.IsNullOrWhiteSpace(compact)
                ? ""
                : BuildLabeledBlock("PRIOR KNOWLEDGE", compact);
        }

        private static string CapOversizedInjectionBlock(string prompt, string label, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(label))
                return prompt;

            string pattern = $@"\[\[{Regex.Escape(label)}\]\]\s*(?<body>[\s\S]*?)\s*\[\[END {Regex.Escape(label)}\]\]";
            return Regex.Replace(prompt, pattern, match =>
            {
                string body = match.Groups["body"].Value.Trim();
                if (body.Length <= maxChars)
                    return match.Value;

                int cut = body.LastIndexOf(' ', maxChars);
                if (cut <= 0)
                    cut = maxChars;

                string truncated = body[..cut].TrimEnd() + "...";
                return $"[[{label}]]\n{truncated}\n[[END {label}]]";
            }, RegexOptions.IgnoreCase);
        }

        private static string CapOversizedInjections(string prompt)
        {
            string result = prompt ?? string.Empty;
            foreach (string label in CappedInjectionLabels)
            {
                // The retrieved knowledge base is the PRIMARY grounding source on the (fallback) path that
                // uses it, and its size is already bounded upstream by the chunk count. Capping it to the
                // same 1200 chars as transient tool blocks (web/calculator/prior-knowledge) gutted that
                // grounding right before inference and invited the model to fill the gaps — so give it a
                // much larger ceiling. The per-role token-budget guard in ExecuteCouncilRoleAsync still
                // prevents an actual context overflow.
                int maxChars = string.Equals(label, "PROJECT KNOWLEDGE BASE", StringComparison.OrdinalIgnoreCase)
                    ? 6000
                    : string.Equals(label, "WEB SEARCH DATA", StringComparison.OrdinalIgnoreCase)
                        ? 4200
                    : 1200;
                result = CapOversizedInjectionBlock(result, label, maxChars);
            }
            return result;
        }

        private static List<PromptInjectionBlockInfo> CollectPromptInjectionInfos(IEnumerable<(string Role, string Content)> history)
        {
            var infos = new List<PromptInjectionBlockInfo>();
            if (history == null)
                return infos;

            int turnIndex = -1;
            foreach (var item in history)
            {
                if (string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase))
                    turnIndex++;

                if (string.IsNullOrWhiteSpace(item.Content))
                    continue;

                foreach (Match match in LabeledInjectionBlockRegex.Matches(item.Content))
                {
                    infos.Add(new PromptInjectionBlockInfo
                    {
                        Label = match.Groups["label"].Value.Trim(),
                        TurnAge = Math.Max(0, turnIndex),
                        IsCurrentTurn = false,
                        IsCurrentPreflight = false
                    });
                }
            }

            if (infos.Count == 0)
                return infos;

            int latestTurn = infos.Max(i => i.TurnAge);
            return infos.Select(i => new PromptInjectionBlockInfo
            {
                Label = i.Label,
                TurnAge = Math.Max(0, latestTurn - i.TurnAge + 1),
                IsCurrentTurn = false,
                IsCurrentPreflight = false
            }).ToList();
        }

        private static string PruneStaleToolInjections(string prompt, IReadOnlyList<PromptInjectionBlockInfo> injections, string currentUserMessage)
        {
            if (string.IsNullOrWhiteSpace(prompt) || injections == null || injections.Count == 0)
                return prompt;

            string result = prompt;
            foreach (PromptInjectionBlockInfo injection in injections)
            {
                if (injection.IsCurrentTurn || injection.IsCurrentPreflight || injection.TurnAge <= 2)
                    continue;
                if (!StalePrunableInjectionLabels.Contains(injection.Label))
                    continue;

                string pattern = $@"\[\[{Regex.Escape(injection.Label)}\]\]\s*(?<body>[\s\S]*?)\s*\[\[END {Regex.Escape(injection.Label)}\]\]\s*";
                Match match = Regex.Match(result, pattern, RegexOptions.IgnoreCase);
                if (!match.Success)
                    continue;

                double overlap = WebSearchService.GetWordOverlap(match.Groups["body"].Value, currentUserMessage);
                if (overlap < 0.15)
                    result = result.Remove(match.Index, match.Length);
            }

            return result.Trim();
        }

        private static string ApplyPreInferenceContextReduction(string prompt, IReadOnlyList<PromptInjectionBlockInfo> injections, string currentUserMessage)
        {
            string capped = CapOversizedInjections(prompt);
            return PruneStaleToolInjections(capped, injections, currentUserMessage);
        }

        private static string BuildCappedMemoryContent(string content, int maxTokens = 560)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            int maxChars = (int)(maxTokens * AvgCharsPerToken);
            string trimmed = content.Trim();
            return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
        }

        private static string BuildStudyMemoryContent(StudyChunk chunk, StudyChunkResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Document: {chunk.DocumentName} | Chunk {chunk.ChunkIndex}");

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                sb.AppendLine($"Memory: {BuildSingleLineSummary(result.Summary, 220)}");
            }

            if (result.Concepts.Count > 0)
            {
                sb.AppendLine("Signals:");
                foreach (string concept in result.Concepts
                    .Select(c => BuildSingleLineSummary(c, 120))
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(4))
                {
                    sb.AppendLine($"- {concept}");
                }
            }

            return BuildCappedMemoryContent(sb.ToString(), 170);
        }

        private static string BuildSingleLineSummary(string content, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return "";
            }

            string normalized = Regex.Replace(content.Trim(), @"\s+", " ");
            return normalized.Length <= maxChars ? normalized : normalized[..maxChars].TrimEnd() + "...";
        }

        private void WriteArchitectSessionMemory(string architectOutput, int sessionRunIndex)
        {
            foreach (var line in architectOutput.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!(trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4))
                {
                    continue;
                }

                string stepContent = trimmed[(sep + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(stepContent))
                {
                    continue;
                }

                _sessionHippocampus.Write(new SessionHippocampusEntry
                {
                    Content = BuildCappedMemoryContent($"Plan step: {BuildSingleLineSummary(stepContent, 180)}", 90),
                    Source = SessionHippocampusSource.ArchitectOutput,
                    Tag = SessionHippocampusTag.SolutionPattern,
                    Priority = 2,
                    Timestamp = DateTime.Now,
                    SessionRunIndex = sessionRunIndex
                });
            }
        }

        private void WriteBuilderSessionMemory(string builderOutput, int sessionRunIndex)
        {
            if (string.IsNullOrWhiteSpace(builderOutput))
            {
                return;
            }

            var lines = builderOutput.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                bool looksLikeFunction = line.StartsWith("def ", StringComparison.Ordinal)
                    || line.StartsWith("function ", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("(") && line.Contains(")") && line.Contains("{") &&
                       (line.Contains("public ", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("private ", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("protected ", StringComparison.OrdinalIgnoreCase)
                        || line.Contains("static ", StringComparison.OrdinalIgnoreCase));

                if (!looksLikeFunction)
                {
                    continue;
                }

                string annotation = "";
                for (int j = i + 1; j < Math.Min(i + 6, lines.Length); j++)
                {
                    string next = lines[j].Trim();
                    if (next.StartsWith("//") || next.StartsWith("#") || next.StartsWith("/*") || next.StartsWith("\"\"\"") || next.StartsWith("'''"))
                    {
                        annotation = next.Trim('/', '#', '*', ' ', '"', '\'');
                        break;
                    }
                }

                string memoryContent = string.IsNullOrWhiteSpace(annotation)
                    ? $"Implementation pattern: {line}"
                    : $"Implementation pattern: {line} — {BuildSingleLineSummary(annotation, 120)}";

                _sessionHippocampus.Write(new SessionHippocampusEntry
                {
                    Content = BuildCappedMemoryContent(memoryContent, 100),
                    Source = SessionHippocampusSource.BuilderOutput,
                    Tag = SessionHippocampusTag.SolutionPattern,
                    Priority = 2,
                    Timestamp = DateTime.Now,
                    SessionRunIndex = sessionRunIndex
                });
            }
        }

        private void WriteCriticSessionMemory(string criticOutput, int sessionRunIndex)
        {
            foreach (var line in criticOutput.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (!(trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4))
                {
                    continue;
                }

                _sessionHippocampus.Write(new SessionHippocampusEntry
                {
                    Content = BuildCappedMemoryContent($"Failure signal: {BuildSingleLineSummary(trimmed, 180)}", 100),
                    Source = SessionHippocampusSource.CriticOutput,
                    Tag = SessionHippocampusTag.ErrorPattern,
                    Priority = 3,
                    Timestamp = DateTime.Now,
                    SessionRunIndex = sessionRunIndex
                });
            }
        }

        private static TaskComplexity EstimateTaskComplexity(int requirementCount)
        {
            return requirementCount switch
            {
                <= 2 => TaskComplexity.Simple,
                >= 7 => TaskComplexity.Complex,
                _ => TaskComplexity.Moderate
            };
        }

        private static List<string> VerifyFulfillment(CouncilRunContext context, string finalOutput)
        {
            var missing = new List<string>();
            if (context.Decomposition == null || context.Decomposition.Requirements.Count == 0 || string.IsNullOrWhiteSpace(finalOutput))
            {
                return missing;
            }

            string lowerOut = finalOutput.ToLowerInvariant();
            if (context.TaskType == CouncilTaskType.Coding)
            {
                var functionMatches = context.Decomposition.Requirements
                    .SelectMany(r => System.Text.RegularExpressions.Regex.Matches(r, @"\b[A-Za-z_][A-Za-z0-9_]*\b").Select(m => m.Value))
                    .Where(t => t.Contains('_') || t.EndsWith("Service", StringComparison.OrdinalIgnoreCase) || t.StartsWith("get", StringComparison.OrdinalIgnoreCase) || t.StartsWith("set", StringComparison.OrdinalIgnoreCase) || t.StartsWith("calculate", StringComparison.OrdinalIgnoreCase) || t.StartsWith("compute", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(24)
                    .ToList();

                foreach (var fn in functionMatches)
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(finalOutput, $@"\b{System.Text.RegularExpressions.Regex.Escape(fn)}\b"))
                        missing.Add($"Required function/identifier may be missing: {fn}");
                }

                // Identifier-only checking misses ordinary product requirements such as "add a
                // delete button" or "persist tasks". Use conservative lexical evidence as a
                // second pass; this emits a review warning, never silently rewrites user output.
                foreach (string requirement in context.Decomposition.Requirements)
                {
                    if (!RequirementHasImplementationSignal(requirement, finalOutput))
                        missing.Add($"Coding requirement may be unimplemented: {requirement}");
                }
            }
            else
            {
                foreach (var req in context.Decomposition.Requirements)
                {
                    var tokens = System.Text.RegularExpressions.Regex.Matches(req.ToLowerInvariant(), @"\b[a-z][a-z0-9_]{3,}\b")
                        .Select(m => m.Value)
                        .Distinct()
                        .Take(6)
                        .ToList();

                    if (tokens.Count == 0)
                        continue;

                    bool covered = tokens.Any(t => lowerOut.Contains(t, StringComparison.Ordinal));
                    if (!covered)
                    {
                        if (context.TaskType == CouncilTaskType.Research)
                            missing.Add($"Research requirement may be unaddressed: {req}");
                        else if (context.TaskType == CouncilTaskType.Analysis)
                            missing.Add($"Analysis dimension may be unaddressed: {req}");
                        else
                            missing.Add($"Requirement may be unaddressed: {req}");
                    }
                }
            }

            return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> BuildFinalVerificationFailures(CouncilRunContext context, string finalOutput)
        {
            var failures = new List<string>();
            string output = finalOutput ?? string.Empty;

            if (context.IsArtifactCanvasRequest)
                return BuildProjectCanvasFinalVerificationFailures(context, output);

            if (context.TaskType == CouncilTaskType.Coding)
            {
                if (!context.BuilderRoutedToCanvas)
                    failures.Add("No implementation from this run was routed to Project Canvas.");

                if (!CanvasHasRealContent(output))
                    failures.Add("Project Canvas has no generated implementation.");

                CodeOutputDetection code = DetectCodeOutput(output);
                if (!code.IsCode)
                    failures.Add("Final output is not executable source code.");

                string request = $"{context.UserPrompt} {context.Objective}";
                if (RequestsCSharpImplementation(request) && !string.Equals(DetectLanguage(output), "c#", StringComparison.OrdinalIgnoreCase))
                    failures.Add("The request asked for a C# implementation, but the final artifact is not detected as C# source.");

                if (RequestsExecutableTests(request) && !ContainsExecutableTestSignal(output))
                    failures.Add("The request asked for executable tests, but no test method/assertion structure was detected.");
            }

            failures.AddRange(VerifyFulfillment(context, output));
            if (context.TaskType == CouncilTaskType.Coding && !string.IsNullOrWhiteSpace(output) && DetectCodeOutput(output).IsCode)
                failures.AddRange(RunStaticValidation(output).Select(finding => "Static validation: " + finding));

            return failures.Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
        }

        private static bool RequestsCSharpImplementation(string request)
            => Regex.IsMatch(request ?? string.Empty, @"(?<!\w)(?:c#|csharp|\.cs|csproj)(?!\w)", RegexOptions.IgnoreCase);

        private static bool RequestsExecutableTests(string request)
            => Regex.IsMatch(request ?? string.Empty, @"\b(?:unit tests?|executable tests?|xunit|nunit|mstest|assertions?|test methods?)\b", RegexOptions.IgnoreCase);

        private static bool ContainsExecutableTestSignal(string output)
            => Regex.IsMatch(output ?? string.Empty, @"\[(?:Fact|Theory|Test|TestMethod)\]|\bAssert\.|\bAssert\s*\(|\bTestMethod\b", RegexOptions.IgnoreCase);

        private string GetPreviousRoleOutput(string role)
        {
            for (int i = _chatHistory.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_chatHistory[i].Role, role, StringComparison.OrdinalIgnoreCase))
                    return _chatHistory[i].Content;
            }

            return "";
        }

        private static bool CriticFoundIssues(string criticOutput)
        {
            if (string.IsNullOrWhiteSpace(criticOutput))
                return false;

            if (CriticContractParser.IsExplicitCleanPass(criticOutput)
                && !CriticContractParser.ContainsNumberedFindingPattern(criticOutput))
                return false;

            if (CriticContractParser.IsStructuredArtifactPass(criticOutput))
                return false;

            string lower = criticOutput.ToLowerInvariant();

            string[] issueIndicators = [
                "error", "issue", "incorrect", "missing", "broken", "fix",
                "bug", "flaw", "defect", "wrong", "fail", "undefined",
                "critical", "warning", "severity", "problem", "incomplete",
                "should be", "must be", "needs to", "replace with",
                "line ", "not found", "unhandled", "unreachable", "unused",
                "null reference", "off-by-one", "out of range", "syntax error",
                "logic error", "type mismatch", "not implemented"
            ];

            return issueIndicators.Any(indicator => lower.Contains(indicator));
        }

        private static string BuildCriticPayload(CouncilRunContext context, string sandboxResult)
        {
            var payload = new StringBuilder();
            string taskContract = BuildCouncilGoalContractBlock(context.GoalContract);
            if (!string.IsNullOrWhiteSpace(taskContract))
                payload.AppendLine(taskContract);
            if (context.IsCloudExecution)
                payload.AppendLine(BuildCloudVerificationPacket(context));

            if (context.StaticValidationFindings.Count > 0)
            {
                var sb = new StringBuilder();
                sb.AppendLine("The following issues were detected automatically. Confirm each one and check for additional problems:");
                for (int i = 0; i < context.StaticValidationFindings.Count; i++)
                    sb.AppendLine($"{i + 1}. {context.StaticValidationFindings[i]}");
                payload.AppendLine(BuildLabeledBlock("PRE-FLAGGED ISSUES", sb.ToString()));
            }

            // Cloud council models have ~131k-token windows; the tight local caps starved the cloud
            // Critic of the very evidence it audits (it literally could not see most of a large
            // artifact). Budgets stay unchanged for local models.
            int sandboxCap = context.IsCloudExecution ? 16000 : 2000;
            int requestCap = context.IsCloudExecution ? 12000 : 2000;

            if (!string.IsNullOrWhiteSpace(sandboxResult))
            {
                string truncatedSandbox = sandboxResult.Length > sandboxCap
                    ? sandboxResult[..sandboxCap] + "\n[...truncated]"
                    : sandboxResult;
                payload.AppendLine(BuildLabeledBlock("SANDBOX OUTPUT", truncatedSandbox));
            }

            if (context.FormulaChecklist.Count > 0)
            {
                var formula = new StringBuilder();
                formula.AppendLine("Verify each formula below against the Builder's code. Trace the math with example values.");
                for (int i = 0; i < context.FormulaChecklist.Count; i++)
                {
                    var item = context.FormulaChecklist[i];
                    formula.AppendLine($"{i + 1}. [{item.StepReference}]");
                    formula.AppendLine($"   Formula: {item.Formula}");
                    formula.AppendLine($"   Unit conversions: {item.UnitConversions}");
                    formula.AppendLine($"   Expected range: {item.ExpectedRange}");
                }
                payload.AppendLine(BuildLabeledBlock("FORMULA CHECKLIST", formula.ToString()));
            }

            if (!string.IsNullOrWhiteSpace(context.CalculatorContext))
            {
                payload.AppendLine(context.CalculatorContext.Trim());
            }

            string request = string.IsNullOrWhiteSpace(context.Objective)
                ? context.UserPrompt
                : context.UserPrompt + "\n[OBJECTIVE] " + context.Objective;
            // Trim the request to prevent very long user prompts from consuming all context
            if (request.Length > requestCap)
                request = request[..requestCap] + "\n[...request truncated for context budget]";
            payload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", request));

            // For document tasks, inject a *summary reference* instead of raw document content.
            // The Critic's job is to review the Builder's OUTPUT against the Architect's PLAN,
            // not to re-read the entire source document. Injecting the full document was the
            // primary cause of context overflow and prompt truncation on smaller context windows.
            if (context.IsDocumentTask && !string.IsNullOrWhiteSpace(context.DocumentContent))
            {
                // Provide just enough document reference for the Critic to verify factual grounding.
                // Local: 1500 chars to protect small context windows. Cloud: 6000 chars — the cloud
                // Critic has the headroom, and more source text means better hallucination checks.
                int maxDocChars = context.IsCloudExecution ? 48000 : 1500;
                string docSnippet = context.DocumentContent.Length > maxDocChars
                    ? context.DocumentContent[..maxDocChars] + "\n[...document truncated — Critic should verify Builder output against Architect plan steps]"
                    : context.DocumentContent;
                payload.AppendLine(BuildLabeledBlock("DOCUMENT REFERENCE (excerpt)", docSnippet));
            }

            if (!string.IsNullOrWhiteSpace(context.BuilderOutput))
            {
                string builderText = context.BuilderOutput;
                // Keep start and end for review when over budget. Cloud gets a much larger window so
                // the Critic can actually audit big artifacts instead of reviewing 5k of a 20k file.
                int builderCap = context.IsCloudExecution ? 96000 : 5000;
                int builderHead = context.IsCloudExecution ? 64000 : 3500;
                int builderTail = context.IsCloudExecution ? 32000 : 1500;
                if (builderText.Length > builderCap)
                {
                    builderText = builderText[..builderHead] + "\n[...middle section truncated for context budget...]\n" + builderText[^builderTail..];
                }

                if (context.BuilderOutputTruncated)
                {
                    builderText += "\n[WARNING] The Builder's output appears to be truncated (unclosed braces/functions detected).";
                }
                payload.AppendLine(BuildLabeledBlock("BUILDER OUTPUT", builderText));
            }

            if (!string.IsNullOrWhiteSpace(context.ArchitectOutput))
            {
                var plan = new StringBuilder();
                foreach (var line in context.ArchitectOutput.Split('\n'))
                {
                    string trimmed = line.TrimStart();
                    if (trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4)
                    {
                        string stepLine = trimmed.Length > 200 ? trimmed[..200] + "..." : trimmed;
                        plan.AppendLine(stepLine);
                    }
                }
                payload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", plan.ToString()));
            }

            if (context.PipelineMetadata.Count > 0)
            {
                var flags = new List<string>();
                foreach (var meta in context.PipelineMetadata)
                {
                    if (meta.RequiredReformatRetry) flags.Add($"{meta.StageName}: required reformat");
                    if (meta.TruncationDetected) flags.Add($"{meta.StageName}: truncation detected");
                }
                if (flags.Count > 0)
                {
                    payload.AppendLine(BuildLabeledBlock("PIPELINE FLAGS", string.Join("; ", flags)));
                }
            }

            return payload.ToString();
        }

        private static int CountRevisionBlockingCriticIssues(CriticReport report, bool includeLowSeverity)
        {
            if (report.Issues == null || report.Issues.Count == 0)
                return 0;

            return report.Issues.Count(issue => IsRevisionBlockingCriticIssue(issue, includeLowSeverity));
        }

        private static bool IsRevisionBlockingCriticIssue(CriticIssue issue, bool includeLowSeverity)
        {
            string severity = (issue.Severity ?? string.Empty).Trim().ToLowerInvariant();
            if (severity is "critical" or "high" or "medium")
                return true;

            string text = $"{issue.Summary} {issue.Evidence} {issue.SuggestedFix}".ToLowerInvariant();
            if (text.Contains("not render", StringComparison.Ordinal)
                || text.Contains("non-render", StringComparison.Ordinal)
                || text.Contains("broken", StringComparison.Ordinal)
                || text.Contains("runtime", StringComparison.Ordinal)
                || text.Contains("syntax", StringComparison.Ordinal)
                || text.Contains("incorrect", StringComparison.Ordinal)
                || text.Contains("wrong", StringComparison.Ordinal)
                || text.Contains("missing", StringComparison.Ordinal)
                || text.Contains("placeholder", StringComparison.Ordinal)
                || text.Contains("todo", StringComparison.Ordinal)
                || text.Contains("external", StringComparison.Ordinal)
                || text.Contains("cdn", StringComparison.Ordinal)
                || text.Contains("not self-contained", StringComparison.Ordinal)
                || text.Contains("no code fence", StringComparison.Ordinal)
                || text.Contains("multiple code fences", StringComparison.Ordinal)
                || text.Contains("text outside", StringComparison.Ordinal)
                || text.Contains("format mismatch", StringComparison.Ordinal)
                || text.Contains("standalone static svg", StringComparison.Ordinal)
                || text.Contains("mathematically", StringComparison.Ordinal)
                || text.Contains("formula", StringComparison.Ordinal))
            {
                return true;
            }

            return includeLowSeverity && severity == "low";
        }

        private async Task SendQueryAsync()
        {
            if (_isStudySessionRunning)
            {
                AppendChat("system", "Study Session is running. Chat is temporarily locked.");
                return;
            }

            if (_isProcessing)
            {
                AppendChat("system", "Already processing...");
                return;
            }

            if (string.IsNullOrWhiteSpace(QueryInput.Text) && !string.IsNullOrWhiteSpace(_lastCancelledRunPrompt))
            {
                QueryInput.Text = _lastCancelledRunPrompt;
            }

            if (string.IsNullOrWhiteSpace(QueryInput.Text))
            {
                AppendChat("error", "Query is empty.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_council[CouncilRole.Architect].ModelPath) &&
                string.IsNullOrWhiteSpace(_council[CouncilRole.Builder].ModelPath) &&
                string.IsNullOrWhiteSpace(_council[CouncilRole.Critic].ModelPath) &&
                !_isCloudModeEnabled)
            {
                AppendChat("error", "Load at least one council model.");
                return;
            }

            if (_isCloudModeEnabled)
            {
                LoadOpenRouterKeyForWorkplace();
                if (!_openRouterChatService.HasValidKey)
                {
                    AppendChat("error", "Workplace cloud mode needs a valid OpenRouter API key in Settings.");
                    return;
                }
            }

            _isProcessing = true;
            SendButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            _systemNotifications.Clear();
            _activityLogs.Clear();
            _unreadNotificationCount = 0;
            NotificationBadge.Visibility = Visibility.Collapsed;
            NotificationBadgeText.Text = "0";
            NotificationDropdownPanel.Visibility = Visibility.Collapsed;
            _isNotificationPanelOpen = false;
            WorkplaceErrorNotificationBar.Visibility = Visibility.Collapsed;
            WorkplaceErrorNotificationText.Text = string.Empty;
            CouncilConfidenceBanner.Visibility = Visibility.Collapsed;
            RefineButton.Visibility = Visibility.Collapsed;
            RevisionNoticeBlock.Text = "Issues were found and the output was revised.";
            RevisionNoticeBlock.Visibility = Visibility.Collapsed;
            if (!_isStudySessionRunning)
            {
                StudySessionNotificationBar.Visibility = Visibility.Collapsed;
                StudySessionStatusText.Text = "Study Session idle";
                StudySessionDetailText.Text = string.Empty;
                StudySessionPhaseText.Text = "Idle";
                StudySessionProgressBar.Value = 0;
                StudySessionProgressLabel.Text = "0%";
                StudySessionEntryCountText.Text = "0";
            }

            string userQuery = QueryInput.Text.Trim();
            _submittedRunPrompt = userQuery;
            _lastCancelledRunPrompt = string.Empty;
            string userInstruction = userQuery;
            QueryInput.Text = string.Empty;
            Guid? refinementParentId = null;
            bool refinementPass = false;
            string previousFinalForDiff = _lastFinalOutput;
            if (_isRefinementMode && _lastRunContext != null)
            {
                refinementPass = true;
                refinementParentId = _taskHistory.FirstOrDefault()?.Id;
                userQuery = $"Refinement instruction: {userQuery}\n\n[ORIGINAL PROMPT]\n{_lastRunContext.UserPrompt}\n\n[PRIOR ARCHITECT]\n{_lastRunContext.ArchitectOutput}\n\n[PRIOR BUILDER]\n{_lastRunContext.BuilderOutput}";
                _isRefinementMode = false;
                RefinementModeBanner.Visibility = Visibility.Collapsed;
            }
            AppendChat("user", userQuery);
            _chatHistory.Add(("user", userQuery));
            UpdateWorkplaceTokenUsageIndicator();

            // On a single GPU the Normal-Chat model and the council role models compete for the
            // same VRAM/RAM. Loading role models on top of a still-resident chat model is the
            // "Failed to load model" relay error (a second copy of an 8B model overflows VRAM,
            // the plan drops to CPU, and the CPU copy then overflows RAM). For a LOCAL run, free
            // the chat model first; MainWindow restores it when the user returns to the chat view.
            if (!_isCloudModeEnabled && ReleaseHostChatModelAsync != null)
            {
                try
                {
                    await ReleaseHostChatModelAsync(CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    LogActivity($"Chat model release before relay skipped: {releaseEx.Message}");
                }
            }

            StartPipelineProgress();

            string objective = ReadObjectiveText();
            UpdateContextPressurePreview(userQuery, objective, 0);

            string proactiveWebContext = string.Empty;

            // ═══════════════════════════════════════════════════════
            // DOCUMENT INPUT RESOLUTION — runs before everything else
            // When documents are present, the user's prompt is the instruction,
            // the document is the subject. These are never the same thing.
            // ═══════════════════════════════════════════════════════
            bool documentsLoaded = _documents.Count > 0;
            bool shouldUseDocuments = ShouldUseDocumentContext(userQuery, objective);
            bool isDocumentTask = documentsLoaded && shouldUseDocuments;
            // Engage sticky grounding so subsequent follow-up turns keep using the document even when
            // they don't name it (see ShouldUseDocumentContext).
            if (isDocumentTask)
                _documentContextEngaged = true;
            string calculatorContext = "";
            if (CalculatorToolAgent.TryBuildContext($"{userQuery}\n{objective}", out var calcContext, out var calcSignal))
            {
                calculatorContext = calcContext;
                AppendChat("system", calcSignal);
            }
            string enrichedUserPrompt = string.IsNullOrWhiteSpace(calculatorContext)
                ? userQuery
                : userQuery + "\n\n" + calculatorContext;

            string intentQuery = refinementPass ? userInstruction : userQuery;
            bool directArtifactCanvasRequest = DetectArtifactCanvasIntent(intentQuery, objective);
            bool projectCanvasFollowUp = DetectArtifactCanvasFollowUpIntent(intentQuery, _lastRunContext, ProjectCanvasEditor?.Text);
            ArtifactRenderInfo existingCanvasArtifact = ArtifactRenderService
                .DetectForCanvas(ProjectCanvasEditor?.Text ?? string.Empty, _lastSandboxOutput);
            bool existingCanvasIsRenderable = existingCanvasArtifact.SupportsPreview;
            bool continuesRenderableArtifact = _lastRunContext?.IsArtifactCanvasRequest == true || existingCanvasIsRenderable;
            bool isArtifactCanvasRequest = directArtifactCanvasRequest
                || (projectCanvasFollowUp && continuesRenderableArtifact);
            CouncilTaskType taskType = isDocumentTask
                ? CouncilTaskType.Document
                : projectCanvasFollowUp && !isArtifactCanvasRequest
                    ? CouncilTaskType.Coding
                    : DetectTaskType(intentQuery, objective, isArtifactCanvasRequest);
            if (!isDocumentTask
                && _connectedWorkspace.CodebaseEditAccessEnabled
                && LooksLikeWorkspaceCodingRequest(intentQuery, objective))
            {
                taskType = CouncilTaskType.Coding;
            }
            if (refinementPass && _lastRunContext != null && !directArtifactCanvasRequest)
            {
                taskType = IsExplicitCodingImplementationRequest(intentQuery, objective)
                    ? CouncilTaskType.Coding
                    : _lastRunContext.TaskType;
            }
            var (canvasViewportWidth, canvasViewportHeight) = GetCanvasArtifactViewportSize();
            string preferredArtifactFormatHint = isArtifactCanvasRequest
                ? GetPreferredArtifactFormatHint(userQuery, objective, canvasViewportWidth)
                : string.Empty;
            if (projectCanvasFollowUp
                && existingCanvasIsRenderable
                && !RequestsExplicitArtifactFormatChange(userQuery))
            {
                preferredArtifactFormatHint = GetCanvasIterationFormatHint(existingCanvasArtifact.Kind, canvasViewportWidth);
            }
            if (projectCanvasFollowUp
                && !directArtifactCanvasRequest
                && !string.IsNullOrWhiteSpace(_lastRunContext?.PreferredArtifactFormatHint))
            {
                preferredArtifactFormatHint += " Maintain this artifact format unless the user's new instruction explicitly changes it.";
            }
            string webGroundingPrompt = string.IsNullOrWhiteSpace(objective)
                ? userQuery
                : $"{userQuery}\n{objective}";
            bool webGroundingRequired = _isWebSearchEnabled
                && _webSearchService.RequiresFreshOrSourceBackedGrounding(webGroundingPrompt);
            _activeCouncilWebPrompt = webGroundingPrompt;

            var runContext = new CouncilRunContext
            {
                UserPrompt = userQuery,
                Objective = objective,
                CalculatorContext = calculatorContext,
                CalculatorUsed = !string.IsNullOrWhiteSpace(calculatorContext),
                TaskType = taskType,
                IsDocumentTask = isDocumentTask,
                IsArtifactCanvasRequest = isArtifactCanvasRequest,
                IsProjectCanvasIteration = projectCanvasFollowUp,
                ExistingCanvasArtifactKind = existingCanvasArtifact.Kind,
                PreferredArtifactFormatHint = preferredArtifactFormatHint,
                PreviousArtifactRequest = _lastRunContext?.IsArtifactCanvasRequest == true ? _lastRunContext.UserPrompt : string.Empty,
                PreviousArtifactFormatHint = _lastRunContext?.IsArtifactCanvasRequest == true ? _lastRunContext.PreferredArtifactFormatHint : string.Empty,
                CanvasViewportWidth = canvasViewportWidth,
                CanvasViewportHeight = canvasViewportHeight,
                WebGroundingRequired = webGroundingRequired,
                IsCloudExecution = CanUseCloudCouncil
            };
            _latestCouncilReactiveWebContext = string.Empty;
            _activeCouncilRunContext = runContext;

            // Artifact iteration: when this is a canvas request and the canvas already holds a real
            // artifact (not the placeholder), capture it so the Builder edits the existing artifact
            // instead of regenerating from scratch. Runs on the UI thread here, so the read is direct.
            if (projectCanvasFollowUp)
            {
                string currentCanvas = ProjectCanvasEditor?.Text ?? string.Empty;
                string trimmedCanvas = currentCanvas.Trim();
                bool canvasHasRealArtifact = CanvasHasRealContent(trimmedCanvas);
                if (canvasHasRealArtifact)
                {
                    int builderContext = (int)GetRoleContextSize(CouncilRole.Builder);
                    // A replacement needs room for both the original source and the generated
                    // replacement. Scale the source allowance to the actual role context instead of
                    // using the old fixed 6k cap that dropped ordinary HTML artifacts mid-file.
                    int artifactIterationCap = CanUseCloudCouncil
                        ? 300000
                        : Math.Clamp((builderContext - 3500) * 2, 6000, 40000);
                    runContext.CurrentArtifactForIteration = currentCanvas.Length > artifactIterationCap
                        ? currentCanvas[..artifactIterationCap]
                        : currentCanvas;
                    runContext.CurrentArtifactForIterationWasTruncated = currentCanvas.Length > artifactIterationCap;
                    if (currentCanvas.Length > artifactIterationCap)
                        LogActivity($"Canvas iteration: source ({currentCanvas.Length} chars) exceeds the safe {artifactIterationCap}-char local context allowance; mutation verification will prevent a partial replacement from overwriting it.");
                    else
                        LogActivity($"Canvas iteration: current source ({trimmedCanvas.Length} chars) attached for Builder editing.");
                }
            }

            int activeRunIndex = _completedCouncilRunCount + 1;
            int sandboxAutoFixRetries = 0;
            bool sandboxFailed = false;

            var contextState = new ContextStateObject
            {
                UserPrompt = enrichedUserPrompt,
                Objective = objective,
                CalculatorContext = calculatorContext,
                WorkspaceContext = BuildConnectedWorkspaceContext(userQuery, objective, runContext)
            };
            if (runContext.IsWorkspaceTask)
            {
                AppendChat("system", runContext.WorkspaceFilesRead.Count > 0
                    ? $"Connected codebase context attached: {runContext.WorkspaceFilesRead.Count} file(s)."
                    : "Codebase Edit Access is enabled, but no readable local code files are connected yet.");
            }
            CouncilBaseStateVault? baseStateVault = null;

            UpdateTaskTypeBadge(taskType);
            UpdateStageIndicator(null, false, false, false);
            RevisionNoticeBlock.Visibility = Visibility.Collapsed;

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            try
            {
                await CompressChatHistoryIfNeededAsync(token);

                if (_isWebSearchEnabled)
                {
                    proactiveWebContext = await BuildPromptWebContextAsync(userQuery, objective, token);
                    if (!string.IsNullOrWhiteSpace(proactiveWebContext))
                    {
                        contextState.WebContext = proactiveWebContext;
                        runContext.WebContext = proactiveWebContext;
                        if (!HasWebSearchEvidence(proactiveWebContext))
                            AppendChat("warning", "Web search returned no usable evidence; current/source-backed facts will not be answered from memory.");
                        AppendChat("memory", "🌐 Web search context attached to this council run.");
                    }
                }

                // ═══════════════════════════════════════════════════════
                // DOCUMENT CONTENT RESOLUTION — first step in pipeline
                // ═══════════════════════════════════════════════════════
                string documentContent = "";
                if (runContext.IsDocumentTask)
                {
                    var resolvedDocs = await ResolveWorkspaceDocumentContentAsync();
                    documentContent = resolvedDocs.Content;
                    runContext.DocumentContent = documentContent;
                    runContext.DocumentFileNames = resolvedDocs.FileNames;

                    int docTokenEstimate = EstimateTokenCount(documentContent);
                    LogActivity($"Document task: {runContext.DocumentFileNames.Count} file(s), {documentContent.Length} chars (~{docTokenEstimate} tokens) resolved.");
                    AppendChat("system", $"Document content resolved: {runContext.DocumentFileNames.Count} file(s), ~{docTokenEstimate} tokens.");
                }

                LogActivity("Retrieving knowledge base context...");
                int maxChunks = _documentRetriever.CalculateMaxChunksForContext((int)_contextSize);
                var relevantChunks = shouldUseDocuments
                    ? _documentRetriever.RetrieveRelevantChunks(userQuery, maxChunks)
                    : new List<DocumentChunk>();
                var finalChunks = shouldUseDocuments ? MergeWithPriority(relevantChunks, maxChunks) : new List<DocumentChunk>();
                string knowledgePacket = BuildKnowledgePacket(finalChunks, _nextPromptPriorityConcept);
                LogActivity($"Knowledge context ready: {finalChunks.Count} chunks from {_documents.Count} documents.");
                UpdateContextPressurePreview(userQuery, objective, finalChunks.Count);

                AppendChat("system", shouldUseDocuments
                    ? $"Documents: {_documents.Count} | Retrieved context chunks: {finalChunks.Count}"
                    : "Document context skipped (request appears unrelated to uploaded documents).");

                string objectiveClause = string.IsNullOrWhiteSpace(objective)
                    ? ""
                    : $"\n[PRIMARY OBJECTIVE] {objective}\nAll outputs must align with this objective. Treat it as the highest-priority instruction.\n";

                if (!string.IsNullOrWhiteSpace(objective))
                    LogActivity($"Objective pinned: \"{(objective.Length > 60 ? objective[..60] + "..." : objective)}\"");

                LogActivity($"Task type detected: {taskType}.");

                bool isCalcTask = DetectCalculationTask(userQuery, objective);
                runContext.IsCalculationTask = isCalcTask;
                if (isCalcTask)
                    LogActivity("Calculation-heavy task detected — formula-aware prompting enabled.");

            bool userInputIntentDetected = DetectDynamicUserInputIntent($"{userQuery}\n{objective}");
            int sandboxScore = ScoreSandboxEligibility($"{userQuery}\n{objective}");
            runContext.PythonSandboxScore = sandboxScore;
            runContext.PythonSandboxEligible = sandboxScore >= SandboxEligibilityThreshold && (taskType == CouncilTaskType.Coding || isCalcTask);
            if (runContext.PythonSandboxEligible)
            {
                var sandboxSeeds = ExtractSandboxVariableSeeds($"{userQuery}\n{objective}", userInputIntentDetected);
                runContext.PythonSandboxPreamble = BuildPythonPreamble(sandboxSeeds);
                runContext.PythonSystemPromptInjection = BuildSandboxSystemPromptInjection(sandboxSeeds, userInputIntentDetected);
                string normalizedExpressionNote = BuildNormalizedExpressionNote($"{userQuery}\n{objective}");
                if (!string.IsNullOrWhiteSpace(normalizedExpressionNote))
                    runContext.PythonSystemPromptInjection += "\n" + normalizedExpressionNote;
                LogActivity($"Builder Python sandbox primed (score={sandboxScore}, vars={sandboxSeeds.Count}).");
            }

                // ═══════════════════════════════════════════════════════
                // TOPIC-SHIFT DETECTION — invalidate stale session memory
                // when the user switches subjects mid-conversation
                // ═══════════════════════════════════════════════════════
                DetectAndHandleTopicShift(userQuery, objective);

                // ═══════════════════════════════════════════════════════
                // PRE-FLIGHT — Decompose user prompt before Architect
                // ═══════════════════════════════════════════════════════
                PreFlightDecomposition decomposition;
                if (runContext.IsDocumentTask)
                {
                    decomposition = DecomposeDocumentTask(userQuery, objective, documentContent, runContext.DocumentFileNames);
                    LogActivity($"Document pre-flight: problem references document, {decomposition.Requirements.Count} requirements extracted.");
                }
                else
                {
                    decomposition = DecomposeUserPrompt(userQuery, objective, taskType);
                }
                runContext.Decomposition = decomposition;
                runContext.GoalContract = BuildCouncilGoalContract(
                    runContext,
                    decomposition,
                    _isWebSearchEnabled,
                    _sessionHippocampus.GetMetadata().TotalEntryCount);
                contextState.TaskContract = BuildCouncilGoalContractBlock(runContext.GoalContract)
                    + BuildCouncilCapabilityCard(_isWebSearchEnabled, runContext.IsCloudExecution);
                runContext.Complexity = EstimateTaskComplexity(decomposition.Requirements.Count);
                runContext.SharedVocabulary = BuildSharedVocabulary(userQuery, objective);
                _activeTaskComplexity = runContext.Complexity;
                LogActivity($"Pre-flight: {decomposition.Requirements.Count} requirements, {decomposition.Constraints.Count} constraints extracted.");
                LogActivity($"Task contract pinned: {runContext.GoalContract.AcceptanceChecks.Count} acceptance checks, Canvas={runContext.IsArtifactCanvasRequest}.");
                LogActivity($"Task complexity: {runContext.Complexity}.");

                string sharedVocabularySection = BuildSharedVocabularySection(runContext.SharedVocabulary);
                string sharedVocabularySystemNote = "\n[SHARED VOCABULARY]\nUse these terms exactly as named by the user. Do not rename identifiers unless explicitly instructed.\n" + sharedVocabularySection;
                string webSearchSystemNote = BuildCouncilWebSystemNote(contextState.WebContext, runContext.WebGroundingRequired);
                string builderWebPauseSystemNote = BuildBuilderWebPauseSystemNote();
                // Base-state vault DISABLED: it pre-loaded shared content into each role's KV cache
                // (via LoadState) but the per-role token-budget check (ExecuteCouncilRoleAsync) only
                // counts system+payload, NOT those already-resident base-state tokens. The real
                // decode therefore overflowed the context → llama_decode 'InvalidInputBatch' →
                // batch-recovery retry → cascading model-reload failure ("Failed to load model").
                // It was only ever a latency optimization (each role payload already carries its own
                // content, so simple runs work without it), and on a memory-constrained GPU the
                // extra per-role load+decode+state-save was itself heavy VRAM churn. Leaving it off
                // makes local council runs reliable; re-enabling needs base-state tokens folded into
                // the budget AND headroom verified against the role context window first.
                baseStateVault = null;
                LogActivity("Base state bootstrap disabled — each role decodes its own payload (avoids KV-budget overflow on local models).");

                // ═══════════════════════════════════════════════════════
                // STAGE 1 — Architect: receives decomposed input
                // ═══════════════════════════════════════════════════════
                string architectOutput = "";
                bool isDocumentGrounded = taskType != CouncilTaskType.Coding && finalChunks.Count > 0;

                // Document task shortcut: skip Architect model entirely for document tasks.
                // Small local models consistently produce procedural how-to guides instead of
                // content-specific plans. Use deterministic plan generation instead.
                if (runContext.IsDocumentTask && string.IsNullOrWhiteSpace(_council[CouncilRole.Architect].ModelPath) && !CanUseCloudCouncil)
                {
                    int docTokens = (int)Math.Ceiling(documentContent.Length / AvgCharsPerToken);
                    int builderCtx = (int)GetRoleContextSize(CouncilRole.Builder);
                    architectOutput = BuildDocumentTaskPlan(userQuery, finalChunks, docTokens, builderCtx);
                    runContext.ArchitectOutput = architectOutput;
                    runContext.ArchitectThinking = "";
                    runContext.ArchitectStepCount = CountArchitectSteps(architectOutput);
                    runContext.PipelineMetadata.Add(new StageMetadata { StageName = "Architect" });
                    LogActivity($"Document synthesis task detected — deterministic content plan ({runContext.ArchitectStepCount} steps). Architect model bypassed.");
                    AppendChat("architect", architectOutput);
                    _chatHistory.Add(("architect", architectOutput));
                    UpdateWorkplaceTokenUsageIndicator();
                    UpdateStageIndicator(null, true, false, false);
                }
                else if (HasEffectiveLocalRoleModel(CouncilRole.Architect) || CanUseCloudCouncil)
                {
                    UpdateStageIndicator(CouncilRole.Architect, false, false, false);
                    RelayStatusBlock.Text = "Relay: Architect is planning...";
                    LogActivity("Architect relay started — analyzing request...");

                    var architectConfig = GetEffectiveRoleConfig(CouncilRole.Architect);
                    bool smallLocalArchitectModel = IsSmallLocalCouncilModel(architectConfig.ModelPath ?? architectConfig.DisplayName, _isCloudModeEnabled);

                    string architectSystem = GetEmbeddedSystemPrompt(CouncilRole.Architect)
                        + objectiveClause
                        + (runContext.IsArtifactCanvasRequest && !runContext.IsWorkspaceTask
                            ? BuildArtifactTaskTypeBoost(CouncilRole.Architect, runContext)
                            : GetTaskTypeBoost(taskType, CouncilRole.Architect))
                        + (runContext.IsArtifactCanvasRequest && !runContext.IsWorkspaceTask ? BuildArtifactCanvasBoost(CouncilRole.Architect, runContext.PreferredArtifactFormatHint, runContext) : "")
                        + (runContext.IsArtifactCanvasRequest && !runContext.IsWorkspaceTask && smallLocalArchitectModel ? BuildSmallModelArtifactAssist(CouncilRole.Architect, runContext.PreferredArtifactFormatHint, runContext) : "")
                        + (isCalcTask ? GetCalculationBoost(CouncilRole.Architect) : "")
                        + (runContext.CalculatorUsed ? "\n[CALCULATOR TOOL] Use values from [[CALCULATOR TOOL RESULTS]] exactly when creating steps. Do not invent conflicting numbers." : "")
                        + BuildArchitectContract(taskType, runContext.IsArtifactCanvasRequest, runContext.IsWorkspaceTask)
                        + (runContext.IsDocumentTask
                            ? "\n[CRITICAL — DOCUMENT CONTENT PROVIDED] Full source text is in [[DOCUMENT CONTENT]]. " +
                              "Your plan MUST describe operations on this specific content (extract, compare, synthesize, summarize specific sections). " +
                              "Do NOT produce a procedural how-to guide about document handling. " +
                              "Do NOT include steps about opening files, reading files, OCR, parsing tools, or external access. " +
                              "Producing a generic document-handling guide is a CRITICAL ROLE FAILURE. " +
                              "If document is large, produce section-by-section steps and include a final synthesis step."
                            : (isDocumentGrounded
                                ? "\n[CRITICAL — DOCUMENT ALREADY INGESTED] The user's uploaded files (PDFs, TXTs, etc.) have ALREADY been " +
                                  "read and their full text is provided in the [[PROJECT KNOWLEDGE BASE]] block in the user payload. " +
                                  "You MUST NOT output steps about opening files, reading documents, extracting text, using OCR, " +
                                  "using PDF viewers, or any other file-handling procedure. " +
                                  "Instead, each step should name a CONTENT SECTION to write about using the provided text " +
                                  "(e.g., 'Summarize the main findings from the source text', " +
                                  "'Describe the methodology discussed in the document', 'List the key conclusions'). " +
                                  "The Builder will read [[PROJECT KNOWLEDGE BASE]] directly and write the content. " +
                                  "Ignore Rule 3 about functions/components — this is NOT a coding task."
                                : ""))
                        + webSearchSystemNote
                        + sharedVocabularySystemNote;
                    architectSystem = ComposeCouncilSystemPrompt(architectSystem, CouncilRole.Architect, runContext, GetSystemPromptDocumentBudgetChars(CouncilRole.Architect));
                    if (IsQwen3Model(architectConfig.ModelPath ?? string.Empty))
                        architectSystem = BuildQwen3SystemPrompt(architectSystem, false);

                    string architectQuery = string.IsNullOrWhiteSpace(objective)
                        ? userQuery
                        : userQuery + "\n" + objective;
                    string architectPriorKnowledge = BuildPriorKnowledgeBlock(_sessionHippocampus.Query(architectQuery, 3));
                    // Cloud models have the context headroom for a wider, fuller conversation window,
                    // which markedly improves follow-up planning ("now change X" style turns).
                    string recentConversation = CanUseCloudCouncil
                        ? BuildRecentConversationContext(10, 1600)
                        : BuildRecentConversationContext(4);
                    string architectPayload = BuildPipelineStateHeader("", "")
                        + recentConversation
                        + architectPriorKnowledge
                        + ((runContext.IsArtifactCanvasRequest || runContext.IsWorkspaceTask)
                            ? BuildArchitectHandoffPrimedPayload(runContext, BuildRoleIsolatedPayload(CouncilRole.Architect, contextState))
                            : BuildRolePrimedPayload(CouncilRole.Architect, taskType, BuildRoleIsolatedPayload(CouncilRole.Architect, contextState)))
                        + ((runContext.IsArtifactCanvasRequest || runContext.IsWorkspaceTask)
                            ? BuildArchitectHandoffClosingAnchor()
                            : BuildCouncilClosingAnchor(CouncilRole.Architect));

                    var architectResult = await ExecuteCouncilRoleAsync(
                        CouncilRole.Architect, architectSystem, architectPayload, token, null, baseStateVault, true);
                    architectOutput = architectResult.Answer;

                    string previousArchitectOutput = GetPreviousRoleOutput("architect");
                    string architectCleaned = "";
                    bool architectNormalized = TryNormalizeArchitectPlan(architectOutput, out architectCleaned, out bool architectMarkerFound);

                    // If normalization failed entirely for a document-grounded task,
                    // use deterministic fallback immediately instead of retrying with the model
                    if (!architectNormalized && isDocumentGrounded)
                    {
                        architectCleaned = BuildDocumentTaskPlan(runContext.UserPrompt, finalChunks);
                        architectNormalized = true;
                        architectMarkerFound = true;
                        LogActivity("Architect produced unstructured output for document task; using deterministic content plan.");
                        AppendChat("system", "Architect plan auto-corrected: unstructured output replaced with content plan.");
                    }

                    if (!architectNormalized && (runContext.IsArtifactCanvasRequest || runContext.IsWorkspaceTask))
                    {
                        architectCleaned = BuildArtifactArchitectHandoff(runContext);
                        architectNormalized = true;
                        architectMarkerFound = true;
                        LogActivity(runContext.IsWorkspaceTask
                            ? "Architect produced unstructured output for connected codebase; using deterministic patch handoff."
                            : "Architect produced unstructured output for Project Canvas; using deterministic artifact handoff.");
                    }

                    // Deterministic sanitization: strip file-operation steps for non-coding tasks
                    if (architectNormalized && taskType != CouncilTaskType.Coding && !runContext.IsArtifactCanvasRequest)
                    {
                        string sanitized = SanitizeArchitectPlan(architectCleaned, taskType);
                        if (string.IsNullOrWhiteSpace(sanitized))
                        {
                            architectCleaned = BuildFallbackDocumentPlan(finalChunks);
                            LogActivity("Architect produced only file-operation steps; replaced with deterministic document plan.");
                            AppendChat("system", "Architect plan auto-corrected: procedural steps replaced with content-focused plan.");
                            architectMarkerFound = true;
                        }
                        else if (sanitized != architectCleaned)
                        {
                            architectCleaned = sanitized;
                            LogActivity($"Architect plan sanitized: file-operation steps removed, {CountArchitectSteps(sanitized)} steps remain.");
                        }
                    }

                    if (architectNormalized
                        && (runContext.IsArtifactCanvasRequest || runContext.IsWorkspaceTask)
                        && !IsArchitectArtifactHandoff(architectCleaned))
                    {
                        architectCleaned = BuildArtifactArchitectHandoff(runContext);
                        architectMarkerFound = true;
                        LogActivity(runContext.IsWorkspaceTask
                            ? "Architect output normalized into connected-codebase patch handoff contract."
                            : "Architect output normalized into Project Canvas artifact handoff contract.");
                    }

                    bool architectSchemaOk = architectNormalized
                        && ValidateArchitectSchema(architectCleaned)
                        && !ArchitectHasRoleDrift(architectCleaned);

                    if (architectSchemaOk)
                    {
                        architectOutput = architectCleaned;
                        if (!architectMarkerFound)
                        {
                            AppendChat("warning", "Architect output missing termination marker; accepted via normalization.");
                            LogActivity("Architect output accepted without marker using fallback normalization.");
                        }
                    }

                    var architectMeta = new StageMetadata { StageName = "Architect" };

                    if (!architectSchemaOk)
                    {
                        architectMeta.RequiredReformatRetry = true;
                        architectMeta.SchemaValidationPasses = 2;
                        runContext.ArchitectDriftCorrected = true;
                        ArchitectStageText.Text = "Architect · Retried";
                        LogActivity("Architect output violated contract or role boundary. Re-running once with correction...");
                        string loopBreak = IsRepetitionLoop(architectOutput, previousArchitectOutput)
                            ? "Previous output repeated a prior attempt. You must produce different output and follow the contract exactly."
                            : "";

                        var retryResult = await ExecuteCouncilRoleAsync(
                            CouncilRole.Architect, architectSystem,
                            BuildPipelineStateHeader("", "") +
                            "ROLE CORRECTION: You produced invalid Architect output. Architect must not write code. " +
                            "In this workplace, attached files are already ingested into PROJECT KNOWLEDGE BASE. " +
                            "Do NOT include file-opening/OCR/external-tool steps. " +
                            (runContext.IsWorkspaceTask
                                ? "Output ONLY the connected-codebase ARCHITECT_HANDOFF shape and terminate with ARCHITECT PLAN COMPLETE. "
                                : runContext.IsArtifactCanvasRequest
                                    ? "Output ONLY the Project Canvas ARCHITECT_HANDOFF shape and terminate with ARCHITECT PLAN COMPLETE. "
                                    : "Output ONLY a numbered implementation plan and terminate with ARCHITECT PLAN COMPLETE. ") +
                            loopBreak + "\n\n" + BuildLabeledBlock("PREVIOUS ARCHITECT OUTPUT", architectOutput),
                            token, null, baseStateVault, true);
                        architectOutput = retryResult.Answer;

                        bool retryNormalized = TryNormalizeArchitectPlan(architectOutput, out architectCleaned, out bool retryMarkerFound);

                        if (retryNormalized && taskType != CouncilTaskType.Coding && !runContext.IsArtifactCanvasRequest)
                        {
                            string sanitized = SanitizeArchitectPlan(architectCleaned, taskType);
                            if (string.IsNullOrWhiteSpace(sanitized))
                            {
                                architectCleaned = BuildFallbackDocumentPlan(finalChunks);
                                LogActivity("Architect retry also produced only file-operation steps; using deterministic fallback plan.");
                                retryNormalized = true;
                                retryMarkerFound = true;
                            }
                            else if (sanitized != architectCleaned)
                            {
                                architectCleaned = sanitized;
                                LogActivity("Architect retry plan sanitized.");
                            }
                        }

                        if (retryNormalized
                            && (runContext.IsArtifactCanvasRequest || runContext.IsWorkspaceTask)
                            && !IsArchitectArtifactHandoff(architectCleaned))
                        {
                            architectCleaned = BuildArtifactArchitectHandoff(runContext);
                            retryMarkerFound = true;
                            LogActivity(runContext.IsWorkspaceTask
                                ? "Architect retry output normalized into connected-codebase patch handoff contract."
                                : "Architect retry output normalized into Project Canvas artifact handoff contract.");
                        }

                        bool retryOk = retryNormalized
                            && ValidateArchitectSchema(architectCleaned)
                            && !ArchitectHasRoleDrift(architectCleaned);

                        if (!retryOk)
                        {
                            // Two invalid Architect outputs used to hard-abort the whole relay.
                            // Fall back to a deterministic plan from the pre-flight decomposition
                            // instead, so the Builder and Critic still run against the user's
                            // actual requirements.
                            string fallbackPlan = BuildFallbackPlanFromDecomposition(runContext);
                            if (string.IsNullOrWhiteSpace(fallbackPlan))
                            {
                                throw new InvalidOperationException("Architect stage failed contract/role validation after correction attempt.");
                            }

                            architectCleaned = fallbackPlan;
                            retryMarkerFound = true;
                            LogActivity("Architect retry failed contract validation; substituting deterministic plan from pre-flight decomposition so the relay can continue.");
                            AppendChat("warning", "Architect output failed validation after retry. Using a deterministic plan derived from your request so the relay can continue.");
                        }

                        architectOutput = architectCleaned;
                        if (!retryMarkerFound)
                        {
                            AppendChat("warning", "Architect correction output missing termination marker; accepted via normalization.");
                            LogActivity("Architect correction accepted without marker using fallback normalization.");
                        }
                    }

                    runContext.PipelineMetadata.Add(architectMeta);
                    runContext.ArchitectOutput = architectOutput;
                    contextState.ArchitectOutput = architectOutput;
                    runContext.ArchitectThinking = architectResult.ThinkingContent;
                    runContext.ArchitectStepCount = CountArchitectSteps(architectOutput);

                    LogActivity($"Architect responded ({architectOutput.Length} chars, {runContext.ArchitectStepCount} steps). Displaying output.");
                    AppendChat("architect", architectOutput);
                    _chatHistory.Add(("architect", architectOutput));
                    UpdateWorkplaceTokenUsageIndicator();
                    UpdateStageIndicator(null, true, false, false);
                }

                if (!string.IsNullOrWhiteSpace(runContext.ArchitectOutput))
                {
                    WriteArchitectSessionMemory(runContext.ArchitectOutput, activeRunIndex);
                }

                // ═══════════════════════════════════════════════════════════════
                // STAGE 2 — Builder: receives original prompt + Architect output
                //           Uses segmented execution for plans > 4 steps
                // ═══════════════════════════════════════════════════════════════
                string builderOutput = "";
                string builderPriorKnowledge = "";
                int builderRetryAttempts = 0;
                bool codebasePatchCaptured = false;
                // Tracks whether the current builderOutput came purely from the cloud reasoning fallback
                // (chain-of-thought with no final content). Cleared whenever real content replaces it.
                bool builderReasoningFallback = false;
                if (HasEffectiveLocalRoleModel(CouncilRole.Builder) || CanUseCloudCouncil)
                {
                    UpdateStageIndicator(CouncilRole.Builder, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), false, false);
                    RelayStatusBlock.Text = "Relay: Builder is executing...";
                    LogActivity("Builder relay started — generating implementation...");

                    var builderConfig = GetEffectiveRoleConfig(CouncilRole.Builder);
                    if (string.IsNullOrWhiteSpace(_council[CouncilRole.Builder].ModelPath) && !CanUseCloudCouncil)
                        AppendChat("system", $"Builder has no dedicated model — running on '{builderConfig.DisplayName}'.");
                    bool smallLocalBuilderModel = IsSmallLocalCouncilModel(builderConfig.ModelPath ?? builderConfig.DisplayName, _isCloudModeEnabled);

                    string builderSystem = GetEmbeddedSystemPrompt(CouncilRole.Builder)
                        + objectiveClause
                        + (runContext.IsArtifactCanvasRequest
                            ? BuildArtifactTaskTypeBoost(CouncilRole.Builder, runContext)
                            : GetTaskTypeBoost(taskType, CouncilRole.Builder))
                        + (runContext.IsArtifactCanvasRequest ? BuildArtifactCanvasBoost(CouncilRole.Builder, runContext.PreferredArtifactFormatHint, runContext) : "")
                        + (runContext.IsArtifactCanvasRequest && smallLocalBuilderModel ? BuildSmallModelArtifactAssist(CouncilRole.Builder, runContext.PreferredArtifactFormatHint, runContext) : "")
                        + (!_isCloudModeEnabled ? BuildLocalBuilderCognitionBoost(taskType, runContext) : "")
                        + GetTokenBudgetHint(runContext)
                        + (isCalcTask ? GetCalculationBoost(CouncilRole.Builder, taskType) : "")
                        + ((runContext.PythonSandboxEligible && (taskType == CouncilTaskType.Coding || isCalcTask))
                            ? "\n" + runContext.PythonSystemPromptInjection
                            : "")
                        + (runContext.CalculatorUsed ? "\n[CALCULATOR TOOL] Treat [[CALCULATOR TOOL RESULTS]] as authoritative computed values. Reuse them consistently in output." : "")
                        + BuildBuilderContract(taskType, runContext.IsArtifactCanvasRequest)
                        + webSearchSystemNote
                        + builderWebPauseSystemNote
                        + sharedVocabularySystemNote;
                    builderSystem = ComposeCouncilSystemPrompt(builderSystem, CouncilRole.Builder, runContext, GetSystemPromptDocumentBudgetChars(CouncilRole.Builder));
                    if (IsQwen3Model(builderConfig.ModelPath ?? string.Empty))
                        builderSystem = BuildQwen3SystemPrompt(builderSystem, false);

                    _builderPythonSandboxPreamble = runContext.PythonSandboxPreamble;

                    string architectStateSummary = BuildArchitectSummaryFromPlan(runContext.ArchitectOutput);
                    string pipelineStateHeader = BuildPipelineStateHeader(architectStateSummary, "");
                    builderPriorKnowledge = BuildPriorKnowledgeBlock(_sessionHippocampus.Query(runContext.ArchitectOutput, 4));
                    string previousBuilderOutput = GetPreviousRoleOutput("builder");

                    bool useSegmented = taskType == CouncilTaskType.Coding && !runContext.IsCloudExecution
                        && !runContext.IsArtifactCanvasRequest   // a canvas artifact is ONE self-contained file — segmenting it emits partial code chunks that never render
                        && !runContext.IsProjectCanvasIteration // an edit must return one coherent replacement of the existing canvas source
                        && (runContext.Complexity == TaskComplexity.Complex || (runContext.Complexity != TaskComplexity.Simple && runContext.ArchitectStepCount > 4))
                        && !string.IsNullOrWhiteSpace(runContext.ArchitectOutput);

                    int builderContextBudget = runContext.IsCloudExecution
                        ? GetCloudCouncilInputBudgetTokens()
                        : (int)GetRoleContextSize(CouncilRole.Builder);
                    int documentTokens = runContext.IsDocumentTask ? EstimateTokenCount(runContext.DocumentContent) : 0;
                    // Document doesn't fit one full-context pass → retrieve the relevant sections and
                    // answer in ONE pass (RAG), rather than truncating to the head in the normal path.
                    bool useDocumentRetrieval = runContext.IsDocumentTask
                        && !string.IsNullOrWhiteSpace(runContext.DocumentContent)
                        && documentTokens > (int)((builderContextBudget - 900) * 0.6);

                    if (useSegmented)
                    {
                        LogActivity($"Segmented build: {runContext.ArchitectStepCount} steps detected, splitting into segments.");
                        builderOutput = await ExecuteSegmentedBuilderAsync(
                            runContext, builderSystem, knowledgePacket, finalChunks.Count > 0, builderPriorKnowledge, token, baseStateVault);
                    }
                    else if (useDocumentRetrieval)
                    {
                        LogActivity($"Document retrieval build: document ~{documentTokens} tokens exceeds one-pass budget — selecting relevant sections for a single pass.");
                        builderOutput = await ExecuteDocumentRetrievalBuilderAsync(runContext, builderSystem, builderPriorKnowledge, token, baseStateVault);
                    }
                    else
                    {
                        string fewShotPrefix = taskType == CouncilTaskType.Coding
                            ? (isCalcTask ? GetCalculationFewShotExample(userQuery) : GetBuilderFewShotExample())
                            : "";

                        var builderPayload = new StringBuilder();
                        contextState.ArchitectOutput = runContext.ArchitectOutput;

                        // Anchor block: placed at the very top of the payload so the model's
                        // first-read context locks onto the expected output format before it
                        // processes the architect plan or user prompt.
                        if (runContext.IsWorkspaceTask)
                            builderPayload.AppendLine(BuildLabeledBlock("CODEBASE PATCH OUTPUT CONTRACT", BuildCodebasePatchOutputContract()));
                        else if (runContext.IsArtifactCanvasRequest)
                            builderPayload.AppendLine(BuildCanvasArtifactAnchorBlock(runContext));
                        if (smallLocalBuilderModel && (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest))
                            builderPayload.AppendLine(BuildBuilderImplementationCapsule(runContext));

                        // Keep the small prior-request summary near the head. The full current source
                        // is appended near the closing anchor so local tail-preserving context trimming
                        // does not discard it while retaining lower-priority pipeline framing.
                        if (!string.IsNullOrWhiteSpace(runContext.CurrentArtifactForIteration))
                        {
                            if (!string.IsNullOrWhiteSpace(runContext.PreviousArtifactRequest)
                                || !string.IsNullOrWhiteSpace(runContext.PreviousArtifactFormatHint))
                            {
                                var priorArtifactContext = new StringBuilder();
                                if (!string.IsNullOrWhiteSpace(runContext.PreviousArtifactRequest))
                                    priorArtifactContext.AppendLine("Original artifact request: " + runContext.PreviousArtifactRequest.Trim());
                                if (!string.IsNullOrWhiteSpace(runContext.PreviousArtifactFormatHint))
                                    priorArtifactContext.AppendLine("Prior artifact format guidance: " + runContext.PreviousArtifactFormatHint.Trim());
                                builderPayload.AppendLine(BuildLabeledBlock("PRIOR ARTIFACT CONTEXT", priorArtifactContext.ToString().Trim()));
                            }
                        }

                        builderPayload.AppendLine(pipelineStateHeader);
                        builderPayload.AppendLine(sharedVocabularySection);
                        if (!string.IsNullOrWhiteSpace(builderPriorKnowledge))
                            builderPayload.AppendLine(builderPriorKnowledge);
                        if (fewShotPrefix.Length > 0)
                            builderPayload.AppendLine(fewShotPrefix);

                        AppendCouncilWebContext(builderPayload, runContext);
                        builderPayload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, taskType, BuildRoleIsolatedPayload(CouncilRole.Builder, contextState)));

                        if (runContext.IsDocumentTask && !string.IsNullOrWhiteSpace(runContext.DocumentContent))
                        {
                            int maxDocChars = runContext.IsCloudExecution
                                ? GetCloudDocumentCharacterBudget(runContext, 1600)
                                : Math.Max(1600, (((int)GetRoleContextSize(CouncilRole.Builder) - 900) * 4));
                            builderPayload.AppendLine(BuildDocumentContentBlock(runContext.DocumentContent, maxDocChars));
                            builderPayload.AppendLine(DocumentGroundingInstruction);
                        }
                        else if (finalChunks.Count > 0)
                        {
                            builderPayload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
                        }

                        if (!string.IsNullOrWhiteSpace(runContext.CurrentArtifactForIteration))
                        {
                            builderPayload.AppendLine(BuildLabeledBlock("CURRENT PROJECT CANVAS SOURCE — MODIFY THIS", runContext.CurrentArtifactForIteration));
                            builderPayload.AppendLine(BuildLabeledBlock("REQUESTED CANVAS CHANGE", runContext.UserPrompt));
                            builderPayload.AppendLine(runContext.CurrentArtifactForIterationWasTruncated
                                ? "The source block is truncated because the complete canvas exceeds this local model's safe context. Do not return a partial replacement or claim success. Preserve the artifact type and make the requested change only if you can return a complete coherent replacement."
                                : "Modify the current source above and output the COMPLETE updated source (not a diff or fragment). Preserve its language, artifact type, behavior, and every part the user did not ask to change. The final source must materially differ from the current source in the requested way.");
                        }

                        if (runContext.IsWorkspaceTask)
                            builderPayload.AppendLine(BuildLabeledBlock("CODEBASE PATCH OUTPUT CONTRACT", BuildCodebasePatchOutputContract()));

                        builderPayload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: _isWebSearchEnabled && !_isCloudModeEnabled));

                        var builderResult = await ExecuteCouncilRoleAsync(
                            CouncilRole.Builder,
                            builderSystem,
                            builderPayload.ToString(),
                            token,
                            runContext.IsDocumentTask ? 0.25f : null,
                            baseStateVault,
                            true);
                        builderOutput = builderResult.Answer;
                        builderReasoningFallback = builderResult.IsReasoningFallback;
                    }

                    string builderContractCleaned = "";
                    // Artifact canvas requests produce HTML/SVG code — exempt them from the role-drift
                    // and IsLikelyCodeOutput checks that exist to catch code leaking into prose tasks.
                    bool builderMarkerFound;
                    bool builderNormalized;
                    bool builderOutputDetectedAsCode;
                    bool builderContractOk;
                    if (runContext.IsWorkspaceTask
                        && _workspaceAccessService.TryParsePatchProposal(builderOutput, out _, out _))
                    {
                        builderContractCleaned = builderOutput.Trim();
                        builderMarkerFound = true;
                        builderNormalized = true;
                        builderOutputDetectedAsCode = false;
                        builderContractOk = true;
                    }
                    else if (runContext.IsWorkspaceTask
                        && TryRecoverCodebasePatchProposal(builderOutput, runContext, out WorkspacePatchProposal? recoveredBeforeRetry, out string preRetryRecoveryReason)
                        && recoveredBeforeRetry != null)
                    {
                        builderContractCleaned = recoveredBeforeRetry.RawText.Trim();
                        builderMarkerFound = true;
                        builderNormalized = true;
                        builderOutputDetectedAsCode = false;
                        builderContractOk = true;
                        LogActivity("Workspace Builder output recovered before correction retry: " + preRetryRecoveryReason);
                    }
                    else
                    {
                        builderNormalized = TryNormalizeBuilderOutput(builderOutput, taskType, out builderContractCleaned, out builderMarkerFound);
                        builderOutputDetectedAsCode = builderNormalized && DetectCodeOutput(builderContractCleaned).IsCode;
                        builderContractOk = builderNormalized
                            && (runContext.IsArtifactCanvasRequest || builderOutputDetectedAsCode || !BuilderHasRoleDrift(builderContractCleaned, taskType))
                            && (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest || builderOutputDetectedAsCode || !IsLikelyCodeOutput(builderContractCleaned))
                            && !IsDegenerateBuilderOutput(builderContractCleaned, taskType)
                            && !IsRepetitionLoop(builderContractCleaned, previousBuilderOutput);
                    }

                    if (!builderContractOk)
                    {
                        runContext.BuilderDriftCorrected = true;
                        BuilderStageText.Text = "Builder · Retried";

                        string loopBreak = IsRepetitionLoop(builderOutput, previousBuilderOutput)
                            ? "LOOP BREAK: your previous response repeated a prior output. Produce a materially different implementation."
                            : "";

                        var correctionPayload = new StringBuilder();
                        correctionPayload.AppendLine(pipelineStateHeader);
                        correctionPayload.AppendLine(sharedVocabularySection);
                        if (!string.IsNullOrWhiteSpace(builderPriorKnowledge))
                            correctionPayload.AppendLine(builderPriorKnowledge);
                        AppendCouncilWebContext(correctionPayload, runContext);
                        if (smallLocalBuilderModel && (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest))
                            correctionPayload.AppendLine(BuildBuilderImplementationCapsule(runContext));
                        if (runContext.IsWorkspaceTask)
                        {
                            correctionPayload.AppendLine("ROLE CORRECTION: Builder must output a connected-workspace patch proposal only. No standalone code fence, prose, numbered list, or raw file content.");
                            correctionPayload.AppendLine(BuildLabeledBlock("CODEBASE PATCH OUTPUT CONTRACT", BuildCodebasePatchOutputContract()));
                            if (!string.IsNullOrWhiteSpace(runContext.WorkspaceContext))
                                correctionPayload.AppendLine(BuildLabeledBlock("CONNECTED CODEBASE CONTEXT", runContext.WorkspaceContext));
                        }
                        else if (runContext.IsArtifactCanvasRequest)
                        {
                            correctionPayload.AppendLine("ROLE CORRECTION: Builder must output the renderable artifact as exactly ONE ```html, ```svg, or ```python code fence with NOTHING outside the fence. " +
                                "Do NOT output a numbered plan, a step list, prose, or any restatement of the request or the Architect's plan.");
                        }
                        else if (taskType == CouncilTaskType.Coding)
                        {
                            correctionPayload.AppendLine("ROLE CORRECTION: Builder must output executable code only. No prose.");
                        }
                        else
                        {
                            correctionPayload.AppendLine("ROLE CORRECTION: Builder must output well-organized prose covering the Architect's plan. " +
                                "Use [[PROJECT KNOWLEDGE BASE]] as source material. No code blocks, no scripts, no programming constructs.");
                            if (runContext.IsDocumentTask && !string.IsNullOrWhiteSpace(runContext.DocumentContent))
                            {
                                correctionPayload.AppendLine("The document text is in [[DOCUMENT CONTENT]]; do not claim it is unavailable.");
                                correctionPayload.AppendLine(BuildDocumentContentBlock(runContext.DocumentContent, 6000));
                            }
                            else if (finalChunks.Count > 0)
                            {
                                correctionPayload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
                            }
                        }
                        correctionPayload.AppendLine($"Terminate with {BuilderCompletionMarker} on its own line.");
                        correctionPayload.AppendLine(loopBreak);
                        correctionPayload.AppendLine(BuildLabeledBlock("PREVIOUS BUILDER OUTPUT", builderOutput));

                        var builderRetry = await ExecuteCouncilRoleAsync(
                            CouncilRole.Builder,
                            builderSystem,
                            correctionPayload.ToString(),
                            token,
                            runContext.IsDocumentTask ? 0.2f : null,
                            baseStateVault,
                            true);
                        builderOutput = builderRetry.Answer;
                        builderReasoningFallback = builderRetry.IsReasoningFallback;

                        bool retryNormalized;
                        if (runContext.IsWorkspaceTask
                            && _workspaceAccessService.TryParsePatchProposal(builderOutput, out _, out _))
                        {
                            builderContractCleaned = builderOutput.Trim();
                            builderMarkerFound = true;
                            retryNormalized = true;
                            builderOutputDetectedAsCode = false;
                        }
                        else if (runContext.IsWorkspaceTask
                            && TryRecoverCodebasePatchProposal(builderOutput, runContext, out WorkspacePatchProposal? recoveredRetryProposal, out string retryRecoveryReason)
                            && recoveredRetryProposal != null)
                        {
                            builderContractCleaned = recoveredRetryProposal.RawText.Trim();
                            builderMarkerFound = true;
                            retryNormalized = true;
                            builderOutputDetectedAsCode = false;
                            LogActivity("Workspace Builder retry output recovered: " + retryRecoveryReason);
                        }
                        else
                        {
                            retryNormalized = TryNormalizeBuilderOutput(builderOutput, taskType, out builderContractCleaned, out builderMarkerFound);
                            builderOutputDetectedAsCode = retryNormalized && DetectCodeOutput(builderContractCleaned).IsCode;
                        }

                        if (!retryNormalized
                            || (!runContext.IsArtifactCanvasRequest && !builderOutputDetectedAsCode && BuilderHasRoleDrift(builderContractCleaned, taskType))
                            || (taskType != CouncilTaskType.Coding && !runContext.IsArtifactCanvasRequest && !builderOutputDetectedAsCode && IsLikelyCodeOutput(builderContractCleaned))
                            || IsDegenerateBuilderOutput(builderContractCleaned, taskType))
                        {
                            if (runContext.IsWorkspaceTask)
                            {
                                builderContractCleaned = "[[CODEBASE PATCH FORMAT ERROR]]\nBuilder did not return a valid codebase patch proposal. No files were changed.";
                                builderMarkerFound = true;
                                LogActivity("Workspace Builder correction failed to produce a valid patch envelope; raw output suppressed.");
                                AppendChat("warning", "Builder did not return a valid codebase patch format. No files were changed; try a smaller, specific file change.");
                            }
                            else if (isCalcTask && TryNormalizeBuilderOutput(builderOutput, taskType, out builderContractCleaned, out builderMarkerFound))
                            {
                                LogActivity("Builder correction fallback: accepted normalized calculation output despite strict validation miss.");
                                AppendChat("warning", "Builder correction fallback applied for calculation response.");
                            }
                            else
                            {
                                LogActivity("Builder correction failed strict validation; applying resilient fallback instead of terminating pipeline.");
                                AppendChat("warning", "Builder output required automatic recovery. Applying resilient fallback output.");

                                // Artifact requests are code-fenced deliverables — extract the code, never
                                // strip it as if it were prose, so the artifact-recovery stage below can
                                // still find a renderable artifact in the corrected output.
                                string rawFallback = (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest)
                                    ? StripChatFromCode(builderOutput)
                                    : StripMarkdownFences(builderOutput);

                                if (runContext.IsDocumentTask)
                                {
                                    string? regenerated = await TryRegenerateWeakDocumentOutputAsync(
                                        runContext,
                                        builderSystem,
                                        pipelineStateHeader,
                                        sharedVocabularySection,
                                        builderPriorKnowledge,
                                        builderOutput,
                                        token,
                                        baseStateVault);
                                    builderContractCleaned = regenerated
                                        ?? BuildDeterministicDocumentResponse(runContext.UserPrompt, runContext.DocumentContent, runContext.DocumentFileNames);
                                }
                                else if (taskType != CouncilTaskType.Coding && !runContext.IsArtifactCanvasRequest)
                                {
                                    string candidate = string.IsNullOrWhiteSpace(rawFallback) ? builderOutput : rawFallback;
                                    if (IsDegenerateBuilderOutput(candidate, taskType))
                                    {
                                        var synthesis = new StringBuilder();
                                        synthesis.AppendLine("Recovered response based on Architect plan:");
                                        foreach (var line in (runContext.ArchitectOutput ?? "").Split('\n'))
                                        {
                                            string trimmed = line.Trim();
                                            if (trimmed.Length > 1 && char.IsDigit(trimmed[0]))
                                            {
                                                int sep = trimmed.IndexOfAny(['.', ')']);
                                                string content = sep > 0 && sep < trimmed.Length - 1
                                                    ? trimmed[(sep + 1)..].Trim()
                                                    : trimmed;
                                                synthesis.AppendLine($"- {content}");
                                            }
                                        }
                                        builderContractCleaned = synthesis.ToString().Trim();
                                    }
                                    else
                                    {
                                        builderContractCleaned = candidate.Trim();
                                    }
                                }
                                else
                                {
                                    builderContractCleaned = string.IsNullOrWhiteSpace(rawFallback)
                                        ? "# Recovery fallback\n# Builder output could not be validated; please rerun with stricter prompt."
                                        : rawFallback.Trim();
                                }

                                builderMarkerFound = true;
                            }
                        }
                    }

                    if (!builderMarkerFound)
                    {
                        LogActivity("Builder output accepted without completion marker using fallback normalization.");
                    }

                    builderOutput = builderContractCleaned;
                    builderOutput = PostProcessBuilderOutput(builderOutput, runContext);
                    if (runContext.IsWorkspaceTask)
                    {
                        codebasePatchCaptured = TryCaptureCodebasePatchProposal(builderOutput, runContext);
                        if (!codebasePatchCaptured)
                        {
                            LogActivity("Workspace Builder output was not a patch envelope; running one patch-format retry.");
                            AppendChat("system", "Builder returned raw code instead of a reviewable codebase patch. Asking for a patch-format retry...");

                            var patchFormatRetryPayload = new StringBuilder();
                            patchFormatRetryPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
                            patchFormatRetryPayload.AppendLine(BuildLabeledBlock("APPROVED ARCHITECTURE", runContext.ArchitectOutput));
                            if (!string.IsNullOrWhiteSpace(runContext.WorkspaceContext))
                                patchFormatRetryPayload.AppendLine(BuildLabeledBlock("CONNECTED CODEBASE CONTEXT", runContext.WorkspaceContext));
                            patchFormatRetryPayload.AppendLine(BuildLabeledBlock("REJECTED BUILDER OUTPUT", builderOutput));
                            patchFormatRetryPayload.AppendLine(BuildLabeledBlock("REQUIRED OUTPUT FORMAT", BuildCodebasePatchOutputContract()));
                            patchFormatRetryPayload.AppendLine("Return only the [[AXIOM_CODEBASE_PATCH]] envelope. Do not include standalone file content, explanations, or markdown outside the envelope.");
                            patchFormatRetryPayload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: false));

                            var patchFormatRetry = await ExecuteCouncilRoleAsync(
                                CouncilRole.Builder,
                                builderSystem,
                                patchFormatRetryPayload.ToString(),
                                token,
                                0.15f,
                                baseStateVault,
                                true);

                            builderOutput = PostProcessBuilderOutput(patchFormatRetry.Answer, runContext);
                            builderReasoningFallback = patchFormatRetry.IsReasoningFallback;
                            codebasePatchCaptured = TryCaptureCodebasePatchProposal(builderOutput, runContext);
                            if (!codebasePatchCaptured)
                            {
                                builderOutput = "[[CODEBASE PATCH FORMAT ERROR]]\nBuilder did not return a valid codebase patch proposal. No files were changed.";
                                AppendChat("warning", "Builder still did not return a valid codebase patch format. I suppressed the raw output so it cannot be mistaken for an applied change.");
                                LogActivity("Workspace patch-format retry failed; raw output suppressed.");
                                RenderCodebasePatchFailureReview(
                                    patchFormatRetry.Answer,
                                    "The Builder response did not contain a valid `[[AXIOM_CODEBASE_PATCH]]` envelope after a retry.");
                                codebasePatchCaptured = true;
                            }
                        }
                    }

                    if (runContext.WebGroundingRequired
                        && !HasCouncilWebEvidenceForRun(runContext)
                        && taskType != CouncilTaskType.Coding
                        && !runContext.IsArtifactCanvasRequest
                        && !DetectCodeOutput(builderOutput).IsCode
                        && !BuilderStatesWebEvidenceUnavailable(builderOutput))
                    {
                        LogActivity("Builder attempted a current/source-backed answer without usable web evidence; replacing with evidence-gap response.");
                        AppendChat("warning", "Web evidence was required but unavailable, so unsupported current factual claims were suppressed.");
                        builderOutput = BuildWebEvidenceUnavailableBuilderFallback(runContext);
                        builderReasoningFallback = false;
                    }

                    // Run multi-stage artifact recovery for all artifact canvas requests, not just
                    // small local models. Cloud and large local models can also drift to prose.
                    // Stage 2 (prose extraction) rescues buried HTML/SVG from explanatory wrappers.
                    // Stage 3 (deterministic XY plotter) only fires on specific keyword patterns.
                    if (runContext.IsArtifactCanvasRequest
                        && !ArtifactRenderService.DetectForCanvas(builderOutput, null).SupportsPreview)
                    {
                        string recoveredArtifact = TryBuildDeterministicArtifactRecovery(runContext, builderOutput);
                        if (!string.IsNullOrWhiteSpace(recoveredArtifact)
                            && ArtifactRenderService.DetectForCanvas(recoveredArtifact, null).SupportsPreview)
                        {
                            string source = smallLocalBuilderModel ? "small-model fallback" : "prose-extraction";
                            LogActivity($"Artifact recovery ({source}) applied after non-renderable Builder output.");
                            AppendChat("system", $"Artifact recovery applied for Project Canvas ({source}).");
                            builderOutput = recoveredArtifact;
                            builderReasoningFallback = false; // recovered into a real renderable artifact
                        }
                    }

                    if (IsDynamicArtifactStaticSvgMismatch(runContext, builderOutput))
                    {
                        LogActivity("Dynamic artifact validation rejected standalone SVG; running one focused HTML/JS correction pass.");
                        AppendChat("system", "Builder returned a static SVG for a dynamic artifact request. Running one focused HTML/JS correction before rendering...");

                        var formatRetryPayload = new StringBuilder();
                        formatRetryPayload.AppendLine(BuildCanvasArtifactAnchorBlock(runContext));
                        formatRetryPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
                        formatRetryPayload.AppendLine(BuildLabeledBlock("REJECTED BUILDER OUTPUT", builderOutput));
                        formatRetryPayload.AppendLine("The rejected output is a standalone SVG, but this request needs dynamic behavior, calculations, controls, or changing readouts. Return a complete self-contained HTML document in one ```html code fence with inline CSS and inline JavaScript. Do not return standalone SVG. Do not include prose.");
                        formatRetryPayload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: false));

                        var formatRetry = await ExecuteCouncilRoleAsync(
                            CouncilRole.Builder,
                            builderSystem,
                            formatRetryPayload.ToString(),
                            token,
                            runContext.IsDocumentTask ? 0.2f : null,
                            baseStateVault,
                            true);

                        string retryOutput = formatRetry.Answer;
                        if (TryNormalizeBuilderOutput(retryOutput, taskType, out string normalizedRetry, out _))
                            retryOutput = normalizedRetry;
                        retryOutput = PostProcessBuilderOutput(retryOutput, runContext);

                        if (!IsDynamicArtifactStaticSvgMismatch(runContext, retryOutput)
                            && ArtifactRenderService.DetectForCanvas(retryOutput, null).SupportsPreview)
                        {
                            builderOutput = retryOutput;
                            builderReasoningFallback = formatRetry.IsReasoningFallback;
                            LogActivity("Dynamic artifact correction produced a renderable non-SVG artifact.");
                        }
                        else
                        {
                            runContext.StaticValidationFindings.Add("Dynamic artifact request was returned as standalone SVG; Critic should require a self-contained HTML/JS implementation with working controls/readouts.");
                            LogActivity("Dynamic artifact correction did not produce acceptable HTML/JS; pre-flagged for Critic.");
                        }
                    }

                    if (runContext.IsProjectCanvasIteration && CanvasHasRealContent(ProjectCanvasEditor?.Text))
                    {
                        string existingCanvasSource = ProjectCanvasEditor.Text;
                        string candidateCanvasSource = StripChatFromCode(builderOutput ?? string.Empty);
                        bool candidateUnchanged = CanvasSourcesEquivalent(existingCanvasSource, candidateCanvasSource);
                        bool candidateRenderable = !runContext.IsArtifactCanvasRequest
                            || ArtifactRenderService.DetectForCanvas(builderOutput, null).SupportsPreview;
                        bool candidateLooksPartial = runContext.CurrentArtifactForIterationWasTruncated
                            && candidateCanvasSource.Length < existingCanvasSource.Length * 0.85;

                        if ((candidateUnchanged || !candidateRenderable) && !runContext.CurrentArtifactForIterationWasTruncated)
                        {
                            LogActivity(candidateUnchanged
                                ? "Canvas mutation verification found an unchanged Builder result; running one corrective Builder pass."
                                : "Canvas mutation verification found a non-renderable replacement; running one corrective Builder pass.");
                            AppendChat("system", "The Builder did not produce a verifiable canvas change. Running one focused correction pass...");

                            var mutationRetryPayload = new StringBuilder();
                            if (runContext.IsArtifactCanvasRequest)
                                mutationRetryPayload.AppendLine(BuildCanvasArtifactAnchorBlock(runContext));
                            if (smallLocalBuilderModel && (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest))
                                mutationRetryPayload.AppendLine(BuildBuilderImplementationCapsule(runContext));
                            mutationRetryPayload.AppendLine(BuildLabeledBlock("CURRENT PROJECT CANVAS SOURCE — MODIFY THIS", existingCanvasSource));
                            mutationRetryPayload.AppendLine(BuildLabeledBlock("REQUESTED CANVAS CHANGE", runContext.UserPrompt));
                            mutationRetryPayload.AppendLine(BuildLabeledBlock("REJECTED BUILDER RESULT", builderOutput));
                            mutationRetryPayload.AppendLine(candidateUnchanged
                                ? "The rejected result is equivalent to the current canvas. Actually implement the requested change in the returned source."
                                : "The rejected result cannot be rendered as the current artifact type. Return a complete valid replacement in the same format as the current canvas.");
                            mutationRetryPayload.AppendLine("Return the complete updated source only. Preserve unaffected content. Do not describe the change, output a diff, or claim success without changing the source.");
                            mutationRetryPayload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: false));

                            var mutationRetry = await ExecuteCouncilRoleAsync(
                                CouncilRole.Builder,
                                builderSystem,
                                mutationRetryPayload.ToString(),
                                token,
                                runContext.IsDocumentTask ? 0.2f : null,
                                baseStateVault,
                                true);

                            string retryCandidate = mutationRetry.Answer;
                            if (TryNormalizeBuilderOutput(retryCandidate, taskType, out string normalizedRetryCandidate, out _))
                                retryCandidate = normalizedRetryCandidate;
                            retryCandidate = PostProcessBuilderOutput(retryCandidate, runContext);

                            if (runContext.IsArtifactCanvasRequest
                                && !ArtifactRenderService.DetectForCanvas(retryCandidate, null).SupportsPreview)
                            {
                                string recoveredRetry = TryBuildDeterministicArtifactRecovery(runContext, retryCandidate);
                                if (!string.IsNullOrWhiteSpace(recoveredRetry))
                                    retryCandidate = recoveredRetry;
                            }

                            bool retryChanged = !CanvasSourcesEquivalent(existingCanvasSource, retryCandidate);
                            bool retryRenderable = !runContext.IsArtifactCanvasRequest
                                || ArtifactRenderService.DetectForCanvas(retryCandidate, null).SupportsPreview;
                            if (retryChanged && retryRenderable)
                            {
                                builderOutput = retryCandidate;
                                builderReasoningFallback = mutationRetry.IsReasoningFallback;
                                candidateCanvasSource = StripChatFromCode(builderOutput);
                                candidateUnchanged = false;
                                candidateRenderable = true;
                                candidateLooksPartial = false;
                                LogActivity("Canvas mutation correction produced a changed, valid replacement.");
                            }
                        }

                        if (candidateUnchanged || !candidateRenderable || candidateLooksPartial)
                        {
                            runContext.CanvasMutationFailed = true;
                            builderOutput = existingCanvasSource;
                            builderReasoningFallback = false;
                            string reason = candidateLooksPartial
                                ? "the current canvas is too large for a safe complete replacement in this local model context"
                                : candidateUnchanged
                                    ? "the Builder returned source equivalent to the current canvas"
                                    : "the Builder replacement was not valid for the current canvas artifact type";
                            LogActivity($"Canvas mutation rejected: {reason}.");
                            AppendChat("warning", $"Canvas change was not applied because {reason}. The existing Project Canvas was preserved.");
                        }
                    }

                    if (runContext.IsDocumentTask && IsLowValueDocumentOutput(builderOutput, runContext.DocumentFileNames))
                    {
                        LogActivity("Builder document output was low-value/placeholder; attempting grounded regeneration.");
                        runContext.BuilderOutput = builderOutput;
                        string? regenerated = await TryRegenerateWeakDocumentOutputAsync(
                            runContext,
                            builderSystem,
                            pipelineStateHeader,
                            sharedVocabularySection,
                            builderPriorKnowledge,
                            builderOutput,
                            token,
                            baseStateVault);
                        if (!string.IsNullOrWhiteSpace(regenerated))
                        {
                            builderOutput = regenerated;
                            builderReasoningFallback = false;
                            LogActivity("Builder document output regenerated after weak first pass.");
                        }
                        else
                        {
                            AppendChat("warning", "The local model produced a weak document answer after retry, so a source-grounded fallback was used.");
                            builderOutput = BuildDeterministicDocumentQualityPass(runContext);
                        }
                    }
                    else if (runContext.IsDocumentTask
                        && !string.IsNullOrWhiteSpace(runContext.DocumentContent)
                        && IsUngroundedDocumentOutput(builderOutput, runContext.DocumentContent))
                    {
                        // The model falsely claimed the document was missing/unavailable even though its
                        // full text was in the payload — a refusal, not an answer. Substitute a grounded
                        // extract so the user gets real content instead of a false "I can't read the file".
                        // (Faithful-but-paraphrased summaries are NOT intercepted here anymore; only this
                        // unambiguous refusal is — see IsUngroundedDocumentOutput.)
                        LogActivity("Builder falsely claimed the document was unavailable; substituting source-grounded extractive synthesis.");
                        AppendChat("warning", "The drafted answer claimed the document couldn't be read even though its full text was provided, so it was replaced with a summary built directly from the source text.");
                        runContext.BuilderOutput = builderOutput;
                        builderOutput = BuildDeterministicDocumentResponse(runContext.UserPrompt, runContext.DocumentContent, runContext.DocumentFileNames);
                    }

                    if (taskType != CouncilTaskType.Coding && !DetectCodeOutput(builderOutput).IsCode)
                    {
                        int targetWords = TryGetRequestedWordTarget(runContext.UserPrompt, runContext.Objective);
                        if (targetWords > 0)
                        {
                            int words = CountWords(builderOutput);
                            int minAcceptable = (int)(targetWords * 0.9);
                            int continuationPass = 0;

                            while (words < minAcceptable && continuationPass < 2)
                            {
                                continuationPass++;
                                LogActivity($"Builder long-form continuation pass {continuationPass}: {words}/{targetWords} words.");

                                var continuationPayload = new StringBuilder();
                                continuationPayload.AppendLine(pipelineStateHeader);
                                continuationPayload.AppendLine(sharedVocabularySection);
                                if (!string.IsNullOrWhiteSpace(builderPriorKnowledge))
                                    continuationPayload.AppendLine(builderPriorKnowledge);
                                AppendCouncilWebContext(continuationPayload, runContext);
                                continuationPayload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, taskType, ""));
                                continuationPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
                                continuationPayload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", runContext.ArchitectOutput));
                                continuationPayload.AppendLine(BuildLabeledBlock("CURRENT DRAFT", builderOutput));
                                continuationPayload.AppendLine($"Continue the draft with additional high-value content until near {targetWords} words. " +
                                    "Do not restart from the beginning and do not repeat existing paragraphs. Output continuation text only.");

                                if (runContext.IsDocumentTask && !string.IsNullOrWhiteSpace(runContext.DocumentContent))
                                {
                                    continuationPayload.AppendLine(BuildDocumentContentBlock(runContext.DocumentContent, 8000));
                                    continuationPayload.AppendLine(DocumentGroundingInstruction);
                                }
                                else if (finalChunks.Count > 0)
                                    continuationPayload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));

                                var continuationResult = await ExecuteCouncilRoleAsync(
                                    CouncilRole.Builder,
                                    builderSystem,
                                    continuationPayload.ToString(),
                                    token,
                                    runContext.IsDocumentTask ? 0.25f : null,
                                    baseStateVault,
                                    true);
                                string continuationText = continuationResult.Answer
                                    .Replace(BuilderCompletionMarker, "", StringComparison.Ordinal)
                                    .Trim();

                                if (!string.IsNullOrWhiteSpace(continuationText))
                                    builderOutput = (builderOutput.TrimEnd() + "\n\n" + continuationText).Trim();

                                words = CountWords(builderOutput);
                            }
                        }
                    }

                    runContext.BuilderProducedCode = DetectCodeOutput(builderOutput).IsCode;
                    runContext.BuilderOutput = builderOutput;
                    contextState.BuilderOutput = builderOutput;
                    if (taskType == CouncilTaskType.Coding || runContext.BuilderProducedCode)
                    {
                        WriteBuilderSessionMemory(builderOutput, activeRunIndex);
                    }

                    var builderMeta = new StageMetadata
                    {
                        StageName = "Builder",
                        TruncationDetected = runContext.BuilderOutputTruncated
                    };
                    if (runContext.BuilderOutputTruncated)
                    {
                        runContext.BuilderTruncationRecovery = true;
                    }
                    runContext.PipelineMetadata.Add(builderMeta);

                    // Route output based on task type: coding tasks AND artifact canvas requests → Project Canvas
                    // Previously only CouncilTaskType.Coding routed to canvas, so artifact requests
                    // classified as General/Research/Analysis (e.g. "make a datasheet") were silently
                    // sent to chat instead. Now IsArtifactCanvasRequest also triggers the canvas path.
                    if (codebasePatchCaptured)
                    {
                        _chatHistory.Add(("builder", builderOutput));
                        UpdateWorkplaceTokenUsageIndicator();
                    }
                    else if (runContext.CanvasMutationFailed)
                    {
                        _chatHistory.Add(("builder", "[Canvas mutation rejected; existing Project Canvas preserved.]"));
                        UpdateWorkplaceTokenUsageIndicator();
                    }
                    else if (ShouldSuppressReasoningFallbackFromCanvas(builderOutput, builderReasoningFallback))
                    {
                        // Reasoning-only fallback: do NOT dump chain-of-thought into the canvas. Keep it
                        // hidden as builder thinking and tell the user no artifact was produced.
                        runContext.BuilderThinking = builderOutput;
                        LogActivity("Builder produced only reasoning (no renderable artifact). Suppressed from Project Canvas; retained as hidden thinking.");
                        AppendChat("builder", "Builder didn't produce a renderable artifact — only intermediate reasoning — so nothing was sent to the Project Canvas. Try rephrasing to explicitly request an HTML or SVG artifact.");
                        _chatHistory.Add(("builder", "[No renderable artifact produced — reasoning suppressed from canvas.]"));
                        UpdateWorkplaceTokenUsageIndicator();
                    }
                    else if (taskType == CouncilTaskType.Coding
                        || runContext.BuilderProducedCode
                        || (runContext.IsArtifactCanvasRequest && ArtifactRenderService.DetectForCanvas(builderOutput, null).SupportsPreview))
                    {
                        LogActivity($"Builder responded ({builderOutput.Length} chars). Routing to Project Canvas...");
                        UpdateProjectCanvas(builderOutput);

                        // Auto-expand the canvas pane when a renderable artifact arrives so the
                        // user sees the result without having to manually reopen the panel.
                        if (!_isProjectCanvasExpanded && _canvasArtifact.SupportsPreview)
                        {
                            _isProjectCanvasExpanded = true;
                            AnimateProjectCanvasPane(true);
                        }

                        bool isRenderable = _canvasArtifact.SupportsPreview;
                        AppendChat("builder", runContext.IsProjectCanvasIteration
                            ? (isRenderable
                                ? $"Builder applied a verified source change to the {_canvasArtifact.DisplayTitle}."
                                : "Builder applied a verified source change to Project Canvas.")
                            : runContext.IsArtifactCanvasRequest
                                ? (isRenderable
                                    ? $"Builder generated a renderable {_canvasArtifact.DisplayTitle} and sent it to the canvas."
                                    : "Builder output was routed to Project Canvas — no renderable artifact was detected in the output. The canvas shows the raw text. Try asking for an explicit HTML or SVG artifact.")
                                : "Builder output was sent to Project Canvas.");
                        _chatHistory.Add(("builder", builderOutput));
                        UpdateWorkplaceTokenUsageIndicator();
                    }
                    else if (runContext.IsArtifactCanvasRequest)
                    {
                        // Canvas request whose output is NOT renderable: keep the canvas untouched
                        // (don't overwrite a previous artifact with raw prose) and show the output
                        // in chat instead. Dumping non-renderable text into the canvas was a major
                        // source of "the canvas shows random text instead of what I asked for".
                        LogActivity($"Builder responded ({builderOutput.Length} chars) but produced no renderable artifact. Keeping canvas unchanged; sending output to chat.");
                        AppendChat("builder", builderOutput);
                        AppendChat("system", "No renderable artifact was produced, so the Project Canvas was left unchanged. Try asking explicitly for an HTML or SVG artifact.");
                        _chatHistory.Add(("builder", builderOutput));
                        UpdateWorkplaceTokenUsageIndicator();
                    }
                    else
                    {
                        LogActivity($"Builder responded ({builderOutput.Length} chars). Sending to chat (non-coding task).");
                        AppendChat("builder", builderOutput);
                        _chatHistory.Add(("builder", builderOutput));
                        UpdateWorkplaceTokenUsageIndicator();
                    }
                    UpdateStageIndicator(null, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), true, false);
                }
                else
                {
                    LogActivity("Builder stage skipped — no local council model available and cloud mode is off.");
                    AppendChat("warning", "Builder stage skipped — no model is available for the Builder. Load a council model or enable Cloud mode.");
                }

                // ═══════════════════════════════════════════════════════════
                // STATIC VALIDATION — deterministic checks before Critic
                // ═══════════════════════════════════════════════════════════
                if (!codebasePatchCaptured
                    && runContext.IsArtifactCanvasRequest
                    && !string.IsNullOrWhiteSpace(builderOutput))
                {
                    var staticFindings = runContext.StaticValidationFindings
                        .Where(finding => !LooksLikeArtifactValidationFinding(finding))
                        .ToList();
                    ArtifactRenderInfo artifactInfo = ArtifactRenderService.DetectForCanvas(builderOutput, null);
                    if (!artifactInfo.SupportsPreview)
                    {
                        staticFindings.Add("ARTIFACT CHECK: Builder output is not detected as a renderable Project Canvas artifact.");
                    }
                    else if (artifactInfo.Kind == ArtifactKind.Html)
                    {
                        var artifactValidation = ValidateProjectCanvasHtmlArtifact(builderOutput, runContext);
                        staticFindings.AddRange(artifactValidation.Failures.Select(failure => "ARTIFACT CHECK: " + failure));
                    }

                    staticFindings = staticFindings.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
                    runContext.StaticValidationFindings = staticFindings;
                    runContext.StaticValidationIssuesFound = staticFindings.Count > 0;
                    if (staticFindings.Count > 0)
                    {
                        LogActivity($"Artifact validation found {staticFindings.Count} pre-flagged issue(s).");
                        AppendChat("system", $"Artifact validation: {staticFindings.Count} issue(s) pre-flagged for Critic.");
                    }
                }
                else if (!codebasePatchCaptured
                    && (taskType == CouncilTaskType.Coding || runContext.BuilderProducedCode)
                    && !string.IsNullOrWhiteSpace(builderOutput))
                {
                    var staticFindings = runContext.StaticValidationFindings.ToList();
                    staticFindings.AddRange(RunStaticValidation(builderOutput));
                    foreach (string requirementWarning in VerifyFulfillment(runContext, builderOutput))
                        staticFindings.Add("REQUIREMENT CHECK (confirm against output): " + requirementWarning);
                    staticFindings = staticFindings.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
                    runContext.StaticValidationFindings = staticFindings;
                    runContext.StaticValidationIssuesFound = staticFindings.Count > 0;
                    if (staticFindings.Count > 0)
                    {
                        LogActivity($"Static validation found {staticFindings.Count} pre-flagged issue(s).");
                        AppendChat("system", $"Static validation: {staticFindings.Count} issue(s) pre-flagged for Critic.");
                    }

                    if (runContext.PythonSandboxEligible && DetectLanguage(builderOutput) == "python")
                    {
                        _activePythonSandboxPreamble = runContext.PythonSandboxPreamble;
                        try
                        {
                            string staticValidationSandbox = await ExecuteCodeSandboxAsync(builderOutput, "python", runContext);
                            var staticValidationErrors = DetectSandboxErrors(staticValidationSandbox);
                            if (staticValidationErrors.Count > 0)
                            {
                                runContext.StaticValidationFindings.AddRange(staticValidationErrors);
                                runContext.SandboxExceptionsFound = true;
                                LogActivity($"Static validation sandbox found {staticValidationErrors.Count} Python runtime issue(s) before Stage 3.");
                                AppendChat("system", $"Static validation sandbox: {staticValidationErrors.Count} Python runtime issue(s) pre-flagged for Critic.");
                            }
                        }
                        finally
                        {
                            _activePythonSandboxPreamble = string.Empty;
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════
                // FORMULA EXTRACTION — checklist from Architect for Critic
                // ═══════════════════════════════════════════════════════════
                if (isCalcTask && !string.IsNullOrWhiteSpace(runContext.ArchitectOutput))
                {
                    var formulaChecklist = ExtractFormulaChecklist(runContext.ArchitectOutput);
                    runContext.FormulaChecklist = formulaChecklist;
                    if (formulaChecklist.Count > 0)
                    {
                        LogActivity($"Formula checklist: {formulaChecklist.Count} formula(s) extracted from Architect plan.");
                        AppendChat("system", $"Formula checklist: {formulaChecklist.Count} formula(s) extracted for Critic verification.");
                    }
                }

                // --- Code Sandbox: execute builder code and inject results into critic ---
                string sandboxResult = "";
                if (!codebasePatchCaptured
                    && (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest)
                    && !string.IsNullOrWhiteSpace(builderOutput))
                {
                    string detectedLang = DetectLanguage(builderOutput);
                    if (detectedLang is "python" or "java" or "html")
                    {
                        LogActivity($"Code sandbox triggered for '{detectedLang}'. Executing...");
                        RelayStatusBlock.Text = $"Relay: Running {detectedLang} sandbox...";
                        PublishCouncilPetStatus("Sandbox", $"Validating {detectedLang.ToUpperInvariant()}.");
                        _activePythonSandboxPreamble = detectedLang == "python" ? _builderPythonSandboxPreamble : string.Empty;
                        sandboxResult = await ExecuteCodeSandboxAsync(builderOutput, detectedLang, runContext);
                        if (detectedLang == "python" && !string.IsNullOrWhiteSpace(sandboxResult))
                        {
                            var runtimeErrors = DetectSandboxErrors(sandboxResult);
                            if (runtimeErrors.Count > 0)
                            {
                                string correctedPython = await ExecuteBuilderPythonWithSingleRetryAsync(builderOutput, sandboxResult, runContext, token);
                                if (!string.IsNullOrWhiteSpace(correctedPython) && !string.Equals(correctedPython.Trim(), builderOutput.Trim(), StringComparison.Ordinal))
                                {
                                    builderOutput = correctedPython;
                                    runContext.BuilderOutput = correctedPython;
                                    contextState.BuilderOutput = correctedPython;
                                    UpdateProjectCanvas(correctedPython);
                                    sandboxResult = await ExecuteCodeSandboxAsync(correctedPython, detectedLang, runContext);
                                }
                            }
                        }

                        if (detectedLang == "python" && !string.IsNullOrWhiteSpace(sandboxResult) && !sandboxResult.StartsWith("[[PYTHON TIMEOUT]]", StringComparison.OrdinalIgnoreCase))
                            sandboxResult = FormatPythonResultBlock(sandboxResult);

                        if (!string.IsNullOrWhiteSpace(sandboxResult))
                        {
                            var sandboxDisplay = BuildSandboxExecutionDisplay(sandboxResult);
                            LogActivity($"Sandbox result captured ({sandboxResult.Length} chars). Injecting into Critic context.");
                            AppendChat("sandbox", $"{detectedLang} execution result:\n{sandboxDisplay.ChatDisplayPayload}");
                            sandboxResult = sandboxDisplay.CriticContextPayload;

                            var runtimeErrors = DetectSandboxErrors(sandboxResult);
                            if (runtimeErrors.Count > 0)
                            {
                                runContext.SandboxExceptionsFound = true;
                                runContext.StaticValidationFindings.AddRange(runtimeErrors);
                                sandboxFailed = true;
                                LogActivity($"Sandbox error detection: {runtimeErrors.Count} runtime error(s) pre-flagged as critical.");
                                AppendChat("error", $"Sandbox errors: {runtimeErrors.Count} runtime error(s) pre-flagged for Critic.");
                            }
                        }
                        else
                        {
                            LogActivity("Sandbox returned no output.");
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════════════
                // STAGE 3.5 — Zero-LLM Auto-Fix Loop (bypass Critic for fatal sandbox errors)
                // ═══════════════════════════════════════════════════════════════════
                while (taskType == CouncilTaskType.Coding
                    && sandboxFailed
                    && sandboxAutoFixRetries < 2
                    && HasEffectiveLocalRoleModel(CouncilRole.Builder)
                    && !CanUseCloudCouncil)
                {
                    sandboxAutoFixRetries++;
                    LogActivity($"Stage 3.5 auto-fix retry {sandboxAutoFixRetries}/2 due to sandbox failure.");
                    AppendChat("system", $"Auto-fix loop {sandboxAutoFixRetries}/2: attempting Builder runtime error fix before Critic.");

                    string autoFixSystem = GetEmbeddedSystemPrompt(CouncilRole.Builder)
                        + "\nYour code failed to execute. Fix the following error:";
                    if (IsQwen3Model(GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath ?? string.Empty))
                        autoFixSystem = BuildQwen3SystemPrompt(autoFixSystem, false);

                    var autoFixPayload = new StringBuilder();
                    autoFixPayload.AppendLine(BuildLabeledBlock("ORIGINAL CODE", builderOutput));
                    autoFixPayload.AppendLine(BuildLabeledBlock("SANDBOX STACK TRACE", sandboxResult));

                    var autoFixResult = await ExecuteCouncilRoleAsync(
                        CouncilRole.Builder,
                        autoFixSystem,
                        autoFixPayload.ToString(),
                        token,
                        null,
                        baseStateVault,
                        true);

                    string patched = PostProcessBuilderOutput(autoFixResult.Answer, runContext);
                    builderOutput = patched;
                    runContext.BuilderOutput = patched;
                    contextState.BuilderOutput = patched;
                    WriteBuilderSessionMemory(patched, activeRunIndex);
                    UpdateProjectCanvas(patched);
                    AppendChat("builder", $"Builder auto-fix attempt {sandboxAutoFixRetries} applied to Project Canvas.");

                    string recheckLang = DetectLanguage(patched);
                    if (recheckLang is "python" or "java" or "html")
                    {
                        sandboxResult = await ExecuteCodeSandboxAsync(patched, recheckLang, runContext);
                        var recheckErrors = DetectSandboxErrors(sandboxResult);
                        sandboxFailed = recheckErrors.Count > 0;
                        runContext.SandboxExceptionsFound = sandboxFailed;
                        if (sandboxFailed)
                        {
                            runContext.StaticValidationFindings.AddRange(recheckErrors);
                            LogActivity($"Stage 3.5 retry {sandboxAutoFixRetries} still failing; runtime errors persisted.");
                        }
                        else
                        {
                            LogActivity($"Stage 3.5 retry {sandboxAutoFixRetries} succeeded; bypassing Critic syntax pass.");
                            AppendChat("system", "Sandbox passed after auto-fix. Continuing pipeline.");
                        }
                    }
                    else
                    {
                        sandboxFailed = false;
                    }
                }

                // ═══════════════════════════════════════════════════════════════════
                // RANGE VALIDATION — compare sandbox numerical output vs formula expected ranges
                // ═══════════════════════════════════════════════════════════════════
                if (isCalcTask && !string.IsNullOrWhiteSpace(sandboxResult) && runContext.FormulaChecklist.Count > 0)
                {
                    var rangeViolations = ValidateOutputRanges(sandboxResult, runContext.FormulaChecklist);
                    if (rangeViolations.Count > 0)
                    {
                        runContext.StaticValidationFindings.AddRange(rangeViolations);
                        LogActivity($"Range validation: {rangeViolations.Count} violation(s) detected in sandbox output.");
                        AppendChat("warning", $"Range validation: {rangeViolations.Count} violation(s) pre-flagged for Critic.");
                    }
                }

                // ═══════════════════════════════════════════════════════════════════════
                // STAGE 3 — Critic: receives full context + static validation findings
                // ═══════════════════════════════════════════════════════════════════════
                string criticOutput = "";
                bool hasCritic = HasEffectiveLocalRoleModel(CouncilRole.Critic) || CanUseCloudCouncil;
                bool hasBuilder = HasEffectiveLocalRoleModel(CouncilRole.Builder) || CanUseCloudCouncil;
                bool skipCriticForSandbox = (taskType == CouncilTaskType.Coding && !sandboxFailed && sandboxAutoFixRetries > 0)
                    || codebasePatchCaptured;

                if (hasCritic && !string.IsNullOrWhiteSpace(builderOutput) && !skipCriticForSandbox)
                {
                    UpdateStageIndicator(CouncilRole.Critic, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), !string.IsNullOrWhiteSpace(runContext.BuilderOutput), false);
                    RelayStatusBlock.Text = "Relay: Critic is reviewing...";
                    LogActivity("Critic relay started — auditing builder output...");

                    string criticSystem = GetEmbeddedSystemPrompt(CouncilRole.Critic)
                        + (_criticSensitivity == CriticSensitivityLevel.Strict
                            ? "\n[SENSITIVITY: STRICT] Apply heightened scrutiny to all sections and flag anything that could cause incorrect behavior, even if minor."
                            : _criticSensitivity == CriticSensitivityLevel.CriticalOnly
                                ? "\n[SENSITIVITY: CRITICAL ONLY] Report only failures that would cause incorrect results or complete failure. Ignore style and minor structure issues."
                                : "")
                        + objectiveClause
                        + (runContext.IsArtifactCanvasRequest
                            ? BuildArtifactTaskTypeBoost(CouncilRole.Critic, runContext)
                            : GetTaskTypeBoost(taskType, CouncilRole.Critic))
                        + (runContext.IsArtifactCanvasRequest ? BuildArtifactCanvasBoost(CouncilRole.Critic, runContext.PreferredArtifactFormatHint, runContext) : "")
                        + (isCalcTask ? GetCalculationBoost(CouncilRole.Critic) : "")
                        + (runContext.CalculatorUsed ? "\n[CALCULATOR TOOL] Verify Builder math against [[CALCULATOR TOOL RESULTS]] and flag numeric inconsistencies." : "")
                        + BuildCriticContract(taskType, runContext.IsArtifactCanvasRequest)
                        + "\n[CRITIC VISIBILITY RULE] Do not output thinking, hidden reasoning, chain-of-thought, scratch analysis, or deliberation. Output only the final review contract."
                        + webSearchSystemNote
                        + "\nIf any PIPELINE HEALTH flag is true, increase scrutiny and verify Builder output against Architect plan step-by-step."
                        + (runContext.Complexity == TaskComplexity.Complex ? "\n[COMPLEX TASK MODE] Perform requirement-by-requirement verification against every requirement item." : "");
                    criticSystem = ComposeCouncilSystemPrompt(criticSystem, CouncilRole.Critic, runContext, GetSystemPromptDocumentBudgetChars(CouncilRole.Critic));
                    if (IsQwen3Model(GetEffectiveRoleConfig(CouncilRole.Critic).ModelPath ?? string.Empty))
                        criticSystem = BuildQwen3SystemPrompt(criticSystem, false);

                    string architectSummaryForCritic = BuildArchitectSummaryFromPlan(runContext.ArchitectOutput);
                    string builderSummaryForCritic = BuildBuilderSummaryFromCode(runContext.BuilderOutput);
                    string criticStateHeader = BuildPipelineStateHeader(architectSummaryForCritic, builderSummaryForCritic);
                    string criticQuery = runContext.UserPrompt + "\n" + runContext.BuilderOutput;
                    string criticPriorKnowledge = BuildPriorKnowledgeBlock(_sessionHippocampus.Query(criticQuery, 2));
                    contextState.BuilderOutput = runContext.BuilderOutput;
                    contextState.SandboxLogs = sandboxResult;
                    string criticPayloadStr = criticStateHeader
                        + criticPriorKnowledge
                        + BuildPipelineHealthSection(runContext)
                        + BuildRolePrimedPayload(CouncilRole.Critic, taskType, BuildCriticPayload(runContext, sandboxResult))
                        + BuildCouncilClosingAnchor(CouncilRole.Critic);
                    string previousCriticOutput = GetPreviousRoleOutput("critic");

                    var criticResult = await ExecuteCouncilRoleAsync(
                        CouncilRole.Critic, criticSystem, criticPayloadStr, token, null, baseStateVault, true);
                    criticOutput = criticResult.Answer;
                    string criticThinking = criticResult.ThinkingContent;
                    string criticCleaned = "";
                    bool criticReasoningLeak = criticResult.IsReasoningFallback || CriticContainsReasoningLeak(criticOutput);
                    bool criticMarkerFound = false;
                    bool criticContractOk = !criticReasoningLeak
                        && TryNormalizeCriticReview(criticOutput, taskType, out criticCleaned, out criticMarkerFound)
                        && !CriticHasRoleDrift(criticCleaned, taskType)
                        && !IsRepetitionLoop(criticCleaned, previousCriticOutput);

                    if (!criticContractOk)
                    {
                        CriticStageText.Text = "Critic · Retried";
                        string loopBreak = IsRepetitionLoop(criticOutput, previousCriticOutput)
                            ? "LOOP BREAK: your previous response repeated prior output. Produce a different, specific review." : "";
                        string previousCriticForRetry = criticReasoningLeak
                            ? "[Suppressed: previous Critic turn emitted hidden/internal reasoning instead of a final structured review.]"
                            : criticOutput;

                        string correctionPayload = criticStateHeader
                            + criticPriorKnowledge
                            + BuildPipelineHealthSection(runContext)
                            + BuildRolePrimedPayload(CouncilRole.Critic, taskType, BuildCriticPayload(runContext, sandboxResult))
                            + "\nROLE CORRECTION: Critic must provide specific references (line/function/step), must not output thinking or analysis notes, and must end with CRITIC REVIEW COMPLETE. "
                            + loopBreak + "\n\n" + BuildLabeledBlock("PREVIOUS CRITIC OUTPUT", previousCriticForRetry);

                        var criticRetry = await ExecuteCouncilRoleAsync(
                            CouncilRole.Critic, criticSystem, correctionPayload, token, null, baseStateVault, true);
                        criticOutput = criticRetry.Answer;
                        if (!string.IsNullOrWhiteSpace(criticRetry.ThinkingContent))
                            criticThinking = criticRetry.ThinkingContent;
                        bool retryReasoningLeak = criticRetry.IsReasoningFallback || CriticContainsReasoningLeak(criticOutput);

                        if (retryReasoningLeak
                            || !TryNormalizeCriticReview(criticOutput, taskType, out criticCleaned, out criticMarkerFound)
                            || CriticHasRoleDrift(criticCleaned, taskType))
                        {
                            if (runContext.IsDocumentTask)
                            {
                                LogActivity("Critic document validation retry failed; using deterministic critic review fallback.");
                                criticCleaned = BuildDeterministicDocumentCritic(runContext);
                                criticMarkerFound = true;
                            }
                            else
                            {
                                // The Builder output is already delivered to chat/canvas at this
                                // point; aborting the relay over an unparseable review discarded a
                                // usable result. Record a clean pass and tell the user no structured
                                // critique was produced this run.
                                LogActivity("Critic correction failed validation; recording no-findings result instead of aborting the relay.");
                                AppendChat("warning", "Critic review could not be validated after retry. The Builder output above is kept; no structured critique was produced this run.");
                                criticCleaned = "No issues found.";
                                criticMarkerFound = true;
                            }
                        }
                    }

                    criticOutput = criticCleaned;

                    if (!criticMarkerFound)
                    {
                        if (runContext.IsDocumentTask)
                        {
                            LogActivity("Critic output missing marker for document task; switching to deterministic critic report.");
                            criticOutput = BuildDeterministicDocumentCritic(runContext);
                            criticMarkerFound = true;
                        }
                        else
                        {
                            AppendChat("warning", "Critic output missing termination marker; accepted via normalization.");
                            LogActivity("Critic output accepted without marker using fallback normalization.");
                        }
                    }

                    if (runContext.IsDocumentTask)
                    {
                        runContext.BuilderOutput = BuildDeterministicDocumentQualityPass(runContext);
                        contextState.BuilderOutput = runContext.BuilderOutput;
                        builderOutput = runContext.BuilderOutput;

                        string deterministicCritic = BuildDeterministicDocumentCritic(runContext);
                        bool deterministicHasIssues = !deterministicCritic.StartsWith("No issues found", StringComparison.OrdinalIgnoreCase);
                        bool criticLooksWeak = string.IsNullOrWhiteSpace(criticOutput)
                            || (criticOutput.StartsWith("No issues found", StringComparison.OrdinalIgnoreCase) && deterministicHasIssues)
                            || CriticHasRoleDrift(criticOutput, taskType);

                        if (criticLooksWeak)
                        {
                            LogActivity("Critic document output was weak/incomplete; replaced with deterministic critic findings.");
                            criticOutput = deterministicCritic;
                        }
                    }

                    runContext.CriticReview = criticOutput;
                    contextState.CriticOutput = criticOutput;
                    runContext.CriticThinking = criticThinking;
                    WriteCriticSessionMemory(criticOutput, activeRunIndex);
                    var criticReport = CriticContractParser.Parse(criticOutput);
                    bool criticDetectedIssues = criticReport.HasIssues || CriticFoundIssues(criticOutput);
                    if (criticDetectedIssues && !criticReport.HasIssues)
                    {
                        criticReport.Status = "issues";
                        criticReport.Issues.Add(new CriticIssue
                        {
                            Severity = "medium",
                            Summary = "Critic signaled issues outside structured contract parsing.",
                            Evidence = criticOutput.Length > 240 ? criticOutput[..240] + "..." : criticOutput,
                            SuggestedFix = "Apply Critic findings and rerun Builder."
                        });
                    }
                    _lastCriticReport = criticReport;
                    _lastCriticRawOutput = criticOutput;

                    AppendChat("critic", criticOutput);
                    _chatHistory.Add(("critic", criticOutput));
                    UpdateWorkplaceTokenUsageIndicator();
                    LogActivity($"Critic responded ({criticOutput.Length} chars). Audit complete.");
                    UpdateStageIndicator(null, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), !string.IsNullOrWhiteSpace(runContext.BuilderOutput), true);

                    // Safety net: if sandbox found runtime errors but Critic missed them, force revision
                    if (!criticDetectedIssues && runContext.SandboxExceptionsFound)
                    {
                        criticDetectedIssues = true;
                        LogActivity("Safety net: Critic missed sandbox runtime errors — forcing revision.");
                        AppendChat("warning", "Sandbox detected runtime errors that Critic did not flag. Triggering revision.");
                        if (!criticReport.HasIssues)
                        {
                            criticReport.Status = "issues";
                            foreach (var finding in runContext.StaticValidationFindings.Where(f => f.Contains("RUNTIME", StringComparison.OrdinalIgnoreCase)))
                            {
                                criticReport.Issues.Add(new CriticIssue
                                {
                                    Severity = "critical",
                                    Summary = finding,
                                    Evidence = "Detected by sandbox execution",
                                    SuggestedFix = "Fix the runtime error in the code."
                                });
                            }
                            _lastCriticReport = criticReport;
                        }
                    }

                    // ═══════════════════════════════════════════════════════════════
                    // STAGE 4 — Weighted confidence routing after Critic
                    // ═══════════════════════════════════════════════════════════════
                    if (hasBuilder && criticDetectedIssues)
                    {
                        int totalIssueCount = criticReport.Issues?.Count ?? 0;
                        int issueCount = CountRevisionBlockingCriticIssues(
                            criticReport,
                            _criticSensitivity == CriticSensitivityLevel.Strict);
                        LogActivity($"Critic findings count: {totalIssueCount}; revision-blocking count: {issueCount}.");

                        if (issueCount == 0)
                        {
                            LogActivity("Critic findings were non-blocking; keeping current Builder output without repair pass.");
                            AppendChat("system", "Critic findings were advisory only. Project Canvas kept the current Builder output.");
                        }
                        else if (CanUseCloudCouncil)
                        {
                            LogActivity("Cloud council: Critic findings accepted as bounded revision feedback.");
                            AppendChat("system", "Cloud Critic found concrete issues. Running a bounded repair flow against the task contract.");
                        }

                        bool runFullRevision = issueCount >= 3;

                        if (issueCount == 0)
                        {
                            runFullRevision = false;
                        }
                        else if (issueCount >= 1 && issueCount <= 2 && builderRetryAttempts >= MaxBuilderRetryAttempts)
                        {
                            // The current Builder output is already delivered (chat/canvas); aborting the
                            // relay here would discard a usable result over remaining minor findings.
                            runFullRevision = false;
                            LogActivity($"Builder retry limit reached ({MaxBuilderRetryAttempts}); keeping current output instead of aborting the relay.");
                            AppendChat("warning", $"Builder retry limit reached ({MaxBuilderRetryAttempts}). Keeping the current output — remaining Critic findings are listed above.");
                        }
                        else if (issueCount >= 1 && issueCount <= 2)
                        {
                            builderRetryAttempts++;

                            // Targeted patch for minor issues
                            runContext.RevisionTriggered = true;
                            RevisionNoticeBlock.Visibility = Visibility.Visible;
                            LogActivity("Minor issues detected — running targeted patch (not full rewrite).");
                            BuilderStageText.Text = "Builder · Patched";
                            AppendChat("system", "Critic found minor issues. Running targeted patch...");
                            PipelineProgressBlock.Text = "Repair pass: Builder is patching Critic findings.";

                            UpdateStageIndicator(CouncilRole.Builder, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), false, true);
                            RelayStatusBlock.Text = "Relay: Bounded repair pass...";
                            PublishCouncilPetStatus("Builder", "Applying a small repair.");

                            string patchSystem = GetEmbeddedSystemPrompt(CouncilRole.Builder)
                                + objectiveClause
                                // Keep canvas-artifact requests on the artifact boost during patches —
                                // the plain task boost reintroduces the "no code fences" contradiction
                                // that the main builder pass already eliminates.
                                + (runContext.IsArtifactCanvasRequest
                                    ? BuildArtifactTaskTypeBoost(CouncilRole.Builder, runContext)
                                    : GetTaskTypeBoost(taskType, CouncilRole.Builder))
                                + (runContext.IsArtifactCanvasRequest ? BuildArtifactCanvasBoost(CouncilRole.Builder, runContext.PreferredArtifactFormatHint, runContext) : "")
                                + (!_isCloudModeEnabled ? BuildLocalBuilderCognitionBoost(taskType, runContext) : "")
                                + (isCalcTask ? GetCalculationBoost(CouncilRole.Builder, taskType) : "")
                                + (taskType == CouncilTaskType.Coding
                                    ? "\n[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. " +
                                      "Output the complete corrected code — do not output a diff or partial snippet. " +
                                      "Do not change anything that is not mentioned in the findings. " +
                                      "Do NOT include explanations, notes, or commentary about what you changed — output ONLY the raw corrected code."
                                    : "\n[TARGETED PATCH MODE] Fix ONLY the specific issues listed below. " +
                                      "Output the complete corrected final response text (not code) and preserve unaffected content. " +
                                      "Do not output a diff and do not include explanations about what changed.");
                            patchSystem += webSearchSystemNote + builderWebPauseSystemNote;
                            if (IsQwen3Model(GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath ?? string.Empty))
                                patchSystem = BuildQwen3SystemPrompt(patchSystem, false);

                            var patchPayload = new StringBuilder();
                            patchPayload.AppendLine(BuildCouncilGoalContractBlock(runContext.GoalContract));
                            patchPayload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, runContext.IsCloudExecution));
                            // Mirror the main builder pass: anchor the expected artifact format first.
                            if (runContext.IsArtifactCanvasRequest)
                                patchPayload.AppendLine(BuildCanvasArtifactAnchorBlock(runContext));
                            patchPayload.AppendLine(sharedVocabularySection);
                            if (!string.IsNullOrWhiteSpace(builderPriorKnowledge))
                                patchPayload.AppendLine(builderPriorKnowledge);
                            AppendCouncilWebContext(patchPayload, runContext);
                            patchPayload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, taskType, ""));
                            patchPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
                            patchPayload.AppendLine(BuildLabeledBlock("BUILDER OUTPUT", runContext.BuilderOutput));
                            patchPayload.AppendLine(BuildLabeledBlock("CRITIC FINDINGS", runContext.CriticReview));
                            if (runContext.IsDocumentTask && !string.IsNullOrWhiteSpace(runContext.DocumentContent))
                            {
                                patchPayload.AppendLine(BuildDocumentContentBlock(
                                    runContext.DocumentContent,
                                    GetCloudDocumentCharacterBudget(runContext, 6000)));
                            }

                            var patchResult = await ExecuteCouncilRoleAsync(
                                CouncilRole.Builder, patchSystem, patchPayload.ToString(), token, null, baseStateVault, true);
                            string patchedOutput = PostProcessBuilderOutput(patchResult.Answer, runContext);
                            if (runContext.WebGroundingRequired
                                && !HasCouncilWebEvidenceForRun(runContext)
                                && taskType != CouncilTaskType.Coding
                                && !runContext.IsArtifactCanvasRequest
                                && !DetectCodeOutput(patchedOutput).IsCode
                                && !BuilderStatesWebEvidenceUnavailable(patchedOutput))
                            {
                                patchedOutput = BuildWebEvidenceUnavailableBuilderFallback(runContext);
                            }
                            builderReasoningFallback = patchResult.IsReasoningFallback;

                            // Post-patch verification for coding/artifact tasks
                            bool patchEscalate = false;
                            if (runContext.IsArtifactCanvasRequest)
                            {
                                var postPatchFindings = BuildProjectCanvasFinalVerificationFailures(runContext, patchedOutput);
                                if (postPatchFindings.Count > 0)
                                {
                                    LogActivity($"Post-patch artifact validation found {postPatchFindings.Count} issue(s) - escalating to full revision.");
                                    AppendChat("warning", $"Patch left {postPatchFindings.Count} artifact issue(s). Escalating to full revision.");
                                    patchEscalate = true;
                                }
                            }
                            else if (taskType == CouncilTaskType.Coding || DetectCodeOutput(patchedOutput).IsCode)
                            {
                                var postPatchFindings = RunStaticValidation(patchedOutput);
                                postPatchFindings.AddRange(VerifyFulfillment(runContext, patchedOutput));
                                postPatchFindings = postPatchFindings.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                if (postPatchFindings.Count > 0)
                                {
                                    LogActivity($"Post-patch static validation found {postPatchFindings.Count} new issue(s) — escalating to full revision.");
                                    AppendChat("warning", $"Patch introduced {postPatchFindings.Count} new issue(s). Escalating to full revision.");
                                    patchEscalate = true;
                                }

                                if (!patchEscalate)
                                {
                                    string postPatchLang = DetectLanguage(patchedOutput);
                                    if (postPatchLang is "python" or "java" or "html")
                                    {
                                        string postPatchSandbox = await ExecuteCodeSandboxAsync(patchedOutput, postPatchLang, runContext);
                                        if (!string.IsNullOrWhiteSpace(postPatchSandbox))
                                        {
                                            var postPatchErrors = DetectSandboxErrors(postPatchSandbox);
                                            if (postPatchErrors.Count > 0)
                                            {
                                                runContext.SandboxExceptionsFound = true;
                                                LogActivity($"Post-patch sandbox detected {postPatchErrors.Count} runtime error(s) — escalating to full revision.");
                                                AppendChat("warning", $"Patch caused {postPatchErrors.Count} runtime error(s). Escalating to full revision.");
                                                patchEscalate = true;
                                                runContext.StaticValidationFindings.AddRange(postPatchErrors);
                                            }
                                        }
                                    }
                                }
                            }

                            if (!patchEscalate)
                            {
                                builderOutput = patchedOutput;
                                runContext.BuilderOutput = builderOutput;
                                runContext.BuilderThinking = patchResult.ThinkingContent;
                                runContext.BuilderProducedCode = DetectCodeOutput(builderOutput).IsCode;
                                if (taskType == CouncilTaskType.Coding || runContext.BuilderProducedCode)
                                {
                                    WriteBuilderSessionMemory(builderOutput, activeRunIndex);
                                }
                                if (ShouldSuppressReasoningFallbackFromCanvas(builderOutput, builderReasoningFallback))
                                {
                                    runContext.BuilderThinking = builderOutput;
                                    LogActivity("Builder patch produced only reasoning (no renderable artifact). Suppressed from Project Canvas.");
                                    AppendChat("builder", "Builder patch didn't produce a renderable artifact — only reasoning — so the Project Canvas was left unchanged.");
                                    _chatHistory.Add(("builder-patch", "[No renderable artifact produced — reasoning suppressed from canvas.]"));
                                    UpdateWorkplaceTokenUsageIndicator();
                                }
                                else if (taskType == CouncilTaskType.Coding
                                    || runContext.BuilderProducedCode
                                    || (runContext.IsArtifactCanvasRequest && ArtifactRenderService.DetectForCanvas(builderOutput, null).SupportsPreview))
                                {
                                    UpdateProjectCanvas(builderOutput);
                                    AppendChat("builder", runContext.IsArtifactCanvasRequest
                                        ? "Builder artifact patch sent to Project Canvas."
                                        : "Builder patch sent to Project Canvas.");
                                    _chatHistory.Add(("builder-patch", builderOutput));
                                    UpdateWorkplaceTokenUsageIndicator();

                                    if (runContext.IsArtifactCanvasRequest)
                                    {
                                        string artifactPatchLang = DetectLanguage(builderOutput);
                                        if (artifactPatchLang == "html")
                                        {
                                            sandboxResult = await ExecuteCodeSandboxAsync(builderOutput, artifactPatchLang, runContext);
                                            if (!string.IsNullOrWhiteSpace(sandboxResult))
                                            {
                                                var sandboxDisplay = BuildSandboxExecutionDisplay(sandboxResult);
                                                AppendChat("sandbox", $"{artifactPatchLang} post-repair result:\n{sandboxDisplay.ChatDisplayPayload}");
                                                sandboxResult = sandboxDisplay.CriticContextPayload;
                                                var postRepairErrors = DetectSandboxErrors(sandboxResult);
                                                if (postRepairErrors.Count > 0)
                                                {
                                                    runContext.SandboxExceptionsFound = true;
                                                    runContext.StaticValidationFindings.AddRange(postRepairErrors);
                                                }
                                                else
                                                {
                                                    runContext.SandboxExceptionsFound = false;
                                                }
                                            }
                                        }
                                    }
                                }
                                else if (runContext.IsArtifactCanvasRequest)
                                {
                                    // Non-renderable patch output: keep the existing canvas artifact.
                                    AppendChat("builder", builderOutput);
                                    AppendChat("system", "Patch produced no renderable artifact — Project Canvas left unchanged.");
                                    _chatHistory.Add(("builder-patch", builderOutput));
                                    UpdateWorkplaceTokenUsageIndicator();
                                }
                                else
                                {
                                    AppendChat("builder", builderOutput);
                                    _chatHistory.Add(("builder-patch", builderOutput));
                                    UpdateWorkplaceTokenUsageIndicator();
                                }
                                UpdateStageIndicator(null, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), true, true);
                            }
                            else
                            {
                                runFullRevision = true;
                            }
                        }

                        if (runFullRevision && builderRetryAttempts >= MaxBuilderRetryAttempts)
                        {
                            // Same rationale as the patch path: keep the delivered output rather than
                            // converting a quality follow-up into a hard relay failure.
                            LogActivity($"Builder retry limit reached ({MaxBuilderRetryAttempts}); skipping full revision and keeping current output.");
                            AppendChat("warning", $"Builder retry limit reached ({MaxBuilderRetryAttempts}). Full revision skipped — remaining Critic findings are listed above.");
                        }
                        else if (runFullRevision)
                        {
                            builderRetryAttempts++;

                            // Full rewrite for 3+ issues or escalated patch failure
                            runContext.RevisionTriggered = true;
                            RevisionNoticeBlock.Visibility = Visibility.Visible;
                            BuilderStageText.Text = "Builder · Revised";
                            LogActivity("Multiple issues detected — initiating full revision.");
                            AppendChat("system", "Critic found multiple issues. Sending back to Builder for full revision...");
                            PipelineProgressBlock.Text = "Repair pass: Builder is revising after Critic findings.";

                            // --- Revision Builder: full rewrite with Critic findings as structured checklist ---
                            UpdateStageIndicator(CouncilRole.Builder, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), false, true);
                            RelayStatusBlock.Text = "Relay: Bounded repair pass...";
                            PublishCouncilPetStatus("Builder", "Revising the artifact.");

                            string revisionBuilderSystem = GetEmbeddedSystemPrompt(CouncilRole.Builder)
                                + objectiveClause
                                // Same artifact-boost continuity as the main pass and patch path.
                                + (runContext.IsArtifactCanvasRequest
                                    ? BuildArtifactTaskTypeBoost(CouncilRole.Builder, runContext)
                                    : GetTaskTypeBoost(taskType, CouncilRole.Builder))
                                + (runContext.IsArtifactCanvasRequest ? BuildArtifactCanvasBoost(CouncilRole.Builder, runContext.PreferredArtifactFormatHint, runContext) : "")
                                + (!_isCloudModeEnabled ? BuildLocalBuilderCognitionBoost(taskType, runContext) : "")
                                + GetTokenBudgetHint(runContext)
                                + (isCalcTask ? GetCalculationBoost(CouncilRole.Builder, taskType) : "")
                                + (taskType == CouncilTaskType.Coding
                                    ? "\n[REVISION MODE] Rewrite the entire output from scratch. " +
                                      "Do NOT patch inline. Produce a complete replacement. " +
                                      "Address every numbered finding from the Critic in your revised code. " +
                                      "Do not fix one issue and reintroduce another. " +
                                      "Output ONLY the corrected code — no explanations, no prose, no notes about what changed."
                                    : "\n[REVISION MODE] Rewrite the entire output from scratch. " +
                                      "Do NOT patch inline. Produce a complete replacement response text. " +
                                      "Address every numbered finding from the Critic and preserve factual grounding. " +
                                      "Output ONLY the corrected final response — no change log or commentary.");
                            revisionBuilderSystem += webSearchSystemNote + builderWebPauseSystemNote;
                            if (IsQwen3Model(GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath ?? string.Empty))
                                revisionBuilderSystem = BuildQwen3SystemPrompt(revisionBuilderSystem, false);

                            var revisionPayload = new StringBuilder();
                            revisionPayload.AppendLine(BuildCouncilGoalContractBlock(runContext.GoalContract));
                            revisionPayload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, runContext.IsCloudExecution));
                            // Mirror the main builder pass: anchor the expected artifact format first.
                            if (runContext.IsArtifactCanvasRequest)
                                revisionPayload.AppendLine(BuildCanvasArtifactAnchorBlock(runContext));
                            revisionPayload.AppendLine(sharedVocabularySection);
                            if (!string.IsNullOrWhiteSpace(builderPriorKnowledge))
                                revisionPayload.AppendLine(builderPriorKnowledge);
                            AppendCouncilWebContext(revisionPayload, runContext);
                            revisionPayload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, taskType, ""));
                            revisionPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));

                            if (!string.IsNullOrWhiteSpace(runContext.ArchitectOutput))
                            {
                                revisionPayload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", runContext.ArchitectOutput));
                            }

                            revisionPayload.AppendLine(BuildLabeledBlock("BUILDER OUTPUT", runContext.BuilderOutput));
                            revisionPayload.AppendLine(BuildLabeledBlock("CRITIC FINDINGS", runContext.CriticReview));

                            if (runContext.IsDocumentTask && !string.IsNullOrWhiteSpace(runContext.DocumentContent))
                            {
                                revisionPayload.AppendLine(BuildDocumentContentBlock(
                                    runContext.DocumentContent,
                                    GetCloudDocumentCharacterBudget(runContext, 7000)));
                            }

                            if (finalChunks.Count > 0)
                            {
                                revisionPayload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
                            }

                            var revisedBuilderResult = await ExecuteCouncilRoleAsync(
                                CouncilRole.Builder,
                                revisionBuilderSystem,
                                revisionPayload.ToString(),
                                token,
                                runContext.IsDocumentTask ? 0.25f : null,
                                baseStateVault,
                                true);
                            string revisedBuilderOutput = revisedBuilderResult.Answer;
                            revisedBuilderOutput = PostProcessBuilderOutput(revisedBuilderOutput, runContext);
                            if (runContext.WebGroundingRequired
                                && !HasCouncilWebEvidenceForRun(runContext)
                                && taskType != CouncilTaskType.Coding
                                && !runContext.IsArtifactCanvasRequest
                                && !DetectCodeOutput(revisedBuilderOutput).IsCode
                                && !BuilderStatesWebEvidenceUnavailable(revisedBuilderOutput))
                            {
                                revisedBuilderOutput = BuildWebEvidenceUnavailableBuilderFallback(runContext);
                            }
                            builderReasoningFallback = revisedBuilderResult.IsReasoningFallback;

                            if (runContext.IsArtifactCanvasRequest)
                            {
                                var revisionFindings = BuildProjectCanvasFinalVerificationFailures(runContext, revisedBuilderOutput);
                                foreach (string finding in revisionFindings.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
                                    AppendChat("warning", "Post-revision artifact verification: " + finding);
                            }
                            else if (taskType == CouncilTaskType.Coding || DetectCodeOutput(revisedBuilderOutput).IsCode)
                            {
                                var revisionFindings = RunStaticValidation(revisedBuilderOutput);
                                revisionFindings.AddRange(VerifyFulfillment(runContext, revisedBuilderOutput));
                                foreach (string finding in revisionFindings.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
                                    AppendChat("warning", "Post-revision verification: " + finding);
                            }

                            LogActivity($"Builder revision complete ({revisedBuilderOutput.Length} chars). Routing output...");
                            builderOutput = revisedBuilderOutput;
                            runContext.BuilderProducedCode = DetectCodeOutput(builderOutput).IsCode;
                            runContext.BuilderOutput = builderOutput;
                            contextState.BuilderOutput = builderOutput;
                            runContext.BuilderThinking = revisedBuilderResult.ThinkingContent;
                            if (taskType == CouncilTaskType.Coding || runContext.BuilderProducedCode)
                            {
                                WriteBuilderSessionMemory(builderOutput, activeRunIndex);
                            }
                            if (ShouldSuppressReasoningFallbackFromCanvas(builderOutput, builderReasoningFallback))
                            {
                                runContext.BuilderThinking = builderOutput;
                                LogActivity("Builder revision produced only reasoning (no renderable artifact). Suppressed from Project Canvas.");
                                AppendChat("builder", "Builder revision didn't produce a renderable artifact — only reasoning — so the Project Canvas was left unchanged.");
                                _chatHistory.Add(("builder-revision", "[No renderable artifact produced — reasoning suppressed from canvas.]"));
                                UpdateWorkplaceTokenUsageIndicator();
                            }
                            else
                            {
                                if (taskType == CouncilTaskType.Coding
                                    || runContext.BuilderProducedCode
                                    || (runContext.IsArtifactCanvasRequest && ArtifactRenderService.DetectForCanvas(builderOutput, null).SupportsPreview))
                                {
                                    UpdateProjectCanvas(builderOutput);
                                    AppendChat("builder", runContext.IsArtifactCanvasRequest
                                        ? "Builder artifact revision sent to Project Canvas."
                                        : "Builder revision sent to Project Canvas.");
                                }
                                else if (runContext.IsArtifactCanvasRequest)
                                {
                                    // Non-renderable revision output: keep the existing canvas artifact.
                                    AppendChat("builder", builderOutput);
                                    AppendChat("system", "Revision produced no renderable artifact — Project Canvas left unchanged.");
                                }
                                else
                                {
                                    AppendChat("builder", builderOutput);
                                }
                                _chatHistory.Add(("builder-revision", builderOutput));
                                UpdateWorkplaceTokenUsageIndicator();
                            }
                            UpdateStageIndicator(null, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), true, true);
                        }
                    }
                }
                else if (skipCriticForSandbox)
                {
                    LogActivity("Stage 3.5 completed. Critic bypassed to avoid wasting context on resolved runtime errors.");
                    AppendChat("system", "Critic bypassed: sandbox runtime errors were resolved by Stage 3.5 auto-fix loop.");
                }
                else if (hasCritic && string.IsNullOrWhiteSpace(builderOutput)
                      && !string.IsNullOrWhiteSpace(architectOutput))
                {
                    // Critic reviews architect output when no builder is loaded
                    UpdateStageIndicator(CouncilRole.Critic, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), false, false);
                    RelayStatusBlock.Text = "Relay: Critic is reviewing...";
                    string criticSystem = GetEmbeddedSystemPrompt(CouncilRole.Critic)
                        + (_criticSensitivity == CriticSensitivityLevel.Strict
                            ? "\n[SENSITIVITY: STRICT] Apply heightened scrutiny to all sections and flag anything that could cause incorrect behavior, even if minor."
                            : _criticSensitivity == CriticSensitivityLevel.CriticalOnly
                                ? "\n[SENSITIVITY: CRITICAL ONLY] Report only failures that would cause incorrect results or complete failure. Ignore style and minor structure issues."
                                : "")
                        + objectiveClause
                        + (runContext.IsArtifactCanvasRequest && IsSmallLocalCouncilModel(GetEffectiveRoleConfig(CouncilRole.Critic).ModelPath ?? GetEffectiveRoleConfig(CouncilRole.Critic).DisplayName, _isCloudModeEnabled) ? BuildSmallModelArtifactAssist(CouncilRole.Critic, runContext.PreferredArtifactFormatHint, runContext) : "")
                        + BuildCriticContract(taskType, runContext.IsArtifactCanvasRequest)
                        + "\n[CRITIC VISIBILITY RULE] Do not output thinking, hidden reasoning, chain-of-thought, scratch analysis, or deliberation. Output only the final review contract.";
                    criticSystem = ComposeCouncilSystemPrompt(criticSystem, CouncilRole.Critic, runContext, GetSystemPromptDocumentBudgetChars(CouncilRole.Critic));
                    if (IsQwen3Model(GetEffectiveRoleConfig(CouncilRole.Critic).ModelPath ?? string.Empty))
                        criticSystem = BuildQwen3SystemPrompt(criticSystem, false);
                    string criticOnlyPrior = BuildPriorKnowledgeBlock(_sessionHippocampus.Query(runContext.UserPrompt + "\n" + runContext.ArchitectOutput, 5));
                    string criticPayloadStr = BuildPipelineStateHeader(BuildArchitectSummaryFromPlan(runContext.ArchitectOutput), "")
                        + sharedVocabularySection
                        + criticOnlyPrior
                        + BuildPipelineHealthSection(runContext)
                        + BuildRolePrimedPayload(CouncilRole.Critic, taskType, BuildCriticPayload(runContext, ""))
                        + BuildCouncilClosingAnchor(CouncilRole.Critic);
                    var criticOnlyResult = await ExecuteCouncilRoleAsync(
                        CouncilRole.Critic, criticSystem, criticPayloadStr, token, null, baseStateVault, true);
                    criticOutput = criticOnlyResult.Answer;
                    bool criticOnlyLeak = criticOnlyResult.IsReasoningFallback || CriticContainsReasoningLeak(criticOutput);
                    if (criticOnlyLeak || !TryNormalizeCriticReview(criticOutput, taskType, out string criticOnlyCleaned, out bool _))
                    {
                        // Critic-only runs used to hard-abort on a missing marker — harsher than the
                        // main path, which accepts normalized output with a warning. Keep the raw
                        // review instead of failing the relay.
                        LogActivity("Critic-only review failed validation; suppressing invalid/raw reasoning output.");
                        AppendChat("warning", "Critic review could not be validated, so no structured critique was shown.");
                        criticOnlyCleaned = "No issues found.";
                    }
                    criticOutput = criticOnlyCleaned;
                    runContext.CriticReview = criticOutput;
                    runContext.CriticThinking = criticOnlyResult.ThinkingContent;
                    AppendChat("critic", criticOutput);
                    _chatHistory.Add(("critic", criticOutput));
                    UpdateWorkplaceTokenUsageIndicator();
                    UpdateStageIndicator(null, !string.IsNullOrWhiteSpace(runContext.ArchitectOutput), false, true);
                }
                else if (!hasBuilder && !hasCritic && !string.IsNullOrWhiteSpace(architectOutput))
                {
                    AppendChat("architect", architectOutput);
                }

                // Deterministic fulfillment verification (post-critic, pre-delivery finalization)
                List<string> finalVerificationFailures = [];
                if (hasCritic && !string.IsNullOrWhiteSpace(runContext.CriticReview))
                {
                    var finalReview = CriticContractParser.Parse(runContext.CriticReview);
                    string finalOutputForCheck = (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest)
                        ? ProjectCanvasEditor.Text
                        : (runContext.BuilderOutput ?? "");
                    finalVerificationFailures = BuildFinalVerificationFailures(runContext, finalOutputForCheck);
                    if (finalVerificationFailures.Count > 0)
                    {
                        string warningSection = "WARNING: The final output does not yet satisfy the original request:\n- "
                            + string.Join("\n- ", finalVerificationFailures.Take(12));
                        AppendChat("warning", warningSection);
                    }
                }
                else
                {
                    string finalOutputForCheck = (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest)
                        ? ProjectCanvasEditor.Text
                        : (runContext.BuilderOutput ?? "");
                    finalVerificationFailures = BuildFinalVerificationFailures(runContext, finalOutputForCheck);
                    if (finalVerificationFailures.Count > 0)
                    {
                        string warningSection = "WARNING: The final output does not yet satisfy the original request:\n- "
                            + string.Join("\n- ", finalVerificationFailures.Take(12));
                        AppendChat("warning", warningSection);
                    }
                }

                if (runContext.IsArtifactCanvasRequest)
                {
                    string finalArtifactOutput = ProjectCanvasEditor.Text;
                    ReconcileArtifactValidationState(runContext, finalArtifactOutput);
                }

                if (finalVerificationFailures.Count > 0)
                {
                    runContext.FinalVerificationFailed = true;
                    RevisionNoticeBlock.Text = "Final verification found unresolved requirements: " + finalVerificationFailures[0];
                    RevisionNoticeBlock.Visibility = Visibility.Visible;
                }

                if (runContext.IsDocumentTask && !string.IsNullOrWhiteSpace(runContext.BuilderOutput))
                {
                    var contentWarnings = DetectDocumentHallucinations(runContext.BuilderOutput, runContext.DocumentContent);
                    foreach (var warning in contentWarnings.Distinct(StringComparer.OrdinalIgnoreCase).Take(12))
                    {
                        AppendChat("warning", warning);
                    }
                }

                // ═══════════════════════════════════════════════════════
                // POST-RUN — Store session memory
                // ═══════════════════════════════════════════════════════
                bool isCodingTask = taskType == CouncilTaskType.Coding;
                _sessionMemory = new SessionMemoryState
                {
                    ArchitectPlan = runContext.ArchitectOutput,
                    BuilderOutput = isCodingTask ? "" : runContext.BuilderOutput,
                    CriticSummary = runContext.CriticReview,
                    TaskDescription = userQuery.Length > 200 ? userQuery[..200] : userQuery,
                    TaskType = taskType
                };
                WriteGoalContractSessionMemory(runContext.GoalContract, activeRunIndex);
                SessionMemoryStatusBlock.Text = $"Prior run stored ({DateTime.Now:HH:mm})";

                _lastRunContext = runContext;
                _lastSandboxOutput = sandboxResult;
                _lastFinalOutput = (taskType == CouncilTaskType.Coding || runContext.IsArtifactCanvasRequest) ? ProjectCanvasEditor.Text : runContext.BuilderOutput;

                if (refinementPass)
                {
                    AppendChat("system", "Refinement diff:\n" + BuildSimpleDiff(previousFinalForDiff, _lastFinalOutput));
                }

                int criticFindings = CountCriticFindings(runContext.CriticReview);
                if (runContext.FinalVerificationFailed)
                    criticFindings = Math.Max(criticFindings, finalVerificationFailures.Count);
                else if (runContext.IsArtifactCanvasRequest)
                    criticFindings = 0;
                string confidenceLabel = runContext.FinalVerificationFailed
                    ? "Flagged for Review"
                    : BuildConfidenceLabel(criticFindings, runContext.RevisionTriggered);
                ShowConfidenceLabel(confidenceLabel);
                AddTaskHistoryEntry(runContext, _lastFinalOutput, criticFindings, refinementParentId);
                AddPerformanceLogEntry(runContext, criticFindings);

                _sessionHippocampus.Consolidate();
                _completedCouncilRunCount++;
                SavePersistedSession();

                RelayStatusBlock.Text = runContext.FinalVerificationFailed
                    ? "Relay: Completed with unresolved requirements"
                    : "Relay: Completed";
                PublishCouncilPetStatus(
                    runContext.FinalVerificationFailed ? "Review" : "Council",
                    runContext.FinalVerificationFailed ? "Done, but needs review." : "Done. Looks good.");
                _submittedRunPrompt = string.Empty;
                _lastCancelledRunPrompt = string.Empty;
                LogActivity(runContext.FinalVerificationFailed
                    ? "Council relay completed with unresolved final verification requirements."
                    : "Council relay completed successfully.");
                ChatScrollViewer.ScrollToEnd();
            }
            catch (OperationCanceledException)
            {
                RelayStatusBlock.Text = "Relay: Stopped";
                PublishCouncilPetStatus("Council", "Run stopped.");
                UpdateStageIndicator(null, false, false, false);
                PipelineProgressBlock.Text = string.Empty;
                _lastCancelledRunPrompt = string.IsNullOrWhiteSpace(_submittedRunPrompt)
                    ? userInstruction
                    : _submittedRunPrompt;
                if (!string.IsNullOrWhiteSpace(_lastCancelledRunPrompt))
                {
                    QueryInput.Text = _lastCancelledRunPrompt;
                    QueryInput.Focus();
                }
                _lastRolePromptTokenEstimates.Clear();
                _lastRoleGeneratedTokenCounts.Clear();
                _pipelineTokenCount = 0;
                ResetWorkplaceTokenUsageIndicator();
                LogActivity("Council relay stopped by user.");
                AppendChat("system", "Relay stopped by user.");
            }
            catch (Exception ex)
            {
                RelayStatusBlock.Text = "Relay: Error";
                PublishCouncilPetStatus("Council", "Something needs attention.");
                UpdateStageIndicator(null, false, false, false);
                LogActivity($"Relay error: {ex.Message}");
                await BackendLogService.LogErrorAsync("Workplace.Relay", ex);
                AppendChat("error", ex is OpenRouterRateLimitedException
                    ? ex.Message + " (All free models are currently rate-limited. Add an OpenRouter key/credits or retry shortly.)"
                    : ex.Message);
                if (ex is OutOfMemoryException || ex is IOException)
                {
                    _ = ShowNonIntrusiveErrorAsync($"Pipeline error: {ex.Message}");
                }
            }
            finally
            {
                FinalizeOrphanStreamingCards();
                _builderPythonSandboxPreamble = string.Empty;
                _activePythonSandboxPreamble = string.Empty;
                _activeCouncilWebPrompt = string.Empty;
                _activeCouncilRunContext = null;
                CleanupCouncilBaseStateVault(baseStateVault);
                StopPipelineProgress();
                _nextPromptPriorityChunks.Clear();
                _nextPromptPriorityConcept = null;
                MemoryFocusBlock.Text = "Memory Focus: None";

                _isProcessing = false;
                SendButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                _activeTaskComplexity = TaskComplexity.Moderate;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
            }
        }

        private List<DocumentChunk> MergeWithPriority(List<DocumentChunk> baseChunks, int maxChunks)
        {
            if (_nextPromptPriorityChunks.Count == 0)
            {
                return baseChunks;
            }

            var result = new List<DocumentChunk>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var chunk in _nextPromptPriorityChunks)
            {
                string key = $"{chunk.FileName}:{chunk.ChunkId}";
                if (seen.Add(key))
                {
                    result.Add(chunk);
                }

                if (result.Count >= maxChunks)
                {
                    return result;
                }
            }

            foreach (var chunk in baseChunks)
            {
                string key = $"{chunk.FileName}:{chunk.ChunkId}";
                if (seen.Add(key))
                {
                    result.Add(chunk);
                }

                if (result.Count >= maxChunks)
                {
                    break;
                }
            }

            return result;
        }

        // ═══════════════════════════════════════════════
        // Architect Step Counting & Segmented Builder
        // ═══════════════════════════════════════════════

        private static int CountArchitectSteps(string architectOutput)
        {
            if (string.IsNullOrWhiteSpace(architectOutput))
                return 0;

            if (IsArchitectArtifactHandoff(architectOutput))
            {
                int checks = Regex.Matches(architectOutput, @"(?m)^\s*-\s+").Count;
                return Math.Max(1, checks);
            }

            int count = 0;
            foreach (var line in architectOutput.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4)
                    count++;
            }
            return count;
        }

        private static List<List<string>> PartitionArchitectSteps(string architectOutput, int stepsPerSegment = 2)
        {
            var allSteps = new List<string>();
            foreach (var line in architectOutput.Split('\n'))
            {
                if (ArchitectNumberedStepRegex.IsMatch(line))
                    allSteps.Add(line);
            }

            var segments = new List<List<string>>();
            for (int i = 0; i < allSteps.Count; i += stepsPerSegment)
            {
                segments.Add(allSteps.GetRange(i, Math.Min(stepsPerSegment, allSteps.Count - i)));
            }
            return segments;
        }

        private static bool ValidateArchitectPlanQuality(string sanitizedPlanText, PreFlightDecomposition decomposition)
        {
            int stepCount = 0;
            foreach (var line in (sanitizedPlanText ?? string.Empty).Split('\n'))
            {
                if (ArchitectNumberedStepRegex.IsMatch(line))
                    stepCount++;
            }

            if (stepCount == 0)
                return false;

            int requirementCount = decomposition?.Requirements?.Count ?? 0;
            if (requirementCount >= 4 && stepCount < Math.Ceiling(requirementCount / 2d))
                return false;

            return true;
        }

        private async Task<string> ExecuteSegmentedBuilderAsync(
            CouncilRunContext runContext, string builderSystem, string knowledgePacket, bool hasKnowledge, string priorKnowledgeBlock, CancellationToken token, CouncilBaseStateVault? baseStateVault)
        {
            var segments = PartitionArchitectSteps(runContext.ArchitectOutput, 2);
            var assembledCode = new StringBuilder();
            string architectStateSummary = BuildArchitectSummaryFromPlan(runContext.ArchitectOutput);
            string pipelineStateHeader = BuildPipelineStateHeader(architectStateSummary, "");
            string sharedVocabularySection = BuildSharedVocabularySection(runContext.SharedVocabulary);
            string fewShotPrefix = runContext.TaskType == CouncilTaskType.Coding
                ? (runContext.IsCalculationTask ? GetCalculationFewShotExample(runContext.UserPrompt) : GetBuilderFewShotExample())
                : "";
            string previousSegmentOutput = "";

            for (int i = 0; i < segments.Count; i++)
            {
                LogActivity($"Builder segment {i + 1}/{segments.Count}: steps {string.Join(", ", segments[i].Select(s => s.TrimStart().Split(['.', ')'])[0]))}");
                RelayStatusBlock.Text = $"Relay: Builder segment {i + 1}/{segments.Count}...";

                var segmentPayload = new StringBuilder();
                segmentPayload.AppendLine(BuildCouncilGoalContractBlock(runContext.GoalContract));
                segmentPayload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, runContext.IsCloudExecution));
                segmentPayload.AppendLine(pipelineStateHeader);
                segmentPayload.AppendLine(sharedVocabularySection);
                if (!string.IsNullOrWhiteSpace(priorKnowledgeBlock))
                    segmentPayload.AppendLine(priorKnowledgeBlock);
                AppendCouncilWebContext(segmentPayload, runContext);
                if (fewShotPrefix.Length > 0 && i == 0)
                    segmentPayload.AppendLine(fewShotPrefix);

                segmentPayload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, runContext.TaskType, ""));
                segmentPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
                segmentPayload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", runContext.ArchitectOutput));

                var taskBlock = new StringBuilder();
                foreach (var step in segments[i])
                    taskBlock.AppendLine(step);
                segmentPayload.AppendLine(BuildLabeledBlock("SEGMENT TASK", taskBlock.ToString()));

                if (runContext.CompletedBuilderSteps.Count > 0)
                {
                    var completed = new StringBuilder();
                    foreach (var done in runContext.CompletedBuilderSteps)
                        completed.AppendLine($"- {done}");
                    segmentPayload.AppendLine(BuildLabeledBlock("COMPLETED STEPS", completed.ToString()));
                }

                if (assembledCode.Length > 0)
                {
                    segmentPayload.AppendLine(BuildLabeledBlock("CODE ALREADY WRITTEN", assembledCode.ToString()));
                }

                if (hasKnowledge && i == 0)
                {
                    segmentPayload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
                }

                string segmentSystem = builderSystem +
                    "\n[SEGMENTED MODE] Write ONLY the code for the steps listed in YOUR TASK. " +
                    "Do not rewrite code from prior segments. Output only the new code for these steps. " +
                    $"End your output with this exact comment on its own line: {SegmentCompletionMarker}";

                var segResult = await ExecuteCouncilRoleAsync(CouncilRole.Builder, segmentSystem, segmentPayload.ToString(), token, null, baseStateVault, true);
                string segCode = StripMarkdownFences(segResult.Answer);

                bool segmentComplete = segCode.Contains(SegmentCompletionMarker);
                segCode = segCode.Replace(SegmentCompletionMarker, "").TrimEnd();

                if (!segmentComplete)
                {
                    runContext.BuilderTruncationRecovery = true;
                    LogActivity($"Segment {i + 1} missing completion marker — may be truncated. Re-running...");
                    var retryResult = await ExecuteCouncilRoleAsync(CouncilRole.Builder, segmentSystem, segmentPayload.ToString(), token, null, baseStateVault, true);
                    string retryCode = StripMarkdownFences(retryResult.Answer).Replace(SegmentCompletionMarker, "").TrimEnd();
                    if (retryCode.Length > segCode.Length)
                        segCode = retryCode;
                }

                if (IsRepetitionLoop(segCode, previousSegmentOutput))
                {
                    string loopBreakPayload = segmentPayload.ToString() + "\nLOOP BREAK: your previous response repeated prior output. Generate different code for only the requested steps.";
                    var loopRetry = await ExecuteCouncilRoleAsync(CouncilRole.Builder, segmentSystem, loopBreakPayload, token, null, baseStateVault, true);
                    string loopRetryCode = StripMarkdownFences(loopRetry.Answer).Replace(SegmentCompletionMarker, "").TrimEnd();
                    if (!string.IsNullOrWhiteSpace(loopRetryCode))
                        segCode = loopRetryCode;
                }

                if (assembledCode.Length > 0)
                    assembledCode.AppendLine();
                assembledCode.AppendLine(segCode);
                previousSegmentOutput = segCode;

                string doneSteps = string.Join(", ", segments[i].Select(s => s.TrimStart().Split(['.', ')'])[0]));
                if (!string.IsNullOrWhiteSpace(doneSteps))
                    runContext.CompletedBuilderSteps.Add(doneSteps);
            }

            return assembledCode.ToString() + "\n" + BuilderCompletionMarker;
        }

        // Analyzes an attached document too large for a single full-context pass. Instead of one model
        // pass PER chunk (a map-reduce — N slow generations that dominated the ~10-minute Builder time),
        // this is retrieval-augmented generation: it selects the chunks that best fit ONE pass (the
        // request-relevant ones for a question; an even spread for a summary) and answers once. One pass
        // is faster on EVERY machine — GPU or CPU, large or small VRAM — and is the standard way to
        // ground an answer in a document that doesn't fit the context window. The context is right-sized
        // to the selected content, which also leaves more VRAM for model layers on memory-limited cards,
        // with no GPU-architecture-specific branching.
        private async Task<string> ExecuteDocumentRetrievalBuilderAsync(
            CouncilRunContext runContext, string builderSystem, string priorKnowledgeBlock, CancellationToken token, CouncilBaseStateVault? baseStateVault)
        {
            int builderContext = runContext.IsCloudExecution
                ? GetCloudCouncilInputBudgetTokens()
                : (int)GetRoleContextSize(CouncilRole.Builder);

            // Budget the window by its REAL fixed costs first, then give what's left to source content.
            // The Builder system prompt (environment briefing + output contract + role boosts) is roughly
            // 1.5-2.5k tokens — far more than the ~900 the old "(ctx - 900) * 0.62" reserve assumed.
            // Under-counting it let content + system overflow the window, so the budget guard in
            // ExecuteCouncilRoleAsync truncated the payload tail and dropped [[ORIGINAL REQUEST]] / the
            // plan; the model then "summarized" with no idea what was asked. Measuring the overhead keeps
            // both the document AND the request inside the window.
            int builderSystemTokens = EstimateTokenCount(builderSystem);
            int builderFramingTokens = EstimateTokenCount(runContext.UserPrompt)
                + EstimateTokenCount(runContext.ArchitectOutput)
                + 700;  // analysis instruction + block labels + chat-template overhead
            const int builderGenerationReserve = 900;
            int builderFixedOverhead = builderSystemTokens + builderFramingTokens + builderGenerationReserve;
            int contentTokenBudget = Math.Max(512, builderContext - builderFixedOverhead);
            int chunkTokens = Math.Clamp(contentTokenBudget / 4, 400, 1200);
            var chunks = SplitDocumentForSegmentedProcessing(runContext.DocumentContent, chunkTokens);
            var selected = SelectChunksForSinglePass(chunks, runContext.UserPrompt, contentTokenBudget);

            bool coveredWhole = selected.Count >= chunks.Count;
            int selectedTokens = selected.Sum(c => c.TokenEstimate);
            if (coveredWhole)
            {
                LogActivity($"Document retrieval builder: whole document fits one pass ({chunks.Count} chunk(s), ~{selectedTokens}t).");
            }
            else
            {
                LogActivity($"Document retrieval builder: selected {selected.Count}/{chunks.Count} chunks (~{selectedTokens}t) for one focused pass.");
                AppendChat("system", $"The document is long; this answer focuses on the {selected.Count} most relevant of {chunks.Count} sections. Ask about a specific section to cover the rest.");
            }
            RelayStatusBlock.Text = "Relay: Builder is analyzing the document...";

            var combined = new StringBuilder();
            foreach (var chunk in selected)
            {
                if (combined.Length > 0)
                    combined.AppendLine().AppendLine();
                combined.Append(chunk.Section);
            }

            var payload = new StringBuilder();
            payload.AppendLine(BuildCouncilGoalContractBlock(runContext.GoalContract));
            payload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, runContext.IsCloudExecution));
            payload.AppendLine(BuildPipelineStateHeader(BuildArchitectSummaryFromPlan(runContext.ArchitectOutput), ""));
            payload.AppendLine(BuildSharedVocabularySection(runContext.SharedVocabulary));
            if (!string.IsNullOrWhiteSpace(priorKnowledgeBlock))
                payload.AppendLine(priorKnowledgeBlock);
            AppendCouncilWebContext(payload, runContext);
            payload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, runContext.TaskType, ""));
            payload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
            payload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", runContext.ArchitectOutput));
            payload.AppendLine(BuildLabeledBlock(coveredWhole ? "DOCUMENT CONTENT" : "MOST RELEVANT DOCUMENT SECTIONS", combined.ToString()));
            // Comprehension + STRICT grounding: a weak local model otherwise paraphrases loosely and
            // fills gaps with plausible-but-fabricated content. Tell it to read and understand, answer in
            // its own words, but treat the text above as the ONLY source of truth (DocumentGroundingInstruction)
            // and, on partial coverage, scope the answer honestly instead of inventing the missing sections.
            string analysisInstruction =
                "Read and UNDERSTAND the document text above, then answer [[ORIGINAL REQUEST]] in your own words. " +
                DocumentGroundingInstruction;
            if (!coveredWhole)
                analysisInstruction += " These are the sections most relevant to the request, not the whole document; " +
                    "answer only from what is shown here, and do not invent the contents of sections that are not included.";
            payload.AppendLine(analysisInstruction);
            payload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: _isWebSearchEnabled && !_isCloudModeEnabled));

            // Right-size the context to this pass's real need (selected content + the measured fixed
            // overhead computed above), never above the role default. A smaller window costs nothing here
            // yet frees the VRAM a full window's KV cache would reserve, so more of the model stays on the
            // GPU on memory-limited machines. Because the overhead now includes the real system prompt and
            // request, this no longer under-sizes the window and forces a tail truncation that would drop
            // the request. Purely content-driven — no GPU model/architecture checks.
            int neededContext = Math.Clamp(selectedTokens + builderFixedOverhead, (int)MinRoleContext, builderContext);
            int? contextOverride = neededContext < builderContext ? neededContext : (int?)null;

            var result = await ExecuteCouncilRoleAsync(
                CouncilRole.Builder, builderSystem, payload.ToString(), token, 0.25f, baseStateVault, true,
                contextSizeOverride: contextOverride);
            return result.Answer + "\n" + BuilderCompletionMarker;
        }

        private async Task<string?> TryRegenerateWeakDocumentOutputAsync(
            CouncilRunContext runContext,
            string builderSystem,
            string pipelineStateHeader,
            string sharedVocabularySection,
            string priorKnowledgeBlock,
            string previousOutput,
            CancellationToken token,
            CouncilBaseStateVault? baseStateVault)
        {
            if (string.IsNullOrWhiteSpace(runContext.DocumentContent))
                return null;

            int builderContext = runContext.IsCloudExecution
                ? GetCloudCouncilInputBudgetTokens()
                : (int)GetRoleContextSize(CouncilRole.Builder);
            int builderSystemTokens = EstimateTokenCount(builderSystem);
            int fixedOverhead = builderSystemTokens
                + EstimateTokenCount(runContext.UserPrompt)
                + EstimateTokenCount(runContext.ArchitectOutput)
                + EstimateTokenCount(previousOutput)
                + 900;
            int contentTokenBudget = Math.Max(512, builderContext - fixedOverhead);
            int chunkTokens = Math.Clamp(contentTokenBudget / 4, 400, 1000);
            var chunks = SplitDocumentForSegmentedProcessing(runContext.DocumentContent, chunkTokens);
            var selected = SelectChunksForSinglePass(chunks, runContext.UserPrompt, contentTokenBudget);
            if (selected.Count == 0)
                return null;

            var source = new StringBuilder();
            foreach (var chunk in selected)
            {
                if (source.Length > 0)
                    source.AppendLine().AppendLine();
                source.Append(chunk.Section);
            }

            var payload = new StringBuilder();
            payload.AppendLine(BuildCouncilGoalContractBlock(runContext.GoalContract));
            payload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, runContext.IsCloudExecution));
            payload.AppendLine(pipelineStateHeader);
            payload.AppendLine(sharedVocabularySection);
            if (!string.IsNullOrWhiteSpace(priorKnowledgeBlock))
                payload.AppendLine(priorKnowledgeBlock);
            AppendCouncilWebContext(payload, runContext);
            payload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, runContext.TaskType, ""));
            payload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
            payload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", runContext.ArchitectOutput));
            payload.AppendLine(BuildLabeledBlock("PREVIOUS WEAK OUTPUT", previousOutput));
            payload.AppendLine(BuildLabeledBlock(
                selected.Count >= chunks.Count ? "DOCUMENT CONTENT" : "DOCUMENT SECTIONS FOR REGENERATION",
                source.ToString()));
            payload.AppendLine("Regenerate the final answer from the document text above. The previous output was too thin, fragmentary, or copied too much source text. Write a coherent answer in your own words, cover the user's request directly, and do not paste disconnected source sentences.");
            payload.AppendLine(DocumentGroundingInstruction);
            payload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: _isWebSearchEnabled && !_isCloudModeEnabled));

            int selectedTokens = selected.Sum(c => c.TokenEstimate);
            int neededContext = Math.Clamp(selectedTokens + fixedOverhead, (int)MinRoleContext, builderContext);
            int? contextOverride = neededContext < builderContext ? neededContext : (int?)null;

            var retry = await ExecuteCouncilRoleAsync(
                CouncilRole.Builder,
                builderSystem,
                payload.ToString(),
                token,
                0.2f,
                baseStateVault,
                true,
                maxGenerationTokensOverride: 2048,
                contextSizeOverride: contextOverride);

            if (!TryNormalizeBuilderOutput(retry.Answer, runContext.TaskType, out string cleaned, out _))
                return null;

            cleaned = PostProcessBuilderOutput(cleaned, runContext);
            if (IsLowValueDocumentOutput(cleaned, runContext.DocumentFileNames)
                || IsUngroundedDocumentOutput(cleaned, runContext.DocumentContent))
            {
                return null;
            }

            return cleaned;
        }

        // Selects the chunks for a SINGLE document pass, packed to `tokenBudget`. For a request with
        // content keywords it keeps the most relevant chunks; for a generic request (e.g. "summarize")
        // where nothing scores, it keeps an even spread across the document for breadth. The document
        // opening is always included, and the result is returned in document order so the pass reads
        // coherently. Any chunk dropped is content this one pass won't see — the deliberate cost of a
        // single fast pass over a document larger than the context window.
        private static List<(string Section, int TokenEstimate)> SelectChunksForSinglePass(
            List<(string Section, int TokenEstimate)> chunks, string query, int tokenBudget)
        {
            if (chunks.Count == 0)
                return chunks;
            if (chunks.Sum(c => c.TokenEstimate) <= tokenBudget)
                return chunks; // whole document fits — use all of it, in order.

            var keywords = ExtractQueryKeywords(query);
            double maxScore = 0;
            var scored = new List<(int Index, double Score)>(chunks.Count);
            for (int i = 0; i < chunks.Count; i++)
            {
                string lower = chunks[i].Section.ToLowerInvariant();
                double hits = keywords.Sum(k => CountOccurrences(lower, k));
                double density = hits / Math.Max(1.0, chunks[i].Section.Length / 1000.0);
                scored.Add((i, density));
                maxScore = Math.Max(maxScore, density);
            }

            // Relevance order when the request names something narrow in the document; otherwise an
            // even spread so broad summaries sample the whole document instead of just dense keyword
            // pockets.
            bool broadCoverageRequest = IsBroadDocumentCoverageRequest(query);
            List<int> order = maxScore > 0 && !broadCoverageRequest
                ? scored.OrderByDescending(s => s.Score).Select(s => s.Index).ToList()
                : EvenlySpreadIndices(chunks.Count);

            var picked = new HashSet<int> { 0 };               // always keep the document opening
            int used = chunks[0].TokenEstimate;
            foreach (int idx in order)
            {
                if (picked.Contains(idx))
                    continue;
                if (used + chunks[idx].TokenEstimate > tokenBudget)
                    continue;
                picked.Add(idx);
                used += chunks[idx].TokenEstimate;
            }

            return Enumerable.Range(0, chunks.Count).Where(picked.Contains).Select(i => chunks[i]).ToList();
        }

        // Orders indices so a budget-limited prefix samples ACROSS the document (0, then evenly stepped
        // sections, then the remainder) rather than only its head.
        private static List<int> EvenlySpreadIndices(int count)
        {
            var ordered = new List<int>();
            var seen = new HashSet<int>();
            int step = Math.Max(1, count / 8);
            for (int i = 0; i < count; i += step)
                if (seen.Add(i)) ordered.Add(i);
            for (int i = 0; i < count; i++)
                if (seen.Add(i)) ordered.Add(i);
            return ordered;
        }

        private static readonly HashSet<string> ExcerptRankStopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the","a","an","and","or","but","of","to","in","on","for","with","that","this","these","those",
            "is","are","was","were","be","been","it","its","as","at","by","from","into","about","what","which",
            "who","whom","how","why","when","where","please","can","could","would","should","does","summarize",
            "summary","explain","describe","give","tell","write","make","provide","document","file","text"
        };

        private static bool IsBroadDocumentCoverageRequest(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return true;

            string lower = query.ToLowerInvariant();
            string[] broadSignals =
            [
                "summarize", "summarise", "summary", "overview", "key points",
                "main points", "highlights", "review this", "review the document",
                "explain this", "explain the document", "break down"
            ];

            if (!broadSignals.Any(signal => lower.Contains(signal, StringComparison.Ordinal)))
                return false;

            string[] narrowSignals =
            [
                "specific", "section", "chapter", "page ", "figure", "table",
                "quote", "extract", "compare", "contrast", "find ", "where ",
                "when ", "who ", "how many", "what does", "what do they say about"
            ];

            return !narrowSignals.Any(signal => lower.Contains(signal, StringComparison.Ordinal));
        }

        private static List<string> ExtractQueryKeywords(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new List<string>();

            return query.ToLowerInvariant()
                .Split(new[] { ' ', '\t', '\n', '\r', ',', '.', ';', ':', '!', '?', '"', '\'', '(', ')', '[', ']', '/', '\\', '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w.Length >= 4 && !ExcerptRankStopWords.Contains(w))
                .Distinct()
                .Take(24)
                .ToList();
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            if (string.IsNullOrEmpty(needle))
                return 0;
            int count = 0, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                count++;
                idx += needle.Length;
            }
            return count;
        }

        // ═══════════════════════════════════════════════
        // Builder Post-Processing
        // ═══════════════════════════════════════════════

        private static string PostProcessBuilderOutput(string raw, CouncilRunContext context)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return raw;

            // Strip echoed pipeline metadata FIRST — models often regurgitate [[LABEL]] blocks,
            // pipeline state headers, role reminders, and other structural payload content.
            string output = TrimRepeatedRestartTail(StripPipelineMetadata(raw));

            if (context.IsWorkspaceTask
                && (output.Contains("[[AXIOM_CODEBASE_PATCH]]", StringComparison.OrdinalIgnoreCase)
                    || output.Contains("[[CODEBASE PATCH FORMAT ERROR]]", StringComparison.OrdinalIgnoreCase)))
            {
                output = output.Replace(BuilderCompletionMarker, "", StringComparison.OrdinalIgnoreCase).Trim();
                context.BuilderOutputTruncated = DetectTruncation(output);
                return output;
            }

            // For coding tasks AND artifact-canvas requests, extract code from fenced blocks (before
            // markers get stripped). StripChatFromCode properly extracts content between ``` fences and
            // strips chat preamble. StripMarkdownFences only removes ``` lines but keeps all surrounding
            // prose — causing "mixed" output when models include explanations alongside code. Artifact
            // requests are code-fenced HTML/SVG deliverables, so they must take the code path; the prose
            // sanitizer below would strip lines (import/from/etc.) and corrupt the artifact.
            if (context.TaskType == CouncilTaskType.Coding
                || context.IsArtifactCanvasRequest
                || DetectCodeOutput(output).IsCode)
            {
                output = StripChatFromCode(output);
            }
            else
            {
                output = StripMarkdownFences(output);
                output = SanitizeNonCodingFormat(output);
            }

            output = NormalizeIndentation(output);
            output = TrimRepeatedRestartTail(output);
            context.BuilderOutputTruncated = DetectTruncation(output);
            return output;
        }

        private static string StripPipelineMetadata(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            // Remove [[LABEL]]...[[END LABEL]] echoed blocks that are pipeline context, not Builder content.
            // These are injected into the payload as structured context but models sometimes echo them verbatim.
            string[] pipelineBlockLabels =
            [
                "PRIOR KNOWLEDGE", "ARCHITECT PLAN", "APPROVED ARCHITECTURE",
                "BUILDER OUTPUT", "ORIGINAL REQUEST", "USER PROMPT",
                "OBJECTIVE", "PROJECT KNOWLEDGE BASE", "DOCUMENT CONTENT",
                "SHARED VOCABULARY", "PIPELINE STATE HEADER", "PIPELINE HEALTH",
                "PIPELINE FLAGS", "SANDBOX OUTPUT", "SANDBOX LOGS",
                "CALCULATOR TOOL RESULTS", "PRIOR SESSION CONTEXT",
                "REQUIREMENTS", "CONSTRAINTS", "PROBLEM STATEMENT",
                "CODE ALREADY WRITTEN", "SEGMENT TASK", "COMPLETED STEPS",
                "PRE-FLAGGED ISSUES", "FORMULA CHECKLIST", "CRITIC FINDINGS",
                "DOCUMENT REFERENCE", "DOCUMENT SEGMENT", "PRIOR OUTPUT",
                "STATE ROUTING BOUNDARY"
            ];

            string result = text;

            // Strip complete [[LABEL]]...[[END LABEL]] blocks
            foreach (string label in pipelineBlockLabels)
            {
                string openTag = $"[[{label}]]";
                string endTag = $"[[END {label}]]";
                int safety = 0;
                while (safety++ < 5)
                {
                    int openIdx = result.IndexOf(openTag, StringComparison.OrdinalIgnoreCase);
                    if (openIdx < 0) break;
                    int endIdx = result.IndexOf(endTag, openIdx, StringComparison.OrdinalIgnoreCase);
                    if (endIdx >= 0)
                    {
                        result = result[..openIdx] + result[(endIdx + endTag.Length)..];
                    }
                    else
                    {
                        // No closing tag — strip from open tag to end of line (model echoed the header only)
                        int lineEnd = result.IndexOf('\n', openIdx);
                        result = lineEnd >= 0 ? result[..openIdx] + result[(lineEnd + 1)..] : result[..openIdx];
                    }
                }

                // Also strip standalone open/end tags that may appear on their own lines
                result = result.Replace(openTag, "", StringComparison.OrdinalIgnoreCase);
                result = result.Replace(endTag, "", StringComparison.OrdinalIgnoreCase);
            }

            // Strip echoed pipeline structural lines
            var lines = result.Split('\n');
            var cleaned = new List<string>();
            foreach (string line in lines)
            {
                string trimmed = line.Trim();

                // Skip pipeline header separator lines
                if (trimmed.Length >= 6 && trimmed.All(c => c == '═' || c == '=' || c == '─'))
                    continue;

                // Skip echoed role/state markers
                if (trimmed.StartsWith("[STATE ROUTING BOUNDARY]", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("[ROLE REMINDER]", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Active Role:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("PIPELINE STATE HEADER", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("PIPELINE HEALTH", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Architect Summary:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Builder Summary:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Architect role correction required:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Builder role correction required:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Builder truncation recovery re-run:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Static validation structural issues:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Sandbox exceptions detected:", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("Do not consume or infer hidden state", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (trimmed.StartsWith("No prior pipeline state", StringComparison.OrdinalIgnoreCase))
                    continue;

                cleaned.Add(line);
            }

            // Trim leading/trailing blank lines that result from stripped blocks
            string final = string.Join('\n', cleaned).Trim();

            // Remove consecutive blank lines (3+ newlines → 2)
            while (final.Contains("\n\n\n"))
                final = final.Replace("\n\n\n", "\n\n");

            return final;
        }

        private static string StripMarkdownFences(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return text;

            var lines = text.Split('\n').ToList();
            var result = new List<string>();
            foreach (var line in lines)
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("```"))
                    continue;
                result.Add(line);
            }
            return string.Join('\n', result).Trim();
        }

        /// <summary>
        /// Cleans obvious code artifacts from non-coding Builder output.
        /// Light-touch: removes boilerplate lines and extracts prose from accidental code blocks,
        /// but preserves mathematical expressions and structured content.
        /// </summary>
        private static string SanitizeNonCodingFormat(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return output;

            var lines = output.Split('\n');
            var cleaned = new List<string>();

            foreach (string line in lines)
            {
                string trimmed = line.TrimStart();

                // Strip pure programming boilerplate that should never appear in prose
                if (trimmed.StartsWith("if __name__", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("#!/", StringComparison.Ordinal)) continue;
                if (trimmed == "pass") continue;
                if (trimmed.StartsWith("import ", StringComparison.Ordinal) && trimmed.Contains('.') && !trimmed.Contains(" is ", StringComparison.OrdinalIgnoreCase)) continue;
                if (trimmed.StartsWith("from ", StringComparison.Ordinal) && trimmed.Contains(" import ")) continue;
                if (trimmed.StartsWith("Console.WriteLine(", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith("System.out.println(", StringComparison.Ordinal)) continue;
                if (trimmed.StartsWith(">>> ", StringComparison.Ordinal)) continue;

                cleaned.Add(line);
            }

            string result = string.Join('\n', cleaned).Trim();

            // Collapse excessive blank lines left by stripped code
            while (result.Contains("\n\n\n"))
                result = result.Replace("\n\n\n", "\n\n");

            return result;
        }

        private static bool DetectTruncation(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string trimmed = output.TrimEnd();
            if (trimmed.Length == 0)
                return false;

            char last = trimmed[^1];
            if (last != '}' && last != ';' && last != ')' && last != '\n' && last != '"' && last != '\'')
            {
                int openBraces = trimmed.Count(c => c == '{');
                int closeBraces = trimmed.Count(c => c == '}');
                if (openBraces > closeBraces)
                    return true;

                int openParens = trimmed.Count(c => c == '(');
                int closeParens = trimmed.Count(c => c == ')');
                if (openParens > closeParens)
                    return true;
            }

            return false;
        }

        private static string NormalizeIndentation(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return code;

            bool hasTabs = code.Contains('\t');
            bool hasLeadingSpaces = code.Split('\n').Any(l => l.Length > 0 && l[0] == ' ');

            if (hasTabs && hasLeadingSpaces)
            {
                code = code.Replace("\t", "    ");
            }

            return code;
        }

        // ═══════════════════════════════════════════════
        // Token Budget Awareness
        // ═══════════════════════════════════════════════

        private static string GetTokenBudgetHint(CouncilRunContext context)
        {
            int steps = context.ArchitectStepCount;
            bool isComplex = !string.IsNullOrWhiteSpace(context.UserPrompt)
                && (context.UserPrompt.Contains("class", StringComparison.OrdinalIgnoreCase)
                    || context.UserPrompt.Contains("multiple", StringComparison.OrdinalIgnoreCase)
                    || context.UserPrompt.Contains("full", StringComparison.OrdinalIgnoreCase)
                    || steps > 5);

            if (isComplex)
            {
                return "\n[TOKEN BUDGET] The output may be long. Prioritize correctness and completeness over explanation. " +
                       "Do not add comments or docstrings unless asked. Write the shortest correct implementation of each step.";
            }
            return "";
        }

        // ═══════════════════════════════════════════════
        // Pre-Flight Decomposition (Improvement 2)
        // ═══════════════════════════════════════════════

        private static PreFlightDecomposition DecomposeUserPrompt(string userQuery, string objective, CouncilTaskType taskType)
        {
            var decomp = new PreFlightDecomposition();
            string combined = $"{userQuery} {objective}".Trim();

            // Preserve the complete intent instead of reducing a nuanced request to its first
            // sentence. The cap keeps the contract compact enough for 1B-7B local models.
            decomp.ProblemStatement = NormalizeContractText(combined, 700);

            string lower = combined.ToLowerInvariant();
            var requirementMarkers = new[]
            {
                "must ", "should ", "need to ", "needs to ", "require", "want ",
                "create ", "build ", "implement ", "write ", "generate ", "produce ",
                "make ", "add ", "remove ", "change ", "fix ", "improve ", "enhance ",
                "support ", "allow ", "ensure ", "include ", "use ", "display ", "render "
            };
            var constraintMarkers = new[]
            {
                "only ", "without ", " no ", "must not ", "don't ", "do not ",
                "using ", "in python", "in c#", "in java", "in javascript", "in html",
                "standard library", "no external", "offline", "self-contained", "single file",
                "at most", "at least", "under ", "over ", "exactly "
            };

            // Split on paragraphs, bullets, semicolons, and discourse cues that users commonly
            // use to add requirements. Avoid splitting on plain "and", which destroys meaning.
            string prepared = Regex.Replace(combined,
                @"\b(furthermore|moreover|additionally|lastly|finally|also|one last thing)\b\s*[:,]?",
                "\n", RegexOptions.IgnoreCase);
            prepared = Regex.Replace(prepared, @"(?m)^\s*(?:[-*\u2022]|\d{1,2}[.)])\s+", "");
            string[] units = Regex.Split(prepared, @"(?:\r?\n)+|;\s+|(?<=[.!?])\s+(?=[A-Z])");

            foreach (string unit in units)
            {
                string item = NormalizeContractText(unit, 320).Trim(' ', '.', ';');
                if (item.Length < 4)
                    continue;

                string itemLower = " " + item.ToLowerInvariant() + " ";
                bool isConstraint = constraintMarkers.Any(marker => itemLower.Contains(marker));
                bool isRequirement = requirementMarkers.Any(marker => itemLower.Contains(marker));

                if (isRequirement || isConstraint)
                    decomp.Requirements.Add(item);
                if (isConstraint)
                    decomp.Constraints.Add(item);
            }

            if (decomp.Requirements.Count == 0 && combined.Length > 0)
                decomp.Requirements.Add(NormalizeContractText(combined, 320));

            decomp.Requirements = decomp.Requirements
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();
            decomp.Constraints = decomp.Constraints
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            if (taskType == CouncilTaskType.Coding)
            {
                if (lower.Contains("python")) decomp.Constraints.Add("Language: Python");
                else if (lower.Contains("c#") || lower.Contains("csharp")) decomp.Constraints.Add("Language: C#");
                else if (lower.Contains("java") && !lower.Contains("javascript")) decomp.Constraints.Add("Language: Java");
                else if (lower.Contains("javascript") || lower.Contains("js ")) decomp.Constraints.Add("Language: JavaScript");
            }

            decomp.Constraints = decomp.Constraints
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();

            return decomp;
        }

        private string BuildArchitectInput(CouncilRunContext context, PreFlightDecomposition decomp, string knowledgePacket, bool hasKnowledge)
        {
            var payload = new StringBuilder();

            // Inject recent conversation context so the Architect understands multi-turn flow
            string recentContext = BuildRecentConversationContext(4);
            if (!string.IsNullOrWhiteSpace(recentContext))
                payload.AppendLine(recentContext);

            // For non-coding tasks with documents, present knowledge base FIRST
            // so the model sees actual document content before it starts planning
            bool documentGrounded = hasKnowledge && context.TaskType != CouncilTaskType.Coding;
            if (documentGrounded)
            {
                payload.AppendLine("══════════════════════════════════════");
                payload.AppendLine("SOURCE MATERIAL \u2014 ALREADY EXTRACTED FROM USER'S UPLOADED FILES");
                payload.AppendLine("Do NOT plan file-opening/reading/extraction steps. This text IS the file content.");
                payload.AppendLine("══════════════════════════════════════\n");
                payload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
            }

            payload.AppendLine(BuildLabeledBlock("PROBLEM STATEMENT", decomp.ProblemStatement));

            var requirements = new StringBuilder();
            for (int i = 0; i < decomp.Requirements.Count; i++)
                requirements.AppendLine($"{i + 1}. {decomp.Requirements[i]}");
            payload.AppendLine(BuildLabeledBlock("REQUIREMENTS", requirements.ToString()));

            if (decomp.Constraints.Count > 0)
            {
                var constraints = new StringBuilder();
                for (int i = 0; i < decomp.Constraints.Count; i++)
                    constraints.AppendLine($"{i + 1}. {decomp.Constraints[i]}");
                payload.AppendLine(BuildLabeledBlock("CONSTRAINTS", constraints.ToString()));
            }

            payload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", context.UserPrompt));

            if (_sessionMemory != null && !string.IsNullOrWhiteSpace(_sessionMemory.ArchitectPlan))
            {
                var prior = new StringBuilder();
                prior.AppendLine($"Previous task: {_sessionMemory.TaskDescription}");
                string planSummary = _sessionMemory.ArchitectPlan.Length > 500
                    ? _sessionMemory.ArchitectPlan[..500] + "..."
                    : _sessionMemory.ArchitectPlan;
                prior.AppendLine($"Previous plan summary:\n{planSummary}");
                if (!string.IsNullOrWhiteSpace(_sessionMemory.CriticSummary))
                {
                    string criticSummary = _sessionMemory.CriticSummary.Length > 300
                        ? _sessionMemory.CriticSummary[..300] + "..."
                        : _sessionMemory.CriticSummary;
                    prior.AppendLine($"Previous Critic findings:\n{criticSummary}");
                }

                payload.AppendLine(BuildLabeledBlock("PRIOR SESSION CONTEXT", prior.ToString()));
                payload.AppendLine("NOTE: The above is background reference only. Create a fresh plan for the CURRENT user request.");
            }

            // For coding tasks (or when no documents), add knowledge at the end
            if (hasKnowledge && !documentGrounded && !context.IsDocumentTask)
            {
                payload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
            }

            return payload.ToString();
        }

        // ═══════════════════════════════════════════════
        // Role Priming (Improvement 6)
        // ═══════════════════════════════════════════════

        private static string BuildRolePrimedPayload(CouncilRole role, CouncilTaskType taskType, string existingPayload)
        {
            string primer = (role, taskType) switch
            {
                (CouncilRole.Architect, CouncilTaskType.Coding) =>
                    "[ROLE REMINDER] Your only job in this response is to produce a numbered implementation plan following the required format. Locate [[ORIGINAL REQUEST]], [[REQUIREMENTS]], and [[CONSTRAINTS]] blocks and use them as specification. Do not write code or prose.\n\n",
                (CouncilRole.Architect, CouncilTaskType.Document) =>
                    "[ROLE REMINDER] Your only job is to produce a numbered content plan. The document text is in [[DOCUMENT CONTENT]]. " +
                    "Each step must describe a specific operation on the actual content — what to extract, summarize, or synthesize from which part. " +
                    "Do NOT plan file-reading, tool-usage, or any procedural steps. The text is already provided. Do not write code or prose.\n\n",
                (CouncilRole.Architect, _) =>
                    "[ROLE REMINDER] Your only job is to produce a numbered content plan. Each step names a section or subtopic to cover using the text already provided in [[PROJECT KNOWLEDGE BASE]]. Do NOT plan file-reading, file-opening, or text-extraction steps \u2014 the document content is already provided above. Do not write code or prose.\n\n",

                (CouncilRole.Builder, CouncilTaskType.Coding) =>
                    "[ROLE REMINDER — BUILDER] You are the BUILDER. Implement the [[APPROVED ARCHITECTURE]] as working code. " +
                    "WARNING: Do NOT produce a numbered plan, step list, or outline — the Architect already did that. " +
                    "Do NOT re-state or re-list the steps above. Write executable code directly, starting with the first function or class. " +
                    "If the request names C#, output one complete compilable .cs source file; if it asks for tests, include executable test code in the same deliverable. " +
                    "Do NOT switch to diagrams, SVG, architecture documents, or explanatory prose for implementation requests. " +
                    "Before writing each function, mentally trace its execution with sample inputs to verify correctness. " +
                    "Output code only — no explanations, no numbered lists, no plan summaries. " +
                    "Do NOT echo any input labels, headers, or [[LABEL]] blocks.\n\n",
                (CouncilRole.Builder, CouncilTaskType.Document) =>
                    "[ROLE REMINDER — BUILDER] You are the BUILDER. Execute the [[APPROVED ARCHITECTURE]] against the document text. " +
                    "WARNING: Do NOT produce a numbered plan or re-list what you are about to do. Write your output directly. " +
                    "For each plan step: locate the relevant section in [[DOCUMENT CONTENT]], extract the key information, " +
                    "verify it against the original text, then write your output grounded in that evidence. " +
                    "The document IS provided. Do NOT claim it is missing. Do NOT write code. " +
                    "Do NOT echo any input labels, headers, or [[LABEL]] blocks — start directly with your content.\n\n",
                (CouncilRole.Builder, CouncilTaskType.Research) =>
                    "[ROLE REMINDER — BUILDER] You are the BUILDER. Write a thorough research document implementing every step of the [[APPROVED ARCHITECTURE]]. " +
                    "WARNING: Do NOT produce a numbered plan or step list — write research prose directly. " +
                    "For each subtopic: identify the key claim, find supporting evidence in the provided context, synthesize it into clear prose. " +
                    "Use the provided source content. Do NOT write code. " +
                    "Do NOT echo any input labels, headers, or [[LABEL]] blocks — start directly with your research content.\n\n",
                (CouncilRole.Builder, CouncilTaskType.Analysis) =>
                    "[ROLE REMINDER — BUILDER] You are the BUILDER. Write a rigorous analytical document implementing every step of the [[APPROVED ARCHITECTURE]]. " +
                    "WARNING: Do NOT produce a numbered plan or step list — write analytical prose directly. " +
                    "For each dimension: state the criterion, present the evidence, reason through the implications, state your conclusion. " +
                    "Use the provided source context. Do NOT write code. " +
                    "Do NOT echo any input labels, headers, or [[LABEL]] blocks — start directly with your analysis content.\n\n",
                (CouncilRole.Builder, _) =>
                    "[ROLE REMINDER — BUILDER] You are the BUILDER. Produce a thorough response implementing every step of the [[APPROVED ARCHITECTURE]]. " +
                    "WARNING: Do NOT re-list or re-plan the steps above. Write your output directly. " +
                    "For each step: reason through the problem, verify your logic, then write the output. " +
                    "Do NOT echo any input labels, headers, or [[LABEL]] blocks — start directly with your content.\n\n",

                (CouncilRole.Critic, CouncilTaskType.Coding) =>
                    "[ROLE REMINDER] Your only job in this response is to review [[BUILDER OUTPUT]] against [[ARCHITECT PLAN]] and [[ORIGINAL REQUEST]], then output findings.\n\n",
                (CouncilRole.Critic, CouncilTaskType.Document) =>
                    "[ROLE REMINDER] Your only job is to verify [[BUILDER OUTPUT]] against [[DOCUMENT CONTENT]], [[ARCHITECT PLAN]], and [[ORIGINAL REQUEST]]. " +
                    "Priority: (1) no fabricated facts absent from document, (2) user request fulfilled, (3) plan steps addressed, (4) no significant omissions.\n\n",
                (CouncilRole.Critic, _) =>
                    "[ROLE REMINDER] Your only job in this response is to review [[BUILDER OUTPUT]] against [[ARCHITECT PLAN]] and [[ORIGINAL REQUEST]], then output findings.\n\n",

                _ => ""
            };

            string builderWebGrounding = role == CouncilRole.Builder
                ? "[BUILDER WEB GROUNDING] If [[WEB SEARCH DATA]] is present anywhere in this payload, treat it as authoritative for current, online, source-backed, or recently changed claims that it actually covers. " +
                  "Do not use memory, guesses, or background knowledge to add unsupported current/source-backed facts. Do not use off-topic web results as support for the user's named entities. " +
                  "Stable non-current background context may come from the prompt, council plan, project knowledge, or general knowledge when not contradicted by web evidence. " +
                  "For prose/research/analysis outputs, cite source titles or hosts naturally for current/source-backed claims. " +
                  "For code/artifact outputs, use the web evidence only to choose correct APIs, names, values, or constraints; do not fabricate unsupported details. " +
                  "If evidence is partial or mismatched, run a narrower web lookup when tools are available before declaring the answer unsupported; if the lookup still does not confirm a required current/source-backed fact, say it is not confirmed rather than inventing it.\n\n"
                : string.Empty;

            return primer + builderWebGrounding + existingPayload;
        }

        private static string BuildArchitectHandoffPrimedPayload(CouncilRunContext context, string existingPayload)
        {
            string mode = context.IsWorkspaceTask
                ? "connected-codebase patch handoff"
                : "Project Canvas artifact handoff";
            string outputRule = context.IsWorkspaceTask
                ? "The Builder must return a valid [[AXIOM_CODEBASE_PATCH]] envelope for connected workspace files."
                : "The Builder must return one complete self-contained renderable artifact source.";

            return "[ROLE REMINDER] Your only job in this response is to produce the required ARCHITECT_HANDOFF block for a " +
                   mode + ". Do not produce a numbered plan, prose explanation, code, markdown fence, or implementation. " +
                   "Use [[ORIGINAL REQUEST]], [[REQUIREMENTS]], [[CONSTRAINTS]], and [[TASK CONTRACT - SOURCE OF TRUTH]] as the source of truth. " +
                   outputRule + " End your visible response with the required Architect completion marker.\n\n" +
                   existingPayload;
        }

        private static string BuildArchitectHandoffClosingAnchor()
        {
            return "\n\n----------\nNow respond AS THE ARCHITECT. Output ONLY the ARCHITECT_HANDOFF block for the request above, followed by ARCHITECT PLAN COMPLETE on its own line. " +
                   "Do NOT describe your role, your environment, or these instructions. Begin directly with ARCHITECT_HANDOFF.";
        }

        // Closing imperative appended as the very LAST thing the model reads before it starts
        // generating. A model continues from its most recent context; if that context ends with a
        // crisp "produce your output now" command, the next tokens are the deliverable. Without it,
        // when a weak local model is unsure what to do (long system prompt, buried request, attached
        // document) the most salient text to "continue" is the role/environment self-description — the
        // exact echo symptom. This anchor is role-specific and reinforces the top-of-payload primer.
        private static string BuildCouncilClosingAnchor(CouncilRole role, bool allowLocalWebPause = false)
        {
            return role switch
            {
                CouncilRole.Architect =>
                    "\n\n──────────\nNow respond AS THE ARCHITECT. Output ONLY your numbered plan for the request above. " +
                    "Do NOT describe your role, your environment, or these instructions. Begin directly with \"1.\".",
                CouncilRole.Builder =>
                    "\n\n──────────\nNow respond AS THE BUILDER. Output ONLY your implementation for the request above. " +
                    (allowLocalWebPause
                        ? "If a required current, online, source-backed, or recently changed fact is missing from the payload, first output exactly one internal Agentic Pause command on its own line: [PAUSE: WEB_SEARCH | focused query for the missing fact]. After the result returns, continue with the deliverable grounded in that result. "
                        : "") +
                    "Do NOT describe your role, your environment, or these instructions, and do NOT restate the plan. " +
                    (allowLocalWebPause
                        ? "Begin directly with the deliverable after any required internal pause completes."
                        : "Begin directly with the deliverable."),
                CouncilRole.Critic =>
                    "\n\n──────────\nNow respond AS THE CRITIC. Output ONLY your review findings for the request above. " +
                    "Do NOT describe your role, your environment, or these instructions.",
                _ => string.Empty
            };
        }

        // ═══════════════════════════════════════════════
        // Few-Shot Priming for Builder (Improvement 7)
        // ═══════════════════════════════════════════════

        private static string GetBuilderFewShotExample()
        {
            return
                "══════════════════════════════════════\n" +
                "EXAMPLE OF EXPECTED FORMAT (not your actual task)\n" +
                "══════════════════════════════════════\n" +
                "Specification:\n" +
                "  1. Define function celsius_to_fahrenheit(celsius: float) -> float that multiplies by 9/5 and adds 32.\n" +
                "  2. Define function main() that reads a float from stdin and prints the converted value.\n" +
                "\n" +
                "Expected output:\n" +
                "def celsius_to_fahrenheit(celsius: float) -> float:\n" +
                "    return celsius * 9.0 / 5.0 + 32.0\n" +
                "\n" +
                "def main():\n" +
                "    value = float(input())\n" +
                "    print(celsius_to_fahrenheit(value))\n" +
                "\n" +
                "if __name__ == \"__main__\":\n" +
                "    main()\n" +
                "══════════════════════════════════════\n" +
                "END OF EXAMPLE — your actual task follows below.\n" +
                "══════════════════════════════════════\n\n";
        }

        // ═══════════════════════════════════════════════
        // Dynamic Few-Shot Examples for Calculation Tasks
        // ═══════════════════════════════════════════════

        private static string GetCalculationFewShotExample(string userPrompt)
        {
            string lower = userPrompt.ToLowerInvariant();

            string[] unitKeywords = ["unit", "convert", "conversion", "celsius", "fahrenheit", "meters", "feet", "liters", "gallons", "kilograms", "pounds", "miles", "kilometers"];
            string[] validationKeywords = ["valid", "negative", "reject", "check input", "constraint", "impossible", "must be positive", "cannot be", "greater than zero"];

            if (unitKeywords.Any(k => lower.Contains(k)))
                return GetCalcFewShot_UnitConversion();

            if (validationKeywords.Any(k => lower.Contains(k)))
                return GetCalcFewShot_InputValidation();

            return GetCalcFewShot_MultiStep();
        }

        private static string GetCalcFewShot_UnitConversion()
        {
            return
                "══════════════════════════════════════\n" +
                "EXAMPLE OF EXPECTED FORMAT (not your actual task)\n" +
                "══════════════════════════════════════\n" +
                "Specification:\n" +
                "  1. Define function mm_to_liters(rainfall_mm: float, area_m2: float) -> float.\n" +
                "     Formula: liters = (rainfall_mm / 1000) * area_m2 * 1000. Expected range: 100-10000 liters.\n" +
                "\n" +
                "Expected output:\n" +
                "def mm_to_liters(rainfall_mm: float, area_m2: float) -> float:\n" +
                "    rainfall_m = rainfall_mm / 1000.0  # mm -> m: divide by 1000\n" +
                "    volume_m3 = rainfall_m * area_m2\n" +
                "    volume_liters = volume_m3 * 1000.0  # m³ -> liters: multiply by 1000\n" +
                "    if volume_liters < 100 or volume_liters > 10000:\n" +
                "        print(f\"WARNING: result {volume_liters} is outside expected range 100-10000 liters, check inputs or formula\")\n" +
                "    return volume_liters\n" +
                "══════════════════════════════════════\n" +
                "END OF EXAMPLE — your actual task follows below.\n" +
                "══════════════════════════════════════\n\n";
        }

        private static string GetCalcFewShot_InputValidation()
        {
            return
                "══════════════════════════════════════\n" +
                "EXAMPLE OF EXPECTED FORMAT (not your actual task)\n" +
                "══════════════════════════════════════\n" +
                "Specification:\n" +
                "  1. Define function calculate_bmi(weight_kg: float, height_m: float) -> float.\n" +
                "     Reject negative or zero weight/height.\n" +
                "\n" +
                "Expected output:\n" +
                "def calculate_bmi(weight_kg: float, height_m: float) -> float:\n" +
                "    if weight_kg <= 0:\n" +
                "        raise ValueError(f\"Weight must be positive, got {weight_kg}\")\n" +
                "    if height_m <= 0:\n" +
                "        raise ValueError(f\"Height must be positive, got {height_m}\")\n" +
                "    bmi = weight_kg / (height_m ** 2)\n" +
                "    if bmi < 10 or bmi > 60:\n" +
                "        print(f\"WARNING: BMI {bmi:.1f} is outside typical range 10-60, check inputs\")\n" +
                "    return bmi\n" +
                "══════════════════════════════════════\n" +
                "END OF EXAMPLE — your actual task follows below.\n" +
                "══════════════════════════════════════\n\n";
        }

        private static string GetCalcFewShot_MultiStep()
        {
            return
                "══════════════════════════════════════\n" +
                "EXAMPLE OF EXPECTED FORMAT (not your actual task)\n" +
                "══════════════════════════════════════\n" +
                "Specification:\n" +
                "  1. Define function energy_cost(power_w, hours, rate_per_kwh) -> float.\n" +
                "     Formula: cost = (power_w / 1000) * hours * rate_per_kwh. Expected range: $0.01-$100.\n" +
                "\n" +
                "Expected output:\n" +
                "def energy_cost(power_w: float, hours: float, rate_per_kwh: float) -> float:\n" +
                "    power_kw = power_w / 1000.0  # W -> kW: divide by 1000\n" +
                "    energy_kwh = power_kw * hours\n" +
                "    cost = energy_kwh * rate_per_kwh\n" +
                "    if cost < 0.01 or cost > 100:\n" +
                "        print(f\"WARNING: cost ${cost:.2f} is outside expected range $0.01-$100, check inputs or formula\")\n" +
                "    return cost\n" +
                "══════════════════════════════════════\n" +
                "END OF EXAMPLE — your actual task follows below.\n" +
                "══════════════════════════════════════\n\n";
        }

        // ═══════════════════════════════════════════════
        // Sandbox Error Detection
        // ═══════════════════════════════════════════════

        private static List<string> DetectSandboxErrors(string sandboxOutput)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(sandboxOutput))
                return findings;

            if (sandboxOutput.StartsWith("[[PYTHON TIMEOUT]]", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add("[CRITICAL — SANDBOX TIMEOUT] The script took too long to execute.");
                return findings;
            }

            string[] errorIndicators =
            [
                "Traceback", "Error:", "Exception:", "SyntaxError", "NameError",
                "TypeError", "ValueError", "IndentationError", "KeyError",
                "IndexError", "AttributeError", "ImportError", "ZeroDivisionError",
                "FileNotFoundError", "RuntimeError", "OverflowError"
            ];

            var lines = sandboxOutput.Split('\n');
            var detectedErrors = new List<string>();

            foreach (var line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                foreach (var indicator in errorIndicators)
                {
                    if (trimmed.Contains(indicator, StringComparison.Ordinal))
                    {
                        detectedErrors.Add(trimmed);
                        break;
                    }
                }

                if (trimmed.StartsWith("Error", StringComparison.Ordinal) && trimmed.Length > 5 && trimmed[5] == ':')
                {
                    if (!detectedErrors.Contains(trimmed))
                        detectedErrors.Add(trimmed);
                }
            }

            if (detectedErrors.Count > 0)
            {
                findings.Add("[CRITICAL — RUNTIME ERROR] Code execution produced the following error(s):");
                foreach (var err in detectedErrors.Take(10))
                    findings.Add($"  RUNTIME: {err}");
            }

            return findings;
        }

        // ═══════════════════════════════════════════════
        // Output Range Validation for Calculation Tasks
        // ═══════════════════════════════════════════════

        private static List<string> ValidateOutputRanges(string sandboxOutput, List<FormulaChecklistItem> checklist)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(sandboxOutput) || checklist.Count == 0)
                return findings;

            var outputNumbers = ExtractNumbersFromOutput(sandboxOutput);
            if (outputNumbers.Count == 0)
                return findings;

            foreach (var item in checklist)
            {
                if (string.IsNullOrWhiteSpace(item.ExpectedRange) || item.ExpectedRange == "not specified")
                    continue;

                var (low, high) = ParseExpectedRange(item.ExpectedRange);
                if (low == null && high == null)
                    continue;

                foreach (double num in outputNumbers)
                {
                    bool violation = false;
                    if (low.HasValue && high.HasValue)
                    {
                        double orderLow = low.Value / 10.0;
                        double orderHigh = high.Value * 10.0;
                        if (orderLow > 0 && num < orderLow) violation = true;
                        if (orderHigh > 0 && num > orderHigh) violation = true;
                    }
                    else if (low.HasValue && low.Value > 0)
                    {
                        if (num < low.Value / 10.0 || num > low.Value * 100.0)
                            violation = true;
                    }
                    else if (high.HasValue && high.Value > 0)
                    {
                        if (num > high.Value * 10.0)
                            violation = true;
                    }

                    if (violation)
                    {
                        findings.Add($"[RANGE VIOLATION] {item.StepReference}: output value {num} is more than one order of magnitude outside expected range ({item.ExpectedRange}). Formula or conversion may be incorrect.");
                    }
                }
            }

            return findings;
        }

        private static List<double> ExtractNumbersFromOutput(string output)
        {
            var numbers = new List<double>();
            var matches = System.Text.RegularExpressions.Regex.Matches(output, @"-?\d+\.?\d*(?:[eE][+-]?\d+)?");
            foreach (System.Text.RegularExpressions.Match match in matches)
            {
                if (double.TryParse(match.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    if (Math.Abs(val) > 0.001)
                        numbers.Add(val);
                }
            }
            return numbers.Distinct().ToList();
        }

        private static (double? Low, double? High) ParseExpectedRange(string rangeText)
        {
            string lower = rangeText.ToLowerInvariant();

            var rangeMatch = System.Text.RegularExpressions.Regex.Match(lower, @"(\d+\.?\d*)\s*[-–—to]+\s*(\d+\.?\d*)");
            if (rangeMatch.Success)
            {
                if (double.TryParse(rangeMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double lo) &&
                    double.TryParse(rangeMatch.Groups[2].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double hi))
                {
                    return (lo, hi);
                }
            }

            var singleMatch = System.Text.RegularExpressions.Regex.Match(lower, @"~?\s*(\d+\.?\d*)");
            if (singleMatch.Success)
            {
                if (double.TryParse(singleMatch.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double val))
                {
                    return (val / 10.0, val * 10.0);
                }
            }

            return (null, null);
        }

        // ═══════════════════════════════════════════════
        // Structured Output Validation (Improvement 1)
        // ═══════════════════════════════════════════════

        private static bool ValidateArchitectSchema(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            if (IsArchitectArtifactHandoff(output))
                return true;

            var lines = output.Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (lines.Count == 0)
                return false;

            int expected = 1;
            foreach (string line in lines)
            {
                if (line.Contains("```", StringComparison.Ordinal))
                    return false;

                int sep = line.IndexOfAny(['.', ')']);
                if (sep <= 0 || sep >= 4)
                    return false;

                if (!int.TryParse(line[..sep], out int parsed) || parsed != expected++)
                    return false;
            }

            return true;
        }

        private static bool ValidateBuilderContract(string output, CouncilTaskType taskType, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(output))
            {
                reason = "Builder output was empty.";
                return false;
            }

            if (taskType == CouncilTaskType.Coding)
            {
                Match fenceMatch = BuilderCodeFenceRegex.Match(output);
                if (fenceMatch.Success)
                {
                    string outside = BuilderCodeFenceRegex.Replace(output, string.Empty).Trim();
                    if (!string.IsNullOrWhiteSpace(outside))
                    {
                        reason = "Coding builder output contained prose outside the required single code block.";
                        return false;
                    }

                    if (!HasStrongCodeSignal(fenceMatch.Groups["code"].Value))
                    {
                        reason = "Coding builder output did not contain executable code inside the required code block.";
                        return false;
                    }

                    return true;
                }

                if (!HasStrongCodeSignal(output) || LooksLikeBuilderProsePrelude(output))
                {
                    reason = "Coding builder output drifted away from code-only execution content.";
                    return false;
                }

                return true;
            }

            if (IsLikelyCodeOutput(output))
            {
                reason = "Non-coding builder output drifted into code instead of prose.";
                return false;
            }

            if (output.Contains("[[", StringComparison.Ordinal) || output.Contains("]]", StringComparison.Ordinal))
            {
                reason = "Builder echoed internal pipeline metadata instead of final content.";
                return false;
            }

            return true;
        }

        private static int CountCriticFindings(string criticOutput)
        {
            if (string.IsNullOrWhiteSpace(criticOutput))
                return 0;

            int count = 0;
            foreach (var line in criticOutput.Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.Length > 1 && char.IsDigit(trimmed[0]) && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4)
                    count++;
            }
            return count;
        }

        // ═══════════════════════════════════════════════
        // Static Validation Layer (Improvement 4)
        // ═══════════════════════════════════════════════

        private static List<string> RunStaticValidation(string code)
        {
            var findings = new List<string>();
            if (string.IsNullOrWhiteSpace(code))
                return findings;

            string language = DetectLanguage(code);
            AddStructureBalanceChecks(code, findings);

            switch (language)
            {
                case "python":
                    ValidatePythonCode(code, findings);
                    break;
                case "c#":
                case "java":
                    ValidateCStyleCode(code, findings);
                    break;
                case "javascript":
                    ValidateJavaScriptCode(code, findings);
                    break;
                case "html":
                    ValidateHtmlCode(code, findings);
                    break;
                default:
                    ValidateGenericCalls(code, findings);
                    break;
            }

            return findings;
        }

        private static void AddStructureBalanceChecks(string code, List<string> findings)
        {
            int openBraces = code.Count(c => c == '{');
            int closeBraces = code.Count(c => c == '}');
            if (openBraces != closeBraces)
                findings.Add($"Mismatched braces: {openBraces} opening vs {closeBraces} closing.");

            int openParens = code.Count(c => c == '(');
            int closeParens = code.Count(c => c == ')');
            if (openParens != closeParens)
                findings.Add($"Mismatched parentheses: {openParens} opening vs {closeParens} closing.");

            int openBrackets = code.Count(c => c == '[');
            int closeBrackets = code.Count(c => c == ']');
            if (openBrackets != closeBrackets)
                findings.Add($"Mismatched brackets: {openBrackets} opening vs {closeBrackets} closing.");
        }

        private static void ValidatePythonCode(string code, List<string> findings)
        {
            bool hasTabs = false;
            bool hasSpaces = false;
            foreach (var line in code.Split('\n'))
            {
                if (line.Length > 0 && line[0] == '\t') hasTabs = true;
                if (line.Length > 0 && line[0] == ' ') hasSpaces = true;
            }
            if (hasTabs && hasSpaces)
                findings.Add("Mixed indentation: both tabs and spaces used. Python requires consistent indentation.");

            ValidateGenericCalls(code, findings);
        }

        private static void ValidateCStyleCode(string code, List<string> findings)
        {
            ValidateGenericCalls(code, findings);

            if (code.Contains("class ", StringComparison.OrdinalIgnoreCase)
                && !code.Contains("namespace ", StringComparison.OrdinalIgnoreCase)
                && code.Contains("using ", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add("C#-style source appears to miss a namespace declaration.");
            }
        }

        private static void ValidateJavaScriptCode(string code, List<string> findings)
        {
            if (code.Contains("=>") && code.Contains("function ", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add("Mixed JS styles detected (arrow and function declarations); verify consistency.");
            }

            if (code.Contains("var ", StringComparison.Ordinal))
            {
                findings.Add("Found 'var' declarations; prefer 'const' or 'let' for safer scope semantics.");
            }
        }

        private static void ValidateHtmlCode(string code, List<string> findings)
        {
            string lower = code.ToLowerInvariant();
            if (!lower.Contains("<html")) findings.Add("Missing <html> tag.");
            if (!lower.Contains("<head")) findings.Add("Missing <head> tag.");
            if (!lower.Contains("<body")) findings.Add("Missing <body> tag.");
            if (!lower.Contains("</html>")) findings.Add("Missing </html> closing tag.");
        }

        private static void ValidateGenericCalls(string code, List<string> findings)
        {
            var definedNames = new HashSet<string>(StringComparer.Ordinal);
            var calledNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (var line in code.Split('\n'))
            {
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("def "))
                {
                    int nameEnd = trimmed.IndexOf('(', 4);
                    if (nameEnd > 4)
                        definedNames.Add(trimmed[4..nameEnd].Trim());
                }

                if ((trimmed.Contains("void ") || trimmed.Contains("int ") || trimmed.Contains("string ") ||
                     trimmed.Contains("bool ") || trimmed.Contains("float ") || trimmed.Contains("double ") ||
                     trimmed.Contains("static ") || trimmed.Contains("function ")) && trimmed.Contains('('))
                {
                    int parenIdx = trimmed.IndexOf('(');
                    if (parenIdx > 0)
                    {
                        string beforeParen = trimmed[..parenIdx].TrimEnd();
                        int lastSpace = beforeParen.LastIndexOf(' ');
                        if (lastSpace > 0)
                            definedNames.Add(beforeParen[(lastSpace + 1)..].Trim());
                    }
                }

                int searchStart = 0;
                while (searchStart < trimmed.Length)
                {
                    int callParen = trimmed.IndexOf('(', searchStart);
                    if (callParen <= 0) break;

                    int nameStart = callParen - 1;
                    while (nameStart >= 0 && (char.IsLetterOrDigit(trimmed[nameStart]) || trimmed[nameStart] == '_'))
                        nameStart--;
                    nameStart++;

                    if (nameStart < callParen)
                    {
                        string name = trimmed[nameStart..callParen];
                        if (name.Length > 1 && !char.IsDigit(name[0]))
                            calledNames.Add(name);
                    }
                    searchStart = callParen + 1;
                }
            }

            var builtins = new HashSet<string>(StringComparer.Ordinal)
            {
                "print", "input", "len", "range", "int", "float", "str", "list", "dict", "set",
                "open", "type", "isinstance", "enumerate", "zip", "map", "filter", "sorted", "min", "max",
                "abs", "round", "sum", "any", "all", "super", "self", "cls",
                "Console", "Math", "String", "System", "File", "Path", "Directory",
                "if", "for", "while", "return", "class", "new", "var", "await", "async",
                "main", "Main", "__init__", "__name__"
            };

            foreach (var call in calledNames)
            {
                if (!definedNames.Contains(call) && !builtins.Contains(call))
                    findings.Add($"Function '{call}()' is called but not defined in the output.");
            }

            string trimmedCode = code.TrimEnd();
            if (trimmedCode.Length > 0)
            {
                char lastChar = trimmedCode[^1];
                int openBraces = trimmedCode.Count(c => c == '{');
                int closeBraces = trimmedCode.Count(c => c == '}');
                int openParens = trimmedCode.Count(c => c == '(');
                int closeParens = trimmedCode.Count(c => c == ')');
                if (openBraces > closeBraces || openParens > closeParens)
                    findings.Add("Output appears truncated: code ends with unclosed structures.");
                else if (lastChar != '}' && lastChar != ';' && lastChar != ')' && lastChar != ':' &&
                         lastChar != '\n' && lastChar != '"' && lastChar != '\'' && !char.IsLetterOrDigit(lastChar))
                {
                    // Heuristic: ending on an operator or comma is suspicious
                    if (lastChar is ',' or '+' or '-' or '*' or '/' or '=' or '&' or '|')
                        findings.Add($"Output may be truncated: ends with '{lastChar}'.");
                }
            }
        }

        // ═══════════════════════════════════════════════
        // Formula Extraction from Architect Plan
        // ═══════════════════════════════════════════════

        private static List<FormulaChecklistItem> ExtractFormulaChecklist(string architectOutput)
        {
            var checklist = new List<FormulaChecklistItem>();
            if (string.IsNullOrWhiteSpace(architectOutput))
                return checklist;

            string[] formulaIndicators =
            [
                "formula", "equation", "=", "multiply", "divide", "convert",
                "conversion", "calculate", "compute", "×", "÷", "→", "->",
                "liters", "meters", "celsius", "fahrenheit", "joules", "watts",
                "kilograms", "pounds", "gallons", "square", "cubic", "per second",
                "per hour", "percent", "ratio"
            ];

            string[] rangeIndicators =
            [
                "expect", "range", "typical", "should be", "approximately",
                "order of magnitude", "realistic", "between", "result will be"
            ];

            string[] conversionIndicators =
            [
                "convert", "conversion", "divide by", "multiply by",
                "to meters", "to liters", "to celsius", "to fahrenheit",
                "to kilograms", "to pounds", "to gallons", "mm to", "cm to",
                "km to", "inches to", "feet to", "°c to", "°f to"
            ];

            // Parse the Architect output by numbered steps
            var currentStep = new StringBuilder();
            string currentStepRef = "";
            var lines = architectOutput.Split('\n');

            for (int i = 0; i <= lines.Length; i++)
            {
                string line = i < lines.Length ? lines[i] : "";
                string trimmed = line.TrimStart();
                bool isNewStep = trimmed.Length > 1
                    && char.IsDigit(trimmed[0])
                    && trimmed.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4;

                if ((isNewStep || i == lines.Length) && currentStep.Length > 0)
                {
                    string stepText = currentStep.ToString();
                    string stepLower = stepText.ToLowerInvariant();

                    if (formulaIndicators.Any(f => stepLower.Contains(f)))
                    {
                        var item = new FormulaChecklistItem { StepReference = currentStepRef };

                        // Extract the formula: look for lines with = or arithmetic
                        foreach (var sLine in stepText.Split('\n'))
                        {
                            string sTrimmed = sLine.Trim();
                            string sLower = sTrimmed.ToLowerInvariant();
                            if ((sLower.Contains("=") && (sLower.Contains("*") || sLower.Contains("/") || sLower.Contains("+") || sLower.Contains("-")))
                                || sLower.Contains("formula"))
                            {
                                item.Formula = string.IsNullOrWhiteSpace(item.Formula)
                                    ? sTrimmed
                                    : item.Formula + " | " + sTrimmed;
                            }
                        }

                        if (string.IsNullOrWhiteSpace(item.Formula))
                            item.Formula = stepText.Length > 200 ? stepText[..200] : stepText;

                        // Extract unit conversions
                        var convParts = new List<string>();
                        foreach (var sLine in stepText.Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            string sLower = sLine.Trim().ToLowerInvariant();
                            if (conversionIndicators.Any(c => sLower.Contains(c)))
                                convParts.Add(sLine.Trim());
                        }
                        item.UnitConversions = convParts.Count > 0 ? string.Join("; ", convParts) : "none specified";

                        // Extract expected range
                        var rangeParts = new List<string>();
                        foreach (var sLine in stepText.Split(['.', '\n'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            string sLower = sLine.Trim().ToLowerInvariant();
                            if (rangeIndicators.Any(r => sLower.Contains(r)))
                                rangeParts.Add(sLine.Trim());
                        }
                        item.ExpectedRange = rangeParts.Count > 0 ? string.Join("; ", rangeParts) : "not specified";

                        checklist.Add(item);
                    }

                    currentStep.Clear();
                }

                if (isNewStep)
                {
                    int dotPos = trimmed.IndexOfAny(['.', ')']);
                    currentStepRef = dotPos > 0 ? "Step " + trimmed[..dotPos].Trim() : "Step ?";
                }

                if (i < lines.Length)
                    currentStep.AppendLine(line);
            }

            return checklist;
        }

        // ═══════════════════════════════════════════════
        // Asymmetric Context Allocation (Improvement 9)
        // ═══════════════════════════════════════════════

        private uint GetRoleContextSize(CouncilRole role)
        {
            if (CanUseCloudCouncil)
            {
                // Use the cloud model's full context window — no local slider cap.
                // Builder gets the full window (needs maximum room for code + docs).
                // Architect and Critic get half, which is still far larger than any local model.
                int cloudWindow = _openRouterChatService.GetApproximateContextWindowTokens(OpenRouterChatService.WorkplaceCouncilDefaultModelId);
                uint cloudCtx = role == CouncilRole.Builder
                    ? (uint)cloudWindow
                    : (uint)(cloudWindow / 2);
                return Math.Max(cloudCtx, MinRoleContext);
            }

            uint configured = role switch
            {
                CouncilRole.Architect => _architectContextSize,
                CouncilRole.Builder => _builderContextSize,
                CouncilRole.Critic => _criticContextSize,
                _ => _contextSize
            };

            if (_autoOptimizeRoleContexts)
                configured = GetOptimizedContextForModel(role, configured);

            if (role == CouncilRole.Critic && _documents.Count > 0)
                configured = Math.Max(configured, 4096);

            if (_activeTaskComplexity == TaskComplexity.Complex)
                configured = Math.Min(MaxRoleContext, configured + 512);

            return Math.Clamp(configured, MinRoleContext, MaxRoleContext);
        }

        // ═══════════════════════════════════════════════
        // Per-Role Temperature & Sampling (Improvement 10)
        // ═══════════════════════════════════════════════

        private static (float Temperature, float MinP) GetRoleSamplingConfig(CouncilRole role)
        {
            return role switch
            {
                CouncilRole.Architect => (0.7f, 0.0f),
                CouncilRole.Builder => (0.7f, 0.0f),
                CouncilRole.Critic => (0.7f, 0.0f),
                _ => (0.7f, 0.05f)
            };
        }

        // ── Agentic Pause syntax rule ── injected into every LOCAL role system prompt.
        // Promoted to a class-level const so the cloud path can deterministically swap it
        // for CloudNativeToolsNote (cloud roles use real tool-calling, not the [PAUSE:] protocol,
        // which only the local AgenticPauseEngine intercepts).
        private const string AgenticPauseRule =
            "\n\nAGENTIC PAUSE RULE:\n" +
            "If you do not know a specific fact, number, or value, you MUST pause and request it.\n" +
            "Do NOT guess. Do NOT make up numbers or facts.\n" +
            "To pause, output EXACTLY this on its own line (nothing before or after on that line):\n" +
            "[PAUSE: TOOL_NAME | your query here]\n" +
            "Allowed tools: SEARCH_HIPPOCAMPUS, CALCULATE, RUN_SANDBOX, WEB_SEARCH, PYTHON_MATH\n" +
            "Example: [PAUSE: CALCULATE | 45 * 2]\n" +
            "Example: [PAUSE: SEARCH_HIPPOCAMPUS | boiling point of water]\n" +
            "Example: [PAUSE: WEB_SEARCH | latest .NET 10 release notes]\n" +
            "Example: [PAUSE: PYTHON_MATH | print((42 * 17) / 3)]\n" +
            "Tool choice: use CALCULATE for simple arithmetic or unit conversion; use PYTHON_MATH for multi-step equations, formulas, simulations, tables, data transforms, or anything that benefits from executable verification; use WEB_SEARCH for current/latest/source-backed facts; use SEARCH_HIPPOCAMPUS for facts from this chat or prior council work.\n" +
            "Use WEB_SEARCH for current events, updated documentation, specific definitions, comparisons, explanations, or facts outside your training data.\n" +
            "For prompts like \"what is X\" or \"what does X do to Y\", use WEB_SEARCH when X/Y are specific, current, obscure, technical, medical/legal/financial, or likely to have changed.\n" +
            "For complex math or data processing, write a Python 3 script and call PYTHON_MATH.\n" +
            "Your Python code must use print() to output the final answer.\n" +
            "NEVER show Python code, [PAUSE: ...] lines, or internal tool commands in your final visible answer.\n" +
            "You may pause a maximum of 3 times per response.\n" +
            "When you receive [RESULT: ...] continue your sentence as if you always knew that value.\n" +
            "When a WEB_SEARCH result is provided, treat that result as authoritative for current/source-backed claims it actually covers. Do not add unsupported current/source-backed facts from memory or guesses, and do not treat off-topic web results as support.\n" +
            "Do NOT output [PAUSE: ...] lines for things you already know with certainty.";

        // ── Cloud-native tools note ── replaces AgenticPauseRule for cloud council roles.
        // Cloud roles call real tools through OpenRouter's tool-calling protocol, so they must
        // NOT be told to emit [PAUSE:]/[RESULT:] markers (nothing intercepts them in cloud mode).
        // Built dynamically so the advertised tool list always matches BuildCouncilCloudToolDefinitions:
        // a static note that promised web_search while the tool was not advertised invited phantom
        // tool calls when the web toggle was off.
        private string BuildCloudCouncilToolsNote()
        {
            string webSearchEntry = _isWebSearchEnabled
                ? "web_search (definitions, explanations, comparisons, documentation, current events, or facts outside your training data), "
                : string.Empty;
            string webDisabledNote = _isWebSearchEnabled
                ? string.Empty
                : "Web search is currently disabled by the user — do not attempt web lookups; state the limitation if current information would change the answer.\n";
            return
                "\n\nTOOLS AVAILABLE:\n" +
                "You can call real tools directly when you need a current/source-backed fact, definition, explanation, comparison, number, documentation detail, or current information. Do NOT guess and do NOT fabricate numbers or source-backed facts.\n" +
                "Available tools: " + webSearchEntry +
                "run_python (execute Python 3 for math or data processing; print() the final answer), " +
                "calculate (evaluate a math or unit-conversion expression), " +
                "search_session_memory (recall facts, prior plans, and outputs stored earlier in this workplace session).\n" +
                webDisabledNote +
                "Tool choice: use calculate for simple arithmetic or unit conversion; use run_python for multi-step equations, formulas, simulations, tables, data transforms, or anything that benefits from executable verification; use web_search for current/latest/source-backed facts; use search_session_memory for facts from this chat or prior council work.\n" +
                "Before calling web_search, connect the current role payload to the user's latest objective and recent workplace turns. Make the query standalone: include the actual title, person, organization, product, document, API, or topic instead of references like 'the movie', 'this model', 'that article', or pronouns.\n" +
                "For relationship questions, preserve the relation in the query, such as what X does to Y, what happens to character Z at the end of title X, how API A differs from API B, or which version changed a behavior.\n" +
                "Tool results only cover the specific current/source-backed claim or computation they actually address; off-topic web results are not support for the user's named entities. If web evidence is partial, mismatched, or misses a required named entity, call one narrower web_search query before finalizing when the tool is available.\n" +
                "Issue a normal tool call when you need one — the system runs it and returns the result, which you must treat as authoritative grounded fact.\n" +
                "NEVER write [PAUSE: ...] or [RESULT: ...] lines, raw tool commands, or Python code in your final visible answer.\n" +
                "For prompts like \"what is X\" or \"what does X do to Y\", call web_search when X/Y are specific, current, obscure, technical, medical/legal/financial, or likely to have changed.\n" +
                "Do NOT call tools for things you already know with certainty.";
        }

        private static string GetEmbeddedSystemPrompt(CouncilRole role)
        {
            // ── Environment briefing ── injected into every role so the model understands where it
            // runs, where its output goes, and the hard limits of the workplace. Grounds the model
            // and prevents it from offering capabilities it does not have (live web, file system,
            // package installs, external CDNs in the canvas).
            const string environmentBriefing =
                "\n\nYOUR ENVIRONMENT:\n" +
                "You operate inside Axiom Workplace, an offline desktop application. Three AI roles collaborate in a fixed relay: " +
                "Architect (writes the plan) then Builder (produces the implementation) then Critic (reviews it). You are exactly ONE of these roles.\n" +
                "WHERE OUTPUT GOES: The Architect's plan and the Critic's review appear in the chat panel. The Builder's main " +
                "deliverable is rendered in the Project Canvas, a side panel that can display HTML pages, SVG graphics, charts, " +
                "interactive JavaScript, and formatted documents.\n" +
                "THE PROJECT CANVAS IS OFFLINE: anything rendered there has NO internet access. Never reference external URLs, CDNs, " +
                "web fonts, remote images, or online libraries (no <script src=\"http...\">, no Google Fonts, no CDN <link>). Everything " +
                "must be fully self-contained and inline so it works with zero network access.\n" +
                "WHAT YOU CAN DO: reason, write text and code, and use the pause tools below (sandbox execution, web search, Python math, " +
                "memory lookup, calculator).\n" +
                "WHAT YOU CANNOT DO: you cannot browse the live web except through WEB_SEARCH, cannot access the user's files except " +
                "documents they explicitly attach, cannot install packages, and cannot run anything outside the provided tools. " +
                "Do not claim to perform actions you cannot actually do.";

            // ── Role boundary rule ── keeps each role in its lane and stops internal routing
            // markers / cross-role chatter from leaking into the user-visible output.
            const string roleBoundaryRule =
                "\n\nROLE BOUNDARY:\n" +
                "Stay strictly within your own role. Never write another role's part, never speak or answer as another role, and never " +
                "address, quote, or impersonate the other roles in your visible output. Produce only your own contribution.\n" +
                "Internal markers such as [[ORIGINAL REQUEST]], [[OBJECTIVE]], [[APPROVED ARCHITECTURE]], [[ARCHITECT PLAN]], " +
                "[[PRIOR KNOWLEDGE]], [[PROJECT KNOWLEDGE BASE]], [[BUILDER OUTPUT]], and any PIPELINE STATE HEADER are private routing " +
                "labels. Never repeat, echo, restate, or reveal them in your answer.";

            // ── Agentic Pause syntax rule ── injected into every role system prompt.
            // Definition lives in the class-level AgenticPauseRule const (the cloud path swaps it
            // for CloudNativeToolsNote). Written in primitive, explicit language so 1B-5B models
            // follow it reliably for local council inference.
            const string agenticPauseRule = AgenticPauseRule;

            return role switch
            {
                CouncilRole.Architect =>
                    "You are the Architect. Your ONLY job is to produce a numbered step-by-step plan. " +
                    "Exception: when a later STRUCTURED OUTPUT CONTRACT explicitly requires ARCHITECT_HANDOFF, output that handoff shape instead of a numbered plan. " +
                    "Rules: " +
                    "1. Output ONLY a numbered list. No prose, no code, no greetings, no explanations. " +
                    "2. Each step is one concrete action in 1-2 sentences. " +
                    "3. For code tasks, each step must state: the function/component name, what it takes as input, " +
                    "what it returns, and what exact operation it performs. " +
                    "4. Never use vague words like handle, manage, process, or deal with. " +
                    "Describe the exact operation. " +
                    "5. Keep the plan short — no more than 8 steps. The Builder must hold the entire plan in context. " +
                    "6. ALWAYS plan for the LATEST user message. If prior conversation context is provided, " +
                    "use it for background understanding only. Do NOT repeat or continue a plan from a prior turn " +
                    "unless the user explicitly asks you to. Each new user message gets a fresh plan." +
                    environmentBriefing + roleBoundaryRule + agenticPauseRule,

                CouncilRole.Builder =>
                    "You are the Builder. Your role is IMPLEMENTATION ONLY. " +
                    "IDENTITY RULE: You are NOT the Architect. You NEVER produce a numbered plan, numbered steps, " +
                    "a step-by-step outline, or any re-listing of what you are about to do. " +
                    "The [[APPROVED ARCHITECTURE]] block in your context is a SPECIFICATION to implement — " +
                    "not a format to copy, not something to summarize, not something to re-state. " +
                    "Start implementing immediately without any preamble, plan summary, or numbered list. " +
                    "The Architect's numbered plan is your strict specification. " +
                    "Implement every step in the plan in the exact order it appears. " +
                    "Do not add, skip, or reinterpret any step. " +
                    "Never truncate output or summarize sections with descriptions. " +
                    "REASONING RULE: Before producing output for each plan step, silently work through the logic: " +
                    "identify what is being asked, determine the correct approach, verify your reasoning is sound, " +
                    "then write the final output. For MATH: always verify each result by substituting back into the original " +
                    "formula before moving to the next step. For CODE: mentally trace execution with sample inputs " +
                    "before writing the function. For RESEARCH/ANALYSIS: identify the specific claim, locate supporting " +
                    "evidence in the provided context, then write the paragraph. " +
                    "FORMAT RULE: Adapt your output format strictly to the task type. " +
                    "For CODING tasks: output executable code only — no numbered lists, no plan headers. " +
                    "For MATH/CALCULATION tasks (without explicit code request): present formulas and " +
                    "calculations in natural language with step-by-step work — never as code or pseudo-code. " +
                    "For RESEARCH/ANALYSIS/GENERAL tasks: output well-structured prose with paragraphs and headings. " +
                    "CRITICAL OUTPUT RULE: Output ONLY your implementation — the code or prose the user needs. " +
                    "NEVER echo, repeat, or reproduce any part of the input payload. " +
                    "Do NOT output headers like [[PRIOR KNOWLEDGE]], [[ARCHITECT PLAN]], [[BUILDER OUTPUT]], " +
                    "[[OBJECTIVE]], [[USER PROMPT]], [[SHARED VOCABULARY]], PIPELINE STATE HEADER, " +
                    "or any [[LABEL]] blocks. These are internal routing markers — never include them in your response. " +
                    "Do NOT restate the Architect's plan, the user's prompt, or any context you received. " +
                    "Start your response directly with the implementation content." +
                    environmentBriefing + roleBoundaryRule + agenticPauseRule,

                CouncilRole.Critic =>
                    "You are the Critic, a thorough reviewer. " +
                    "Review the Builder's output against the Architect's plan and the original user request. " +
                    "Check whether every planned step was addressed, whether the output is accurate and complete, " +
                    "and whether it fulfills what the user originally asked for. " +
                    "Report each problem as a numbered item. " +
                    "If nothing is wrong, output exactly: 'No issues found.' and nothing else." +
                    environmentBriefing + roleBoundaryRule + agenticPauseRule,

                _ => ""
            };
        }

        private string BuildSubOneBCouncilSystemPrompt(
            CouncilRole role,
            string currentSystemPrompt,
            bool outputIsGrammarConstrained,
            bool allowAgenticPauses)
        {
            if (outputIsGrammarConstrained)
            {
                return "Return exactly the required JSON object. Do not explain. Do not repeat input text.";
            }

            string toolNote = allowAgenticPauses
                ? "\nIf a needed fact or number is missing, write one exact tool line only: [PAUSE: CALCULATE | expression], [PAUSE: PYTHON_MATH | code], [PAUSE: WEB_SEARCH | query], or [PAUSE: SEARCH_HIPPOCAMPUS | query]. After a result, finish normally. Never show tool lines in the final answer."
                : "\nTool preflight is already complete. Use any TOOL OBSERVATION blocks as facts. Do not write [PAUSE:] or JSON.";

            string roleRules = role switch
            {
                CouncilRole.Architect =>
                    "ROLE: Architect. Output only a short numbered plan with 3-5 concrete steps. Do not write code. Do not repeat the user request, system text, labels, or role description.",
                CouncilRole.Builder =>
                    "ROLE: Builder. Output only the final deliverable the user needs. Do not output a plan, role description, checklist, internal labels, or the prompt. For code tasks, write executable self-contained code directly. For prose tasks, write the answer directly.",
                CouncilRole.Critic =>
                    "ROLE: Critic. Review the Builder output against the request. If problems exist, list only specific issues. If none, output exactly: No issues found.",
                _ => "Answer the latest user request directly."
            };

            string compactContext = ClipForSubOneBCouncilPrompt(currentSystemPrompt, 1200);
            return (roleRules
                + "\nFollow the latest ORIGINAL REQUEST. Internal [[LABEL]] blocks are context, not output."
                + toolNote
                + (string.IsNullOrWhiteSpace(compactContext) ? string.Empty : "\n\nTiny-model context summary:\n" + compactContext)).Trim();
        }

        private static string BuildSubOneBCouncilPayload(CouncilRole role, string userPayload)
        {
            string payload = userPayload ?? string.Empty;
            if (payload.Length <= 9000)
                return payload + "\n\nFINAL INSTRUCTION: Produce only the " + role + " output now. Do not restate any instructions.";

            int head = role == CouncilRole.Builder ? 2200 : 1400;
            int tail = role == CouncilRole.Builder ? 6200 : 4200;
            head = Math.Min(head, payload.Length / 3);
            tail = Math.Min(tail, payload.Length - head);
            return payload[..head].TrimEnd()
                + "\n[...middle context omitted for tiny local model...]\n"
                + payload[^tail..].TrimStart()
                + "\n\nFINAL INSTRUCTION: Produce only the " + role + " output now. Do not restate any instructions.";
        }

        private static string ClipForSubOneBCouncilPrompt(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length <= maxChars)
                return text ?? string.Empty;

            int head = Math.Max(300, maxChars / 3);
            int tail = Math.Max(300, maxChars - head - 70);
            return text[..head].TrimEnd()
                + "\n[...omitted...]\n"
                + text[^tail..].TrimStart();
        }

        private static string BuildCouncilBaseSharedPayload(CouncilRunContext runContext, string sharedVocabularySection, string baseKnowledgeBlock, string webContext)
        {
            var sb = new StringBuilder();
            sb.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));

            if (!string.IsNullOrWhiteSpace(runContext.Objective))
                sb.AppendLine(BuildLabeledBlock("OBJECTIVE", runContext.Objective));

            if (!string.IsNullOrWhiteSpace(runContext.CalculatorContext))
                sb.AppendLine(BuildLabeledBlock("CALCULATOR TOOL RESULTS", runContext.CalculatorContext));

            if (!string.IsNullOrWhiteSpace(webContext))
                sb.AppendLine(webContext.Trim());

            if (!string.IsNullOrWhiteSpace(sharedVocabularySection))
                sb.AppendLine(sharedVocabularySection);

            if (!string.IsNullOrWhiteSpace(baseKnowledgeBlock))
                sb.AppendLine(baseKnowledgeBlock);

            return sb.ToString();
        }

        private async Task<CouncilBaseStateVault?> CreateCouncilBaseStateVaultAsync(string sharedPayload, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(sharedPayload))
                return null;

            if (EstimateTokenCount(sharedPayload) < 900)
                return null;

            var vault = new CouncilBaseStateVault { SharedPayload = sharedPayload };
            string runId = Guid.NewGuid().ToString("N");

            foreach (var role in new[] { CouncilRole.Architect, CouncilRole.Builder, CouncilRole.Critic })
            {
                CouncilModelConfig config = _council[role];
                if (string.IsNullOrWhiteSpace(config.ModelPath) || IsGemma4Model(config.DisplayName) || IsGemma4Model(config.ModelPath ?? ""))
                    continue;

                try
                {
                    uint roleContext = GetRoleContextSize(role);

                    // Cache-aware planning — same rationale as ExecuteCouncilRoleAsync: a fresh
                    // plan probed against free VRAM that already contains these cached weights
                    // would plan 0 GPU layers and force a CPU reload of a GPU-resident model.
                    bool vaultWantGpu = InferenceBackendService.CurrentMode == InferenceComputeMode.GpuAccelerated
                        && NativeBackendInit.GpuConfigured;
                    bool vaultReuseCached = _modelCache.TryGetValue(config.ModelPath!, out var cached)
                        && (cached!.GpuLayerCount > 0) == vaultWantGpu;

                    var plan = vaultReuseCached
                        ? InferenceBackendService.CreatePlanForCachedWeights(config.ModelPath!, roleContext, cached!.GpuLayerCount)
                        : InferenceBackendService.CreatePlan(config.ModelPath, roleContext, InferenceBackendService.CurrentMode);

                    LLamaWeights? model = null;
                    if (vaultReuseCached)
                    {
                        model = cached!.Weights;
                        cached.LastUsed = DateTime.Now;
                    }
                    else
                    {
                        if (_modelCache.TryGetValue(config.ModelPath!, out var stale))
                        {
                            stale.Dispose();
                            _modelCache.Remove(config.ModelPath!);
                        }

                        long incomingBytes = GetModelFileSizeSafe(config.ModelPath!);
                        EvictModelCacheForLoad(config.ModelPath!, incomingBytes);

                        await InferenceBackendService.RunExclusiveAsync(async () =>
                        {
                            model = await Task.Run(() => LLamaWeights.LoadFromFile(plan.Parameters), token);
                        });

                        _modelCache[config.ModelPath!] = new CachedModelEntry
                        {
                            ModelPath = config.ModelPath!,
                            Weights = model!,
                            GpuLayerCount = plan.Parameters.GpuLayerCount,
                            SizeBytes = incomingBytes
                        };
                    }

                    using var baseContext = await Task.Run(() => LlamaContextFactory.CreateContext(model!, plan.Parameters), token);
                    var baseExecutor = new InteractiveExecutor(baseContext);
                    var baseSession = new LLama.ChatSession(baseExecutor);
                    baseSession.WithHistoryTransform(new PromptTemplateTransformer(model!, withAssistant: true));
                    baseSession.AddSystemMessage("Council shared base context bootstrap.");

                    // Decoding the shared payload is a full prompt-processing pass —
                    // serialize it with other native decodes and keep it off the UI thread.
                    await InferenceBackendService.RunScopedExclusiveAsync(InferenceBackendService.WorkplaceCouncilScope, () =>
                        Task.Run(() => baseSession.AddAndProcessUserMessage(sharedPayload, token), token));

                    string basePath = Path.Combine(CouncilKvStateFolder, $"base_{runId}_{role.ToString().ToLowerInvariant()}.kvstate");
                    await Task.Run(() => baseContext.SaveState(basePath), token);
                    vault.RoleStatePaths[role] = basePath;
                }
                catch (Exception ex)
                {
                    LogActivity($"Base state creation fallback for {role}: {ex.Message}");
                }
            }

            return vault.RoleStatePaths.Count > 0 ? vault : null;
        }

        private void CleanupCouncilBaseStateVault(CouncilBaseStateVault? vault)
        {
            if (vault == null)
                return;

            foreach (string path in vault.RoleStatePaths.Values)
            {
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                }
                catch
                {
                }
            }
        }

        private async Task<ReasoningParser.ParsedResponse> ExecuteCouncilRoleAsync(
            CouncilRole role,
            string systemPrompt,
            string userPayload,
            CancellationToken token,
            float? temperatureOverride = null,
            CouncilBaseStateVault? baseStateVault = null,
            bool loadBaseState = false,
            bool allowBatchRecovery = true,
            bool showLiveCard = true,
            int? maxGenerationTokensOverride = null,
            int? contextSizeOverride = null,
            bool useBuilderToolDecision = true,
            Grammar? outputGrammar = null,
            bool allowAgenticPauses = true,
            bool internalInferenceStep = false)
        {
            if (!internalInferenceStep)
                RecordCouncilRolePromptUsage(role, systemPrompt, userPayload);

            if (_isCloudModeEnabled)
            {
                return await ExecuteCouncilRoleCloudAsync(role, systemPrompt, userPayload, token, showLiveCard);
            }

            // Role-model fallback: a role with no dedicated model borrows another loaded
            // council model instead of silently skipping its stage. The model cache is keyed
            // by path, so a borrowed model is a cache hit — no extra load cost.
            CouncilModelConfig config = GetEffectiveRoleConfig(role);

            if (string.IsNullOrWhiteSpace(config.ModelPath))
            {
                return ReasoningParser.Parse(userPayload);
            }

            if (string.IsNullOrWhiteSpace(_council[role].ModelPath))
            {
                LogActivity($"{role}: no dedicated model loaded — borrowing '{config.DisplayName}' for this stage.");
            }

            bool isGemma4 = IsGemma4Model(config.DisplayName) || IsGemma4Model(config.ModelPath ?? "");
            LocalModelCapabilityProfile localCapability = LocalModelCapabilityProfile.FromModel(config.ModelPath);
            bool useSubOneBMode = localCapability.IsSubOneB && !isGemma4;
            if (useSubOneBMode)
            {
                systemPrompt = BuildSubOneBCouncilSystemPrompt(role, systemPrompt, outputGrammar != null, allowAgenticPauses);
                userPayload = outputGrammar != null
                    ? ClipForSubOneBCouncilPrompt(userPayload, 7000) + "\n\nReturn exactly one JSON object now."
                    : BuildSubOneBCouncilPayload(role, userPayload);
                if (!internalInferenceStep)
                {
                    LogActivity($"{role}: sub-1B local profile active ({localCapability.Evidence}).");
                    _ = BackendLogService.LogEventAsync(
                        "SubOneBLocalMode",
                        $"Surface:WorkplaceCouncil\nRole:{role}\nModel:{config.DisplayName}\nEvidence:{localCapability.Evidence}\nParams:{localCapability.ParameterCount}");
                }
            }

            if (role == CouncilRole.Builder && useBuilderToolDecision && !isGemma4)
            {
                return await ExecuteLocalBuilderWithToolDecisionAsync(
                    systemPrompt,
                    userPayload,
                    token,
                    temperatureOverride,
                    baseStateVault,
                    loadBaseState,
                    allowBatchRecovery,
                    showLiveCard,
                    maxGenerationTokensOverride,
                    contextSizeOverride,
                    useSubOneBMode);
            }

            if (role == CouncilRole.Builder && useBuilderToolDecision && isGemma4)
                LogActivity("Builder grammar-constrained tool decision skipped: Gemma 4 is using the external CLI runner, which has no configured grammar contract.");

            if (isGemma4)
            {
                string gemmaRoleName = role.ToString();
                LogActivity($"{gemmaRoleName}: Gemma 4 local CLI mode.");

                string prompt = BuildPrompt(PromptFormat.Gemma4, systemPrompt, userPayload);

                var (gemmaRoleTemp, _) = GetRoleSamplingConfig(role);
                if (temperatureOverride.HasValue)
                {
                    gemmaRoleTemp = temperatureOverride.Value;
                }
                string raw = await LocalGemmaCliRunner.InferAsync(
                    config.ModelPath!,
                    prompt,
                    2048,
                    gemmaRoleTemp,
                    0.05f,
                    new[] { "<end_of_turn>" },
                    token);

                string roleMarker = GetRoleCompletionMarker(role);
                bool hasHandoffToken = raw.Contains(HandoffEndToken, StringComparison.Ordinal);
                bool hasRoleMarker = !string.IsNullOrWhiteSpace(roleMarker) && raw.Contains(roleMarker, StringComparison.Ordinal);

                if (!hasHandoffToken && hasRoleMarker)
                {
                    LogActivity($"{gemmaRoleName}: handoff token missing, accepted via completion marker '{roleMarker}'.");
                    if (!internalInferenceStep && (role == CouncilRole.Architect || role == CouncilRole.Critic))
                        AppendChat("warning", $"{gemmaRoleName}: handoff marker was missing; accepted via completion marker normalization.");
                }
                else if (!hasHandoffToken && !hasRoleMarker)
                {
                    if (raw.Length < 64)
                    {
                        LogActivity($"{gemmaRoleName}: completion markers missing on short output; downstream normalization may retry.");
                    }
                    else if (!internalInferenceStep && (role == CouncilRole.Architect || role == CouncilRole.Critic))
                    {
                        AppendChat("warning", $"{gemmaRoleName}: required termination marker was missing; accepted through normalization.");
                    }
                }

                raw = raw.Replace(HandoffEndToken, "", StringComparison.Ordinal);
                string cleanedGemma = CleanModelOutput(raw);
                return ReasoningParser.Parse(cleanedGemma, true);
            }

            bool isQwen3 = IsQwen3Model(config.ModelPath ?? string.Empty) || IsQwen3Model(config.DisplayName);
            if (isQwen3)
                systemPrompt = BuildQwen3SystemPrompt(systemPrompt, false);

            string roleName = role.ToString();
            LogActivity($"{roleName}: Loading model '{config.DisplayName}'...");

            // Live streaming card: local council roles previously generated in total silence —
            // no tokens appeared anywhere until the stage finished, which made slow local runs
            // look like the stage (especially the Builder) was skipped entirely. Mirror the
            // cloud path: stream the in-progress text into a per-role chat card.
            string liveRoleKey = roleName.ToLowerInvariant();
            WorkplaceChatMessage? liveCard = null;
            bool useLiveCard = showLiveCard && IsCouncilDisplayRole(liveRoleKey);
            if (useLiveCard)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_streamingCouncilCards.TryGetValue(liveRoleKey, out var stale))
                    {
                        _chatCards.Remove(stale);
                        _streamingCouncilCards.Remove(liveRoleKey);
                    }
                    liveCard = new WorkplaceChatMessage { Role = liveRoleKey, Content = "⏳ Generating..." };
                    _chatCards.Add(liveCard);
                    _streamingCouncilCards[liveRoleKey] = liveCard;
                    ChatScrollViewer?.ScrollToEnd();
                }, DispatcherPriority.Background);
            }

            long lastLivePushTicks = 0;
            Action<string>? onLiveTextProgress = null;
            if (useLiveCard)
            {
                onLiveTextProgress = current =>
                {
                    // Throttle UI pushes to ~7/sec; the per-token continuations run off the
                    // dispatcher thread, so only the queued update touches the UI.
                    long now = Environment.TickCount64;
                    if (now - Interlocked.Read(ref lastLivePushTicks) < 140)
                        return;
                    Interlocked.Exchange(ref lastLivePushTicks, now);
                    string preview = BuildLiveStreamPreview(current);
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        // Only update while this is still the ACTIVE streaming card. Once AppendChat
                        // finalizes it (and removes it from _streamingCouncilCards), a late throttled
                        // preview must not revert the finalized full answer back to a truncated "…" tail.
                        if (liveCard != null
                            && _streamingCouncilCards.TryGetValue(liveRoleKey, out var activeCard)
                            && ReferenceEquals(activeCard, liveCard))
                            liveCard.Content = preview;
                    }, DispatcherPriority.Background);
                };
            }

            // A caller-supplied context override lets the document map-reduce request a smaller context
            // than the role default. On GPUs without flash attention (Pascal/Maxwell, or any Gemma) the
            // KV cache stays f16, so a large context steals enough VRAM to push layers onto the CPU; a
            // smaller context on a FRESH load fits more layers on the GPU and runs far faster.
            uint roleContext = contextSizeOverride is int ctxOverride && ctxOverride > 0
                ? (uint)Math.Clamp(ctxOverride, (int)MinRoleContext, (int)MaxRoleContext)
                : GetRoleContextSize(role);
            string cacheKey = config.ModelPath!;

            // Self-healing GPU-crash recovery for the council. A llama.cpp native abort (CUDA
            // illegal memory access during decode — the documented Pascal/8GB instability) kills the
            // whole process with no catchable exception. Normal chat already steps a struck model
            // down (smaller GPU context, then CPU) via the crash ledger; the council path never did,
            // so the Builder crashed hard on every run with no recovery. Mirror that here so a model
            // that has crashed on GPU loads at reduced VRAM pressure and, if it keeps dying, on CPU.
            var (recoveryMode, recoveryContext) = ResolveCouncilCrashRecovery(cacheKey, roleContext);
            roleContext = recoveryContext;

            // Cache-aware planning. A fresh CreatePlan probes CURRENT free VRAM — which
            // already contains this model's own weights when they are cached. The old
            // "cacheHit only if the fresh plan's layer count matches" check therefore
            // failed on every stage after the first (fresh plan saw low VRAM → planned
            // 0 layers → mismatch → evicted the GPU-resident weights and reloaded the
            // model from disk CPU-only). Symptom: GPU active for the first stage, then
            // 0% GPU with the CPU pegged for the rest of the run.
            bool wantGpu = recoveryMode == InferenceComputeMode.GpuAccelerated
                && NativeBackendInit.GpuConfigured;
            bool reuseCachedWeights = _modelCache.TryGetValue(cacheKey, out var cachedEntry)
                && (cachedEntry!.GpuLayerCount > 0) == wantGpu;

            var plan = reuseCachedWeights
                ? InferenceBackendService.CreatePlanForCachedWeights(cacheKey, roleContext, cachedEntry!.GpuLayerCount)
                : InferenceBackendService.CreatePlan(config.ModelPath, roleContext, recoveryMode);
            if (!contextSizeOverride.HasValue)
            {
                _effectiveLocalRoleContextSizes[role] = plan.Parameters.ContextSize ?? roleContext;
                UpdateWorkplaceTokenUsageIndicator();
            }
            HardwareInfoBlock.Text = $"Runtime: adaptive context | Backend: {plan.BackendName}";

            LogActivity($"{roleName}: ctx={plan.Parameters.ContextSize}, gpu_layers={plan.Parameters.GpuLayerCount}, backend={plan.BackendName}");
            if (!internalInferenceStep)
                AppendChat("system", $"{roleName}: ctx {plan.Parameters.ContextSize}, gpu layers {plan.Parameters.GpuLayerCount} | {plan.Reason}");

            var responseBuilder = new StringBuilder();

            LogActivity($"{roleName}: Initializing model and session...");

            LLamaWeights? model = null;
            LLamaContext? context = null;
            InteractiveExecutor? executor = null;

            // Cover model load + context creation with crash forensics, not just the decode.
            // A native abort during init (e.g. a CUDA OOM that ggml turns into abort() instead of
            // a catchable error) happens BEFORE the decode marker is dropped, so without this it
            // would leave no record — never self-heal — and crash on every launch. Stamp the
            // PLANNED backend so a death here is attributed to GPU and steps the model down next
            // launch (ResolveCouncilCrashRecovery). EndDecode below clears it on a clean init.
            NativeDecodeForensics.SetActiveModel(config.ModelPath!, plan.UsingGpu, plan.Parameters.GpuLayerCount);
            NativeDecodeForensics.BeginDecode($"Workplace.{roleName}.Init", 0, (int)plan.Parameters.ContextSize, config.DisplayName);

            try
            {
                bool cacheHit = reuseCachedWeights;

                if (cacheHit)
                {
                    model = cachedEntry!.Weights;
                    cachedEntry.LastUsed = DateTime.Now;
                    LogActivity($"{roleName}: Using cached model '{config.DisplayName}' ({(cachedEntry.GpuLayerCount > 0 ? $"{cachedEntry.GpuLayerCount} GPU layers" : "CPU")})");
                }
                else
                {
                    if (_modelCache.TryGetValue(cacheKey, out var stale))
                    {
                        stale.Dispose();
                        _modelCache.Remove(cacheKey);
                    }

                    long incomingBytes = GetModelFileSizeSafe(cacheKey);
                    EvictModelCacheForLoad(cacheKey, incomingBytes);

                    await InferenceBackendService.RunExclusiveAsync(async () =>
                    {
                        model = await Task.Run(() => LLamaWeights.LoadFromFile(plan.Parameters), token);
                    });

                    _modelCache[cacheKey] = new CachedModelEntry
                    {
                        ModelPath = config.ModelPath!,
                        Weights = model!,
                        GpuLayerCount = plan.Parameters.GpuLayerCount,
                        SizeBytes = incomingBytes
                    };
                    LogActivity($"{roleName}: Model loaded and cached.");
                }

                context = await Task.Run(() => LlamaContextFactory.CreateContext(model!, plan.Parameters), token);
                if (loadBaseState && baseStateVault != null && baseStateVault.RoleStatePaths.TryGetValue(role, out string? baseStatePath) && !string.IsNullOrWhiteSpace(baseStatePath))
                {
                    try
                    {
                        await Task.Run(() => context.LoadState(baseStatePath), token);
                    }
                    catch (Exception stateEx)
                    {
                        LogActivity($"{roleName}: base state load fallback ({stateEx.Message}).");
                    }
                }
                executor = new InteractiveExecutor(context);
                LogActivity($"{roleName}: Session ready with native template.");
            }
            // User cancellation (Stop button) during model/context init is NOT a GPU
            // failure — letting it fall into this handler reloaded the model CPU-only
            // and cached it that way, silently degrading every later run to CPU.
            catch (Exception ex) when (plan.UsingGpu && ex is not OperationCanceledException)
            {
                await BackendLogService.LogErrorAsync($"Workplace.{roleName}.GPUInit", ex);
                LogActivity($"{roleName}: GPU init failed — {ex.Message}. Falling back to CPU...");
                AppendChat("warning", $"{roleName}: GPU initialization failed, using CPU fallback.");

                if (_modelCache.ContainsKey(cacheKey))
                {
                    _modelCache[cacheKey].Dispose();
                    _modelCache.Remove(cacheKey);
                }

                var cpuPlan = InferenceBackendService.CreatePlan(config.ModelPath, GetRoleContextSize(role), InferenceComputeMode.CpuOnly);
                HardwareInfoBlock.Text = $"Hardware: RAM {cpuPlan.HardwareProfile.AvailableRamGb:F1} GB free | VRAM {cpuPlan.HardwareProfile.AvailableVramGb:F1} GB | Backend: {cpuPlan.BackendName}";

                long cpuIncomingBytes = GetModelFileSizeSafe(cacheKey);
                EvictModelCacheForLoad(cacheKey, cpuIncomingBytes);

                await InferenceBackendService.RunExclusiveAsync(async () =>
                {
                    model = await Task.Run(() => LLamaWeights.LoadFromFile(cpuPlan.Parameters), token);
                });

                _modelCache[cacheKey] = new CachedModelEntry
                {
                    ModelPath = config.ModelPath!,
                    Weights = model!,
                    GpuLayerCount = cpuPlan.Parameters.GpuLayerCount,
                    SizeBytes = cpuIncomingBytes
                };

                context = await Task.Run(() => LlamaContextFactory.CreateContext(model!, cpuPlan.Parameters), token);
                if (loadBaseState && baseStateVault != null && baseStateVault.RoleStatePaths.TryGetValue(role, out string? baseStatePath) && !string.IsNullOrWhiteSpace(baseStatePath))
                {
                    try
                    {
                        await Task.Run(() => context.LoadState(baseStatePath), token);
                    }
                    catch (Exception stateEx)
                    {
                        LogActivity($"{roleName}: base state load fallback on CPU ({stateEx.Message}).");
                    }
                }
                executor = new InteractiveExecutor(context);
                LogActivity($"{roleName}: CPU fallback session ready and cached.");
            }
            finally
            {
                // Clear the init forensics marker whether init succeeded or threw a catchable
                // error (GPU init falling back to CPU, or even a CPU init failure); only a true
                // native abort — process death, where no managed code runs — leaves it behind for
                // the next launch to record a strike and step the model down.
                NativeDecodeForensics.EndDecode();
            }

            if (executor == null || model == null)
            {
                throw new InvalidOperationException("Failed to initialize inference session.");
            }

            // Stamp the ACTUAL loaded backend for crash forensics. Read the layer count from the
            // cache entry, which reflects the real load (the GPU-init catch above can quietly fall
            // back to a CPU plan), so a native abort during this role's decode is attributed to the
            // right model+backend and recorded as a GPU strike on the next launch.
            int loadedGpuLayers = _modelCache.TryGetValue(cacheKey, out var loadedEntry) && loadedEntry != null
                ? loadedEntry.GpuLayerCount
                : plan.Parameters.GpuLayerCount;
            NativeDecodeForensics.SetActiveModel(config.ModelPath!, loadedGpuLayers > 0, loadedGpuLayers);

            // Use ChatSession + PromptTemplateTransformer so the model's native chat
            // template is applied and special tokens (<|im_start|>, <|im_end|>, etc.)
            // are tokenized as single special tokens — not split into characters.
            var session = new LLama.ChatSession(executor);
            session.WithHistoryTransform(new PromptTemplateTransformer(model, withAssistant: true));
            session.AddSystemMessage(systemPrompt);

            LogActivity($"{roleName}: Session ready with native template. Starting inference...");

            var (roleTemp, roleMinP) = GetRoleSamplingConfig(role);
            if (role == CouncilRole.Critic)
            {
                roleTemp = isQwen3
                    ? 0.7f
                    : _criticSensitivity switch
                    {
                        CriticSensitivityLevel.Strict => 0.35f,
                        CriticSensitivityLevel.CriticalOnly => 0.55f,
                        _ => 0.45f
                    };
            }
            if (temperatureOverride.HasValue)
            {
                roleTemp = temperatureOverride.Value;
            }
            if (useSubOneBMode)
            {
                roleTemp = Math.Min(roleTemp, 0.15f);
                roleMinP = Math.Min(roleMinP, 0.03f);
            }

            // ═══════════════════════════════════════════════
            // Context Budget Enforcement
            // Prevents NoKvSlot/InvalidInputBatch by ensuring prompt + generation fits in
            // the context window. Uses the model's REAL tokenizer — the old chars/4
            // estimate undercounted code-heavy payloads badly enough to overflow the
            // context and fail llama_decode mid-run.
            // ═══════════════════════════════════════════════
            int promptTokenEstimate = CountTokensWithContext(context, systemPrompt) + CountTokensWithContext(context, userPayload);
            int templateOverhead = 80; // chat template special tokens, role markers
            int contextBudget = (int)plan.Parameters.ContextSize;
            int availableForGeneration = contextBudget - promptTokenEstimate - templateOverhead;

            int minGenTokens = outputGrammar != null ? 64 : role switch
            {
                CouncilRole.Builder => 384,
                _ => 256
            };
            if (availableForGeneration < minGenTokens)
            {
                // Prompt fills most/all of the context — truncate user payload to fit
                int systemTokens = CountTokensWithContext(context, systemPrompt) + templateOverhead;
                int reserveForGeneration = Math.Max(minGenTokens, contextBudget / 4);
                int maxPayloadTokens = contextBudget - systemTokens - reserveForGeneration;

                // Convert the token budget to characters using the payload's actual ratio.
                double charsPerToken = userPayload.Length > 0
                    ? Math.Max(1.0, (double)userPayload.Length / Math.Max(1, CountTokensWithContext(context, userPayload)))
                    : AvgCharsPerToken;
                int maxPayloadChars = Math.Max(200, (int)(maxPayloadTokens * charsPerToken));

                if (userPayload.Length > maxPayloadChars)
                {
                    LogActivity($"{roleName}: Payload too large (~{promptTokenEstimate} tokens for ctx {contextBudget}). Truncating to {maxPayloadChars} chars.");
                    AppendChat("warning", $"{roleName}: Prompt trimmed to fit context window ({contextBudget} tokens).");
                    // Keep the TAIL, not the head: the user's actual request ([[USER PROMPT]]) and the
                    // closing answer anchor live at the end of the payload, while the head is front
                    // matter (pipeline header, recent conversation, prior knowledge). Discarding the tail
                    // was leaving the model with only framing text to continue — which is exactly why it
                    // echoed its role/environment instead of answering.
                    userPayload = "[...earlier context truncated to fit context window]\n" + userPayload[^maxPayloadChars..];
                    promptTokenEstimate = CountTokensWithContext(context, systemPrompt) + CountTokensWithContext(context, userPayload);
                    availableForGeneration = contextBudget - promptTokenEstimate - templateOverhead;
                }
            }

            int roleGenerationCap = role == CouncilRole.Builder ? 4096 : 2048;
            // A caller-supplied per-call ceiling (the segmented-document Builder uses this to keep each
            // segment concise) tightens the role cap, but never below the role's minimum so a fit-to-
            // context truncation still leaves room to answer.
            if (maxGenerationTokensOverride is int genCapOverride)
                roleGenerationCap = Math.Min(roleGenerationCap, Math.Max(minGenTokens, genCapOverride));
            if (useSubOneBMode)
                roleGenerationCap = Math.Min(roleGenerationCap, role == CouncilRole.Builder ? 1536 : 768);
            int maxGenTokens = Math.Clamp(availableForGeneration, minGenTokens, roleGenerationCap);
            LogActivity($"{roleName}: Context budget — prompt ~{promptTokenEstimate + templateOverhead}t, gen {maxGenTokens}t, ctx {contextBudget}t");

            PromptFormat activeFormat = config.Format;
            IReadOnlyList<PromptInjectionBlockInfo> priorInjectionInfos = CollectPromptInjectionInfos(_chatHistory);
            userPayload = ApplyPreInferenceContextReduction(userPayload, priorInjectionInfos, _lastRunContext?.UserPrompt ?? userPayload);

            // Cross-family turn terminators MUST be stop sequences here. The council path was
            // missing "<|im_end|>" (only normal chat had it), so a ChatML role that emitted its
            // turn-end token — naturally OR because the old contract told it to type one — did not
            // stop: it leaked the literal token into the message and kept generating, re-typing its
            // whole answer past the turn boundary until the token cap. Listing every family's
            // terminator is the cheap, decisive fix for both the leak and the runaway re-type loop.
            var antiPrompts = new List<string>
            {
                "<|im_end|>", "<|endoftext|>", "<|im_start|>", "<|eot_id|>", "<|start_header_id|>",
                "<end_of_turn>",          // Gemma 1/2/3
                "<|end_of_text|>",        // Llama 3 / Granite
                "<|end_of_role|>",        // Granite role turns
                "<|endofturn|>",          // misc ChatML variants
                "### Instruction:"
            };
            foreach (var ap in GetAntiPrompts(activeFormat))
            {
                if (!antiPrompts.Contains(ap, StringComparer.Ordinal))
                {
                    antiPrompts.Add(ap);
                }
            }

            var inferenceParams = CreateRoleInferenceParams(config.ModelPath ?? string.Empty, maxGenTokens, antiPrompts, roleTemp, roleMinP, outputGrammar, useSubOneBMode);

            bool strictChatMl = IsStrictChatMlModel(config.DisplayName);
            int tokenCount = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            void NoteGeneratedTokens()
            {
                RecordCouncilRoleGeneratedTokens(role, tokenCount);
            }

            async Task<string> ConsumeNativeStreamAsync(IAsyncEnumerable<string> stream)
            {
                var nativeOutput = new StringBuilder();
                int lastProgressLength = 0;
                await foreach (string piece in stream.WithCancellation(token).ConfigureAwait(false))
                {
                    nativeOutput.Append(piece);
                    tokenCount++;
                    _pipelineTokenCount++;
                    if (tokenCount % 8 == 0)
                        NoteGeneratedTokens();
                    if (onLiveTextProgress != null && nativeOutput.Length - lastProgressLength >= 48)
                    {
                        lastProgressLength = nativeOutput.Length;
                        onLiveTextProgress(nativeOutput.ToString());
                    }
                    if (nativeOutput.Length > 30_000)
                        break;
                }
                NoteGeneratedTokens();
                onLiveTextProgress?.Invoke(nativeOutput.ToString());
                return nativeOutput.ToString();
            }

            // Drop a crash-forensics marker for the duration of this role's native decode. If a
            // CUDA illegal-memory-access (or any native abort) kills the process here, the marker
            // survives to the next launch, which records a GPU strike so ResolveCouncilCrashRecovery
            // steps this model down. EndDecode (in the finally) removes it on any clean/cancelled exit,
            // so only a true process death leaves it behind — no false strikes from Stop.
            NativeDecodeForensics.BeginDecode($"Workplace.{roleName}", promptTokenEstimate + templateOverhead, contextBudget, config.DisplayName);
            try
            {
                if (_agenticPauseEngine == null)
                    throw new InvalidOperationException("AgenticPauseEngine not initialized.");

                if (strictChatMl)
                {
                    string strictPrompt = ApplyPreInferenceContextReduction(BuildPrompt(activeFormat, systemPrompt, userPayload), priorInjectionInfos, _lastRunContext?.UserPrompt ?? userPayload);

                    // Stream factory for strict ChatML: re-invokes InferAsync with the latest payload.
                    // On re-invocation after a pause, the payload contains the [RESULT:] injection.
                    // The decode gate is held only while the stream enumerates — never across
                    // tool dispatch, which can itself start nested inference.
                    IAsyncEnumerable<string> StrictStreamFactory(string payload)
                    {
                        string prompt = payload == userPayload
                            ? strictPrompt
                            : ApplyPreInferenceContextReduction(BuildPrompt(activeFormat, systemPrompt, payload), priorInjectionInfos, _lastRunContext?.UserPrompt ?? payload);
                        return InferenceBackendService.RunScopedExclusiveStream(
                            InferenceBackendService.WorkplaceCouncilScope,
                            () => executor.InferAsync(prompt, inferenceParams, token),
                            token);
                    }

                    string rawStrict = allowAgenticPauses
                        ? await _agenticPauseEngine.RunAsync(
                            StrictStreamFactory,
                            null,
                            executor,
                            inferenceParams,
                            systemPrompt,
                            userPayload,
                            delta => { tokenCount += delta; _pipelineTokenCount += delta; NoteGeneratedTokens(); },
                            token,
                            onLiveTextProgress)
                        : await ConsumeNativeStreamAsync(StrictStreamFactory(userPayload));

                    responseBuilder.Append(rawStrict);
                }
                else
                {
                    var chatMessage = new ChatHistory.Message(AuthorRole.User, userPayload);

                    // Stream factory for ChatSession: first call uses chatMessage,
                    // subsequent re-invocations (after pause) inject a new user message.
                    bool firstCall = true;
                    IAsyncEnumerable<string> ChatStreamFactory(string payload)
                    {
                        if (firstCall)
                        {
                            firstCall = false;
                            return InferenceBackendService.RunScopedExclusiveStream(
                                InferenceBackendService.WorkplaceCouncilScope,
                                () => session.ChatAsync(chatMessage, inferenceParams, token),
                                token);
                        }
                        var resumeMsg = new ChatHistory.Message(AuthorRole.User, ApplyPreInferenceContextReduction(payload, priorInjectionInfos, _lastRunContext?.UserPrompt ?? payload));
                        return InferenceBackendService.RunScopedExclusiveStream(
                            InferenceBackendService.WorkplaceCouncilScope,
                            () => session.ChatAsync(resumeMsg, inferenceParams, token),
                            token);
                    }

                    string rawChat = allowAgenticPauses
                        ? await _agenticPauseEngine.RunAsync(
                            ChatStreamFactory,
                            session,
                            executor,
                            inferenceParams,
                            systemPrompt,
                            userPayload,
                            delta => { tokenCount += delta; _pipelineTokenCount += delta; NoteGeneratedTokens(); },
                            token,
                            onLiveTextProgress)
                        : await ConsumeNativeStreamAsync(ChatStreamFactory(userPayload));

                    responseBuilder.Append(rawChat);
                }
            }
            catch (Exception ex) when (
                allowBatchRecovery &&
                (ex.Message.Contains("invalidinputBatch", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("invalid input batch", StringComparison.OrdinalIgnoreCase)
                 || ex.Message.Contains("llama_decode failed", StringComparison.OrdinalIgnoreCase)))
            {
                LogActivity($"{roleName}: decode batch mismatch detected. Retrying once with clean context (base state disabled).");
                await BackendLogService.LogErrorAsync($"Workplace.{roleName}.InvalidInputBatch", ex);
                return await ExecuteCouncilRoleAsync(
                    role,
                    systemPrompt,
                    userPayload,
                    token,
                    temperatureOverride,
                    null,
                    false,
                    false,
                    showLiveCard,
                    maxGenerationTokensOverride,
                    contextSizeOverride,
                    useBuilderToolDecision,
                    outputGrammar,
                    allowAgenticPauses,
                    internalInferenceStep);
            }
            catch (Exception ex) when (ex.Message.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase))
            {
                // Safety net: KV cache exhausted despite budget enforcement (token estimate was off)
                LogActivity($"{roleName}: KV cache exhausted (NoKvSlot) — prompt exceeded context window despite budget check.");
                AppendChat("error", $"{roleName}: Prompt exceeded model context window. Try a shorter prompt, remove documents, or clear session memory.");
                return ReasoningParser.Parse("[Error: Prompt exceeded context window. Output was not generated.]");
            }
            finally
            {
                sw.Stop();
                NativeDecodeForensics.EndDecode();
                context?.Dispose();
            }

            double tokPerSec = tokenCount / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
            NoteGeneratedTokens();
            LogActivity($"{role}: Inference complete — {tokenCount} tokens in {sw.Elapsed.TotalSeconds:F1}s ({tokPerSec:F1} tok/s)");

            string rawOutput = responseBuilder.ToString();
            string completionMarker = GetRoleCompletionMarker(role);
            bool hasHandoff = rawOutput.Contains(HandoffEndToken, StringComparison.Ordinal);
            bool hasCompletionMarker = !string.IsNullOrWhiteSpace(completionMarker)
                && rawOutput.Contains(completionMarker, StringComparison.Ordinal);

            if (!hasHandoff && hasCompletionMarker)
            {
                LogActivity($"{roleName}: handoff token missing, accepted via completion marker '{completionMarker}'.");
                if (!internalInferenceStep && (role == CouncilRole.Architect || role == CouncilRole.Critic))
                    AppendChat("warning", $"{roleName}: handoff marker was missing; accepted via completion marker normalization.");
            }
            else if (!hasHandoff && !hasCompletionMarker)
            {
                if (rawOutput.Length < 64)
                {
                    LogActivity($"{roleName}: completion markers missing on short output; downstream normalization may retry.");
                }
                else if (!internalInferenceStep && (role == CouncilRole.Architect || role == CouncilRole.Critic))
                {
                    AppendChat("warning", $"{roleName}: required termination marker was missing; accepted through normalization.");
                }
            }

            rawOutput = rawOutput.Replace(HandoffEndToken, "", StringComparison.Ordinal);
            string cleaned = CleanModelOutput(rawOutput);
            return ReasoningParser.Parse(cleaned, isGemma4);
        }

        private static bool IsStrictChatMlModel(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return false;
            }

            return displayName.Contains("NVIDIA-Nemotron-3-Nano-4B-Q4_K_M", StringComparison.OrdinalIgnoreCase)
                   || displayName.Contains("Qwen2.5-4B", StringComparison.OrdinalIgnoreCase)
                   || displayName.Contains("Qwen3", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGemma4Model(string modelNameOrPath)
        {
            if (string.IsNullOrWhiteSpace(modelNameOrPath))
            {
                return false;
            }

            return modelNameOrPath.Contains("gemma-4", StringComparison.OrdinalIgnoreCase)
                   || modelNameOrPath.Contains("gemma4", StringComparison.OrdinalIgnoreCase)
                   || modelNameOrPath.Contains("gemma_4", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMmprojFile(string modelPath)
        {
            string file = Path.GetFileName(modelPath);
            return file.StartsWith("mmproj", StringComparison.OrdinalIgnoreCase)
                   || file.Contains("mmproj-", StringComparison.OrdinalIgnoreCase);
        }

        private static class Gemma4Formatter
        {
            private const string TurnOpen = "<start_of_turn>";
            private const string TurnClose = "<end_of_turn>";

            // Gemma's chat format uses <start_of_turn>/<end_of_turn> and has NO system role — the
            // system text is folded into the first user turn (exactly as llama.cpp's built-in gemma
            // template does). The previous implementation emitted invented tokens (<|turn>, <turn|>,
            // <|channel|>thought) that the tokenizer does not recognize as turn boundaries, so the
            // model never saw a turn structure and continued/echoed the system text instead of
            // answering the request.
            public static string BuildPrompt(string systemPrompt, string userPayload)
            {
                var sb = new StringBuilder();
                sb.Append(TurnOpen).Append("user\n");
                if (!string.IsNullOrWhiteSpace(systemPrompt))
                    sb.Append(systemPrompt.Trim()).Append("\n\n");
                sb.Append((userPayload ?? string.Empty).Trim());
                sb.Append(TurnClose).Append('\n');
                sb.Append(TurnOpen).Append("model\n");
                return sb.ToString();
            }
        }

        private string[] GetAntiPrompts(PromptFormat format)
        {
            return format switch
            {
                PromptFormat.Gemma4 => new[] { "<end_of_turn>" },
                PromptFormat.Llama3 => new[] { "<|eot_id|>", "<|start_header_id|>" },
                PromptFormat.Alpaca => new[] { "### Instruction:", "### Input:" },
                _ => new[] { "<|im_start|>" }
            };
        }

        private string BuildPrompt(PromptFormat format, string systemPrompt, string userPayload)
        {
            return format switch
            {
                PromptFormat.Gemma4 => Gemma4Formatter.BuildPrompt(systemPrompt, userPayload),
                PromptFormat.Llama3 =>
                    $"<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n{systemPrompt}<|eot_id|><|start_header_id|>user<|end_header_id|>\n{userPayload}<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n",
                PromptFormat.Alpaca =>
                    $"### Instruction:\n{systemPrompt}\n\n### Input:\n{userPayload}\n\n### Response:\n",
                _ =>
                    $"<|im_start|>system\n{systemPrompt}<|im_end|>\n<|im_start|>user\n{userPayload}<|im_end|>\n<|im_start|>assistant\n"
            };
        }

        // Resolves the model a role actually runs with. A role with no dedicated model borrows
        // another loaded council model (Builder prefers the Architect's, etc.) instead of silently
        // skipping its stage — the old behavior made the Builder appear to "skip" whenever only
        // one or two models were loaded. Borrowing is cheap: the model cache is keyed by path.
        private CouncilModelConfig GetEffectiveRoleConfig(CouncilRole role)
        {
            CouncilModelConfig own = _council[role];
            if (!string.IsNullOrWhiteSpace(own.ModelPath))
                return own;

            CouncilRole[] borrowOrder = role switch
            {
                CouncilRole.Builder => new[] { CouncilRole.Architect, CouncilRole.Critic },
                CouncilRole.Architect => new[] { CouncilRole.Builder, CouncilRole.Critic },
                _ => new[] { CouncilRole.Architect, CouncilRole.Builder }
            };

            foreach (CouncilRole other in borrowOrder)
            {
                CouncilModelConfig candidate = _council[other];
                if (!string.IsNullOrWhiteSpace(candidate.ModelPath))
                    return candidate;
            }

            return own;
        }

        private bool HasEffectiveLocalRoleModel(CouncilRole role)
            => !string.IsNullOrWhiteSpace(GetEffectiveRoleConfig(role).ModelPath);

        // Detects tagless chain-of-thought prose ("We need to...", "The user wants...") that some
        // models emit instead of an artifact. Such text must never be rendered in the canvas.
        private static bool LooksLikeReasoningProse(string output)
        {
            if (string.IsNullOrWhiteSpace(output))
                return false;

            string head = output.TrimStart();
            if (head.Length > 200)
                head = head[..200];
            head = head.ToLowerInvariant();

            string[] reasoningOpeners =
            [
                "we need to", "we need a", "we should", "we want to",
                "the user wants", "the user is asking", "the user asked", "the user has asked",
                "let me think", "let's think", "let me start", "let me figure",
                "i need to", "i should", "okay, so", "okay so", "ok, so",
                "hmm,", "hmm.", "so the user", "first, i", "thinking:",
                "to solve this, i", "the task is to", "my plan is"
            ];

            return reasoningOpeners.Any(o => head.StartsWith(o, StringComparison.Ordinal));
        }

        // Builds the live preview text for a local streaming card. Closed <think> blocks are
        // stripped; while the model is inside an unclosed think block the card shows a labeled
        // reasoning tail instead of dumping the raw chain-of-thought as if it were the answer.
        private static string BuildLiveStreamPreview(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return "⏳ Generating...";

            // The live card renders raw stream tokens before the final-output cleaner runs, so strip
            // any turn-terminator/role tokens here too — this is where the user saw "<|im_end|>" leak.
            raw = StripSpecialTokenText(raw);

            int openIdx = raw.LastIndexOf("<think>", StringComparison.OrdinalIgnoreCase);
            int closeIdx = raw.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            bool insideThinking = openIdx >= 0 && closeIdx < openIdx;

            if (insideThinking)
            {
                string thinking = raw[(openIdx + "<think>".Length)..].Trim();
                return "🧠 Reasoning...\n" + TailForPreview(thinking, 500);
            }

            string visible = ReasoningParser.StripThinkTags(raw).Trim();
            if (string.IsNullOrWhiteSpace(visible))
                return "⏳ Generating...";
            return TailForPreview(visible, 1600);
        }

        private static string TailForPreview(string text, int maxChars)
            => text.Length <= maxChars ? text : "…" + text[^maxChars..];

        // Removes streaming cards that were never finalized (relay aborted/stopped mid-stage)
        // so the chat doesn't keep a stuck "Generating..." card.
        private void FinalizeOrphanStreamingCards()
        {
            if (_streamingCouncilCards.Count == 0)
                return;

            foreach (var entry in _streamingCouncilCards.ToList())
            {
                if (string.IsNullOrWhiteSpace(entry.Value.Content)
                    || entry.Value.Content.StartsWith("⏳", StringComparison.Ordinal))
                {
                    _chatCards.Remove(entry.Value);
                }
            }
            _streamingCouncilCards.Clear();
        }

        // A reasoning-only fallback (model emitted chain-of-thought but no final content) is prose, never
        // a renderable artifact. Dumping it into the Project Canvas produces walls of thinking text instead
        // of a tool/visual, so suppress it from the canvas and keep it hidden. Only a genuinely renderable
        // code artifact (HTML/SVG/Chart/Interactive JS) is allowed through — the Markdown "Document"
        // preview is excluded because chain-of-thought with a few headings qualifies as a Document,
        // which is exactly how walls of thinking text were ending up rendered in the canvas.
        private bool ShouldSuppressReasoningFallbackFromCanvas(string builderOutput, bool reasoningFallback)
        {
            bool looksLikeReasoning = reasoningFallback || LooksLikeReasoningProse(builderOutput);
            if (!looksLikeReasoning)
                return false;

            ArtifactKind kind = ArtifactRenderService.DetectForCanvas(builderOutput, null).Kind;
            return kind is not (ArtifactKind.Html or ArtifactKind.Svg or ArtifactKind.Chart or ArtifactKind.InteractiveJavaScript);
        }

        private void UpdateProjectCanvas(string content)
        {
            string cleanCode = StripChatFromCode(content);
            string previousSource = ProjectCanvasEditor.Text ?? string.Empty;

            if (!CanvasSourcesEquivalent(previousSource, cleanCode))
            {
                ExitCanvasDiffView();
                bool shouldCaptureDiff = _activeCouncilRunContext?.IsProjectCanvasIteration == true
                    && CanvasHasRealContent(previousSource);
                if (shouldCaptureDiff)
                    CaptureCanvasDiff(previousSource, cleanCode);
                else
                    ClearCanvasDiff();
            }

            ProjectCanvasEditor.Text = cleanCode;
            if (_activeCouncilRunContext != null)
                _activeCouncilRunContext.BuilderRoutedToCanvas = true;

            string language = DetectLanguage(cleanCode);
            SetCanvasHighlighting(language);
            RefreshCanvasArtifact(content, _lastSandboxOutput);
            UpdateWorkplaceTokenUsageIndicator();
            _ = QueueWorkspaceStateSaveAsync();
        }

        private void CaptureCanvasDiff(string original, string revised)
        {
            _canvasDiffBaseSource = original ?? string.Empty;
            _canvasDiffCurrentSource = revised ?? string.Empty;
            IReadOnlyList<LineDiffEntry> changes = LineDiff.Build(_canvasDiffBaseSource, _canvasDiffCurrentSource);
            _canvasDiffAdditionCount = changes.Count(change => change.Kind == LineDiffKind.Added);
            _canvasDiffRemovalCount = changes.Count(change => change.Kind == LineDiffKind.Removed);

            bool hasChanges = _canvasDiffAdditionCount > 0 || _canvasDiffRemovalCount > 0;
            ShowDiffButton.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
            CanvasDiffSummaryBlock.Text = hasChanges
                ? $"Latest revision: +{_canvasDiffAdditionCount} / -{_canvasDiffRemovalCount} lines"
                : string.Empty;
            CanvasDiffSummaryBlock.Visibility = hasChanges ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ClearCanvasDiff()
        {
            _canvasDiffBaseSource = string.Empty;
            _canvasDiffCurrentSource = string.Empty;
            _canvasDiffAdditionCount = 0;
            _canvasDiffRemovalCount = 0;
            _isDiffViewActive = false;
            DiffViewerLines.Items.Clear();
            DiffViewerScroller.Visibility = Visibility.Collapsed;
            ShowDiffButton.Visibility = Visibility.Collapsed;
            ShowDiffButton.Content = "Diff";
            CanvasDiffSummaryBlock.Text = string.Empty;
            CanvasDiffSummaryBlock.Visibility = Visibility.Collapsed;
        }

        private void ExitCanvasDiffView()
        {
            if (!_isDiffViewActive)
                return;

            _isDiffViewActive = false;
            DiffViewerScroller.Visibility = Visibility.Collapsed;
            ShowDiffButton.Content = "Diff";
        }

        private async Task EnsureCanvasArtifactWebViewInitializedAsync()
        {
            if (_canvasArtifactWebViewReady)
                return;

            try
            {
                var webView = FindName("CanvasArtifactWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
                if (webView == null)
                    return;

                var webViewEnvironment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppDataPaths.WebView2UserData,
                    options: WebView2GpuPolicy.CreateEnvironmentOptions());
                await webView.EnsureCoreWebView2Async(webViewEnvironment);
                webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                webView.CoreWebView2.Settings.IsZoomControlEnabled = false;

                // Solid backdrop matching the canvas pane (NotebookBackgroundBrush #171615).
                // A fully transparent default background renders as a black void on some GPU
                // configurations — the "black screen" artifact — so we paint a real color instead.
                webView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xFF, 0x17, 0x16, 0x15);

                // If a navigation fails (e.g. the surface was not ready), retry once with the
                // last source so transient races don't leave a blank pane.
                webView.CoreWebView2.NavigationCompleted += (_, args) =>
                {
                    if (args.IsSuccess)
                    {
                        _canvasArtifactNavRetried = false;
                        _canvasArtifactNavOk = true;
                        return;
                    }
                    if (!_canvasArtifactNavRetried && !string.IsNullOrEmpty(_canvasArtifactNavSource))
                    {
                        _canvasArtifactNavRetried = true;
                        try { webView.NavigateToString(_canvasArtifactNavSource); } catch { }
                    }
                };
                _canvasArtifactWebViewReady = true;
            }
            catch
            {
                _canvasArtifactWebViewReady = false;
            }
        }

        private void RefreshCanvasArtifact(string builderOutput, string? sandboxOutput)
        {
            _canvasArtifact = ArtifactRenderService.DetectForCanvas(builderOutput, sandboxOutput);
            // Default a renderable artifact straight to preview mode. Doing this BEFORE the UI
            // refresh guarantees the preview host (and its WebView2) is visible/realized at the
            // moment we navigate, which was the root cause of the intermittent blank canvas.
            _isCanvasPreviewMode = _canvasArtifact.SupportsPreview && !_connectedWorkspace.CodebaseEditAccessEnabled;

            RefreshCanvasArtifactUi();
            _ = RenderCanvasArtifactPreviewAsync();
        }

        private async Task RenderCanvasArtifactPreviewAsync()
        {
            if (!_canvasArtifact.SupportsPreview)
                return;

            await EnsureCanvasArtifactWebViewInitializedAsync();
            var webView = FindName("CanvasArtifactWebView") as Microsoft.Web.WebView2.Wpf.WebView2;
            if (!_canvasArtifactWebViewReady || webView?.CoreWebView2 == null)
                return;

            string html = _canvasArtifact.RenderSource ?? string.Empty;

            // Skip a redundant reload when the pane is already showing this exact content
            // (e.g. toggling Code/Preview, or a refresh with identical output) — avoids flicker.
            if (_canvasArtifactNavOk && string.Equals(html, _canvasArtifactNavSource, StringComparison.Ordinal))
                return;

            _canvasArtifactNavSource = html;
            _canvasArtifactNavRetried = false;
            _canvasArtifactNavOk = false;

            try
            {
                // NavigateToString silently fails for very large payloads (the WebView2 string
                // limit is ~2 MB). For big artifacts, stage the HTML to a temp file and navigate
                // to it instead so the design still renders rather than showing a blank pane.
                if (System.Text.Encoding.UTF8.GetByteCount(html) > 1_400_000)
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), "axiom_canvas_artifact.html");
                    await File.WriteAllTextAsync(tempPath, html, System.Text.Encoding.UTF8);
                    webView.CoreWebView2.Navigate(new Uri(tempPath).AbsoluteUri);
                }
                else
                {
                    webView.NavigateToString(html);
                }
            }
            catch
            {
                try { webView.NavigateToString(html); } catch { }
            }
        }

        private void RefreshCanvasArtifactUi()
        {
            bool hasArtifact = _canvasArtifact.SupportsPreview;
            bool suppressPreviewForCodebase = _connectedWorkspace.CodebaseEditAccessEnabled;
            if (suppressPreviewForCodebase)
                _isCanvasPreviewMode = false;

            bool showNativePreview = !_suppressCanvasNativePreviewForOverlay && !_isDiffViewActive && hasArtifact && _isCanvasPreviewMode;
            if (FindName("CanvasArtifactTogglePanel") is StackPanel togglePanel)
                togglePanel.Visibility = hasArtifact && !suppressPreviewForCodebase ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("CopyCanvasArtifactSourceButton") is Button copyButton)
                copyButton.Visibility = hasArtifact ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("SaveCanvasArtifactButton") is Button saveButton)
                saveButton.Visibility = hasArtifact ? Visibility.Visible : Visibility.Collapsed;
            if (FindName("CanvasCodeViewButton") is Button codeButton)
                codeButton.Opacity = !_isCanvasPreviewMode ? 1.0 : 0.65;
            if (FindName("CanvasPreviewViewButton") is Button previewButton)
            {
                previewButton.Opacity = _isCanvasPreviewMode ? 1.0 : 0.65;
                previewButton.Visibility = suppressPreviewForCodebase ? Visibility.Collapsed : Visibility.Visible;
            }
            if (FindName("CanvasArtifactPreviewHost") is Grid previewHost)
                previewHost.Visibility = showNativePreview ? Visibility.Visible : Visibility.Collapsed;
            ProjectCanvasEditor.Visibility = _isDiffViewActive || (hasArtifact && _isCanvasPreviewMode)
                ? Visibility.Collapsed
                : Visibility.Visible;
            DiffViewerScroller.Visibility = _isDiffViewActive ? Visibility.Visible : Visibility.Collapsed;
            CanvasSubtitleBlock.Text = _isDiffViewActive
                ? "Source diff — red removed, green added"
                : hasArtifact
                ? _isCanvasPreviewMode
                    ? _canvasArtifact.DisplayTitle
                    : $"{_canvasArtifact.DisplayTitle} — code view"
                : "Builder output rendered here.";
            UpdateCanvasHeaderLayout();
        }

        private bool HasCanvasDiffAvailable()
        {
            return !string.IsNullOrWhiteSpace(_canvasDiffBaseSource)
                && !string.IsNullOrWhiteSpace(_canvasDiffCurrentSource)
                && (_canvasDiffAdditionCount > 0 || _canvasDiffRemovalCount > 0);
        }

        private void UpdateCanvasHeaderLayout()
        {
            if (!IsLoaded)
                return;

            double paneWidth = ProjectCanvasPane.ActualWidth;
            if (paneWidth <= 0 || double.IsNaN(paneWidth))
                paneWidth = ProjectCanvasPane.Width > 0 && !double.IsNaN(ProjectCanvasPane.Width)
                    ? ProjectCanvasPane.Width
                    : ProjectCanvasPane.MinWidth;

            bool compact = paneWidth < 600;
            bool narrow = paneWidth < 500;
            bool veryNarrow = paneWidth < 420;
            bool hasDiff = HasCanvasDiffAvailable();

            Thickness buttonMargin = compact
                ? new Thickness(0, 0, 5, 0)
                : new Thickness(0, 0, 8, 0);
            CanvasArtifactTogglePanel.Margin = compact
                ? new Thickness(0, 0, 5, 0)
                : new Thickness(0, 0, 8, 0);
            CopyCanvasButton.Margin = buttonMargin;
            RunCodeButton.Margin = buttonMargin;
            ShowDiffButton.Margin = buttonMargin;
            CodeOutputToggleButton.Margin = buttonMargin;
            CanvasMoreActionsButton.Margin = buttonMargin;

            SetCanvasHeaderButtonSize(CanvasCodeViewButton, veryNarrow ? 48 : compact ? 54 : double.NaN, veryNarrow ? 48 : 58, veryNarrow ? "Code" : "Code");
            SetCanvasHeaderButtonSize(CanvasPreviewViewButton, veryNarrow ? 58 : compact ? 68 : double.NaN, veryNarrow ? 58 : 78, veryNarrow ? "Prev" : "Preview");
            SetCanvasHeaderButtonSize(RunCodeButton, veryNarrow ? 54 : compact ? 62 : double.NaN, veryNarrow ? 54 : 72, compact ? "Run" : "▶ Run");

            CopyCanvasButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            CodeOutputToggleButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
            ShowDiffButton.Visibility = hasDiff && !narrow ? Visibility.Visible : Visibility.Collapsed;

            CanvasMoreCopyItem.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            CanvasMoreOutputItem.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
            CanvasMoreDiffItem.Visibility = hasDiff && narrow ? Visibility.Visible : Visibility.Collapsed;
            CanvasMoreOutputItem.Header = _isCodeOutputExpanded ? "Hide output" : "Show output";
            CanvasMoreActionsButton.Visibility =
                CanvasMoreCopyItem.Visibility == Visibility.Visible
                || CanvasMoreOutputItem.Visibility == Visibility.Visible
                || CanvasMoreDiffItem.Visibility == Visibility.Visible
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            CanvasSubtitleBlock.Visibility = veryNarrow ? Visibility.Collapsed : Visibility.Visible;
            CanvasDiffSummaryBlock.Visibility = veryNarrow || !hasDiff ? Visibility.Collapsed : Visibility.Visible;
            CanvasTitleBlock.FontSize = veryNarrow ? 13 : 14;
        }

        private static void SetCanvasHeaderButtonSize(Button button, double width, double minWidth, object content)
        {
            if (double.IsNaN(width))
                button.ClearValue(WidthProperty);
            else
                button.Width = width;

            button.MinWidth = minWidth;
            button.Content = content;
        }

        public void SetNativePreviewSuppressedForOverlay(bool suppressed)
        {
            if (_suppressCanvasNativePreviewForOverlay == suppressed)
                return;

            _suppressCanvasNativePreviewForOverlay = suppressed;
            RefreshCanvasArtifactUi();
        }

        private void CanvasCodeViewButton_Click(object sender, RoutedEventArgs e)
        {
            ExitCanvasDiffView();
            _isCanvasPreviewMode = false;
            RefreshCanvasArtifactUi();
        }

        private void CanvasPreviewViewButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_canvasArtifact.SupportsPreview)
                return;

            ExitCanvasDiffView();
            _isCanvasPreviewMode = true;
            _ = RenderCanvasArtifactPreviewAsync();
            RefreshCanvasArtifactUi();
        }

        private void CopyCanvasArtifactSourceButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_canvasArtifact.SupportsPreview)
                return;

            try
            {
                Clipboard.SetText(_canvasArtifact.RawSource ?? string.Empty);
                AppendChat("system", "Artifact source copied.");
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Artifact copy failed: {ex.Message}");
            }
        }

        private async void SaveCanvasArtifactButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_canvasArtifact.SupportsPreview)
                return;

            var dialog = new SaveFileDialog
            {
                DefaultExt = _canvasArtifact.SuggestedFileExtension,
                FileName = "project-canvas-artifact" + _canvasArtifact.SuggestedFileExtension,
                Filter = _canvasArtifact.Kind switch
                {
                    ArtifactKind.Html => "HTML files (*.html)|*.html|All files (*.*)|*.*",
                    ArtifactKind.Svg => "SVG files (*.svg)|*.svg|All files (*.*)|*.*",
                    ArtifactKind.Chart => "PNG files (*.png)|*.png|All files (*.*)|*.*",
                    ArtifactKind.Document => "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                    _ => "All files (*.*)|*.*"
                }
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                if (_canvasArtifact.Kind == ArtifactKind.Chart && !string.IsNullOrWhiteSpace(_canvasArtifact.BinaryBase64))
                    await File.WriteAllBytesAsync(dialog.FileName, Convert.FromBase64String(_canvasArtifact.BinaryBase64));
                else
                    await File.WriteAllTextAsync(dialog.FileName, _canvasArtifact.SaveContent ?? _canvasArtifact.RawSource ?? string.Empty);

                AppendChat("system", $"Artifact saved: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Artifact save failed: {ex.Message}");
            }
        }

        private static string DetectLanguage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return "markdown";

            string lower = content.ToLowerInvariant();
            Match typedFence = BuilderTypedCodeFenceRegex.Match(content);
            string? fencedLanguage = typedFence.Success
                ? NormalizeCodeLanguage(typedFence.Groups["language"].Value)
                : null;
            if (!string.IsNullOrWhiteSpace(fencedLanguage))
                return fencedLanguage;

            if (lower.Contains("<!doctype") || lower.Contains("<html")
                || (lower.Contains("<body") && lower.Contains("</body>")))
                return "html";

            if (lower.TrimStart().StartsWith("<svg") || lower.Contains("<svg ") || lower.Contains("<svg\n"))
                return "xml";

            if (lower.Contains("namespace ")
                || (lower.Contains("using ") && Regex.IsMatch(content, @"\b(?:class|record|struct|interface)\s+\w+"))
                || lower.Contains("console.writeline"))
                return "c#";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:def\s+\w+\s*\(|from\s+\S+\s+import\s+|import\s+\S+)|\bif\s+__name__\s*=="))
                return "python";

            if ((lower.Contains("public class ") && lower.Contains("public static void main"))
                || lower.Contains("system.out.println"))
                return "java";

            if (Regex.IsMatch(content, @"\b(?:interface|type)\s+\w+\s*[={]|:\s*(?:string|number|boolean)(?:\[\])?\s*[;,]", RegexOptions.IgnoreCase))
                return "typescript";

            if (Regex.IsMatch(content, @"\b(?:const|let|var)\s+[A-Za-z_$][\w$]*\s*=|\bfunction\s+[A-Za-z_$][\w$]*\s*\(|=>|document\.(?:getElementById|querySelector)", RegexOptions.IgnoreCase))
                return "javascript";

            if (Regex.IsMatch(content, @"(?is)^\s*(?:select\s+.+\s+from\s+|insert\s+into\s+|update\s+\S+\s+set\s+|create\s+(?:table|view|index)\s+)", RegexOptions.IgnoreCase))
                return "sql";

            if (lower.StartsWith("#!") || Regex.IsMatch(content, @"(?m)^\s*(?:export\s+\w+=|sudo\s+|apt(?:-get)?\s+|npm\s+|docker\s+|echo\s+\$)"))
                return "bash";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:param\s*\(|\$[A-Za-z_]\w*\s*=|Get-[A-Za-z]+|Set-[A-Za-z]+)"))
                return "powershell";

            if (lower.Contains("#include") || lower.Contains("std::"))
                return "cpp";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:fn\s+\w+\s*\(|use\s+\w+::)"))
                return "rust";

            if (Regex.IsMatch(content, @"(?m)^\s*(?:package\s+\w+|func\s+\w+\s*\()"))
                return "go";

            if ((lower.TrimStart().StartsWith("{") && lower.TrimEnd().EndsWith("}"))
                || (lower.TrimStart().StartsWith("[") && lower.TrimEnd().EndsWith("]")))
            {
                try
                {
                    using JsonDocument _ = JsonDocument.Parse(content);
                    return "json";
                }
                catch (JsonException)
                {
                }
            }

            if (Regex.IsMatch(content, "(?m)^\\s*(?:[.#]?[A-Za-z][\\w\\s>+~:#.\\[\\]=\"'-]*)\\s*\\{\\s*$")
                && content.Contains(':'))
                return "css";

            if (lower.Contains("<project") || lower.Contains("<window") || lower.TrimStart().StartsWith("<?xml"))
                return "xml";

            return "markdown";
        }

        private void SetCanvasHighlighting(string language)
        {
            var manager = HighlightingManager.Instance;
            string extension = language switch
            {
                "c#" => ".cs",
                "javascript" => ".js",
                "typescript" => ".ts",
                "python" => ".py",
                "cpp" => ".cpp",
                "bash" => ".sh",
                "powershell" => ".ps1",
                "yaml" => ".yaml",
                "markdown" => ".md",
                _ => "." + language
            };
            ProjectCanvasEditor.SyntaxHighlighting =
                manager.GetDefinitionByExtension(extension) ??
                manager.GetDefinition(language) ??
                manager.GetDefinition("Markdown") ??
                manager.GetDefinition("C#");
        }

        // Removes leaked chat-template special tokens (turn terminators / role headers) a model can
        // emit as literal text when its template is mismatched or it was over-instructed. Shared by
        // the final-output cleaner and the live streaming preview so neither shows raw tokens. The
        // earlier list only covered ChatML/Llama tags; Gemma (<end_of_turn>) and Granite
        // (<|end_of_role|>, <|end_of_text|>) leaks slipped through into visible role messages.
        private static string StripSpecialTokenText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            string stripped = text
                .Replace("<|im_end|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|im_start|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|endoftext|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|eot_id|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|start_header_id|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|end_header_id|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|begin_of_text|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|end_of_text|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|end_of_role|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|endofturn|>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<end_of_turn>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<start_of_turn>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<|turn>", "", StringComparison.OrdinalIgnoreCase)
                .Replace("<turn|>", "", StringComparison.OrdinalIgnoreCase);

            // Collapse padding-sentinel runs (e.g. Gemma's literal "<pad>") a mismatched-template or
            // degenerate model can stream as visible text — the "<pad><pad>…" council spam.
            return OpenRouterChatService.StripPadSentinelTokens(stripped);
        }

        private static string CleanModelOutput(string raw)
        {
            string cleaned = StripSpecialTokenText(raw).Trim();

            // Strip stray role headers the model may echo
            foreach (var tag in new[] { "system", "user", "assistant" })
            {
                cleaned = cleaned.Replace($"<|im_start|>{tag}", "", StringComparison.OrdinalIgnoreCase);
                cleaned = cleaned.Replace($"<|start_header_id|>{tag}<|end_header_id|>", "", StringComparison.OrdinalIgnoreCase);
            }

            // Strip echoed agentic-pause override markers — internal pipeline signals, never
            // user-visible content.
            cleaned = Regex.Replace(cleaned, @"\[SYSTEM OVERRIDE:[^\]]*\]", string.Empty, RegexOptions.IgnoreCase);

            // A runaway role can begin re-typing its answer; the stream guard cuts it off
            // mid-restart, leaving a duplicated tail. Drop it so the answer appears once.
            cleaned = TrimRepeatedRestartTail(cleaned.Trim());

            return cleaned.Trim();
        }

        // The stream-level runaway guard (AgenticPauseEngine.LooksLikeRunawayRepetition) stops a
        // role that starts re-typing its answer from the top, but it fires mid-restart — leaving a
        // trailing fragment that duplicates content already present above. If a long suffix of the
        // text also occurs verbatim earlier, the model was restarting; drop the duplicated tail so
        // the user (and the Project Canvas) sees the answer exactly once and the bounded retry does
        // not re-loop on a bloated payload. Conservative 200-char minimum: a verbatim suffix that
        // long almost never recurs in genuine output, so this only collapses real restarts.
        private static string TrimRepeatedRestartTail(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 600)
                return text;

            int n = text.Length;
            int maxSuffix = Math.Min(3000, n / 2);
            for (int suffixLen = maxSuffix; suffixLen >= 200; suffixLen -= 32)
            {
                int tailStart = n - suffixLen;
                string suffix = text.Substring(tailStart, suffixLen);
                // Match must lie entirely before the trailing suffix (no overlap).
                if (text.IndexOf(suffix, 0, tailStart, StringComparison.Ordinal) >= 0)
                    return text[..tailStart].TrimEnd();
            }

            return text;
        }

        private static string StripChatFromCode(string builderOutput)
        {
            if (string.IsNullOrWhiteSpace(builderOutput))
                return builderOutput;

            // If there's a fenced code block, extract only the code blocks
            var blocks = new List<string>();
            string remaining = builderOutput;

            while (true)
            {
                int fenceStart = remaining.IndexOf("```", StringComparison.Ordinal);
                if (fenceStart < 0) break;

                int lineEnd = remaining.IndexOf('\n', fenceStart);
                if (lineEnd < 0) break;

                int fenceClose = remaining.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
                if (fenceClose < 0) break;

                string codeContent = remaining[(lineEnd + 1)..fenceClose].Trim();
                if (codeContent.Length > 0)
                    blocks.Add(codeContent);

                remaining = remaining[(fenceClose + 3)..];
            }

            if (blocks.Count > 0)
                return string.Join("\n\n", blocks);

            // No fenced blocks found — strip common chat preamble patterns
            var lines = builderOutput.Split('\n').ToList();
            int firstCodeLine = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].TrimStart();
                if (IsLeadingChatLine(trimmed))
                    continue;
                firstCodeLine = i;
                break;
            }

            // Strip trailing chat after code
            int lastCodeLine = lines.Count - 1;
            for (int i = lines.Count - 1; i >= firstCodeLine; i--)
            {
                string trimmed = lines[i].TrimStart();
                if (IsTrailingChatLine(trimmed))
                    continue;
                lastCodeLine = i;
                break;
            }

            return string.Join('\n', lines.Skip(firstCodeLine).Take(lastCodeLine - firstCodeLine + 1));
        }

        private static bool IsLeadingChatLine(string trimmed)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
                return true;

            string[] leadingPatterns =
            [
                "Sure", "Here", "I'll", "I will", "I've", "I have",
                "Below", "Let me", "Of course", "Certainly",
                "The following", "The corrected", "The fixed", "The revised",
                "The updated", "Fixed version", "Corrected version",
                "Updated version", "Revised version",
                "Changes made", "After addressing", "After fixing",
                "Addressing the", "Based on the", "As requested",
                "Per the critic", "Following the critic"
            ];

            return leadingPatterns.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                || (trimmed.EndsWith(':') && trimmed.Length < 100 && !trimmed.Contains('('));
        }

        private static bool IsTrailingChatLine(string trimmed)
        {
            if (string.IsNullOrWhiteSpace(trimmed))
                return true;

            string[] trailingPatterns =
            [
                "This code", "This script", "This program", "This implementation",
                "Note:", "Note that", "I hope", "Let me know", "Feel free",
                "These changes", "The above", "All issues", "All findings",
                "I've addressed", "I have addressed", "I've fixed", "I have fixed",
                "Changes summary", "Summary of changes", "What changed",
                "The changes above", "This addresses", "This fixes",
                "This resolves", "This should"
            ];

            return trailingPatterns.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildKnowledgePacket(List<DocumentChunk> chunks, string? priorityConcept)
        {
            var packet = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(priorityConcept))
            {
                packet.AppendLine($"[PRIORITIZED CONCEPT] {priorityConcept}");
                packet.AppendLine("The references below are semantically prioritized for this concept.");
                packet.AppendLine();
            }

            if (chunks.Count == 0)
            {
                packet.Append("No uploaded references.");
                return packet.ToString();
            }

            for (int i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];
                packet.AppendLine($"[Reference {i + 1}: {chunk.FileName}]");
                packet.AppendLine(chunk.Content);
                packet.AppendLine();
            }

            return packet.ToString();
        }

        private void LogActivity(string message)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => LogActivity(message), DispatcherPriority.Background);
                return;
            }

            string line = $"{DateTime.Now:HH:mm:ss}  {message}";
            _activityLogs.Add(line);

            while (_activityLogs.Count > 120)
            {
                _activityLogs.RemoveAt(0);
            }
        }

        // Internal routing markers / cross-role tokens that must never reach the chat UI.
        private static readonly System.Text.RegularExpressions.Regex CouncilInternalLabelLineRegex =
            new(@"(?m)^[ \t>*#-]*\[\[[A-Z0-9 _/&\-]+\]\]\s*:?\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex CouncilPipelineHeaderLineRegex =
            new(@"(?m)^[ \t>*#]*PIPELINE STATE HEADER.*$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex CouncilPauseResultTokenRegex =
            new(@"\[(?:PAUSE|RESULT)\s*:[^\]]*\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex CouncilLeadingRoleHeaderRegex =
            new(@"^\s*(?:#+\s*)?(?:Architect|Builder|Critic|System|Assistant|User)\s*:\s*", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex CouncilExcessBlankLinesRegex =
            new(@"\n{3,}", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Display-only scrub for council role messages. Removes internal pipeline routing markers,
        /// pause/result tokens, and stray role headers that a model may echo, so one role's chat
        /// never reveals the internal plumbing or another role's framing. Does not touch the data
        /// sent through the pipeline — only what the user sees.
        /// </summary>
        private static string SanitizeCouncilDisplayText(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return content ?? string.Empty;

            string cleaned = CouncilInternalLabelLineRegex.Replace(content, string.Empty);
            cleaned = CouncilPipelineHeaderLineRegex.Replace(cleaned, string.Empty);
            cleaned = CouncilPauseResultTokenRegex.Replace(cleaned, string.Empty);
            cleaned = CouncilLeadingRoleHeaderRegex.Replace(cleaned, string.Empty);
            cleaned = CouncilExcessBlankLinesRegex.Replace(cleaned, "\n\n");

            string trimmed = cleaned.Trim();
            // Never let scrubbing blank out a message entirely — fall back to the original text.
            return string.IsNullOrWhiteSpace(trimmed) ? content : trimmed;
        }

        private static bool IsCouncilDisplayRole(string role) =>
            role is "architect" or "builder" or "critic" or "critic-final";

        private void AppendChat(string role, string content)
        {
            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.InvokeAsync(() => AppendChat(role, content), DispatcherPriority.Background);
                return;
            }

            if (IsCouncilDisplayRole(role))
                content = SanitizeCouncilDisplayText(content);

            // Finalize an existing streaming card for this role rather than adding a duplicate card.
            if (_streamingCouncilCards.TryGetValue(role, out WorkplaceChatMessage? streamCard))
            {
                streamCard.Content = content.Trim();
                _streamingCouncilCards.Remove(role);
                Dispatcher.InvokeAsync(() => ChatScrollViewer?.ScrollToEnd(), DispatcherPriority.Background);
                UpdateWorkplaceTokenUsageIndicator();
                RequestWorkspaceStateSave();
                return;
            }

            var message = new WorkplaceChatMessage { Role = role, Content = content.Trim() };

            if (NotificationRoles.Contains(role))
            {
                _systemNotifications.Add(message);
                while (_systemNotifications.Count > 200)
                    _systemNotifications.RemoveAt(0);

                _unreadNotificationCount++;
                Dispatcher.InvokeAsync(() =>
                {
                    NotificationBadge.Visibility = Visibility.Visible;
                    NotificationBadgeText.Text = _unreadNotificationCount > 99 ? "99+" : _unreadNotificationCount.ToString();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            else
            {
                _chatCards.Add(message);
                Dispatcher.InvokeAsync(() => ChatScrollViewer?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
            }

            UpdateWorkplaceTokenUsageIndicator();
            RequestWorkspaceStateSave();
        }

        private void RequestWorkspaceStateSave()
        {
            Interlocked.Exchange(ref _stateSaveRequested, 1);

            if (_isProcessing)
                return;

            if (Interlocked.CompareExchange(ref _stateSaveWorkerRunning, 1, 0) != 0)
                return;

            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        if (Interlocked.Exchange(ref _stateSaveRequested, 0) == 0)
                            break;

                        await Task.Delay(220);
                        await QueueWorkspaceStateSaveAsync();
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref _stateSaveWorkerRunning, 0);
                    if (Interlocked.Exchange(ref _stateSaveRequested, 0) == 1)
                    {
                        RequestWorkspaceStateSave();
                    }
                }
            });
        }

        private void NotificationToggle_Click(object sender, RoutedEventArgs e)
        {
            _isNotificationPanelOpen = !_isNotificationPanelOpen;
            NotificationDropdownPanel.Visibility = _isNotificationPanelOpen ? Visibility.Visible : Visibility.Collapsed;
            NotificationToggleArrow.Text = _isNotificationPanelOpen ? "\u25be" : "\u25b8";

            if (_isNotificationPanelOpen)
            {
                _unreadNotificationCount = 0;
                NotificationBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearNotifications_Click(object sender, RoutedEventArgs e)
        {
            _systemNotifications.Clear();
            _unreadNotificationCount = 0;
            NotificationBadge.Visibility = Visibility.Collapsed;
            RequestWorkspaceStateSave();
        }

        private void CopyWorkplaceMessage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not WorkplaceChatMessage msg)
                return;

            if (string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                return;

            try
            {
                Clipboard.SetText(msg.Content ?? string.Empty);
                AppendChat("system", "Copied response.");
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Copy failed: {ex.Message}");
            }
        }

        private void StartPipelineProgress()
        {
            _pipelineTokenCount = 0;
            _lastRoleGeneratedTokenCounts.Clear();
            _pipelineStopwatch.Restart();
            _progressTimer.Start();
            _lastArchitectDuration = 0;
            _lastBuilderDuration = 0;
            _lastCriticDuration = 0;
            _activeStageRole = null;
            _stageStopwatch.Reset();
            ProgressTimer_Tick(null, EventArgs.Empty);
        }

        private void StopPipelineProgress()
        {
            // Record timing for the last active stage
            if (_activeStageRole.HasValue && _stageStopwatch.IsRunning)
            {
                double elapsed = _stageStopwatch.Elapsed.TotalSeconds;
                switch (_activeStageRole.Value)
                {
                    case CouncilRole.Architect: _lastArchitectDuration = elapsed; break;
                    case CouncilRole.Builder: _lastBuilderDuration = elapsed; break;
                    case CouncilRole.Critic: _lastCriticDuration = elapsed; break;
                }
            }
            _stageStopwatch.Reset();
            _activeStageRole = null;
            _progressTimer.Stop();
            _pipelineStopwatch.Stop();
            PipelineProgressBlock.Text = "";
        }

        private void ProgressTimer_Tick(object? sender, EventArgs e)
        {
            string etaLabel = ComputePipelineEtaLabel();
            PipelineProgressBlock.Text = $"\u23F1 {_pipelineStopwatch.Elapsed:mm\\:ss} | {_pipelineTokenCount} tokens{etaLabel}";
        }

        private string ComputePipelineEtaLabel()
        {
            if (_performanceLog.Count < 2 || !_activeStageRole.HasValue)
                return "";

            // Compute rolling average durations from last 5 runs
            var recentRuns = _performanceLog.Take(5).ToList();
            double avgArchitect = recentRuns.Where(r => r.ArchitectDurationSeconds > 0).Select(r => r.ArchitectDurationSeconds).DefaultIfEmpty(0).Average();
            double avgBuilder = recentRuns.Where(r => r.BuilderDurationSeconds > 0).Select(r => r.BuilderDurationSeconds).DefaultIfEmpty(0).Average();
            double avgCritic = recentRuns.Where(r => r.CriticDurationSeconds > 0).Select(r => r.CriticDurationSeconds).DefaultIfEmpty(0).Average();

            if (avgArchitect <= 0 && avgBuilder <= 0 && avgCritic <= 0)
                return "";

            double remainingSeconds = 0;
            double stageElapsed = _stageStopwatch.IsRunning ? _stageStopwatch.Elapsed.TotalSeconds : 0;

            switch (_activeStageRole.Value)
            {
                case CouncilRole.Architect:
                    remainingSeconds = Math.Max(0, avgArchitect - stageElapsed) + avgBuilder + avgCritic;
                    break;
                case CouncilRole.Builder:
                    remainingSeconds = Math.Max(0, avgBuilder - stageElapsed) + avgCritic;
                    break;
                case CouncilRole.Critic:
                    remainingSeconds = Math.Max(0, avgCritic - stageElapsed);
                    break;
            }

            if (remainingSeconds <= 2)
                return " | finishing";

            return remainingSeconds < 60
                ? $" | ~{remainingSeconds:F0}s remaining"
                : $" | ~{remainingSeconds / 60:F1}m remaining";
        }

        private void SavePersistedSession()
        {
            try
            {
                var snapshot = CaptureSessionSnapshotForPersistence();
                var advancedState = CaptureAdvancedStateSnapshotForPersistence();
                PersistCapturedWorkspaceState(snapshot, advancedState);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workplace session save failed: {ex.Message}");
            }
        }

        private WorkplaceSessionSnapshot CaptureSessionSnapshotForPersistence()
        {
            SessionHippocampusMetadata hippocampusMetadata = _sessionHippocampus.GetMetadata();
            var snapshot = new WorkplaceSessionSnapshot
            {
                ObjectiveText = "",
                ProjectCanvasText = ProjectCanvasEditor.Text,
                CloudModeEnabled = _isCloudModeEnabled,
                GlobalContextSize = _contextSize,
                ArchitectContextSize = _architectContextSize,
                BuilderContextSize = _builderContextSize,
                CriticContextSize = _criticContextSize,
                AutoOptimizeRoleContexts = _autoOptimizeRoleContexts,
                ChatCards = _chatCards.Where(c => !NotificationRoles.Contains(c.Role)).Select(c => new WorkplaceChatMessageDto
                {
                    Role = c.Role,
                    Content = c.Content,
                    Timestamp = c.Timestamp
                }).ToList(),
                SystemNotifications = new List<WorkplaceChatMessageDto>(),
                Documents = _documents.Select(d => new WorkplaceDocumentDto
                {
                    Name = d.Name,
                    FilePath = d.FilePath,
                    Type = d.Type,
                    Info = d.Info,
                    ChunkCount = d.ChunkCount
                }).ToList(),
                TaskHistory = _taskHistory.ToList(),
                PerformanceLog = _performanceLog.ToList(),
                IsRunStateIsolated = true,
                HippocampusEntries = _sessionHippocampus.ExportEntries(),
                StudySessionCompleted = hippocampusMetadata.StudySessionCompleted,
                StudySessionProcessedDocumentCount = _studySessionProcessedDocumentCount,
                CompletedCouncilRunCount = _completedCouncilRunCount,
                LastSandboxOutput = _lastSandboxOutput,
                LastFinalOutput = _lastFinalOutput,
                LastConfidenceLabel = _lastConfidenceLabel,
                CanvasDiffBaseSource = _canvasDiffBaseSource,
                CanvasDiffCurrentSource = _canvasDiffCurrentSource,
                CanvasDiffAdditionCount = _canvasDiffAdditionCount,
                CanvasDiffRemovalCount = _canvasDiffRemovalCount,
                ConnectedWorkspace = CloneConnectedWorkspaceState()
            };

            snapshot.CouncilModels["Architect"] = new WorkplaceCouncilModelDto
            {
                ModelPath = _council[CouncilRole.Architect].ModelPath ?? "",
                DisplayName = _council[CouncilRole.Architect].DisplayName,
                Format = _council[CouncilRole.Architect].Format.ToString(),
                UseCloud = _isCloudModeEnabled,
                CloudModelId = OpenRouterChatService.WorkplaceCouncilDefaultModelId
            };
            snapshot.CouncilModels["Builder"] = new WorkplaceCouncilModelDto
            {
                ModelPath = _council[CouncilRole.Builder].ModelPath ?? "",
                DisplayName = _council[CouncilRole.Builder].DisplayName,
                Format = _council[CouncilRole.Builder].Format.ToString(),
                UseCloud = _isCloudModeEnabled,
                CloudModelId = OpenRouterChatService.WorkplaceCouncilDefaultModelId
            };
            snapshot.CouncilModels["Critic"] = new WorkplaceCouncilModelDto
            {
                ModelPath = _council[CouncilRole.Critic].ModelPath ?? "",
                DisplayName = _council[CouncilRole.Critic].DisplayName,
                Format = _council[CouncilRole.Critic].Format.ToString(),
                UseCloud = _isCloudModeEnabled,
                CloudModelId = OpenRouterChatService.WorkplaceCouncilDefaultModelId
            };

            return snapshot;
        }

        private WorkspaceAdvancedStateSnapshot CaptureAdvancedStateSnapshotForPersistence()
        {
            return new WorkspaceAdvancedStateSnapshot
            {
                TaskHistory = _taskHistory.ToList(),
                Templates = _workspaceTemplates.ToList(),
                CriticSensitivity = _criticSensitivity.ToString(),
                PerformanceLog = _performanceLog.ToList()
            };
        }

        private void PersistCapturedWorkspaceState(WorkplaceSessionSnapshot snapshot, WorkspaceAdvancedStateSnapshot advancedState)
        {
            _workplacePersistence.Save(snapshot);
            _advancedStatePersistence.Save(advancedState);
        }

        private void SaveAdvancedState()
        {
            try
            {
                _advancedStatePersistence.Save(CaptureAdvancedStateSnapshotForPersistence());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workplace advanced state save failed: {ex.Message}");
            }
        }

        private void LoadAdvancedState()
        {
            try
            {
                var state = _advancedStatePersistence.Load();
                if (!string.IsNullOrWhiteSpace(_advancedStatePersistence.LastLoadStatusMessage))
                    Debug.WriteLine(_advancedStatePersistence.LastLoadStatusMessage);
                if (state == null)
                    return;

                bool restoreLegacyRunHistory = !_restoredIsolatedRunState
                    && _chatCards.Count > 0
                    && _taskHistory.Count == 0
                    && _performanceLog.Count == 0;
                if (restoreLegacyRunHistory)
                {
                    _taskHistory.Clear();
                    foreach (var e in state.TaskHistory.OrderByDescending(t => t.Timestamp))
                        _taskHistory.Add(e);
                }

                _workspaceTemplates.Clear();
                foreach (var t in state.Templates)
                    _workspaceTemplates.Add(t);

                if (restoreLegacyRunHistory)
                {
                    _performanceLog.Clear();
                    foreach (var e in state.PerformanceLog.OrderByDescending(p => p.Timestamp))
                        _performanceLog.Add(e);
                    UpdatePerformanceAggregate();
                }

                _criticSensitivity = Enum.TryParse<CriticSensitivityLevel>(state.CriticSensitivity, out var lvl)
                    ? lvl
                    : CriticSensitivityLevel.Standard;

                CriticSensitivityCombo.SelectedIndex = _criticSensitivity switch
                {
                    CriticSensitivityLevel.Strict => 1,
                    CriticSensitivityLevel.CriticalOnly => 2,
                    _ => 0
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workplace advanced state load failed: {ex.Message}");
            }
        }

        private void UpdateCriticSensitivityBadge()
        {
            CriticSensitivityBadgeText.Text = _criticSensitivity switch
            {
                CriticSensitivityLevel.Strict => "Strict",
                CriticSensitivityLevel.CriticalOnly => "Critical",
                _ => "Standard"
            };
        }

        private static string BuildConfidenceLabel(int criticFindings, bool revisionTriggered)
        {
            if (criticFindings <= 0)
                return "High Confidence";
            if (criticFindings <= 2)
                return "Moderate Confidence";
            return revisionTriggered ? "Reviewed and Revised" : "Flagged for Review";
        }

        private void ShowConfidenceLabel(string label)
        {
            _lastConfidenceLabel = label;
            CouncilConfidenceBanner.Visibility = Visibility.Visible;
            CouncilConfidenceText.Text = label;
            CouncilConfidenceText.Foreground = label switch
            {
                "High Confidence" => new SolidColorBrush(Color.FromRgb(34, 197, 94)),
                "Moderate Confidence" => new SolidColorBrush(Color.FromRgb(245, 158, 11)),
                "Reviewed and Revised" => new SolidColorBrush(Color.FromRgb(59, 130, 246)),
                "Flagged for Review" => new SolidColorBrush(Color.FromRgb(255, 59, 59)),
                _ => new SolidColorBrush(Color.FromRgb(122, 122, 114))
            };
            RefineButton.Visibility = Visibility.Visible;
        }

        private void AddTaskHistoryEntry(CouncilRunContext context, string finalResult, int criticFindingCount, Guid? parentId = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddTaskHistoryEntry(context, finalResult, criticFindingCount, parentId));
                return;
            }

            var entry = new CouncilTaskHistoryEntry
            {
                ParentId = parentId,
                UserPrompt = context.UserPrompt,
                Objective = context.Objective,
                TaskType = context.TaskType.ToString(),
                Complexity = context.Complexity.ToString(),
                ArchitectOutput = context.ArchitectOutput,
                BuilderOutput = context.BuilderOutput,
                CriticFindings = context.CriticReview,
                FinalResult = finalResult,
                Timestamp = DateTime.Now,
                RevisionTriggered = context.RevisionTriggered,
                CriticFindingCount = criticFindingCount,
                ConfidenceLabel = _lastConfidenceLabel
            };

            _taskHistory.Insert(0, entry);
            SaveAdvancedState();
        }

        private void AddPerformanceLogEntry(CouncilRunContext context, int criticFindingCount)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AddPerformanceLogEntry(context, criticFindingCount));
                return;
            }

            _performanceLog.Insert(0, new ModelPerformanceLogEntry
            {
                Timestamp = DateTime.Now,
                ArchitectModel = _council[CouncilRole.Architect].DisplayName,
                BuilderModel = _council[CouncilRole.Builder].DisplayName,
                CriticModel = _council[CouncilRole.Critic].DisplayName,
                ArchitectTokens = EstimateTokenCount(context.ArchitectOutput),
                BuilderTokens = EstimateTokenCount(context.BuilderOutput),
                CriticTokens = EstimateTokenCount(context.CriticReview),
                ArchitectDurationSeconds = _lastArchitectDuration,
                BuilderDurationSeconds = _lastBuilderDuration,
                CriticDurationSeconds = _lastCriticDuration,
                RevisionTriggered = context.RevisionTriggered,
                CriticFindingCountBeforeRevision = criticFindingCount,
                FinalConfidenceLabel = _lastConfidenceLabel,
                TaskType = context.TaskType.ToString()
            });
            UpdatePerformanceAggregate();
            SaveAdvancedState();
        }

        private void UpdatePerformanceAggregate()
        {
            if (_performanceLog.Count == 0)
            {
                PerformanceAggregateBlock.Text = "No runs logged yet.";
                return;
            }

            double avgFindings = _performanceLog.Average(p => p.CriticFindingCountBeforeRevision);
            double revisionRate = _performanceLog.Count(p => p.RevisionTriggered) * 100d / _performanceLog.Count;
            string topCombo = ChatAdvancedStatePersistence.GetMostFrequentModelCombo(_performanceLog);
            PerformanceAggregateBlock.Text = $"Runs: {_performanceLog.Count} | Avg Critic findings: {avgFindings:F2} | Revision rate: {revisionRate:F1}% | Most common models: {topCombo}";
        }

        private void CriticSensitivityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _criticSensitivity = CriticSensitivityCombo.SelectedIndex switch
            {
                1 => CriticSensitivityLevel.Strict,
                2 => CriticSensitivityLevel.CriticalOnly,
                _ => CriticSensitivityLevel.Standard
            };
            UpdateCriticSensitivityBadge();
            SaveAdvancedState();
        }

        private void RefineButton_Click(object sender, RoutedEventArgs e)
        {
            _isRefinementMode = true;
            RefinementModeBanner.Visibility = Visibility.Visible;
            QueryInput.Focus();
        }

        private void TaskHistoryListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _activeHistorySelection = TaskHistoryListBox.SelectedItem as CouncilTaskHistoryEntry;
            if (_activeHistorySelection == null)
            {
                TaskHistoryDetailBlock.Text = "Select a task history entry to view details.";
                return;
            }

            TaskHistoryDetailBlock.Text =
                $"Prompt: {_activeHistorySelection.UserPrompt}\n\n" +
                $"Objective: {_activeHistorySelection.Objective}\n\n" +
                $"Task Type: {_activeHistorySelection.TaskType} | Complexity: {_activeHistorySelection.Complexity}\n\n" +
                $"Architect:\n{_activeHistorySelection.ArchitectOutput}\n\n" +
                $"Builder:\n{_activeHistorySelection.BuilderOutput}\n\n" +
                $"Critic:\n{_activeHistorySelection.CriticFindings}\n\n" +
                $"Final:\n{_activeHistorySelection.FinalResult}";
        }

        private void ReplayTaskHistory_Click(object sender, RoutedEventArgs e)
        {
            if (_activeHistorySelection == null)
                return;

            QueryInput.Text = _activeHistorySelection.UserPrompt;
            QueryInput.Focus();
        }

        private void CompareTaskHistory_Click(object sender, RoutedEventArgs e)
        {
            var selected = TaskHistoryListBox.SelectedItems.Cast<CouncilTaskHistoryEntry>().Take(2).ToList();
            if (selected.Count < 2)
            {
                AppendChat("system", "Select two history entries to compare.");
                return;
            }

            string diff = BuildSimpleDiff(selected[0].FinalResult, selected[1].FinalResult);
            AppendChat("system", $"History Compare\nA: {selected[0].Timestamp:HH:mm:ss}\nB: {selected[1].Timestamp:HH:mm:ss}\n\n{diff}");
        }

        private static string BuildSimpleDiff(string left, string right)
        {
            var sb = new StringBuilder();
            foreach (LineDiffEntry entry in LineDiff.Build(left, right))
            {
                if (entry.Kind == LineDiffKind.Removed)
                    sb.AppendLine($"- {entry.Text}");
                else if (entry.Kind == LineDiffKind.Added)
                    sb.AppendLine($"+ {entry.Text}");
            }
            return sb.Length == 0 ? "No differences." : sb.ToString().Trim();
        }

        private void ShowDiffButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDiffViewActive)
            {
                ExitCanvasDiffView();
                _isCanvasPreviewMode = false;
                RefreshCanvasArtifactUi();
                return;
            }

            if (string.IsNullOrWhiteSpace(_canvasDiffBaseSource)
                || string.IsNullOrWhiteSpace(_canvasDiffCurrentSource))
            {
                AppendChat("system", "No Project Canvas revision diff is available yet.");
                return;
            }

            RenderInlineDiff(_canvasDiffBaseSource, _canvasDiffCurrentSource);
            _isCanvasPreviewMode = false;
            _isDiffViewActive = true;
            ShowDiffButton.Content = "Code";
            RefreshCanvasArtifactUi();
        }

        private void RenderInlineDiff(string original, string revised)
        {
            DiffViewerLines.Items.Clear();
            IReadOnlyList<LineDiffEntry> diff = LineDiff.Build(original, revised);
            if (diff.Count == 0)
                return;

            const int contextLines = 3;
            var visibleIndices = new HashSet<int>();
            for (int i = 0; i < diff.Count; i++)
            {
                if (diff[i].Kind == LineDiffKind.Unchanged)
                    continue;

                int start = Math.Max(0, i - contextLines);
                int end = Math.Min(diff.Count - 1, i + contextLines);
                for (int index = start; index <= end; index++)
                    visibleIndices.Add(index);
            }

            int previousVisibleIndex = -2;
            foreach (int index in visibleIndices.OrderBy(value => value))
            {
                if (index > previousVisibleIndex + 1)
                    AddDiffOmissionRow();

                AddDiffRow(diff[index]);
                previousVisibleIndex = index;
            }
        }

        private void AddDiffRow(LineDiffEntry entry)
        {
            var deletionBrush = new SolidColorBrush(Color.FromArgb(40, 255, 59, 59));
            var additionBrush = new SolidColorBrush(Color.FromArgb(40, 34, 197, 94));
            var deletionFg = new SolidColorBrush(Color.FromRgb(255, 120, 120));
            var additionFg = new SolidColorBrush(Color.FromRgb(120, 220, 140));
            var normalFg = new SolidColorBrush(Color.FromRgb(237, 232, 227));
            var lineNumFg = new SolidColorBrush(Color.FromRgb(138, 130, 121));
            var font = new FontFamily("Consolas");

            var panel = new DockPanel
            {
                Margin = new Thickness(0),
                Background = entry.Kind switch
                {
                    LineDiffKind.Removed => deletionBrush,
                    LineDiffKind.Added => additionBrush,
                    _ => Brushes.Transparent
                }
            };
            string oldNumber = entry.OldLineNumber?.ToString() ?? string.Empty;
            string newNumber = entry.NewLineNumber?.ToString() ?? string.Empty;
            panel.Children.Add(new TextBlock
            {
                Text = $"{oldNumber,4} {newNumber,4} ",
                FontFamily = font,
                FontSize = 11,
                Foreground = lineNumFg,
                Width = 74,
                TextAlignment = TextAlignment.Right
            });
            panel.Children.Add(new TextBlock
            {
                Text = (entry.Kind switch
                {
                    LineDiffKind.Removed => "− ",
                    LineDiffKind.Added => "+ ",
                    _ => "  "
                }) + entry.Text,
                FontFamily = font,
                FontSize = 12,
                Foreground = entry.Kind switch
                {
                    LineDiffKind.Removed => deletionFg,
                    LineDiffKind.Added => additionFg,
                    _ => normalFg
                },
                TextWrapping = TextWrapping.NoWrap
            });
            DiffViewerLines.Items.Add(panel);
        }

        private void AddDiffOmissionRow()
        {
            DiffViewerLines.Items.Add(new TextBlock
            {
                Text = "      ··· unchanged lines ···",
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(138, 130, 121)),
                Margin = new Thickness(0, 3, 0, 3)
            });
        }

        private void ClearTaskHistory_Click(object sender, RoutedEventArgs e)
        {
            _taskHistory.Clear();
            TaskHistoryDetailBlock.Text = "Task history cleared.";
            SaveAdvancedState();
        }

        private void CreateWorkspaceTemplate_Click(object sender, RoutedEventArgs e)
        {
            string name = $"Template {_workspaceTemplates.Count + 1}";
            var entry = new WorkspaceTemplateEntry
            {
                Name = name,
                Objective = ReadObjectiveText(),
                ArchitectModelPath = _council[CouncilRole.Architect].ModelPath ?? "",
                BuilderModelPath = _council[CouncilRole.Builder].ModelPath ?? "",
                CriticModelPath = _council[CouncilRole.Critic].ModelPath ?? "",
                ArchitectDisplayName = _council[CouncilRole.Architect].DisplayName,
                BuilderDisplayName = _council[CouncilRole.Builder].DisplayName,
                CriticDisplayName = _council[CouncilRole.Critic].DisplayName,
                TaskTypeOverride = ""
            };
            _workspaceTemplates.Add(entry);
            SaveAdvancedState();
        }

        private void WorkspaceTemplateListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
        }

        private void LoadWorkspaceTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (WorkspaceTemplateListBox.SelectedItem is not WorkspaceTemplateEntry tpl)
                return;

            _council[CouncilRole.Architect].ModelPath = string.IsNullOrWhiteSpace(tpl.ArchitectModelPath) ? null : tpl.ArchitectModelPath;
            _council[CouncilRole.Builder].ModelPath = string.IsNullOrWhiteSpace(tpl.BuilderModelPath) ? null : tpl.BuilderModelPath;
            _council[CouncilRole.Critic].ModelPath = string.IsNullOrWhiteSpace(tpl.CriticModelPath) ? null : tpl.CriticModelPath;
            _council[CouncilRole.Architect].DisplayName = string.IsNullOrWhiteSpace(tpl.ArchitectDisplayName) ? "No model selected" : tpl.ArchitectDisplayName;
            _council[CouncilRole.Builder].DisplayName = string.IsNullOrWhiteSpace(tpl.BuilderDisplayName) ? "No model selected" : tpl.BuilderDisplayName;
            _council[CouncilRole.Critic].DisplayName = string.IsNullOrWhiteSpace(tpl.CriticDisplayName) ? "No model selected" : tpl.CriticDisplayName;
            UpdateCouncilBlocks();
            SavePersistedSession();
        }

        private void EditWorkspaceTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (WorkspaceTemplateListBox.SelectedItem is not WorkspaceTemplateEntry tpl)
                return;

            tpl.Objective = ReadObjectiveText();
            tpl.ArchitectModelPath = _council[CouncilRole.Architect].ModelPath ?? "";
            tpl.BuilderModelPath = _council[CouncilRole.Builder].ModelPath ?? "";
            tpl.CriticModelPath = _council[CouncilRole.Critic].ModelPath ?? "";
            tpl.ArchitectDisplayName = _council[CouncilRole.Architect].DisplayName;
            tpl.BuilderDisplayName = _council[CouncilRole.Builder].DisplayName;
            tpl.CriticDisplayName = _council[CouncilRole.Critic].DisplayName;
            SaveAdvancedState();
        }

        private void DeleteWorkspaceTemplate_Click(object sender, RoutedEventArgs e)
        {
            if (WorkspaceTemplateListBox.SelectedItem is WorkspaceTemplateEntry tpl)
            {
                _workspaceTemplates.Remove(tpl);
                SaveAdvancedState();
            }
        }

        private void PerformanceLogListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PerformanceLogListBox.SelectedItem is not ModelPerformanceLogEntry p)
            {
                PerformanceLogDetailBlock.Text = "";
                return;
            }

            PerformanceLogDetailBlock.Text =
                $"Models: A={p.ArchitectModel}, B={p.BuilderModel}, C={p.CriticModel}\n" +
                $"Tokens: A={p.ArchitectTokens}, B={p.BuilderTokens}, C={p.CriticTokens}\n" +
                $"Revision: {(p.RevisionTriggered ? "Yes" : "No")} | Critic findings: {p.CriticFindingCountBeforeRevision} | Confidence: {p.FinalConfidenceLabel}";
        }

        private void ExportPerformanceLog_Click(object sender, RoutedEventArgs e)
        {
            string dir = Path.Combine(AppDataPaths.ChatHistory, "WorkplaceExports");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"performance_log_{DateTime.Now:yyyyMMdd_HHmmss}.csv");
            var sb = new StringBuilder();
            sb.AppendLine("Timestamp,TaskType,Confidence,Revision,CriticFindings,ArchitectModel,BuilderModel,CriticModel,ArchitectTokens,BuilderTokens,CriticTokens");
            foreach (var p in _performanceLog)
            {
                sb.AppendLine($"{p.Timestamp:o},{p.TaskType},{p.FinalConfidenceLabel},{p.RevisionTriggered},{p.CriticFindingCountBeforeRevision},{p.ArchitectModel},{p.BuilderModel},{p.CriticModel},{p.ArchitectTokens},{p.BuilderTokens},{p.CriticTokens}");
            }
            File.WriteAllText(path, sb.ToString());
            AppendChat("system", $"Performance log exported: {path}");
        }

        private async Task QueueWorkspaceStateSaveAsync()
        {
            try
            {
                if (!await _stateSaveGate.WaitAsync(0))
                    return;
                try
                {
                    WorkplaceSessionSnapshot? snapshot = null;
                    WorkspaceAdvancedStateSnapshot? advancedState = null;

                    await Dispatcher.InvokeAsync(() =>
                    {
                        snapshot = CaptureSessionSnapshotForPersistence();
                        advancedState = CaptureAdvancedStateSnapshotForPersistence();
                    }, DispatcherPriority.Background);

                    if (snapshot != null && advancedState != null)
                        await Task.Run(() => PersistCapturedWorkspaceState(snapshot, advancedState));
                }
                finally
                {
                    _stateSaveGate.Release();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workspace async save failed: {ex.Message}");
            }
        }

        private async Task ShowNonIntrusiveErrorAsync(string message)
        {
            await _notificationGate.WaitAsync();
            try
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    WorkplaceErrorNotificationText.Text = message;
                    WorkplaceErrorNotificationBar.Visibility = Visibility.Visible;
                });

                await Task.Delay(3200);

                await Dispatcher.InvokeAsync(() =>
                {
                    WorkplaceErrorNotificationBar.Visibility = Visibility.Collapsed;
                    WorkplaceErrorNotificationText.Text = string.Empty;
                });
            }
            finally
            {
                _notificationGate.Release();
            }
        }

        private void LoadPersistedSession()
        {
            try
            {
                var snapshot = _workplacePersistence.Load();
                if (!string.IsNullOrWhiteSpace(_workplacePersistence.LastLoadStatusMessage))
                    Debug.WriteLine(_workplacePersistence.LastLoadStatusMessage);
                if (snapshot == null)
                {
                    return;
                }

                RestoreSnapshot(snapshot);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Workplace session load failed: {ex.Message}");
            }
        }

        private static PromptFormat ParsePromptFormatByName(string? format)
        {
            return format?.Trim() switch
            {
                "Gemma4" or "Gemma 4" => PromptFormat.Gemma4,
                "Llama3" or "Llama 3" => PromptFormat.Llama3,
                "Alpaca" => PromptFormat.Alpaca,
                _ => PromptFormat.ChatML
            };
        }

        private void DisposeModelCache()
        {
            foreach (var entry in _modelCache.Values)
                entry.Dispose();
            _modelCache.Clear();
        }

        /// <summary>
        /// Releases all cached council models, freeing their VRAM/RAM. The normal chat
        /// surface calls this before loading its own model — otherwise leftover council
        /// weights consume the VRAM budget and the chat model's plan resolves to 0 GPU
        /// layers (pure CPU) even though the GPU is idle. No-op while the council is
        /// actively running.
        /// </summary>
        public void ReleaseCachedCouncilModels()
        {
            if (_isProcessing || _isStudySessionRunning || _modelCache.Count == 0)
                return;

            LogActivity($"Released {_modelCache.Count} cached council model(s) to free memory for the chat surface.");
            DisposeModelCache();
        }

        /// <summary>
        /// Clears any GPU crash strikes recorded against the configured council role models.
        /// Called when the user EXPLICITLY re-selects GPU mode in Settings — the deliberate
        /// "retry the GPU" signal. ResolveCouncilCrashRecovery steps a struck model down to CPU
        /// and the council never auto-clears strikes (a per-turn clear would oscillate GPU↔CPU),
        /// so without this an explicit GPU re-select cleared only the normal-chat model and left
        /// the council pinned to CPU with no recovery. Mirrors the normal-chat clear in
        /// ReloadModelWithCurrentModeAsync.
        /// </summary>
        public void ClearCouncilGpuStrikes()
        {
            foreach (var cfg in _council.Values)
            {
                if (!string.IsNullOrWhiteSpace(cfg?.ModelPath))
                    NativeCrashLedger.RegisterCleanRun(cfg.ModelPath!);
            }
        }

        private async void StudySessionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isStudySessionRunning)
            {
                _studySessionCancelRequested = true;
                StudySessionStatusText.Text = "Cancelling Study Session...";
                StudySessionDetailText.Text = "Cancellation requested. Current chunk will complete before stop.";
                return;
            }

            await StartStudySessionAsync();
        }

        private async Task StartStudySessionAsync()
        {
            if (_isStudySessionRunning)
                return;

            bool shouldAutoHideNotification = false;
            int autoHideDelayMs = 2200;

            if (_documents.Count == 0)
            {
                StudySessionNotificationBar.Visibility = Visibility.Visible;
                StudySessionStatusText.Text = "Study Session unavailable";
                StudySessionDetailText.Text = "Load at least one document before starting a Study Session.";
                StudySessionPhaseText.Text = "Idle";
                StudySessionEntryCountText.Text = "0";
                StudySessionProgressBar.Value = 0;
                StudySessionProgressLabel.Text = "0%";
                _ = HideStudySessionNotificationBarAsync(2600);
                return;
            }

            CouncilRole? studyRole = ResolveStudyRole();
            if (!studyRole.HasValue)
            {
                StudySessionNotificationBar.Visibility = Visibility.Visible;
                StudySessionStatusText.Text = "Study Session unavailable";
                StudySessionDetailText.Text = "Load at least one council model to run Study Session.";
                StudySessionPhaseText.Text = "Idle";
                StudySessionEntryCountText.Text = "0";
                StudySessionProgressBar.Value = 0;
                StudySessionProgressLabel.Text = "0%";
                _ = HideStudySessionNotificationBarAsync(2600);
                return;
            }

            _isStudySessionRunning = true;
            _studySessionCancelRequested = false;
            _studySessionProcessedDocumentCount = 0;
            _studySessionDomainDefinitionCount = 0;
            _studySessionCts = new CancellationTokenSource();

            // A local Study Session loads council role models, which on a single GPU must not sit
            // on top of the resident Normal-Chat model (the "Failed to load model" error). Free
            // the chat model first; MainWindow restores it on return to the chat view.
            if (!_isCloudModeEnabled && ReleaseHostChatModelAsync != null)
            {
                try
                {
                    await ReleaseHostChatModelAsync(CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    LogActivity($"Chat model release before study session skipped: {releaseEx.Message}");
                }
            }

            // Start each Study Session from a clean study-memory baseline so
            // old studied documents do not bleed into later study runs.
            _sessionHippocampus.ClearBySource(SessionHippocampusSource.StudySession);

            InputAreaContainer.IsEnabled = false;
            InputAreaContainer.Opacity = 0.55;
            StudySessionButton.Content = "Cancel Study Session";

            StudySessionNotificationBar.Visibility = Visibility.Visible;
            StudySessionStatusText.Text = "Study Session in progress";
            StudySessionDetailText.Text = "AI models are processing loaded documents. Chat will be restored when complete.";
            StudySessionPhaseText.Text = "Starting";
            StudySessionProgressBar.Value = 0;
            StudySessionProgressLabel.Text = "0%";
            StudySessionEntryCountText.Text = "0";

            int totalEntriesWritten = 0;
            int docsProcessed = 0;
            int domainDefinitions = 0;

            try
            {
                var studyDocs = await ResolveStudyDocumentsAsync();
                docsProcessed = studyDocs.Count;
                _studySessionProcessedDocumentCount = docsProcessed;
                string docList = studyDocs.Count == 0
                    ? "No documents resolved."
                    : string.Join(", ", studyDocs.Select(d => d.Name).Take(5)) + (studyDocs.Count > 5 ? ", ..." : "");
                StudySessionProgress?.Invoke(this, new StudySessionProgressEventArgs
                {
                    PhaseName = "Phase 1 · Document Segmentation",
                    Current = 1,
                    Total = 4,
                    EntriesWritten = totalEntriesWritten,
                    Message = $"Segmenting {docsProcessed} document(s): {docList}"
                });

                var chunks = new List<StudyChunk>();
                foreach (var doc in studyDocs)
                {
                    chunks.AddRange(SegmentDocumentForStudy(doc.Name, doc.Content));
                }

                if (chunks.Count == 0)
                {
                    throw new InvalidOperationException("No study chunks could be generated from loaded documents.");
                }

                var conceptPool = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

                StudySessionProgress?.Invoke(this, new StudySessionProgressEventArgs
                {
                    PhaseName = "Phase 2 · Core Study Loop",
                    Current = 0,
                    Total = chunks.Count,
                    EntriesWritten = totalEntriesWritten,
                    Message = $"Processing {chunks.Count} chunk(s)..."
                });

                for (int i = 0; i < chunks.Count; i++)
                {
                    StudyChunk chunk = chunks[i];
                    StudyChunkResult parsed = await RunStudyChunkInferenceAsync(studyRole.Value, chunk, _studySessionCts.Token);

                    string combined = BuildStudyMemoryContent(chunk, parsed);
                    _sessionHippocampus.Write(new SessionHippocampusEntry
                    {
                        Content = combined,
                        Source = SessionHippocampusSource.StudySession,
                        Tag = SessionHippocampusTag.Summary,
                        Priority = 2,
                        Timestamp = DateTime.Now,
                        SessionRunIndex = 0
                    });
                    totalEntriesWritten++;

                    foreach (string concept in parsed.Concepts)
                    {
                        string term = ExtractConceptTerm(concept);
                        if (string.IsNullOrWhiteSpace(term))
                            continue;
                        if (!conceptPool.TryGetValue(term, out var refs))
                        {
                            refs = new List<string>();
                            conceptPool[term] = refs;
                        }
                        if (refs.Count < 8 && !refs.Contains(parsed.Summary, StringComparer.OrdinalIgnoreCase))
                        {
                            refs.Add(parsed.Summary);
                        }
                    }

                    StudySessionProgress?.Invoke(this, new StudySessionProgressEventArgs
                    {
                        PhaseName = "Phase 2 · Core Study Loop",
                        Current = i + 1,
                        Total = chunks.Count,
                        EntriesWritten = totalEntriesWritten,
                        Message = $"Processed chunk {i + 1}/{chunks.Count} from '{chunk.DocumentName}'"
                    });

                    if (_studySessionCancelRequested)
                    {
                        break;
                    }
                }

                var flaggedTerms = FlagDomainTerms(conceptPool);
                if (!_studySessionCancelRequested && flaggedTerms.Count > 0)
                {
                    StudySessionProgress?.Invoke(this, new StudySessionProgressEventArgs
                    {
                        PhaseName = "Phase 3 · Domain Definition Extraction",
                        Current = 0,
                        Total = flaggedTerms.Count,
                        EntriesWritten = totalEntriesWritten,
                        Message = $"Generating {flaggedTerms.Count} domain definition(s)..."
                    });

                    for (int i = 0; i < flaggedTerms.Count; i++)
                    {
                        var term = flaggedTerms[i];
                        string context = string.Join("\n", conceptPool[term].Take(6));
                        string definition = await BuildDomainDefinitionAsync(studyRole.Value, term, context, _studySessionCts.Token);

                        _sessionHippocampus.Write(new SessionHippocampusEntry
                        {
                            Content = BuildCappedMemoryContent($"{term}: {definition}"),
                            Source = SessionHippocampusSource.StudySession,
                            Tag = SessionHippocampusTag.DomainDefinition,
                            Priority = 3,
                            Timestamp = DateTime.Now,
                            SessionRunIndex = 0
                        });
                        totalEntriesWritten++;
                        domainDefinitions++;
                        _studySessionDomainDefinitionCount = domainDefinitions;

                        StudySessionProgress?.Invoke(this, new StudySessionProgressEventArgs
                        {
                            PhaseName = "Phase 3 · Domain Definition Extraction",
                            Current = i + 1,
                            Total = flaggedTerms.Count,
                            EntriesWritten = totalEntriesWritten,
                            Message = $"Generated definition {i + 1}/{flaggedTerms.Count}"
                        });

                        if (_studySessionCancelRequested)
                        {
                            break;
                        }
                    }
                }

                StudySessionProgress?.Invoke(this, new StudySessionProgressEventArgs
                {
                    PhaseName = "Phase 4 · Consolidation",
                    Current = 1,
                    Total = 1,
                    EntriesWritten = totalEntriesWritten,
                    Message = "Consolidating session hippocampus entries..."
                });

                _sessionHippocampus.Consolidate();
                if (!_studySessionCancelRequested)
                {
                    _sessionHippocampus.MarkStudySessionCompleted();
                }

                SessionHippocampusMetadata metadata = _sessionHippocampus.GetMetadata();
                UpdateSessionHippocampusIndicator();

                if (_studySessionCancelRequested)
                {
                    StudySessionStatusText.Text = "Study Session partially completed";
                    StudySessionDetailText.Text = $"Processed {docsProcessed} document(s), wrote {totalEntriesWritten} entries before cancel.";
                    autoHideDelayMs = 2000;
                }
                else
                {
                    StudySessionStatusText.Text = "Study Session complete";
                    StudySessionDetailText.Text = $"Processed {docsProcessed} document(s), wrote {totalEntriesWritten} entries, extracted {domainDefinitions} domain definition(s).";
                    autoHideDelayMs = 2400;
                }

                StudySessionPhaseText.Text = "Completed";
                StudySessionEntryCountText.Text = metadata.TotalEntryCount.ToString();
                StudySessionProgressBar.Value = 100;
                StudySessionProgressLabel.Text = "100%";
                shouldAutoHideNotification = true;
            }
            catch (Exception ex)
            {
                StudySessionStatusText.Text = "Study Session error";
                StudySessionDetailText.Text = ex.Message;
                StudySessionPhaseText.Text = "Stopped";
                StudySessionProgressLabel.Text = "0%";
                AppendChat("error", $"Study Session failed: {ex.Message}");
                shouldAutoHideNotification = true;
                autoHideDelayMs = 3200;
            }
            finally
            {
                _studySessionCts?.Dispose();
                _studySessionCts = null;
                _studySessionCancelRequested = false;
                _isStudySessionRunning = false;
                InputAreaContainer.IsEnabled = true;
                InputAreaContainer.Opacity = 1;
                StudySessionButton.Content = "Start Study Session";

                if (shouldAutoHideNotification)
                {
                    _ = HideStudySessionNotificationBarAsync(autoHideDelayMs);
                }
            }
        }

        private void StudySessionProgress_Progressed(object? sender, StudySessionProgressEventArgs e)
        {
            _ = Dispatcher.InvokeAsync(() =>
            {
                StudySessionPhaseText.Text = e.PhaseName;
                StudySessionEntryCountText.Text = e.EntriesWritten.ToString();
                StudySessionDetailText.Text = e.Message;

                double pct = e.Total <= 0 ? 0 : Math.Clamp((double)e.Current / e.Total * 100, 0, 100);
                StudySessionProgressBar.Value = pct;
                StudySessionProgressLabel.Text = $"{pct:0}%";
            }, DispatcherPriority.Background);
        }

        private CouncilRole? ResolveStudyRole()
        {
            if (!string.IsNullOrWhiteSpace(_council[CouncilRole.Architect].ModelPath))
            {
                return CouncilRole.Architect;
            }

            foreach (var kvp in _council)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Value.ModelPath))
                {
                    return kvp.Key;
                }
            }

            // Cloud mode has no local ModelPath, but ExecuteCouncilRoleAsync routes inference to the
            // cloud council when _isCloudModeEnabled. Allow Study Session to run on the cloud council.
            if (CanUseCloudCouncil)
            {
                return CouncilRole.Architect;
            }

            return null;
        }

        private async Task<List<(string Name, string Content)>> ResolveStudyDocumentsAsync()
        {
            var results = new List<(string Name, string Content)>();
            foreach (var doc in _documents)
            {
                string displayName = string.IsNullOrWhiteSpace(doc.Name)
                    ? (string.IsNullOrWhiteSpace(doc.FilePath) ? "document" : Path.GetFileName(doc.FilePath))
                    : doc.Name;

                string text = "";
                if (!string.IsNullOrWhiteSpace(doc.FilePath) && File.Exists(doc.FilePath))
                {
                    string ext = Path.GetExtension(doc.FilePath).ToLowerInvariant();
                    try
                    {
                        if (ext == ".pdf")
                        {
                            text = await PdfExtractor.ExtractTextFromPdfAsync(doc.FilePath);
                        }
                        else if (IsPlainTextExtension(ext))
                        {
                            text = await Task.Run(() =>
                            {
                                try { return File.ReadAllText(doc.FilePath, Encoding.UTF8); }
                                catch { return File.ReadAllText(doc.FilePath, Encoding.Default); }
                            });
                        }
                    }
                    catch
                    {
                        text = "";
                    }
                }

                // Fallback: if the file is missing or extraction yielded nothing, reconstruct the
                // document text from the chunks already held in memory by the retriever so a Study
                // Session is not silently empty (e.g. failed PDF extraction or a not-on-disk import).
                if (string.IsNullOrWhiteSpace(text))
                {
                    string recovered = _documentRetriever.GetAllTextForFile(displayName);
                    if (string.IsNullOrWhiteSpace(recovered) && !string.IsNullOrWhiteSpace(doc.FilePath))
                        recovered = _documentRetriever.GetAllTextForFile(Path.GetFileName(doc.FilePath));
                    if (!string.IsNullOrWhiteSpace(recovered))
                    {
                        text = recovered;
                        LogActivity($"Study Session: recovered '{displayName}' text from in-memory chunks (file extraction unavailable).");
                    }
                }

                if (!string.IsNullOrWhiteSpace(text))
                {
                    results.Add((displayName, text));
                }
            }

            return results;
        }

        private static List<StudyChunk> SegmentDocumentForStudy(string documentName, string content)
        {
            const int targetTokens = 350;
            const int overlapTokens = 50;
            int targetChars = (int)(targetTokens * AvgCharsPerToken);
            int overlapChars = (int)(overlapTokens * AvgCharsPerToken);

            var chunks = new List<StudyChunk>();
            if (string.IsNullOrWhiteSpace(content))
                return chunks;

            string text = content.Trim();
            int start = 0;
            int index = 1;
            while (start < text.Length)
            {
                int length = Math.Min(targetChars, text.Length - start);
                string slice = text.Substring(start, length).Trim();
                if (!string.IsNullOrWhiteSpace(slice))
                {
                    chunks.Add(new StudyChunk
                    {
                        DocumentName = documentName,
                        ChunkIndex = index,
                        Content = slice,
                        TokenEstimate = (int)Math.Ceiling(slice.Length / AvgCharsPerToken)
                    });
                    index++;
                }

                if (start + length >= text.Length)
                {
                    break;
                }

                start += Math.Max(1, targetChars - overlapChars);
            }

            return chunks;
        }

        private async Task<StudyChunkResult> RunStudyChunkInferenceAsync(CouncilRole role, StudyChunk chunk, CancellationToken token)
        {
            string system =
                "You are running Study Session preprocessing. Return ONLY these sections in order: " +
                "[SUMMARY], [CONCEPTS], [Q&A]. No extra sections. " +
                "[SUMMARY]: 3-5 sentences describing what the chunk is fundamentally about. " +
                "[CONCEPTS]: numbered list of 5-10 key facts, rules, formulas, or concepts from the chunk. " +
                "[Q&A]: generate exactly 3 Q/A pairs the chunk answers in the format 'Q1:' and 'A1:' etc. " +
                "Use only the provided chunk content.";

            string payload = BuildLabeledBlock("DOCUMENT NAME", chunk.DocumentName)
                + BuildLabeledBlock("CHUNK INDEX", chunk.ChunkIndex.ToString())
                + BuildLabeledBlock("CHUNK CONTENT", chunk.Content);

            var result = await ExecuteCouncilRoleAsync(role, system, payload, token, 0.65f, showLiveCard: false);
            return ParseStudyChunkResponse(result.Answer);
        }

        private static StudyChunkResult ParseStudyChunkResponse(string text)
        {
            var parsed = new StudyChunkResult();
            if (string.IsNullOrWhiteSpace(text))
                return parsed;

            string summary = ExtractSection(text, "SUMMARY", "CONCEPTS");
            string concepts = ExtractSection(text, "CONCEPTS", "Q&A");
            string qa = ExtractSection(text, "Q&A", "");

            parsed.Summary = string.IsNullOrWhiteSpace(summary) ? text.Trim() : summary.Trim();
            parsed.Concepts = ParseNumberedList(concepts);

            var qas = new List<(string Question, string Answer)>();
            var qMatches = System.Text.RegularExpressions.Regex.Matches(qa, @"Q\d+\s*:\s*(.+?)\r?\nA\d+\s*:\s*(.+?)(?=(\r?\nQ\d+\s*:)|$)", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (System.Text.RegularExpressions.Match m in qMatches)
            {
                string q = m.Groups[1].Value.Trim();
                string a = m.Groups[2].Value.Trim();
                if (!string.IsNullOrWhiteSpace(q) && !string.IsNullOrWhiteSpace(a))
                    qas.Add((q, a));
            }
            parsed.QuestionAnswers = qas;

            return parsed;
        }

        private static string ExtractSection(string text, string startLabel, string endLabel)
        {
            int start = text.IndexOf($"[{startLabel}]", StringComparison.OrdinalIgnoreCase);
            if (start < 0)
                return "";
            start += startLabel.Length + 2;

            int end = string.IsNullOrWhiteSpace(endLabel)
                ? text.Length
                : text.IndexOf($"[{endLabel}]", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = text.Length;

            return text[start..end].Trim();
        }

        private static List<string> ParseNumberedList(string text)
        {
            var items = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                string t = line.Trim();
                if (string.IsNullOrWhiteSpace(t))
                    continue;

                bool numbered = t.Length > 1 && char.IsDigit(t[0]) && t.IndexOfAny(['.', ')']) is int sep && sep > 0 && sep < 4;
                if (numbered)
                {
                    int sepIndex = t.IndexOfAny(['.', ')']);
                    if (sepIndex > 0)
                    {
                        items.Add(t[(sepIndex + 1)..].Trim());
                    }
                }
                else if (t.StartsWith("- ") || t.StartsWith("* "))
                {
                    items.Add(t[2..].Trim());
                }
            }

            return items;
        }

        private static string BuildStudyCombinedContent(StudyChunk chunk, StudyChunkResult result)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Document: {chunk.DocumentName} | Chunk {chunk.ChunkIndex}");
            sb.AppendLine("Summary:");
            sb.AppendLine(result.Summary);

            if (result.Concepts.Count > 0)
            {
                sb.AppendLine("Concepts:");
                for (int i = 0; i < result.Concepts.Count; i++)
                    sb.AppendLine($"{i + 1}. {result.Concepts[i]}");
            }

            if (result.QuestionAnswers.Count > 0)
            {
                sb.AppendLine("Q&A:");
                for (int i = 0; i < result.QuestionAnswers.Count; i++)
                {
                    sb.AppendLine($"Q{i + 1}: {result.QuestionAnswers[i].Question}");
                    sb.AppendLine($"A{i + 1}: {result.QuestionAnswers[i].Answer}");
                }
            }

            return BuildCappedMemoryContent(sb.ToString());
        }

        private static string ExtractConceptTerm(string concept)
        {
            if (string.IsNullOrWhiteSpace(concept))
                return "";

            string normalized = concept.Trim();
            int colon = normalized.IndexOf(':');
            if (colon > 2)
            {
                normalized = normalized[..colon].Trim();
            }

            var words = System.Text.RegularExpressions.Regex.Matches(normalized, @"\b[A-Za-z][A-Za-z0-9_\-]{2,}\b")
                .Select(m => m.Value)
                .Take(4)
                .ToList();
            return words.Count == 0 ? "" : string.Join(" ", words);
        }

        private static List<string> FlagDomainTerms(Dictionary<string, List<string>> conceptPool)
        {
            var common = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","and","with","from","this","that","using","process","system","data","file","input","output","result","summary","analysis","document","text"
            };

            var flagged = new List<string>();
            foreach (var kvp in conceptPool)
            {
                string term = kvp.Key.Trim();
                if (string.IsNullOrWhiteSpace(term))
                    continue;

                bool appearsAcrossChunks = kvp.Value.Count >= 2;
                bool domainSpecific = !common.Contains(term)
                    && (term.Any(char.IsUpper) || term.Any(char.IsDigit) || term.Contains('_') || term.Contains('-') || term.Length > 10);

                if (appearsAcrossChunks || domainSpecific)
                {
                    flagged.Add(term);
                }
            }

            return flagged.Distinct(StringComparer.OrdinalIgnoreCase).Take(40).ToList();
        }

        private async Task<string> BuildDomainDefinitionAsync(CouncilRole role, string term, string context, CancellationToken token)
        {
            string system =
                "You are generating a domain definition from provided source context only. " +
                "Return a concise definition and short explanation for the requested term using only this context. " +
                "Do not use external knowledge. Do not add unrelated terms.";

            string payload = BuildLabeledBlock("TERM", term)
                + BuildLabeledBlock("CHUNK SUMMARIES", context);

            var result = await ExecuteCouncilRoleAsync(role, system, payload, token, 0.3f, showLiveCard: false);
            return string.IsNullOrWhiteSpace(result.Answer)
                ? "Definition unavailable from provided context."
                : BuildCappedMemoryContent(result.Answer, 200);
        }

        // ═══════════════════════════════════════════════
        // Context Compression Engine
        // ═══════════════════════════════════════════════

        private int EstimateTokenCount(string text)
        {
            return (int)Math.Ceiling(text.Length / AvgCharsPerToken);
        }

        /// <summary>
        /// Counts tokens with the model's real tokenizer when a live context is
        /// available; falls back to the chars/4 estimate otherwise.
        /// </summary>
        private int CountTokensWithContext(LLamaContext? context, string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            try
            {
                if (context != null)
                    return context.Tokenize(text, addBos: false, special: true).Length;
            }
            catch
            {
            }

            return EstimateTokenCount(text);
        }

        private int EstimateTotalHistoryTokens()
        {
            int total = 0;
            foreach (var entry in _chatHistory)
            {
                total += EstimateTokenCount(entry.Content);
            }
            return total;
        }

        private int GetContextCompressionThresholdTokens()
        {
            if (CanUseCloudCouncil)
            {
                int cloudWindow = _openRouterChatService.GetApproximateContextWindowTokens(
                    OpenRouterChatService.WorkplaceCouncilDefaultModelId);
                int cloudUsableHistoryWindow = Math.Max(32768, cloudWindow - 32768);
                return Math.Max(ContextCompressionThreshold, (int)(cloudUsableHistoryWindow * 0.60));
            }

            int architectContext = (int)GetRoleContextSize(CouncilRole.Architect);
            int builderContext = (int)GetRoleContextSize(CouncilRole.Builder);
            int criticContext = (int)GetRoleContextSize(CouncilRole.Critic);
            int limitingContext = Math.Max((int)MinRoleContext, Math.Min(architectContext, Math.Min(builderContext, criticContext)));

            int reservedForCurrentTurn = Math.Min(1800, Math.Max(900, limitingContext / 5));
            int usableHistoryWindow = Math.Max(ContextCompressionThreshold, limitingContext - reservedForCurrentTurn);
            const double triggerRatio = 0.45;

            return Math.Max(ContextCompressionThreshold, (int)(usableHistoryWindow * triggerRatio));
        }

        private int GetRecentMessagesToKeepAfterCompression()
        {
            return CanUseCloudCouncil ? 16 : 6;
        }

        private async Task CompressChatHistoryIfNeededAsync(CancellationToken token)
        {
            int totalTokens = EstimateTotalHistoryTokens();
            int compressionThreshold = GetContextCompressionThresholdTokens();
            if (totalTokens <= compressionThreshold || _chatHistory.Count < 4)
            {
                return;
            }

            int recentMessagesToKeep = GetRecentMessagesToKeepAfterCompression();
            int messagesToCompress = Math.Max(2, _chatHistory.Count - recentMessagesToKeep);
            if (messagesToCompress >= _chatHistory.Count)
                messagesToCompress = Math.Max(2, _chatHistory.Count / 2);
            var oldMessages = _chatHistory.Take(messagesToCompress).ToList();

            var summaryInput = new StringBuilder();
            summaryInput.AppendLine("Summarize this conversation for future council context.");
            summaryInput.AppendLine("Preserve: user requirements, decisions, constraints, source-backed facts with dates/sources, tool results, code/API details, unresolved issues, and any explicit user preferences.");
            summaryInput.AppendLine("Discard: greetings, duplicated role chatter, failed drafts, repeated boilerplate, and low-value status messages.");
            foreach (var msg in oldMessages)
            {
                summaryInput.AppendLine($"[{msg.Role}] {msg.Content}");
            }

            string summary;
            string? summarizerModelPath = _council.Values
                .Where(c => !string.IsNullOrWhiteSpace(c.ModelPath))
                .Select(c => c.ModelPath)
                .FirstOrDefault();

            if (CanUseCloudCouncil)
            {
                // Summarize via a direct (non-streamed) cloud call. Routing through
                // ExecuteCouncilRoleAsync in cloud mode would create a visible streaming council
                // card for an internal maintenance step and burn a full council role turn.
                try
                {
                    OpenRouterChatResponse cloudSummary = await _openRouterChatService.SendConversationAsync(
                        new List<OpenRouterMessage> { new("user", summaryInput.ToString()) },
                        "You are a context compactor for a multi-role AI council. Keep durable facts, requirements, constraints, tool results, source-backed evidence, and unresolved tasks. Remove repetition and chatter. Be concise but not lossy.",
                        false,
                        OpenRouterChatService.WorkplaceCouncilDefaultModelId,
                        null,
                        token);
                    summary = string.IsNullOrWhiteSpace(cloudSummary.Text)
                        ? BuildFallbackSummary(oldMessages)
                        : cloudSummary.Text.Trim();
                }
                catch
                {
                    summary = BuildFallbackSummary(oldMessages);
                }
            }
            else if (!string.IsNullOrWhiteSpace(summarizerModelPath))
            {
                try
                {
                    var config = _council.Values.First(c => c.ModelPath == summarizerModelPath);
                    var summaryResult = await ExecuteCouncilRoleAsync(
                        _council.First(kv => kv.Value.ModelPath == summarizerModelPath).Key,
                        "You are a context compactor for a multi-role AI council. Keep durable facts, requirements, constraints, tool results, source-backed evidence, and unresolved tasks. Remove repetition and chatter. Be concise but not lossy.",
                        summaryInput.ToString(),
                        token,
                        showLiveCard: false);
                    summary = summaryResult.Answer;
                }
                catch
                {
                    summary = BuildFallbackSummary(oldMessages);
                }
            }
            else
            {
                summary = BuildFallbackSummary(oldMessages);
            }

            _chatHistory.RemoveRange(0, messagesToCompress);
            _chatHistory.Insert(0, ("system", $"[Context Summary] {summary}"));

            int newTokens = EstimateTotalHistoryTokens();
            LogActivity($"Context compressed: {totalTokens} → {newTokens} tokens ({messagesToCompress} messages summarized)");
            AppendChat("system", $"Context compressed: {totalTokens} → {newTokens} tokens");
        }

        private static string BuildFallbackSummary(List<(string Role, string Content)> messages)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
            {
                string truncated = msg.Content.Length > 120 ? msg.Content[..120] + "..." : msg.Content;
                sb.AppendLine($"{msg.Role}: {truncated}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Builds a compact rolling window of recent conversation turns for the Architect
        /// so it understands the current conversation flow and doesn't get stuck on stale context.
        /// </summary>
        private string BuildRecentConversationContext(int maxTurns = 4, int perTurnChars = 200)
        {
            if (_chatHistory.Count == 0)
                return "";

            // If a major topic shift was detected and session memory was reset,
            // avoid feeding stale recent turns into the next architect pass.
            if (_sessionMemory == null)
                return "";

            var recentTurns = _chatHistory
                .Where(h => h.Role is "user" or "architect" or "builder")
                .TakeLast(maxTurns * 2)
                .ToList();

            if (recentTurns.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("[RECENT CONVERSATION CONTEXT]");
            sb.AppendLine("The following is a summary of recent conversation turns. Use this to understand what the user has been working on and any topic shifts.");

            foreach (var (role, content) in recentTurns)
            {
                string roleLabel = role switch
                {
                    "user" => "User",
                    "architect" => "Architect",
                    "builder" => "Builder",
                    _ => role
                };

                // Compact: cap each turn (200 chars local; cloud callers pass a larger budget)
                string compacted = content.Length > perTurnChars ? content[..perTurnChars] + "..." : content;
                sb.AppendLine($"[{roleLabel}] {compacted}");
            }

            sb.AppendLine("[END RECENT CONVERSATION CONTEXT]");
            sb.AppendLine("Your plan must address the LATEST user message above. Do not repeat or continue a plan from a prior turn unless the user explicitly asks.");
            return sb.ToString();
        }

        /// <summary>
        /// Detects whether the current user query represents a significant topic shift
        /// from the stored session memory. When detected, invalidates stale session memory
        /// so the Architect doesn't carry forward an irrelevant prior plan.
        /// </summary>
        private bool DetectAndHandleTopicShift(string currentQuery, string objective)
        {
            if (_sessionMemory == null || string.IsNullOrWhiteSpace(_sessionMemory.TaskDescription))
                return false;

            string currentCombined = $"{currentQuery} {objective}".ToLowerInvariant();
            string previousTask = _sessionMemory.TaskDescription.ToLowerInvariant();

            // Extract significant words from both
            var stopWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the","and","for","with","that","this","from","into","about","have","has","are",
                "was","were","will","just","what","how","can","does","please","make","create",
                "write","give","need","want","should","could","would","also","like","using"
            };

            var currentWords = Regex.Matches(currentCombined, @"\b[a-z][a-z0-9_]{2,}\b")
                .Select(m => m.Value)
                .Where(w => !stopWords.Contains(w))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var previousWords = Regex.Matches(previousTask, @"\b[a-z][a-z0-9_]{2,}\b")
                .Select(m => m.Value)
                .Where(w => !stopWords.Contains(w))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (currentWords.Count == 0 || previousWords.Count == 0)
                return false;

            int overlap = currentWords.Intersect(previousWords, StringComparer.OrdinalIgnoreCase).Count();
            double similarity = (double)overlap / Math.Max(currentWords.Count, previousWords.Count);

            // Less than 20% overlap indicates a significant topic shift
            if (similarity < 0.20)
            {
                LogActivity($"Topic shift detected (similarity={similarity:P0}). Invalidating stale session memory.");
                _sessionMemory = null;
                SessionMemoryStatusBlock.Text = "Session memory reset (topic change).";
                return true;
            }

            return false;
        }

        // ═══════════════════════════════════════════════
        // Local Code Execution Sandbox
        // ═══════════════════════════════════════════════

        private async void RerunBuilder_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                AppendChat("system", "Already processing...");
                return;
            }

            if (_lastRunContext == null)
            {
                AppendChat("warning", "No prior run to re-run Builder from.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_council[CouncilRole.Builder].ModelPath) && !CanUseCloudCouncil)
            {
                AppendChat("warning", "Builder model is not configured.");
                return;
            }

            _isProcessing = true;
            SendButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            _cancellationTokenSource = new CancellationTokenSource();
            string previousActiveCouncilWebPrompt = _activeCouncilWebPrompt;
            string previousReactiveWebContext = _latestCouncilReactiveWebContext;
            CouncilRunContext? previousActiveRunContext = _activeCouncilRunContext;
            _latestCouncilReactiveWebContext = string.Empty;
            _activeCouncilRunContext = _lastRunContext;
            _activeCouncilWebPrompt = string.IsNullOrWhiteSpace(_lastRunContext.Objective)
                ? _lastRunContext.UserPrompt
                : $"{_lastRunContext.UserPrompt}\n{_lastRunContext.Objective}";

            try
            {
                BuilderStageText.Text = "Builder · Re-run";
                RelayStatusBlock.Text = "Relay: Builder re-run...";

                string objectiveClause = string.IsNullOrWhiteSpace(_lastRunContext.Objective)
                    ? ""
                    : $"\n[PRIMARY OBJECTIVE] {_lastRunContext.Objective}\nAll outputs must align with this objective.\n";

                int maxChunks = _documentRetriever.CalculateMaxChunksForContext((int)_contextSize);
                var chunks = MergeWithPriority(_documentRetriever.RetrieveRelevantChunks(_lastRunContext.UserPrompt, maxChunks), maxChunks);
                string knowledge = BuildKnowledgePacket(chunks, _nextPromptPriorityConcept);
                var builderConfig = GetEffectiveRoleConfig(CouncilRole.Builder);
                bool smallLocalBuilderModel = IsSmallLocalCouncilModel(builderConfig.ModelPath ?? builderConfig.DisplayName, _isCloudModeEnabled);

                string builderSystem = GetEmbeddedSystemPrompt(CouncilRole.Builder)
                    + objectiveClause
                    + (_lastRunContext.IsArtifactCanvasRequest
                        ? BuildArtifactTaskTypeBoost(CouncilRole.Builder, _lastRunContext)
                        : GetTaskTypeBoost(_lastRunContext.TaskType, CouncilRole.Builder))
                    + (_lastRunContext.IsArtifactCanvasRequest ? BuildArtifactCanvasBoost(CouncilRole.Builder, _lastRunContext.PreferredArtifactFormatHint, _lastRunContext) : "")
                    + (_lastRunContext.IsArtifactCanvasRequest && smallLocalBuilderModel ? BuildSmallModelArtifactAssist(CouncilRole.Builder, _lastRunContext.PreferredArtifactFormatHint, _lastRunContext) : "")
                    + (!_isCloudModeEnabled ? BuildLocalBuilderCognitionBoost(_lastRunContext.TaskType, _lastRunContext) : "")
                    + (_lastRunContext.IsCalculationTask ? GetCalculationBoost(CouncilRole.Builder, _lastRunContext.TaskType) : "")
                    + BuildBuilderContract(_lastRunContext.TaskType, _lastRunContext.IsArtifactCanvasRequest)
                    + BuildCouncilWebSystemNote(_lastRunContext.WebContext, _lastRunContext.WebGroundingRequired)
                    + BuildBuilderWebPauseSystemNote()
                    + "\n[SHARED VOCABULARY]\nUse user terms exactly as named.";
                builderSystem = ComposeCouncilSystemPrompt(builderSystem, CouncilRole.Builder, _lastRunContext, GetSystemPromptDocumentBudgetChars(CouncilRole.Builder));

                string sharedVocabularySection = BuildSharedVocabularySection(_lastRunContext.SharedVocabulary);
                string pipelineState = BuildPipelineStateHeader(BuildArchitectSummaryFromPlan(_lastRunContext.ArchitectOutput), "");

                var payload = new StringBuilder();
                payload.AppendLine(BuildCouncilGoalContractBlock(_lastRunContext.GoalContract));
                payload.AppendLine(BuildCouncilCapabilityCard(_isWebSearchEnabled, _lastRunContext.IsCloudExecution));
                if (_lastRunContext.IsArtifactCanvasRequest)
                    payload.AppendLine(BuildCanvasArtifactAnchorBlock(_lastRunContext));
                if (smallLocalBuilderModel && (_lastRunContext.TaskType == CouncilTaskType.Coding || _lastRunContext.IsArtifactCanvasRequest))
                    payload.AppendLine(BuildBuilderImplementationCapsule(_lastRunContext));
                payload.AppendLine(pipelineState);
                payload.AppendLine(sharedVocabularySection);
                AppendCouncilWebContext(payload, _lastRunContext);
                payload.AppendLine(BuildRolePrimedPayload(CouncilRole.Builder, _lastRunContext.TaskType, ""));
                payload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", _lastRunContext.UserPrompt));
                if (!string.IsNullOrWhiteSpace(_lastRunContext.ArchitectOutput))
                {
                    payload.AppendLine(BuildLabeledBlock("ARCHITECT PLAN", _lastRunContext.ArchitectOutput));
                }
                if (_lastRunContext.IsDocumentTask && !string.IsNullOrWhiteSpace(_lastRunContext.DocumentContent))
                {
                    payload.AppendLine(BuildDocumentContentBlock(_lastRunContext.DocumentContent, 7000));
                }
                if (chunks.Count > 0)
                {
                    payload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledge));
                }
                payload.Append(BuildCouncilClosingAnchor(CouncilRole.Builder, allowLocalWebPause: _isWebSearchEnabled && !_isCloudModeEnabled));

                var result = await ExecuteCouncilRoleAsync(
                    CouncilRole.Builder,
                    builderSystem,
                    payload.ToString(),
                    _cancellationTokenSource.Token,
                    _lastRunContext.IsDocumentTask ? 0.25f : null);
                if (!TryNormalizeBuilderOutput(result.Answer, _lastRunContext.TaskType, out string rerunBuilderCleaned, out bool _))
                {
                    throw new InvalidOperationException("Builder re-run output missing completion marker.");
                }
                string rerunOutput = PostProcessBuilderOutput(rerunBuilderCleaned, _lastRunContext);
                bool rerunProducedCode = DetectCodeOutput(rerunOutput).IsCode;
                if (_lastRunContext.WebGroundingRequired
                    && !HasCouncilWebEvidenceForRun(_lastRunContext)
                    && _lastRunContext.TaskType != CouncilTaskType.Coding
                    && !_lastRunContext.IsArtifactCanvasRequest
                    && !rerunProducedCode
                    && !BuilderStatesWebEvidenceUnavailable(rerunOutput))
                {
                    rerunOutput = BuildWebEvidenceUnavailableBuilderFallback(_lastRunContext);
                }
                _lastRunContext.BuilderOutput = rerunOutput;
                _lastRunContext.BuilderProducedCode = rerunProducedCode;

                bool rerunToCanvas = _lastRunContext.TaskType == CouncilTaskType.Coding
                    || rerunProducedCode
                    || (_lastRunContext.IsArtifactCanvasRequest && ArtifactRenderService.DetectForCanvas(rerunOutput, null).SupportsPreview);
                if (rerunToCanvas)
                {
                    UpdateProjectCanvas(rerunOutput);
                    AppendChat("builder", "Builder re-run output sent to Project Canvas.");
                }
                else
                {
                    AppendChat("builder", rerunOutput);
                }

                _lastFinalOutput = rerunToCanvas ? ProjectCanvasEditor.Text : rerunOutput;
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Builder re-run failed: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                SendButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                _activeCouncilWebPrompt = previousActiveCouncilWebPrompt;
                _latestCouncilReactiveWebContext = previousReactiveWebContext;
                _activeCouncilRunContext = previousActiveRunContext;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                RelayStatusBlock.Text = "Relay: Idle";
            }
        }

        private async void RerunCritic_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                AppendChat("system", "Already processing...");
                return;
            }

            if (_lastRunContext == null || string.IsNullOrWhiteSpace(_lastRunContext.BuilderOutput))
            {
                AppendChat("warning", "No prior builder output available for critic re-run.");
                return;
            }

            if (string.IsNullOrWhiteSpace(_council[CouncilRole.Critic].ModelPath))
            {
                AppendChat("warning", "Critic model is not configured.");
                return;
            }

            _isProcessing = true;
            SendButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            _cancellationTokenSource = new CancellationTokenSource();

            try
            {
                CriticStageText.Text = "Critic · Re-run";
                RelayStatusBlock.Text = "Relay: Critic re-run...";

                string objectiveClause = string.IsNullOrWhiteSpace(_lastRunContext.Objective)
                    ? ""
                    : $"\n[PRIMARY OBJECTIVE] {_lastRunContext.Objective}\nAll outputs must align with this objective.\n";

                string criticSystem = GetEmbeddedSystemPrompt(CouncilRole.Critic)
                    + objectiveClause
                    + GetTaskTypeBoost(_lastRunContext.TaskType, CouncilRole.Critic)
                    + (_lastRunContext.IsCalculationTask ? GetCalculationBoost(CouncilRole.Critic) : "")
                    + BuildCriticContract(_lastRunContext.TaskType, _lastRunContext.IsArtifactCanvasRequest)
                    + "\n[CRITIC VISIBILITY RULE] Do not output thinking, hidden reasoning, chain-of-thought, scratch analysis, or deliberation. Output only the final review contract.";
                criticSystem = ComposeCouncilSystemPrompt(criticSystem, CouncilRole.Critic, _lastRunContext, GetSystemPromptDocumentBudgetChars(CouncilRole.Critic));

                string payload = BuildPipelineStateHeader(BuildArchitectSummaryFromPlan(_lastRunContext.ArchitectOutput), BuildBuilderSummaryFromCode(_lastRunContext.BuilderOutput))
                    + BuildSharedVocabularySection(_lastRunContext.SharedVocabulary)
                    + BuildPipelineHealthSection(_lastRunContext)
                    + BuildRolePrimedPayload(CouncilRole.Critic, _lastRunContext.TaskType, BuildCriticPayload(_lastRunContext, _lastSandboxOutput))
                    + BuildCouncilClosingAnchor(CouncilRole.Critic);
                var result = await ExecuteCouncilRoleAsync(CouncilRole.Critic, criticSystem, payload, _cancellationTokenSource.Token);
                if (result.IsReasoningFallback
                    || CriticContainsReasoningLeak(result.Answer)
                    || !TryNormalizeCriticReview(result.Answer, _lastRunContext.TaskType, out string rerunCriticCleaned, out bool _))
                {
                    throw new InvalidOperationException("Critic re-run output was not a valid structured review.");
                }
                _lastCriticRawOutput = rerunCriticCleaned;
                _lastCriticReport = CriticContractParser.Parse(rerunCriticCleaned);
                _lastRunContext.CriticReview = rerunCriticCleaned;

                AppendChat("critic", rerunCriticCleaned);
                SavePersistedSession();
            }
            catch (Exception ex)
            {
                AppendChat("error", $"Critic re-run failed: {ex.Message}");
            }
            finally
            {
                _isProcessing = false;
                SendButton.IsEnabled = true;
                StopButton.IsEnabled = false;
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;
                RelayStatusBlock.Text = "Relay: Idle";
            }
        }

        private async void RerunSandbox_Click(object sender, RoutedEventArgs e)
        {
            string code = ProjectCanvasEditor.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                AppendChat("sandbox", "No code in Project Canvas to run.");
                return;
            }

            string lang = DetectLanguage(code);
            if (lang is not ("python" or "java" or "html"))
            {
                AppendChat("sandbox", $"Language '{lang}' is not supported for execution. Supported: Python, Java, HTML.");
                return;
            }

            RelayStatusBlock.Text = "Relay: Sandbox re-run...";
            string result = await ExecuteCodeSandboxAsync(code, lang);
            _lastSandboxOutput = result;
            AppendChat("sandbox", $"{lang} re-run output:\n{result}");
            RelayStatusBlock.Text = "Relay: Idle";
            SavePersistedSession();
        }

        private void ExportRunArtifacts_Click(object sender, RoutedEventArgs e)
        {
            if (_lastRunContext == null)
            {
                AppendChat("warning", "No prior run available to export.");
                return;
            }

            var formatResult = MessageBox.Show("Export Council output as Markdown? Click No for plain text.", "Export Format", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (formatResult == MessageBoxResult.Cancel)
                return;

            bool markdown = formatResult == MessageBoxResult.Yes;
            string ext = markdown ? "md" : "txt";
            string safe = string.Join("_", Regex.Matches(_lastRunContext.UserPrompt, @"\b\w+\b").Select(m => m.Value).Take(5));
            if (string.IsNullOrWhiteSpace(safe)) safe = "council_export";
            string fileName = $"{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
            string folder = Path.Combine(AppDataPaths.ChatHistory, "WorkplaceExports");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, fileName);

            var sb = new StringBuilder();
            if (markdown)
            {
                sb.AppendLine($"# Council Export ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
                sb.AppendLine($"**Task Type:** {_lastRunContext.TaskType}");
                sb.AppendLine();
                sb.AppendLine("## Original User Prompt");
                sb.AppendLine(_lastRunContext.UserPrompt);
                if (!string.IsNullOrWhiteSpace(_lastRunContext.Objective))
                {
                    sb.AppendLine();
                    sb.AppendLine("## Objective Statement");
                    sb.AppendLine(_lastRunContext.Objective);
                }
                sb.AppendLine();
                sb.AppendLine("## Architect Plan");
                sb.AppendLine(_lastRunContext.ArchitectOutput);
                sb.AppendLine();
                sb.AppendLine("## Builder Output");
                sb.AppendLine(_lastRunContext.BuilderOutput);
                sb.AppendLine();
                sb.AppendLine("## Critic Findings");
                sb.AppendLine(_lastRunContext.CriticReview);
                sb.AppendLine();
                sb.AppendLine("## Confidence Label");
                sb.AppendLine(_lastConfidenceLabel);
                sb.AppendLine();
                sb.AppendLine("## Final Delivered Result");
                sb.AppendLine(_lastFinalOutput);
            }
            else
            {
                sb.AppendLine($"Council Export ({DateTime.Now:yyyy-MM-dd HH:mm:ss})");
                sb.AppendLine($"Task Type: {_lastRunContext.TaskType}");
                sb.AppendLine("\n=== Original User Prompt ===\n" + _lastRunContext.UserPrompt);
                if (!string.IsNullOrWhiteSpace(_lastRunContext.Objective))
                    sb.AppendLine("\n=== Objective Statement ===\n" + _lastRunContext.Objective);
                sb.AppendLine("\n=== Architect Plan ===\n" + _lastRunContext.ArchitectOutput);
                sb.AppendLine("\n=== Builder Output ===\n" + _lastRunContext.BuilderOutput);
                sb.AppendLine("\n=== Critic Findings ===\n" + _lastRunContext.CriticReview);
                sb.AppendLine("\n=== Confidence Label ===\n" + _lastConfidenceLabel);
                sb.AppendLine("\n=== Final Delivered Result ===\n" + _lastFinalOutput);
            }

            File.WriteAllText(path, sb.ToString());
            AppendChat("system", $"Council export created: {path}");
        }

        private async void RunCodeButton_Click(object sender, RoutedEventArgs e)
        {
            string code = ProjectCanvasEditor.Text;
            if (string.IsNullOrWhiteSpace(code))
            {
                AppendChat("sandbox", "No code in Project Canvas to run.");
                return;
            }

            string lang = DetectLanguage(code);
            if (lang is not ("python" or "java" or "html"))
            {
                AppendChat("sandbox", $"Language '{lang}' is not supported for execution. Supported: Python, Java, HTML.");
                return;
            }

            LogActivity($"Manual sandbox run: {lang}");
            string result = await ExecuteCodeSandboxAsync(code, lang);
            AppendChat("sandbox", $"{lang} output:\n{result}");
            ChatScrollViewer.ScrollToEnd();
        }

        private static string ExtractCodeBlock(string content, string language)
        {
            string fenceTag = $"```{language}";
            int start = content.IndexOf(fenceTag, StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                start = content.IndexOf('\n', start);
                if (start < 0) return content;
                start++;
                int end = content.IndexOf("```", start, StringComparison.Ordinal);
                if (end > start)
                    return content[start..end].Trim();
            }

            // Also try generic ``` block
            start = content.IndexOf("```", StringComparison.Ordinal);
            if (start >= 0)
            {
                start = content.IndexOf('\n', start);
                if (start < 0) return content;
                start++;
                int end = content.IndexOf("```", start, StringComparison.Ordinal);
                if (end > start)
                    return content[start..end].Trim();
            }

            return content;
        }

        private async Task<string> ExecuteCodeSandboxAsync(string content, string language, CouncilRunContext? contextOverride = null)
        {
            string code = ExtractCodeBlock(content, language);
            string tempDir = Path.Combine(Path.GetTempPath(), "AxiomSandbox");
            Directory.CreateDirectory(tempDir);

            try
            {
                return language switch
                {
                    "python" => await RunPythonAsync(code, tempDir),
                    "java" => await RunJavaAsync(code, tempDir),
                    "html" => ValidateHtml(code, tempDir, contextOverride ?? _activeCouncilRunContext ?? _lastRunContext),
                    _ => $"Unsupported language: {language}"
                };
            }
            catch (Exception ex)
            {
                return $"Sandbox error: {ex.Message}";
            }
        }

        private async Task<string> RunPythonAsync(string code, string tempDir)
        {
            var result = await _pythonExecutionService.ExecuteMathScriptAsync(code, _activePythonSandboxPreamble, 10000).ConfigureAwait(false);
            if (result.TimedOut)
                return BuildSandboxTimeoutResultBlock();

            return AppendUnitToNumericOutput(result.Output, (_lastRunContext?.UserPrompt ?? string.Empty) + "\n" + (_lastRunContext?.Objective ?? string.Empty));
        }

        private static async Task<string> RunJavaAsync(string code, string tempDir)
        {
            // Extract class name from code
            var classMatch = System.Text.RegularExpressions.Regex.Match(code, @"public\s+class\s+(\w+)");
            string className = classMatch.Success ? classMatch.Groups[1].Value : "Main";

            string javaPath = Path.Combine(tempDir, $"{className}.java");
            await File.WriteAllTextAsync(javaPath, code);

            string javacExe = FindExecutable("javac") ?? "javac";
            string compileResult = await RunProcessAsync(javacExe, $"\"{javaPath}\"", tempDir);

            if (compileResult.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return $"Compilation errors:\n{compileResult}";
            }

            string javaExe = FindExecutable("java") ?? "java";
            string runResult = await RunProcessAsync(javaExe, $"-cp \"{tempDir}\" {className}", tempDir);
            return string.IsNullOrWhiteSpace(compileResult)
                ? runResult
                : $"Compile output:\n{compileResult}\n\nRun output:\n{runResult}";
        }

        private static string ValidateHtml(string code, string tempDir, CouncilRunContext? context = null)
        {
            string htmlPath = Path.Combine(tempDir, "sandbox_preview.html");
            File.WriteAllText(htmlPath, code);

            return BuildProjectCanvasSandboxResult(code, htmlPath, context);
        }

        private static string? FindExecutable(string name)
        {
            string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
            string ext = OperatingSystem.IsWindows() ? ".exe" : "";

            foreach (string dir in pathEnv.Split(Path.PathSeparator))
            {
                string full = Path.Combine(dir, name + ext);
                if (File.Exists(full))
                    return full;
            }
            return null;
        }

        private static async Task<string> RunProcessAsync(string fileName, string arguments, string workingDir)
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            try
            {
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                bool exited = await Task.Run(() => process.WaitForExit(15_000));
                if (!exited)
                {
                    try { process.Kill(true); } catch { }
                    return "Execution timed out (15s limit).";
                }
            }
            catch (Exception ex)
            {
                return $"Failed to start {fileName}: {ex.Message}";
            }

            string result = output.ToString();
            string err = error.ToString();

            if (!string.IsNullOrWhiteSpace(err))
                result += (string.IsNullOrWhiteSpace(result) ? "" : "\n") + "stderr:\n" + err;

            return string.IsNullOrWhiteSpace(result) ? "(no output)" : result.Trim();
        }
    }
}
