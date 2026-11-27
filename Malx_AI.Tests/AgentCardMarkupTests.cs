using System.Collections.Generic;
using Malx_AI;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentCardMarkupTests
    {
        [Fact]
        public void PlanIsACompactChecklistWithoutMarkdownEmphasis()
        {
            var plan = new AgentPlan(new List<AgentPlanItem>
            {
                new("Read files", AgentPlanStatus.Completed),
                new("Run it", AgentPlanStatus.InProgress),
                new("Write report", AgentPlanStatus.Pending)
            });

            string text = AgentCardMarkup.Plan(plan);

            Assert.StartsWith("```axiom-plan\nPlan · 1 of 3 done\n", text);
            Assert.Contains("✓ Read files\n", text);
            Assert.Contains("▸ Run it\n", text);
            Assert.Contains("○ Write report\n", text);
            Assert.DoesNotContain("~~", text);
            Assert.DoesNotContain("**", text);
        }

        [Fact]
        public void EmptyPlanAndNoStepsRenderNothing()
        {
            Assert.Equal(string.Empty, AgentCardMarkup.Plan(AgentPlan.Empty));
            Assert.Equal(string.Empty, AgentCardMarkup.Activity(new string[0]));
        }

        [Fact]
        public void ActivityIsOneFencedBlockThatCannotBeClosedByAStepLine()
        {
            string text = AgentCardMarkup.Activity(new[] { "✓ Wrote a.txt +3 -0", "✗ Ran ```bad``` — failed" });

            Assert.StartsWith("```axiom-activity\n", text);
            Assert.EndsWith("\n```", text);
            Assert.Equal(2, text.Split("```").Length - 1);
        }

        [Fact]
        public void LineCountsComeFromTheToolArguments()
        {
            var write = new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = "a.txt", ["content"] = "1\n2\n3\n" });
            var edit = new AgentToolCall(AgentToolNames.EditFile, new Dictionary<string, string> { ["path"] = "a.txt", ["old_string"] = "x", ["new_string"] = "y\nz" });
            var read = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string> { ["path"] = "a.txt" });

            Assert.Equal("+3 -0", AgentCardMarkup.LineCounts(write));
            Assert.Equal("+2 -1", AgentCardMarkup.LineCounts(edit));
            Assert.Equal(string.Empty, AgentCardMarkup.LineCounts(read));
        }

        [Fact]
        public void FinishedCardSaysHowManyStepsItWorkedFor()
        {
            Assert.Equal("Worked for 1 step · View activity", AgentCardMarkup.WorkedForLabel(1));
            Assert.Equal("Worked for 9 steps · View activity", AgentCardMarkup.WorkedForLabel(9));
        }

        [Fact]
        public void HistoryRestoreDropsTheNewCardBlocks()
        {
            string card = "```axiom-plan\nPlan · 1 of 2 done\n✓ a\n▸ b\n```\n\n```axiom-activity\n✓ Ran x\n```\n\nHere is the answer.";

            Assert.Equal("Here is the answer.", WorkplaceHistoryRestore.CleanAgentCard(card));
        }
    }
}
