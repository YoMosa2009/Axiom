using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Two observed failures from one run of "open Edge, take me to YouTube, then in a new tab take me
/// to github.com": YouTube was loaded and on screen while the controller reported the destination
/// unreached, and a working Ctrl+T was repeatedly reported as having produced no new tab, which
/// drove the agent to open four tabs for a two-tab request.
/// </summary>
public class ComputerUseNavigationAndTabTests
{
    private static ComputerUseBrowserState Browser(string loaded, bool addressFocused, params string[] tabs) => new()
    {
        DocumentAddress = loaded,
        Address = loaded,
        AddressHasFocus = addressFocused,
        ActiveTabId = tabs.Length > 0 ? tabs[^1] : "",
        TabIds = tabs,
        TabTitles = tabs,
        HasTabTelemetry = tabs.Length > 0
    };

    [Fact]
    public void APageThatHasLoadedIsConfirmedEvenWithTheCaretStillInTheAddressBar()
    {
        // A browser routinely keeps keyboard focus in the address bar after navigating from it.
        // Only the LOADED document is compared, so focus says nothing about whether the page is up.
        var state = Browser("https://www.youtube.com/", addressFocused: true, "tab1");

        Assert.True(ComputerUseBrowserVerification.IsNavigationConfirmed("https://www.youtube.com/", state, out string reason), reason);
    }

    [Fact]
    public void AnAddressTypedButNotYetLoadedIsStillNotConfirmed()
    {
        // The guard that actually matters: the address bar reads YouTube, the loaded document is
        // still the new-tab page. That must not confirm.
        var state = new ComputerUseBrowserState
        {
            Address = "https://www.youtube.com",
            DocumentAddress = "https://ntp.msn.com/edge/ntp",
            AddressHasFocus = true,
            TabIds = ["tab1"], TabTitles = ["tab1"], HasTabTelemetry = true
        };

        Assert.False(ComputerUseBrowserVerification.IsNavigationConfirmed("https://www.youtube.com/", state, out _));
    }

    [Fact]
    public void ANavigationWithNoLoadedDocumentYetIsStillPending()
    {
        var state = new ComputerUseBrowserState
        {
            Address = "https://www.youtube.com", DocumentAddress = "",
            TabIds = ["tab1"], TabTitles = ["tab1"], HasTabTelemetry = true
        };

        Assert.False(ComputerUseBrowserVerification.IsNavigationConfirmed("https://www.youtube.com/", state, out string reason));
        Assert.Contains("No loaded document", reason);
    }

    [Fact]
    public void AConfirmedNewTabClearsTheRequirement()
    {
        int? pending = 1;
        string? pendingUrl = null;
        bool recoveryRequired = true;
        var progress = new ComputerUseTaskProgress();
        var capture = new ComputerUseCapture { JpegBytes = [1], BrowserState = Browser("", false, "tab1", "tab2") };

        string observation = ComputerUseSessionController.ReconcileBrowserState(
            capture, ref pending, ref pendingUrl, ref recoveryRequired, ref progress);

        Assert.Contains("[BROWSER VERIFIED]", observation);
        Assert.Null(pending);
        Assert.False(recoveryRequired);
    }

    [Fact]
    public void AGenuineFailureToOpenARequiredTabIsStillReportedAsAFailure()
    {
        // Baseline known, count unchanged: Ctrl+T really did nothing. The lock-down still applies,
        // but only because THIS outcome asked for a separate tab.
        const string request = "take me to github, then in a new tab take me to youtube";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"take me to github","description":"Open GitHub.","kind":"browser","destination":"https://github.com","match":"site","separate_tab":false,"application":"","required_text":[]},
              {"request_text":"then in a new tab take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);
        contract!.ObserveCurrentOutcome(new ComputerUseCapture
        {
            JpegBytes = [1], ForegroundProcessName = "msedge",
            BrowserState = Browser("https://github.com/", false, "tab1")
        });

        int? pending = 2;
        string? pendingUrl = null;
        bool recoveryRequired = false;
        var progress = new ComputerUseTaskProgress();
        var capture = new ComputerUseCapture { JpegBytes = [1], BrowserState = Browser("", false, "tab1", "tab2") };

        string observation = ComputerUseSessionController.ReconcileBrowserState(
            capture, ref pending, ref pendingUrl, ref recoveryRequired, ref progress, contract);

        Assert.Contains("[TAB VERIFICATION FAILED]", observation);
        Assert.True(recoveryRequired);
    }

    [Fact]
    public void AnUnknownTabBaselineIsInconclusiveRatherThanAPermanentFailure()
    {
        // This is the loop that produced four tabs. With no "before" count, the count check can
        // neither prove nor disprove the new tab -- but it used to report a hard failure, which
        // switched on the recovery path whose ONLY permitted action is opening another tab, which
        // landed here again, forever.
        int? pending = -1;
        string? pendingUrl = null;
        bool recoveryRequired = true;
        var progress = new ComputerUseTaskProgress();
        var capture = new ComputerUseCapture { JpegBytes = [1], BrowserState = Browser("", false, "tab1", "tab2", "tab3") };

        string observation = ComputerUseSessionController.ReconcileBrowserState(
            capture, ref pending, ref pendingUrl, ref recoveryRequired, ref progress);

        Assert.Contains("[TAB OBSERVATION INCONCLUSIVE]", observation);
        Assert.DoesNotContain("[TAB VERIFICATION FAILED]", observation);
        Assert.Null(pending);
        Assert.False(recoveryRequired);
    }

    [Fact]
    public void TheMostRecentTabCountIsUsedWhenThisCaptureHasNoTelemetry()
    {
        // Telemetry drops out often enough (window not yet foreground, UIA tree still building)
        // that falling straight to "no baseline" is what stranded the check in the first place.
        int? pending = null;
        string? typed = null;
        string? pendingUrl = null;
        var capture = new ComputerUseCapture
        {
            JpegBytes = [1],
            BrowserState = new ComputerUseBrowserState { Address = "https://www.youtube.com", HasTabTelemetry = false }
        };

        ComputerUseSessionController.TrackBrowserIntent(
            new ComputerUseAction { Type = ComputerUseActionType.Key, Keys = "Ctrl+T" },
            capture, ref pending, ref typed, ref pendingUrl, addressEntryExpected: false, lastKnownTabCount: 2);

        Assert.Equal(2, pending);

        // And that baseline then verifies a real third tab.
        bool recoveryRequired = false;
        var progress = new ComputerUseTaskProgress();
        var after = new ComputerUseCapture { JpegBytes = [1], BrowserState = Browser("", false, "tab1", "tab2", "tab3") };
        string observation = ComputerUseSessionController.ReconcileBrowserState(
            after, ref pending, ref pendingUrl, ref recoveryRequired, ref progress);

        Assert.Contains("[BROWSER VERIFIED]", observation);
        Assert.Null(pending);
    }

    [Fact]
    public void ADestinationOutcomeLatchesWhileTheAddressBarStillHasFocus()
    {
        // End to end on the contract: the reported symptom was "it thinks I'm not on YouTube but
        // it already took me there".
        const string request = "take me to youtube";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"take me to youtube","description":"Open YouTube.","kind":"browser","destination":"https://www.youtube.com","match":"exact","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);

        var capture = new ComputerUseCapture
        {
            JpegBytes = [1],
            TargetWindowHandle = new IntPtr(9),
            ForegroundProcessName = "msedge",
            BrowserState = Browser("https://www.youtube.com/", addressFocused: true, "tab1")
        };

        Assert.Contains("[OUTCOME VERIFIED]", contract!.ObserveCurrentOutcome(capture));
        Assert.True(contract.Complete);
    }
}
