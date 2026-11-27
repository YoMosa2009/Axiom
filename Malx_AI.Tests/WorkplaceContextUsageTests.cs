using Xunit;

namespace Malx_AI.Tests;

public sealed class WorkplaceContextUsageTests
{
    private static WorkplaceContextEstimate Estimate(int messages = 1000, int pending = 0) =>
        new(Messages: messages, SystemTools: 500, McpTools: 0, Skills: 0, SystemPrompt: 1100,
            McpInstructions: 0, MemoryFiles: 0, PendingInput: pending);

    [Fact]
    public void EstimateOnlyUsesTheEstimatedRowsAsTheyAre()
    {
        var result = WorkplaceContextUsage.Calculate(100_000, null, 0, Estimate(pending: 50), 0);

        Assert.False(result.IsMeasured);
        Assert.Equal(1000, result.Messages);
        Assert.Equal(2650, result.Used);
        Assert.Equal(100_000 - 2650 - 8000, result.Free);
        Assert.Equal(2.65, result.Percent, 3);
        Assert.Equal(80_000 - 2650, result.UntilCompact);
    }

    [Fact]
    public void MeasuredPromptOverridesTheEstimateAndRowsStillAddUp()
    {
        var result = WorkplaceContextUsage.Calculate(100_000, 12_000, 300, Estimate(messages: 99), 0);

        Assert.True(result.IsMeasured);
        Assert.Equal(12_300, result.Used);
        Assert.Equal(500, result.SystemTools);
        Assert.Equal(1100, result.SystemPrompt);
        Assert.Equal(12_300 - 1600, result.Messages);
    }

    [Fact]
    public void GrowthSinceMeasurementAndPendingInputAreAddedOnTop()
    {
        var result = WorkplaceContextUsage.Calculate(100_000, 10_000, 200, Estimate(pending: 40), 700);

        Assert.Equal(10_000 + 200 + 700 + 40, result.Used);
        Assert.Equal(40, result.PendingInput);
        Assert.Equal(10_900 - 1600, result.Messages);
    }

    [Fact]
    public void FixedOverheadLargerThanTheMeasurementIsScaledDownWithNoNegativeRows()
    {
        var result = WorkplaceContextUsage.Calculate(100_000, 800, 0, Estimate(), 0);

        Assert.Equal(800, result.Used);
        Assert.Equal(0, result.Messages);
        Assert.True(result.SystemTools >= 0 && result.SystemPrompt >= 0);
        Assert.Equal(800, result.SystemTools + result.SystemPrompt);
    }

    [Fact]
    public void UsageBeyondTheWindowClampsPercentAndFreeSpace()
    {
        var result = WorkplaceContextUsage.Calculate(8192, 20_000, 0, Estimate(), 0);

        Assert.Equal(20_000, result.Used);
        Assert.Equal(100.0, result.Percent);
        Assert.Equal(0, result.Free);
        Assert.Equal(0, result.UntilCompact);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ZeroOrInvalidWindowNeverDividesOrGoesNegative(int window)
    {
        var result = WorkplaceContextUsage.Calculate(window, 5000, 100, Estimate(), 10);

        Assert.Equal(0.0, result.Percent);
        Assert.Equal(0, result.Free);
        Assert.Equal(0, result.UntilCompact);
        Assert.Equal(0, result.AutocompactBuffer);
        Assert.Equal(5110, result.Used);
    }

    [Fact]
    public void ZeroMeasuredPromptIsTreatedAsNoMeasurement()
    {
        var result = WorkplaceContextUsage.Calculate(100_000, 0, 50, Estimate(), 0);

        Assert.False(result.IsMeasured);
        Assert.Equal(1000, result.Messages);
    }
}
