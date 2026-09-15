using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// Reproduces the observed start-up failure: the session window opened, three planning attempts
/// were rejected in a row, and it closed again without sending a single desktop action. The plan
/// itself was fine -- it simply described "open up microsoft edge" as its own outcome, which is an
/// application launch and has no URL to give.
/// </summary>
public class ComputerUsePlanAcceptanceTests
{
    private const string Request = "open up microsoft edge, then take me to my github repo 'Axiom', i then want you to open a new tab and take me to youtube.";

    // Verbatim from backend-events.log, ComputerUse.PlanRejected attempts 1-3.
    private const string RejectedPlan = """
        ```json
        {
          "outcomes": [
            {
              "request_text": "open up microsoft edge",
              "description": "Open the Microsoft Edge browser.",
              "kind": "browser",
              "destination": "",
              "match": "site",
              "separate_tab": false,
              "application": "Microsoft Edge",
              "required_text": []
            },
            {
              "request_text": "take me to my github repo 'Axiom'",
              "description": "Navigate to the specific GitHub repository 'Axiom' at the verified URL.",
              "kind": "browser",
              "destination": "https://github.com/YoMosa2009/Axiom",
              "match": "exact",
              "separate_tab": false,
              "application": "",
              "required_text": []
            },
            {
              "request_text": "open a new tab and take me to youtube",
              "description": "Open YouTube in a new tab.",
              "kind": "browser",
              "destination": "https://www.youtube.com",
              "match": "site",
              "separate_tab": true,
              "application": "",
              "required_text": []
            }
          ],
          "error": ""
        }
        ```
        """;

    [Fact]
    public void TheExactRejectedPlanNowStartsTheSession()
    {
        Assert.True(
            ComputerUseTaskContract.TryParse(RejectedPlan, Request, out ComputerUseTaskContract? contract, out string error),
            "The plan that ended three sessions before any input was sent must be accepted: " + error);

        // The launch outcome survives as a desktop outcome, so the app is still verified against
        // the real foreground window rather than silently dropped.
        ComputerUseTaskProgress progress = contract!.Progress([]);
        Assert.Equal("Open the Microsoft Edge browser.", progress.Current);
        Assert.Contains("Required application: Microsoft Edge", contract.Describe());
        Assert.DoesNotContain("Required destination: http", contract.Describe());
    }

    [Fact]
    public void TheReclassifiedLaunchStillLeadsToBothRequestedDestinations()
    {
        Assert.True(ComputerUseTaskContract.TryParse(RejectedPlan, Request, out ComputerUseTaskContract? contract, out _));

        ComputerUseTaskProgress progress = contract!.Progress([]);
        Assert.Equal("Navigate to the specific GitHub repository 'Axiom' at the verified URL.", progress.Next);
        Assert.Contains("Open YouTube in a new tab.", progress.Remaining);
    }

    [Fact]
    public void ABrowserOutcomeWithNoUrlAndNoApplicationIsStillRejectedWithUsableGuidance()
    {
        // Nothing here says what the outcome is, so it cannot be repaired. The message must point
        // at the actual fix (kind desktop) instead of demanding a URL the planner cannot know --
        // that wording is what kept the model re-sending the identical plan.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge","description":"Open something.","kind":"browser","destination":"","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        Assert.False(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error));
        Assert.Contains("kind desktop", error);
        Assert.Contains("never invent", error);
    }

    [Fact]
    public void AMissingUrlOnANewTabDestinationIsNotReclassifiedAway()
    {
        // separate_tab means this really is a browser destination, so a missing URL is a genuine
        // planning error. Reclassifying it would silently drop a requested destination.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"take me to my github repo 'Axiom'","description":"Open the repository.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"","required_text":[]},
              {"request_text":"open a new tab and take me to youtube","description":"Open a site in a new tab.","kind":"browser","destination":"","match":"site","separate_tab":true,"application":"Microsoft Edge","required_text":[]}
            ]}
            """;

        Assert.False(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error));
        Assert.Contains("Outcome 2", error);
    }

    [Fact]
    public void AnApplicationLaunchOutcomeCannotSmuggleInADestination()
    {
        // Only a genuinely empty destination is reclassified; a browser outcome that has a URL
        // keeps being validated as one.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge","description":"Open Edge at a page.","kind":"browser","destination":"not-a-url","match":"site","separate_tab":false,"application":"Microsoft Edge","required_text":[]}
            ]}
            """;

        Assert.False(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error));
        Assert.Contains("absolute destination URL", error);
    }

    [Fact]
    public void ConversationalFillerIsNotTreatedAsARequestedOutcome()
    {
        // The literal second rejection reason from this request: the planner quoted
        // "open a new tab and take me to youtube" but not "i then want you to", and the session
        // died over three pronouns.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge","description":"Open Microsoft Edge.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Microsoft Edge","required_text":[]},
              {"request_text":"take me to my github repo 'Axiom'","description":"Open the repository.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"","required_text":[]},
              {"request_text":"open a new tab and take me to youtube","description":"Open YouTube in a new tab.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":true,"application":"","required_text":[]}
            ]}
            """;

        Assert.True(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error), error);
    }

    [Fact]
    public void ADroppedDestinationIsStillCaught()
    {
        // The coverage check must keep doing its actual job: a plan that silently omits YouTube
        // has to be rejected, not merely produce a shorter plan.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge","description":"Open Microsoft Edge.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Microsoft Edge","required_text":[]},
              {"request_text":"take me to my github repo 'Axiom'","description":"Open the repository.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        Assert.False(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error));
        Assert.Contains("youtube", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ADroppedConstraintInsideAKeptOutcomeIsStillCaught()
    {
        // "new tab" is the whole point of the third outcome. Losing it must not pass just because
        // youtube is still mentioned somewhere.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge","description":"Open Microsoft Edge.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Microsoft Edge","required_text":[]},
              {"request_text":"take me to my github repo 'Axiom'","description":"Open the repository.","kind":"browser","destination":"https://github.com/YoMosa2009/Axiom","match":"exact","separate_tab":false,"application":"","required_text":[]},
              {"request_text":"take me to youtube","description":"Open YouTube.","kind":"browser","destination":"https://www.youtube.com","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        Assert.False(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error));
        Assert.Contains("new", error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("tab", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InventedContentWordsAreStillRejected()
    {
        // Relaxing function words must not let a planner cite a request the user never made.
        const string plan = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge and delete the repository","description":"Open Microsoft Edge.","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Microsoft Edge","required_text":[]}
            ]}
            """;

        Assert.False(ComputerUseTaskContract.TryParse(plan, Request, out _, out string error));
        Assert.Contains("without invented words", error);
    }

    [Fact]
    public async Task ThePlannerAcceptsTheObservedPlanOnTheFirstAttempt()
    {
        // End to end through the real planning entry point, with the model's actual output. This
        // is the call that returned "could not start after 3 planning attempts".
        int calls = 0;
        ComputerUsePlanning.Result result = await ComputerUsePlanning.CreateAsync(
            Request,
            navigationContext: "",
            inferAsync: (_, _, _) => { calls++; return Task.FromResult(RejectedPlan); },
            token: CancellationToken.None);

        Assert.NotNull(result.Contract);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(1, calls);
        Assert.Equal("", result.Error);
    }

    [Fact]
    public async Task ARetryCarriesTheValidationErrorWhereTheModelWillWeighIt()
    {
        // Three byte-identical plans came back because the correction sat in a JSON field of the
        // user payload. A deterministic model needs the rejection in the system prompt.
        const string unusable = """
            {"error":"","outcomes":[
              {"request_text":"open up microsoft edge","description":"Open something.","kind":"browser","destination":"","match":"site","separate_tab":false,"application":"","required_text":[]}
            ]}
            """;

        var systemPrompts = new List<string>();
        ComputerUsePlanning.Result result = await ComputerUsePlanning.CreateAsync(
            Request,
            navigationContext: "",
            inferAsync: (systemPrompt, _, _) =>
            {
                systemPrompts.Add(systemPrompt);
                return Task.FromResult(systemPrompts.Count == 1 ? unusable : RejectedPlan);
            },
            token: CancellationToken.None);

        Assert.NotNull(result.Contract);
        Assert.Equal(2, result.Attempts);
        Assert.DoesNotContain("PLAN CORRECTION REQUIRED", systemPrompts[0]);
        Assert.Contains("PLAN CORRECTION REQUIRED", systemPrompts[1]);
        Assert.Contains("kind desktop", systemPrompts[1]);
    }
}
