using System;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI.ComputerUse
{
    internal static partial class ComputerUseSessionController
    {
        internal sealed class PendingActionEvidence
        {
            public required byte[] BeforeScreenshot { get; init; }
            public required string ActionLabel { get; init; }
            public required string ExpectedState { get; init; }

            /// <summary>What the controller itself reported when it carried the action out.</summary>
            public string ControllerResult { get; init; } = "";
        }

        /// <summary>How one executed action turned out, judged from evidence in order of trust.</summary>
        internal enum StepVerdict
        {
            /// <summary>It worked: the controller measured it, or the observer saw it.</summary>
            Confirmed,

            /// <summary>It was sent and the screen reacted, but its exact effect cannot be seen.</summary>
            Delivered,

            /// <summary>Nothing happened at all.</summary>
            NoEffect
        }

        internal const string TargetClickedTag = "[TARGET CLICKED]";
        internal const string DragVerifiedTag = "[DRAG VERIFIED]";
        internal const string DragNoEffectTag = "[DRAG NO EFFECT]";

        /// <summary>True when the controller's own result proves the action landed.</summary>
        internal static bool IsControllerConfirmed(string? controllerResult)
        {
            string text = controllerResult ?? string.Empty;
            return text.StartsWith(TargetClickedTag, StringComparison.Ordinal)
                || text.StartsWith(DragVerifiedTag, StringComparison.Ordinal);
        }

        /// <summary>
        /// Decides whether an executed action counts as progress.
        /// </summary>
        /// <remarks>
        /// The logged Paint run ended with "six actions without a verified effect" although every
        /// click had been delivered to the right control. The only judge was an observer model
        /// looking at a downscaled JPEG, which cannot see a toolbar button's faint selected tint
        /// and so reported each tool click as failed; told that, the model alternated between two
        /// tools until the session was stopped. Evidence is therefore ranked: what the controller
        /// measured outranks what an observer could or could not make out, and a delivered action
        /// that changed the screen is progress even when nobody can name the change.
        /// </remarks>
        internal static StepVerdict JudgeStep(string? controllerResult, bool screenChanged, bool observerConfirmed)
        {
            string text = controllerResult ?? string.Empty;
            if (text.StartsWith(DragNoEffectTag, StringComparison.Ordinal))
                return StepVerdict.NoEffect;
            if (IsControllerConfirmed(text) || observerConfirmed)
                return StepVerdict.Confirmed;
            return screenChanged ? StepVerdict.Delivered : StepVerdict.NoEffect;
        }

        internal static string DescribeStepVerdict(StepVerdict verdict, string actionLabel) => verdict switch
        {
            StepVerdict.Confirmed =>
                $"[STEP OK] {actionLabel} worked. It is done — do not repeat it. Do the next missing step.",
            StepVerdict.Delivered =>
                $"[STEP DELIVERED] {actionLabel} was sent and the screen reacted. Its exact effect is too small to confirm at screenshot resolution, which is normal for a tool or option highlight. Treat it as done and do the NEXT step; do not repeat it or switch to a different tool because of this.",
            _ =>
                $"[STEP HAD NO EFFECT] {actionLabel} changed nothing on screen. Do not send it again unchanged: aim at a different point, use a listed target_id, or use the keyboard."
        };

        /// <summary>One line of the per-outcome work log shown to the model.</summary>
        internal static string VerdictWord(StepVerdict verdict) => verdict switch
        {
            StepVerdict.Confirmed => "ok",
            StepVerdict.Delivered => "delivered",
            _ => "no effect"
        };

        /// <summary>Where a pointer action will land, and which control (if any) that is.</summary>
        internal sealed record PointerAim(int ImageX, int ImageY, ComputerUseUiTarget? Target, bool Named, string Note);

        // Controls small enough that a click anywhere on them means "this control", so aiming at
        // the exact centre is strictly better than the model's estimate. A text box or list row
        // is excluded: where inside it the click lands can matter.
        private static bool IsSnapControl(ComputerUseUiTarget target)
            => target.Role is "Button" or "RadioButton" or "CheckBox" or "TabItem" or "MenuItem" or "Hyperlink"
                && target.ImageWidth is > 0 and <= 160
                && target.ImageHeight is > 0 and <= 90;

        private static ComputerUseUiTarget? FindControlAt(ComputerUseCapture capture, int imageX, int imageY)
            => capture.UiTargets
                .Where(target => IsSnapControl(target) && target.Contains(imageX, imageY, margin: 0))
                .OrderBy(target => target.ImageWidth * target.ImageHeight)
                .FirstOrDefault();

        /// <summary>
        /// Reconciles the coordinates and the target id a pointer action carries.
        /// </summary>
        /// <remarks>
        /// Models copy every field of the schema into every action, so a click meant for one
        /// button routinely arrives still carrying the id of the button clicked before it. The id
        /// used to win unconditionally: a click at (345,74) on "Brushes" was silently delivered to
        /// "Pencil" at (223,74), reported as a Pencil click, and then judged by an observer that
        /// had been told it landed at (345,74). The rule now is that the two must agree. When
        /// they do not, the coordinates — which the model computed for THIS action — are what it
        /// meant, the stale id is dropped, and the result says so.
        /// </remarks>
        internal static bool TryResolvePointerAim(
            ComputerUseAction action,
            ComputerUseCapture capture,
            out PointerAim aim,
            out string refusal)
        {
            refusal = "";
            string requestedId = (action.TargetId ?? string.Empty).Trim();
            bool hasCoordinates = action.X > 0 || action.Y > 0;
            ComputerUseUiTarget? named = requestedId.Length == 0
                ? null
                : capture.UiTargets.FirstOrDefault(target => string.Equals(target.Id, requestedId, StringComparison.OrdinalIgnoreCase));

            if (requestedId.Length > 0 && named == null)
            {
                // An id in the issued format that is no longer listed is genuinely stale: the
                // control was there and has gone. Any other shape was never issued — the model
                // named the control it had in mind — so its coordinates are still usable.
                if (ComputerUseTargetId.IsIssued(requestedId) || !hasCoordinates)
                {
                    aim = new PointerAim(action.X, action.Y, null, false, "");
                    refusal = $"[ACTION NOT SENT] Target id '{requestedId}' is stale or unavailable. Inspect the fresh screenshot and choose a current target or coordinates.";
                    return false;
                }
            }

            if (named != null && (!hasCoordinates || named.Contains(action.X, action.Y, margin: 10)))
            {
                aim = new PointerAim(named.ImageX, named.ImageY, named, true, "");
                return true;
            }

            string note = named == null
                ? ""
                : $" Note: target_id {named.Id} is '{named.Name}' at ({named.ImageX},{named.ImageY}), which is not where ({action.X},{action.Y}) is, so that id was ignored and the coordinates were used. Send target_id alone to click a listed control.";

            ComputerUseUiTarget? under = FindControlAt(capture, action.X, action.Y);
            if (under != null)
            {
                aim = new PointerAim(under.ImageX, under.ImageY, under, false, note);
                return true;
            }

            aim = new PointerAim(action.X, action.Y, null, false, note);
            return true;
        }

        /// <summary>
        /// The live control to click for a target chosen from an earlier capture: the same name
        /// and role, and — when several share them — the one that has not moved.
        /// </summary>
        internal static ComputerUseUiTarget? PickLiveTarget(
            IReadOnlyList<ComputerUseUiTarget> sameNameAndRole,
            int expectedX,
            int expectedY,
            int tolerance = 48)
        {
            if (sameNameAndRole.Count == 1)
                return sameNameAndRole[0];

            return sameNameAndRole
                .Select(target => (Target: target, Distance: Math.Max(Math.Abs(target.ImageX - expectedX), Math.Abs(target.ImageY - expectedY))))
                .Where(candidate => candidate.Distance <= tolerance)
                .OrderBy(candidate => candidate.Distance)
                .Select(candidate => candidate.Target)
                .FirstOrDefault();
        }

        /// <summary>
        /// Whether a click would only re-select what the application already reports as selected.
        /// </summary>
        internal static bool IsRedundantSelection(ComputerUseAction action, PointerAim aim)
            => action.Type == ComputerUseActionType.Click
                && aim.Target is { State: "selected" };

        internal static ComputerUseTaskProgress MergeTaskProgress(ComputerUseTaskProgress prior, ComputerUseTaskProgress update)
        {
            if (prior.PlanInitialized || string.IsNullOrWhiteSpace(update.Current))
                return prior;

            return new ComputerUseTaskProgress
            {
                // The model may describe a proposed completion, but only a controller-side
                // observation may add a permanent completed item.
                Completed = prior.Completed,
                Current = update.Current.Trim(),
                Next = update.Next.Trim(),
                Remaining = update.Remaining,
                PlanInitialized = true
            };
        }

        internal static void TrackBrowserIntent(
            ComputerUseAction action,
            ComputerUseCapture capture,
            ref int? pendingNewTabCount,
            ref string? typedBrowserUrl,
            ref string? pendingBrowserUrl,
            bool addressEntryExpected = false,
            int? lastKnownTabCount = null)
        {
            ComputerUseBrowserState? state = capture.BrowserState;
            if (IsNewTabAction(action, capture))
            {
                // Prefer this capture's count, then the most recent one seen this session. Tab
                // telemetry is momentarily absent often enough (window not yet foreground, UIA
                // tree still building) that falling straight to "no baseline" strands the check.
                // -1 still means attempted without a baseline, not zero existing tabs.
                pendingNewTabCount ??= state?.HasTabTelemetry == true
                    ? state.TabCount
                    : lastKnownTabCount ?? -1;
            }

            if (action.Type == ComputerUseActionType.Type
                && (addressEntryExpected || state?.AddressHasFocus == true)
                && !string.IsNullOrWhiteSpace(action.Text))
            {
                typedBrowserUrl = action.Text.Trim();
            }

            if (action.Type == ComputerUseActionType.Key
                && string.Equals(action.Keys?.Trim(), "Enter", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(typedBrowserUrl))
            {
                pendingBrowserUrl = ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(typedBrowserUrl, out string url)
                    ? url : null;
                typedBrowserUrl = null;
            }
        }

        internal static bool TryLookLikeNavigationAddress(string? text)
        {
            string value = (text ?? string.Empty).Trim();
            if (value.Length == 0)
                return false;
            if (ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl(value, out _))
                return true;
            return !value.Any(char.IsWhiteSpace)
                && value.Split('/')[0].Contains('.', StringComparison.Ordinal)
                && ComputerUseBrowserVerification.TryGetAbsoluteHttpUrl("https://" + value, out _);
        }

        internal static bool UpdateAddressEntryIntent(bool prior, ComputerUseAction action, ComputerUseCapture capture)
        {
            if (IsNewTabAction(action, capture)) return true;
            if (action.Type == ComputerUseActionType.Key)
            {
                string keys = action.Keys.Replace(" ", "").ToUpperInvariant();
                if (keys is "CTRL+L" or "ALT+D") return true;
                if (keys is "ENTER" or "ESC" or "ESCAPE" or "TAB" or "ALT+TAB") return false;
            }
            if (action.Type is ComputerUseActionType.Open or ComputerUseActionType.Click
                or ComputerUseActionType.DoubleClick or ComputerUseActionType.RightClick) return false;
            return prior;
        }

        internal static void TrackApplicationLaunch(ComputerUseAction action, ref string? pendingApplicationLaunch)
        {
            if (action.Type != ComputerUseActionType.Open)
                return;

            string appName = FirstNonEmpty(action.Text, action.Keys).Trim();
            if (!string.IsNullOrWhiteSpace(appName))
                pendingApplicationLaunch = appName;
        }

        internal static string ReconcileApplicationLaunch(
            ComputerUseCapture capture,
            ref string? pendingApplicationLaunch,
            ref bool applicationLaunchRecoveryRequired,
            ref ComputerUseTaskProgress taskProgress)
        {
            if (string.IsNullOrWhiteSpace(pendingApplicationLaunch))
                return string.Empty;

            string appName = pendingApplicationLaunch;
            if (ComputerUseApplicationVerification.IsRequestedApplicationInFront(appName, capture))
            {
                pendingApplicationLaunch = null;
                applicationLaunchRecoveryRequired = false;
                taskProgress = AddVerifiedCompletion(taskProgress, "Opened " + appName + ".");
                return $"[APP LAUNCH VERIFIED] {appName} is the foreground window ({capture.ForegroundProcessName}: {capture.ForegroundWindowTitle}).";
            }

            applicationLaunchRecoveryRequired = true;
            string process = FirstNonEmpty(capture.ForegroundProcessName, "unknown");
            string title = FirstNonEmpty(capture.ForegroundWindowTitle, "unknown");
            return $"[APP LAUNCH VERIFICATION FAILED] {appName} is not visible in the foreground. Observed window: {process}: {title}. Keep this stage unfinished; do not type into the desktop or claim the app opened.";
        }

        internal static bool IsNewTabAction(ComputerUseAction action, ComputerUseCapture capture)
        {
            if (action.Type == ComputerUseActionType.Key
                && string.Equals(action.Keys?.Trim(), "Ctrl+T", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (action.Type is not (ComputerUseActionType.Click or ComputerUseActionType.DoubleClick)
                || string.IsNullOrWhiteSpace(action.TargetId))
            {
                return false;
            }

            ComputerUseUiTarget? target = capture.UiTargets.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, action.TargetId.Trim(), StringComparison.OrdinalIgnoreCase));
            return target != null && string.Equals(target.Name.Trim(), "New tab", StringComparison.OrdinalIgnoreCase);
        }

        internal static string ReconcileBrowserState(
            ComputerUseCapture capture,
            ref int? pendingNewTabCount,
            ref string? pendingBrowserUrl,
            ref bool separateTabRecoveryRequired,
            ref ComputerUseTaskProgress taskProgress,
            ComputerUseTaskContract? taskContract = null)
        {
            var observations = new List<string>();
            ComputerUseBrowserState? state = capture.BrowserState;
            // Only an outcome that actually REQUIRES a separate tab may lock navigation down. A new
            // tab the model opened on its own initiative is not a task requirement, and treating a
            // failure to verify one as a hard barrier blocks every other action -- which is how a
            // run whose plan contained no separate-tab outcome at all still ended in tab recovery.
            bool tabRequiredByContract = taskContract?.Current?.SeparateTab == true;
            if (state?.IsErrorPage == true)
                observations.Add("[BROWSER ERROR DETECTED] The visible browser page is an error/404. It is not a completed destination.");

            if (pendingNewTabCount.HasValue)
            {
                if (ComputerUseBrowserVerification.IsNewTabConfirmed(pendingNewTabCount.Value, state))
                {
                    taskProgress = AddVerifiedCompletion(taskProgress, "Opened a separate browser tab.");
                    separateTabRecoveryRequired = false;
                    pendingNewTabCount = null;
                    observations.Add($"[BROWSER VERIFIED] New tab count is {state!.TabCount}.");
                }
                else if (ComputerUseBrowserVerification.IsNewTabBaselineUnknown(pendingNewTabCount.Value)
                    && state?.HasTabTelemetry == true)
                {
                    // No "before" count was ever recorded, so a delta can neither prove nor
                    // disprove the new tab. Calling that a failure is false AND self-amplifying:
                    // it turns on the separate-tab recovery path, whose only permitted action is
                    // opening another tab, which lands here again. Resolve it as unproven and let
                    // the contract's own tab evidence and the visual assessment decide.
                    separateTabRecoveryRequired = false;
                    pendingNewTabCount = null;
                    observations.Add($"[TAB OBSERVATION INCONCLUSIVE] The tab count before this action was not observed, so a new tab cannot be confirmed by count; {state!.TabCount} tab(s) are open now. Judge from the screenshot and the tab strip. Do not repeat the new-tab action just to satisfy this check.");
                }
                else if (state?.HasTabTelemetry == true)
                {
                    // The baseline is kept either way: tab telemetry can lag a step, and a delayed
                    // observation should still be able to confirm the tab. Only the lock-down is
                    // conditional.
                    separateTabRecoveryRequired = tabRequiredByContract;
                    observations.Add(tabRequiredByContract
                        ? "[TAB VERIFICATION FAILED] Ctrl+T did not produce an observed additional tab. Keep this goal unfinished; ensure the browser has focus before trying a different recovery."
                        : "[TAB NOT OBSERVED] No additional tab was counted after that action, and no current outcome requires a separate tab. Continue with the current outcome; do not keep opening tabs.");
                }
                else
                {
                    // Absence of telemetry is not evidence of failure, and must never lock the
                    // session down -- the previous message said as much while doing exactly that.
                    separateTabRecoveryRequired = false;
                    pendingNewTabCount = null;
                    observations.Add("[TAB OBSERVATION UNAVAILABLE] The browser did not expose tab-count telemetry. Inspect the fresh screenshot before deciding whether Ctrl+T worked; do not treat this as a failed tab action.");
                }
            }

            if (!string.IsNullOrWhiteSpace(pendingBrowserUrl))
            {
                if (ComputerUseBrowserVerification.IsNavigationConfirmed(pendingBrowserUrl, state, out string reason)
                    || taskContract?.ConfirmsNavigation(pendingBrowserUrl, capture) == true)
                {
                    taskProgress = AddVerifiedCompletion(taskProgress, "Opened " + pendingBrowserUrl);
                    observations.Add("[BROWSER VERIFIED] Loaded destination satisfies the navigation requirement and has no detected error page: " + state!.DocumentAddress);
                    pendingBrowserUrl = null;
                }
                else if (state?.IsAvailable == true)
                {
                    observations.Add("[NAVIGATION VERIFICATION PENDING] " + reason + " Do not mark this destination complete.");
                }
            }

            return string.Join("\n", observations);
        }

        internal static ComputerUseTaskProgress AddVerifiedCompletion(
            ComputerUseTaskProgress progress,
            string completion,
            bool advanceCurrentGoal = false)
        {
            var completed = new List<string>(progress.Completed);
            if (!completed.Contains(completion, StringComparer.OrdinalIgnoreCase))
                completed.Add(completion);

            return new ComputerUseTaskProgress
            {
                Completed = completed,
                Current = advanceCurrentGoal ? progress.Next : progress.Current,
                Next = advanceCurrentGoal ? progress.Remaining.FirstOrDefault() ?? "" : progress.Next,
                Remaining = advanceCurrentGoal ? progress.Remaining.Skip(1).ToList() : progress.Remaining,
                PlanInitialized = progress.PlanInitialized
            };
        }

        internal static bool HasVerifiedNavigationContext(string navigationContext, ComputerUseTaskProgress progress)
        {
            return ComputerUseTrustedNavigation.TryGetVerifiedUrl(navigationContext, out string url)
                && progress.Completed.Any(item => item.StartsWith("Opened ", StringComparison.Ordinal)
                    && ComputerUseBrowserVerification.AreEquivalentUrls(url, item[7..]));
        }

        internal static bool IsRepeatSensitiveAction(ComputerUseAction action)
            => action.Type is ComputerUseActionType.Click
                or ComputerUseActionType.DoubleClick
                or ComputerUseActionType.RightClick
                or ComputerUseActionType.Type;

        // Deliberately NOT repeat-sensitive: drawing is repetitive by nature. Several strokes from
        // the same start point are how a shape gets built, and blocking the second one as a
        // "repeated missed target" would make drawing impossible for the opposite reason clicking
        // already was.


        internal static bool IsBlockedWhileRecoveringSeparateTab(ComputerUseAction action, ComputerUseCapture capture)
        {
            if (action.Type == ComputerUseActionType.Key)
                return !string.Equals(action.Keys?.Trim(), "Ctrl+T", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(action.Keys?.Trim(), "Alt+Tab", StringComparison.OrdinalIgnoreCase);

            if (action.Type is ComputerUseActionType.Click or ComputerUseActionType.DoubleClick)
                return !IsNewTabAction(action, capture);

            return action.Type is not (ComputerUseActionType.Screenshot or ComputerUseActionType.Wait or ComputerUseActionType.Move or ComputerUseActionType.Open);
        }

        internal static bool IsBlockedWhileRecoveringApplicationLaunch(ComputerUseAction action)
            => action.Type is ComputerUseActionType.Type or ComputerUseActionType.Key;

        internal static bool IsBlockedBeforeAddressCommit(ComputerUseAction action)
        {
            if (action.Type == ComputerUseActionType.Type)
                return true;

            if (action.Type != ComputerUseActionType.Key)
                return false;

            return !string.Equals(action.Keys?.Trim(), "Enter", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Whether this action aims at a control, and so may resolve an accessible target id.
        /// </summary>
        /// <remarks>
        /// A keystroke has nowhere to land. Treating a target id on one as binding let a stray
        /// "target_id" field veto Ctrl+T outright, which ended a session with no input ever sent.
        /// </remarks>
        internal static bool RequiresPointerTarget(ComputerUseAction action)
            => action.Type is ComputerUseActionType.Click
                or ComputerUseActionType.DoubleClick
                or ComputerUseActionType.RightClick
                or ComputerUseActionType.Move;

        internal static bool RequiresVisualEvidence(ComputerUseAction action)
            => action.Type is ComputerUseActionType.Click
                or ComputerUseActionType.DoubleClick
                or ComputerUseActionType.RightClick
                or ComputerUseActionType.Drag
                or ComputerUseActionType.Type
                or ComputerUseActionType.Key
                or ComputerUseActionType.Open
                or ComputerUseActionType.Scroll
                or ComputerUseActionType.Zoom;

        internal static string GetImmediateExpectedState(ComputerUseAction action)
        {
            if (action.Type == ComputerUseActionType.Type)
                return "The entered text is visible in the already focused control.";

            if (action.Type == ComputerUseActionType.Key)
                return string.IsNullOrWhiteSpace(action.Keys)
                    ? "The keyboard action has a visible effect."
                    : $"The {action.Keys.Trim()} shortcut has a visible effect.";

            return FirstNonEmpty(action.ExpectedState, "a visible result from this action");
        }

        internal static string ReconcileActionEvidence(
            ComputerUseCapture capture,
            ref PendingActionEvidence? pending,
            ComputerUseExecutionLedger executionLedger,
            ref bool lastActionProducedVisualEvidence)
        {
            if (pending == null)
                return string.Empty;

            bool changed = ComputerUseObservationEvidence.HasVisualChange(pending.BeforeScreenshot, capture.JpegBytes);
            string actionLabel = pending.ActionLabel;
            string expectedState = pending.ExpectedState;
            bool controllerConfirmed = IsControllerConfirmed(pending.ControllerResult);
            pending = null;
            lastActionProducedVisualEvidence = changed;
            // An action the controller itself confirmed is not undone by a screenshot that looks
            // the same: a tool highlight or a thin stroke can vanish in a downscaled JPEG.
            string screenshotObservation = changed
                ? $"[POST-ACTION SCREENSHOT] The screen changed after {actionLabel}. Inspect this fresh screenshot for the expected state: {expectedState}."
                : controllerConfirmed
                    ? $"[POST-ACTION SCREENSHOT] {actionLabel} was confirmed by the controller. The screenshot looks the same at this resolution, which does not undo it."
                    : $"[POST-ACTION VERIFICATION FAILED] The screenshot is unchanged after {actionLabel}. Expected visible state: {expectedState}. Treat the action as not achieved and recover without repeating blindly.";
            string ledgerObservation = executionLedger.ReconcileObservation(changed || controllerConfirmed);
            return string.IsNullOrWhiteSpace(ledgerObservation)
                ? screenshotObservation
                : screenshotObservation + "\n" + ledgerObservation;
        }

        internal static string FirstNonEmpty(params string[] values)
        {
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }
    }
}
