using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Replays the run of "open microsoft edge, take me to any weather website, and then make a new tab
/// and take me to youtube". All three outcomes were verified — the log ends with "All contracted
/// outcomes are complete" — and the session then looped on "task complete / task not complete"
/// until it was stopped by hand, because completion was gated on an observer certifying the whole
/// sequence from a single screenshot that can only ever show one tab.
/// </summary>
public class ComputerUseCompletionTests
{
    private const string Request = "open microsoft edge, take me to any weather website, and then make a new tab and take me to youtube.";

    private const string Plan = """
        {"error":"","outcomes":[
          {"request_text":"open microsoft edge","description":"Open the Microsoft Edge browser application.","kind":"desktop","destination":"","match":"","separate_tab":false,"application":"Microsoft Edge","required_text":[]},
          {"request_text":"take me to any weather website","description":"Navigate to a weather website.","kind":"browser","destination":"https://www.weather.com","match":"site","separate_tab":false,"application":"","required_text":[]},
          {"request_text":"make a new tab and take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
        ]}
        """;

    // The observer's real verdict on the final screenshot: it sees YouTube, cannot see the weather
    // tab behind it, and concludes the sequence was not fulfilled.
    private static readonly ComputerUseVisualAssessment ObserverCannotSeeThePriorTabs = new()
    {
        ActionSucceeded = true, GoalCompleted = false, TaskCompleted = false,
        Evidence = "The current page is YouTube. The weather site step is missing or was skipped."
    };

    private static ComputerUseCapture Edge(string loaded, string activeTab, params string[] tabs) => new()
    {
        JpegBytes = [1],
        TargetWindowHandle = new IntPtr(88),
        ForegroundProcessName = "msedge",
        ForegroundWindowTitle = "Microsoft Edge",
        BrowserState = new()
        {
            DocumentAddress = loaded, Address = loaded, ActiveTabId = activeTab,
            TabIds = tabs, TabTitles = tabs, HasTabTelemetry = true
        }
    };

    [Fact]
    public void TheContractCompletesWithoutTheObserverEverAgreeing()
    {
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out ComputerUseTaskContract? contract, out string error), error);

        contract!.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://weather.com/", "tab1", "tab1"));
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);

        // YouTube in a second tab, with the weather tab still present.
        contract.ObserveCurrentOutcome(Edge("https://www.youtube.com/", "tab2", "tab1", "tab2"));

        // This is the state the run reached and then could not act on.
        Assert.True(contract.Complete);
        Assert.Contains("All contracted outcomes are complete", contract.Describe());

        // The observer still refuses, and that must no longer matter.
        Assert.False(ObserverCannotSeeThePriorTabs.TaskCompleted);
    }

    [Fact]
    public void AnUnfinishedContractIsNotComplete()
    {
        // The guard still guards: two of three outcomes verified is not a finished task.
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out ComputerUseTaskContract? contract, out string error), error);

        contract!.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://weather.com/", "tab1", "tab1"));

        Assert.False(contract.Complete);
        Assert.Equal("Open YouTube in a new tab.", contract.Progress([]).Current);
    }

    [Fact]
    public void TheFinalOutcomeStillRequiresItsSeparateTab()
    {
        // Completion is not being handed out cheaply: YouTube loaded in the SAME tab leaves the
        // contract unfinished, because a separate tab was part of the request.
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out ComputerUseTaskContract? contract, out string error), error);

        contract!.ObserveCurrentOutcome(Edge("", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://weather.com/", "tab1", "tab1"));
        contract.ObserveCurrentOutcome(Edge("https://www.youtube.com/", "tab1", "tab1"));

        Assert.False(contract.Complete);
    }

    [Fact]
    public void ATabOpenedByAnyMeansIsCreditedFromTheObservedCount()
    {
        // The extra tab in that run: the model clicked "+" at coordinates, which is not a
        // recognisable new-tab action, so the tab was never credited — it read its own successful
        // action as a failure and opened another.
        var click = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 700, Y = 17 };
        var beforeClick = Edge("https://weather.com/", "tab1", "tab1");
        Assert.False(ComputerUseSessionController.IsNewTabAction(click, beforeClick));

        // The count is what proves it, and the count went up.
        var afterClick = Edge("", "tab2", "tab1", "tab2");
        Assert.Equal(1, beforeClick.BrowserState!.TabCount);
        Assert.Equal(2, afterClick.BrowserState!.TabCount);
    }

    [Fact]
    public void AnIncompleteButAssessedOutcomeIsReportedAsProgressNotFailure()
    {
        // Every Paint run showed the model "The current outcome has no supporting visual
        // assessment" right beside an assessment that described real progress — "a large circle
        // has been drawn, but the eyes and mouth are missing". Told its work had not been assessed,
        // it treated a correct first stroke as a failure and redid it.
        const string request = "draw me a smiley face";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"draw me a smiley face","description":"Draw a smiley face in the Paint application","kind":"desktop","destination":"","match":"","separate_tab":false,"application":"Paint","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error), error);

        var partial = new ComputerUseVisualAssessment
        {
            ActionSucceeded = true, GoalCompleted = false, TaskCompleted = false,
            Evidence = "A large circle has been drawn, but the smiley face is incomplete as eyes and a mouth are missing."
        };
        var paint = new ComputerUseCapture { JpegBytes = [1], ForegroundProcessName = "mspaint", ForegroundWindowTitle = "Untitled - Paint" };

        Assert.False(contract!.TryComplete(paint, partial, out string reason));
        Assert.DoesNotContain("no supporting visual assessment", reason);
        Assert.Contains("Not complete yet", reason);
        // The observer's own description of what is done and what is missing reaches the model.
        Assert.Contains("eyes and a mouth are missing", reason);
        Assert.Contains("do not redo it", reason);
    }

    [Fact]
    public void AMissingAssessmentIsStillReportedAsMissing()
    {
        // The original message remains true for the case it describes.
        const string request = "draw me a smiley face";
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"draw me a smiley face","description":"Draw a smiley face","kind":"desktop","destination":"","match":"","separate_tab":false,"application":"Paint","required_text":[]}
            ]}
            """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out _));

        Assert.False(contract!.TryComplete(new ComputerUseCapture { JpegBytes = [1] }, new ComputerUseVisualAssessment(), out string reason));
        Assert.Contains("no supporting visual assessment", reason);
    }
}
