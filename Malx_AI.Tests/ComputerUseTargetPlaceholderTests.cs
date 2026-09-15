using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// The run of "open microsoft edge, take me to any weather website, and then make a new tab and
/// take me to youtube" reached the weather site, then died with
/// "[ACTION NOT SENT] Target id 'none' is stale or unavailable" — five turns without desktop input.
/// The model had answered the optional target_id field with the string "none", meaning there is no
/// target, and that was read as a target that could not be found.
/// </summary>
public class ComputerUseTargetPlaceholderTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("None")]
    [InlineData("NULL")]
    [InlineData("nil")]
    [InlineData("n/a")]
    [InlineData("undefined")]
    [InlineData("empty")]
    [InlineData("-")]
    [InlineData("  none  ")]
    public void APlaceholderTargetIdMeansNoTarget(string placeholder)
    {
        string raw = """
            {"thinking":"open a new tab","task_progress":{"completed":[],"current":"open a tab","next":"","remaining":[]},
             "safety":{"ok":true,"dangerous":false,"risk":"low","causes":"opens a tab","effects":"a new tab","reason":"safe"},
             "action":{"type":"key","keys":"Ctrl+T","target_id":"PLACEHOLDER","expected_state":"a new tab is open"}}
            """.Replace("PLACEHOLDER", placeholder);

        ComputerUseTurn turn = ComputerUseActionParser.Parse(raw);
        Assert.True(turn.Parsed, turn.ParseError);
        Assert.Equal("", turn.Action.TargetId);
    }

    [Fact]
    public void ARealTargetIdIsStillKept()
    {
        const string raw = """
            {"thinking":"click new tab","task_progress":{"completed":[],"current":"open a tab","next":"","remaining":[]},
             "safety":{"ok":true,"dangerous":false,"risk":"low","causes":"clicks","effects":"a new tab","reason":"safe"},
             "action":{"type":"click","x":10,"y":20,"target_id":"ui3","expected_state":"a new tab is open"}}
            """;

        ComputerUseTurn turn = ComputerUseActionParser.Parse(raw);
        Assert.True(turn.Parsed, turn.ParseError);
        Assert.Equal("ui3", turn.Action.TargetId);
    }

    [Fact]
    public void AKeystrokeIsNotAPointerActionSoATargetIdCannotApplyToIt()
    {
        // Even a non-placeholder target id on a keystroke is meaningless: Ctrl+T has nowhere to
        // aim. Only pointer actions resolve a target, which is what stops a stray field from
        // vetoing a keystroke the task depends on.
        Assert.False(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.Key, Keys = "Ctrl+T", TargetId = "ui3" }));
        Assert.False(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.Type, Text = "hello", TargetId = "ui3" }));
        Assert.False(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.Scroll, TargetId = "ui3" }));

        Assert.True(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.Click, TargetId = "ui3" }));
        Assert.True(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.DoubleClick, TargetId = "ui3" }));
        Assert.True(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.RightClick, TargetId = "ui3" }));
        Assert.True(ComputerUseSessionController.RequiresPointerTarget(
            new ComputerUseAction { Type = ComputerUseActionType.Move, TargetId = "ui3" }));
    }

    [Fact]
    public void TheNewTabKeystrokeFromTheFailedRunIsStillRecognisedAsANewTabAction()
    {
        // The placeholder must not have cost the action its meaning either — this is what sets the
        // pending-tab baseline.
        const string raw = """
            {"thinking":"open a new tab for YouTube","task_progress":{"completed":[],"current":"open a tab","next":"","remaining":[]},
             "safety":{"ok":true,"dangerous":false,"risk":"low","causes":"opens a tab","effects":"a new tab","reason":"safe"},
             "action":{"type":"key","keys":"Ctrl+T","target_id":"none","expected_state":"a new blank tab is open"}}
            """;

        ComputerUseTurn turn = ComputerUseActionParser.Parse(raw);
        Assert.True(turn.Parsed, turn.ParseError);

        var capture = new ComputerUseCapture
        {
            JpegBytes = [1],
            BrowserState = new() { Address = "https://weather.com", TabIds = ["t1"], TabTitles = ["t1"], HasTabTelemetry = true }
        };
        Assert.True(ComputerUseSessionController.IsNewTabAction(turn.Action, capture));
    }

    [Theory]
    // Ids the controller actually issues.
    [InlineData("ui1", true)]
    [InlineData("ui12", true)]
    [InlineData("UI3", true)]
    // Names the model invented for the control it had in mind. These were reported as "stale",
    // which threw away the action instead of using the coordinates supplied alongside it.
    [InlineData("ui_brush_tool", false)]
    [InlineData("brush", false)]
    [InlineData("ui", false)]
    [InlineData("ui-3", false)]
    [InlineData("", false)]
    public void AnIssuedIdIsToldApartFromAnInventedOne(string id, bool issued)
    {
        Assert.Equal(issued, ComputerUseTargetId.IsIssued(id));
    }
}
