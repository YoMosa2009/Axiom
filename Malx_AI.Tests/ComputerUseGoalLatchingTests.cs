using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Reproduces the observed multi-goal failure: "open Edge, go to my GitHub repo, then open a new
/// tab and go to YouTube" reached GitHub, opened the second tab, and then typed the GitHub URL
/// again instead of advancing. The cause was that a browser outcome could only ever be proven
/// against the ACTIVE tab, so opening the tab the next outcome requires destroyed the evidence for
/// the finished one, stranding the contract on an outcome that was already done.
/// </summary>
public class ComputerUseGoalLatchingTests
{
    private const string Request = "open up microsoft edge, take me to my github repo 'Axiom', then open a new tab and take me to youtube.";
    private const string Plan = """
        {"error":"","outcomes":[
          {"request_text":"open up microsoft edge, take me to my github repo 'Axiom',","description":"Open the GitHub repository in Microsoft Edge.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"Microsoft Edge"},
          {"request_text":"then open a new tab and take me to youtube.","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true}
        ]}
        """;

    private static readonly ComputerUseVisualAssessment ModelSaysComplete = new()
    {
        ActionSucceeded = true, GoalCompleted = true, TaskCompleted = true, Evidence = "The requested page is visible."
    };

    // The model refusing to bless a step must not be able to strand a controller-verifiable goal.
    private static readonly ComputerUseVisualAssessment ModelSaysNotYet = new()
    {
        ActionSucceeded = false, GoalCompleted = false, TaskCompleted = false, Evidence = "The page has not loaded yet."
    };

    private static ComputerUseCapture Edge(string loadedUrl, string activeTab, params string[] tabs) => new()
    {
        JpegBytes = [1],
        TargetWindowHandle = new IntPtr(123),
        ForegroundProcessName = "msedge",
        ForegroundWindowTitle = "Axiom - Microsoft Edge",
        BrowserState = new()
        {
            DocumentAddress = loadedUrl,
            Address = loadedUrl,
            ActiveTabId = activeTab,
            TabIds = tabs,
            TabTitles = tabs,
            HasTabTelemetry = true
        }
    };

    private static ComputerUseTaskContract Parse()
    {
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out ComputerUseTaskContract? contract));
        return contract!;
    }

    [Fact]
    public void ReachedGoalStaysCompleteAfterTheNextTabReplacesTheActiveDocument()
    {
        ComputerUseTaskContract contract = Parse();

        // The repository loads in the first tab. The controller can see this for itself.
        string evidence = contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));
        Assert.Contains("[OUTCOME VERIFIED]", evidence);
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);

        // Ctrl+T. The active document is now the new-tab page and the repository is in the
        // background -- the exact moment the old code lost the first goal.
        ComputerUseCapture newTab = Edge("", "tab2", "tab1", "tab2");
        contract.ObserveCurrentOutcome(newTab);
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);
        Assert.Contains("youtube.com", contract.Describe());
    }

    [Fact]
    public void TheReportedRegressionRetypingTheFirstDestinationIsBlocked()
    {
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));

        ComputerUseCapture newTab = Edge("", "tab2", "tab1", "tab2");
        bool addressEntry = ComputerUseSessionController.UpdateAddressEntryIntent(
            false, new() { Type = ComputerUseActionType.Key, Keys = "Ctrl+L" }, newTab);

        var retypeFirstGoal = new ComputerUseAction { Type = ComputerUseActionType.Type, Text = "https://github.com/YoMosa2009/Axiom" };
        Assert.True(contract.BlocksAction(retypeFirstGoal, newTab, out string reason, addressEntry));
        Assert.Contains("already complete", reason);

        var youtube = new ComputerUseAction { Type = ComputerUseActionType.Type, Text = "https://www.youtube.com" };
        Assert.False(contract.BlocksAction(youtube, newTab, out _, addressEntry));
    }

    [Fact]
    public void AGuessedOwnerIsBlockedBeforeItReachesTheAddressBar()
    {
        // Observed in the same run: the trusted destination is YoMosa2009 but the model typed
        // YoMosa2000. That belongs to no outcome at all, so the old "is it an earlier outcome?"
        // rule let it through and the agent navigated itself to a 404.
        ComputerUseTaskContract contract = Parse();
        ComputerUseCapture tab = Edge("", "tab1", "tab1");
        bool addressEntry = ComputerUseSessionController.UpdateAddressEntryIntent(
            false, new() { Type = ComputerUseActionType.Key, Keys = "Ctrl+L" }, tab);

        var wrongOwner = new ComputerUseAction { Type = ComputerUseActionType.Type, Text = "https://github.com/YoMosa2000/Axiom" };
        Assert.True(contract.BlocksAction(wrongOwner, tab, out string reason, addressEntry));
        Assert.Contains("character for character", reason);

        var correct = new ComputerUseAction { Type = ComputerUseActionType.Type, Text = "https://github.com/YoMosa2009/Axiom" };
        Assert.False(contract.BlocksAction(correct, tab, out _, addressEntry));
    }

    [Fact]
    public void AnExactUrlGoalDoesNotNeedTheModelToAgreeItIsDone()
    {
        // The loaded document address is stronger evidence than the model's opinion of it. A
        // cautious "not loaded yet" must not be able to block a goal the controller can prove.
        ComputerUseTaskContract contract = Parse();
        Assert.True(contract.TryComplete(
            Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"), ModelSaysNotYet, out _)
            || contract.Progress([]).Current == "Open YouTube in a new tab.");
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);
    }

    [Fact]
    public void AGoalAskingForMoreThanArrivingStillRequiresItsVisualAssessment()
    {
        // Reaching the host must not finish an outcome that asked for something to be DONE there,
        // or "play a video on YouTube" completes at the homepage. What decides this is the user's
        // own wording rather than the planner's match label: a plain "take me to youtube" is
        // finished by arriving, and treating it otherwise stranded a whole run on one outcome.
        const string request = "open a new tab and play the lofi hip hop video on youtube";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open a new tab and play the lofi hip hop video on youtube","description":"Play the requested video.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);

        ComputerUseCapture youtube = Edge("https://www.youtube.com/", "tab1", "tab1");
        Assert.Contains("[OUTCOME DESTINATION REACHED]", contract!.ObserveCurrentOutcome(youtube));
        Assert.False(contract.Complete);

        Assert.True(contract.TryComplete(youtube, ModelSaysComplete, out _));
        Assert.True(contract.Complete);
    }

    [Fact]
    public void TheSeparateTabRequirementSurvivesTheLatch()
    {
        // Goal 2 must still land in a DIFFERENT tab with goal 1's tab still open. Latching goal 1
        // is what supplies the "prior tab" evidence that check needs.
        ComputerUseTaskContract contract = Parse();
        contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));

        ComputerUseCapture sameTab = Edge("https://www.youtube.com/", "tab1", "tab1");
        Assert.Equal("", contract.ObserveCurrentOutcome(sameTab));
        Assert.False(contract.TryComplete(sameTab, ModelSaysComplete, out string reason));
        Assert.Contains("different tab", reason);

        ComputerUseCapture separateTab = Edge("https://www.youtube.com/", "tab2", "tab1", "tab2");
        Assert.True(contract.TryComplete(separateTab, ModelSaysComplete, out _));
        Assert.True(contract.Complete);
    }

    [Fact]
    public void AnErrorPageAtTheRightAddressIsNotAReachedGoal()
    {
        ComputerUseTaskContract contract = Parse();
        var errorPage = new ComputerUseCapture
        {
            JpegBytes = [1],
            TargetWindowHandle = new IntPtr(123),
            ForegroundProcessName = "msedge",
            ForegroundWindowTitle = "Axiom - Microsoft Edge",
            BrowserState = new()
            {
                DocumentAddress = "https://github.com/YoMosa2009/Axiom",
                Address = "https://github.com/YoMosa2009/Axiom",
                ActiveTabId = "tab1", TabIds = ["tab1"], TabTitles = ["tab1"],
                HasTabTelemetry = true, IsErrorPage = true
            }
        };

        Assert.Equal("", contract.ObserveCurrentOutcome(errorPage));
        Assert.False(contract.TryComplete(errorPage, ModelSaysComplete, out _));
        Assert.Contains("github.com/YoMosa2009/Axiom", contract.Describe());
    }

    [Fact]
    public void AnAddressTypedButNotYetLoadedIsNotAReachedGoal()
    {
        // The address bar reads the destination while the loaded document is still the new-tab
        // page. Only the loaded document may latch a goal — the typed text never can, whether or
        // not the caret is still sitting in it.
        ComputerUseTaskContract contract = Parse();
        var typedNotLoaded = new ComputerUseCapture
        {
            JpegBytes = [1],
            TargetWindowHandle = new IntPtr(123),
            ForegroundProcessName = "msedge",
            BrowserState = new()
            {
                Address = "https://github.com/YoMosa2009/Axiom",
                DocumentAddress = "https://ntp.msn.com/edge/ntp",
                AddressHasFocus = true,
                ActiveTabId = "tab1", TabIds = ["tab1"], TabTitles = ["tab1"], HasTabTelemetry = true
            }
        };

        Assert.Equal("", contract.ObserveCurrentOutcome(typedNotLoaded));
        Assert.False(contract.Complete);
    }
}
