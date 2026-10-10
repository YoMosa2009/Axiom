using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public class LocalModelFitAdvisorTests
{
    private static readonly LocalModelFitCandidate[] Candidates = new[]
    {
        new LocalModelFitCandidate("Qwen3-0.6B", 0.40),
        new LocalModelFitCandidate("Qwen3-1.7B", 1.19),
        new LocalModelFitCandidate("Qwen3-4B", 2.33),
        new LocalModelFitCandidate("Qwen3-8B", 4.68)
    };

    [Fact]
    public void ATwelveGigabyteGraphicsCardGetsTheEightBModel()
    {
        var hw = new LocalModelFitHardware(32, 12, true, 16);
        var index = LocalModelFitAdvisor.ChooseRecommendedIndex(Candidates, hw);
        Assert.Equal(3, index);

        var result = LocalModelFitAdvisor.Assess(Candidates[3], hw);
        Assert.Equal(LocalModelFit.RunsOnGpu, result.Fit);
    }

    [Fact]
    public void AFourGigabyteGraphicsCardGetsTheLargestModelThatFitsOnIt()
    {
        var hw = new LocalModelFitHardware(16, 4, true, 8);
        var index = LocalModelFitAdvisor.ChooseRecommendedIndex(Candidates, hw);
        Assert.Equal(2, index);

        var result = LocalModelFitAdvisor.Assess(Candidates[3], hw);
        Assert.Equal(LocalModelFit.RunsOnCpu, result.Fit);
    }

    [Fact]
    public void AnEightCoreLaptopWithoutAGraphicsCardGetsTheFourBModel()
    {
        var hw = new LocalModelFitHardware(16, 0, false, 8);
        var index = LocalModelFitAdvisor.ChooseRecommendedIndex(Candidates, hw);
        Assert.Equal(2, index);
    }

    [Fact]
    public void AFourCoreLaptopWithoutAGraphicsCardGetsASmallModel()
    {
        var hw = new LocalModelFitHardware(16, 0, false, 4);
        var index = LocalModelFitAdvisor.ChooseRecommendedIndex(Candidates, hw);
        Assert.Equal(1, index);
    }

    [Fact]
    public void ALowMemoryComputerStillGetsTheSmallestModel()
    {
        var hw = new LocalModelFitHardware(2, 0, false, 2);
        var index = LocalModelFitAdvisor.ChooseRecommendedIndex(Candidates, hw);
        Assert.Equal(0, index);

        var result0 = LocalModelFitAdvisor.Assess(Candidates[0], hw);
        Assert.Equal(LocalModelFit.RunsOnCpu, result0.Fit);

        var result1 = LocalModelFitAdvisor.Assess(Candidates[1], hw);
        Assert.Equal(LocalModelFit.TooLarge, result1.Fit);
    }

    [Fact]
    public void TheEightBModelIsTooLargeForEightGigabytesOfMemory()
    {
        var hw = new LocalModelFitHardware(8, 0, false, 8);
        var result = LocalModelFitAdvisor.Assess(Candidates[3], hw);
        Assert.Equal(LocalModelFit.TooLarge, result.Fit);
        Assert.Equal("Too large for this computer.", result.Summary);
    }

    [Fact]
    public void AnEmptyListHasNoRecommendation()
    {
        var hw = new LocalModelFitHardware(16, 8, true, 8);
        var index = LocalModelFitAdvisor.ChooseRecommendedIndex(Array.Empty<LocalModelFitCandidate>(), hw);
        Assert.Equal(-1, index);
    }

    [Fact]
    public void EstimateMemoryNeededGbAddsOverhead()
    {
        Assert.Equal(2.9, LocalModelFitAdvisor.EstimateMemoryNeededGb(2.0), 3);
    }
}
