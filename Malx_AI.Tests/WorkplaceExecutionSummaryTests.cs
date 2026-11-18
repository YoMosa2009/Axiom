using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public class WorkplaceExecutionSummaryTests
{
    [Fact]
    public void CompletedActivitySeparatesWithoutLosingAnySteps()
    {
        string activity = string.Join("\n", Enumerable.Range(1, 26).Select(i => $"- ✓ Action {i}"));
        var result = WorkplaceExecutionSummary.Parse("The app opens.\n\n### Steps Executed (26)\n" + activity, true);
        Assert.Equal("The app opens.", result.Body);
        Assert.Equal(activity, result.Steps);
        Assert.Equal(26, result.Count);
    }

    [Theory]
    [InlineData("User content\n### Steps Executed (1)\n- item", false)]
    [InlineData("```markdown\n### Steps Executed (1)\n- example\n```", true)]
    [InlineData("~~~\n### Steps Executed (1)\n- example\n~~~", true)]
    [InlineData("Answer\n### Steps Executed (1)\n- item\nStill important answer text", true)]
    [InlineData("Answer\n### Steps Executed (2)\n- only one", true)]
    [InlineData("Answer\n### Steps Executed (0)", true)]
    [InlineData("Answer\n### Steps Executed (99999999999999999999)\n- item", true)]
    public void DoesNotHideUserContentCodeExamplesOrFollowingAnswer(string content, bool allow)
    {
        var result = WorkplaceExecutionSummary.Parse(content, allow);
        Assert.Equal(content, result.Body);
        Assert.Equal(0, result.Count);
        Assert.Empty(result.Steps);
    }

    [Fact]
    public void RestoredCrLfHistoryAndIndentedStepDetailsStayReadable()
    {
        var result = WorkplaceExecutionSummary.Parse("Answer\r\n\r\n### Steps Executed (2)\r\n- First\r\n  Extra detail\r\n- Second\r\n", true);
        Assert.Equal("Answer", result.Body);
        Assert.Equal(2, result.Count);
        Assert.Equal("- First\n  Extra detail\n- Second", result.Steps);
    }

    [Fact]
    public void HeaderAfterAFencedExampleCanBeSeparated()
    {
        var result = WorkplaceExecutionSummary.Parse("```\n### Steps Executed (3)\n```\nDone.\n### Steps Executed (1)\n- Tool result", true);
        Assert.Equal("```\n### Steps Executed (3)\n```\nDone.", result.Body);
        Assert.Equal(1, result.Count);
    }
}
