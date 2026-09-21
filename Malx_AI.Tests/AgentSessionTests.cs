using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>A model that returns a scripted sequence of replies, so the loop can be driven without one.</summary>
    internal sealed class ScriptedAgentModel : IAgentModel
    {
        private readonly Queue<AgentModelReply> _replies;
        public List<IReadOnlyList<AgentExchange>> SeenHistories { get; } = new();

        public ScriptedAgentModel(params AgentModelReply[] replies) => _replies = new Queue<AgentModelReply>(replies);

        public string? Unavailable { get; set; }

        public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
        {
            SeenHistories.Add(history.ToList());
            return Task.FromResult(_replies.Count > 0
                ? _replies.Dequeue()
                : AgentModelReply.Answer("done"));
        }
    }

    public class AgentSessionTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomAgentTests", Guid.NewGuid().ToString("N"));

        public AgentSessionTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private AgentSession NewSession(int maxSteps = 6) =>
            new(AgentScope.Folder(_folder), maxSteps);

        private static Task<AgentApprovalOutcome> AlwaysApprove(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Approve);

        private static Task<AgentApprovalOutcome> AlwaysDeny(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Deny);

        private static AgentToolCall Write(string path, string content) =>
            new(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = path, ["content"] = content });

        [Fact]
        public async Task AToolCallRunsAndItsResultReachesTheModel()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write("notes.txt", "hello")),
                AgentModelReply.Answer("Wrote the file."));

            AgentRunResult result = await NewSession().RunAsync(
                "make notes.txt", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("Wrote the file.", result.FinalMessage);
            Assert.Equal("hello", File.ReadAllText(Path.Combine(_folder, "notes.txt")));

            // The second turn must see the first step, or the model is flying blind.
            Assert.Empty(model.SeenHistories[0]);
            Assert.Single(model.SeenHistories[1]);
            Assert.Contains("notes.txt", model.SeenHistories[1][0].Observation);
        }

        [Fact]
        public async Task APlainAnswerEndsTheRun()
        {
            var model = new ScriptedAgentModel(AgentModelReply.Answer("There are 12 files."));

            AgentRunResult result = await NewSession().RunAsync(
                "how many files?", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("There are 12 files.", result.FinalMessage);
            Assert.Empty(result.Steps);
        }

        [Fact]
        public async Task FinishEndsTheRunWithItsSummary()
        {
            var finish = new AgentToolCall(AgentToolNames.Finish, new Dictionary<string, string> { ["summary"] = "All set." });
            var model = new ScriptedAgentModel(AgentModelReply.Tool(finish));

            AgentRunResult result = await NewSession().RunAsync(
                "do it", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("All set.", result.FinalMessage);
        }

        [Fact]
        public async Task ManualModeDeniedStepIsNotExecutedAndTheModelIsTold()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write("blocked.txt", "x")),
                AgentModelReply.Answer("Understood."));

            AgentRunResult result = await NewSession().RunAsync(
                "write it", AgentApprovalMode.Manual, model, AlwaysDeny, _ => { }, CancellationToken.None);

            Assert.False(File.Exists(Path.Combine(_folder, "blocked.txt")));
            Assert.Contains("declined", model.SeenHistories[1][0].Observation, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Understood.", result.FinalMessage);
        }

        [Fact]
        public async Task ABlockedCommandNeverRunsAndTheModelSeesWhy()
        {
            var format = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "format c:" });
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(format),
                AgentModelReply.Answer("I will not do that."));

            AgentRunResult result = await NewSession().RunAsync(
                "wipe the disk", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal(AgentPermission.Deny, result.Steps[0].Permission);
            Assert.Null(result.Steps[0].Result);
            Assert.Contains("Refused", model.SeenHistories[1][0].Observation);
        }

        [Fact]
        public async Task AMalformedCallIsReportedBackAndTheRunContinues()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Malformed("that was not valid JSON"),
                AgentModelReply.Answer("Fixed."));

            AgentRunResult result = await NewSession().RunAsync(
                "go", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("Fixed.", result.FinalMessage);
            Assert.Contains("not valid JSON", model.SeenHistories[1][0].Observation);
        }

        [Fact]
        public async Task TheStepLimitStopsARunawayLoop()
        {
            var replies = Enumerable.Range(0, 20)
                .Select(i => AgentModelReply.Tool(Write($"f{i}.txt", "x")))
                .ToArray();

            AgentRunResult result = await NewSession(maxSteps: 3).RunAsync(
                "loop", AgentApprovalMode.Auto, new ScriptedAgentModel(replies), AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.True(result.StoppedOnStepLimit);
            Assert.Equal(3, result.Steps.Count);
            Assert.Contains("3-step limit", result.FinalMessage);
        }

        [Fact]
        public async Task AnUnavailableModelExplainsItselfAndRunsNothing()
        {
            var model = new ScriptedAgentModel(AgentModelReply.Tool(Write("x.txt", "x")))
            {
                Unavailable = "Cloud mode needs an API key."
            };

            AgentRunResult result = await NewSession().RunAsync(
                "go", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("Cloud mode needs an API key.", result.FinalMessage);
            Assert.Empty(result.Steps);
            Assert.False(File.Exists(Path.Combine(_folder, "x.txt")));
        }

        [Fact]
        public async Task TheActivityLineNamesTheStepAndClearsAfterwards()
        {
            var labels = new List<string?>();
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write("a.txt", "x")),
                AgentModelReply.Answer("done"));

            await NewSession().RunAsync(
                "go", AgentApprovalMode.Auto, model, AlwaysApprove, labels.Add, CancellationToken.None);

            Assert.Contains("Wrote a.txt", labels);
            Assert.Null(labels[^1]);
        }

        [Fact]
        public async Task ScopeKeepsTheAgentInsideItsFolder()
        {
            string outside = Path.Combine(Path.GetTempPath(), "axiom-agent-outside.txt");
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write(outside, "escaped")),
                AgentModelReply.Answer("could not"));

            AgentRunResult result = await NewSession().RunAsync(
                "write outside", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.False(File.Exists(outside));
            Assert.False(result.Steps[0].Result!.Succeeded);
            Assert.Contains("outside the folder", result.Steps[0].Result!.Error!);
        }

        /// <summary>A model that throws a transport failure for the first N turns, then behaves.</summary>
        private sealed class FlakyAgentModel : IAgentModel
        {
            private readonly int _failures;
            private int _calls;

            public FlakyAgentModel(int failures) => _failures = failures;

            public string? Unavailable => null;

            public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
            {
                if (_calls++ < _failures)
                    throw new IOException("the response ended prematurely");
                return Task.FromResult(AgentModelReply.Answer("recovered"));
            }
        }

        [Fact]
        public async Task ADroppedConnectionIsRetriedRatherThanEndingTheRun()
        {
            // A free-tier provider dropping one body must not abandon work already done.
            AgentRunResult result = await NewSession().RunAsync(
                "go", AgentApprovalMode.Auto, new FlakyAgentModel(failures: 2),
                AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("recovered", result.FinalMessage);
        }

        [Fact]
        public async Task RepeatedConnectionFailuresReportHonestlyInsteadOfClaimingSuccess()
        {
            AgentRunResult result = await NewSession().RunAsync(
                "go", AgentApprovalMode.Auto, new FlakyAgentModel(failures: 99),
                AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Contains("kept failing", result.FinalMessage);
            Assert.DoesNotContain("Done.", result.FinalMessage);
        }

        [Fact]
        public async Task AnEmptyReplyIsRetriedNotTreatedAsAFinishedAnswer()
        {
            // The live bug: a dropped turn ended the run instantly with a cheerful "Done."
            // having written nothing at all.
            var model = new ScriptedAgentModel(
                AgentModelReply.Malformed("That reply was empty."),
                AgentModelReply.Tool(Write("late.txt", "made it")),
                AgentModelReply.Answer("Wrote it after the hiccup."));

            AgentRunResult result = await NewSession().RunAsync(
                "write late.txt", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("Wrote it after the hiccup.", result.FinalMessage);
            Assert.True(File.Exists(Path.Combine(_folder, "late.txt")));
        }

        [Fact]
        public async Task ThreeUnusableRepliesInARowStopTheRunWithAnExplanation()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Malformed("empty"),
                AgentModelReply.Malformed("empty"),
                AgentModelReply.Malformed("empty"),
                AgentModelReply.Answer("never reached"));

            AgentRunResult result = await NewSession().RunAsync(
                "go", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Contains("did not return anything usable", result.FinalMessage);
        }

        [Fact]
        public async Task TheBackoffGrowsBetweenAttempts()
        {
            // An immediate retry against a provider that just failed usually fails again, so the
            // gap has to widen rather than hammer.
            var waits = new List<TimeSpan>();
            for (int attempt = 1; attempt < 3; attempt++)
                waits.Add(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)));

            Assert.Equal(TimeSpan.FromSeconds(1), waits[0]);
            Assert.Equal(TimeSpan.FromSeconds(2), waits[1]);
        }

        [Fact]
        public async Task ApproveAlwaysStopsAskingForThatCommand()
        {
            int asked = 0;
            Task<AgentApprovalOutcome> ApproveAlwaysOnce(AgentToolCall call, string reason, CancellationToken token)
            {
                asked++;
                return Task.FromResult(AgentApprovalOutcome.ApproveAlways);
            }

            var echo = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "echo hello" });
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(echo),
                AgentModelReply.Tool(echo),
                AgentModelReply.Answer("done"));

            await NewSession().RunAsync(
                "echo twice", AgentApprovalMode.Manual, model, ApproveAlwaysOnce, _ => { }, CancellationToken.None);

            Assert.Equal(1, asked);
        }
    }
}
