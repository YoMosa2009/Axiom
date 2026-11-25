using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentRunCompactionTests
    {
        /// <summary>A model that also writes checkpoints, with a scripted reply.</summary>
        private sealed class SummarizingModel : IAgentModel, IAgentSummarizer
        {
            private readonly Queue<string?> _summaries;
            public int SummaryCalls { get; private set; }
            public int Rewrites { get; private set; }
            public string? Unavailable => null;

            public SummarizingModel(params string?[] summaries) => _summaries = new Queue<string?>(summaries);

            public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
                => Task.FromResult(AgentModelReply.Answer("done"));

            public Task<string?> SummarizeAsync(string goal, IReadOnlyList<AgentExchange> history, string instruction, CancellationToken token)
            {
                SummaryCalls++;
                return Task.FromResult(_summaries.Count > 0 ? _summaries.Dequeue() : null);
            }

            public void HistoryRewritten() => Rewrites++;
        }

        private static AgentExchange Write(string path, int chars) => new(
            new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = path, ["content"] = new string('x', chars) }),
            $"Wrote {chars} characters to {path}.");

        private static AgentExchange Run(string command, int outputChars) => new(
            new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = command }),
            new string('o', outputChars) + "\n[Exit code: 0]");

        private static List<AgentExchange> LongHistory(int count = 20)
        {
            var history = new List<AgentExchange>();
            for (int i = 0; i < count; i++)
                history.Add(i % 2 == 0 ? Write($"C:\\proj\\file{i}.js", 3000) : Run($"node test{i}.js", 3000));
            return history;
        }

        private static string GoodCheckpoint(string filesSection) =>
            "## Objective\nBuild the site.\n## User requests\n\"build the site\"\n## Decisions\nPlain JS.\n## Work state\nCompleted: pages. In progress: tests.\n"
            + "## Errors and fixes\n(none)\n## Next step\nRun the tests.\n## Files changed\n" + filesSection + "\n## Important context\n(none)";

        [Fact]
        public void NothingHappensBelowTheTrigger()
        {
            var compactor = new AgentRunCompactor(new AgentCompactionPolicy(100_000, AgentCompactionMode.Deterministic));
            Assert.False(compactor.ShouldCompact("goal", LongHistory(10)));
        }

        [Fact]
        public void TheMessageCountAloneTriggersCompaction()
        {
            var compactor = new AgentRunCompactor(new AgentCompactionPolicy(1_000_000, AgentCompactionMode.Deterministic) { MaxExchanges = 12 });
            Assert.True(compactor.ShouldCompact("goal", LongHistory(13)));
        }

        [Fact]
        public async Task DeterministicCompactionPrunesOldStepsAndKeepsRecentOnesWordForWord()
        {
            List<AgentExchange> history = LongHistory();
            AgentExchange lastBefore = history[^1];
            var policy = new AgentCompactionPolicy(20_000, AgentCompactionMode.Deterministic);
            var compactor = new AgentRunCompactor(policy);
            var model = new SummarizingModel();

            string? description = await compactor.CompactIfNeededAsync("goal", history, model, ["C:\\proj\\file0.js"], null, null, CancellationToken.None);

            Assert.NotNull(description);
            Assert.Same(lastBefore, history[^1]);
            Assert.True(compactor.EstimateTokens("goal", history) < policy.TriggerTokens);
            Assert.Equal(0, model.SummaryCalls);
            Assert.Equal(1, model.Rewrites);
            // No old step still carries a whole file body.
            Assert.DoesNotContain(history.Take(history.Count - policy.KeepRecentExchanges),
                exchange => exchange.Call.Arguments.TryGetValue("content", out string? content) && content.Length > 200);
        }

        [Fact]
        public async Task ADeterministicDigestReplacesOldStepsWhenPruningIsNotEnough()
        {
            List<AgentExchange> history = LongHistory(60);
            var compactor = new AgentRunCompactor(new AgentCompactionPolicy(6_000, AgentCompactionMode.Deterministic));

            await compactor.CompactIfNeededAsync("goal", history, new SummarizingModel(), ["C:\\proj\\file0.js"], null, null, CancellationToken.None);

            Assert.Equal(AgentRunCompactor.SummaryTool, history[0].Call.Tool);
            Assert.Contains("Files changed in this run (Axiom's record): C:\\proj\\file0.js", history[0].Observation);
            Assert.Equal(5, history.Count);
        }

        [Fact]
        public async Task AModelCheckpointHasItsFileListReplacedByAxiomsRecord()
        {
            List<AgentExchange> history = LongHistory(60);
            var model = new SummarizingModel(GoodCheckpoint("- C:\\proj\\file0.js\n- C:\\proj\\invented.js"));
            var compactor = new AgentRunCompactor(new AgentCompactionPolicy(6_000, AgentCompactionMode.ModelSummary));

            string? description = await compactor.CompactIfNeededAsync("goal", history, model, ["C:\\proj\\file0.js"], null, null, CancellationToken.None);

            Assert.Contains("model-written checkpoint", description);
            string checkpoint = history[0].Observation;
            Assert.Contains("- C:\\proj\\file0.js\n(from Axiom's record of this run)", checkpoint.Replace("\r", ""));
            Assert.Contains("Not in Axiom's record, so treat as NOT changed: C:\\proj\\invented.js", checkpoint);
            Assert.Contains("## Next step\nRun the tests.", checkpoint.Replace("\r", ""));
        }

        [Fact]
        public async Task AFewPrunedStepsAreNotWorthAModelCheckpoint()
        {
            // Live run, Hybrid Local 2026-11-25: with only a few large steps to replace, the model
            // was asked twice per compaction for a checkpoint longer than the pruned steps.
            var history = new List<AgentExchange>
            {
                Write("C:\\proj\\a.js", 6000), Write("C:\\proj\\b.js", 6000), Write("C:\\proj\\c.js", 6000),
                Run("node a.js", 200), Run("node b.js", 200), Run("node c.js", 200), Run("node d.js", 200),
            };
            var model = new SummarizingModel(GoodCheckpoint("- C:\\proj\\a.js"));
            var compactor = new AgentRunCompactor(new AgentCompactionPolicy(6_000, AgentCompactionMode.ModelSummary) { TargetRatio = 0.05 });

            string? description = await compactor.CompactIfNeededAsync("goal", history, model,
                ["C:\\proj\\a.js", "C:\\proj\\b.js", "C:\\proj\\c.js"], null, null, CancellationToken.None);

            Assert.Equal(0, model.SummaryCalls);
            Assert.StartsWith("pruned old steps", description);
            Assert.Equal(7, history.Count);
        }

        [Fact]
        public void ACheckpointIsCheckedAgainstTheOriginalStepsSize()
        {
            // The same checkpoint is too long for a 100-character span and fine for the original steps.
            Assert.Null(AgentRunCompactor.ValidateAndRepair(GoodCheckpoint("(none)"), [], null, replacedChars: 100, out _));
            Assert.NotNull(AgentRunCompactor.ValidateAndRepair(GoodCheckpoint("(none)"), [], null, replacedChars: 18_000, out _));
        }

        [Fact]
        public void ADigestOfADigestDoesNotRepeatTheFileListOrPlan()
        {
            AgentPlan plan = AgentPlan.TryParse("[>] a\n[ ] b", null, 12, out _)!;
            string first = AgentRunCompactor.BuildDigest([Run("one", 10)], ["x.js"], plan);
            var older = new List<AgentExchange>
            {
                new(new AgentToolCall(AgentRunCompactor.SummaryTool, new Dictionary<string, string>()), first),
                Run("two", 10)
            };

            string second = AgentRunCompactor.BuildDigest(older, ["x.js", "y.js"], plan);

            Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "Files changed in this run"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "Current plan"));
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(second, "EARLIER STEPS"));
            Assert.Contains("run_command (command: one)", second);
            Assert.Contains("run_command (command: two)", second);
        }

        [Fact]
        public async Task AnUnusableCheckpointGetsOneNudgeThenTheDigestIsUsed()
        {
            List<AgentExchange> history = LongHistory(60);
            var model = new SummarizingModel("I summarised it.", "## Objective\nstill no template");
            var compactor = new AgentRunCompactor(new AgentCompactionPolicy(6_000, AgentCompactionMode.ModelSummary));

            string? description = await compactor.CompactIfNeededAsync("goal", history, model, [], null, null, CancellationToken.None);

            Assert.Equal(2, model.SummaryCalls);
            Assert.Contains("deterministic digest", description);
            Assert.Contains("[EARLIER STEPS OF THIS RUN, COMPACTED BY AXIOM]", history[0].Observation);
        }

        [Fact]
        public void ACheckpointMustBeShorterThanWhatItReplaces()
        {
            Assert.Null(AgentRunCompactor.ValidateAndRepair(GoodCheckpoint("(none)"), [], null, replacedChars: 50, out string? problem));
            Assert.Contains("longer", problem);
        }

        [Fact]
        public void ThinkingIsStrippedFromACheckpoint()
        {
            string? repaired = AgentRunCompactor.ValidateAndRepair("<think>hmm</think>" + GoodCheckpoint("(none)"), [], null, 100_000, out _);
            Assert.NotNull(repaired);
            Assert.DoesNotContain("hmm", repaired);
        }

        [Fact]
        public void UserMessagesSurvivePruning()
        {
            var history = LongHistory(10);
            history.Insert(1, new AgentExchange(new AgentToolCall(AgentSession.UserMessageTool, new Dictionary<string, string>()),
                "Please use blue, not red. " + new string('z', 600)));

            List<AgentExchange> pruned = AgentRunCompactor.Prune(history, new AgentCompactionPolicy(10_000, AgentCompactionMode.Deterministic));

            Assert.Same(history[1], pruned[1]);
        }

        [Fact]
        public async Task CompactionStopsWhenItCannotGetUnderTheLimit()
        {
            // A single recent observation bigger than the whole window: nothing compaction does helps.
            var policy = new AgentCompactionPolicy(3_000, AgentCompactionMode.Deterministic) { RecentObservationMaxChars = 100_000 };
            var compactor = new AgentRunCompactor(policy);
            var model = new SummarizingModel();
            List<AgentExchange> history = LongHistory(10);
            history.Add(Run("huge", 60_000));

            for (int round = 0; round < 4; round++)
            {
                history.AddRange(LongHistory(3));
                history.Add(Run("huge again", 60_000));
                await compactor.CompactIfNeededAsync("goal", history, model, [], null, null, CancellationToken.None);
            }

            Assert.True(compactor.Disabled);
            Assert.Equal(2, compactor.Compactions);
        }

        [Fact]
        public async Task TheSessionCompactsALongRunBetweenSteps()
        {
            var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AxiomCompactionSession", Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                var replies = new List<AgentModelReply>();
                for (int i = 0; i < 14; i++)
                {
                    replies.Add(AgentModelReply.Tool(new AgentToolCall(AgentToolNames.WriteFile,
                        new Dictionary<string, string> { ["path"] = $"f{i}.txt", ["content"] = new string((char)('a' + i % 26), 4000) })));
                }
                replies.Add(AgentModelReply.Answer("Wrote them."));
                var model = new ScriptedAgentModel(replies.ToArray());
                var session = new AgentSession(AgentScope.Folder(folder), 30);
                var compactions = new List<string>();
                session.Compacted = compactions.Add;

                AgentRunResult result = await session.RunAsync("write files", AgentApprovalMode.Auto, model,
                    (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None,
                    policy: AgentTurnPolicy.Default with { Compaction = new AgentCompactionPolicy(12_000, AgentCompactionMode.Deterministic) });

                Assert.Equal("Wrote them.", result.FinalMessage);
                Assert.NotEmpty(compactions);
                // Old steps no longer carry their 4,000-character file bodies.
                Assert.True(model.SeenHistories.Last()
                    .Count(exchange => exchange.Call.Arguments.TryGetValue("content", out string? body) && body.Length >= 4000) <= 7);
            }
            finally
            {
                System.IO.Directory.Delete(folder, recursive: true);
            }
        }

        [Fact]
        public void PolicyForPicksTheModeBySize()
        {
            Assert.Equal(AgentCompactionMode.ModelSummary, AgentCompactionPolicy.For(131072, true, LocalModelSizeClass.Unknown, 8192, 4000, 120).Mode);
            Assert.Equal(AgentCompactionMode.ModelSummary, AgentCompactionPolicy.For(32768, false, LocalModelSizeClass.TenBPlus, 2048, 3000).Mode);
            Assert.Equal(AgentCompactionMode.Deterministic, AgentCompactionPolicy.For(32768, false, LocalModelSizeClass.FourToTenB, 2048, 3000).Mode);
            Assert.Equal(AgentCompactionMode.Deterministic, AgentCompactionPolicy.For(8192, false, LocalModelSizeClass.OneToFourB, 2048, 3000).Mode);
            Assert.Equal(52, AgentCompactionPolicy.For(131072, true, LocalModelSizeClass.Unknown, 8192, 4000, 120).MaxExchanges);
        }
    }
}
