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
        }

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
            if (ComputerUseApplicationVerification.IsRequestedApplicationVisible(appName, capture))
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
            pending = null;
            lastActionProducedVisualEvidence = changed;
            string screenshotObservation = changed
                ? $"[POST-ACTION SCREENSHOT] The screen changed after {actionLabel}. Inspect this fresh screenshot for the expected state: {expectedState}."
                : $"[POST-ACTION VERIFICATION FAILED] The screenshot is unchanged after {actionLabel}. Expected visible state: {expectedState}. Treat the action as not achieved and recover without repeating blindly.";
            string ledgerObservation = executionLedger.ReconcileObservation(changed);
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
