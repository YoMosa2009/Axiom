using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// "Open the paint app and draw me a smiley face" finally drew the circle, then ended with
/// "stopped after repeating the same missed target". The clicks were not missing: the model was
/// selecting the Oval tool, whose only visible effect is a small toolbar highlight the observer
/// could not certify at screenshot scale. A successful click read as a miss, the model re-clicked
/// the same correct control, and three of those killed the run.
/// </summary>
public class ComputerUseRepeatGuardTests
{
    private static readonly byte[] Frame = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] SameFrame = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] ChangedFrame = [1, 2, 3, 4, 5, 6, 7, 9];

    private static ComputerUseCapture Capture(byte[] jpeg) => new() { JpegBytes = jpeg };

    [Fact]
    public void AScreenThatChangedIsReportedAsChanged()
    {
        // The controller's own measurement, independent of any model's opinion of it.
        Assert.True(ComputerUseObservationEvidence.HasVisualChange(Frame, ChangedFrame));
        Assert.False(ComputerUseObservationEvidence.HasVisualChange(Frame, SameFrame));
    }

    [Fact]
    public void ASuccessfulClickIsRecordedAsHavingChangedTheScreen()
    {
        // The path that feeds the stuck-pointer guard. A click that altered the screen — even by a
        // toolbar highlight too small for an observer to name — must come back as movement.
        var pending = new ComputerUseSessionController.PendingActionEvidence
        {
            BeforeScreenshot = Frame,
            ActionLabel = "Click left at (567, 163)",
            ExpectedState = "The Oval tool is selected."
        };
        var ledger = new ComputerUseExecutionLedger();
        ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Click, X = 567, Y = 163 }, "The Oval tool is selected.");

        bool screenChanged = false;
        ComputerUseSessionController.ReconcileActionEvidence(
            Capture(ChangedFrame), ref pending, ledger, ref screenChanged);

        Assert.True(screenChanged);
    }

    [Fact]
    public void AClickThatChangedNothingIsRecordedAsAMiss()
    {
        // The guard still has to catch a pointer landing on dead space, which is what it is for.
        var pending = new ComputerUseSessionController.PendingActionEvidence
        {
            BeforeScreenshot = Frame,
            ActionLabel = "Click left at (5, 5)",
            ExpectedState = "A menu opens."
        };
        var ledger = new ComputerUseExecutionLedger();
        ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Click, X = 5, Y = 5 }, "A menu opens.");

        bool screenChanged = true;
        string observation = ComputerUseSessionController.ReconcileActionEvidence(
            Capture(SameFrame), ref pending, ledger, ref screenChanged);

        Assert.False(screenChanged);
        Assert.Contains("[POST-ACTION VERIFICATION FAILED]", observation);
    }

    [Fact]
    public void RepeatingAnActionOnTheSameTargetIsStillDetected()
    {
        // The detection itself is unchanged; only what counts as "missed" moved from the observer's
        // opinion to the controller's own measurement.
        var first = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 567, Y = 163, TargetId = "ui1" };
        var again = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 567, Y = 163, TargetId = "ui1" };
        var elsewhere = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 900, Y = 700, TargetId = "ui4" };

        Assert.True(ComputerUseCoordinateMapper.IsSameTarget(first, again));
        Assert.False(ComputerUseCoordinateMapper.IsSameTarget(first, elsewhere));
    }

    [Fact]
    public void ADragIsNeverCountedAsARepeatedMiss()
    {
        // Drawing is repetitive by nature: strokes from one point are how a shape is built.
        var stroke = new ComputerUseAction { Type = ComputerUseActionType.Drag, X = 400, Y = 400, X2 = 800, Y2 = 800 };
        Assert.False(ComputerUseSessionController.IsRepeatSensitiveAction(stroke));
    }
}
