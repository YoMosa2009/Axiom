using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class WorkplaceRunStepsTests
{
    [Fact]
    public void ToolStartedStatusRoundTripsItsLabelAndBudget()
    {
        string status = WorkplaceRunSteps.ToolStarted("Searching the web for 'axiom'...", 2, 8);

        Assert.Equal("Using a tool 2 of 8 · Searching the web for 'axiom'", status);
        Assert.Equal("Searching the web for 'axiom'", WorkplaceRunSteps.TryGetStartedLabel(status));
        Assert.True(WorkplaceRunSteps.TryParseBudget(status, out int used, out int max));
        Assert.Equal((2, 8), (used, max));
    }

    [Fact]
    public void ResumeAndFailureStatusesCarryTheBudgetButNoStepLabel()
    {
        foreach (string status in new[] { WorkplaceRunSteps.Resumed(3, 4), WorkplaceRunSteps.ToolFailed(3, 4) })
        {
            Assert.Null(WorkplaceRunSteps.TryGetStartedLabel(status));
            Assert.True(WorkplaceRunSteps.TryParseBudget(status, out int used, out int max));
            Assert.Equal((3, 4), (used, max));
        }
        Assert.StartsWith(WorkplaceRunSteps.ToolFailedPrefix, WorkplaceRunSteps.ToolFailed(1, 3));
    }

    [Fact]
    public void FooterIsFoldedIntoTheActivitySummaryLikeAgentRuns()
    {
        var steps = new List<string> { WorkplaceRunSteps.StepLine("Searching the web"), WorkplaceRunSteps.StepLine("Running Python sandbox") };
        WorkplaceRunSteps.MarkLastFailed(steps);

        string content = WorkplaceRunSteps.AppendStepsFooter("Here is the answer.", steps);
        var summary = WorkplaceExecutionSummary.Parse(content, allowActivity: true);

        Assert.Equal("Here is the answer.", summary.Body);
        Assert.Equal(2, summary.Count);
        Assert.Contains("✓ Searching the web", summary.Steps);
        Assert.Contains("✗ Running Python sandbox", summary.Steps);
    }

    [Fact]
    public void FooterIsNotAddedTwiceOrToEmptyCards()
    {
        var steps = new List<string> { "✓ Calculating" };
        string once = WorkplaceRunSteps.AppendStepsFooter("Answer", steps);

        Assert.Equal(once, WorkplaceRunSteps.AppendStepsFooter(once, steps));
        Assert.Equal("", WorkplaceRunSteps.AppendStepsFooter("", steps));
        Assert.Equal("Answer", WorkplaceRunSteps.AppendStepsFooter("Answer", new List<string>()));
    }

    [Theory]
    [InlineData("ERROR: timed out", true)]
    [InlineData("Web search is disabled by the user.", true)]
    [InlineData("", true)]
    [InlineData("1. Example Domain — https://example.com", false)]
    [InlineData("42", false)]
    public void ToolFailuresAreRecognised(string observation, bool failed)
    {
        Assert.Equal(failed, WorkplaceRunSteps.LooksLikeFailure(observation));
    }
}
