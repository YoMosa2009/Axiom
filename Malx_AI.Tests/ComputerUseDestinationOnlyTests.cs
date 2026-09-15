using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Replays the run of "open microsoft edge, take me to github, and then make a new tab and take me
/// to youtube". Edge launched and GitHub loaded, but outcome 2 was refused at steps 4, 7 and 9 and
/// the contract never left it: the planner marked a plain "take me to github" as site-match and
/// described it as "the GitHub homepage", so the observer refused it against a repository page on
/// the same site — and the agent was told to navigate to GitHub over and over.
/// </summary>
public class ComputerUseDestinationOnlyTests
{
    private const string Request = "I want you to open microsoft edge, take me to github, and then i want you to make a new tab and take me to youtube.";

    // Verbatim from backend-events.log, ComputerUse.Plan at 2026-09-07 11:45:05.
    private const string Plan = """
        {"error":"","outcomes":[
          {"request_text":"open microsoft edge","description":"Open the Microsoft Edge browser.","kind":"desktop","destination":"","match":"","separate_tab":false,"application":"microsoft edge","required_text":[]},
          {"request_text":"take me to github","description":"Navigate to the GitHub homepage.","kind":"browser","destination":"https://github.com","match":"site","separate_tab":false,"application":"","required_text":[]},
          {"request_text":"make a new tab and take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
        ]}
        """;

    // The observer's actual verdict from that run.
    private static readonly ComputerUseVisualAssessment ObserverRefuses = new()
    {
        ActionSucceeded = true, GoalCompleted = false, TaskCompleted = false,
        Evidence = "The current page is a specific GitHub repository (YoMosa2009/Axiom), not the GitHub homepage."
    };

    private static ComputerUseCapture Edge(string loaded, string activeTab, params string[] tabs) => new()
    {
        JpegBytes = [1],
        TargetWindowHandle = new IntPtr(77),
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
    public void ReachingTheSiteFinishesAPlainTakeMeToRequest()
    {
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        Assert.Equal("Navigate to the GitHub homepage.", contract.Progress([]).Current);

        // A repository page on github.com. The user asked to be taken to github; they are there.
        string evidence = contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));
        Assert.Contains("[OUTCOME VERIFIED]", evidence);
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);
    }

    [Fact]
    public void TheWholeRunCompletesThoughTheObserverRefusesEveryCheck()
    {
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));

        ComputerUseCapture youtubeInNewTab = Edge("https://www.youtube.com/", "tab2", "tab1", "tab2");
        Assert.Contains("[OUTCOME VERIFIED]", contract.ObserveCurrentOutcome(youtubeInNewTab));
        Assert.True(contract.Complete);

        // Never needed the observer at any point.
        Assert.False(ObserverRefuses.GoalCompleted);
    }

    [Fact]
    public void GoingBackToAFinishedDestinationIsRefused()
    {
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));

        ComputerUseCapture newTab = Edge("", "tab2", "tab1", "tab2");
        bool addressEntry = ComputerUseSessionController.UpdateAddressEntryIntent(
            false, new() { Type = ComputerUseActionType.Key, Keys = "Ctrl+L" }, newTab);

        Assert.True(contract.BlocksAction(
            new() { Type = ComputerUseActionType.Type, Text = "https://github.com/" }, newTab, out string reason, addressEntry));
        Assert.Contains("already complete", reason);
    }

    [Theory]
    // Arriving IS the whole request.
    [InlineData("take me to github", "https://github.com", false)]
    [InlineData("make a new tab and take me to youtube", "https://www.youtube.com", false)]
    [InlineData("take me to my github repo 'Axiom'", "https://github.com/YoMosa2009/Axiom", false)]
    [InlineData("take me to a website called 'Neal.fun'", "https://neal.fun", false)]
    [InlineData("open youtube in a new tab", "https://www.youtube.com", false)]
    // The request asks for something arriving does not accomplish.
    [InlineData("play the lofi hip hop video on youtube", "https://www.youtube.com", true)]
    [InlineData("search for cats on youtube", "https://www.youtube.com", true)]
    [InlineData("go to youtube and subscribe to the channel", "https://www.youtube.com", true)]
    [InlineData("open github and star the repository", "https://github.com", true)]
    public void WhetherArrivingCompletesTheOutcomeFollowsTheUsersOwnWords(string requestText, string destination, bool needsObserver)
    {
        string plan = $$"""
            {"error":"","outcomes":[
              {"request_text":"{{requestText}}","description":"Some planner phrasing.","kind":"browser","destination":"{{destination}}","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, requestText, out ComputerUseTaskContract? contract, out string error), error);

        var capture = new ComputerUseCapture
        {
            JpegBytes = [1],
            TargetWindowHandle = new IntPtr(77),
            ForegroundProcessName = "msedge",
            BrowserState = new()
            {
                DocumentAddress = destination + "/", Address = destination,
                ActiveTabId = "tab1", TabIds = ["tab1"], TabTitles = ["tab1"], HasTabTelemetry = true
            }
        };

        string evidence = contract!.ObserveCurrentOutcome(capture);
        if (needsObserver)
        {
            // Reaching the site is only the precondition; the rest still has to be judged.
            Assert.Contains("[OUTCOME DESTINATION REACHED]", evidence);
            Assert.False(contract.Complete);
        }
        else
        {
            Assert.Contains("[OUTCOME VERIFIED]", evidence);
            Assert.True(contract.Complete);
        }
    }

    [Fact]
    public void AnOutcomeWithRequiredTextAlwaysNeedsMoreThanArriving()
    {
        const string request = "go to example.com and type hello there";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"go to example.com and type hello there","description":"Type on the page.","kind":"browser","destination":"https://example.com","match":"site","separate_tab":false,"application":"","required_text":["hello there"]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);

        var capture = new ComputerUseCapture
        {
            JpegBytes = [1],
            ForegroundProcessName = "msedge",
            BrowserState = new()
            {
                DocumentAddress = "https://example.com/", Address = "https://example.com/",
                ActiveTabId = "tab1", TabIds = ["tab1"], TabTitles = ["tab1"], HasTabTelemetry = true
            }
        };

        Assert.Equal("", contract!.ObserveCurrentOutcome(capture));
        Assert.False(contract.Complete);
    }
}
