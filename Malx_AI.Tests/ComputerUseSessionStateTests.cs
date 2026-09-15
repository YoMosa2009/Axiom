using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

public class ComputerUseSessionStateTests
{
    private static ComputerUseCapture Capture(int tabs = 1, string address = "", bool focused = false, bool error = false)
        => new() { JpegBytes = [1], ForegroundProcessName = "msedge", ForegroundWindowTitle = "Browser",
            BrowserState = new() { HasTabTelemetry = true, TabTitles = Enumerable.Repeat("Tab", tabs).ToArray(),
                Address = address, DocumentAddress = address, AddressHasFocus = focused, IsErrorPage = error } };

    [Fact]
    public void DelayedTabObservationRetainsBaselineAndClearsRecovery()
    {
        int? baseline = null;
        string? typed = null, url = null;
        bool recovery = false;
        var progress = new ComputerUseTaskProgress();
        ComputerUseSessionController.TrackBrowserIntent(new() { Type = ComputerUseActionType.Key, Keys = "Ctrl+T" }, Capture(), ref baseline, ref typed, ref url);
        ComputerUseSessionController.ReconcileBrowserState(Capture(), ref baseline, ref url, ref recovery, ref progress);
        // No contract outcome requires a separate tab here, so an unconfirmed tab must not lock
        // navigation down — but the baseline is kept so a delayed observation can still confirm it.
        Assert.False(recovery);
        Assert.Equal(1, baseline);
        ComputerUseSessionController.ReconcileBrowserState(Capture(tabs: 2), ref baseline, ref url, ref recovery, ref progress);
        Assert.False(recovery);
        Assert.Null(baseline);
        Assert.Single(progress.Completed);
    }

    [Fact]
    public void DelayedNavigationRetainsExactUrlUntilLoadedAndReleasesTrustRestriction()
    {
        const string destination = "https://example.com/project";
        int? baseline = null;
        string? url = destination;
        bool recovery = false;
        var progress = new ComputerUseTaskProgress { Current = "Visit project", Next = "Find video" };
        ComputerUseSessionController.ReconcileBrowserState(Capture(address: "https://example.com/old"), ref baseline, ref url, ref recovery, ref progress);
        Assert.Equal(destination, url);
        ComputerUseSessionController.ReconcileBrowserState(Capture(address: destination, error: true), ref baseline, ref url, ref recovery, ref progress);
        Assert.Empty(progress.Completed);
        Assert.Equal(destination, url);
        ComputerUseSessionController.ReconcileBrowserState(Capture(address: destination), ref baseline, ref url, ref recovery, ref progress);
        Assert.Null(url);
        Assert.True(ComputerUseSessionController.HasVerifiedNavigationContext(ComputerUseTrustedNavigation.CreateContext(destination), progress));
        Assert.False(ComputerUseTrustedNavigation.IsVerifiedDestinationAction(ComputerUseTrustedNavigation.CreateContext(destination), true, "https://example.org/video", out _));
        // Loading a URL alone must not complete a broader goal such as playing a video.
        Assert.Equal("Visit project", progress.Current);
    }

    [Fact]
    public void PlanCannotBeReplacedAndAllLaterOutcomesSurviveAdvancement()
    {
        var progress = ComputerUseSessionController.MergeTaskProgress(new(), new() {
            Current = "Create document", Next = "Save document", Remaining = ["Open other app", "Upload document"] });
        progress = ComputerUseSessionController.AddVerifiedCompletion(progress, progress.Current, true);
        progress = ComputerUseSessionController.MergeTaskProgress(progress, new() { Current = "Create document", Completed = ["Invented completion"] });
        Assert.Equal("Save document", progress.Current);
        Assert.Equal("Open other app", progress.Next);
        Assert.Equal(["Upload document"], progress.Remaining);
        Assert.Equal(["Create document"], progress.Completed);
        progress = ComputerUseSessionController.AddVerifiedCompletion(progress, progress.Current, true);
        progress = ComputerUseSessionController.AddVerifiedCompletion(progress, progress.Current, true);
        progress = ComputerUseSessionController.AddVerifiedCompletion(progress, progress.Current, true);
        Assert.Empty(progress.Current);
        Assert.Empty(progress.Next);
        Assert.Equal(4, progress.Completed.Count);
    }

    [Fact]
    public void TextInEditorDoesNotCreatePendingBrowserNavigation()
    {
        int? baseline = null;
        string? typed = null, url = null;
        ComputerUseSessionController.TrackBrowserIntent(new() { Type = ComputerUseActionType.Type, Text = "https://example.com" }, Capture(), ref baseline, ref typed, ref url);
        Assert.Null(typed);
        ComputerUseSessionController.TrackBrowserIntent(new() { Type = ComputerUseActionType.Type, Text = "a search query" }, Capture(focused: true), ref baseline, ref typed, ref url);
        Assert.Equal("a search query", typed);
        Assert.True(ComputerUseSessionController.IsBlockedBeforeAddressCommit(new() { Type = ComputerUseActionType.Type, Text = "more text" }));
        ComputerUseSessionController.TrackBrowserIntent(new() { Type = ComputerUseActionType.Key, Keys = "Enter" }, Capture(focused: true), ref baseline, ref typed, ref url);
        Assert.Null(typed);
        Assert.Null(url);
    }

    [Fact]
    public void DifferentAccessibleControlsAreNotTheSameZeroCoordinateTarget()
    {
        Assert.False(ComputerUseCoordinateMapper.IsSameTarget(
            new() { Type = ComputerUseActionType.Click, TargetId = "ui1" },
            new() { Type = ComputerUseActionType.Click, TargetId = "ui2" }));
    }

    [Theory]
    [InlineData("{\"thinking\":\"I will click now\"}")]
    [InlineData("{\"action\":{\"type\":\"invented_action\"}}")]
    [InlineData("{\"action\":null}")]
    public void NarrativeOrInvalidActionIsNotAParsedScreenshot(string raw)
        => Assert.False(ComputerUseActionParser.Parse(raw).Parsed);

    [Theory]
    [InlineData("```json\n", false)]
    [InlineData("{\"action_succeeded\":true,\"goal_completed\":true,\"task_completed\":true,\"evidence\":\"\"}", false)]
    [InlineData("{\"action_succeeded\":true,\"goal_completed\":true,\"task_completed\":true,\"evidence\":\"Saved document visible in editor\"}", true)]
    public void VisualAssessmentRequiresStructuredEvidence(string raw, bool accepted)
        => Assert.Equal(accepted, ComputerUseVisualAssessment.Parse(raw).TaskCompleted);

    [Fact]
    public void TypedAddressIsNotLoadedDocumentEvidence()
    {
        Assert.False(ComputerUseBrowserVerification.IsNavigationConfirmed("https://example.com", new() {
            Address = "https://example.com", AddressHasFocus = true }, out _));
        Assert.False(ComputerUseBrowserVerification.IsNavigationConfirmed("https://example.com", new() {
            Address = "https://example.com" }, out _));
        Assert.False(ComputerUseBrowserVerification.IsNewTabConfirmed(-1, Capture(2).BrowserState));
    }

    [Fact]
    public void DelayedApplicationLaunchCanRecoverWithoutLaunchingAgain()
    {
        string? app = "Microsoft Edge";
        bool recovery = false;
        var progress = new ComputerUseTaskProgress();
        ComputerUseSessionController.ReconcileApplicationLaunch(new() { JpegBytes = [1], ForegroundProcessName = "explorer", ForegroundWindowTitle = "Desktop" }, ref app, ref recovery, ref progress);
        Assert.True(recovery);
        Assert.NotNull(app);
        ComputerUseSessionController.ReconcileApplicationLaunch(Capture(), ref app, ref recovery, ref progress);
        Assert.False(recovery);
        Assert.Null(app);
    }
}
