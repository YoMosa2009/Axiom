using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Malx_AI.ComputerUse;
using Malx_AI.Mcp;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private string _computerUseGoal = "";
        private string _computerUseSummary = "";
        private string _computerUseApplication = "";

        private void ClearComputerUseContext()
        {
            _computerUseGoal = _computerUseSummary = _computerUseApplication = "";
            if (ComputerUseContextButton != null) ComputerUseContextButton.Visibility = Visibility.Collapsed;
        }

        private void ComputerUseContextButton_Click(object sender, RoutedEventArgs e)
        {
            ClearComputerUseContext();
            AppendVisibleNotice("Returned to Agent Access. The previous desktop work remains in this chat.");
            RequestWorkspaceStateSave();
        }
        private List<DocumentInfo>? _computerUseTurnImages;
        private string _activeComputerUseNavigationContext = string.Empty;
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
            try { await RunComputerUseSessionFromChatCoreAsync(userQuery); }
            finally { if (_isProcessing) FinishComputerUseRunUi(); }
        }

        private async Task RunComputerUseSessionFromChatCoreAsync(string userQuery)
        {
            // Capability is judged from the model catalog; without it the check can only guess
            // from the model's name, which wrongly blocked vision models with plain names.
            if (_isCloudModeEnabled && !_isHybridLocalCouncilSelected)
            {
                try
                {
                    LoadOpenRouterKeyForWorkplace();
                    LoadManagedEndpointForWorkplace();
                    await _openRouterChatService.EnsureModelCatalogAsync(CancellationToken.None);
                }
                catch (Exception catalogEx)
                {
                    LogActivity($"Model catalog refresh skipped: {catalogEx.Message}");
                }
            }

            ComputerUseCapabilityResult capability = ProbeComputerUseCapability();
            string goal = ComputerUseMention.StripMentions(userQuery);
            string continuation = string.IsNullOrWhiteSpace(_computerUseGoal) ? "" :
                $"[PREVIOUS COMPUTER USE TASK]\n{_computerUseGoal}\n[PREVIOUS RESULT]\n{_computerUseSummary}\n"
                + $"[WORK APPLICATION]\n{_computerUseApplication}\n"
                + "The current user message takes priority. Interpret it using this context. Preserve existing work; do not search unrelated applications or project files to reconstruct it.";
            ConversationEvidence.Current?.Record("Computer Use request", goal + "\n" + continuation);
            if (!capability.CanRun)
            {
                string suggestion = !_isCloudModeEnabled
                    ? "\n\nTip: In Settings → Cloud AI, Axiom Cloud (DeepSeek 4.1F-1017, $9/mo) includes vision and @ComputerUse out of the box."
                    : string.Empty;
                AppendVisibleNotice(capability.Reason + suggestion);
                FinishComputerUseRunUi();
                return;
            }

            if (string.IsNullOrWhiteSpace(goal))
            {
                AppendVisibleNotice("Computer Use is ready. Add a short goal after @ComputerUse, for example: @ComputerUse open Notepad and type hello.");
                FinishComputerUseRunUi();
                return;
            }

            Window? host = Window.GetWindow(this);
            if (host == null)
            {
                AppendVisibleNotice("Computer Use could not find the main window.");
                FinishComputerUseRunUi();
                return;
            }

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;
            string navigationContext = await ResolveComputerUseNavigationContextAsync(goal, token);
            _activeComputerUseNavigationContext = navigationContext;
            navigationContext = continuation + "\n[RECENT CHAT]\n"
                + string.Join("\n\n", _chatCards.TakeLast(6).Select(c => c.Role + ": " + c.Content)) + "\n" + navigationContext;
            _activeComputerUseNavigationContext = navigationContext;
            _computerUseGoal = goal;
            ComputerUseContextButton.Visibility = Visibility.Visible;
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
                    AppendVisibleNotice(capability.Reason);
                    FinishComputerUseRunUi();
                    return;
                }
            }

            RelayStatusBlock.Text = "Relay: Computer Use";
            PublishCouncilPetStatus("Computer Use", "Operating the desktop.");
            const string runTitle = "Computer Use";
            AgentRunCard runCard = StartAgentRunCard(goal, runTitle);

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
                        InitialMode = _agentApprovalMode == Malx_AI.Agent.AgentApprovalMode.Auto ? ComputerUseMode.Autopilot : ComputerUseMode.Ask,
                        ActingModelLabel = capability.ActingModelLabel,
                        ExecutionSurface = capability.ExecutionSurface,
                        NavigationContext = navigationContext,
                        InitialApplication = _computerUseApplication,
                        OnApplication = application => _computerUseApplication = application,
                        OnEvidence = (kind, content) => ConversationEvidence.Current?.Record("Computer Use " + kind, content),
                        OnCapture = capture => ConversationEvidence.Current?.SaveImage(capture.JpegBytes, ".jpg"),
                        InferAsync = InferComputerUseTurnAsync,
                        VerifyAsync = InferComputerUseVerificationAsync,
                        PlanAsync = InferComputerUsePlanAsync,
                        OnChat = message => Dispatcher.Invoke(() =>
                        {
                            LogActivity(message);
                            AppendAgentRunCardLine(runCard, message);
                        }),
                        OnStop = () =>
                        {
                            try { _cancellationTokenSource?.Cancel(); } catch { }
                        }
                    },
                    token);

                _computerUseSummary = result.Summary;
                ConversationEvidence.Current?.Record("Computer Use result", result.Summary);
                CompleteAgentRunCard(runCard, result.Summary);
                _chatHistory.Add(("assistant", result.Summary));
                LogActivity($"Computer Use {(result.Completed ? "completed" : "stopped unfinished")} after {result.Steps} step(s). {result.Summary}");
                if (!token.IsCancellationRequested)
                    CouncilRunFinished?.Invoke(result.Completed
                        ? "Computer Use finished."
                        : "Computer Use stopped before completion. Open Axiom to review.");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                _computerUseSummary = "Computer Use stopped by the user; the requested change was not verified complete.";
                ConversationEvidence.Current?.Record("Computer Use stopped", _computerUseSummary);
                CompleteAgentRunCard(runCard, "Computer Use stopped by the user.");
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("Workplace.ComputerUse", ex);
                CompleteAgentRunCard(runCard, "Computer Use failed: " + ex.Message);
                if (!token.IsCancellationRequested)
                    CouncilRunFinished?.Invoke("Computer Use stopped with an error.");
            }
            finally
            {
                _computerUseTurnImages = null;
                FinishComputerUseRunUi();
                NotifyManagedUsageChangedIfActive();
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

        private async Task<string> InferComputerUsePlanAsync(string systemPrompt, string userPayload,
            ComputerUseCapture capture, CancellationToken token)
        {
            using var document = System.Text.Json.JsonDocument.Parse(userPayload);
            string request = document.RootElement.GetProperty("request").GetString() ?? "";
            string navContext = document.RootElement.TryGetProperty("navigation_context", out var navEl)
                ? (navEl.GetString() ?? _activeComputerUseNavigationContext)
                : _activeComputerUseNavigationContext;

            string raw = string.Empty;
            try
            {
                raw = await InferComputerUseResponseAsync(systemPrompt, userPayload, capture, token,
                    value => ComputerUseTaskContract.TryParse(value, request, out _, out _, out _, strictCitations: false),
                    retryInvalidResponse: true, includeScreenshot: false).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogActivity($"Computer Use model planning failed, falling back to deterministic plan: {ex.Message}");
            }

            if (ComputerUseTaskContract.TryParse(raw, request, out _, out _, out _, strictCitations: false))
                return raw;

            // Preserve explicit planner refusal (e.g. unresolvable owned destination error) if stated cleanly.
            if (!string.IsNullOrWhiteSpace(raw))
            {
                try
                {
                    int first = raw.IndexOf('{'), last = raw.LastIndexOf('}');
                    if (first >= 0 && last > first)
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(raw[first..(last + 1)]);
                        if (doc.RootElement.TryGetProperty("error", out var errProp)
                            && !string.IsNullOrWhiteSpace(errProp.GetString()))
                        {
                            return raw;
                        }
                    }
                }
                catch { }
            }

            LogActivity("Using deterministic Computer Use multi-step fallback plan.");
            return ComputerUseDeterministicPlanBuilder.BuildFallbackPlanJson(request, navContext);
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
                            cancellationToken: token, maxTokensOverride: includeScreenshot ? 2048 : 2048,
                            allowModelFallback: false, requireCompleteResponse: true).ConfigureAwait(true);
                        return new ReasoningParser.ParsedResponse { Answer = response.Text, ThinkingContent = response.Reasoning };
                    }
                    return await ExecuteCouncilRoleAsync(
                    CouncilRole.Builder,
                    prompt,
                    payload,
                    token,
                    showLiveCard: false,
                    maxGenerationTokensOverride: includeScreenshot ? 1536 : 2048,
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
            if (!IsVisible || QueryInput == null || !QueryInput.IsKeyboardFocusWithin
                || WorkplaceMentionPopup == null || WorkplaceMentionList == null)
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
                        WorkplaceMentionList.ScrollIntoView(WorkplaceMentionList.SelectedItem);
                        e.Handled = true;
                        return;
                    }

                    if (e.Key == Key.Up)
                    {
                        _workplaceMentionSelectedIndex = Math.Max(0, _workplaceMentionSelectedIndex - 1);
                        WorkplaceMentionList.SelectedIndex = _workplaceMentionSelectedIndex;
                        WorkplaceMentionList.ScrollIntoView(WorkplaceMentionList.SelectedItem);
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
                // While the agent runs, Enter sends the message to it (see TryPostMessageToRunningAgent).
                if (!SendButton.IsEnabled || (_isProcessing && _agentInbox == null))
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
            if (e.OriginalSource is DependencyObject source
                && ItemsControl.ContainerFromElement(WorkplaceMentionList, source) is ListBoxItem { Content: McpConnectorInfo connector })
            {
                ApplyWorkplaceMentionCompletion(connector);
                e.Handled = true;
            }
        }

        private void ApplyWorkplaceMentionCompletion(McpConnectorInfo connector)
        {
            if (QueryInput == null || connector == null || _workplaceMentionAtIndex < 0)
                return;

            int caret = QueryInput.CaretOffset;
            int atIndex = _workplaceMentionAtIndex;
            string next = McpMentionHelper.ApplyMentionCompletion(QueryInput.Text ?? string.Empty, atIndex, caret, connector.Handle);
            QueryInput.Document.Text = next;
            int newCaret = Math.Min(next.Length, atIndex + 1 + connector.Handle.Length + 1);
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
