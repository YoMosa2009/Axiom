using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// The session panel showed two contradictory lines at once: "Paint is not visible in the
/// foreground. Observed window: explorer: unknown" next to "The Paint application is open and
/// visible in the foreground". Both were reporting honestly — the controller was asking whether
/// Paint held keyboard FOCUS at the instant of capture, which is a different question from whether
/// Paint is on screen. Hiding the session panel just before the screenshot moves focus, and Windows
/// routes it through the desktop shell on the way.
/// </summary>
public class ComputerUseWindowPresenceTests
{
    private static ComputerUseCapture Capture(string foregroundProcess, string foregroundTitle, params string[] visible) => new()
    {
        JpegBytes = [1],
        ForegroundProcessName = foregroundProcess,
        ForegroundWindowTitle = foregroundTitle,
        VisibleWindows = visible
    };

    [Fact]
    public void AnOpenApplicationCountsEvenWhileFocusIsPassingThroughTheShell()
    {
        // The exact reported state.
        ComputerUseCapture capture = Capture("explorer", "", "mspaint: Untitled - Paint");
        Assert.True(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Paint", capture));
    }

    [Fact]
    public void TheForegroundWindowStillCountsOnItsOwn()
    {
        // The ordinary case must keep working with no visible-window list at all.
        ComputerUseCapture capture = Capture("mspaint", "Untitled - Paint");
        Assert.True(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Paint", capture));
    }

    [Fact]
    public void AnApplicationThatIsNotRunningIsStillReportedAsAbsent()
    {
        // The guard has to keep guarding: presence is evidence, not an assumption.
        ComputerUseCapture capture = Capture("explorer", "", "msedge: YouTube - Microsoft Edge", "notepad: Untitled - Notepad");
        Assert.False(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Paint", capture));
    }

    [Fact]
    public void AnEmptyDesktopReportsNothingVisible()
    {
        Assert.False(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Paint", Capture("explorer", "")));
        Assert.False(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Paint", null));
        Assert.False(ComputerUseApplicationVerification.IsRequestedApplicationVisible("", Capture("mspaint", "Untitled - Paint")));
    }

    [Theory]
    // The name the user says rarely matches the process name exactly.
    [InlineData("Paint", "mspaint: Untitled - Paint")]
    [InlineData("Microsoft Edge", "msedge: YouTube - Microsoft Edge")]
    [InlineData("Notepad", "notepad: Untitled - Notepad")]
    [InlineData("File Explorer", "explorer: Documents - File Explorer")]
    public void AnApplicationIsRecognisedFromEitherItsProcessOrItsTitle(string requested, string visibleWindow)
    {
        ComputerUseCapture capture = Capture("explorer", "", visibleWindow);
        Assert.True(ComputerUseApplicationVerification.IsRequestedApplicationVisible(requested, capture));
    }

    [Fact]
    public void ALaunchOutcomeCompletesOnceItsApplicationIsOnScreen()
    {
        // End to end: the contract's launch outcome no longer waits for focus to settle before it
        // will admit the application is open.
        const string request = "open the paint app";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open the paint app","description":"Open the Paint application","kind":"desktop","destination":"","match":"","separate_tab":false,"application":"Paint","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);

        ComputerUseCapture capture = Capture("explorer", "", "mspaint: Untitled - Paint");
        Assert.Contains("[OUTCOME VERIFIED]", contract!.ObserveCurrentOutcome(capture));
        Assert.True(contract.Complete);
    }
}
