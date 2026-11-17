using System;
using System.Collections.Generic;
using System.Linq;
using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Pins the fixes for the logged "Open the paint app and draw a person" run, which opened Paint
/// and then spent nine steps alternating between two tool buttons before being stopped with
/// "six actions without a verified effect" and a blank canvas.
/// </summary>
public class ComputerUsePaintRunTests
{
    private static readonly byte[] Frame = [1, 2, 3, 4];

    // The toolbar as that run saw it: Pencil at (223,74), Brushes at (345,74), Oval at (433,72).
    private static ComputerUseCapture PaintCapture(params ComputerUseUiTarget[] extra) => new()
    {
        JpegBytes = Frame,
        ImageWidth = 1280,
        ImageHeight = 853,
        ScreenWidth = 2256,
        ScreenHeight = 1504,
        ForegroundProcessName = "mspaint",
        ForegroundWindowTitle = "Untitled - Paint",
        UiTargets = new List<ComputerUseUiTarget>
        {
            new() { Id = "ui9", Name = "Brushes", Role = "RadioButton", ImageX = 345, ImageY = 74, ImageWidth = 34, ImageHeight = 47, State = "selected" },
            new() { Id = "ui11", Name = "Oval", Role = "Button", ImageX = 433, ImageY = 72, ImageWidth = 17, ImageHeight = 17, State = "off" },
            new() { Id = "ui17", Name = "Pencil", Role = "Button", ImageX = 223, ImageY = 74, ImageWidth = 27, ImageHeight = 27, State = "off" }
        }.Concat(extra).ToList()
    };

    // ───────────────────────────── drawing in one action

    [Fact]
    public void AWholeFigureArrivesAsOneActionOfSeveralStrokes()
    {
        ComputerUseTurn turn = ComputerUseActionParser.Parse("""
            {"thinking":"draw a stick figure","task_progress":{"current":"draw a person"},
             "action":{"type":"drag","strokes":[
                {"type":"circle","x":640,"y":300,"r":40},
                [[640,340],[640,460]],
                [[640,370],[590,420]],
                [[640,370],[690,420]],
                [[640,460],[600,540]],
                [[640,460],[680,540]]],
              "expected_state":"a stick figure is on the canvas"}}
            """);

        Assert.True(turn.Parsed);
        Assert.Equal(ComputerUseActionType.Drag, turn.Action.Type);
        Assert.Equal(6, turn.Action.Strokes.Count);
        Assert.True(turn.Action.Strokes[0].Count >= 20, "a circle must be many points, not a straight line");
        Assert.Equal(turn.Action.Strokes[0][0], turn.Action.Strokes[0][^1]);
        Assert.Equal((640, 340), turn.Action.Strokes[1][0]);
        Assert.Equal("Draw 6 strokes starting at (680, 300)", turn.Action.ShortLabel);
        Assert.Equal(6, turn.Action.DragPaths.Count);
    }

    [Theory]
    [InlineData("""{"action":{"type":"drag","strokes":[[100,100,300,100],[200,100,200,300]]}}""", 2)]
    [InlineData("""{"action":{"type":"draw","lines":[{"x":10,"y":10,"x2":90,"y2":90},{"from":[10,90],"to":[90,10]}]}}""", 2)]
    [InlineData("""{"action":{"type":"drag","shapes":[{"type":"rect","x":100,"y":100,"x2":300,"y2":250},{"type":"ellipse","x":200,"y":175,"rx":60,"ry":30}]}}""", 2)]
    [InlineData("""{"action":{"type":"drag","strokes":[{"points":[[5,5],[50,60],[90,5]]}]}}""", 1)]
    public void EveryCommonWayOfWritingStrokesIsRead(string json, int expectedStrokes)
    {
        ComputerUseTurn turn = ComputerUseActionParser.Parse(json);

        Assert.True(turn.Parsed);
        Assert.Equal(expectedStrokes, turn.Action.Strokes.Count);
        Assert.All(turn.Action.Strokes, stroke => Assert.True(stroke.Count >= 2));
    }

    [Fact]
    public void ABendingPathKeepsItsMiddlePoints()
    {
        ComputerUseTurn turn = ComputerUseActionParser.Parse(
            """{"action":{"type":"drag","path":[[100,100],[150,40],[200,100]]}}""");

        Assert.Single(turn.Action.Strokes);
        Assert.Equal(3, turn.Action.Strokes[0].Count);
        Assert.Equal((150, 40), turn.Action.Strokes[0][1]);
    }

    [Fact]
    public void AnOrdinaryTwoPointDragIsUnchanged()
    {
        ComputerUseTurn turn = ComputerUseActionParser.Parse(
            """{"action":{"type":"drag","x":590,"y":300,"x2":690,"y2":300}}""");

        Assert.Empty(turn.Action.Strokes);
        Assert.Equal("Drag from (590, 300) to (690, 300)", turn.Action.ShortLabel);
        Assert.Equal([(590, 300), (690, 300)], turn.Action.DragPaths.Single());
    }

    [Fact]
    public void StrokesThatGoNowhereAreDropped()
    {
        ComputerUseTurn turn = ComputerUseActionParser.Parse(
            """{"action":{"type":"drag","strokes":[[[40,40],[40,40]],[[10,10],[80,10]]]}}""");

        Assert.Single(turn.Action.Strokes);
    }

    // ───────────────────────────── the path the pointer follows

    [Fact]
    public void ADragIsWalkedInSmallEvenSteps()
    {
        IReadOnlyList<(int X, int Y)> path = ComputerUseStrokeGeometry.Interpolate([(1041, 530), (1217, 530)], 6);

        Assert.Equal((1041, 530), path[0]);
        Assert.Equal((1217, 530), path[^1]);
        Assert.True(path.Count >= 30, "a 176-pixel stroke needs dozens of moves, not nine");
        for (int i = 1; i < path.Count; i++)
        {
            int jump = Math.Max(Math.Abs(path[i].X - path[i - 1].X), Math.Abs(path[i].Y - path[i - 1].Y));
            Assert.InRange(jump, 1, 6);
        }
    }

    [Fact]
    public void AbsoluteMouseCoordinatesSpanTheWholeDesktop()
    {
        Assert.Equal(0, ComputerUseStrokeGeometry.ToAbsolute(0, 0, 2256));
        Assert.Equal(65535, ComputerUseStrokeGeometry.ToAbsolute(2255, 0, 2256));
        Assert.Equal(0, ComputerUseStrokeGeometry.ToAbsolute(-1920, -1920, 4176));
        Assert.InRange(ComputerUseStrokeGeometry.ToAbsolute(1128, 0, 2256), 32760, 32790);
    }

    [Fact]
    public void TheMeasuredAreaCoversEveryStrokeAndStaysOnScreen()
    {
        var area = ComputerUseStrokeGeometry.Bounds(
            [[(100, 100), (300, 100)], [(200, 50), (200, 400)]], 14, 0, 0, 2256, 1504);

        Assert.Equal((86, 36), (area.Left, area.Top));
        Assert.Equal(300 + 14 - 86 + 1, area.Width);
        Assert.Equal(400 + 14 - 36 + 1, area.Height);

        var clamped = ComputerUseStrokeGeometry.Bounds([[(2, 2), (2250, 1500)]], 14, 0, 0, 2256, 1504);
        Assert.Equal((0, 0, 2256, 1504), clamped);
    }

    // ───────────────────────────── measured, not guessed

    [Theory]
    [InlineData(0, 176, false)]     // nothing changed along a 176-pixel stroke: it did not draw
    [InlineData(12, 176, false)]    // a blinking caret's worth of noise
    [InlineData(176, 176, true)]    // a one-pixel pencil line
    [InlineData(2400, 176, true)]   // a brush
    [InlineData(-1, 176, true)]     // the area could not be read: unknown is never "failed"
    public void ADragIsJudgedByThePixelsItChanged(int changedPixels, double pathLength, bool visible)
    {
        Assert.Equal(visible, ComputerUseObservationEvidence.IsDragEffectVisible(changedPixels, pathLength));
    }

    [Fact]
    public void ChangedPixelsAreCounted()
    {
        Assert.Equal(2, ComputerUseObservationEvidence.CountChangedPixels([1, 1, 1, 1], [1, 9, 1, 9]));
        Assert.Equal(0, ComputerUseObservationEvidence.CountChangedPixels([7, 7], [7, 7]));
        Assert.Equal(-1, ComputerUseObservationEvidence.CountChangedPixels(null, [7, 7]));
        Assert.Equal(-1, ComputerUseObservationEvidence.CountChangedPixels([7], [7, 7]));
    }

    // ───────────────────────────── target_id must not hijack coordinates

    [Fact]
    public void AStaleTargetIdNoLongerMovesAClickToTheWrongButton()
    {
        // Step 7 of the logged run: the model clicked "Brushes" at (345,74) but the action still
        // carried target_id ui17 from the Pencil click before it, and the click went to Pencil.
        var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 345, Y = 74, TargetId = "ui17" };

        Assert.True(ComputerUseSessionController.TryResolvePointerAim(action, PaintCapture(), out var aim, out _));

        Assert.Equal("Brushes", aim.Target?.Name);
        Assert.Equal((345, 74), (aim.ImageX, aim.ImageY));
        Assert.False(aim.Named);
        Assert.Contains("ui17", aim.Note);
        Assert.Contains("ignored", aim.Note);
    }

    [Fact]
    public void AStaleTargetIdOnAClickOverEmptySpaceLeavesTheCoordinatesAlone()
    {
        var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 640, Y = 400, TargetId = "ui17" };

        Assert.True(ComputerUseSessionController.TryResolvePointerAim(action, PaintCapture(), out var aim, out _));

        Assert.Null(aim.Target);
        Assert.Equal((640, 400), (aim.ImageX, aim.ImageY));
    }

    [Theory]
    [InlineData(0, 0)]       // id alone
    [InlineData(225, 76)]    // id with coordinates on the same control
    public void ATargetIdThatAgreesWithTheActionClicksThatControl(int x, int y)
    {
        var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = x, Y = y, TargetId = "ui17" };

        Assert.True(ComputerUseSessionController.TryResolvePointerAim(action, PaintCapture(), out var aim, out _));

        Assert.Equal("Pencil", aim.Target?.Name);
        Assert.True(aim.Named);
        Assert.Equal((223, 74), (aim.ImageX, aim.ImageY));
        Assert.Equal("", aim.Note);
    }

    [Fact]
    public void AClickByCoordinatesOnAButtonIsAimedAtItsCentre()
    {
        var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 438, Y = 68 };

        Assert.True(ComputerUseSessionController.TryResolvePointerAim(action, PaintCapture(), out var aim, out _));

        Assert.Equal("Oval", aim.Target?.Name);
        Assert.Equal((433, 72), (aim.ImageX, aim.ImageY));
    }

    [Fact]
    public void AnIssuedIdThatHasGoneIsStillRefused()
    {
        var action = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 300, Y = 300, TargetId = "ui99" };

        Assert.False(ComputerUseSessionController.TryResolvePointerAim(action, PaintCapture(), out _, out string refusal));
        Assert.StartsWith("[ACTION NOT SENT]", refusal);
    }

    [Fact]
    public void ADragNeverTakesItsAimFromATargetId()
    {
        // Steps 5 and 8 of the logged run: the drag carried "target_id":"ui17".
        var drag = new ComputerUseAction { Type = ComputerUseActionType.Drag, X = 590, Y = 300, X2 = 690, Y2 = 300, TargetId = "ui17" };

        Assert.False(ComputerUseSessionController.RequiresPointerTarget(drag));
        Assert.Equal([(590, 300), (690, 300)], drag.DragPaths.Single());
    }

    [Fact]
    public void WhenSeveralControlsShareANameTheOneThatHasNotMovedIsClicked()
    {
        var first = new ComputerUseUiTarget { Name = "Close", Role = "Button", ImageX = 100, ImageY = 20 };
        var second = new ComputerUseUiTarget { Name = "Close", Role = "Button", ImageX = 900, ImageY = 20 };

        Assert.Same(second, ComputerUseSessionController.PickLiveTarget([first, second], 905, 22));
        Assert.Null(ComputerUseSessionController.PickLiveTarget([first, second], 500, 400));
        Assert.Same(first, ComputerUseSessionController.PickLiveTarget([first], 500, 400));
    }

    [Fact]
    public void ReselectingWhatTheAppReportsAsSelectedIsRecognised()
    {
        var click = new ComputerUseAction { Type = ComputerUseActionType.Click, TargetId = "ui9" };
        Assert.True(ComputerUseSessionController.TryResolvePointerAim(click, PaintCapture(), out var selected, out _));
        Assert.True(ComputerUseSessionController.IsRedundantSelection(click, selected));

        var pencil = new ComputerUseAction { Type = ComputerUseActionType.Click, TargetId = "ui17" };
        Assert.True(ComputerUseSessionController.TryResolvePointerAim(pencil, PaintCapture(), out var unselected, out _));
        Assert.False(ComputerUseSessionController.IsRedundantSelection(pencil, unselected));
    }

    // ───────────────────────────── the verdict that replaced the observer's veto

    [Fact]
    public void AToolClickTheControllerDeliveredIsNotFailedByAnObserverThatCannotSeeTheHighlight()
    {
        // Steps 3 and 4: [TARGET CLICKED] on Oval, then Pencil; the observer said neither button
        // "appears visually highlighted", and both were counted as actions without effect.
        string result = "[TARGET CLICKED] The click was delivered to the centre of the accessible control 'Pencil' (ui17) at image (223,74).";

        Assert.Equal(ComputerUseSessionController.StepVerdict.Confirmed,
            ComputerUseSessionController.JudgeStep(result, screenChanged: false, observerConfirmed: false));
    }

    [Fact]
    public void AMeasuredStrokeIsConfirmedAndAMeasuredMissIsNot()
    {
        Assert.Equal(ComputerUseSessionController.StepVerdict.Confirmed,
            ComputerUseSessionController.JudgeStep("[DRAG VERIFIED] 6 strokes, 2400 changed pixels", false, false));
        // The controller measured nothing along the path; an observer's optimism cannot overrule it.
        Assert.Equal(ComputerUseSessionController.StepVerdict.NoEffect,
            ComputerUseSessionController.JudgeStep("[DRAG NO EFFECT] nothing changed along the path", true, true));
    }

    [Fact]
    public void AnActionTheScreenReactedToIsProgressEvenWhenNobodyCanNameTheChange()
    {
        Assert.Equal(ComputerUseSessionController.StepVerdict.Delivered,
            ComputerUseSessionController.JudgeStep("Clicked at image (345,74) / screen (609,131).", screenChanged: true, observerConfirmed: false));
        Assert.Equal(ComputerUseSessionController.StepVerdict.NoEffect,
            ComputerUseSessionController.JudgeStep("Clicked at image (5,5) / screen (9,9).", screenChanged: false, observerConfirmed: false));
        Assert.Equal(ComputerUseSessionController.StepVerdict.Confirmed,
            ComputerUseSessionController.JudgeStep("Pressed Ctrl+T.", screenChanged: false, observerConfirmed: true));
    }

    [Fact]
    public void TheLoggedRunWouldNoLongerBeStoppedAfterSixActions()
    {
        // The six actions the session executed on "draw a person", with what the controller
        // reports for each now, and the observer refusing every one of them as it did then.
        string[] results =
        [
            "[TARGET CLICKED] ... 'Oval' (ui11) at image (433,72).",
            "[TARGET CLICKED] ... 'Pencil' (ui17) at image (223,74).",
            "[DRAG VERIFIED] The controller sent the drag from image (590,300) to (690,300) and measured 412 changed pixels along the path",
            "[TARGET CLICKED] ... 'Brushes' (ui9) at image (345,74).",
            "[DRAG VERIFIED] The controller sent the drag from image (600,300) to (680,300) and measured 1890 changed pixels along the path",
            "[TARGET CLICKED] ... 'Oval' (ui11) at image (433,72)."
        ];

        int withoutEffect = 0;
        foreach (string result in results)
        {
            var verdict = ComputerUseSessionController.JudgeStep(result, screenChanged: false, observerConfirmed: false);
            withoutEffect = verdict == ComputerUseSessionController.StepVerdict.NoEffect ? withoutEffect + 1 : 0;
        }

        Assert.Equal(0, withoutEffect);
    }

    [Fact]
    public void TheVerdictTellsTheModelToMoveOnRatherThanSwitchTools()
    {
        string delivered = ComputerUseSessionController.DescribeStepVerdict(
            ComputerUseSessionController.StepVerdict.Delivered, "Click left at (345, 74)");
        string failed = ComputerUseSessionController.DescribeStepVerdict(
            ComputerUseSessionController.StepVerdict.NoEffect, "Click left at (5, 5)");

        Assert.StartsWith("[STEP DELIVERED]", delivered);
        Assert.Contains("do not repeat it", delivered);
        Assert.StartsWith("[STEP HAD NO EFFECT]", failed);
        Assert.Equal("ok", ComputerUseSessionController.VerdictWord(ComputerUseSessionController.StepVerdict.Confirmed));
    }

    [Fact]
    public void AControllerConfirmedActionIsNotUndoneByAnIdenticalScreenshot()
    {
        var pending = new ComputerUseSessionController.PendingActionEvidence
        {
            BeforeScreenshot = Frame,
            ActionLabel = "Click left at (223, 74)",
            ExpectedState = "Pencil tool selected",
            ControllerResult = "[TARGET CLICKED] The click was delivered to the centre of the accessible control 'Pencil' (ui17)."
        };
        var ledger = new ComputerUseExecutionLedger();
        ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Click, X = 223, Y = 74 }, "Pencil tool selected");

        bool changed = true;
        string observation = ComputerUseSessionController.ReconcileActionEvidence(PaintCapture(), ref pending, ledger, ref changed);

        Assert.False(changed);
        Assert.DoesNotContain("[POST-ACTION VERIFICATION FAILED]", observation);
        Assert.NotEqual(ComputerUseActionEvidenceState.Failed, ledger.State);
    }

    [Fact]
    public void ADeliveredStepDoesNotBlockFinishing()
    {
        var ledger = new ComputerUseExecutionLedger();
        ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Click, X = 345, Y = 74 }, "Brushes selected");
        ledger.ReconcileObservation(visibleChange: true);
        Assert.False(ledger.CanFinish);

        ledger.MarkDelivered();

        Assert.True(ledger.CanFinish);
        Assert.False(ledger.RequiresRecovery);
        Assert.Contains("treat it as done", ledger.Describe());
    }

    // ───────────────────────────── what the model and the observer are told

    [Fact]
    public void ControlsSayWhetherTheyAreSelected()
    {
        ComputerUseCapture capture = PaintCapture();

        Assert.Contains("state=selected", capture.UiTargets[0].Describe());
        Assert.Contains("state=off", capture.UiTargets[2].Describe());
        Assert.True(capture.UiTargets[0].IsSelected);
        Assert.False(capture.UiTargets[2].IsSelected);
    }

    [Fact]
    public void TheCanvasIsReportedAsAWorkAreaInScreenshotPixels()
    {
        // Paint's own accessibility tree at 2256x1504: toolbar groups and the canvas.
        IReadOnlyList<ComputerUseUiRegion> regions = ComputerUseImageGeometry.SelectRegions(
        [
            new() { Name = "Tools", Role = "Group", Left = 353, Top = 68, Right = 559, Bottom = 218 },
            new() { Name = "Using Brush tool on Canvas", Role = "Group", Left = 213, Top = 258, Right = 1567, Bottom = 1088 },
            new() { Name = "", Role = "Pane", Left = 0, Top = 226, Right = 1782, Bottom = 1089 }
        ], 2256, 1504, 1280, 853);

        ComputerUseUiRegion canvas = Assert.Single(regions);
        Assert.Equal("Using Brush tool on Canvas", canvas.Name);
        Assert.InRange(canvas.Left, 120, 122);
        Assert.InRange(canvas.Right, 888, 890);
        Assert.True(canvas.Contains(640, 400));
        Assert.False(canvas.Contains(223, 74));
        Assert.Contains("spans x", canvas.Describe());
    }

    [Fact]
    public void TheObserverIsGivenTheControllersOwnEvidence()
    {
        var ledger = new ComputerUseExecutionLedger();
        ledger.BeginAction(new ComputerUseAction { Type = ComputerUseActionType.Click, X = 223, Y = 74 }, "Pencil tool selected");

        string payload = ComputerUseVisualAssessment.BuildPayload(
            "Open the paint app and draw a person",
            new ComputerUseTaskProgress { Current = "draw a person" },
            PaintCapture(), ledger, "",
            "[TARGET CLICKED] The click was delivered to 'Pencil' (ui17).",
            ["3. Click left at (433, 72) -> ok: [TARGET CLICKED] 'Oval'"]);

        Assert.Contains("controller_evidence", payload);
        Assert.Contains("TARGET CLICKED", payload);
        Assert.Contains("steps_already_done_for_current_outcome", payload);
        Assert.Contains("state=selected", payload);
    }

    [Fact]
    public void TheObserverIsToldToJudgeTheStepAndTheOutcomeSeparately()
    {
        string prompt = ComputerUseVisualAssessment.SystemPrompt;

        Assert.Contains("Judge ONLY that step", prompt);
        Assert.Contains("controller_evidence", prompt);
        Assert.Contains("a stick figure is a drawn person", prompt);
    }

    [Fact]
    public void ACanvasReachingPastItsWindowIsCutToTheWindow()
    {
        // Measured on a real Paint window at 150,100-1353,902: the canvas reported 449..1503 x 453..1002.
        var canvas = new ComputerUseUiRegion { Name = "Using Brush tool on Canvas", Role = "Group", Left = 449, Top = 453, Right = 1503, Bottom = 1002 };

        ComputerUseUiRegion? clipped = ComputerUseImageGeometry.ClipToWindow(canvas, 150, 100, 1353, 902);

        Assert.NotNull(clipped);
        Assert.Equal((449, 453, 1353, 902), (clipped!.Left, clipped.Top, clipped.Right, clipped.Bottom));
        Assert.Equal("Using Brush tool on Canvas", clipped.Name);
    }

    [Fact]
    public void ARegionEntirelyOutsideItsWindowIsDropped()
    {
        var offscreen = new ComputerUseUiRegion { Name = "Canvas", Left = 1400, Top = 950, Right = 1500, Bottom = 1000 };

        Assert.Null(ComputerUseImageGeometry.ClipToWindow(offscreen, 150, 100, 1353, 902));
    }

    [Fact]
    public void AClickAimedByTargetIdIsLabelledByItsTargetNotTheCorner()
    {
        // Simulated runs logged "Click left at (0, 0)" for every tool selection, and the observer
        // was handed that as the step it had to judge.
        var byTarget = new ComputerUseAction { Type = ComputerUseActionType.Click, TargetId = "ui16" };
        var byPoint = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 345, Y = 74 };

        Assert.DoesNotContain("(0, 0)", byTarget.ShortLabel);
        Assert.Contains("on target ui16", byTarget.ShortLabel);
        Assert.Contains("at (345, 74)", byPoint.ShortLabel);
    }
}
