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
    public class AgentPlanTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomAgentPlanTests", Guid.NewGuid().ToString("N"));

        public AgentPlanTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private static AgentToolCall Plan(string plan) =>
            new(AgentToolNames.UpdatePlan, new Dictionary<string, string> { ["plan"] = plan });

        private static Task<AgentApprovalOutcome> Approve(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Approve);

        [Fact]
        public void NativeJsonArrayIsRead()
        {
            AgentPlan? plan = AgentPlan.TryParse(
                "[{\"step\":\"Read the code\",\"status\":\"completed\"},{\"step\":\"Fix the bug\",\"status\":\"in_progress\"},{\"step\":\"Run the tests\",\"status\":\"pending\"}]",
                null, AgentPlan.MaxItemsFull, out string? error);

            Assert.Null(error);
            Assert.NotNull(plan);
            Assert.Equal(3, plan!.Items.Count);
            Assert.Equal("Fix the bug", plan.Current?.Step);
            Assert.Equal(1, plan.CompletedCount);
        }

        [Fact]
        public void FlatChecklistOnOneLineIsRead()
        {
            AgentPlan? plan = AgentPlan.TryParse("[x] read the file | [>] fix the bug | [ ] test it", null, AgentPlan.MaxItemsCompact, out string? error);

            Assert.Null(error);
            Assert.Equal(new[] { AgentPlanStatus.Completed, AgentPlanStatus.InProgress, AgentPlanStatus.Pending },
                plan!.Items.Select(item => item.Status));
        }

        [Fact]
        public void StatusWordsAreAccepted()
        {
            AgentPlan? plan = AgentPlan.TryParse("done: write index.html\nin progress: style it\n- todo: check it", null, 12, out string? error);

            Assert.Null(error);
            Assert.Equal("style it", plan!.Current?.Step);
            Assert.Equal(3, plan.Items.Count);
        }

        [Fact]
        public void TwoStepsInProgressAreRejected()
        {
            Assert.Null(AgentPlan.TryParse("[>] a\n[>] b", null, 12, out string? error));
            Assert.Contains("At most one", error);
        }

        [Fact]
        public void DuplicateAndOverlongPlansAreRejected()
        {
            Assert.Null(AgentPlan.TryParse("[ ] a\n[ ] A", null, 12, out string? duplicate));
            Assert.Contains("twice", duplicate);

            Assert.Null(AgentPlan.TryParse("[ ] 1|[ ] 2|[ ] 3|[ ] 4|[ ] 5|[ ] 6", null, AgentPlan.MaxItemsCompact, out string? tooLong));
            Assert.Contains("5 or fewer", tooLong);
        }

        [Fact]
        public void ALineWithNoReadableStatusIsAnErrorNotAGuess()
        {
            Assert.Null(AgentPlan.TryParse("just some prose about the work", null, 12, out string? error));
            Assert.Contains("[ ] pending", error);
        }

        [Fact]
        public void TheObservationIsShortCounts()
        {
            AgentPlan plan = AgentPlan.TryParse("[x] a\n[>] b\n[ ] c", null, 12, out _)!;
            Assert.Equal("Plan updated: 1/3 completed, 1 in progress, 1 pending. Now: \"b\".", plan.DescribeUpdate());
        }

        [Fact]
        public async Task UpdatePlanIsHandledByTheSessionAndDoesNotUseAStep()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Plan("[>] write it\n[ ] check it")),
                AgentModelReply.Tool(Plan("[x] write it\n[x] check it")),
                AgentModelReply.Answer("Done and checked."));
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 1);
            var seen = new List<AgentPlan>();
            session.PlanChanged = seen.Add;

            AgentRunResult result = await session.RunAsync("task", AgentApprovalMode.Auto, model, Approve, _ => { }, CancellationToken.None);

            Assert.Equal("Done and checked.", result.FinalMessage);
            Assert.False(result.StoppedOnStepLimit);
            Assert.Equal(2, seen.Count);
            Assert.False(session.Plan.HasOpenItems);
        }

        [Fact]
        public async Task FinishingWithOpenStepsIsSentBackOnce()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Plan("[x] write it\n[ ] check it")),
                AgentModelReply.Answer("Done."),
                AgentModelReply.Answer("Done, the check was not needed."));

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 8).RunAsync(
                "task", AgentApprovalMode.Auto, model, Approve, _ => { }, CancellationToken.None);

            Assert.Equal("Done, the check was not needed.", result.FinalMessage);
            Assert.Contains(model.SeenHistories[2], exchange => exchange.Observation.Contains("your plan still has open steps"));
        }

        [Fact]
        public async Task ACarriedPlanDoesNotBlockAFinishWhenThisRunNeverUpdatedIt()
        {
            var model = new ScriptedAgentModel(AgentModelReply.Answer("Here is the answer."));
            var session = new AgentSession(AgentScope.Folder(_folder), 8)
            {
                Plan = AgentPlan.TryParse("[>] something from earlier", null, 12, out _)!
            };

            AgentRunResult result = await session.RunAsync("question", AgentApprovalMode.Auto, model, Approve, _ => { }, CancellationToken.None);

            Assert.Equal("Here is the answer.", result.FinalMessage);
        }

        [Fact]
        public async Task AnInvalidPlanComesBackAsAnErrorTheModelCanFix()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Plan("[>] a\n[>] b")),
                AgentModelReply.Answer("ok"));

            await new AgentSession(AgentScope.Folder(_folder), 8).RunAsync(
                "task", AgentApprovalMode.Auto, model, Approve, _ => { }, CancellationToken.None);

            Assert.StartsWith("ERROR: At most one", model.SeenHistories[1].Last().Observation);
        }

        [Fact]
        public async Task PlanUpdatesInARowAreCapped()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Plan("[>] a\n[ ] b")),
                AgentModelReply.Tool(Plan("[>] a\n[ ] b\n[ ] c")),
                AgentModelReply.Tool(Plan("[>] a\n[ ] c")),
                AgentModelReply.Answer("ok"));

            await new AgentSession(AgentScope.Folder(_folder), 8).RunAsync(
                "task", AgentApprovalMode.Auto, model, Approve, _ => { }, CancellationToken.None);

            Assert.StartsWith("Blocked: the plan was just updated", model.SeenHistories[3].Last().Observation);
        }

        [Fact]
        public void EveryToolSchemaIncludesUpdatePlanAndItIsKnown()
        {
            Assert.True(AgentToolNames.IsKnown("update_plan"));
            Assert.Contains(AgentToolSchemas.All(), tool => tool.Name == AgentToolNames.UpdatePlan);
        }

        [Fact]
        public void OnlyTheCompactTierListsThePlanToolInTheFlatProtocol()
        {
            string compact = AgentPromptBuilder.Build(AgentTier.Compact, AgentScope.Folder(_folder), AgentApprovalMode.Auto, includeProjectInstructions: false);
            string micro = AgentPromptBuilder.Build(AgentTier.Micro, AgentScope.Folder(_folder), AgentApprovalMode.Auto, includeProjectInstructions: false);

            Assert.Contains("update_plan", compact);
            Assert.DoesNotContain("update_plan", micro);
        }
    }
}
