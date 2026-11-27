using System.Linq;
using System.Collections.Generic;
using Malx_AI;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>Fixes from the owner's 2026-11-26 Workplace logs (DeepSeek and Hybrid runs).</summary>
    public class WorkplaceHarnessFixTests
    {
        [Theory]
        [InlineData("Start-Sleep -Seconds 550; Get-Content log.txt -Tail 5", true)]
        [InlineData("cd F:\\x; Start-Sleep 900", true)]
        [InlineData("sleep 30", true)]
        [InlineData("timeout /t 60", true)]
        [InlineData("[System.Threading.Thread]::Sleep(5000)", true)]
        [InlineData("py -3.12 verify.py --N 1000", false)]
        [InlineData("Get-Content sleep_notes.txt", false)]
        public void SleepCommandsAreRecognised(string command, bool expected)
        {
            Assert.Equal(expected, AgentToolExecutor.IsSleepCommand(command));
        }

        [Fact]
        public void WaitProcessCountsAsAWaitButNotASleep()
        {
            Assert.True(AgentToolExecutor.IsWaitCommand("Wait-Process -Id 1234"));
            Assert.False(AgentToolExecutor.IsSleepCommand("Wait-Process -Id 1234"));
        }

        [Fact]
        public void AnswerToAMidRunMessageIsShownInFullEvenWhenShort()
        {
            Assert.True(AgentNarration.ShouldShowInFull("Still running: 33% done, no failures.", answeringUser: true));
            Assert.False(AgentNarration.ShouldShowInFull("Let me check the log.", answeringUser: false));
        }

        [Fact]
        public void LongModelTextIsShownInFullInsteadOfBeingCut()
        {
            string longUpdate = new string('a', 10) + " " + string.Join(" ", new string[60].Select(_ => "progress"));
            Assert.True(AgentNarration.ShouldShowInFull(longUpdate, answeringUser: false));
        }

        [Fact]
        public void ForUserKeepsParagraphsAndDropsThinkBlocks()
        {
            string said = AgentNarration.ForUser("<think>plan it</think>Here is the status.\n\n| a | b |\n|---|---|\n| 1 | 2 |")!;
            Assert.DoesNotContain("plan it", said);
            Assert.Contains("| 1 | 2 |", said);
            Assert.StartsWith("Here is the status.", said);
        }

        [Fact]
        public void HistoryIsRebuiltFromCardsWithoutRunBookkeeping()
        {
            var cards = new List<(string, string)>
            {
                ("user", "Verify Legendre up to 1e8"),
                ("agent", "**Plan** (1/2)\n- ✓ Read files\n- ▸ **Run it**\n\n- ✓ Ran py -3.12 mr_verify.py (5.0s)\n\n› Let me check.\n\n_Continuing below with your message_"),
                ("user", "any updates?"),
                ("agent", "33% done, 0 failures.\n\n### Steps Executed (2)\n- ✓ Ran Get-Content log\n- ✓ Read results.txt"),
                ("notice", "The agent is still working."),
                ("agent", "Stopped.")
            };

            List<(string Role, string Content)> turns = WorkplaceHistoryRestore.FromCards(cards);

            Assert.Equal(3, turns.Count);
            Assert.Equal(("user", "Verify Legendre up to 1e8"), turns[0]);
            Assert.Equal(("user", "any updates?"), turns[1]);
            Assert.Equal("assistant", turns[2].Role);
            Assert.Equal("33% done, 0 failures.", turns[2].Content);
        }

        [Fact]
        public void SplitRunCardsJoinIntoOneAnswer()
        {
            var cards = new List<(string, string)>
            {
                ("user", "build it"),
                ("agent", "Started the build; it takes about 4 minutes."),
                ("agent", "Build passed. The app is in F:\\App\\bin.")
            };

            List<(string Role, string Content)> turns = WorkplaceHistoryRestore.FromCards(cards);

            Assert.Equal(2, turns.Count);
            Assert.Contains("Started the build", turns[1].Content);
            Assert.Contains("Build passed", turns[1].Content);
        }
    }
}
