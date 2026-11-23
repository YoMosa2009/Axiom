using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// The text protocol small local models use. A Qwen3-1.7B run (2026-11-23) sent browser calls
/// with no action because "action:" lines were dropped, and reached for run_command when no tool
/// was needed; these cover both.
/// </summary>
public sealed class AgentFlatProtocolTests
{
    [Fact]
    public void BrowserActionLineIsKept()
    {
        Assert.True(AgentToolCallParser.TryParse("TOOL browser_control\naction: open\nurl: https://example.com\nEND", out var call, out _));
        Assert.Equal(AgentToolNames.BrowserControl, call!.Tool);
        Assert.Equal("open", call.Arg("action"));
        Assert.Equal("https://example.com", call.Arg("url"));
    }

    [Fact]
    public void InspectNeedsOnlyItsAction()
    {
        Assert.True(AgentToolCallParser.TryParse("TOOL browser_control\naction: inspect\nEND", out var call, out string? error));
        Assert.Null(error);
        Assert.Equal("inspect", call!.Arg("action"));
    }

    [Fact]
    public void ActionHeaderStyleStillNamesTheTool()
    {
        Assert.True(AgentToolCallParser.TryParse("ACTION: list_directory\npath: C:\\work\nEND", out var call, out _));
        Assert.Equal(AgentToolNames.ListDirectory, call!.Tool);
        Assert.False(call.Arguments.ContainsKey("action"));
    }

    [Theory]
    [InlineData("TOOL browser_control open https://example.com", "open", "url", "https://example.com")]
    [InlineData("TOOL browser_control click b2", "click", "target_id", "b2")]
    [InlineData("TOOL browser_control inspect", "inspect", null, null)]
    public void OneLineBrowserCallsAreRead(string reply, string action, string? key, string? value)
    {
        Assert.True(AgentToolCallParser.TryParse(reply, out var call, out _));
        Assert.Equal(action, call!.Arg("action"));
        if (key != null)
            Assert.Equal(value, call.Arg(key));
    }

    [Theory]
    [InlineData("TOOL browser_control\nurl: https://example.com\nEND", "open")]
    [InlineData("TOOL browser_control\ntarget_id: b4\nEND", "click")]
    [InlineData("TOOL browser_control\ntarget_id: b2\ntext: Ada\nEND", "fill")]
    public void AMissingBrowserActionIsInferred(string reply, string expected)
    {
        Assert.True(AgentToolCallParser.TryParse(reply, out var call, out _));
        Assert.Equal(expected, call!.Arg("action"));
    }

    [Fact]
    public void OneLineSkillCallNamesTheSkill()
    {
        Assert.True(AgentToolCallParser.TryParse("TOOL use_skill brand-voice", out var call, out _));
        Assert.Equal(AgentToolNames.UseSkill, call!.Tool);
        Assert.Equal("brand-voice", call.Arg("name"));
    }

    [Theory]
    [InlineData("144 divided by 12 is 12.")]
    [InlineData("Action: open the page in your browser and check the heading.")]
    [InlineData("The answer is 12.\nTool run_command failed earlier, so I worked it out by hand.")]
    public void ProseIsAnAnswerNotACall(string reply)
    {
        Assert.False(AgentToolCallParser.TryParse(reply, out var call, out string? error));
        Assert.Null(call);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(AgentTier.Compact)]
    [InlineData(AgentTier.Micro)]
    public void SmallModelInstructionsShowBrowserSkillAndPlainAnswers(AgentTier tier)
    {
        string prompt = AgentPromptBuilder.Build(tier, AgentScope.Folder(@"C:\work"), AgentApprovalMode.Manual).Replace("\r\n", "\n");

        Assert.Contains("TOOL browser_control\naction: open\nurl: https://example.com\nEND", prompt);
        Assert.Contains("action: inspect", prompt);
        Assert.Contains("target_id: b2", prompt);
        Assert.Contains("TOOL use_skill\nname: brand-voice\nEND", prompt);
        Assert.Contains("answer in plain words, with no TOOL line", prompt);
    }

    [Fact]
    public void EveryExampleInTheInstructionsParses()
    {
        string prompt = AgentPromptBuilder.Build(AgentTier.Compact, AgentScope.Folder(@"C:\work"), AgentApprovalMode.Manual).Replace("\r\n", "\n");
        var examples = System.Text.RegularExpressions.Regex.Matches(prompt, @"(?m)^TOOL [a-z_]+\n(?:.*\n)*?END$");
        Assert.True(examples.Count >= 7, $"found {examples.Count} examples");
        foreach (string example in examples.Select(m => m.Value))
        {
            Assert.True(AgentToolCallParser.TryParse(example, out var call, out string? error), example + " -> " + error);
            Assert.NotNull(call);
        }
    }
}
