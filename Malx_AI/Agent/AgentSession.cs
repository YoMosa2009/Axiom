using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>Asks the user to approve one call. Returns the user's decision.</summary>
    public delegate Task<AgentApprovalOutcome> AgentApprovalRequest(AgentToolCall call, string reason, CancellationToken token);

    /// <summary>Reports the running step to the UI. A null label means "nothing is running".</summary>
    public delegate void AgentActivityReporter(string? label);

    public enum AgentApprovalOutcome
    {
        Approve,
        ApproveAlways,
        Deny
    }

    public sealed record AgentStep(AgentToolCall Call, AgentPermission Permission, AgentToolResult? Result, string Note,
        long ElapsedMilliseconds = 0);

    public sealed record AgentRunResult(
        string FinalMessage,
        IReadOnlyList<AgentStep> Steps,
        bool StoppedOnStepLimit,
        bool Cancelled,
        IReadOnlyList<AgentExchange>? Exchanges = null);

    /// <summary>
    /// The agent loop: ask the model, run what it asks for, feed the result back, repeat.
    /// </summary>
    /// <remarks>
    /// The model arrives as an <see cref="IAgentModel"/> and the approval prompt and activity
    /// reporter as delegates, so the loop has no dependency on WPF or on any particular inference
    /// backend. Local, Hybrid Local, and Cloud share this one implementation, and it is testable
    /// without a model.
    /// </remarks>
    public sealed class AgentSession
    {
        private readonly AgentToolExecutor _executor;
        private readonly AgentScope _scope;
        private readonly int _maxSteps;
        private readonly List<string> _sessionAllowList = new();

        /// <summary>How many unusable turns in a row before the run gives up and says so.</summary>
        private const int MaxConsecutiveProtocolErrors = 3;

        /// <summary>How many times a run's claimed result can be sent back for fixing.</summary>
        private const int MaxVerificationPasses = 2;

        /// <summary>How often one exact call may run in a single task before it is blocked.</summary>
        private const int MaxIdenticalCallsPerRun = 2;

        /// <summary>
        /// The live checklist the model keeps with update_plan. Set before a run to carry an
        /// earlier turn's plan forward; read afterwards to keep it.
        /// </summary>
        public AgentPlan Plan { get; set; } = AgentPlan.Empty;

        /// <summary>Called on the run's thread whenever update_plan changes the plan.</summary>
        public Action<AgentPlan>? PlanChanged { get; set; }

        /// <summary>
        /// Called on the run's thread with a sentence and a half saying what the model is doing
        /// now: its own words before a tool call when it wrote any, otherwise a description.
        /// </summary>
        public Action<string>? Narrated { get; set; }

        /// <summary>
        /// Called with the model's words, in full, when they are a message to the user rather
        /// than a one-line "doing now": its first reply after a mid-run message, or anything the
        /// narration would have cut short. The step's narration is then the call's description.
        /// </summary>
        public Action<string>? Said { get; set; }

        /// <summary>
        /// When true (the default), a user message pauses a model turn that has not started a
        /// tool call, and the turn is asked again with the message.
        /// </summary>
        public bool InterruptTurnsForMessages { get; set; } = true;

        /// <summary>Called when the user's mid-run messages are handed to the model (the count).</summary>
        public Action<int>? MessagesDelivered { get; set; }

        /// <summary>Paused turns that do not use up a step; later pauses do.</summary>
        private const int MaxPausesForMessagesPerRun = 6;

        /// <summary>Called after the run's history is compacted, with a one-line description for logs.</summary>
        public Action<string>? Compacted { get; set; }

        /// <summary>update_plan calls in a row before the next one is refused.</summary>
        private const int MaxPlanUpdatesInARow = 2;

        /// <summary>update_plan calls per run that do not use up a step of the budget.</summary>
        private const int FreePlanUpdatesPerRun = 4;

        /// <summary>Steps without a plan update, while steps are still open, before the plan is shown again.</summary>
        internal const int PlanReminderInterval = 8;

        public AgentSession(AgentScope scope, int maxSteps, AgentToolExecutor? executor = null)
        {
            _scope = scope ?? AgentScope.WholeComputer();
            _maxSteps = Math.Max(1, maxSteps);
            _executor = executor ?? new AgentToolExecutor(_scope);
        }

        public async Task<AgentRunResult> RunAsync(
            string goal,
            AgentApprovalMode mode,
            IAgentModel model,
            AgentApprovalRequest requestApproval,
            AgentActivityReporter reportActivity,
            CancellationToken token,
            IReadOnlyList<AgentExchange>? initialHistory = null,
            IEnumerable<string>? initialAllowList = null,
            Action<AgentStep>? onStep = null,
            AgentUserMessageInbox? inbox = null,
            CommandReviewMode? commandReviewMode = null,
            AgentTurnPolicy? policy = null)
        {
            policy ??= AgentTurnPolicy.Default;
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(requestApproval);

            if (initialAllowList != null)
            {
                foreach (string item in initialAllowList)
                {
                    if (!string.IsNullOrWhiteSpace(item) && !_sessionAllowList.Contains(item))
                        _sessionAllowList.Add(item);
                }
            }

            if (!string.IsNullOrWhiteSpace(model.Unavailable))
                return new AgentRunResult(model.Unavailable!, [], false, false, []);

            var steps = new List<AgentStep>();
            // Waits inside tools end early when the user writes (AgentToolExecutor.MessageInbox).
            _executor.MessageInbox = inbox;

            // Each completed step is reported as it happens, so the UI can show the work live
            // (like a terminal agent) instead of one summary at the very end.
            void AddStep(AgentStep step)
            {
                steps.Add(step);
                if (step.Call.Tool != AgentToolNames.RecallContext)
                {
                    ConversationEvidence.Current?.Record("Agent call: " + step.Call.Tool,
                        System.Text.Json.JsonSerializer.Serialize(step.Call.Arguments));
                    ConversationEvidence.Current?.Record("Agent result: " + step.Call.Tool,
                        step.Result?.Succeeded == true ? step.Result.Output : "ERROR: " + (step.Result?.Error ?? step.Note));
                    if (step.Result?.ImageDataUrl is { } image)
                    {
                        int comma = image.IndexOf(',');
                        if (comma >= 0) ConversationEvidence.Current?.SaveImage(Convert.FromBase64String(image[(comma + 1)..]),
                            image.StartsWith("data:image/jpeg", StringComparison.OrdinalIgnoreCase) ? ".jpg" : ".png");
                    }
                }
                try { onStep?.Invoke(step); }
                catch { /* a UI callback failure must never stop the run */ }
            }
            var history = initialHistory != null && initialHistory.Count > 0
                ? new List<AgentExchange>(AgentContextManager.CompactExchanges(initialHistory))
                : new List<AgentExchange>();
            int consecutiveProtocolErrors = 0;
            AgentToolCall? lastToolCall = null;
            int consecutiveIdenticalCalls = 0;
            int verificationPasses = 0;
            int screenshotsSinceLastMutation = 0;
            int totalScreenshots = 0;
            var callCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var fileReadCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            bool changedSomething = false;
            int stepsSinceLastChange = 0;
            int blockedInARow = 0;
            int recallsInARow = 0;
            int worldChanges = 0;
            int pausesForMessages = 0;
            bool answeringUser = false;
            string? wrapUpReason = null;
            bool checkedSinceLastChange = true;
            bool checkGateUsed = false;
            bool explorationNudged = false;
            int reviewPasses = 0;
            var changedFiles = new List<string>();
            int currentStep = 0;
            int planUpdatesInARow = 0;
            int freePlanUpdatesUsed = 0;
            int stepsSincePlanUpdate = 0;
            bool planUpdatedThisRun = false;
            bool planGateUsed = false;
            var compactor = policy.Compaction == null ? null : new AgentRunCompactor(policy.Compaction);

            void SendBack(string activity, string message)
            {
                reportActivity?.Invoke(activity);
                history.Add(new AgentExchange(
                    new AgentToolCall("(verification)", new Dictionary<string, string>()),
                    message));
            }

            // Decides whether a claimed finish is accepted. In order: the claim against the disk
            // (always), then at Extra High and Ultra a check of the changed files, then at Ultra
            // a fresh review. Each sends the run back with concrete work instead of commenting.
            async Task<bool> SendBackIfUnfinishedAsync(string answer)
            {
                if (verificationPasses < MaxVerificationPasses)
                {
                    IReadOnlyList<string> problems = AgentWorkVerifier.FindProblems(answer, steps, _scope.WorkingDirectory);
                    if (problems.Count > 0)
                    {
                        verificationPasses++;
                        SendBack("Checking the work",
                            "Not finished yet - checking the files on disk found:\n- "
                            + string.Join("\n- ", problems)
                            + "\nFix these with the tools (create the missing files or correct the references), then give your final summary.");
                        return true;
                    }
                }

                // A plan this run wrote that still has open steps: sent back once, like the
                // check gate below. Soft on purpose (the research found no harness that hard-
                // blocks on it): the model may close steps it dropped and say why.
                if (planUpdatedThisRun && !planGateUsed && !policy.AnswerOnly && Plan.HasOpenItems
                    && currentStep < _maxSteps - 2)
                {
                    planGateUsed = true;
                    SendBack("Checking the plan", Plan.OpenItemsAtFinishMessage());
                    return true;
                }

                if (policy.RequireCheckedFinish && changedSomething && !checkedSinceLastChange && !checkGateUsed
                    && currentStep < _maxSteps - 2)
                {
                    checkGateUsed = true;
                    SendBack("Checking the work", AgentQualityGates.CheckBeforeFinishMessage(changedFiles));
                    return true;
                }

                if (policy.Reviewer != null && changedSomething && reviewPasses == 0 && currentStep < _maxSteps - 4)
                {
                    reviewPasses++;
                    reportActivity?.Invoke("Reviewing the work with fresh eyes");
                    IReadOnlyList<string> issues;
                    try
                    {
                        issues = await policy.Reviewer(new AgentReviewRequest(goal, answer, changedFiles.ToList()), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        issues = [];
                    }
                    if (issues.Count > 0)
                    {
                        SendBack("Fixing what the review found", AgentQualityGates.ReviewFindingsMessage(issues));
                        return true;
                    }
                }

                return false;
            }

            // A step Axiom refused to run. Three in a row means the model is not reading the
            // refusals, so the run stops asking it for tool calls and asks for the summary.
            void NoteBlocked(AgentToolCall blockedCall, AgentPermission permission, string message, string note)
            {
                AddStep(new AgentStep(blockedCall, permission, AgentToolResult.Fail(message), note));
                history.Add(new AgentExchange(blockedCall, message));
                if (++blockedInARow >= MaxBlockedCallsInARow)
                    wrapUpReason = "several tool calls in a row were blocked";
            }

            // What the user typed while the run was working joins the conversation between steps,
            // the way a terminal agent takes a message mid-task, instead of requiring a Stop.
            bool DeliverUserMessages(string? draftAnswer = null)
            {
                if (inbox == null || !inbox.HasPending)
                    return false;

                IReadOnlyList<string> messages = inbox.TakeAll();
                if (messages.Count == 0)
                    return false;

                string text = AgentUserMessageInbox.FormatForModel(messages, goal);
                if (!string.IsNullOrWhiteSpace(draftAnswer))
                    text = $"(You were about to finish with: \"{Clip(draftAnswer.Trim(), 400)}\")\n" + text;
                history.Add(new AgentExchange(new AgentToolCall(UserMessageTool, new Dictionary<string, string>()), text));
                answeringUser = true;
                try { MessagesDelivered?.Invoke(messages.Count); }
                catch { /* a UI callback failure must never stop the run */ }
                return true;
            }

            bool DeliverBackgroundWakeups()
            {
                IReadOnlyList<string> wakeups = _executor.BackgroundTasks.TakeDueWakeNotifications();
                if (wakeups.Count == 0)
                    return false;

                foreach (string report in wakeups)
                {
                    history.Add(new AgentExchange(
                        new AgentToolCall(BackgroundWakeTool, new Dictionary<string, string>()),
                        report));
                }
                return true;
            }

            try
            {
                for (int step = 0; step < _maxSteps; step++, currentStep = step)
                {
                    token.ThrowIfCancellationRequested();
                    if (wrapUpReason != null)
                        break;
                    DeliverUserMessages();
                    DeliverBackgroundWakeups();

                    if (compactor != null)
                    {
                        string? compacted = await compactor.CompactIfNeededAsync(
                            goal, history, model, changedFiles, Plan, reportActivity, token).ConfigureAwait(false);
                        if (compacted != null)
                        {
                            try { Compacted?.Invoke(compacted); }
                            catch { /* logging only */ }
                        }
                    }

                    reportActivity?.Invoke("Thinking");
                    AgentModelReply reply;
                    // A message sent while the model is still thinking pauses that turn: it is
                    // asked again straight away with the message in front of it, instead of the
                    // user waiting minutes for a step that may no longer be wanted. A turn that is
                    // already writing a tool call finishes first, so no work is thrown away.
                    using var turn = CancellationTokenSource.CreateLinkedTokenSource(token);
                    bool pausedForMessage = false;
                    void PauseForMessage()
                    {
                        if (!InterruptTurnsForMessages || model.IsComposingToolCall)
                            return;
                        pausedForMessage = true;
                        try { turn.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    if (inbox != null)
                    {
                        inbox.MessagePosted += PauseForMessage;
                        // One that arrived between the delivery above and subscribing.
                        DeliverUserMessages();
                    }
                    try
                    {
                        reply = await model.NextAsync(goal, history, turn.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (pausedForMessage && !token.IsCancellationRequested)
                    {
                        // The abandoned turn ran no tool. The next pass delivers the message.
                        reportActivity?.Invoke("Reading your message");
                        if (pausesForMessages++ < MaxPausesForMessagesPerRun)
                            step--;
                        continue;
                    }
                    catch (AgentFatalException fatal)
                    {
                        return new AgentRunResult(fatal.Message, steps, false, false, history);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A dropped connection mid-run is not a reason to abandon work already
                        // done. Free-tier providers drop bodies and time out often enough that a
                        // single hiccup must not leave the machine half-changed with no summary.
                        if (++consecutiveProtocolErrors >= MaxConsecutiveProtocolErrors)
                        {
                            return new AgentRunResult(
                                $"The connection to the model kept failing ({ex.Message}). "
                                + "Anything already done is listed above; try again when the provider settles.",
                                steps,
                                false,
                                false,
                                history);
                        }

                        history.Add(new AgentExchange(
                            new AgentToolCall("(transport)", new Dictionary<string, string>()),
                            "The previous request failed to reach the model. Continue from what is already done."));
                        continue;
                    }
                    finally
                    {
                        if (inbox != null)
                            inbox.MessagePosted -= PauseForMessage;
                        reportActivity?.Invoke(null);
                    }

                    if (reply.ProtocolError != null)
                    {
                        // Recoverable: tell the model what was wrong and let it try again. Bounded,
                        // so a model that keeps returning nothing usable reports that honestly
                        // instead of burning every step and claiming success.
                        if (++consecutiveProtocolErrors >= MaxConsecutiveProtocolErrors)
                        {
                            return new AgentRunResult(
                                "The model did not return anything usable after several attempts. "
                                + "That is usually a transient problem with the provider - try again, "
                                + "or switch model in Settings if it keeps happening.",
                                steps,
                                false,
                                false,
                                history);
                        }

                        history.Add(new AgentExchange(
                            new AgentToolCall("(invalid)", new Dictionary<string, string>()),
                            "Tool error: " + reply.ProtocolError));
                        continue;
                    }

                    consecutiveProtocolErrors = 0;

                    if (reply.Call == null)
                    {
                        string answer = reply.FinalText ?? string.Empty;

                        // A message that arrived while the model was writing this answer has not
                        // been seen yet; finishing now would silently drop the user's correction.
                        if (DeliverUserMessages(answer))
                            continue;

                        if (await SendBackIfUnfinishedAsync(answer).ConfigureAwait(false))
                            continue;

                        return new AgentRunResult(
                            string.IsNullOrWhiteSpace(answer) ? "Done." : answer.Trim(),
                            steps,
                            false,
                            false,
                            history);
                    }

                    AgentToolCall call = reply.Call;
                    bool notFinish = !string.Equals(call.Tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase);
                    bool saidInFull = false;
                    if (Said != null && notFinish && AgentNarration.ShouldShowInFull(reply.Said, answeringUser))
                    {
                        try { Said(reply.Said!); saidInFull = true; }
                        catch { /* a UI callback failure must never stop the run */ }
                    }
                    answeringUser = false;
                    if (Narrated != null && notFinish)
                    {
                        try { Narrated(saidInFull ? AgentNarration.FromCall(call) : reply.Narration ?? AgentNarration.FromCall(call)); }
                        catch { /* a UI callback failure must never stop the run */ }
                    }
                    if (string.Equals(call.Tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase))
                    {
                        string summary = call.Arg("summary");
                        if (DeliverUserMessages(summary))
                            continue;

                        // The finish tool used to skip every check a plain-text answer gets.
                        if (await SendBackIfUnfinishedAsync(summary).ConfigureAwait(false))
                            continue;

                        AddStep(new AgentStep(call, AgentPermission.Allow, AgentToolResult.Ok("done"), "finished"));
                        return new AgentRunResult(
                            string.IsNullOrWhiteSpace(summary) ? "Done." : summary,
                            steps,
                            false,
                            false,
                            history);
                    }

                    // update_plan is bookkeeping, handled here: it changes nothing on the machine,
                    // needs no approval and shows the user the plan. A few per run are free so a
                    // small step budget is not spent on it.
                    if (string.Equals(call.Tool, AgentToolNames.UpdatePlan, StringComparison.OrdinalIgnoreCase))
                    {
                        if (++planUpdatesInARow > MaxPlanUpdatesInARow)
                        {
                            NoteBlocked(call, AgentPermission.Allow,
                                "Blocked: the plan was just updated. Work on the current step now; update the plan again only after a step is done.",
                                "plan updated too often");
                            continue;
                        }

                        string planText = call.Arg("plan");
                        AgentToolResult planResult;
                        if (string.IsNullOrWhiteSpace(planText))
                        {
                            planResult = AgentToolResult.Ok("Current plan:\n" + Plan.Render());
                        }
                        else
                        {
                            AgentPlan? parsed = AgentPlan.TryParse(planText, call.Arg("explanation"), policy.MaxPlanItems, out string? planError);
                            if (parsed == null)
                            {
                                planResult = AgentToolResult.Fail(planError ?? "The plan could not be read.");
                            }
                            else
                            {
                                Plan = parsed;
                                planUpdatedThisRun = true;
                                stepsSincePlanUpdate = 0;
                                try { PlanChanged?.Invoke(parsed); }
                                catch { /* a UI callback failure must never stop the run */ }
                                planResult = AgentToolResult.Ok(parsed.DescribeUpdate());
                            }
                        }

                        AddStep(new AgentStep(call, AgentPermission.Allow, planResult, "plan"));
                        history.Add(new AgentExchange(call, planResult.ToObservation()));
                        if (freePlanUpdatesUsed < FreePlanUpdatesPerRun)
                        {
                            freePlanUpdatesUsed++;
                            step--;
                        }
                        continue;
                    }

                    planUpdatesInARow = 0;

                    if (AreCallsIdentical(call, lastToolCall))
                    {
                        consecutiveIdenticalCalls++;
                    }
                    else
                    {
                        consecutiveIdenticalCalls = 0;
                        lastToolCall = call;
                    }

                    if (consecutiveIdenticalCalls >= 3)
                    {
                        return new AgentRunResult(
                            $"The agent was stopped because it repeatedly attempted the identical action ({call.DescribeShort()}) without making progress. If the desired files or folders already exist, review them or provide a refined instruction.",
                            steps,
                            false,
                            false,
                            history);
                    }

                    AgentPermissionDecision decision = commandReviewMode.HasValue
                        ? CommandSafetyReviewer.Evaluate(call, _scope, commandReviewMode.Value, _sessionAllowList)
                        : AgentPermissionPolicy.Evaluate(call, mode, _sessionAllowList);

                    if (decision.Permission == AgentPermission.Deny)
                    {
                        AddStep(new AgentStep(call, AgentPermission.Deny, null, decision.Reason));
                        history.Add(new AgentExchange(call, "Refused: " + decision.Reason));
                        continue;
                    }

                    // A question turn looks; it does not act. Without this a model asked "Is it
                    // done?" started editing and re-verifying the project instead of answering.
                    if (policy.AnswerOnly && !IsAllowedInAnswerOnlyTurn(call.Tool))
                    {
                        NoteBlocked(call, decision.Permission,
                            $"Blocked: the user asked a question, so this turn only answers it. {call.Tool} is not available here. "
                            + "Answer the question now in words, from what you already know. If one fact is truly missing, a single read_file, list_directory, search_text or check_background_task is allowed.",
                            "question turn: answer only");
                        continue;
                    }

                    if (policy.LimitFileInspection && call.Tool.Equals(AgentToolNames.ReadFile, StringComparison.OrdinalIgnoreCase))
                    {
                        string path = InspectionPath(call.Arg("path"));
                        // Different line ranges are still reads of the same unchanged file.
                        // Exact-call repetition guards alone let the logged run spend 18 turns
                        // paging through one HTML file without attempting a fix.
                        if (fileReadCounts.TryGetValue(path, out int reads) && reads >= 6)
                        {
                            NoteBlocked(call, decision.Permission,
                                "Blocked: this file has already been read six times without a change. Use search_text with a specific literal on the file path to find the relevant code, then make a targeted fix. Do not reread the file through run_command or invent a diagnosis from an inconclusive check.",
                                "repeated file inspection");
                            continue;
                        }
                    }

                    if (string.Equals(call.Tool, AgentToolNames.TakeScreenshot, StringComparison.OrdinalIgnoreCase)
                        && (screenshotsSinceLastMutation >= 2 || totalScreenshots >= 4))
                    {
                        NoteBlocked(call, decision.Permission,
                            $"Blocked: Screenshot limit reached ({totalScreenshots} taken in this run, {screenshotsSinceLastMutation} since the last file change). take_screenshot already captures the app you name in its window argument without needing focus. Do NOT take more screenshots or run WScript.Shell/AppActivate — if a code fix is needed, use edit_file/write_file now; if the app works, stop calling tools and write your final Markdown summary immediately.",
                            "screenshot limit reached");
                        continue;
                    }

                    if ((string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(call.Tool, AgentToolNames.StartBackgroundTask, StringComparison.OrdinalIgnoreCase))
                        && IsUiFocusAutomationCommand(call.Arg("command"))
                        )
                    {
                        NoteBlocked(call, decision.Permission,
                            DesktopIsolationMessage,
                            "desktop window control blocked");
                        continue;
                    }

                    if (consecutiveIdenticalCalls == 2)
                    {
                        NoteBlocked(call, decision.Permission,
                            $"Repetitive action blocked: '{call.Tool}' with identical arguments was attempted 3 times in a row without making progress. Proceed immediately to creating or modifying files using write_file or finish.",
                            "repetition blocked");
                        continue;
                    }

                    // Once a run has built something, checking it is meant to be short. A logged
                    // run built its project in 18 steps and then spent 42 more refocusing a window
                    // and screenshotting it. After this many steps in a row without changing a
                    // file, the run is asked for its summary instead of another look.
                    if (policy.VerificationBudget > 0 && changedSomething && !IsProgressTool(call.Tool)
                        && stepsSinceLastChange >= policy.VerificationBudget)
                    {
                        // Ultra: a run that ends by running out of checking steps still gets its
                        // fresh review (a live run on 2026-11-24 ended this way and skipped it).
                        // Real findings reopen the run for fixing instead of closing it.
                        if (policy.Reviewer != null && reviewPasses == 0 && currentStep < _maxSteps - 6)
                        {
                            reviewPasses++;
                            reportActivity?.Invoke("Reviewing the work with fresh eyes");
                            IReadOnlyList<string> issues;
                            try
                            {
                                issues = await policy.Reviewer(new AgentReviewRequest(goal, "(the agent was still checking its work)", changedFiles.ToList()), token).ConfigureAwait(false);
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch
                            {
                                issues = [];
                            }
                            reportActivity?.Invoke(null);
                            if (issues.Count > 0)
                            {
                                stepsSinceLastChange = 0;
                                AddStep(new AgentStep(call, decision.Permission, null, "review found problems"));
                                history.Add(new AgentExchange(call, "Blocked: that call did not run. " + AgentQualityGates.ReviewFindingsMessage(issues)));
                                continue;
                            }
                        }

                        wrapUpReason = $"{stepsSinceLastChange} steps in a row changed nothing";
                        NoteBlocked(call, decision.Permission,
                            $"Blocked: {stepsSinceLastChange} steps in a row have changed nothing, so the checking phase is over. Do not call another tool.",
                            "verification budget used up");
                        continue;
                    }

                    // Cycles that alternate (rewrite a file, rerun the same failing command,
                    // rewrite, rerun...) slip past the consecutive check above; a live run spent
                    // its whole 30-step budget that way. Count exact repeats across the run.
                    // A follow-up turn on 2026-11-24 spent its first five steps (about 90 s) on
                    // recall_context with different queries before touching the project. The
                    // archive does not change between those calls, so after a few in a row the
                    // model is told to act on what it already has.
                    if (call.Tool == AgentToolNames.RecallContext)
                    {
                        if (++recallsInARow > MaxRecallsInARow)
                        {
                            NoteBlocked(call, decision.Permission,
                                $"Blocked: recall_context has already been used {recallsInARow - 1} times in a row. The earlier context will not change. "
                                + "Act on what you have now: inspect the project files or continue the work.",
                                "recall limit");
                            continue;
                        }
                    }
                    else
                    {
                        recallsInARow = 0;
                    }

                    // A look at something that changes (the open browser page, a folder) is a new
                    // look once anything has run since. A live run (2026-11-26) inspected a page,
                    // clicked, and inspected again, and was stopped as "repeating" while working.
                    string signature = CallSignature(call);
                    if (IsStateObservation(call))
                        signature += "|after:" + worldChanges;
                    int seen = callCounts.TryGetValue(signature, out int previous) ? previous + 1 : 1;
                    callCounts[signature] = seen;
                    // Looking again (listing a folder, re-reading a file) is legitimate after
                    // changes, so read-only calls get more room than commands and writes.
                    int allowed = AgentToolNames.IsReadOnly(call.Tool) || IsStateObservation(call) ? MaxIdenticalCallsPerRun + 2 : MaxIdenticalCallsPerRun;
                    if (seen >= allowed + 2)
                    {
                        return new AgentRunResult(
                            $"The agent was stopped because it kept repeating the same action ({call.DescribeShort()}) without making progress. "
                            + "Anything already done is listed above; a more specific instruction usually gets it unstuck.",
                            steps,
                            false,
                            false,
                            history);
                    }

                    if (seen > allowed)
                    {
                        NoteBlocked(call, decision.Permission,
                            $"Blocked: this exact {call.Tool} call has already been made {seen - 1} times in this task, and repeating it will not change the result. "
                            + "Use the earlier results: if a command keeps failing, try a different command or approach; if the work is done, give your final summary.",
                            "repetition blocked");
                        continue;
                    }

                    blockedInARow = 0;

                    if (decision.Permission == AgentPermission.Ask)
                    {
                        reportActivity?.Invoke("Waiting for approval");
                        AgentApprovalOutcome outcome;
                        try
                        {
                            outcome = await requestApproval(call, decision.Reason, token).ConfigureAwait(false);
                        }
                        finally
                        {
                            reportActivity?.Invoke(null);
                        }

                        if (outcome == AgentApprovalOutcome.Deny)
                        {
                            AddStep(new AgentStep(call, AgentPermission.Ask, null, "denied by the user"));
                            history.Add(new AgentExchange(
                                call,
                                "The user declined this step. Do not retry it; work around it or explain what you need."));
                            continue;
                        }

                        if (outcome == AgentApprovalOutcome.ApproveAlways
                            && (string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(call.Tool, AgentToolNames.StartBackgroundTask, StringComparison.OrdinalIgnoreCase)))
                        {
                            string key = AgentPermissionPolicy.AllowListKeyFor(call.Arg("command"));
                            if (key.Length > 0 && !_sessionAllowList.Contains(key))
                                _sessionAllowList.Add(key);
                        }
                    }

                    reportActivity?.Invoke(call.DescribeShort());
                    var toolTimer = System.Diagnostics.Stopwatch.StartNew();
                    AgentToolResult result;
                    try
                    {
                        result = await _executor.ExecuteAsync(call, token).ConfigureAwait(false);
                    }
                    finally
                    {
                        reportActivity?.Invoke(null);
                    }

                    AddStep(new AgentStep(call, decision.Permission, result, decision.Reason, toolTimer.ElapsedMilliseconds));
                    if (!AgentToolNames.IsReadOnly(call.Tool) && !IsStateObservation(call))
                        worldChanges++;
                    if (result.Succeeded && call.Tool.Equals(AgentToolNames.ReadFile, StringComparison.OrdinalIgnoreCase))
                    {
                        string path = InspectionPath(call.Arg("path"));
                        fileReadCounts[path] = fileReadCounts.GetValueOrDefault(path) + 1;
                    }
                    if (result.Succeeded && IsProgressTool(call.Tool))
                    {
                        changedSomething = true;
                        stepsSinceLastChange = 0;
                        if (!call.Tool.Equals(AgentToolNames.StartBackgroundTask, StringComparison.OrdinalIgnoreCase))
                        {
                            fileReadCounts.Remove(InspectionPath(call.Arg("path")));
                            string changedPath = call.Arg("path");
                            if (changedPath.Length > 0 && !changedFiles.Contains(changedPath, StringComparer.OrdinalIgnoreCase))
                                changedFiles.Add(changedPath);
                            checkedSinceLastChange = false;
                        }
                    }
                    else
                    {
                        stepsSinceLastChange++;
                        if (result.Succeeded && AgentQualityGates.IsCheckTool(call.Tool))
                            checkedSinceLastChange = true;
                    }

                    if (result.Succeeded)
                    {
                        if (string.Equals(call.Tool, AgentToolNames.TakeScreenshot, StringComparison.OrdinalIgnoreCase))
                        {
                            screenshotsSinceLastMutation++;
                            totalScreenshots++;
                        }
                        else if (string.Equals(call.Tool, AgentToolNames.WriteFile, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(call.Tool, AgentToolNames.AppendFile, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(call.Tool, AgentToolNames.EditFile, StringComparison.OrdinalIgnoreCase))
                        {
                            screenshotsSinceLastMutation = 0;
                        }
                    }

                    string observation = result.ToObservation();
                    if (consecutiveIdenticalCalls == 1)
                    {
                        observation += "\n[WARNING: You executed the identical tool call with the identical arguments twice in a row. Do not repeat this action again. If the desired folder or state already exists, proceed immediately to writing files using write_file.]";
                    }
                    if (_maxSteps >= 3 && step == _maxSteps - 2)
                    {
                        observation += policy.AnswerOnly
                            ? "\n[LAST CHECK USED: Do NOT call any more tools. Answer the user's question now, in words.]"
                            : "\n[FINAL STEP WARNING: You have only 1 step left in your step budget. Do NOT call any more tools — reply on your next turn with your complete, structured Markdown summary of everything accomplished.]";
                    }
                    else if (policy.VerificationBudget > 0 && changedSomething && !IsProgressTool(call.Tool)
                        && stepsSinceLastChange == Math.Max(2, policy.VerificationBudget - 3))
                    {
                        observation += $"\n[WRAP-UP WARNING: {stepsSinceLastChange} steps in a row have changed nothing. If the work is verified, stop calling tools and write your final Markdown summary now. If something is actually broken, fix it with edit_file. Only {policy.VerificationBudget - stepsSinceLastChange} more checking steps are allowed.]";
                    }
                    else if (policy.ExplorationBudget > 0 && !changedSomething && !explorationNudged
                        && steps.Count >= policy.ExplorationBudget)
                    {
                        explorationNudged = true;
                        observation += AgentQualityGates.ExplorationNudge(steps.Count);
                    }

                    // Claude Code re-shows a stale todo list; models forget the plan they wrote.
                    if (Plan.HasOpenItems && ++stepsSincePlanUpdate % PlanReminderInterval == 0)
                        observation += Plan.StaleReminder(stepsSincePlanUpdate);
                    history.Add(new AgentExchange(call, observation, result.ImageDataUrl));
                }
            }
            catch (OperationCanceledException)
            {
                reportActivity?.Invoke(null);
                return new AgentRunResult("Stopped.", steps, false, true, history);
            }

            // The run is out of room (or was told to stop checking). It still owes the user an
            // account of what it did: one last turn is spent asking for the summary, and if the
            // model answers with yet another tool call Axiom writes the summary from the steps.
            bool hitStepLimit = wrapUpReason == null;
            string? closing = await RequestClosingSummaryAsync(
                goal, model, history, reportActivity, token, policy,
                hitStepLimit ? $"You have used all {_maxSteps} steps available for this turn" : $"This turn is being closed because {wrapUpReason}")
                .ConfigureAwait(false);
            if (closing == CancelledMarker)
                return new AgentRunResult("Stopped.", steps, false, true, history);

            reportActivity?.Invoke(null);
            string fallback = AgentFollowUp.BuildStepLimitSummary(steps, _maxSteps);
            if (!hitStepLimit)
            {
                // Not a ceiling: the work is most likely done and the model was only circling.
                return new AgentRunResult(
                    string.IsNullOrWhiteSpace(closing)
                        ? fallback.Replace($"I reached the {_maxSteps}-step limit for this model before finishing. Ask me to continue if the work so far looks right.",
                            $"I stopped here because {wrapUpReason}. This is what the run did:", StringComparison.Ordinal)
                        : closing!,
                    steps,
                    false,
                    false,
                    history);
            }

            return new AgentRunResult(
                string.IsNullOrWhiteSpace(closing)
                    ? fallback
                    : $"I reached the {_maxSteps}-step limit for this model before finishing. Ask me to continue if the work so far looks right.\n\n{closing}",
                steps,
                true,
                false,
                history);
        }

        private const string CancelledMarker = "\u0001cancelled";

        private string InspectionPath(string requested)
        {
            try
            {
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(_scope.WorkingDirectory,
                    Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'))));
            }
            catch { return requested; }
        }

        /// <summary>How many refused tool calls in a row end the tool-calling part of a run.</summary>
        private const int MaxBlockedCallsInARow = 3;
        internal const int MaxRecallsInARow = 3;

        /// <summary>The pseudo-tool name the closing-summary request is recorded under.</summary>
        internal const string WrapUpTool = "(wrap_up)";

        /// <summary>
        /// Asks the model, once, for its final summary with no further tool calls.
        /// Returns null when it did not produce one.
        /// </summary>
        private static async Task<string?> RequestClosingSummaryAsync(
            string goal,
            IAgentModel model,
            List<AgentExchange> history,
            AgentActivityReporter? reportActivity,
            CancellationToken token,
            AgentTurnPolicy policy,
            string reason)
        {
            history.Add(new AgentExchange(
                new AgentToolCall(WrapUpTool, new Dictionary<string, string>()),
                policy.AnswerOnly
                    ? $"{reason}. Do NOT call a tool. Answer the user's question now, in words, from what you know."
                    : $"{reason}. Do NOT call a tool — no tool call will be run. Reply now with your final Markdown summary of THIS turn only: "
                      + "(1) what you built or changed, (2) the exact files created or edited, (3) what you checked and what it showed, "
                      + "(4) what is still unfinished or unverified, and (5) how to run it."));

            reportActivity?.Invoke("Writing the summary");
            try
            {
                AgentModelReply reply = await model.NextAsync(goal, history, token).ConfigureAwait(false);
                if (reply.ProtocolError != null)
                    return null;
                if (reply.Call == null)
                    return string.IsNullOrWhiteSpace(reply.FinalText) ? null : reply.FinalText.Trim();
                if (string.Equals(reply.Call.Tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase))
                {
                    string summary = reply.Call.Arg("summary");
                    return string.IsNullOrWhiteSpace(summary) ? null : summary.Trim();
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return CancelledMarker;
            }
            catch
            {
                // A summary is a courtesy; a failed request for one must not lose the run.
                return null;
            }
            finally
            {
                reportActivity?.Invoke(null);
            }
        }

        /// <summary>Tools that change the project, as opposed to looking at it.</summary>
        internal static bool IsProgressTool(string? tool) =>
            string.Equals(tool, AgentToolNames.WriteFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.AppendFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.EditFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.StartBackgroundTask, StringComparison.OrdinalIgnoreCase);

        /// <summary>What a turn that only answers a question may still call.</summary>
        internal static bool IsAllowedInAnswerOnlyTurn(string? tool) =>
            string.Equals(tool, AgentToolNames.ReadFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.ListDirectory, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.FindFiles, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.SearchText, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.CheckBackgroundTask, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.UseSkill, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.Finish, StringComparison.OrdinalIgnoreCase);

        private static readonly System.Text.RegularExpressions.Regex SleepOnlyCommandRegex = new(
            @"^\s*(?:Start-Sleep|sleep|timeout)\b[^;|&]*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>A command that does nothing but wait. All of them count as one repeated call.</summary>
        internal static bool IsSleepOnlyCommand(string? command) =>
            !string.IsNullOrWhiteSpace(command) && SleepOnlyCommandRegex.IsMatch(command);

        /// <summary>The pseudo-tool name a user's mid-run message is recorded under in the history.</summary>
        internal const string UserMessageTool = "(user)";

        /// <summary>The pseudo-tool name a background wake/completion notification is recorded under.</summary>
        internal const string BackgroundWakeTool = "(background_wake)";

        private static string Clip(string text, int max) =>
            text.Length <= max ? text : text[..max] + "…";

        /// <summary>Commands the user chose to stop being asked about during this run.</summary>
        public IReadOnlyList<string> SessionAllowList => _sessionAllowList;

        internal const string DesktopIsolationMessage =
            "Blocked: Agent Access commands must not hide, minimize, close, reposition, or focus desktop windows (including Axiom). "
            + "Do not use WScript.Shell, AppActivate, SendKeys or native window-control APIs. "
            + "For a generated page, use take_screenshot with url set to its file:/// or HTTP URL; it renders in a background browser and appears inside Axiom. "
            + "For an existing app, use take_screenshot with window set to its title. Neither requires changing focus. Desktop input belongs to explicit @ComputerUse sessions.";

        internal static bool IsUiFocusAutomationCommand(string? command) =>
            !string.IsNullOrWhiteSpace(command)
            && System.Text.RegularExpressions.Regex.IsMatch(command,
                @"\b(?:WScript\.Shell|AppActivate|SendKeys|ShowWindow(?:Async)?|SetForegroundWindow|SetWindowPlacement|SetWindowPos|BringWindowToTop|SwitchToThisWindow|ShowDesktop|ToggleDesktop|MinimizeAll|CloseMainWindow|SendInput|mouse_event|keybd_event)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        internal static string CallSignature(AgentToolCall call)
        {
            if (string.Equals(call.Tool, AgentToolNames.TakeScreenshot, StringComparison.OrdinalIgnoreCase))
                return AgentToolNames.TakeScreenshot;
            if (string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                && IsUiFocusAutomationCommand(call.Arg("command")))
                return "run_command|ui_focus_automation";
            // Waiting 2 seconds and waiting 3 seconds are the same non-action.
            if (string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                && IsSleepOnlyCommand(call.Arg("command")))
                return "run_command|sleep";

            return call.Tool.ToLowerInvariant() + "|" + string.Join("|", call.Arguments
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key.ToLowerInvariant() + "=" + pair.Value));
        }

        /// <summary>
        /// Calls that only look at something that can change between steps: the open browser
        /// page, a folder listing, background task status.
        /// </summary>
        internal static bool IsStateObservation(AgentToolCall call)
        {
            if (string.Equals(call.Tool, AgentToolNames.ListDirectory, StringComparison.OrdinalIgnoreCase)
                || string.Equals(call.Tool, AgentToolNames.CheckBackgroundTask, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.Equals(call.Tool, AgentToolNames.BrowserControl, StringComparison.OrdinalIgnoreCase))
                return false;
            string action = call.Arg("action").Trim().ToLowerInvariant();
            return action is "inspect" or "status" or "detect"
                || (action is "read" or "read_page" or "fetch" && string.IsNullOrWhiteSpace(call.Arg("url")));
        }

        internal static bool AreCallsIdentical(AgentToolCall? a, AgentToolCall? b)
        {
            if (a == null || b == null)
                return false;
            if (!string.Equals(a.Tool, b.Tool, StringComparison.OrdinalIgnoreCase))
                return false;
            if (a.Arguments.Count != b.Arguments.Count)
                return false;
            foreach (var kvp in a.Arguments)
            {
                if (!b.Arguments.TryGetValue(kvp.Key, out string? val) || !string.Equals(kvp.Value, val, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
