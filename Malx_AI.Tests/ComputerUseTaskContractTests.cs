using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

public class ComputerUseTaskContractTests
{
    private const string Request = "Open microsoft edge and take me to my github repo called 'Axiom', then open a new tab and take me to youtube";
    private const string Plan = """
        {"error":"","outcomes":[
          {"request_text":"Open microsoft edge and","description":"Open Microsoft Edge","kind":"desktop","destination":"","match":"exact","separate_tab":false},
          {"request_text":"take me to my github repo called 'Axiom', then","description":"Visit the requested repository","kind":"browser","destination":"https://github.com/example/Axiom","match":"exact","separate_tab":false},
          {"request_text":"open a new tab and take me to youtube","description":"Visit YouTube in a new tab","kind":"browser","destination":"https://www.youtube.com/","match":"site","separate_tab":true}
        ]}
        """;

    // Deliberately wrong model output: controller constraints must override all true flags.
    private static readonly ComputerUseVisualAssessment ClaimsEverythingComplete = new()
    {
        ActionSucceeded = true, GoalCompleted = true, TaskCompleted = true, Evidence = "The requested page is visible."
    };

    private static ComputerUseCapture Browser(string url, string selected, params string[] tabs) => new()
    {
        JpegBytes = [1], TargetWindowHandle = new IntPtr(123), ForegroundProcessName = "msedge",
        BrowserState = new() { DocumentAddress = url, Address = url, ActiveTabId = selected, TabIds = tabs,
            TabTitles = tabs, HasTabTelemetry = true }
    };

    private static ComputerUseTaskContract AtSecondDestination()
    {
        Assert.True(ComputerUseTaskContract.TryParse(Plan, Request, out var contract));
        var first = Browser("https://github.com/example/Axiom", "tab1", "tab1");
        Assert.True(contract!.TryComplete(first, ClaimsEverythingComplete, out _)); // app outcome
        Assert.True(contract.TryComplete(first, ClaimsEverythingComplete, out _)); // repo outcome
        return contract;
    }

    [Fact]
    public void ExactReportedSequenceCannotFinishOnGitHubInSecondTab()
    {
        var contract = AtSecondDestination();
        var duplicate = Browser("https://github.com/example/Axiom", "tab2", "tab1", "tab2");
        Assert.False(contract.TryComplete(duplicate, ClaimsEverythingComplete, out string reason));
        Assert.Contains("youtube.com", reason);
        Assert.False(contract.Complete);
        Assert.Equal("Visit YouTube in a new tab", contract.Progress([]).Current);
        Assert.True(contract.TryComplete(Browser("https://www.youtube.com/", "tab2", "tab1", "tab2"), ClaimsEverythingComplete, out _));
        Assert.True(contract.Complete);
    }

    [Theory]
    [InlineData("https://github.com/example/Axiom")]
    [InlineData("github.com/example/Axiom")]
    public void RepeatDestinationBlockedEvenWhenAddressFocusTelemetryIsFalse(string address)
    {
        var contract = AtSecondDestination();
        var newTab = Browser("", "tab2", "tab1", "tab2");
        bool addressEntry = ComputerUseSessionController.UpdateAddressEntryIntent(false,
            new() { Type = ComputerUseActionType.Key, Keys = "Ctrl+L" }, newTab);
        Assert.True(contract.BlocksAction(new() { Type = ComputerUseActionType.Type, Text = address }, newTab, out _, addressEntry));
        Assert.False(contract.BlocksAction(new() { Type = ComputerUseActionType.Type, Text = "https://www.youtube.com/" }, newTab, out _, addressEntry));
    }

    [Fact]
    public void AUrlEnteredIntoPageContentIsNotTreatedAsNavigation()
    {
        var contract = AtSecondDestination();
        var current = Browser("https://www.youtube.com/", "tab2", "tab1", "tab2");
        bool addressEntry = ComputerUseSessionController.UpdateAddressEntryIntent(true,
            new() { Type = ComputerUseActionType.Click, TargetId = "commentField" }, current);
        Assert.False(addressEntry);
        Assert.False(contract.BlocksAction(new() { Type = ComputerUseActionType.Type, Text = "https://example.org/reference" }, current, out _, addressEntry));
    }

    [Fact]
    public void YoutubeInOriginalTabDoesNotSatisfySeparateTabRequirement()
    {
        var contract = AtSecondDestination();
        var sameTab = Browser("https://www.youtube.com/", "tab1", "tab1", "tab2");
        Assert.False(contract.TryComplete(sameTab, ClaimsEverythingComplete, out _));
        Assert.True(contract.BlocksAction(new() { Type = ComputerUseActionType.Type, Text = "https://www.youtube.com/" }, sameTab, out _));
    }

    [Fact]
    public void LosingOriginalTabOrSwitchingBrowserCannotSatisfyNewTabRequirement()
    {
        var contract = AtSecondDestination();
        Assert.False(contract.TryComplete(Browser("https://www.youtube.com/", "tab2", "tab2"), ClaimsEverythingComplete, out _));
        var anotherWindow = new ComputerUseCapture { JpegBytes = [1], TargetWindowHandle = new IntPtr(456),
            BrowserState = Browser("https://www.youtube.com/", "tab2", "tab1", "tab2").BrowserState };
        Assert.False(contract.TryComplete(anotherWindow, ClaimsEverythingComplete, out _));
    }

    [Fact]
    public void MerelyOpeningBlankTabCannotCompleteDestination()
        => Assert.False(AtSecondDestination().TryComplete(Browser("", "tab2", "tab1", "tab2"), ClaimsEverythingComplete, out _));

    [Fact]
    public void SiteCanonicalizationDoesNotLeaveNavigationPermanentlyPending()
    {
        var contract = AtSecondDestination();
        int? pendingTab = null;
        string? pendingUrl = "https://youtube.com/";
        bool recovery = false;
        var progress = contract.Progress([]);
        ComputerUseSessionController.ReconcileBrowserState(Browser("https://www.youtube.com/", "tab2", "tab1", "tab2"),
            ref pendingTab, ref pendingUrl, ref recovery, ref progress, contract);
        Assert.Null(pendingUrl);
        Assert.False(contract.Complete); // navigation does not waive richer outcome criteria
    }

    [Fact]
    public void IntermediateAuthenticationDestinationIsAllowedButCannotCompleteOutcome()
    {
        var contract = AtSecondDestination();
        var current = Browser("https://accounts.example.org/login", "tab2", "tab1", "tab2");
        Assert.False(contract.BlocksAction(new() { Type = ComputerUseActionType.Type, Text = "https://accounts.example.org/login" }, current, out _, true));
        Assert.False(contract.TryComplete(current, ClaimsEverythingComplete, out _));
    }

    [Fact]
    public void BehaviorIsNotSpecificToNamedSites()
    {
        string plan = Plan.Replace("youtube", "example.org").Replace("YouTube", "Example")
            .Replace("https://www.example.org.com/", "https://example.org/");
        string request = Request.Replace("youtube", "example.org");
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out var contract));
        var first = Browser("https://github.com/example/Axiom", "a", "a");
        Assert.True(contract!.TryComplete(first, ClaimsEverythingComplete, out _));
        Assert.True(contract.TryComplete(first, ClaimsEverythingComplete, out _));
        Assert.False(contract.TryComplete(Browser("https://github.com/example/Axiom", "b", "a", "b"), ClaimsEverythingComplete, out _));
        Assert.True(contract.TryComplete(Browser("https://example.org/", "b", "a", "b"), ClaimsEverythingComplete, out _));
    }

    [Fact]
    public void InvalidOrUnsupportedPlansFailBeforeActions()
    {
        Assert.False(ComputerUseTaskContract.TryParse("{}", Request, out _));
        Assert.False(ComputerUseTaskContract.TryParse(Plan.Replace("Open microsoft edge", "invented request"), Request, out _));
        Assert.False(ComputerUseTaskContract.TryParse(Plan.Replace("https://www.youtube.com/", ""), Request, out _));
    }

    [Fact]
    public void PlanThatOmitsALaterClauseIsRejected()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(Plan)!;
        node["outcomes"]!.AsArray().RemoveAt(2);
        Assert.False(ComputerUseTaskContract.TryParse(node.ToJsonString(), Request, out _));
    }

    [Fact]
    public void DesktopOutcomesRequireTheCorrectApplicationAndRequestedText()
    {
        const string request = "Open Notepad and type hello then open Calculator";
        const string plan = """
        {"error":"","outcomes":[
         {"request_text":"Open Notepad and type hello then","description":"Enter hello in Notepad","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Notepad","required_text":["hello"]},
         {"request_text":"open Calculator","description":"Open Calculator","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Calculator","required_text":[]}
        ]}
        """;
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out var contract));
        ComputerUseCapture App(string process, string value) => new() { JpegBytes = [1], ForegroundProcessName = process,
            ForegroundWindowTitle = process, UiTargets = [new() { Name = "Editor", Role = "Edit", Value = value }] };
        Assert.False(contract!.TryComplete(App("msedge", "hello"), ClaimsEverythingComplete, out _));
        Assert.False(contract.TryComplete(App("notepad", "wrong text"), ClaimsEverythingComplete, out _));
        Assert.True(contract.TryComplete(App("notepad", "hello"), ClaimsEverythingComplete, out _));
        Assert.False(contract.Complete);
        Assert.False(contract.TryComplete(App("notepad", "hello"), ClaimsEverythingComplete, out _));
        Assert.True(contract.TryComplete(App("CalculatorApp", ""), ClaimsEverythingComplete, out _));
        Assert.True(contract.Complete);
    }
}
