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
    /// <summary>A run finishes the task by itself instead of waiting to be told "continue".</summary>
    public sealed class AgentAutonomyTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "axiom-autonomy-" + Guid.NewGuid().ToString("N"));
        private readonly List<TimeSpan> _waits = new();

        public AgentAutonomyTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); }
            catch (IOException) { }
        }

        private static AgentModelReply Tool(string tool, params (string Key, string Value)[] arguments) =>
            AgentModelReply.Tool(new AgentToolCall(tool, arguments.ToDictionary(pair => pair.Key, pair => pair.Value)));

        private static AgentModelReply Write(string path) => Tool(AgentToolNames.WriteFile, ("path", path), ("content", "x"));

        private static AgentModelReply Plan(string plan) => Tool(AgentToolNames.UpdatePlan, ("plan", plan));

        private AgentTurnPolicy Autonomous(int extensions = 3) => new()
        {
            Autonomy = AgentAutonomy.For(AgentTier.Full, metered: false) with
            {
                BudgetExtensions = extensions,
                Wait = (delay, _) => { _waits.Add(delay); return Task.CompletedTask; }
            }
        };

        private Task<AgentRunResult> Run(IAgentModel model, AgentTurnPolicy policy, int maxSteps = 40) =>
            new AgentSession(AgentScope.Folder(_folder), maxSteps).RunAsync("goal", AgentApprovalMode.Auto, model,
                (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None, policy: policy);

        [Fact]
        public async Task AnAnswerWithOpenPlanItemsIsCarriedOnUntilTheyAreDone()
        {
            var model = new ScriptedAgentModel(
                Plan("[>] a | [ ] b | [ ] c"),
                Write("a.txt"), AgentModelReply.Answer("Finished a."),
                Write("b.txt"), AgentModelReply.Answer("Finished b."),
                Write("c.txt"), Plan("[x] a | [x] b | [x] c"), AgentModelReply.Answer("All done."));

            AgentRunResult result = await Run(model, Autonomous());

            Assert.Equal("All done.", result.FinalMessage);
            Assert.True(File.Exists(Path.Combine(_folder, "c.txt")));
        }

        [Fact]
        public async Task AskingWhetherToContinueIsAnsweredYes()
        {
            var model = new ScriptedAgentModel(
                Write("a.txt"), AgentModelReply.Answer("Part one is in place. Shall I continue with part two?"),
                Write("b.txt"), AgentModelReply.Answer("Both parts are done."));

            AgentRunResult result = await Run(model, Autonomous());

            Assert.Equal("Both parts are done.", result.FinalMessage);
        }

        [Fact]
        public async Task AReplyThatOnlyAnnouncesTheWorkIsSentBackToDoIt()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Answer("Creating hello.py and running it with python hello.py."),
                Write("hello.py"), AgentModelReply.Answer("hello.py is written."));

            AgentRunResult result = await Run(model, Autonomous());

            Assert.Equal("hello.py is written.", result.FinalMessage);
        }

        [Theory]
        [InlineData("I'll start by reading the file.", true)]
        [InlineData("Let me check the folder first.", true)]
        [InlineData("Created hello.py; it prints 1 to 5.", false)]
        [InlineData("Let me know if you want changes.", false)]
        [InlineData("Running it needs Python installed. Do you have it?", false)]
        public void OnlyABareAnnouncementCounts(string answer, bool expected) =>
            Assert.Equal(expected, AgentAutonomy.OnlyAnnounces(answer));

        [Fact]
        public async Task AStatedNeedForTheUserEndsTheRun()
        {
            var model = new ScriptedAgentModel(
                Plan("[>] a | [ ] deploy"), Write("a.txt"),
                AgentModelReply.Answer("Built it.\nNEEDS YOU: shall I proceed with the deployment?"));

            AgentRunResult result = await Run(model, Autonomous());

            Assert.Contains("NEEDS YOU", result.FinalMessage);
        }

        [Fact]
        public async Task AModelThatOnlyTalksIsNotSentBackForever()
        {
            var replies = new List<AgentModelReply> { Plan("[>] a | [ ] b") };
            replies.AddRange(Enumerable.Range(1, 10).Select(i => AgentModelReply.Answer("Still thinking " + i)));
            var model = new ScriptedAgentModel(replies.ToArray());

            AgentRunResult result = await Run(model, Autonomous());

            Assert.Equal("Still thinking 3", result.FinalMessage);
        }

        [Fact]
        public async Task TheStepBudgetIsExtendedWhileWorkAdvances()
        {
            var replies = Enumerable.Range(0, 12).Select(i => Write($"f{i}.txt")).ToList();
            replies.Add(AgentModelReply.Answer("Twelve files written."));

            AgentRunResult result = await Run(new ScriptedAgentModel(replies.ToArray()), Autonomous(), maxSteps: 5);

            Assert.Equal("Twelve files written.", result.FinalMessage);
            Assert.False(result.StoppedOnStepLimit);
        }

        [Fact]
        public async Task TheExtensionsRunOut()
        {
            var replies = Enumerable.Range(0, 30).Select(i => Write($"f{i}.txt")).ToArray();

            AgentRunResult result = await Run(new ScriptedAgentModel(replies), Autonomous(extensions: 1), maxSteps: 5);

            Assert.True(result.StoppedOnStepLimit);
            Assert.Equal(10, result.Steps.Count);
        }

        [Fact]
        public async Task AStalledModelIsWaitedForAndAskedAgain()
        {
            var model = new StallingModel(stalls: 2);

            AgentRunResult result = await Run(model, Autonomous());

            Assert.Equal("Done after waiting.", result.FinalMessage);
            Assert.Equal([TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)], _waits);
        }

        [Fact]
        public async Task WithoutAutonomyAStallStillEndsTheRun()
        {
            AgentRunResult result = await Run(new StallingModel(stalls: 1), new AgentTurnPolicy());

            Assert.Equal("stalled", result.FinalMessage);
        }

        [Theory]
        [InlineData("Shall I continue?", true)]
        [InlineData("Say **continue** and I will do the rest.", true)]
        [InlineData("Would you like me to proceed with step 3?", true)]
        [InlineData("Let me know if you want me to continue.", true)]
        [InlineData("All done. The app runs with start.bat.", false)]
        [InlineData("Let me know if you have questions.", false)]
        public void OnlyAnEndingThatWaitsCountsAsAsking(string answer, bool expected) =>
            Assert.Equal(expected, AgentAutonomy.AsksToContinue(answer));

        private sealed class StallingModel(int stalls) : IAgentModel
        {
            private int _left = stalls;

            public string? Unavailable => null;

            public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token) =>
                _left-- > 0
                    ? throw new AgentStalledException("stalled")
                    : Task.FromResult(AgentModelReply.Answer("Done after waiting."));
        }
    }
}
