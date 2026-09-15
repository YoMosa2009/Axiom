using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// A plan rejection ends the session before a single action is sent, so any planner quirk that is
/// fatal becomes a prompt the user has to work around. These cover request shapes unrelated to the
/// one that was reported: each is a plausible planner output that used to be fatal and now either
/// repairs itself or, failing that, still fails for a reason that genuinely prevents acting.
/// </summary>
public class ComputerUsePlanGeneralityTests
{
    private static ComputerUseTaskContract Accepts(string request, string plan)
    {
        Assert.True(ComputerUseTaskContract.TryParse(plan, request, out ComputerUseTaskContract? contract, out string error),
            "This plan must start a session, not end it: " + error);
        return contract!;
    }

    [Fact]
    public void ANewTabRequestWithNothingBeforeItStillRuns()
    {
        // "Open a new tab and go to X" as the whole request: separate_tab has no preceding
        // destination to be separate from. Holding that constraint deadlocks the contract, and
        // rejecting the plan does nothing at all, so the constraint is dropped and the tab is
        // verified when it is actually opened.
        ComputerUseTaskContract contract = Accepts(
            "open a new tab and take me to youtube",
            """
            {"error":"","outcomes":[
              {"request_text":"open a new tab and take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
            ]}
            """);

        Assert.Contains("separate tab: False", contract.Describe());
        Assert.Contains("https://www.youtube.com", contract.Describe());
    }

    [Fact]
    public void ADesktopOutcomeCarryingAnExecutableNameStillRuns()
    {
        ComputerUseTaskContract contract = Accepts(
            "open notepad",
            """
            {"error":"","outcomes":[
              {"request_text":"open notepad","description":"Open Notepad.","kind":"desktop","destination":"notepad.exe","match":"exact","separate_tab":false,"application":"Notepad","required_text":[]}
            ]}
            """);

        Assert.Contains("Required application: Notepad", contract.Describe());
        Assert.DoesNotContain("notepad.exe", contract.Describe());
    }

    [Fact]
    public void ADesktopOutcomeCarryingARealUrlIsHonouredAsABrowserDestination()
    {
        // The mirror of the reported bug. The planner stated a destination; the label was wrong.
        ComputerUseTaskContract contract = Accepts(
            "take me to youtube",
            """
            {"error":"","outcomes":[
              {"request_text":"take me to youtube","description":"Open YouTube.","kind":"desktop","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """);

        Assert.Contains("Required destination: https://www.youtube.com", contract.Describe());
    }

    [Fact]
    public void RequiredTextQuotedInADifferentCaseIsNormalisedNotRejected()
    {
        ComputerUseTaskContract contract = Accepts(
            "open notepad and type Hello World",
            """
            {"error":"","outcomes":[
              {"request_text":"open notepad and type Hello World","description":"Type the requested text.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Notepad","required_text":["hello world"]}
            ]}
            """);

        // The user's own casing is what gets typed, not the planner's.
        Assert.Contains("required visible text: Hello World", contract.Describe());
    }

    [Fact]
    public void AnUnrecognisedMatchValueFallsBackToTheStricterReading()
    {
        ComputerUseTaskContract contract = Accepts(
            "take me to github.com",
            """
            {"error":"","outcomes":[
              {"request_text":"take me to github.com","description":"Open GitHub.","kind":"browser","destination":"https://github.com","match":"url","separate_tab":false,"application":"","required_text":[]}
            ]}
            """);

        Assert.Contains("match: exact", contract.Describe());
    }

    [Fact]
    public void AParaphrasedCitationIsNotAnInventedInstruction()
    {
        // "repo" quoted as "repository" is the same word. Planners inflect what they quote.
        Accepts(
            "take me to my github repo 'Axiom'",
            """
            {"error":"","outcomes":[
              {"request_text":"take me to my github repository 'Axiom'","description":"Open the repository.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"","required_text":[]}
            ]}
            """);
    }

    [Fact]
    public void PolitePhrasingDoesNotChangeWhatCountsAsCovered()
    {
        Accepts(
            "could you please open notepad for me and type hi",
            """
            {"error":"","outcomes":[
              {"request_text":"please open notepad and type hi","description":"Open Notepad and type the text.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Notepad","required_text":["hi"]}
            ]}
            """);
    }

    [Theory]
    // No destination and no application: nothing here says what to do, and a URL must never be invented.
    [InlineData("""{"error":"","outcomes":[{"request_text":"take me to youtube","description":"Go somewhere.","kind":"browser","destination":"","match":"site","separate_tab":false,"application":"","required_text":[]}]}""")]
    // An instruction the user never gave.
    [InlineData("""{"error":"","outcomes":[{"request_text":"take me to youtube and delete the account","description":"Open YouTube.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":[]}]}""")]
    // Nothing to do.
    [InlineData("""{"error":"","outcomes":[]}""")]
    // The planner itself said it could not resolve the request.
    [InlineData("""{"error":"No repository URL was supplied.","outcomes":[]}""")]
    // Text to type that the user never asked for.
    [InlineData("""{"error":"","outcomes":[{"request_text":"take me to youtube","description":"Search.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":["my home address"]}]}""")]
    // Not a plan at all.
    [InlineData("I will open YouTube for you now.")]
    public void PlansThatGenuinelyCannotBeActedOnAreStillRejected(string plan)
    {
        Assert.False(ComputerUseTaskContract.TryParse(plan, "take me to youtube", out _, out string error));
        Assert.NotEqual("", error);
    }

    [Fact]
    public async Task AStructurallySoundPlanRunsRatherThanEndingTheSessionOverItsWording()
    {
        // The generalisation that matters: after the retries are spent, a plan whose only defect
        // is how it quoted the request still runs. Every destination, application and tab is
        // verified from the screen regardless, so this trades a wording guarantee for actually
        // doing the work -- and says so instead of hiding it.
        const string request = "open notepad and take me to youtube";
        const string paraphrased = """
            {"error":"","outcomes":[
              {"request_text":"launch notepad","description":"Open Notepad.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Notepad","required_text":[]},
              {"request_text":"take me to youtube","description":"Open YouTube.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        // Strict validation, which governs every retry, still objects to it.
        Assert.False(ComputerUseTaskContract.TryParse(paraphrased, request, out _, out _));

        int calls = 0;
        ComputerUsePlanning.Result result = await ComputerUsePlanning.CreateAsync(
            request,
            navigationContext: "",
            inferAsync: (_, _, _) => { calls++; return Task.FromResult(paraphrased); },
            token: CancellationToken.None);

        Assert.NotNull(result.Contract);
        Assert.Equal(3, calls);
        // Both requested outcomes survive: Notepad first, YouTube still queued behind it.
        ComputerUseTaskProgress progress = result.Contract!.Progress([]);
        Assert.Equal("Open Notepad.", progress.Current);
        Assert.Equal("Open YouTube.", progress.Next);
        Assert.False(string.IsNullOrWhiteSpace(result.Warning));
        Assert.Contains("closest usable plan", result.Warning);
    }

    [Fact]
    public async Task SalvageCannotRescueAPlanThatCannotBeActedOn()
    {
        // The last-resort path is only ever about wording. A plan with no usable destination stays
        // fatal, and the session still reports why rather than pretending to work.
        const string unusable = """
            {"error":"","outcomes":[
              {"request_text":"take me to youtube","description":"Go somewhere.","kind":"browser","destination":"","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        ComputerUsePlanning.Result result = await ComputerUsePlanning.CreateAsync(
            "take me to youtube",
            navigationContext: "",
            inferAsync: (_, _, _) => Task.FromResult(unusable),
            token: CancellationToken.None);

        Assert.Null(result.Contract);
        Assert.Contains("no absolute destination URL", result.Error);
    }
}
