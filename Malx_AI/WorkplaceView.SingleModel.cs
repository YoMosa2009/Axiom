using Microsoft.Win32;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private readonly CouncilModelConfig _singleModel = new();

        private string GetExecutionRoleDisplayName(CouncilRole role)
            => _isSingleModelMode && role == CouncilRole.Builder ? "Agent" : role.ToString();

        private void SingleModelModeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                AppendChat("system", "Wait for the active run to finish before switching Workplace modes.");
                return;
            }

            _isSingleModelMode = !_isSingleModelMode;
            RefreshSingleModelModeUi();
            ResetWorkplaceTokenUsageIndicator();
            AppendChat("system", _isSingleModelMode
                ? "Single Model mode enabled. One agent now plans, uses tools, executes, and verifies each request."
                : "Council mode enabled. Architect, Builder, and Critic stages are active.");
            SavePersistedSession();
        }

        private void LoadSingleModel_Click(object sender, RoutedEventArgs e)
        {
            if (_isCloudModeEnabled)
            {
                AppendChat("system", $"Cloud mode already uses {WorkplaceCloudRoleDisplayName} as the single agent.");
                return;
            }

            var dialog = new OpenFileDialog { Filter = "GGUF files (*.gguf)|*.gguf" };
            if (dialog.ShowDialog() != true)
                return;

            if (IsMmprojFile(dialog.FileName))
            {
                AppendChat("error", "Selected file is an mmproj projector, not a text model. Select the main non-mmproj .gguf model file.");
                return;
            }

            string? previousPath = _singleModel.ModelPath;
            _singleModel.ModelPath = dialog.FileName;
            _singleModel.DisplayName = IsQwen3Model(dialog.FileName)
                ? ModelInferenceProfiles.DefaultQwen3DisplayName
                : Path.GetFileNameWithoutExtension(dialog.FileName);
            _singleModel.Format = IsGemma4Model(dialog.FileName)
                ? PromptFormat.Gemma4
                : ParsePromptFormat(SingleModelFormatCombo.SelectedItem as ComboBoxItem);

            if (!string.IsNullOrWhiteSpace(previousPath)
                && !string.Equals(previousPath, dialog.FileName, StringComparison.OrdinalIgnoreCase)
                && !_council.Values.Any(config => string.Equals(config.ModelPath, previousPath, StringComparison.OrdinalIgnoreCase))
                && _modelCache.TryGetValue(previousPath, out var cached))
            {
                cached.Dispose();
                _modelCache.Remove(previousPath);
            }

            RefreshSingleModelModeUi();
            UpdateContextInfo();
            AppendChat("system", $"Single agent model set: {_singleModel.DisplayName}");
            LogActivity($"Single agent model configured: {_singleModel.DisplayName}");
            SavePersistedSession();
        }

        private void SingleModelContextSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!IsLoaded || SingleModelContextSlider == null)
                return;

            _singleModelContextSize = Math.Clamp((uint)Math.Round(SingleModelContextSlider.Value), MinRoleContext, MaxRoleContext);
            if (SingleModelContextValueText != null)
                SingleModelContextValueText.Text = $"{_singleModelContextSize} tokens";
            UpdateContextInfo();
            SavePersistedSession();
        }

        private void RefreshSingleModelModeUi()
        {
            if (SingleModelModeToggleButton == null)
                return;

            Visibility councilVisibility = _isSingleModelMode ? Visibility.Collapsed : Visibility.Visible;
            Visibility singleVisibility = _isSingleModelMode ? Visibility.Visible : Visibility.Collapsed;

            WorkplaceModeTitleText.Text = _isSingleModelMode ? "Workplace Agent" : "Workplace Council";
            SingleModelModeToggleButton.Content = _isSingleModelMode ? "Single Model: On" : "Single Model: Off";
            SingleModelModeToggleButton.Opacity = _isSingleModelMode ? 1.0 : 0.72;
            CouncilSubtitleText.Visibility = councilVisibility;
            SingleModelSubtitleText.Visibility = singleVisibility;

            ArchitectModelCard.Visibility = councilVisibility;
            RoleContextControlsCard.Visibility = councilVisibility;
            ManualRoleContextControlsCard.Visibility = councilVisibility;
            SingleModelModelCard.Visibility = singleVisibility;
            SingleModelContextCard.Visibility = _isSingleModelMode && !_isCloudModeEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;

            SingleModelModelBlock.Text = _isCloudModeEnabled
                ? WorkplaceCloudRoleDisplayName
                : _singleModel.DisplayName;
            SingleModelFormatCombo.SelectedIndex = _singleModel.Format switch
            {
                PromptFormat.Llama3 => 1,
                PromptFormat.Alpaca => 2,
                PromptFormat.Gemma4 => 3,
                _ => 0
            };
            SingleModelContextSlider.Value = _singleModelContextSize;
            SingleModelContextValueText.Text = $"{_singleModelContextSize} tokens";

            StageIndicatorGrid.Columns = _isSingleModelMode ? 1 : 3;
            ArchitectStageIndicator.Visibility = councilVisibility;
            CriticStageIndicator.Visibility = councilVisibility;
            BuilderStageText.Text = _isSingleModelMode ? "Agent · Idle" : "Builder · Idle";
            BuilderGenerationStatusText.Text = _isSingleModelMode ? "Agent generating" : "Builder generating";

            ArchitectTokenUsageRow.Visibility = councilVisibility;
            CriticTokenUsageRow.Visibility = councilVisibility;
            BuilderTokenUsageTitle.Text = _isSingleModelMode ? "Agent" : "Builder";
            AgentTokenUsageRow.Margin = _isSingleModelMode ? new Thickness(0) : new Thickness(0, 0, 12, 0);

            SendButton.Content = _isSingleModelMode ? "Run Agent" : "Run Council";
            CanvasSubtitleBlock.Text = _isSingleModelMode ? "Agent output rendered here." : "Builder output rendered here.";
            CouncilContextControlsTitle.Text = _isSingleModelMode ? "Execution Context" : "Role Context Controls";

            RefreshWorkplaceCloudModeUi();
            UpdateStageIndicator(null, false, false, false);
            UpdateWorkplaceTokenUsageIndicator();
        }

        private string BuildSingleModelSystemPrompt(CouncilRunContext context)
        {
            string prompt =
                "You are Axiom's single agentic Workplace model. You are the only model handling this request: understand it, plan privately, use tools when useful, execute it, verify it, and return one complete final result. " +
                "Do not mention internal pipeline stages, role handoffs, or routing labels. " +
                "Never expose hidden reasoning, scratch work, tool protocol text, or a plan unless the user explicitly asks for a plan. " +
                "For code, return complete executable source. For ordinary questions, answer directly. " +
                "The Project Canvas is offline, so HTML/SVG artifacts must be self-contained with inline CSS/JavaScript and no external URLs, CDNs, fonts, images, or libraries. " +
                "Use attached documents, retrieved knowledge, session memory, calculator results, web evidence, and connected-workspace context as authoritative only for what they actually contain." +
                AgenticPauseRule;

            if (_connectedWorkspace.CodebaseEditAccessEnabled)
                prompt += AgenticPauseCodebaseToolsAddendum;

            prompt += context.IsArtifactCanvasRequest && !context.IsWorkspaceTask
                ? BuildSingleModelArtifactBoost(context)
                : BuildSingleModelTaskBoost(context.TaskType);

            if (context.IsWorkspaceTask)
            {
                prompt += "\n[SINGLE AGENT CODEBASE MODE]\nYour final visible answer must be exactly one valid [[AXIOM_CODEBASE_PATCH]] envelope. Inspect files with the read-only codebase tools when needed. Do not claim files changed; the host validates and applies the patch.";
            }

            if (context.IsCalculationTask)
                prompt += "\n[CALCULATION TASK]\nUse calculate or Python for non-trivial arithmetic. State formulas and units clearly, verify conversions, and sanity-check the result before answering. For code, implement every conversion explicitly and include realistic validation cases.";

            prompt += BuildCouncilWebSystemNote(context.WebContext, context.WebGroundingRequired)
                .Replace("council plan", "session context", StringComparison.OrdinalIgnoreCase);
            return ComposeCouncilSystemPrompt(prompt, CouncilRole.Builder, context, 0);
        }

        private static string BuildSingleModelTaskBoost(CouncilTaskType taskType)
        {
            return taskType switch
            {
                CouncilTaskType.Coding =>
                    "\n[CODING TASK]\nReturn real, complete, executable code with no placeholders. Define every referenced symbol, preserve requested identifiers, handle likely edge cases, and mentally trace representative inputs before finalizing. Output code only unless the user explicitly asks for explanation.",
                CouncilTaskType.Research =>
                    "\n[RESEARCH TASK]\nWrite a direct, well-organized answer grounded in provided sources and retrieved evidence. Separate confirmed facts from uncertainty. Do not invent citations or unsupported current facts.",
                CouncilTaskType.Analysis =>
                    "\n[ANALYSIS TASK]\nApply a clear analytical framework, connect each conclusion to evidence, distinguish fact from inference, and answer the decision or question directly.",
                CouncilTaskType.Document =>
                    "\n[DOCUMENT TASK]\nThe attached document text is already present in the payload. Work directly from its content. Do not give instructions for opening, parsing, or accessing the file, and do not claim the document is unavailable.",
                _ =>
                    "\n[GENERAL TASK]\nAnswer the latest request directly and completely. Use concise structure where it improves clarity; do not output a plan or meta-commentary."
            };
        }

        private static string BuildSingleModelArtifactBoost(CouncilRunContext context)
        {
            string formatHint = string.IsNullOrWhiteSpace(context.PreferredArtifactFormatHint)
                ? "Prefer one complete self-contained HTML/CSS/JavaScript artifact when no format is specified."
                : context.PreferredArtifactFormatHint.Trim();
            return "\n[PROJECT CANVAS ARTIFACT TASK]\nReturn one complete renderable artifact wrapped in exactly one correctly tagged code fence and nothing outside it. " +
                "Use HTML with inline CSS/JavaScript for interactive, animated, calculated, or stateful behavior; use standalone SVG only for static or explicitly requested SVG output. " +
                BuildCanvasEnvironmentSpec(context.CanvasViewportWidth, context.CanvasViewportHeight) + " " +
                formatHint;
        }

        private string BuildSingleModelPayload(
            CouncilRunContext context,
            ContextStateObject state,
            string knowledgePacket,
            bool hasKnowledge,
            string sharedVocabularySection)
        {
            var payload = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(state.TaskContract))
                payload.AppendLine(AdaptSingleModelPromptText(state.TaskContract.Trim()));

            payload.AppendLine(BuildRecentConversationContext(CanUseCloudCouncil ? 10 : 4, CanUseCloudCouncil ? 1600 : 900));
            string priorKnowledge = BuildPriorKnowledgeBlock(_sessionHippocampus.Query(context.UserPrompt, 4));
            if (!string.IsNullOrWhiteSpace(priorKnowledge))
                payload.AppendLine(priorKnowledge);
            if (!string.IsNullOrWhiteSpace(sharedVocabularySection))
                payload.AppendLine(sharedVocabularySection);

            AppendCouncilWebContext(payload, context);
            if (!string.IsNullOrWhiteSpace(context.Objective))
                payload.AppendLine(BuildLabeledBlock("OBJECTIVE", context.Objective));
            payload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", context.UserPrompt));

            if (!string.IsNullOrWhiteSpace(state.WorkspaceContext))
                payload.AppendLine(BuildLabeledBlock("CONNECTED CODEBASE CONTEXT", state.WorkspaceContext));
            if (!string.IsNullOrWhiteSpace(context.CalculatorContext))
                payload.AppendLine(context.CalculatorContext.Trim());

            if (context.IsDocumentTask && !string.IsNullOrWhiteSpace(context.DocumentContent))
            {
                int maxDocChars = context.IsCloudExecution
                    ? GetCloudDocumentCharacterBudget(context, 1600)
                    : Math.Max(1600, (((int)GetRoleContextSize(CouncilRole.Builder) - 900) * 4));
                payload.AppendLine(BuildDocumentContentBlock(context.DocumentContent, maxDocChars));
                payload.AppendLine(DocumentGroundingInstruction);
            }
            else if (hasKnowledge)
            {
                payload.AppendLine(BuildLabeledBlock("PROJECT KNOWLEDGE BASE", knowledgePacket));
            }

            if (!string.IsNullOrWhiteSpace(context.CurrentArtifactForIteration))
            {
                payload.AppendLine(BuildLabeledBlock("CURRENT PROJECT CANVAS SOURCE — MODIFY THIS", context.CurrentArtifactForIteration));
                payload.AppendLine("Return the complete updated source. Preserve all unaffected behavior and content.");
            }

            if (context.IsWorkspaceTask)
                payload.AppendLine(BuildLabeledBlock("CODEBASE PATCH OUTPUT CONTRACT", BuildCodebasePatchOutputContractForBuilder()));

            payload.AppendLine("Complete the latest request now. Work as one agent, use tools only when useful, verify the result, and output only the final deliverable.");
            return payload.ToString();
        }

        private static string AdaptSingleModelPromptText(string text)
        {
            return (text ?? string.Empty)
                .Replace("Architect", "planning", StringComparison.OrdinalIgnoreCase)
                .Replace("Builder", "agent", StringComparison.OrdinalIgnoreCase)
                .Replace("Critic", "verification", StringComparison.OrdinalIgnoreCase)
                .Replace("Council", "Workplace", StringComparison.OrdinalIgnoreCase);
        }

        private async Task ExecuteSingleModelRunAsync(
            CouncilRunContext runContext,
            ContextStateObject contextState,
            string knowledgePacket,
            bool hasKnowledge,
            string sharedVocabularySection,
            CancellationToken token,
            int activeRunIndex,
            bool refinementPass,
            string previousFinalForDiff,
            Guid? refinementParentId)
        {
            UpdateStageIndicator(CouncilRole.Builder, false, false, false);
            RelayStatusBlock.Text = "Agent: Working...";
            LogActivity("Single agent run started.");

            string systemPrompt = BuildSingleModelSystemPrompt(runContext);
            string payload = BuildSingleModelPayload(runContext, contextState, knowledgePacket, hasKnowledge, sharedVocabularySection);
            ReasoningParser.ParsedResponse result = await ExecuteCouncilRoleAsync(
                CouncilRole.Builder,
                systemPrompt,
                payload,
                token,
                runContext.IsDocumentTask ? 0.25f : null,
                showLiveCard: true,
                contextSizeOverride: (int)GetRoleContextSize(CouncilRole.Builder));

            string output = result.Answer ?? string.Empty;
            if (!runContext.IsWorkspaceTask
                && TryNormalizeBuilderOutput(output, runContext.TaskType, out string normalized, out _))
            {
                output = normalized;
            }
            output = PostProcessBuilderOutput(output, runContext).Trim();

            if (string.IsNullOrWhiteSpace(output) || result.IsReasoningFallback && LooksLikeReasoningProse(output))
                throw new InvalidOperationException("The single model did not produce a usable final answer.");

            if (runContext.IsDocumentTask && IsLowValueDocumentOutput(output, runContext.DocumentFileNames))
                output = BuildDeterministicDocumentResponse(runContext.UserPrompt, runContext.DocumentContent, runContext.DocumentFileNames);

            if (runContext.WebGroundingRequired
                && !HasCouncilWebEvidenceForRun(runContext)
                && runContext.TaskType != CouncilTaskType.Coding
                && !runContext.IsArtifactCanvasRequest
                && !BuilderStatesWebEvidenceUnavailable(output))
            {
                output = BuildWebEvidenceUnavailableBuilderFallback(runContext);
                AppendChat("warning", "Web evidence was required but unavailable, so unsupported current factual claims were suppressed.");
            }

            bool patchCaptured = false;
            if (runContext.IsWorkspaceTask)
            {
                (output, patchCaptured) = await CaptureSingleModelWorkspacePatchAsync(output, runContext, systemPrompt, token);
            }

            if (runContext.IsArtifactCanvasRequest
                && !ArtifactRenderService.DetectForCanvas(output, null).SupportsPreview)
            {
                string recovered = TryBuildDeterministicArtifactRecovery(runContext, output);
                if (!string.IsNullOrWhiteSpace(recovered))
                    output = recovered;
            }

            runContext.BuilderOutput = output;
            runContext.BuilderThinking = result.ThinkingContent;
            runContext.BuilderProducedCode = DetectCodeOutput(output).IsCode;
            runContext.PipelineMetadata.Add(new StageMetadata { StageName = "Single Agent" });
            contextState.BuilderOutput = output;

            if (runContext.TaskType == CouncilTaskType.Coding || runContext.BuilderProducedCode)
                WriteBuilderSessionMemory(output, activeRunIndex);

            bool routedToCanvas = false;
            if (!patchCaptured && !runContext.IsWorkspaceTask && (runContext.TaskType == CouncilTaskType.Coding
                || runContext.BuilderProducedCode
                || runContext.IsArtifactCanvasRequest && ArtifactRenderService.DetectForCanvas(output, null).SupportsPreview))
            {
                if (runContext.IsProjectCanvasIteration
                    && CanvasHasRealContent(ProjectCanvasEditor.Text)
                    && (CanvasSourcesEquivalent(ProjectCanvasEditor.Text, StripChatFromCode(output))
                        || runContext.IsArtifactCanvasRequest && !ArtifactRenderService.DetectForCanvas(output, null).SupportsPreview))
                {
                    runContext.CanvasMutationFailed = true;
                    AppendChat("warning", "The agent did not produce a verifiable canvas change, so the existing Project Canvas was preserved.");
                }
                else
                {
                    UpdateProjectCanvas(output);
                    runContext.BuilderRoutedToCanvas = true;
                    routedToCanvas = true;
                    AppendChat("agent", _canvasArtifact.SupportsPreview
                        ? $"Generated a renderable {_canvasArtifact.DisplayTitle} and sent it to Project Canvas."
                        : "Output was sent to Project Canvas.");
                }
            }
            else if (!patchCaptured)
            {
                AppendChat("agent", output);
            }

            _chatHistory.Add(("agent", output));
            UpdateWorkplaceTokenUsageIndicator();
            UpdateStageIndicator(null, false, true, false);

            string sandboxResult = string.Empty;
            if (!patchCaptured && (routedToCanvas || runContext.TaskType == CouncilTaskType.Coding))
            {
                string language = DetectLanguage(output);
                if (language is "python" or "java" or "html")
                {
                    sandboxResult = await ExecuteCodeSandboxAsync(output, language, runContext);
                    _lastSandboxOutput = sandboxResult;
                    if (!string.IsNullOrWhiteSpace(sandboxResult))
                        AppendChat("sandbox", sandboxResult);
                    List<string> errors = DetectSandboxErrors(sandboxResult);
                    if (errors.Count > 0)
                    {
                        runContext.SandboxExceptionsFound = true;
                        runContext.StaticValidationFindings.AddRange(errors);
                    }
                }
            }

            string finalOutputForCheck = patchCaptured
                ? output
                : routedToCanvas ? ProjectCanvasEditor.Text : output;
            List<string> verificationFailures = BuildFinalVerificationFailures(runContext, finalOutputForCheck);
            if (verificationFailures.Count > 0)
            {
                runContext.FinalVerificationFailed = true;
                RevisionNoticeBlock.Text = "Verification found unresolved requirements: " + verificationFailures[0];
                RevisionNoticeBlock.Visibility = Visibility.Visible;
                AppendChat("warning", "Final verification found unresolved requirements:\n- " + string.Join("\n- ", verificationFailures.Take(12)));
            }

            if (runContext.IsArtifactCanvasRequest && routedToCanvas)
                ReconcileArtifactValidationState(runContext, ProjectCanvasEditor.Text);

            _sessionMemory = new SessionMemoryState
            {
                ArchitectPlan = string.Empty,
                BuilderOutput = runContext.TaskType == CouncilTaskType.Coding ? string.Empty : output,
                CriticSummary = string.Empty,
                TaskDescription = runContext.UserPrompt.Length > 200 ? runContext.UserPrompt[..200] : runContext.UserPrompt,
                TaskType = runContext.TaskType
            };
            WriteGoalContractSessionMemory(runContext.GoalContract, activeRunIndex);
            SessionMemoryStatusBlock.Text = $"Prior agent run stored ({DateTime.Now:HH:mm})";

            _lastRunContext = runContext;
            _lastFinalOutput = finalOutputForCheck;
            if (refinementPass)
                AppendChat("system", "Refinement diff:\n" + BuildSimpleDiff(previousFinalForDiff, _lastFinalOutput));

            string confidence = runContext.FinalVerificationFailed ? "Flagged for Review" : "Verified";
            ShowConfidenceLabel(confidence);
            AddTaskHistoryEntry(runContext, _lastFinalOutput, verificationFailures.Count, refinementParentId);
            AddPerformanceLogEntry(runContext, verificationFailures.Count);

            _sessionHippocampus.Consolidate();
            _completedCouncilRunCount++;
            _submittedRunPrompt = string.Empty;
            _lastCancelledRunPrompt = string.Empty;
            RelayStatusBlock.Text = runContext.FinalVerificationFailed
                ? "Agent: Completed with unresolved requirements"
                : "Agent: Completed";
            PublishCouncilPetStatus(runContext.FinalVerificationFailed ? "Review" : "Agent", runContext.FinalVerificationFailed ? "Done, but needs review." : "Done.");
            LogActivity(runContext.FinalVerificationFailed
                ? "Single agent run completed with unresolved verification requirements."
                : "Single agent run completed successfully.");
            CouncilRunFinished?.Invoke(runContext.FinalVerificationFailed
                ? $"Agent finished with {verificationFailures.Count} unresolved requirement(s)."
                : "Agent finished successfully.");
            SavePersistedSession();
            ChatScrollViewer.ScrollToEnd();
        }

        private async Task<(string Output, bool Captured)> CaptureSingleModelWorkspacePatchAsync(
            string output,
            CouncilRunContext runContext,
            string systemPrompt,
            CancellationToken token)
        {
            output = await TryCompleteTruncatedWorkspaceHtmlPatchAsync(output, runContext, systemPrompt, token, null);
            if (TryCaptureCodebasePatchProposal(output, runContext))
                return (output, true);

            var retryPayload = new StringBuilder();
            retryPayload.AppendLine("Your previous response was not a valid, applicable connected-codebase patch. Correct it now.");
            retryPayload.AppendLine(BuildLabeledBlock("ORIGINAL REQUEST", runContext.UserPrompt));
            retryPayload.AppendLine(BuildLabeledBlock("CONNECTED CODEBASE CONTEXT", runContext.WorkspaceContext));
            retryPayload.AppendLine(BuildLabeledBlock("REQUIRED OUTPUT FORMAT", BuildCodebasePatchOutputContractForBuilder()));
            retryPayload.AppendLine("Return only one [[AXIOM_CODEBASE_PATCH]] envelope. It must make a real relevant change and use exact current-file SEARCH anchors.");

            ReasoningParser.ParsedResponse retry = await ExecuteCouncilRoleAsync(
                CouncilRole.Builder,
                systemPrompt,
                retryPayload.ToString(),
                token,
                0.15f,
                showLiveCard: true,
                contextSizeOverride: (int)GetRoleContextSize(CouncilRole.Builder));
            string retryOutput = PostProcessBuilderOutput(retry.Answer, runContext);
            retryOutput = await TryCompleteTruncatedWorkspaceHtmlPatchAsync(retryOutput, runContext, systemPrompt, token, null);
            if (TryCaptureCodebasePatchProposal(retryOutput, runContext))
                return (retryOutput, true);

            string? rescued = await TryContentOnlyCodebasePatchRescueAsync(runContext, systemPrompt, output + "\n" + retryOutput, token, null);
            if (!string.IsNullOrWhiteSpace(rescued))
                return (rescued, true);

            AppendChat("warning", "The agent did not return a valid codebase patch. No files were changed.");
            LogActivity("Single agent codebase patch retries failed; no files changed.");
            return ("[[CODEBASE PATCH FORMAT ERROR]]\nThe agent did not return a valid codebase patch proposal. No files were changed.", false);
        }
    }
}
