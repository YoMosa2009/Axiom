using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// "@ComputerUse open the paint app, and draw a smiley face" opened Paint and then never drew
/// anything, cycling between the pen and the oval tool. Nothing was wrong with the loop: the action
/// vocabulary had no way to hold the mouse button down and move, so drawing a stroke — and dragging
/// a shape out to size, rubber-band selecting, moving a window by its title bar, moving a slider —
/// was not expressible at all. Selecting a tool was the only thing the model could actually do.
/// </summary>
public class ComputerUseDragTests
{
    private static ComputerUseTurn ParseAction(string action)
    {
        string raw = """
            {"thinking":"draw","task_progress":{"completed":[],"current":"draw","next":"","remaining":[]},
             "safety":{"ok":true,"dangerous":false,"risk":"low","causes":"draws","effects":"a stroke","reason":"safe"},
             "action":ACTION}
            """.Replace("ACTION", action);
        ComputerUseTurn turn = ComputerUseActionParser.Parse(raw);
        Assert.True(turn.Parsed, turn.ParseError);
        return turn;
    }

    [Theory]
    [InlineData("drag")]
    [InlineData("left_click_drag")]
    [InlineData("click_drag")]
    [InlineData("drag_to")]
    [InlineData("stroke")]
    [InlineData("draw")]
    public void TheNamesAModelReachesForAllMeanDrag(string typeName)
    {
        ComputerUseTurn turn = ParseAction($$"""{"type":"{{typeName}}","x":100,"y":120,"x2":260,"y2":300,"expected_state":"a stroke appears"}""");
        Assert.Equal(ComputerUseActionType.Drag, turn.Action.Type);
        Assert.Equal(100, turn.Action.X);
        Assert.Equal(120, turn.Action.Y);
        Assert.Equal(260, turn.Action.X2);
        Assert.Equal(300, turn.Action.Y2);
    }

    [Theory]
    // The end point has several conventional spellings.
    [InlineData("""{"type":"drag","x":10,"y":20,"x2":90,"y2":80}""", 90, 80)]
    [InlineData("""{"type":"drag","x":10,"y":20,"to_x":90,"to_y":80}""", 90, 80)]
    [InlineData("""{"type":"drag","x":10,"y":20,"end_x":90,"end_y":80}""", 90, 80)]
    [InlineData("""{"type":"drag","x":10,"y":20,"target_x":90,"target_y":80}""", 90, 80)]
    // Expressed only as an offset, the end is derived rather than silently becoming a no-op.
    [InlineData("""{"type":"drag","x":10,"y":20,"dx":80,"dy":60}""", 90, 80)]
    public void AnEndPointIsUnderstoodHoweverItIsNamed(string action, int expectedX2, int expectedY2)
    {
        ComputerUseTurn turn = ParseAction(action);
        Assert.Equal(ComputerUseActionType.Drag, turn.Action.Type);
        Assert.Equal(expectedX2, turn.Action.X2);
        Assert.Equal(expectedY2, turn.Action.Y2);
    }

    [Fact]
    public void ADragIsAVisibleActionAndMustProduceEvidence()
    {
        var drag = new ComputerUseAction { Type = ComputerUseActionType.Drag, X = 10, Y = 20, X2 = 90, Y2 = 80 };
        Assert.True(ComputerUseSessionController.RequiresVisualEvidence(drag));
    }

    [Fact]
    public void RepeatedStrokesAreNotTreatedAsAStuckAction()
    {
        // Drawing is repetitive by nature — several strokes from one point are how a shape gets
        // built. Blocking the second as a "repeated missed target" would make drawing impossible
        // for the mirror image of the reason clicking already was.
        var drag = new ComputerUseAction { Type = ComputerUseActionType.Drag, X = 10, Y = 20, X2 = 90, Y2 = 80 };
        Assert.False(ComputerUseSessionController.IsRepeatSensitiveAction(drag));
    }

    [Fact]
    public void ADragAimsAtCoordinatesRatherThanAnAccessibleControl()
    {
        // A canvas has no named control to target; a drag is defined by its two points.
        var drag = new ComputerUseAction { Type = ComputerUseActionType.Drag, X = 10, Y = 20, X2 = 90, Y2 = 80 };
        Assert.False(ComputerUseSessionController.RequiresPointerTarget(drag));
    }

    [Fact]
    public void TheActionReadsBackAsWhatItDoes()
    {
        var drag = new ComputerUseAction { Type = ComputerUseActionType.Drag, X = 10, Y = 20, X2 = 90, Y2 = 80 };
        Assert.Equal("Drag from (10, 20) to (90, 80)", drag.ShortLabel);
    }

    [Fact]
    public void ADragWithNoDistanceIsNotAStroke()
    {
        // A start and end at the same point presses and releases in one place, which is a click
        // wearing a drag's name. The controller refuses it rather than reporting a stroke that
        // left no mark.
        ComputerUseTurn turn = ParseAction("""{"type":"drag","x":40,"y":40,"x2":40,"y2":40}""");
        Assert.Equal(ComputerUseActionType.Drag, turn.Action.Type);
        Assert.Equal(turn.Action.X, turn.Action.X2);
        Assert.Equal(turn.Action.Y, turn.Action.Y2);
    }

    [Fact]
    public void TheComputerUseConventionReadsCoordinateAsTheEndOfADrag()
    {
        // The standard computer-use tool shape for a drag is start_coordinate + coordinate, where
        // "coordinate" is the END. Reading it as the start — as a click would — lost the end point
        // entirely, and the logged run showed exactly that: "Drag from (640, 426) to (640, 426)",
        // three times, while the model said it meant to draw a large circle.
        ComputerUseTurn turn = ParseAction("""{"type":"left_click_drag","start_coordinate":[512,298],"coordinate":[768,554]}""");
        Assert.Equal(ComputerUseActionType.Drag, turn.Action.Type);
        Assert.Equal(512, turn.Action.X);
        Assert.Equal(298, turn.Action.Y);
        Assert.Equal(768, turn.Action.X2);
        Assert.Equal(554, turn.Action.Y2);
    }

    [Theory]
    // Nested points, in the forms models actually write.
    [InlineData("""{"type":"drag","start":{"x":100,"y":120},"end":{"x":300,"y":320}}""", 100, 120, 300, 320)]
    [InlineData("""{"type":"drag","from":{"x":100,"y":120},"to":{"x":300,"y":320}}""", 100, 120, 300, 320)]
    [InlineData("""{"type":"drag","start":[100,120],"end":[300,320]}""", 100, 120, 300, 320)]
    [InlineData("""{"type":"drag","from":[100,120],"to":[300,320]}""", 100, 120, 300, 320)]
    [InlineData("""{"type":"drag","x":100,"y":120,"to":[300,320]}""", 100, 120, 300, 320)]
    // A stroke as a path: first point to last.
    [InlineData("""{"type":"drag","path":[[100,120],[200,220],[300,320]]}""", 100, 120, 300, 320)]
    [InlineData("""{"type":"drag","points":[{"x":100,"y":120},{"x":300,"y":320}]}""", 100, 120, 300, 320)]
    // Decimals, which used to be dropped silently and fall back to the start point.
    [InlineData("""{"type":"drag","x":100.4,"y":119.6,"x2":300.0,"y2":320.2}""", 100, 120, 300, 320)]
    [InlineData("""{"type":"drag","start_coordinate":[100.0,120.0],"coordinate":[300.5,320.4]}""", 100, 120, 300, 320)]
    // Numbers as strings.
    [InlineData("""{"type":"drag","x":"100","y":"120","x2":"300","y2":"320"}""", 100, 120, 300, 320)]
    public void EveryCommonWayOfWritingADragKeepsBothEnds(string action, int x, int y, int x2, int y2)
    {
        ComputerUseTurn turn = ParseAction(action);
        Assert.Equal(ComputerUseActionType.Drag, turn.Action.Type);
        Assert.Equal((x, y, x2, y2), (turn.Action.X, turn.Action.Y, turn.Action.X2, turn.Action.Y2));
    }

    [Fact]
    public void AClickStillReadsCoordinateAsWhereItClicks()
    {
        // The drag convention must not leak into a click, where "coordinate" IS the point.
        ComputerUseTurn turn = ParseAction("""{"type":"click","coordinate":[567,163]}""");
        Assert.Equal(ComputerUseActionType.Click, turn.Action.Type);
        Assert.Equal(567, turn.Action.X);
        Assert.Equal(163, turn.Action.Y);
    }

    [Fact]
    public void ADecimalClickCoordinateNoLongerBreaksParsing()
    {
        // The coordinate array used GetInt32, which throws on a decimal.
        ComputerUseTurn turn = ParseAction("""{"type":"click","coordinate":[567.6,162.8]}""");
        Assert.Equal(568, turn.Action.X);
        Assert.Equal(163, turn.Action.Y);
    }
}
