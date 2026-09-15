using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseExecutionLedgerTests
    {
        [Fact]
        public void FailedAction_RequiresRecoveryAndBlocksCompletion()
        {
            var ledger = new ComputerUseExecutionLedger();
            ledger.BeginAction(new ComputerUseAction
            {
                Type = ComputerUseActionType.Click,
                X = 100,
                Y = 200,
                ExpectedState = "The Save dialog opens"
            });

            string observation = ledger.ReconcileObservation(visibleChange: false);

            Assert.Equal(ComputerUseActionEvidenceState.Failed, ledger.State);
            Assert.True(ledger.RequiresRecovery);
            Assert.False(ledger.CanFinish);
            Assert.Contains("different recovery action", observation);
        }

        [Fact]
        public void RecoveryAction_ReplacesFailureOnlyAfterItsOwnObservation()
        {
            var ledger = new ComputerUseExecutionLedger();
            ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Click, ExpectedState = "Menu opens" });
            ledger.ReconcileObservation(visibleChange: false);

            ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Key, Keys = "Alt+F", ExpectedState = "File menu opens" });

            Assert.Equal(ComputerUseActionEvidenceState.Pending, ledger.State);
            Assert.False(ledger.CanFinish);

            ledger.ReconcileObservation(visibleChange: true);

            Assert.Equal(ComputerUseActionEvidenceState.ObservedChange, ledger.State);
            Assert.False(ledger.CanFinish);
            ledger.AssessExpectedState(true, "File menu items are visible.");
            Assert.True(ledger.CanFinish);
            Assert.Contains("File menu opens", ledger.Describe());
        }

        [Fact]
        public void ImmediateExpectedState_OverridesADeferredGoalClaim()
        {
            var ledger = new ComputerUseExecutionLedger();
            ledger.BeginAction(
                new ComputerUseAction
                {
                    Type = ComputerUseActionType.Type,
                    ExpectedState = "The final document is saved"
                },
                "The entered text is visible in the focused editor.");

            Assert.Contains("focused editor", ledger.Describe());
            Assert.DoesNotContain("final document is saved", ledger.Describe(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
