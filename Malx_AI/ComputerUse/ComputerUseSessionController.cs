using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Malx_AI.ComputerUse
{
    internal sealed class ComputerUseSessionRequest
    {
        public required string Goal { get; init; }
        public required Window HostWindow { get; init; }
        public required ComputerUseMode InitialMode { get; init; }
        public required string ActingModelLabel { get; init; }
        public required string ExecutionSurface { get; init; }
        public required Func<string, string, ComputerUseCapture, CancellationToken, Task<string>> InferAsync { get; init; }
        public required Func<string, string, ComputerUseCapture, CancellationToken, Task<string>> VerifyAsync { get; init; }
        public required Func<string, string, ComputerUseCapture, CancellationToken, Task<string>> PlanAsync { get; init; }
        public string NavigationContext { get; init; } = "";
        public Action<string>? OnChat { get; init; }
        public Action? OnStop { get; init; }
    }

    internal sealed class ComputerUseSessionResult
    {
        public bool Completed { get; init; }
        public string Summary { get; init; } = "";
        public int Steps { get; init; }
    }

    internal static partial class ComputerUseSessionController
    {
        public const int MaxSteps = 64;
        private const int RepeatAbortCount = 3;
        // Outer watchdog only. The chat transport already enforces its own first-byte, idle and
        // no-content limits (and a local runner reports its own failures), so this must sit ABOVE
        // those to let the specific, actionable error surface first. A single turn may also spend
        // one format-recovery retry inside this budget. Too small a value here turns a healthy but
        // slow model into a permanent "interrupted" loop on modest hardware.
        private static readonly TimeSpan ModelTurnTimeout = TimeSpan.FromSeconds(240);

        // Planning sends no screenshot and caps its output at a couple of thousand tokens, so the
        // vision-sized budget above does not apply to it: a healthy plan comes back in seconds.
        // It is still above the transport's own 90s stall detection, so a stalled stream reports
        // its specific error first — this only bounds the case the transport cannot see, where the
        // server accepts the connection and never sends response headers at all. Nothing is
        // visible on screen during planning, so a long silent wait here reads as a dead app.
        private static readonly TimeSpan PlanningTurnTimeout = TimeSpan.FromSeconds(120);

        public static string BuildSystemPrompt(ComputerUseCapture capture, ComputerUseMode mode)
        {
            return
                "You are Axiom Computer Use. You operate a real Windows desktop with screenshots and GUI actions — not a terminal, not MCP, not shell commands.\n" +
                "You receive one JPEG of the active app's monitor. Use image pixel coordinates; the controller maps them to native screen pixels. Origin is top-left of THIS image.\n" +
                $"Screenshot size: {capture.ImageWidth}x{capture.ImageHeight}. Native monitor pixels: {capture.ScreenWidth}x{capture.ScreenHeight} at origin ({capture.ScreenX},{capture.ScreenY}).\n" +
                "x and y MUST be screenshot pixels from this JPEG (0..width-1, 0..height-1). Read the yellow grid labels. Do not invent coordinates.\n" +
                "Always look at the screenshot before acting.\n" +
                "Return EXACTLY one JSON object and nothing else. Schema:\n" +
                "{\n" +
                "  \"thinking\": \"short description of what you see and will do\",\n" +
                "  \"task_progress\": {\n" +
                "    \"completed\": [\"controller-verified completions echoed from the previous turn only\"],\n" +
                "    \"current\": \"one next unfinished sub-goal\",\n" +
                "    \"next\": \"the sub-goal after current, if any\",\n" +
                "    \"remaining\": [\"all further user outcomes in order\"]\n" +
                "  },\n" +
                "  \"safety\": {\n" +
                "    \"ok\": true,\n" +
                "    \"dangerous\": false,\n" +
                "    \"risk\": \"low|medium|high\",\n" +
                "    \"causes\": \"what this action does\",\n" +
                "    \"effects\": \"what will change on screen or in the OS\",\n" +
                "    \"reason\": \"why it is okay or not\"\n" +
                "  },\n" +
                "  \"action\": {\n" +
                "    \"type\": \"screenshot|move|click|double_click|right_click|drag|scroll|type|key|wait|zoom|open|done\",\n" +
                "    \"x\": 0, \"y\": 0, \"x2\": 0, \"y2\": 0, \"dx\": 0, \"dy\": 0,\n" +
                "    \"text\": \"\", \"keys\": \"\", \"ms\": 0, \"expected_state\": \"visible result expected in the next screenshot\",\n" +
                "    \"button\": \"left\", \"target_id\": \"ui1\",\n" +
                "    \"explanation\": \"one concise sentence of the intended GUI action\",\n" +
                "    \"outcome\": \"completed|blocked (required for done)\",\n" +
                "    \"summary\": \"final result when type=done\"\n" +
                "  }\n" +
                "}\n" +
                "Rules:\n" +
                "- One action per turn. After click/type/scroll/key, the next turn will include a fresh screenshot.\n" +
                "- Every GUI action must state its expected_state. The controller compares the next screenshot with the prior one. Pixel differences alone cannot prove success or failure; the separate visual assessment must establish the expected state.\n" +
                "- Before each action, reconcile the screenshot with [TASK PROGRESS] and [EXECUTION LEDGER]. Keep every verified sub-goal completed forever; do not revisit any completed task outcome in any app.\n" +
                "- task_progress.completed is controller-owned evidence. Echo it unchanged; do not add or remove entries. The controller has already fixed the ordered outcomes and their required evidence. Echo current/next/remaining; do not replace or skip them. Work only on CURRENT and its constraints.\n" +
                "- [EXECUTION LEDGER] is controller-owned action evidence for every application. pending means wait for the fresh screenshot; failed means choose a different recovery action; observed change means inspect whether the expected state is actually visible. Only verified means the expected state is supported; observed change alone does not permit completion.\n" +
                "- To open an app, use type=open with text set to the exact app name (example: Microsoft Edge, File Explorer, Notepad). Never click taskbar icons.\n" +
                "- An app launch is complete only when the controller reports [APP LAUNCH VERIFIED] from the real foreground window identity. A pixel change, a launch request, or an assumption is not proof that an app is open.\n" +
                "- For browser navigation, avoid tiny browser chrome: key Ctrl+T for a new tab, key Ctrl+L to focus the address bar, type the complete URL, then key Enter. A new tab is complete only when [BROWSER STATE] shows a higher tab count. A URL is complete only when [BROWSER STATE] shows the expected address and no error page.\n" +
                "- A [VERIFIED NAVIGATION URL] is controller-owned evidence. Type it exactly; never substitute a guessed account, path, or query. If an owned resource has no verified URL, do not manufacture one: finish that sub-goal as blocked and ask for its URL.\n" +
                "- A failed new-tab verification is a hard barrier: never navigate the existing tab as a fallback. Retry Ctrl+T after focus is restored, or use an accessible control explicitly named New tab. After typing a complete URL, press Enter before any further type action; never append a search query to an uncommitted address.\n" +
                "- When [ACCESSIBLE TARGETS] are provided, choose target_id for a named control; it is an exact control center and is more reliable than estimating x,y.\n" +
                "- Allowed launch keys: Win+S (Search), Win+E (Explorer), Win+I (Settings), Win+D (Desktop). Win+R and Win+X are blocked.\n" +
                "- Click only large, clearly labeled UI (buttons, links, window chrome). Tiny clustered icons will miss neighboring apps.\n" +
                "- Use type=drag with x,y as the start and x2,y2 as the end for anything done by holding the mouse button: drawing a stroke, dragging a shape out to size, selecting a region, moving a window by its title bar, or moving a slider. A click cannot do any of these — it presses and releases in one place. To draw, first select the tool, then drag; one stroke per turn.\n" +
                "- The two points of a drag must differ. A shape tool draws the box between them, so drag corner to corner: {\"type\":\"drag\",\"x\":500,\"y\":300,\"x2\":780,\"y2\":560} draws a shape about 280 by 260 pixels. A line or freehand stroke runs from the first point to the second.\n" +
                "- If an action missed or nothing changed, do NOT repeat the same target blindly. Inspect the new screenshot and choose a different recovery method appropriate to the active application.\n" +
                "- Many outcomes are built from several actions (a drawing from several strokes, a form from several fields). A partial result is PROGRESS, not failure: when the screenshot shows part of the work done, keep it and do the next missing part. Never redo a part that is already visible, and do not re-select a tool that is already selected.\n" +
                "- Use type=done with outcome=completed only after all user outcomes are verified; use outcome=blocked with an honest explanation when recovery cannot proceed.\n" +
                "- Never type secrets you cannot see. Never dump credentials. Never format disks, shut down, or send money.\n" +
                "- key uses combos like Enter, Tab, Ctrl+S, Win+E, Alt+Tab.\n" +
                "- scroll dy is wheel steps; negative scrolls up. zoom dy>0 zooms in (Ctrl+wheel).\n" +
                (mode == ComputerUseMode.Autopilot
                    ? "- Autopilot is on: you may act without a person clicking Allow, but you MUST fill safety.causes, safety.effects, and safety.dangerous on every action.\n"
                    : "- Ask mode is on: keep explanation short; the user must approve each action before it runs.\n");
        }

        public static async Task<ComputerUseSessionResult> RunAsync(ComputerUseSessionRequest request, CancellationToken token)
        {
            try
            {
                var result = await RunCoreAsync(request, token).ConfigureAwait(true);
                await BackendLogService.LogEventAsync("ComputerUse.SessionEnded",
                    $"Completed:{result.Completed}; steps:{result.Steps}; {result.Summary}").ConfigureAwait(true);
                return result;
            }
            catch (Exception ex)
            {
                // Every session records how it ended, including the ways that were never meant to
                // happen. Without this an unhandled fault leaves the session log simply stopping
                // mid-run, and the only trace is in a separate error log — which is exactly how a
                // crash here read as "it just closed on its own".
                await BackendLogService.LogEventAsync("ComputerUse.SessionEnded",
                    $"Completed:False; ended by {ex.GetType().Name}: {ex.Message}").ConfigureAwait(true);
                throw;
            }
        }

        private static async Task<ComputerUseSessionResult> RunCoreAsync(ComputerUseSessionRequest request, CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(request);
            Window host = request.HostWindow;
            WindowState previousState = host.WindowState;
            bool previousTopmost = host.Topmost;
            ComputerUseSessionWindow? hud = null;
            ComputerUsePointerWindow? pointer = null;
            ComputerUseMode mode = request.InitialMode;
            var transcript = new StringBuilder();
            int steps = 0;
            string summary = "Computer Use stopped.";

            try
            {
                host.WindowState = WindowState.Minimized;
                hud = new ComputerUseSessionWindow();
                if (mode == ComputerUseMode.Autopilot)
                    hud.AutopilotModeRadio.IsChecked = true;
                else
                    hud.AskModeRadio.IsChecked = true;
                hud.ModeChanged += next => mode = next;
                hud.StopRequested += () =>
                {
                    try { request.OnStop?.Invoke(); } catch { }
                    try { request.OnChat?.Invoke("Computer Use stop requested."); } catch { }
                };
                hud.Show();
                pointer = new ComputerUsePointerWindow();
                pointer.Show();
                var cursor = ComputerUseNativeInput.GetCursor();
                pointer.PlaceAtScreen(cursor.X, cursor.Y);

                hud.SetStatus($"{request.ExecutionSurface} · {request.ActingModelLabel}");
                hud.AppendLog($"Goal: {request.Goal}");
                request.OnChat?.Invoke($"Computer Use started ({request.ExecutionSurface} / {request.ActingModelLabel}). Mode: {mode}.");

                string? lastObservation = null;
                ComputerUseAction? lastExecuted = null;
                var recentActions = new List<string>();
                var taskProgress = new ComputerUseTaskProgress();
                ComputerUseTaskContract? taskContract = null;
                string? pendingApplicationLaunch = null;
                bool applicationLaunchRecoveryRequired = false;
                int? pendingNewTabCount = null;
                bool separateTabRecoveryRequired = false;
                string? typedBrowserUrl = null;
                bool addressEntryExpected = false;
                string? pendingBrowserUrl = null;
                PendingActionEvidence? pendingActionEvidence = null;
                var executionLedger = new ComputerUseExecutionLedger();
                bool lastActionProducedVisualEvidence = true;
                // Whether the SCREEN changed, measured by the controller itself. Distinct from the
                // line above, which is whether the observer was willing to certify the expected
                // state. Conflating them made an unconfirmable-but-successful click — selecting a
                // small toolbar tool, whose only visible effect is a highlight — look like a
                // pointer that had missed, and three of those ended the session.
                bool screenChangedAfterLastAction = true;
                int repeatCount = 0;
                int turnsWithoutInput = 0;
                int inferenceFailures = 0;
                int actionsWithoutVerifiedEffect = 0;
                int visionDetailLevel = 0;
                int turnsSinceDetailReduction = 0;
                int? lastKnownTabCount = null;
                int? tabCountBeforeAction = null;
                string? refusedOutcome = null;
                int outcomeRefusals = 0;
                while (!token.IsCancellationRequested && steps < MaxSteps)
                {
                    if (++turnsWithoutInput > 5)
                    {
                        summary = "Computer Use stopped without completing the task after five turns without desktop input. " + lastObservation;
                        return new ComputerUseSessionResult { Completed = false, Summary = summary, Steps = steps };
                    }
                    steps++;
                    try
                    {
                    hud.SetStatus($"Step {steps}/{MaxSteps} · capturing");
                    ComputerUseCapture capture = ComputerUseScreenCapture.CaptureDesktop(visionDetailLevel, hud, pointer);
                    if (taskContract == null)
                    {
                        hud.BeginWait("Defining requested outcomes");
                        // Recorded before the call, so a turn that never comes back leaves a trace
                        // instead of an unexplained gap between session start and nothing at all.
                        await BackendLogService.LogEventAsync("ComputerUse.PlanStarted", request.Goal).ConfigureAwait(true);
                        ComputerUsePlanning.Result plan;
                        try
                        {
                            plan = await ComputerUsePlanning.CreateAsync(request.Goal, request.NavigationContext,
                                (system, payload, ct) => InferTurnWithTimeoutAsync(request, system, payload, capture, ct, planning: true),
                                token, async (attempt, error, raw) =>
                                {
                                    hud.BeginWait(attempt < 3 ? $"Correcting outcome plan ({attempt + 1}/3)" : "Outcome planning failed");
                                    hud.AppendLog($"Plan attempt {attempt}: {error}");
                                    await BackendLogService.LogEventAsync("ComputerUse.PlanRejected",
                                        $"Attempt {attempt}: {error}\nResponse: {raw[..Math.Min(raw.Length, 16000)]}").ConfigureAwait(true);
                                }).ConfigureAwait(true);
                        }
                        finally
                        {
                            hud.EndWait();
                        }
                        taskContract = plan.Contract;
                        if (taskContract == null)
                        {
                            string failure = $"Computer Use could not start after {plan.Attempts} planning attempts: {plan.Error} No desktop input was sent.";
                            hud.AppendLog(failure);
                            return new ComputerUseSessionResult { Completed = false, Steps = steps,
                                Summary = failure };
                        }
                        taskProgress = taskContract!.Progress(taskProgress.Completed);
                        // A salvaged plan runs, but never silently: if coverage of the request
                        // could not be fully established, the user is told before anything happens.
                        if (!string.IsNullOrWhiteSpace(plan.Warning))
                        {
                            hud.AppendLog("[PLAN WARNING] " + plan.Warning);
                            request.OnChat?.Invoke("Computer Use plan warning: " + plan.Warning);
                        }

                        hud.AppendLog(taskContract.Describe());
                        await BackendLogService.LogEventAsync("ComputerUse.Plan", plan.RawPlan).ConfigureAwait(true);
                        // Planning may take time. Refresh the screen before asking for input.
                        continue;
                    }
                    if (capture.BrowserState?.HasTabTelemetry == true)
                    {
                        int observedTabCount = capture.BrowserState.TabCount;
                        // Say so plainly when a tab appears, however it was opened. Without this a
                        // tab created by clicking "+" at coordinates goes uncredited, the model
                        // reads its own successful action as a failure, and opens another one.
                        if (tabCountBeforeAction is int before && observedTabCount > before)
                        {
                            string tabObservation = $"[BROWSER VERIFIED] A new tab is open (tab count went from {before} to {observedTabCount}). Do not open another.";
                            lastObservation = string.IsNullOrWhiteSpace(lastObservation)
                                ? tabObservation
                                : lastObservation + "\n" + tabObservation;
                            hud.AppendLog(tabObservation);
                            separateTabRecoveryRequired = false;
                            pendingNewTabCount = null;
                        }

                        lastKnownTabCount = observedTabCount;
                        tabCountBeforeAction = null;
                    }
                    pointer.PlaceAtScreen(ComputerUseNativeInput.GetCursor().X, ComputerUseNativeInput.GetCursor().Y);
                    hud.AppendLog($"Monitor {capture.ScreenWidth}x{capture.ScreenHeight} @ ({capture.ScreenX},{capture.ScreenY}) · image {capture.ImageWidth}x{capture.ImageHeight}");

                    string applicationVerification = ReconcileApplicationLaunch(
                        capture,
                        ref pendingApplicationLaunch,
                        ref applicationLaunchRecoveryRequired,
                        ref taskProgress);
                    if (!string.IsNullOrWhiteSpace(applicationVerification))
                    {
                        lastObservation = string.IsNullOrWhiteSpace(lastObservation)
                            ? applicationVerification
                            : lastObservation + "\n" + applicationVerification;
                        hud.AppendLog(applicationVerification);
                    }

                    string verification = ReconcileBrowserState(
                        capture,
                        ref pendingNewTabCount,
                        ref pendingBrowserUrl,
                        ref separateTabRecoveryRequired,
                        ref taskProgress,
                        taskContract);
                    if (!string.IsNullOrWhiteSpace(verification))
                    {
                        lastObservation = string.IsNullOrWhiteSpace(lastObservation)
                            ? verification
                            : lastObservation + "\n" + verification;
                        hud.AppendLog(verification);
                    }

                    ComputerUseTaskContract.Outcome? completedOutcome = taskContract.Current;
                    string outcomeObservation = taskContract.ObserveCurrentOutcome(capture);
                    if (!string.IsNullOrWhiteSpace(outcomeObservation))
                    {
                        if (outcomeObservation.StartsWith("[OUTCOME VERIFIED]", StringComparison.Ordinal))
                        {
                            if (completedOutcome != null)
                            {
                                taskProgress = AddVerifiedCompletion(taskProgress, completedOutcome.Description);
                                taskProgress = AddVerifiedCompletion(taskProgress, "Opened " + completedOutcome.Destination);
                            }

                            taskProgress = taskContract.Progress(taskProgress.Completed);
                            // The destination is settled, so any half-entered address for it is
                            // stale bookkeeping that would otherwise block the next outcome.
                            typedBrowserUrl = null;
                            pendingBrowserUrl = null;
                            addressEntryExpected = false;
                            await BackendLogService.LogEventAsync("ComputerUse.Outcome",
                                $"Step {steps}; accepted=True (controller evidence); {outcomeObservation}\n{taskContract.Describe()}").ConfigureAwait(true);
                        }

                        lastObservation = string.IsNullOrWhiteSpace(lastObservation)
                            ? outcomeObservation
                            : lastObservation + "\n" + outcomeObservation;
                        hud.AppendLog(outcomeObservation);
                    }

                    // Every requested outcome has been verified from real evidence at the moment it
                    // happened, so the task is done and the session ends here. It must NOT wait for
                    // the model to volunteer "done" and then have that judged by an observer: one
                    // screenshot shows one tab, so a finished multi-tab request reads as unfinished
                    // forever, and a completed run loops on "task complete / not complete" instead
                    // of stopping.
                    if (taskContract.Complete)
                    {
                        summary = taskProgress.Completed.Count > 0
                            ? "Computer Use finished every requested outcome: " + string.Join("; ", taskProgress.Completed)
                            : "Computer Use finished every requested outcome.";
                        hud.SetStatus("Done");
                        hud.AppendLog(summary);
                        await BackendLogService.LogEventAsync("ComputerUse.TaskComplete",
                            $"Step {steps}; {summary}").ConfigureAwait(true);
                        return new ComputerUseSessionResult { Completed = true, Summary = summary, Steps = steps };
                    }

                    string actionEvidence = ReconcileActionEvidence(capture, ref pendingActionEvidence, executionLedger, ref screenChangedAfterLastAction);
                    lastActionProducedVisualEvidence = screenChangedAfterLastAction;
                    if (!string.IsNullOrWhiteSpace(actionEvidence))
                    {
                        lastObservation = string.IsNullOrWhiteSpace(lastObservation)
                            ? actionEvidence
                            : lastObservation + "\n" + actionEvidence;
                        hud.AppendLog(actionEvidence);
                    }

                    bool taskVisuallyComplete = false;
                    if (executionLedger.State != ComputerUseActionEvidenceState.None)
                    {
                        hud.BeginWait($"Step {steps}/{MaxSteps} · verifying visible outcome");
                        // The observer is told never to override an unresolved controller
                        // constraint, so only constraints bearing on the CURRENT outcome may be
                        // shown to it. A pending browser navigation belonging to a later outcome
                        // otherwise suppresses completion of an application launch that is plainly
                        // done, and the contract stalls there for the rest of the run.
                        string assessmentConstraints = taskContract.Describe()
                            + "\n" + applicationVerification
                            + (taskContract.Current?.Kind == "browser" ? "\n" + verification : "");
                        string assessmentRaw = await InferTurnWithTimeoutAsync(request,
                                ComputerUseVisualAssessment.SystemPrompt,
                                ComputerUseVisualAssessment.BuildPayload(request.Goal, taskProgress, capture,
                                    executionLedger, assessmentConstraints),
                                capture, token, verification: true).ConfigureAwait(true);
                        hud.EndWait();
                        var assessment = ComputerUseVisualAssessment.Parse(assessmentRaw);
                        // An unconfirmed tab only blocks completion when an outcome actually
                        // requires a separate tab. Otherwise it is bookkeeping for a tab the model
                        // opened on its own, and letting it gate completion strands every outcome
                        // behind an action the task never asked for.
                        bool unresolved = applicationLaunchRecoveryRequired || separateTabRecoveryRequired
                            || (pendingNewTabCount.HasValue && taskContract.Current?.SeparateTab == true)
                            || !string.IsNullOrWhiteSpace(pendingBrowserUrl)
                            || capture.BrowserState?.IsErrorPage == true;
                        executionLedger.AssessExpectedState(assessment.ActionSucceeded && !unresolved, assessment.Evidence);
                        lastActionProducedVisualEvidence = executionLedger.CanFinish;
                        if (lastActionProducedVisualEvidence)
                            actionsWithoutVerifiedEffect = 0;
                        else if (!string.IsNullOrWhiteSpace(actionEvidence) && ++actionsWithoutVerifiedEffect >= 6)
                            return new ComputerUseSessionResult { Completed = false, Steps = steps,
                                Summary = "Computer Use stopped after six actions without a verified effect. " + assessment.Evidence };
                        lastObservation = "[VISUAL ASSESSMENT] " + assessment.Evidence +
                            (unresolved ? " Controller constraints remain unresolved; no completion accepted." : "");
                        hud.AppendLog(lastObservation);
                        var requiredOutcome = taskContract.Current;
                        if (!unresolved && string.IsNullOrWhiteSpace(typedBrowserUrl) && requiredOutcome != null)
                        {
                            bool accepted = taskContract.TryComplete(capture, assessment, out string outcomeEvidence);
                            if (accepted)
                            {
                                refusedOutcome = null;
                                outcomeRefusals = 0;
                            }
                            else
                            {
                                if (!string.Equals(refusedOutcome, requiredOutcome.Description, StringComparison.Ordinal))
                                {
                                    refusedOutcome = requiredOutcome.Description;
                                    outcomeRefusals = 0;
                                }

                                // The destination is confirmed loaded and the observer still will
                                // not sign the outcome off. Repeating the navigation cannot change
                                // that, and silently retrying it forever is what a stalled run
                                // looks like from outside. Name the situation so the agent can
                                // either do what remains or finish the outcome as blocked.
                                if (++outcomeRefusals >= 4 && taskContract.CurrentDestinationReached)
                                {
                                    outcomeEvidence += "\n[OUTCOME STALLED] The required destination is confirmed loaded, "
                                        + $"but this outcome has not been accepted after {outcomeRefusals} checks. Do NOT navigate to it again. "
                                        + "If it requires anything beyond arriving, do that now; otherwise return done with outcome=blocked and say what is missing.";
                                    await BackendLogService.LogEventAsync("ComputerUse.OutcomeStalled",
                                        $"Step {steps}; refusals:{outcomeRefusals}; {taskContract.Describe()}\nAssessment: {assessment.Evidence}").ConfigureAwait(true);
                                }
                            }
                            hud.AppendLog(outcomeEvidence);
                            lastObservation += "\n" + outcomeEvidence;
                            if (accepted)
                            {
                                taskProgress = AddVerifiedCompletion(taskProgress, requiredOutcome.Description);
                                if (requiredOutcome.Kind == "browser")
                                    taskProgress = AddVerifiedCompletion(taskProgress, "Opened " + requiredOutcome.Destination);
                                taskProgress = taskContract.Progress(taskProgress.Completed);
                            }
                            await BackendLogService.LogEventAsync("ComputerUse.Outcome",
                                $"Step {steps}; accepted={accepted}; {outcomeEvidence}\n{taskContract.Describe()}").ConfigureAwait(true);
                        }
                        taskVisuallyComplete = !unresolved && assessment.TaskCompleted
                            && taskContract.Complete
                            && string.IsNullOrWhiteSpace(typedBrowserUrl)
                            && string.IsNullOrWhiteSpace(taskProgress.Current)
                            && string.IsNullOrWhiteSpace(taskProgress.Next) && taskProgress.Remaining.Count == 0;
                    }

                    string userPayload = BuildUserPayload(request.Goal, request.NavigationContext, capture,
                        taskContract.Describe() + "\n" + lastObservation, recentActions, taskProgress, executionLedger, steps, mode);
                    hud.BeginWait($"Step {steps}/{MaxSteps} · thinking");
                    string raw;
                    try
                    {
                        raw = await InferTurnWithTimeoutAsync(
                            request,
                            BuildSystemPrompt(capture, mode),
                            userPayload,
                            capture,
                            token).ConfigureAwait(true);
                    }
                    finally
                    {
                        hud.EndWait();
                    }

                    inferenceFailures = 0;
                    // A turn completed at the reduced payload. Only climb back toward full detail
                    // after the provider has proven stable, so recovery never oscillates.
                    if (visionDetailLevel > 0 && ++turnsSinceDetailReduction >= 4)
                    {
                        visionDetailLevel--;
                        turnsSinceDetailReduction = 0;
                        hud.AppendLog($"Model stable again — restoring screenshot detail to {ComputerUseImageGeometry.MaxEdgeForDetailLevel(visionDetailLevel)}px.");
                    }
                    ComputerUseTurn turn = ComputerUseActionParser.Parse(raw);
                    if (!string.IsNullOrWhiteSpace(turn.Thinking))
                        hud.AppendLog(turn.Thinking);

                    // The session log recorded outcome checks but never the actions themselves, so a
                    // run that "did nothing" left no way to tell whether the model chose nothing,
                    // chose the wrong thing, or chose the right thing and was refused.
                    await BackendLogService.LogEventAsync("ComputerUse.Turn",
                        $"Step {steps}; parsed:{turn.Parsed}; chose: {(turn.Parsed ? turn.Action.ShortLabel : "(unparseable)")}"
                        + (turn.Parsed && turn.Action.Type == ComputerUseActionType.Drag
                            ? $" [{turn.Action.X},{turn.Action.Y} -> {turn.Action.X2},{turn.Action.Y2}]\nRaw: {Clip(ExtractActionJson(raw), 500)}"
                            : "")
                        + $"\nThinking: {Clip(turn.Thinking, 400)}").ConfigureAwait(true);

                    if (!turn.Parsed)
                    {
                        lastObservation = "[COMPUTER USE PARSE ERROR]\nReturn only the required JSON object. " + turn.ParseError;
                        hud.AppendLog("Could not parse an action. Asking the model to retry.");
                        continue;
                    }

                    taskProgress = MergeTaskProgress(taskProgress, turn.Progress);
                    if (!taskProgress.PlanInitialized && turn.Action.Type != ComputerUseActionType.Done)
                    {
                        lastObservation = "[PLAN REQUIRED] Enumerate the requested outcomes in task_progress.current, next, and remaining before acting.";
                        continue;
                    }

                    ComputerUseSafetyVerdict safety = ComputerUseSafety.Evaluate(turn.Action, turn.Safety);
                    hud.AppendLog($"{turn.Action.ShortLabel}\nRisk {safety.Risk}. {safety.Reason}".Trim());

                    if (turn.Action.Type == ComputerUseActionType.Done)
                    {
                        if (string.Equals(turn.Action.Outcome, "blocked", StringComparison.OrdinalIgnoreCase))
                            return new ComputerUseSessionResult { Completed = false, Steps = steps,
                                Summary = "Computer Use could not complete the task: " + turn.Action.Summary };
                        // A complete contract is proof in its own right; the visual assessment is
                        // only needed while outcomes remain unverified.
                        if (!taskContract.Complete
                            && (!taskVisuallyComplete || !string.IsNullOrWhiteSpace(typedBrowserUrl)
                                || separateTabRecoveryRequired))
                        {
                            lastObservation = "[COMPLETION NOT VERIFIED] The full user request is not supported by fresh evidence. Continue the unfinished goal or return done with outcome=blocked and explain what remains.";
                            continue;
                        }
                        if (applicationLaunchRecoveryRequired)
                        {
                            lastObservation = "[APP LAUNCH VERIFICATION FAILED] The requested app is not the visible foreground window. Do not report success; retry type=open or use a visible launch recovery.";
                            hud.AppendLog("Blocked Done: requested app launch remains unverified.");
                            continue;
                        }
                        if (!executionLedger.CanFinish)
                        {
                            lastObservation = "[EXECUTION LEDGER BLOCK] The previous action is still " + executionLedger.State.ToString().ToLowerInvariant() + ". " +
                                "Capture and inspect its result or perform a recovery action before reporting completion.";
                            hud.AppendLog("Blocked Done: an action is unresolved in the execution ledger.");
                            continue;
                        }
                        if (capture.BrowserState?.IsErrorPage == true)
                        {
                            lastObservation = "[BROWSER VERIFICATION FAILED] The current browser page is an error page. Do not report success; recover the correct destination or finish as blocked.";
                            hud.AppendLog("Blocked Done: the visible browser page is an error.");
                            continue;
                        }
                        if (pendingNewTabCount.HasValue || !string.IsNullOrWhiteSpace(pendingBrowserUrl))
                        {
                            lastObservation = "[BROWSER VERIFICATION PENDING] A requested tab or URL has not been verified in a fresh browser observation. Do not report success yet.";
                            hud.AppendLog("Blocked Done: browser verification is pending.");
                            continue;
                        }
                        if (!lastActionProducedVisualEvidence)
                        {
                            lastObservation = "[POST-ACTION VERIFICATION FAILED] The screenshot did not visibly change after the last action. Do not report completion; inspect the current screen and choose a recovery action.";
                            hud.AppendLog("Blocked Done: no post-action visual evidence.");
                            continue;
                        }
                        summary = string.IsNullOrWhiteSpace(turn.Action.Summary) ? "Computer Use finished." : turn.Action.Summary;
                        hud.SetStatus("Done");
                        hud.AppendLog(summary);
                        return new ComputerUseSessionResult { Completed = true, Summary = summary, Steps = steps };
                    }

                    if (turn.Action.Type == ComputerUseActionType.Screenshot)
                    {
                        lastObservation = "Screenshot captured. Choose the next GUI action.";
                        continue;
                    }

                    if (taskContract.BlocksAction(turn.Action, capture, out string contractBlock, addressEntryExpected))
                    {
                        lastObservation = contractBlock;
                        hud.AppendLog(contractBlock);
                        continue;
                    }

                    if (applicationLaunchRecoveryRequired && IsBlockedWhileRecoveringApplicationLaunch(turn.Action))
                    {
                        lastObservation =
                            "[APP LAUNCH RECOVERY REQUIRED] The requested app has not appeared as the foreground window. Do not type text or send app-specific shortcuts to the desktop. " +
                            "Retry type=open with the requested app, or use a visible launch control.";
                        hud.AppendLog("Blocked input while the requested app launch remains unverified.");
                        continue;
                    }

                    if (separateTabRecoveryRequired && IsBlockedWhileRecoveringSeparateTab(turn.Action, capture))
                    {
                        lastObservation =
                            "[TAB RECOVERY REQUIRED] A requested separate browser tab was not observed. Do not focus an address bar, type a URL/query, press Enter, or navigate the existing tab. " +
                            "Retry Ctrl+T after browser focus is confirmed, or click only the accessible control explicitly named New tab using its target_id.";
                        hud.AppendLog("Blocked same-tab navigation while the requested tab remains unresolved.");
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(typedBrowserUrl) && IsBlockedBeforeAddressCommit(turn.Action))
                    {
                        lastObservation =
                            "[ADDRESS COMMIT REQUIRED] A complete URL was just typed into the address bar. Press Enter and inspect the fresh browser state before typing anything else. " +
                            "Do not append a query or another URL to the address field.";
                        hud.AppendLog("Blocked additional typing before the address-bar URL was committed.");
                        continue;
                    }

                    if (turn.Action.Type == ComputerUseActionType.Type
                        && capture.BrowserState?.AddressHasFocus == true
                        && ComputerUseTrustedNavigation.IsUnverifiedOwnedRepositoryNavigation(request.Goal, request.NavigationContext, turn.Action.Text))
                    {
                        lastObservation = "[OWNED RESOURCE EVIDENCE REQUIRED] This task names an owned GitHub repository, but its exact URL is not controller-verified. Do not type a guessed github.com owner/path. Finish this sub-goal as blocked and ask the user for the repository URL.";
                        hud.AppendLog("Blocked an inferred GitHub repository address without evidence.");
                        continue;
                    }

                    bool incompleteAutopilotPass = mode == ComputerUseMode.Autopilot
                        && (string.IsNullOrWhiteSpace(turn.Safety.Causes) || string.IsNullOrWhiteSpace(turn.Safety.Effects));
                    if (incompleteAutopilotPass)
                    {
                        lastObservation = "[COMPUTER USE SAFETY PASS REQUIRED]\nAutopilot refused to run this action because causes/effects were missing. Resend JSON with a complete safety object.";
                        hud.AppendLog("Autopilot held the action for a missing safety pass.");
                        continue;
                    }

                    if (safety.Dangerous)
                    {
                        lastObservation = ComputerUseSafety.BuildRefusalObservation(turn.Action, safety);
                        hud.AppendLog("Blocked as dangerous. Use type=open with the app name instead of Win+R or taskbar icons.");
                        continue;
                    }

                    if ((turn.Action.Type is ComputerUseActionType.Click or ComputerUseActionType.DoubleClick or ComputerUseActionType.RightClick)
                        && ComputerUseCoordinateMapper.IsLikelyTaskbarClick(capture, turn.Action.X, turn.Action.Y))
                    {
                        lastObservation =
                            "[COMPUTER USE TASKBAR BLOCK]\nTaskbar icon clicks are too imprecise and hit neighboring apps.\n" +
                            "Use type=open with text set to the exact app name, for example {\"type\":\"open\",\"text\":\"Microsoft Edge\"}.";
                        hud.AppendLog("Blocked a taskbar-icon click. Use open instead.");
                        continue;
                    }

                    if (lastExecuted != null && !screenChangedAfterLastAction && IsRepeatSensitiveAction(turn.Action)
                        && ComputerUseCoordinateMapper.IsSameTarget(lastExecuted, turn.Action))
                    {
                        repeatCount++;
                        if (repeatCount >= RepeatAbortCount)
                        {
                            summary = "Computer Use stopped after repeating the same missed target. Use a keyboard shortcut on the next run if the click target is small.";
                            hud.AppendLog(summary);
                            return new ComputerUseSessionResult { Completed = false, Summary = summary, Steps = steps };
                        }

                        lastObservation =
                            "[COMPUTER USE REPEAT BLOCK]\nThe last pointer/type action used the same target and did not establish progress.\n" +
                            "Do not repeat it blindly; inspect the fresh screenshot and use a different target or recovery method.";
                        hud.AppendLog("Blocked a repeated missed pointer/type action.");
                        continue;
                    }

                    if (mode == ComputerUseMode.Ask || safety.RequiresAsk && mode == ComputerUseMode.Ask)
                    {
                        string explanation = FirstNonEmpty(
                            turn.Action.Explanation,
                            turn.Action.ShortLabel + " — " + safety.Effects,
                            turn.Action.ShortLabel);
                        hud.SetStatus("Waiting for permission");
                        bool allowed = await hud.RequestPermissionAsync(explanation).ConfigureAwait(true);
                        if (token.IsCancellationRequested)
                            break;
                        if (!allowed)
                        {
                            lastObservation = "[COMPUTER USE DENIED BY USER]\nThe user denied: " + turn.Action.ShortLabel + ". Choose a different action or type=done.";
                            hud.AppendLog("User denied this action.");
                            continue;
                        }
                    }

                    hud.SetStatus($"Step {steps}/{MaxSteps} · {turn.Action.Type}");
                    try
                    {
                        lastObservation = await ExecuteActionAsync(turn.Action, capture, hud, pointer, token).ConfigureAwait(true);
                        await BackendLogService.LogEventAsync("ComputerUse.Action",
                            $"Step {steps}; {turn.Action.ShortLabel}; target window: {capture.ForegroundProcessName}: {Clip(capture.ForegroundWindowTitle, 80)}"
                            + $"\nResult: {Clip(lastObservation, 400)}").ConfigureAwait(true);
                    }
                    catch (Exception actionEx) when (actionEx is not OperationCanceledException)
                    {
                        // One action failing is not a reason to destroy a session that has already
                        // verified work. Sending input touches native APIs, window handles and UI
                        // objects, so a fault here is a normal hazard and belongs in the loop's
                        // recovery path — the run reports what went wrong and picks a different
                        // approach, rather than the panel vanishing with no summary at all.
                        lastObservation = "[ACTION NOT SENT] " + turn.Action.ShortLabel
                            + " could not be carried out: " + actionEx.Message
                            + " Inspect the fresh screenshot and try a different approach.";
                        hud.AppendLog(lastObservation);
                        await BackendLogService.LogErrorAsync("ComputerUse.ActionFailed", actionEx).ConfigureAwait(true);
                        continue;
                    }
                    if (lastObservation.StartsWith("[ACTION NOT SENT]", StringComparison.Ordinal))
                    {
                        hud.AppendLog(lastObservation);
                        continue;
                    }
                    if (RequiresVisualEvidence(turn.Action))
                        turnsWithoutInput = 0;
                    TrackApplicationLaunch(turn.Action, ref pendingApplicationLaunch);
                    TrackBrowserIntent(turn.Action, capture, ref pendingNewTabCount, ref typedBrowserUrl,
                        ref pendingBrowserUrl, addressEntryExpected, lastKnownTabCount);
                    tabCountBeforeAction = lastKnownTabCount;
                    addressEntryExpected = UpdateAddressEntryIntent(addressEntryExpected, turn.Action, capture);
                    pendingActionEvidence = RequiresVisualEvidence(turn.Action)
                        ? new PendingActionEvidence
                        {
                            BeforeScreenshot = capture.JpegBytes,
                            ActionLabel = turn.Action.ShortLabel,
                            ExpectedState = GetImmediateExpectedState(turn.Action)
                        }
                        : null;
                    if (RequiresVisualEvidence(turn.Action))
                        executionLedger.BeginAction(turn.Action, GetImmediateExpectedState(turn.Action));
                    lastExecuted = turn.Action;
                    repeatCount = 0;
                    transcript.AppendLine(turn.Action.ShortLabel);
                    recentActions.Add($"{steps}. {turn.Action.ShortLabel} -> {lastObservation}");
                    if (recentActions.Count > 8)
                        recentActions.RemoveAt(0);
                    await Task.Delay(GetPostActionSettleDelay(turn.Action), token).ConfigureAwait(true);
                    if (RequiresVisualEvidence(turn.Action))
                    {
                        hud.BeginWait($"Step {steps}/{MaxSteps} · waiting for the screen to settle");
                        try
                        {
                            await WaitForScreenToSettleAsync(visionDetailLevel, hud, pointer, token).ConfigureAwait(true);
                        }
                        finally
                        {
                            hud.EndWait();
                        }
                    }
                    }
                    catch (ComputerUseInferenceInterruptedException ex)
                    {
                        hud.EndWait();
                        inferenceFailures++;
                        turnsWithoutInput = 0;
                        // Resending the payload that just killed the provider will kill it again.
                        // Step the screenshot down the detail ladder so each retry is materially
                        // cheaper than the request that failed. This is provider-agnostic: it
                        // helps a memory-starved local runner, an over-long context, and a
                        // request-size rejection alike.
                        string detailNote = "";
                        if (ComputerUseImageGeometry.CanReduceDetail(visionDetailLevel))
                        {
                            visionDetailLevel++;
                            turnsSinceDetailReduction = 0;
                            detailNote = $" Retrying with a smaller {ComputerUseImageGeometry.MaxEdgeForDetailLevel(visionDetailLevel)}px screenshot.";
                        }
                        lastObservation = "[INFERENCE INTERRUPTED] " + ex.Message + " Progress is retained. Inspect the fresh screenshot before choosing the next action.";
                        hud.AppendLog(lastObservation + detailNote);
                        await BackendLogService.LogEventAsync("ComputerUse.InferenceRecovery",
                            $"Step:{steps}; attempt:{inferenceFailures}; detail:{visionDetailLevel}; ledger:{executionLedger.State}; {ex}").ConfigureAwait(true);
                        if (inferenceFailures <= 3)
                        {
                            hud.SetStatus($"Model interrupted — retrying observation ({inferenceFailures}/3)");
                            await Task.Delay(TimeSpan.FromSeconds(inferenceFailures * 2), token).ConfigureAwait(true);
                        }
                        else
                        {
                            bool retry = await hud.RequestRecoveryAsync(ex.Message, token).ConfigureAwait(true);
                            if (!retry)
                            {
                                summary = "Computer Use stopped by the user while model inference was unavailable. The task remains unfinished.";
                                break;
                            }
                            inferenceFailures = 0;
                        }
                    }
                }

                if (steps >= MaxSteps)
                    summary = "Computer Use reached the step limit.";
                else if (token.IsCancellationRequested)
                    summary = "Computer Use stopped by the user.";

                hud?.AppendLog(summary);
                return new ComputerUseSessionResult { Completed = false, Summary = summary, Steps = steps };
            }
            finally
            {
                try { pointer?.Close(); } catch { }
                try { hud?.ClearPermission(); hud?.Close(); } catch { }
                try
                {
                    host.Topmost = previousTopmost;
                    if (host.WindowState == WindowState.Minimized)
                        host.WindowState = previousState == WindowState.Minimized ? WindowState.Normal : previousState;
                    host.Activate();
                }
                catch
                {
                }
            }
        }

        private static async Task<string> InferTurnWithTimeoutAsync(
            ComputerUseSessionRequest request,
            string systemPrompt,
            string userPayload,
            ComputerUseCapture capture,
            CancellationToken token,
            bool verification = false,
            bool planning = false)
        {
            return await ComputerUseInference.RunAsync(
                ct => (planning ? request.PlanAsync : verification ? request.VerifyAsync : request.InferAsync)(systemPrompt, userPayload, capture, ct),
                planning ? PlanningTurnTimeout : ModelTurnTimeout, token).ConfigureAwait(true);
        }

        private static string BuildUserPayload(string goal, string navigationContext, ComputerUseCapture capture, string? observation, IReadOnlyList<string> recentActions, ComputerUseTaskProgress taskProgress, ComputerUseExecutionLedger executionLedger, int step, ComputerUseMode mode)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[COMPUTER USE GOAL]");
            builder.AppendLine(goal);
            builder.AppendLine();
            builder.AppendLine("[TASK PROGRESS — PRESERVE COMPLETED GOALS]");
            if (taskProgress.Completed.Count == 0)
                builder.AppendLine("COMPLETED: none verified yet");
            else
            {
                builder.AppendLine("COMPLETED:");
                foreach (string completed in taskProgress.Completed)
                    builder.AppendLine("- " + completed);
            }
            builder.AppendLine("CURRENT: " + FirstNonEmpty(taskProgress.Current, taskProgress.PlanInitialized ? "All planned outcomes are verified; report the final result." : "Identify the first unfinished goal."));
            foreach (string remaining in taskProgress.Remaining)
                builder.AppendLine("LATER: " + remaining);
            if (!string.IsNullOrWhiteSpace(taskProgress.Next))
                builder.AppendLine("NEXT: " + taskProgress.Next);
            builder.AppendLine("COMPLETED is controller-verified evidence only. Do not claim a completion in it yourself. Do not type, delete, or navigate to a completed destination.");
            builder.AppendLine();
            builder.AppendLine("[EXECUTION LEDGER — CONTROLLER OWNED]");
            builder.AppendLine(executionLedger.Describe());
            builder.AppendLine("If evidence state is failed, select a different recovery action. If it is pending, inspect the fresh screenshot before taking another action. Do not report done while it is pending or failed.");
            builder.AppendLine();
            if (!string.IsNullOrWhiteSpace(navigationContext)
                && !HasVerifiedNavigationContext(navigationContext, taskProgress))
            {
                builder.AppendLine("[TRUSTED NAVIGATION CONTEXT]");
                builder.AppendLine(navigationContext.Trim());
                builder.AppendLine();
            }
            builder.AppendLine($"[TURN] {step}/{MaxSteps}  [MODE] {mode}");
            builder.AppendLine($"[SCREENSHOT] JPEG is {capture.ImageWidth}x{capture.ImageHeight} in image coordinates (scaled from the native monitor). Yellow labels are the x,y you must use.");
            builder.AppendLine($"[FOREGROUND WINDOW] Process: {FirstNonEmpty(capture.ForegroundProcessName, "unknown")}; title: {FirstNonEmpty(capture.ForegroundWindowTitle, "unknown")}");
            builder.AppendLine("[BROWSER STATE]");
            builder.AppendLine(ComputerUseBrowserVerification.Describe(capture.BrowserState));
            builder.AppendLine("[LAUNCH] To open an app, return {\"action\":{\"type\":\"open\",\"text\":\"Microsoft Edge\"}}. Do not click the taskbar.");
            if (capture.UiTargets.Count > 0)
            {
                builder.AppendLine("[ACCESSIBLE TARGETS]");
                foreach (ComputerUseUiTarget target in capture.UiTargets)
                    builder.AppendLine(target.Describe());
            }
            if (recentActions.Count > 0)
            {
                builder.AppendLine("[RECENT ACTIONS]");
                foreach (string action in recentActions)
                    builder.AppendLine(action);
                builder.AppendLine("[VERIFY] Check the new screenshot for the expected visible result before advancing the plan. If it did not happen, choose a different method rather than repeating a click.");
            }
            if (!string.IsNullOrWhiteSpace(observation))
            {
                builder.AppendLine();
                builder.AppendLine("[PREVIOUS ACTION RESULT]");
                builder.AppendLine(observation.Trim());
            }

            builder.AppendLine();
            builder.AppendLine("Return the next JSON action now.");
            return builder.ToString();
        }

        private static async Task<string> ExecuteActionAsync(
            ComputerUseAction action,
            ComputerUseCapture capture,
            ComputerUseSessionWindow hud,
            ComputerUsePointerWindow pointer,
            CancellationToken token)
        {
            int actionX = action.X;
            int actionY = action.Y;
            // Only a pointer action aims at a control. Ctrl+T has nowhere to land, so a target id
            // that arrived on a keystroke is noise and must not be able to veto the keystroke.
            bool targetApplies = RequiresPointerTarget(action);
            string requestedTargetId = action.TargetId.Trim();
            string resolvedTargetName = capture.UiTargets
                .FirstOrDefault(target => string.Equals(target.Id, requestedTargetId, StringComparison.OrdinalIgnoreCase))?.Name
                ?? requestedTargetId;
            bool useTarget = targetApplies && requestedTargetId.Length > 0;
            if (useTarget && !ComputerUseUiCatalog.TryResolve(capture, requestedTargetId, out actionX, out actionY))
            {
                bool wasNeverIssued = !ComputerUseTargetId.IsIssued(requestedTargetId);
                bool coordinatesUsable = actionX > 0 || actionY > 0;
                if (!wasNeverIssued || !coordinatesUsable)
                {
                    return $"[ACTION NOT SENT] Target id '{requestedTargetId}' is stale or unavailable. Inspect the fresh screenshot and choose a current target or coordinates.";
                }

                // The model named the control it meant rather than quoting an id the controller
                // issued. That is not a claim about the screen, so it is dropped and the
                // coordinates it supplied alongside are used — the post-action screenshot still has
                // to show the expected result, so a bad aim is caught the same way any other is.
                useTarget = false;
            }

            (int x, int y) = ComputerUseCoordinateMapper.MapToScreen(capture, actionX, actionY);
            bool hudWasVisible = hud.IsVisible;
            bool pointerWasVisible = pointer.IsVisible;
            try
            {
                if (hudWasVisible)
                    hud.Hide();
                if (pointerWasVisible)
                    pointer.Hide();
                if (capture.TargetWindowHandle != IntPtr.Zero)
                {
                    if (!ComputerUseNativeInput.ActivateWindow(capture.TargetWindowHandle))
                        return "[ACTION NOT SENT] The captured target window could not be activated. Capture again and recover.";
                    await Task.Delay(120, token).ConfigureAwait(true);
                }

                if (useTarget)
                {
                    var original = capture.UiTargets.First(target => string.Equals(target.Id, requestedTargetId, StringComparison.OrdinalIgnoreCase));
                    var matches = ComputerUseUiCatalog.Capture(capture.TargetWindowHandle,
                        capture.ScreenX, capture.ScreenY, capture.ScreenWidth, capture.ScreenHeight)
                        .Where(target => target.Name == original.Name && target.Role == original.Role).ToList();
                    if (matches.Count != 1)
                        return "[ACTION NOT SENT] The accessible target changed or is ambiguous. Capture again before clicking.";
                    (x, y) = (capture.ScreenX + matches[0].ImageX, capture.ScreenY + matches[0].ImageY);
                }

                switch (action.Type)
                {
                    case ComputerUseActionType.Move:
                        await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        return $"Moved pointer to image ({actionX},{actionY}) / screen ({x},{y}).";
                    case ComputerUseActionType.Click:
                        await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        ComputerUseNativeInput.LeftClickAt(x, y);
                        return useTarget
                            ? $"[TARGET CLICKED] The click was delivered to the centre of the accessible control '{resolvedTargetName}' ({requestedTargetId}) at image ({actionX},{actionY}). The control was hit; do not click it again to make sure. If its effect is not visible yet, continue with the next step."
                            : $"Clicked at image ({actionX},{actionY}) / screen ({x},{y}).";
                    case ComputerUseActionType.DoubleClick:
                        await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        ComputerUseNativeInput.DoubleClickAt(x, y);
                        return $"Double-clicked at image ({actionX},{actionY}) / screen ({x},{y}).";
                    case ComputerUseActionType.RightClick:
                        await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        ComputerUseNativeInput.RightClickAt(x, y);
                        return $"Right-clicked at image ({actionX},{actionY}) / screen ({x},{y}).";
                    case ComputerUseActionType.Drag:
                    {
                        int endImageX = Math.Clamp(action.X2, 0, Math.Max(0, capture.ImageWidth - 1));
                        int endImageY = Math.Clamp(action.Y2, 0, Math.Max(0, capture.ImageHeight - 1));
                        (int endX, int endY) = ComputerUseCoordinateMapper.MapToScreen(capture, endImageX, endImageY);
                        if (endX == x && endY == y)
                        {
                            // The same abstract rule, repeated, changed nothing: the model sent the
                            // identical zero-length drag three times running. A worked example built
                            // from its own point does — it treats that point as the centre it meant
                            // and shows the two corners a shape there would need.
                            int half = Math.Max(24, Math.Min(capture.ImageWidth, capture.ImageHeight) / 7);
                            int left = Math.Clamp(actionX - half, 0, Math.Max(0, capture.ImageWidth - 1));
                            int top = Math.Clamp(actionY - half, 0, Math.Max(0, capture.ImageHeight - 1));
                            int right = Math.Clamp(actionX + half, 0, Math.Max(0, capture.ImageWidth - 1));
                            int bottom = Math.Clamp(actionY + half, 0, Math.Max(0, capture.ImageHeight - 1));
                            return $"[ACTION NOT SENT] That drag starts and ends at the same point ({actionX}, {actionY}), so it would draw nothing. "
                                + "A drag presses at one point and releases at a DIFFERENT one; a shape tool draws the box between them. "
                                + $"To draw a shape centred on ({actionX}, {actionY}), drag corner to corner, for example "
                                + $"{{\"type\":\"drag\",\"x\":{left},\"y\":{top},\"x2\":{right},\"y2\":{bottom}}}. "
                                + "Pick the corners for the size you want.";
                        }

                        await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        await ComputerUseNativeInput.DragAsync(x, y, endX, endY,
                            PointerStep(pointer), token).ConfigureAwait(true);
                        return $"Dragged from image ({actionX},{actionY}) to ({endImageX},{endImageY}) / screen ({x},{y}) to ({endX},{endY}).";
                    }
                    case ComputerUseActionType.Scroll:
                        if (actionX != 0 || actionY != 0)
                            await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        ComputerUseNativeInput.Scroll(action.Dx, action.Dy == 0 ? -3 : action.Dy);
                        return $"Scrolled dx={action.Dx} dy={(action.Dy == 0 ? -3 : action.Dy)} at screen ({x},{y}).";
                    case ComputerUseActionType.Zoom:
                        if (actionX != 0 || actionY != 0)
                            await MovePointerAsync(pointer, x, y, token).ConfigureAwait(true);
                        ComputerUseNativeInput.Zoom(action.Dy == 0 ? 1 : action.Dy);
                        return $"Zoomed {(action.Dy < 0 ? "out" : "in")} at screen ({x},{y}).";
                    case ComputerUseActionType.Type:
                        await ComputerUseNativeInput.TypeTextAsync(action.Text, token).ConfigureAwait(true);
                        return $"Typed {action.Text.Length} character(s).";
                    case ComputerUseActionType.Key:
                        ComputerUseNativeInput.PressCombo(action.Keys);
                        return $"Pressed {action.Keys}.";
                    case ComputerUseActionType.Open:
                        string appName = string.IsNullOrWhiteSpace(action.Text) ? action.Keys : action.Text;
                        if (string.IsNullOrWhiteSpace(appName))
                            return "[ACTION NOT SENT] Missing app name. Resend type=open with text set to the app.";
                        await ComputerUseNativeInput.OpenAppAsync(appName, token).ConfigureAwait(true);
                        return $"Requested launch of \"{appName}\" through Search; foreground verification is pending.";
                    case ComputerUseActionType.Wait:
                        await Task.Delay(Math.Clamp(action.Ms <= 0 ? 400 : action.Ms, 50, 4000), token).ConfigureAwait(true);
                        return $"Waited {Math.Clamp(action.Ms <= 0 ? 400 : action.Ms, 50, 4000)} ms.";
                    default:
                        return "Screenshot taken.";
                }
            }
            finally
            {
                if (pointerWasVisible)
                    pointer.Show();
                if (hudWasVisible)
                    hud.Show();
            }
        }

        // Just the "action" object from a model reply, for the log. The whole reply is long, and the
        // shape of this one object is what decides how a drag is read.
        private static string ExtractActionJson(string raw)
        {
            int start = raw.IndexOf("\"action\"", StringComparison.Ordinal);
            if (start < 0)
                return raw;
            int brace = raw.IndexOf('{', start);
            if (brace < 0)
                return raw[start..];

            int depth = 0;
            for (int i = brace; i < raw.Length; i++)
            {
                if (raw[i] == '{') depth++;
                else if (raw[i] == '}' && --depth == 0)
                    return raw[brace..(i + 1)];
            }

            return raw[brace..];
        }

        private static string Clip(string? text, int max)
        {
            string value = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return value.Length <= max ? value : value[..max] + "…";
        }

        private static int GetPostActionSettleDelay(ComputerUseAction action)
            => action.Type switch
            {
                ComputerUseActionType.Open => 1100,
                ComputerUseActionType.Click or ComputerUseActionType.DoubleClick or ComputerUseActionType.Key or ComputerUseActionType.Type => 500,
                ComputerUseActionType.Scroll or ComputerUseActionType.Zoom => 350,
                _ => 250
            };

        // How long a screen may keep changing after an action before the next screenshot is taken
        // anyway. A site that animates forever must not stall the session, and a site that takes a
        // couple of seconds to render must not be judged half-drawn.
        private static readonly TimeSpan MaxSettleWait = TimeSpan.FromSeconds(6);
        private static readonly TimeSpan SettlePollInterval = TimeSpan.FromMilliseconds(400);

        /// <summary>
        /// Waits until the screen stops changing, or the budget runs out.
        /// </summary>
        /// <remarks>
        /// The fixed delay above is only a floor: it is what a local control needs to repaint, not
        /// what a page needs to load. Navigation, search and app launches all finish on their own
        /// schedule, and screenshotting at a fixed moment after the click means the model is often
        /// shown a blank or half-rendered page and reasons about that instead of the result. This
        /// is deliberately generic — it watches pixels, so it applies to any application, not just
        /// a browser.
        /// </remarks>
        private static async Task<int> WaitForScreenToSettleAsync(
            int visionDetailLevel,
            ComputerUseSessionWindow hud,
            ComputerUsePointerWindow pointer,
            CancellationToken token)
        {
            var deadline = DateTime.UtcNow + MaxSettleWait;
            byte[] previous = ComputerUseScreenCapture.CaptureDesktop(visionDetailLevel, hud, pointer).JpegBytes;
            int stableChecks = 0;
            int polls = 0;

            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(SettlePollInterval, token).ConfigureAwait(true);
                byte[] current = ComputerUseScreenCapture.CaptureDesktop(visionDetailLevel, hud, pointer).JpegBytes;
                polls++;
                if (!ComputerUseObservationEvidence.HasVisualChange(previous, current))
                {
                    // Two consecutive quiet samples, so this is a settled screen rather than the
                    // gap between two frames of something still loading.
                    if (++stableChecks >= 2)
                        break;
                }
                else
                {
                    stableChecks = 0;
                }

                previous = current;
            }

            return polls;
        }

        /// <summary>
        /// A per-step callback that moves the pointer overlay from any thread.
        /// </summary>
        /// <remarks>
        /// Input movement runs its waits with ConfigureAwait(false), so the steps arrive on a
        /// thread-pool thread — and the overlay is a WPF window, which may only be touched on the
        /// thread that owns it. Every caller needs exactly this marshalling, so it lives here
        /// rather than being retyped at each call site and silently omitted at one of them.
        /// </remarks>
        private static Action<int, int> PointerStep(ComputerUsePointerWindow pointer)
            => (x, y) => pointer.Dispatcher.Invoke(() => pointer.PlaceAtScreen(x, y));

        private static async Task MovePointerAsync(ComputerUsePointerWindow pointer, int screenX, int screenY, CancellationToken token)
        {
            var current = ComputerUseNativeInput.GetCursor();
            await ComputerUseNativeInput.SmoothMoveAsync(
                current.X,
                current.Y,
                screenX,
                screenY,
                PointerStep(pointer),
                token).ConfigureAwait(true);
        }

    }
}
