using Malx_AI.ComputerUse;
using System.Net.Http;
using Xunit;

namespace Malx_AI.Tests;

public class ComputerUseInferenceTests
{
    [Fact]
    public async Task ProviderFailureIsAnInterruptionNotACompletedTurn()
    {
        var error = await Assert.ThrowsAsync<ComputerUseInferenceInterruptedException>(() =>
            ComputerUseInference.RunAsync(_ => throw new HttpRequestException("model runner stopped"),
                TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Contains("model runner stopped", error.Message);
    }

    [Fact]
    public async Task TimeoutCancelsAndDrainsOldRequestBeforeRetryIsPossible()
    {
        bool drained = false;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> call = ComputerUseInference.RunAsync(async ct =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { await gate.Task; drained = true; }
            return "unused";
        }, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        await Task.Delay(60);
        Assert.False(call.IsCompleted);
        gate.SetResult();
        await Assert.ThrowsAsync<ComputerUseInferenceInterruptedException>(() => call);
        Assert.True(drained);
    }

    [Fact]
    public async Task UserStopIsNotRetriedAsAProviderFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUseInference.RunAsync(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return ""; }, TimeSpan.FromSeconds(1), cts.Token));
    }

    [Fact]
    public async Task EmptyProviderOutputIsAnInterruptionNotAParseFailure()
    {
        // An empty completion means the provider produced nothing; re-asking it through the
        // action-parse path would burn desktop-control steps without any new evidence.
        var error = await Assert.ThrowsAsync<ComputerUseInferenceInterruptedException>(() =>
            ComputerUseInference.RunAsync(_ => Task.FromResult(" \r\n\t "),
                TimeSpan.FromSeconds(1), CancellationToken.None));
        Assert.Contains("empty response", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ScreenshotDetailLadderShrinksTheRetryPayloadMonotonically()
    {
        int previousEdge = int.MaxValue;
        long previousQuality = long.MaxValue;
        for (int level = 0; level <= ComputerUseImageGeometry.MaxDetailLevel; level++)
        {
            int edge = ComputerUseImageGeometry.MaxEdgeForDetailLevel(level);
            long quality = ComputerUseImageGeometry.JpegQualityForDetailLevel(level);
            Assert.True(edge < previousEdge, $"Detail level {level} must be smaller than the level before it.");
            Assert.True(quality < previousQuality, $"Detail level {level} must be cheaper than the level before it.");
            previousEdge = edge;
            previousQuality = quality;
        }

        Assert.True(ComputerUseImageGeometry.CanReduceDetail(0));
        Assert.False(ComputerUseImageGeometry.CanReduceDetail(ComputerUseImageGeometry.MaxDetailLevel));
        // Out-of-range levels must clamp rather than throw: the ladder is driven by a running
        // failure count, not by a validated input.
        Assert.Equal(ComputerUseImageGeometry.MaxEdgeForDetailLevel(ComputerUseImageGeometry.MaxDetailLevel),
            ComputerUseImageGeometry.MaxEdgeForDetailLevel(99));
        Assert.Equal(ComputerUseImageGeometry.MaxEdgeForDetailLevel(0),
            ComputerUseImageGeometry.MaxEdgeForDetailLevel(-4));
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(1920, 1080)]
    public void ReducedDetailKeepsClickCoordinatesOnTarget(int width, int height)
    {
        // Degrading the screenshot must not degrade aiming: every ladder level has to map back
        // to the same physical point, or recovery would trade a dead session for misplaced clicks.
        for (int level = 0; level <= ComputerUseImageGeometry.MaxDetailLevel; level++)
        {
            var size = ComputerUseImageGeometry.GetSizeForDetailLevel(width, height, level);
            Assert.InRange(Math.Max(size.Width, size.Height), 1, ComputerUseImageGeometry.MaxEdgeForDetailLevel(level));

            var targets = ComputerUseImageGeometry.ScaleTargets(
                [new() { Id = "a", ImageX = width / 2, ImageY = height / 2 }],
                width, height, size.Width, size.Height);
            var capture = new ComputerUseCapture
            {
                JpegBytes = [1], ScreenX = -100, ScreenY = 20, ScreenWidth = width, ScreenHeight = height,
                ImageWidth = size.Width, ImageHeight = size.Height, DetailLevel = level
            };
            var point = ComputerUseCoordinateMapper.MapToScreen(capture, targets[0].ImageX, targets[0].ImageY);
            int tolerance = (int)Math.Ceiling(width / (double)size.Width) + 2;
            Assert.InRange(Math.Abs(point.ScreenX - (width / 2 - 100)), 0, tolerance);
            Assert.InRange(Math.Abs(point.ScreenY - (height / 2 + 20)), 0, tolerance);
        }
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(2160, 3840)]
    [InlineData(1024, 768)]
    public void BoundedImageKeepsAccessibleTargetCoordinatesAligned(int width, int height)
    {
        var size = ComputerUseImageGeometry.GetSize(width, height);
        Assert.InRange(Math.Max(size.Width, size.Height), 1, 1280);
        var targets = ComputerUseImageGeometry.ScaleTargets([new() { Id = "a", ImageX = width / 2, ImageY = height / 2 }],
            width, height, size.Width, size.Height);
        var capture = new ComputerUseCapture { JpegBytes = [1], ScreenX = -100, ScreenY = 20,
            ScreenWidth = width, ScreenHeight = height, ImageWidth = size.Width, ImageHeight = size.Height };
        var point = ComputerUseCoordinateMapper.MapToScreen(capture, targets[0].ImageX, targets[0].ImageY);
        Assert.InRange(Math.Abs(point.ScreenX - (width / 2 - 100)), 0, 3);
        Assert.InRange(Math.Abs(point.ScreenY - (height / 2 + 20)), 0, 3);
    }
}
