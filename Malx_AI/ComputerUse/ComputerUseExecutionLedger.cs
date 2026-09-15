using System;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// Controller-owned evidence for the currently executing UI stage.  This is deliberately
    /// application-agnostic: browser telemetry can add stronger checks, but every desktop action
    /// still has the same observe -> act -> verify -> recover lifecycle.
    /// </summary>
    internal sealed class ComputerUseExecutionLedger
    {
        private string _actionLabel = string.Empty;
        private string _expectedState = string.Empty;

        public ComputerUseActionEvidenceState State { get; private set; } = ComputerUseActionEvidenceState.None;
        public string ActionLabel => _actionLabel;
        public string ExpectedState => _expectedState;
        public bool RequiresRecovery => State == ComputerUseActionEvidenceState.Failed;
        public bool IsPending => State == ComputerUseActionEvidenceState.Pending;
        public bool CanFinish => State is ComputerUseActionEvidenceState.None or ComputerUseActionEvidenceState.Verified;

        public void AssessExpectedState(bool succeeded, string evidence)
        {
            if (State == ComputerUseActionEvidenceState.None)
                return;
            State = succeeded && !string.IsNullOrWhiteSpace(evidence)
                ? ComputerUseActionEvidenceState.Verified
                : ComputerUseActionEvidenceState.Failed;
        }

        public void BeginAction(ComputerUseAction action, string? immediateExpectedState = null)
        {
            if (action == null)
                throw new ArgumentNullException(nameof(action));

            _actionLabel = action.ShortLabel;
            _expectedState = string.IsNullOrWhiteSpace(immediateExpectedState)
                ? (string.IsNullOrWhiteSpace(action.ExpectedState)
                ? "a visible result from this action"
                : action.ExpectedState.Trim())
                : immediateExpectedState.Trim();
            State = ComputerUseActionEvidenceState.Pending;
        }

        public string ReconcileObservation(bool visibleChange)
        {
            if (!IsPending)
                return string.Empty;

            State = visibleChange
                ? ComputerUseActionEvidenceState.ObservedChange
                : ComputerUseActionEvidenceState.Failed;

            return visibleChange
                ? $"[EXECUTION LEDGER] Observed visible change after {_actionLabel}. Confirm the expected state before advancing: {_expectedState}."
                : $"[EXECUTION LEDGER] {_actionLabel} did not produce visible evidence for: {_expectedState}. This stage is failed; choose a different recovery action.";
        }

        public string Describe()
        {
            if (State == ComputerUseActionEvidenceState.None)
                return "No action is awaiting verification.";

            string state = State switch
            {
                ComputerUseActionEvidenceState.Pending => "pending fresh observation",
                ComputerUseActionEvidenceState.ObservedChange => "visible change observed; semantic state still must be checked",
                ComputerUseActionEvidenceState.Verified => "expected state supported by a separate visual assessment or controller telemetry",
                ComputerUseActionEvidenceState.Failed => "failed; recovery action required",
                _ => "unknown"
            };
            return $"Last action: {_actionLabel}\nExpected state: {_expectedState}\nEvidence state: {state}";
        }
    }

    internal enum ComputerUseActionEvidenceState
    {
        None,
        Pending,
        ObservedChange,
        Verified,
        Failed
    }
}
