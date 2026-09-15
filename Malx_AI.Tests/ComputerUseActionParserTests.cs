using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseActionParserTests
    {
        [Fact]
        public void Parse_ReadsFencedJsonActionAndSafety()
        {
            const string raw = """
thinking about the desktop
```json
{
  "thinking": "Notepad is focused.",
  "safety": { "ok": true, "dangerous": false, "risk": "low", "causes": "Types text", "effects": "Characters appear" },
  "action": { "type": "type", "text": "hello", "explanation": "Type hello in Notepad" }
}
```
""";

            ComputerUseTurn turn = ComputerUseActionParser.Parse(raw);
            Assert.True(turn.Parsed);
            Assert.Equal(ComputerUseActionType.Type, turn.Action.Type);
            Assert.Equal("hello", turn.Action.Text);
            Assert.True(turn.Safety.Ok);
            Assert.False(turn.Safety.Dangerous);
            Assert.Equal("Notepad is focused.", turn.Thinking);
        }

        [Fact]
        public void Parse_ReadsCoordinateArrayClick()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("""{"action":{"type":"click","coordinate":[120,80]}}""");
            Assert.True(turn.Parsed);
            Assert.Equal(ComputerUseActionType.Click, turn.Action.Type);
            Assert.Equal(120, turn.Action.X);
            Assert.Equal(80, turn.Action.Y);
        }

        [Fact]
        public void Parse_FailsOnEmptyOutput()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("no json here");
            Assert.False(turn.Parsed);
            Assert.False(string.IsNullOrWhiteSpace(turn.ParseError));
        }

        [Fact]
        public void Parse_ReadsZoomOut()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("""{"action":{"type":"zoom_out","x":10,"y":20}}""");
            Assert.True(turn.Parsed);
            Assert.Equal(ComputerUseActionType.Zoom, turn.Action.Type);
            Assert.Equal(-1, turn.Action.Dy);
            Assert.Equal(10, turn.Action.X);
            Assert.Equal(20, turn.Action.Y);
        }

        [Fact]
        public void Parse_ReadsOpenApp()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("""{"action":{"type":"open","text":"Microsoft Edge"}}""");
            Assert.True(turn.Parsed);
            Assert.Equal(ComputerUseActionType.Open, turn.Action.Type);
            Assert.Equal("Microsoft Edge", turn.Action.Text);
        }

        [Fact]
        public void Parse_ReadsAccessibleTargetId()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("""{"action":{"type":"click","target_id":"ui7"}}""");
            Assert.True(turn.Parsed);
            Assert.Equal("ui7", turn.Action.TargetId);
        }

        [Fact]
        public void Parse_ReadsExpectedVisibleState()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("""{"action":{"type":"click","expected_state":"The Save confirmation is visible"}}""");
            Assert.True(turn.Parsed);
            Assert.Equal("The Save confirmation is visible", turn.Action.ExpectedState);
        }

        [Fact]
        public void Parse_ReadsStructuredTaskProgress()
        {
            ComputerUseTurn turn = ComputerUseActionParser.Parse("""
                {"task_progress":{"completed":["Opened the Axiom GitHub repository"],"current":"Open the requested YouTube video","next":""},"action":{"type":"key","keys":"Ctrl+T"}}
                """);

            Assert.True(turn.Parsed);
            Assert.Equal("Opened the Axiom GitHub repository", Assert.Single(turn.Progress.Completed));
            Assert.Equal("Open the requested YouTube video", turn.Progress.Current);
        }
    }
}
