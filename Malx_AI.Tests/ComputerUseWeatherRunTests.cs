using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Replays the run of "Open microsoft edge, then take me to any weather app, and then after that,
/// take me to youtube". Edge launched, the weather search loaded, and the session then died at
/// step 12 with "[TAB RECOVERY REQUIRED]" — for a plan in which no outcome asked for a new tab.
/// </summary>
public class ComputerUseWeatherRunTests
{
    private const string Request = "Open microsoft edge, then take me to any weather app, and then after that, take me to youtube.";

    // Verbatim from backend-events.log, ComputerUse.Plan at 2026-09-07 12:19:51.
    private const string Plan = """
        {"error":"","outcomes":[
          {"request_text":"Open microsoft edge","description":"Open the Microsoft Edge browser.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Microsoft Edge","required_text":[]},
          {"request_text":"take me to any weather app","description":"Navigate to a weather application or website.","kind":"browser","destination":"https://www.google.com/search?q=weather","match":"site","separate_tab":false,"application":"","required_text":[]},
          {"request_text":"take me to youtube","description":"Navigate to the YouTube website.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":[]}
        ]}
        """;

    // The observer's actual reply that step: prose agreeing the outcome is met, boolean saying no.
    private static readonly ComputerUseVisualAssessment ObserverContradictsItself = new()
    {
        ActionSucceeded = true, GoalCompleted = false, TaskCompleted = false,
        Evidence = "The browser is currently on a Google search results page for 'weather', which satisfies the requirement to navigate to a weather application/website."
    };

    private static ComputerUseCapture Edge(string loaded, string activeTab, params string[] tabs) => new()
    {
        JpegBytes = [1],
        TargetWindowHandle = new IntPtr(55),
        ForegroundProcessName = "msedge",
        ForegroundWindowTitle = "Microsoft Edge",
        BrowserState = new()
        {
            DocumentAddress = loaded, Address = loaded, ActiveTabId = activeTab,
            TabIds = tabs, TabTitles = tabs, HasTabTelemetry = true
        }
    };

    private static ComputerUseTaskContract Parse()
    {
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out ComputerUseTaskContract? contract, out string error), error);
        return contract!;
    }

    [Fact]
    public void TheSearchDestinationsOwnSubjectIsNotAnExtraCriterion()
    {
        // "weather" is the destination — it is the ?q= value — so the outcome is finished by
        // arriving, and never has to be put to the observer at all.
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        Assert.Equal("Navigate to a weather application or website.", contract.Progress([]).Current);

        string evidence = contract.ObserveCurrentOutcome(Edge("https://www.google.com/search?q=weather", "tab1", "tab1"));
        Assert.Contains("[OUTCOME VERIFIED]", evidence);
        Assert.Equal("Navigate to the YouTube website.", contract.Progress([]).Current);
    }

    [Fact]
    public void TheRunCompletesDespiteTheObserverContradictingItsOwnEvidence()
    {
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://www.google.com/search?q=weather", "tab1", "tab1"));

        // No new tab was requested by this plan, so YouTube in the same tab finishes it.
        Assert.True(contract.TryComplete(Edge("https://www.youtube.com/", "tab1", "tab1"), ObserverContradictsItself, out _));
        Assert.True(contract.Complete);
    }

    [Fact]
    public void AModelInitiatedTabDoesNotLockNavigationDownWhenNoOutcomeNeedsOne()
    {
        // The session-ending bug: the agent opened a tab on its own while stuck, the count check
        // did not confirm it, and the recovery lock then blocked every navigation until the run
        // ended with five turns without desktop input.
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));

        int? pending = 1;
        string? pendingUrl = null;
        bool recoveryRequired = false;
        var progress = new ComputerUseTaskProgress();
        // Ctrl+T pressed, but the count is unchanged.
        var capture = Edge("https://www.google.com/search?q=weather", "tab1", "tab1");

        string observation = ComputerUseSessionController.ReconcileBrowserState(
            capture, ref pending, ref pendingUrl, ref recoveryRequired, ref progress, contract);

        Assert.Contains("[TAB NOT OBSERVED]", observation);
        Assert.DoesNotContain("[TAB VERIFICATION FAILED]", observation);
        Assert.False(recoveryRequired);
        // The baseline is retained so a tab that shows up a step later can still be counted; it
        // simply no longer gates anything.
        Assert.Equal(1, pending);
    }

    [Fact]
    public void AnOutcomeThatDoesRequireANewTabStillLocksDownOnFailure()
    {
        // The barrier still exists where it belongs: an outcome that genuinely asked for a separate
        // tab must not be satisfied by navigating the existing one.
        const string request = "take me to github, then in a new tab take me to youtube";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"take me to github","description":"Open GitHub.","kind":"browser","destination":"https://github.com","match":"site","separate_tab":false,"application":"","required_text":[]},
              {"request_text":"then in a new tab take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);
        contract!.ObserveCurrentOutcome(Edge("https://github.com/", "tab1", "tab1"));
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);

        int? pending = 1;
        string? pendingUrl = null;
        bool recoveryRequired = false;
        var progress = new ComputerUseTaskProgress();

        string observation = ComputerUseSessionController.ReconcileBrowserState(
            Edge("https://github.com/", "tab1", "tab1"), ref pending, ref pendingUrl, ref recoveryRequired, ref progress, contract);

        Assert.Contains("[TAB VERIFICATION FAILED]", observation);
        Assert.True(recoveryRequired);
    }

    [Fact]
    public void MissingTabTelemetryNeverLocksTheSessionDown()
    {
        // The old message said "do not treat this as a failed tab action" while setting the very
        // flag that treats it as one.
        int? pending = 1;
        string? pendingUrl = null;
        bool recoveryRequired = true;
        var progress = new ComputerUseTaskProgress();
        var capture = new ComputerUseCapture
        {
            JpegBytes = [1],
            BrowserState = new ComputerUseBrowserState { Address = "https://www.google.com/", HasTabTelemetry = false }
        };

        string observation = ComputerUseSessionController.ReconcileBrowserState(
            capture, ref pending, ref pendingUrl, ref recoveryRequired, ref progress);

        Assert.Contains("[TAB OBSERVATION UNAVAILABLE]", observation);
        Assert.False(recoveryRequired);
        Assert.Null(pending);
    }

    [Theory]
    // A search destination whose query carries the subject is still finished by arriving.
    [InlineData("take me to any weather app", "https://www.google.com/search?q=weather", false)]
    [InlineData("show me a news site", "https://news.google.com", false)]
    [InlineData("take me to some music service", "https://open.spotify.com", false)]
    // Asking for something to be done there still needs the observer.
    [InlineData("search youtube for lofi and play the first result", "https://www.youtube.com", true)]
    [InlineData("take me to any weather app and screenshot the forecast", "https://www.google.com/search?q=weather", true)]
    public void ArrivalVersusExtraWorkIsJudgedFromTheRequest(string requestText, string destination, bool needsObserver)
    {
        string plan = $$"""
            {"error":"","outcomes":[
              {"request_text":"{{requestText}}","description":"Planner phrasing.","kind":"browser","destination":"{{destination}}","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, requestText, out ComputerUseTaskContract? contract, out string error), error);

        string evidence = contract!.ObserveCurrentOutcome(Edge(destination, "tab1", "tab1"));
        if (needsObserver)
        {
            Assert.Contains("[OUTCOME DESTINATION REACHED]", evidence);
            Assert.False(contract.Complete);
        }
        else
        {
            Assert.Contains("[OUTCOME VERIFIED]", evidence);
            Assert.True(contract.Complete);
        }
    }
}
