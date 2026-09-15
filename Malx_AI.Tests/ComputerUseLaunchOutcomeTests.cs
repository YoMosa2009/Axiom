using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Replays the observed three-outcome run. Edge opened and the repository loaded, but the contract
/// stayed on "Outcome 1/3: Open the Microsoft Edge browser application" for the whole session, so
/// the agent kept being told its current goal was one it had already finished — and went back and
/// redid earlier work instead of reaching YouTube.
/// </summary>
public class ComputerUseLaunchOutcomeTests
{
    private const string Request = "open up microsoft edge, then take me to my github repo 'Axiom', i then want you to make a new tab and take me to youtube.";

    // Verbatim from backend-events.log, ComputerUse.Plan at 17:23:29.
    private const string Plan = """
        {"error":"","outcomes":[
          {"request_text":"open up microsoft edge","description":"Open the Microsoft Edge browser application.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Microsoft Edge","required_text":[]},
          {"request_text":"take me to my github repo 'Axiom'","description":"Navigate to the specific GitHub repository page for Axiom.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"","required_text":[]},
          {"request_text":"make a new tab and take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
        ]}
        """;

    // The observer refusing to bless a step is exactly what stranded the run.
    private static readonly ComputerUseVisualAssessment ModelSaysNotYet = new()
    {
        ActionSucceeded = false, GoalCompleted = false, TaskCompleted = false,
        Evidence = "Controller constraints remain unresolved; no completion accepted."
    };

    private static readonly ComputerUseVisualAssessment ModelSaysComplete = new()
    {
        ActionSucceeded = true, GoalCompleted = true, TaskCompleted = true, Evidence = "YouTube is visible."
    };

    private static ComputerUseCapture Edge(string loadedUrl, string activeTab, params string[] tabs) => new()
    {
        JpegBytes = [1],
        TargetWindowHandle = new IntPtr(123),
        ForegroundProcessName = "msedge",
        ForegroundWindowTitle = "Axiom - Microsoft Edge",
        BrowserState = new()
        {
            DocumentAddress = loadedUrl, Address = loadedUrl, ActiveTabId = activeTab,
            TabIds = tabs, TabTitles = tabs, HasTabTelemetry = true
        }
    };

    private static ComputerUseTaskContract Parse()
    {
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out ComputerUseTaskContract? contract, out string error), error);
        return contract!;
    }

    [Fact]
    public void TheLaunchOutcomeCompletesFromTheForegroundWindowNotTheObserver()
    {
        ComputerUseTaskContract contract = Parse();
        Assert.Equal("Open the Microsoft Edge browser application.", contract.Progress([]).Current);

        string evidence = contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        Assert.Contains("[OUTCOME VERIFIED]", evidence);
        Assert.Equal("Navigate to the specific GitHub repository page for Axiom.", contract.Progress([]).Current);
    }

    [Fact]
    public void TheWholeRunReachesYouTubeWithTheObserverRefusingEveryStep()
    {
        // The observer says "not yet" throughout, as it did in the real run. The two outcomes the
        // controller can prove for itself must still advance, leaving only YouTube's own criteria
        // to be judged.
        ComputerUseTaskContract contract = Parse();

        contract.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://github.com/YoMosa2009/Axiom", "tab1", "tab1"));
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);

        // Second tab opened; the repository is now a background tab.
        ComputerUseCapture newTab = Edge("", "tab2", "tab1", "tab2");
        contract.ObserveCurrentOutcome(newTab);
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);

        // Retyping the finished destination is refused rather than becoming the whole run.
        bool addressEntry = ComputerUseSessionController.UpdateAddressEntryIntent(
            false, new() { Type = ComputerUseActionType.Key, Keys = "Ctrl+T" }, newTab);
        Assert.True(contract.BlocksAction(
            new() { Type = ComputerUseActionType.Type, Text = "https://github.com/YoMosa2009/Axiom" },
            newTab, out string reason, addressEntry));
        Assert.Contains("already complete", reason);

        // "take me to youtube" is finished by being on YouTube, so the observer's refusal cannot
        // hold this one either — it completes on the controller's own evidence.
        ComputerUseCapture youtube = Edge("https://www.youtube.com/", "tab2", "tab1", "tab2");
        Assert.True(contract.TryComplete(youtube, ModelSaysNotYet, out _));
        Assert.True(contract.Complete);
    }

    [Fact]
    public void ALaunchOutcomeWaitsForTheApplicationItNamed()
    {
        // Nothing is assumed from the request: until that application is actually in front, the
        // outcome stays open.
        ComputerUseTaskContract contract = Parse();
        var notepad = new ComputerUseCapture
        {
            JpegBytes = [1], ForegroundProcessName = "notepad", ForegroundWindowTitle = "Untitled - Notepad"
        };

        Assert.Equal("", contract.ObserveCurrentOutcome(notepad));
        Assert.Equal("Open the Microsoft Edge browser application.", contract.Progress([]).Current);
    }

    [Theory]
    // Work to be done inside the app: opening it is not doing it.
    [InlineData("Turn on dark mode in Settings.", "Settings")]
    [InlineData("Open Notepad and type the greeting.", "Notepad")]
    [InlineData("Save the document in Word.", "Word")]
    [InlineData("Open File Explorer and delete the folder.", "File Explorer")]
    public void AnOutcomeThatAsksForWorkInsideTheAppIsNotFinishedByLaunchingIt(string description, string application)
    {
        ComputerUseTaskContract contract = SingleLaunchOutcome(description, application);
        Assert.Equal("", contract.ObserveCurrentOutcome(Foreground(application)));
        Assert.False(contract.Complete);
    }

    [Theory]
    // Pure launch phrasings, which the launch alone genuinely does complete.
    [InlineData("Open the Microsoft Edge browser application.", "Microsoft Edge")]
    [InlineData("Launch Microsoft Edge.", "Microsoft Edge")]
    [InlineData("Start the Microsoft Edge browser and bring it to the foreground.", "Microsoft Edge")]
    [InlineData("Open Microsoft Edge.", "Microsoft Edge")]
    [InlineData("Open the Notepad app.", "Notepad")]
    [InlineData("Bring the File Explorer window to the front.", "File Explorer")]
    public void PureLaunchPhrasingsAllCompleteOnTheForegroundWindow(string description, string application)
    {
        ComputerUseTaskContract contract = SingleLaunchOutcome(description, application);
        Assert.Contains("[OUTCOME VERIFIED]", contract.ObserveCurrentOutcome(Foreground(application)));
        Assert.True(contract.Complete);
    }

    private static ComputerUseTaskContract SingleLaunchOutcome(string description, string application)
    {
        // A self-contained one-outcome plan, so this exercises launch completion rather than the
        // plan-coverage rules.
        string plan = $$"""
            {"error":"","outcomes":[
              {"request_text":"open the app","description":"{{description}}","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"{{application}}","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, "open the app", out ComputerUseTaskContract? contract, out string error), error);
        return contract!;
    }

    private static ComputerUseCapture Foreground(string application) => new()
    {
        JpegBytes = [1],
        ForegroundProcessName = application.Replace(" ", ""),
        ForegroundWindowTitle = application
    };
}
