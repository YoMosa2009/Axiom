using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// The session panel used to show the controller's raw model-facing strings — a column of shouted
/// brackets. These cover the reading of those tags for display; the strings themselves are the
/// model's contract and are never rewritten.
/// </summary>
public class ComputerUseLogFormattingTests
{
    [Theory]
    // Real observations, verbatim from the controller.
    [InlineData("[OUTCOME VERIFIED] Open the Microsoft Edge browser.", "Verified", ComputerUseLogSeverity.Verified)]
    [InlineData("[BROWSER VERIFIED] New tab count is 2.", "Verified", ComputerUseLogSeverity.Verified)]
    [InlineData("[APP LAUNCH VERIFIED] Microsoft Edge is the foreground window.", "Verified", ComputerUseLogSeverity.Verified)]
    [InlineData("[OUTCOME DESTINATION REACHED] youtube.com is loaded.", "Reached", ComputerUseLogSeverity.Verified)]
    [InlineData("[ACTION NOT SENT] Target id 'none' is stale or unavailable.", "Blocked", ComputerUseLogSeverity.Blocked)]
    [InlineData("[TAB VERIFICATION FAILED] Ctrl+T did not produce a tab.", "Blocked", ComputerUseLogSeverity.Blocked)]
    [InlineData("[OUTCOME DESTINATION BLOCK] Current outcome requires github.com.", "Blocked", ComputerUseLogSeverity.Blocked)]
    [InlineData("[NAVIGATION VERIFICATION PENDING] No loaded document URL yet.", "Waiting", ComputerUseLogSeverity.Waiting)]
    [InlineData("[TAB OBSERVATION UNAVAILABLE] No tab-count telemetry.", "Waiting", ComputerUseLogSeverity.Waiting)]
    [InlineData("[COMPLETION NOT VERIFIED] The full request is not supported by fresh evidence.", "Waiting", ComputerUseLogSeverity.Waiting)]
    [InlineData("[OUTCOME STALLED] The destination is confirmed loaded.", "Attention", ComputerUseLogSeverity.Attention)]
    [InlineData("[INFERENCE INTERRUPTED] The model returned an empty response.", "Attention", ComputerUseLogSeverity.Attention)]
    [InlineData("[PLAN WARNING] Proceeding with the closest usable plan.", "Attention", ComputerUseLogSeverity.Attention)]
    [InlineData("[VISUAL ASSESSMENT] The current page is weather.com.", "Observed", ComputerUseLogSeverity.Observed)]
    [InlineData("[POST-ACTION SCREENSHOT] The screen changed after Key Enter.", "Observed", ComputerUseLogSeverity.Observed)]
    [InlineData("[EXECUTION LEDGER] Observed visible change after Key Ctrl+T.", "Progress", ComputerUseLogSeverity.Quiet)]
    public void ATaggedObservationBecomesAStatusWordAndASentence(string line, string expectedBadge, ComputerUseLogSeverity expectedSeverity)
    {
        (string badge, ComputerUseLogSeverity severity, string body) = ComputerUseLogFormatting.Classify(line);

        Assert.Equal(expectedBadge, badge);
        Assert.Equal(expectedSeverity, severity);
        // The sentence survives intact; only the bracket is taken off the front.
        Assert.DoesNotContain("[", body);
        Assert.False(string.IsNullOrWhiteSpace(body));
    }

    [Fact]
    public void AFailureThatMentionsVerificationIsStillAFailure()
    {
        // Ordering matters: "[TAB VERIFICATION FAILED]" contains "VERIFIED"-adjacent wording, and
        // showing it in green would say the opposite of what happened.
        (string badge, ComputerUseLogSeverity severity, _) =
            ComputerUseLogFormatting.Classify("[TAB VERIFICATION FAILED] Ctrl+T did not produce an observed additional tab.");
        Assert.Equal("Blocked", badge);
        Assert.Equal(ComputerUseLogSeverity.Blocked, severity);

        (string notVerified, ComputerUseLogSeverity notVerifiedSeverity, _) =
            ComputerUseLogFormatting.Classify("[COMPLETION NOT VERIFIED] Not supported by fresh evidence.");
        Assert.Equal("Waiting", notVerified);
        Assert.Equal(ComputerUseLogSeverity.Waiting, notVerifiedSeverity);
    }

    [Theory]
    [InlineData("Click left at (204, 14)")]
    [InlineData("The browser is successfully on a weather website.")]
    [InlineData("Monitor 2256x1504 @ (0,0) · image 1280x853")]
    public void ModelNarrationGetsNoBadge(string line)
    {
        // Badging every line rebuilds the same wall of noise in a different font.
        (string badge, ComputerUseLogSeverity severity, string body) = ComputerUseLogFormatting.Classify(line);
        Assert.Equal("", badge);
        Assert.Equal(ComputerUseLogSeverity.Plain, severity);
        Assert.Equal(line, body);
    }

    [Fact]
    public void AnUnrecognisedTagIsShownReadablyRatherThanShouted()
    {
        (string badge, ComputerUseLogSeverity severity, string body) =
            ComputerUseLogFormatting.Classify("[SOMETHING NEW] a future observation");
        Assert.Equal("Something new", badge);
        Assert.Equal(ComputerUseLogSeverity.Quiet, severity);
        Assert.Equal("a future observation", body);
    }

    [Fact]
    public void ATagWithNoSentenceStillReads()
    {
        (string badge, _, string body) = ComputerUseLogFormatting.Classify("[OUTCOME VERIFIED]");
        Assert.Equal("Verified", badge);
        Assert.Equal("OUTCOME VERIFIED", body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void EmptyInputIsHarmless(string? line)
    {
        (string badge, ComputerUseLogSeverity severity, string body) = ComputerUseLogFormatting.Classify(line);
        Assert.Equal("", badge);
        Assert.Equal("", body);
        Assert.Equal(ComputerUseLogSeverity.Plain, severity);
    }
}
