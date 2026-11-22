using Xunit;

namespace Malx_AI.Tests;

public sealed class EffortBudgetTests
{
    [Theory]
    [InlineData(2048, 8192, 2048)]
    [InlineData(2730, 8192, 2730)]
    [InlineData(3277, 32768, 3277)]
    [InlineData(0, 8192, 2048)]
    [InlineData(100, 8192, 256)]
    public void LocalReserveMatchesTheRealReplyLimit(int maxTokens, int contextSize, int expected)
    {
        Assert.Equal(expected, EffortPolicy.LocalGenerationReserve(maxTokens, contextSize));
    }

    [Fact]
    public void LocalReserveNeverTakesMoreThanHalfTheWindow()
    {
        Assert.Equal(1024, EffortPolicy.LocalGenerationReserve(3000, 2048));
    }

    [Theory]
    [InlineData(EffortLevel.Light, 8192)]
    [InlineData(EffortLevel.Medium, 8192)]
    [InlineData(EffortLevel.High, 8192)]
    [InlineData(EffortLevel.ExtraHigh, 10240)]
    [InlineData(EffortLevel.Ultra, 12288)]
    public void ThinkingTurnsGetMoreOutputRoomAtHigherEffort(EffortLevel level, int expected)
    {
        Assert.Equal(expected, EffortPolicy.ScaleCompletionTokens(8192, thinkingEnabled: true, contextWindow: 1_000_000, level));
    }

    [Fact]
    public void TurnsWithoutThinkingKeepTheirOutputCap()
    {
        Assert.Equal(8192, EffortPolicy.ScaleCompletionTokens(8192, thinkingEnabled: false, contextWindow: 1_000_000, EffortLevel.Ultra));
    }

    [Fact]
    public void OutputRoomIsCappedAtAQuarterOfTheWindowButNeverBelowTheBaseline()
    {
        Assert.Equal(10000, EffortPolicy.ScaleCompletionTokens(8192, thinkingEnabled: true, contextWindow: 40000, EffortLevel.Ultra));
        Assert.Equal(8192, EffortPolicy.ScaleCompletionTokens(8192, thinkingEnabled: true, contextWindow: 16384, EffortLevel.Ultra));
    }

    [Fact]
    public void ManagedSubscriptionHoldsAgentStepsAtHigh()
    {
        Assert.Equal(60, EffortPolicy.CapManagedAgentSteps(128, 60, isManagedSubscription: true));
        Assert.Equal(36, EffortPolicy.CapManagedAgentSteps(36, 60, isManagedSubscription: true));
        Assert.Equal(128, EffortPolicy.CapManagedAgentSteps(128, 60, isManagedSubscription: false));
    }

    [Fact]
    public void MissingServerUsageBecomesAMarkedEstimate()
    {
        OpenRouterTokenUsage? usage = OpenRouterTokenUsageParser.Estimate(1200, 401);
        Assert.NotNull(usage);
        Assert.True(usage!.IsEstimated);
        Assert.Equal(1200, usage.PromptTokens);
        Assert.Equal(101, usage.CompletionTokens);
        Assert.Equal(1301, usage.TotalTokens);
        Assert.Null(OpenRouterTokenUsageParser.Estimate(0, 0));
    }
}
