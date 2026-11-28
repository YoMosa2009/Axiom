using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// What replaced the Critic at Extra High and Ultra (2026-11-24), the slow-turn limits that
/// replaced the fixed 3-minute deadline, and the reply-context attachment.
/// </summary>
public sealed class EffortQualityGateTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomGateTests", Guid.NewGuid().ToString("N"));

    public EffortQualityGateTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static Task<AgentApprovalOutcome> Approve(AgentToolCall call, string reason, CancellationToken token)
        => Task.FromResult(AgentApprovalOutcome.Approve);

    private static AgentToolCall Write(string path, string content) =>
        new(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = path, ["content"] = content });

    private static AgentToolCall Read(string path) =>
        new(AgentToolNames.ReadFile, new Dictionary<string, string> { ["path"] = path });

    private Task<AgentRunResult> Run(ScriptedAgentModel model, AgentTurnPolicy policy, int maxSteps = 12)
        => new AgentSession(AgentScope.Folder(_folder), maxSteps)
            .RunAsync("build notes.txt", AgentApprovalMode.Auto, model, Approve, _ => { }, CancellationToken.None, policy: policy);

    [Fact]
    public async Task ExtraHighSendsAnUncheckedFinishBackOnce()
    {
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(Write("notes.txt", "hello")),
            AgentModelReply.Answer("Done."),
            AgentModelReply.Tool(Read("notes.txt")),
            AgentModelReply.Answer("Done, and I read it back."));
        var policy = new AgentTurnPolicy().WithEffort(EffortLevel.ExtraHigh, AgentTier.Full);

        AgentRunResult result = await Run(model, policy);

        Assert.Equal("Done, and I read it back.", result.FinalMessage);
        Assert.Contains(model.SeenHistories[2], e => e.Observation.Contains("nothing has checked your last changes"));
    }

    [Fact]
    public async Task HighEffortFinishesWithoutTheCheck()
    {
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(Write("notes.txt", "hello")),
            AgentModelReply.Answer("Done."));
        var policy = new AgentTurnPolicy().WithEffort(EffortLevel.High, AgentTier.Full);

        AgentRunResult result = await Run(model, policy);

        Assert.Equal("Done.", result.FinalMessage);
    }

    [Fact]
    public async Task TheFinishToolIsCheckedToo()
    {
        var finish = new AgentToolCall(AgentToolNames.Finish, new Dictionary<string, string> { ["summary"] = "All done." });
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(Write("notes.txt", "hello")),
            AgentModelReply.Tool(finish),
            AgentModelReply.Tool(Read("notes.txt")),
            AgentModelReply.Tool(finish));
        var policy = new AgentTurnPolicy().WithEffort(EffortLevel.Ultra, AgentTier.Full);

        AgentRunResult result = await Run(model, policy);

        Assert.Equal("All done.", result.FinalMessage);
        Assert.Equal(4, model.SeenHistories.Count);
    }

    [Fact]
    public async Task UltraReviewFindingsGoBackIntoTheRun()
    {
        int reviews = 0;
        var policy = new AgentTurnPolicy().WithEffort(EffortLevel.Ultra, AgentTier.Full) with
        {
            Reviewer = (request, _) =>
            {
                reviews++;
                Assert.Contains("notes.txt", request.ChangedFiles);
                return Task.FromResult<IReadOnlyList<string>>(["notes.txt: says hello, the request asked for goodbye"]);
            },
        };
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(Write("notes.txt", "hello")),
            AgentModelReply.Tool(Read("notes.txt")),
            AgentModelReply.Answer("Done."),
            AgentModelReply.Tool(Write("notes.txt", "goodbye")),
            AgentModelReply.Tool(Read("notes.txt")),
            AgentModelReply.Answer("Fixed."));

        AgentRunResult result = await Run(model, policy);

        Assert.Equal(1, reviews);
        Assert.Equal("Fixed.", result.FinalMessage);
        Assert.Equal("goodbye", File.ReadAllText(Path.Combine(_folder, "notes.txt")));
        Assert.Contains(model.SeenHistories[3], e => e.Observation.Contains("fresh review"));
    }

    [Fact]
    public async Task AFailingReviewerNeverBlocksTheRun()
    {
        var policy = new AgentTurnPolicy().WithEffort(EffortLevel.Ultra, AgentTier.Full) with
        {
            Reviewer = (_, _) => throw new InvalidOperationException("server down"),
        };
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(Write("notes.txt", "hello")),
            AgentModelReply.Tool(Read("notes.txt")),
            AgentModelReply.Answer("Done."));

        AgentRunResult result = await Run(model, policy);

        Assert.Equal("Done.", result.FinalMessage);
    }

    [Fact]
    public async Task ALongRunWithNoFileWrittenIsNudgedOnce()
    {
        var policy = new AgentTurnPolicy { ExplorationBudget = 2 };
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.ListDirectory, new Dictionary<string, string> { ["path"] = "." })),
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.FindFiles, new Dictionary<string, string> { ["pattern"] = "*.txt" })),
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.FindFiles, new Dictionary<string, string> { ["pattern"] = "*.md" })),
            AgentModelReply.Answer("Nothing there."));

        await Run(model, policy);

        IReadOnlyList<AgentExchange> last = model.SeenHistories.Last();
        Assert.Single(last, e => e.Observation.Contains("PROGRESS CHECK"));
    }

    [Theory]
    [InlineData("NO ISSUES", 0)]
    [InlineData("", 0)]
    [InlineData("- app.js: the refresh button is never wired up\n- index.html: links style.css, file is styles.css", 2)]
    [InlineData("1. server.py crashes on start because port is a string", 1)]
    public void ReviewRepliesAreReadAsAList(string reply, int expected)
        => Assert.Equal(expected, AgentQualityGates.ParseReviewIssues(reply).Count);

    [Fact]
    public void QuestionTurnsGetNoEffortGates()
    {
        AgentTurnPolicy policy = new AgentTurnPolicy(AnswerOnly: true).WithEffort(EffortLevel.Ultra, AgentTier.Full);
        Assert.False(policy.RequireCheckedFinish);
        Assert.Equal(0, policy.ExplorationBudget);
    }

    [Fact]
    public void SmallModelsSkipTheCheckGate()
        => Assert.False(new AgentTurnPolicy().WithEffort(EffortLevel.Ultra, AgentTier.Compact).RequireCheckedFinish);

    [Theory]
    [InlineData(EffortLevel.Light)]
    [InlineData(EffortLevel.High)]
    [InlineData(EffortLevel.Ultra)]
    public void TurnLimitsGrowWithEffortAndAllowLongSilenceOnSelfHostedServers(EffortLevel effort)
    {
        AgentTurnLimits cloud = AgentTurnLimits.For(effort, selfHosted: false);
        AgentTurnLimits hosted = AgentTurnLimits.For(effort, selfHosted: true);
        Assert.True(cloud.Total >= TimeSpan.FromMinutes(10));
        Assert.True(hosted.Idle > cloud.Idle);
        Assert.True(AgentTurnLimits.For(EffortLevel.Ultra, false).Total > AgentTurnLimits.For(EffortLevel.Light, false).Total);
    }

    [Fact]
    public void SlowTurnGuidanceAsksForSmallerSteps()
    {
        string text = CloudAgentModel.SlowTurnGuidance("produced no output for 8 minutes");
        Assert.Contains("append_file", text);
        Assert.Contains("did not run", text);
    }

    [Fact]
    public void StreamActivityReachesOnlyTheActiveListener()
    {
        int pings = 0;
        using (StreamActivity.Use(() => pings++))
        {
            StreamActivity.Ping();
            StreamActivity.Ping();
        }
        StreamActivity.Ping();
        Assert.Equal(2, pings);
    }

    [Theory]
    [InlineData("python -c \"import x\"", true)]
    [InlineData("py -3 -c \"print(1)\"", true)]
    [InlineData("node -e \"console.log(1)\"", true)]
    [InlineData("python script.py", false)]
    public void FailedInlineScriptsGetAScriptFileHint(string command, bool hinted)
        => Assert.Equal(hinted, AgentToolExecutor.InlineScriptHint(command).Length > 0);

    [Fact]
    public void CheckFindingsBecomeARepairableReview()
    {
        string contract = EffortQualityChecks.ToReviewContract(["ACCEPTANCE HARNESS: add() returned 3 for 1+1", "ACCEPTANCE HARNESS: add() returned 3 for 1+1"]);
        CriticReport report = CriticContractParser.Parse(contract);
        Assert.True(report.HasIssues);
        Assert.Single(report.Issues);

        Assert.False(CriticContractParser.Parse(EffortQualityChecks.ToReviewContract([])).HasIssues);
    }

    [Theory]
    [InlineData(EffortLevel.High, true, true, false)]
    [InlineData(EffortLevel.ExtraHigh, true, true, false)]
    [InlineData(EffortLevel.Ultra, true, false, true)]
    [InlineData(EffortLevel.Ultra, false, false, false)]
    [InlineData(EffortLevel.Ultra, false, true, true)]
    public void FreshReviewIsForUltraOnCapableModels(EffortLevel effort, bool cloud, bool fourB, bool expected)
        => Assert.Equal(expected, EffortQualityChecks.UsesFreshReview(effort, cloud, fourB));

    [Fact]
    public async Task UltraReviewStillRunsWhenTheCheckingBudgetRunsOut()
    {
        int reviews = 0;
        var policy = new AgentTurnPolicy(VerificationBudget: 2).WithEffort(EffortLevel.Ultra, AgentTier.Full) with
        {
            Reviewer = (_, _) =>
            {
                reviews++;
                return Task.FromResult<IReadOnlyList<string>>(["notes.txt: missing the second line the request asked for"]);
            },
        };
        var model = new ScriptedAgentModel(
            AgentModelReply.Tool(Write("notes.txt", "one")),
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.SearchText, new Dictionary<string, string> { ["pattern"] = "absent-a" })),
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.SearchText, new Dictionary<string, string> { ["pattern"] = "absent-b" })),
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.SearchText, new Dictionary<string, string> { ["pattern"] = "absent-c" })),
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.SearchText, new Dictionary<string, string> { ["pattern"] = "absent-d" })),
            AgentModelReply.Tool(Write("notes.txt", "one\ntwo")),
            AgentModelReply.Tool(Read("notes.txt")),
            AgentModelReply.Answer("Fixed."));

        AgentRunResult result = await Run(model, policy, maxSteps: 20);

        Assert.Equal(1, reviews);
        Assert.Equal("Fixed.", result.FinalMessage);
        Assert.Equal("one\ntwo", File.ReadAllText(Path.Combine(_folder, "notes.txt")));
    }

    [Fact]
    public void ALocalPageCanBeOpenedWithoutAServer()
    {
        string page = Path.Combine(_folder, "index.html");
        File.WriteAllText(page, "<html></html>");
        Assert.StartsWith("file:///", BrowserControlService.LocalPageUrl(page));
        Assert.StartsWith("file:///", BrowserControlService.LocalPageUrl(new Uri(page).AbsoluteUri));
        Assert.Null(BrowserControlService.LocalPageUrl(Path.Combine(_folder, "missing.html")));

        string script = Path.Combine(_folder, "run.bat");
        File.WriteAllText(script, "echo hi");
        Assert.Null(BrowserControlService.LocalPageUrl(script));
        Assert.Null(BrowserControlService.LocalPageUrl("index.html"));
    }

    [Fact]
    public void AnAttachedQuoteLeadsTheMessage()
    {
        string composed = ReplyContextAttachment.Compose("Line one\nLine two", "  Why is this?  ");
        Assert.Equal("> Line one\n> Line two\n\nWhy is this?", composed.Replace("\r\n", "\n"));
    }

    [Fact]
    public void ThePreviewIsOneQuotedLine()
    {
        string preview = ReplyContextAttachment.Preview("Research   is running\n in the background. " + new string('x', 200));
        Assert.StartsWith("“Research is running in the background.", preview);
        Assert.EndsWith("...”", preview);
        Assert.DoesNotContain('\n', preview);
    }
}
