using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Malx_AI.ComputerUse;
using Malx_AI.Mcp;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private List<DocumentInfo>? _computerUseTurnImages;
        private bool _workplaceMentionPopupOpen;
        private int _workplaceMentionAtIndex = -1;
        private int _workplaceMentionSelectedIndex;

        private ComputerUseCapabilityResult ProbeComputerUseCapability()
        {
            CouncilModelConfig acting = GetEffectiveRoleConfig(CouncilRole.Builder);
            string label = _isCloudModeEnabled
                ? WorkplaceCloudRoleDisplayName
                : string.IsNullOrWhiteSpace(acting.DisplayName) ? "Workplace agent" : acting.DisplayName;
            string idOrPath = _isCloudModeEnabled
                ? (_isHybridLocalCouncilSelected
                    ? (_openRouterChatService.CustomEndpointConfiguredModelId ?? GetEffectiveCouncilModelId())
                    : GetEffectiveCouncilModelId())
                : acting.ModelPath ?? string.Empty;
            if (_isCloudModeEnabled && _isHybridLocalCouncilSelected && string.IsNullOrWhiteSpace(label))
                label = OpenRouterChatService.CustomEndpointModelLabel;
            bool catalogVision = _isCloudModeEnabled && _openRouterChatService.SupportsImageInput(GetEffectiveCouncilModelId());
            bool catalogKnown = !_isCloudModeEnabled || _openRouterChatService.HasImageInputCatalog;
            bool projector = !_isCloudModeEnabled && ComputerUseCapability.HasLocalProjector(acting.ModelPath);
            return ComputerUseCapability.Evaluate(
                _isCloudModeEnabled,
                _isHybridLocalCouncilSelected,
                catalogVision,
                catalogKnown,
                label,
                idOrPath,
                projector);
        }

        private async Task RunComputerUseSessionFromChatAsync(string userQuery)
        {
            ComputerUseCapabilityResult capability = ProbeComputerUseCapability();
            string goal = ComputerUseMention.StripMentions(userQuery);
            if (!capability.CanRun)
            {
                AppendChat("error", capability.Reason);
                FinishComputerUseRunUi();
                return;
            }

            if (string.IsNullOrWhiteSpace(goal))
            {
                AppendChat("system", "Computer Use is ready. Add a short goal after @ComputerUse, for example: @ComputerUse open Notepad and type hello.");
                FinishComputerUseRunUi();
                return;
            }

            Window? host = Window.GetWindow(this);
            if (host == null)
            {
                AppendChat("error", "Computer Use could not find the main window.");
                FinishComputerUseRunUi();
                return;
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;
            string navigationContext = await ResolveComputerUseNavigationContextAsync(goal, token);
            if (_isCloudModeEnabled && _isHybridLocalCouncilSelected)
            {
                LoadCustomEndpointForWorkplace();
                try
                {
                    await _openRouterChatService.RefreshCustomEndpointMetadataAsync(token, force: true);
                }
                catch (Exception refreshEx)
                {
                    LogActivity($"Hybrid Local metadata refresh skipped: {refreshEx.Message}");
                }

                capability = ProbeComputerUseCapability();
                if (!capability.CanRun)
                {
                    AppendChat("error", capability.Reason);
                    FinishComputerUseRunUi();
                    return;
                }
            }

            RelayStatusBlock.Text = "Relay: Computer Use";
            PublishCouncilPetStatus("Computer Use", "Operating the desktop.");

            if (!_isCloudModeEnabled && ReleaseHostChatModelAsync != null)
            {
                try
                {
                    await ReleaseHostChatModelAsync(CancellationToken.None);
                }
                catch (Exception releaseEx)
                {
                    LogActivity($"Chat model release before Computer Use skipped: {releaseEx.Message}");
                }
            }

            try
            {
                ComputerUseSessionResult result = await ComputerUseSessionController.RunAsync(
                    new ComputerUseSessionRequest
                    {
                        Goal = goal,
                        HostWindow = host,
                        InitialMode = ComputerUseMode.Ask,
                        ActingModelLabel = capability.ActingModelLabel,
                        ExecutionSurface = capability.ExecutionSurface,
                        NavigationContext = navigationContext,
                        InferAsync = InferComputerUseTurnAsync,
                        VerifyAsync = InferComputerUseVerificationAsync,
                        PlanAsync = InferComputerUsePlanAsync,
                        OnChat = message => Dispatcher.Invoke(() =>
                        {
                            LogActivity(message);
                            AppendChat("system", message);
                        }),
                        OnStop = () =>
                        {
                            try { _cancellationTokenSource?.Cancel(); } catch { }
                        }
                    },
                    token);

                AppendChat("system", result.Summary);
                LogActivity($"Computer Use {(result.Completed ? "completed" : "stopped unfinished")} after {result.Steps} step(s). {result.Summary}");
            }
            catch (OperationCanceledException)
            {
                AppendChat("system", "Computer Use stopped by the user.");
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("Workplace.ComputerUse", ex);
                AppendChat("error", "Computer Use failed: " + ex.Message);
            }
            finally
            {
                _computerUseTurnImages = null;
                FinishComputerUseRunUi();
            }
        }

        private async Task<string> ResolveComputerUseNavigationContextAsync(string goal, CancellationToken token)
        {
            if (!ComputerUseGoalAnalysis.TryGetRequestedOwnedGitHubRepository(goal, out string repositoryName))
                return string.Empty;

            if (ComputerUseTrustedNavigation.TryGetGitHubRepositoryUrl(
                _connectedWorkspace.RepositoryUrl,
                repositoryName,
                out string workspaceRepositoryUrl))
            {
                return ComputerUseTrustedNavigation.CreateContext(workspaceRepositoryUrl)
                    + " The connected workspace matches this requested repository. Navigate to exactly this URL; do not derive or replace its owner. The controller will only accept a non-error page at that exact address as complete.";
            }

            if (_mcpConnectorService?.IsGitHubConnected != true)
            {
                return $"The request refers to the user's GitHub repository named '{repositoryName}', but no connected GitHub identity can resolve its owner. Never invent a github.com owner or treat a search guess as success. Use visible, non-error evidence only; otherwise finish this sub-goal as blocked and ask for the repository URL.";
            }

            try
            {
                string args = System.Text.Json.JsonSerializer.Serialize(new { type = "all", max_results = 50 });
                McpToolExecutionResult repositories = await _mcpConnectorService
                    .ExecuteToolAsync("github_list_repos", args, token)
                    .ConfigureAwait(true);
                if (!repositories.Success
                    || !ComputerUseTrustedNavigation.TryFindUniquePublicRepositoryUrl(repositories.Result, repositoryName, out string verifiedUrl))
                {
                    return $"No unique public repository named '{repositoryName}' could be established from connected workspace or GitHub evidence. Do not guess an owner or construct a repository URL; finish this sub-goal as blocked and ask for its URL.";
                }

                return ComputerUseTrustedNavigation.CreateContext(verifiedUrl)
                    + " This destination was uniquely confirmed from connected GitHub data. Navigate to exactly this URL; do not derive or replace its owner. The controller will only accept a non-error page at that exact address as complete.";
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogActivity($"Computer Use GitHub destination lookup skipped: {ex.Message}");
                return $"The GitHub repository owner could not be verified for '{repositoryName}'. Do not guess an owner or construct a repository URL.";
            }
        }

        private Task<string> InferComputerUseTurnAsync(string systemPrompt, string userPayload,
            ComputerUseCapture capture, CancellationToken token)
            => InferComputerUseResponseAsync(systemPrompt, userPayload, capture, token,
                value => ComputerUseActionParser.Parse(value).Parsed);

        private Task<string> InferComputerUseVerificationAsync(string systemPrompt, string userPayload,
            ComputerUseCapture capture, CancellationToken token)
            => InferComputerUseResponseAsync(systemPrompt, userPayload, capture, token,
                value => !string.IsNullOrWhiteSpace(ComputerUseVisualAssessment.Parse(value).Evidence));

        private Task<string> InferComputerUsePlanAsync(string systemPrompt, string userPayload,
            ComputerUseCapture capture, CancellationToken token)
        {
            using var document = System.Text.Json.JsonDocument.Parse(userPayload);
            string request = document.RootElement.GetProperty("request").GetString() ?? "";
            return InferComputerUseResponseAsync(systemPrompt, userPayload, capture, token,
                value => ComputerUseTaskContract.TryParse(value, request, out _), retryInvalidResponse: false, includeScreenshot: false);
        }

        private async Task<string> InferComputerUseResponseAsync(
            string systemPrompt,
            string userPayload,
            ComputerUseCapture capture,
            CancellationToken token,
            Func<string, bool> isValidResponse,
            bool retryInvalidResponse = true,
            bool includeScreenshot = true)
        {
            _computerUseTurnImages = !includeScreenshot ? [] :
            [
                new DocumentInfo
                {
                    Name = "computer-use-screenshot.jpg",
                    FilePath = "",
                    Type = "image",
                    Info = $"{capture.ImageWidth}x{capture.ImageHeight}",
                    MimeType = capture.MimeType,
                    Base64Data = Convert.ToBase64String(capture.JpegBytes),
                    IsImage = true
                }
            ];

            try
            {
                async Task<ReasoningParser.ParsedResponse> InferAsync(string prompt, string payload)
                {
                    if (_isCloudModeEnabled)
                    {
                        var message = new OpenRouterMessage("user", payload, PreserveFullText: true,
                            ImageDataUrls: includeScreenshot ? [$"data:{capture.MimeType};base64,{Convert.ToBase64String(capture.JpegBytes)}"] : null);
                        var response = await _openRouterChatService.SendConversationStreamAsync([message],
                            _openRouterChatService.BuildSystemPromptForModel(GetEffectiveCouncilModelId(), prompt),
                            thinkingEnabled: false, modelId: GetEffectiveCouncilModelId(), tools: null,
                            cancellationToken: token, maxTokensOverride: includeScreenshot ? 1024 : 2048,
                            allowModelFallback: false, requireCompleteResponse: true).ConfigureAwait(true);
                        return new ReasoningParser.ParsedResponse { Answer = response.Text, ThinkingContent = response.Reasoning };
                    }
                    return await ExecuteCouncilRoleAsync(
                    CouncilRole.Builder,
                    prompt,
                    payload,
                    token,
                    showLiveCard: false,
                    maxGenerationTokensOverride: includeScreenshot ? 1024 : 2048,
                    useBuilderToolDecision: false,
                    allowAgenticPauses: false,
                    internalInferenceStep: true);
                }

                ReasoningParser.ParsedResponse parsed = await InferAsync(systemPrompt, userPayload);

                string answer = parsed.Answer ?? string.Empty;
                if (string.IsNullOrWhiteSpace(answer) && parsed.HasThinking)
                    answer = parsed.ThinkingContent ?? string.Empty;

                // A malformed response (often a lone ```json fence) should not consume a
                // desktop-control step. Retry once with the same fresh screenshot and a
                // deliberately small response contract.
                if (retryInvalidResponse && !isValidResponse(answer))
                {
                    ReasoningParser.ParsedResponse retry = await InferAsync(
                        systemPrompt + "\nFORMAT RECOVERY: Return one compact JSON object now. Do not use Markdown fences or prose.",
                        userPayload + "\n[FORMAT RECOVERY] Return only the JSON object required by the system schema.");
                    string retryAnswer = retry.Answer ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(retryAnswer) && retry.HasThinking)
                        retryAnswer = retry.ThinkingContent ?? string.Empty;
                    if (isValidResponse(retryAnswer))
                        answer = retryAnswer;
                }
                return answer;
            }
            finally
            {
                _computerUseTurnImages = null;
            }
        }

        private void FinishComputerUseRunUi()
        {
            _isProcessing = false;
            SendButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            RelayStatusBlock.Text = "Relay: Idle";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = null;
        }

        private void UpdateWorkplaceMentionPopup()
        {
            if (QueryInput == null || WorkplaceMentionPopup == null || WorkplaceMentionList == null)
            {
                CloseWorkplaceMentionPopup();
                return;
            }

            string text = QueryInput.Text ?? string.Empty;
            int caret = QueryInput.CaretOffset;
            if (!McpMentionHelper.TryGetActiveMentionQuery(text, caret, out int atIndex, out string query))
            {
                CloseWorkplaceMentionPopup();
                return;
            }

            var matches = new List<McpConnectorInfo>();
            if (ComputerUseMention.MatchesQuery(query))
                matches.Add(ComputerUseMention.CreatePickerOption());

            if (WorkplaceProjectCanvasMentionMatches(query))
                matches.Add(CreateWorkplaceProjectCanvasMentionOption());

            // Connector mentions are a Cloud capability, but their picker must be shared by
            // single-model and three-role Council runs.  Tool exposure is resolved later from
            // the submitted prompt, so this is deliberately not tied to a particular role.
            if (_isCloudModeEnabled && _mcpConnectorService != null)
            {
                matches.AddRange(McpMentionHelper.FilterConnectors(
                    _mcpConnectorService.GetConnectors(),
                    query,
                    connectedOnly: false));
            }

            if (matches.Count == 0)
            {
                CloseWorkplaceMentionPopup();
                return;
            }

            if (!string.IsNullOrEmpty(query)
                && matches.Count == 1
                && string.Equals(matches[0].Handle, query, StringComparison.OrdinalIgnoreCase))
            {
                CloseWorkplaceMentionPopup();
                _workplaceMentionAtIndex = atIndex;
                return;
            }

            _workplaceMentionAtIndex = atIndex;
            WorkplaceMentionList.ItemsSource = matches;
            _workplaceMentionSelectedIndex = 0;
            WorkplaceMentionList.SelectedIndex = 0;
            WorkplaceMentionPopup.IsOpen = true;
            _workplaceMentionPopupOpen = true;
        }

        private void CloseWorkplaceMentionPopup()
        {
            _workplaceMentionPopupOpen = false;
            _workplaceMentionAtIndex = -1;
            if (WorkplaceMentionPopup != null)
                WorkplaceMentionPopup.IsOpen = false;
            if (WorkplaceMentionList != null)
                WorkplaceMentionList.ItemsSource = null;
        }

        private void WorkplaceQueryInput_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_workplaceMentionPopupOpen && WorkplaceMentionList != null)
            {
                int count = WorkplaceMentionList.Items.Count;
                if (count > 0)
                {
                    if (e.Key == Key.Escape)
                    {
                        CloseWorkplaceMentionPopup();
                        e.Handled = true;
                        return;
                    }

                    if (e.Key == Key.Down)
                    {
                        _workplaceMentionSelectedIndex = Math.Min(count - 1, _workplaceMentionSelectedIndex + 1);
                        WorkplaceMentionList.SelectedIndex = _workplaceMentionSelectedIndex;
                        e.Handled = true;
                        return;
                    }

                    if (e.Key == Key.Up)
                    {
                        _workplaceMentionSelectedIndex = Math.Max(0, _workplaceMentionSelectedIndex - 1);
                        WorkplaceMentionList.SelectedIndex = _workplaceMentionSelectedIndex;
                        e.Handled = true;
                        return;
                    }

                    if (e.Key is Key.Tab or Key.Enter)
                    {
                        if (WorkplaceMentionList.SelectedItem is McpConnectorInfo connector)
                            ApplyWorkplaceMentionCompletion(connector);
                        e.Handled = true;
                        return;
                    }
                }
            }

            // Ctrl+V pastes images or files from clipboard into the attachment tray
            if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (TryHandleClipboardAttachmentPaste())
                {
                    e.Handled = true;
                    return;
                }
            }

            // Plain Enter sends the prompt; Shift+Enter inserts a newline
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
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void WorkplaceMentionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (WorkplaceMentionList?.SelectedItem is McpConnectorInfo connector)
                ApplyWorkplaceMentionCompletion(connector);
        }

        private void ApplyWorkplaceMentionCompletion(McpConnectorInfo connector)
        {
            if (QueryInput == null || connector == null || _workplaceMentionAtIndex < 0)
                return;

            int caret = QueryInput.CaretOffset;
            string next = McpMentionHelper.ApplyMentionCompletion(QueryInput.Text ?? string.Empty, _workplaceMentionAtIndex, caret, connector.Handle);
            QueryInput.Document.Text = next;
            int newCaret = Math.Min(next.Length, _workplaceMentionAtIndex + 1 + connector.Handle.Length + 1);
            QueryInput.CaretOffset = newCaret;
            QueryInput.Focus();
            CloseWorkplaceMentionPopup();
        }

        private static McpConnectorInfo CreateWorkplaceProjectCanvasMentionOption() => new()
        {
            Id = "workplace-project-canvas",
            Handle = "ProjectCanvas",
            DisplayName = "Project Canvas",
            Description = "Render the completed Workplace response as an artifact.",
            Kind = McpConnectorKind.GitHub,
            LogoGlyph = "\u25C7",
            IsConnected = true,
            AccountLabel = "Artifact rendering · all Workplace modes"
        };

        private static bool WorkplaceProjectCanvasMentionMatches(string? query)
        {
            string normalized = (query ?? string.Empty).Trim();
            return normalized.Length == 0
                || "ProjectCanvas".Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || "Project Canvas".Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || "artifact".Contains(normalized, StringComparison.OrdinalIgnoreCase);
        }
    }
}
