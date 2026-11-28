using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>
    /// The ways long agent tasks failed in the 2026-11-27 logs: lost output, refused paging, a
    /// tracker that called failures done, follow-ups that forgot, early compaction, and guards
    /// that ended real work.
    /// </summary>
    public sealed class AgentLongTaskTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "axiom-longtask-" + Guid.NewGuid().ToString("N"));

        public AgentLongTaskTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            try { Directory.Delete(_folder, true); }
            catch (IOException) { }
        }

        private static AgentToolCall Call(string tool, params (string Key, string Value)[] arguments) =>
            new(tool, arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

        private static Task<AgentRunResult> Run(AgentSession session, IAgentModel model, AgentTurnPolicy? policy = null) =>
            session.RunAsync("goal", AgentApprovalMode.Auto, model,
                (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None, policy: policy);

        [Fact]
        public void ALongResultKeepsItsEnd()
        {
            string log = "first line\n" + string.Join("\n", Enumerable.Repeat("compiling", 2000)) + "\nerror CS1002: ; expected";
            string seen = AgentToolResult.Fail(log).ToObservation();
            Assert.StartsWith("ERROR: first line", seen);
            Assert.EndsWith("error CS1002: ; expected", seen);
            Assert.True(seen.Length <= 6000);
        }

        [Fact]
        public void LargeWindowsSeeMoreOfEachResult()
        {
            Assert.Equal(6000, AgentOutputClip.ObservationBudget(6000, 8192));
            Assert.Equal(21845, AgentOutputClip.ObservationBudget(6000, 131072));
            Assert.Equal(AgentOutputClip.MaxObservationChars, AgentOutputClip.ObservationBudget(6000, 1_048_576));
        }

        [Fact]
        public async Task ALongFileCanBeReadToTheEndInParts()
        {
            File.WriteAllLines(Path.Combine(_folder, "handoff.md"), Enumerable.Range(1, 300).Select(i => "line " + i));
            var replies = Enumerable.Range(0, 10).Select(i => AgentModelReply.Tool(Call(AgentToolNames.ReadFile,
                ("path", "handoff.md"), ("start_line", (i * 30 + 1).ToString()), ("line_count", "30")))).ToList();
            replies.Add(AgentModelReply.Answer("Read it all."));

            AgentRunResult result = await Run(new AgentSession(AgentScope.Folder(_folder), 40), new ScriptedAgentModel(replies.ToArray()));

            Assert.Equal(10, result.Steps.Count(step => step.Result?.Succeeded == true));
        }

        [Fact]
        public async Task CommandsThatReturnSomethingNewDoNotEndARunAfterAnEdit()
        {
            var replies = new List<AgentModelReply> { AgentModelReply.Tool(Call(AgentToolNames.WriteFile, ("path", "a.txt"), ("content", "x"))) };
            replies.AddRange(Enumerable.Range(0, 20).Select(i => AgentModelReply.Tool(
                Call(AgentToolNames.RunCommand, ("command", $"'check {i}'")))));
            replies.Add(AgentModelReply.Answer("All checks ran."));

            AgentRunResult result = await Run(new AgentSession(AgentScope.Folder(_folder), 60),
                new ScriptedAgentModel(replies.ToArray()), new AgentTurnPolicy(VerificationBudget: 4));

            Assert.Equal("All checks ran.", result.FinalMessage);
            Assert.Equal(21, result.Steps.Count);
        }

        [Fact]
        public void TheTrackerSaysWhichStepsDidNotHappen()
        {
            AgentToolCall ssh = Call(AgentToolNames.RunCommand, ("command", "ssh root@host uptime"));
            string failed = WorkplaceTaskTrackerFileService.StepLine(new AgentStep(ssh, AgentPermission.Allow,
                AgentToolResult.Fail("[Exit code: 255 | Duration: 5ms]\nPermission denied (publickey)."), ""));
            string blocked = WorkplaceTaskTrackerFileService.StepLine(new AgentStep(ssh, AgentPermission.Deny, null, "Auto-Review declined"));
            string path = Path.Combine(_folder, "AXIOM_TASK_TRACKER.md");

            WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(path, "t", "g", [failed, blocked]);
            string text = File.ReadAllText(path);

            Assert.StartsWith("[FAILED]", failed);
            Assert.Contains("Permission denied", failed);
            Assert.StartsWith("[BLOCKED]", blocked);
            Assert.Contains("- [FAILED]", text);
            Assert.DoesNotContain("- [x]", text);
        }

        [Fact]
        public void TheTrackerStaysOutOfGit()
        {
            Directory.CreateDirectory(Path.Combine(_folder, ".git", "info"));
            string path = Path.Combine(_folder, "AXIOM_TASK_TRACKER.md");

            WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(path, "t", "g");
            WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(path, "t", "g");

            Assert.Equal(["AXIOM_TASK_TRACKER.md*"],
                File.ReadAllLines(Path.Combine(_folder, ".git", "info", "exclude")));
        }

        [Fact]
        public void AFollowUpIsToldTheCommandThatWorkedAndTheOneThatFailed()
        {
            const string worked = "wsl.exe -d Debian -u root --exec bash -lc \"ssh -i /root/.ssh/deploy_key ubuntu@10.0.0.9 uptime\"";
            var run = new List<AgentExchange> { new(Call(AgentToolNames.RunCommand, ("command", worked)), "[Exit code: 0 | Duration: 4ms]\nup 38 days") };
            run.AddRange(Enumerable.Range(0, 30).Select(i => new AgentExchange(
                Call(AgentToolNames.RunCommand, ("command", "step " + i)), "[Exit code: 0 | Duration: 4ms]\nok")));
            run.Add(new AgentExchange(Call(AgentToolNames.RunCommand, ("command", "ssh root@10.0.0.9")), "ERROR: Permission denied"));
            run.Add(new AgentExchange(new AgentToolCall("(wrap_up)", new Dictionary<string, string>()), "Do NOT call a tool"));

            List<AgentExchange> history = AgentContextManager.AppendTurn([], run, null, "migrate the server", "Copied the archives.");
            string record = AgentContextManager.BuildCarryOver(history, 20_000)!.Observation;

            Assert.Contains(worked, record);
            Assert.Contains("[FAILED] run_command (command: ssh root@10.0.0.9)", record);
            Assert.Contains("migrate the server", record);
            Assert.Contains("Copied the archives.", record);
            Assert.DoesNotContain("Do NOT call a tool", record);
            Assert.True(record.Length < 22_000);
        }

        [Fact]
        public void TheTaskHistoryGrowsEachTurnUntilACheckpointReplacesIt()
        {
            var first = AgentContextManager.AppendTurn([],
                [new AgentExchange(Call(AgentToolNames.RunCommand, ("command", "one")), "ok")], null, "first", "done one");
            AgentExchange carried = AgentContextManager.BuildCarryOver(first, 5000)!;

            var second = AgentContextManager.AppendTurn(first,
                [carried, new AgentExchange(Call(AgentToolNames.RunCommand, ("command", "two")), "ok")], carried, "second", "done two");
            Assert.Contains(second, exchange => exchange.Call.Arg("command") == "one");
            Assert.DoesNotContain(carried, second);
            Assert.Equal("USER ASKED: second", AgentContextManager.LastTurn(second)[0].Observation);

            var checkpoint = new AgentExchange(Call(AgentRunCompactor.SummaryTool), "checkpoint covering turn one");
            var third = AgentContextManager.AppendTurn(second, [checkpoint], carried, "third", "done three");
            Assert.DoesNotContain(third, exchange => exchange.Call.Arg("command") == "one");
            Assert.Contains(checkpoint, third);
        }

        [Fact]
        public void ALargeWindowCompactsBySizeAndKeepsALongRecentStretch()
        {
            AgentCompactionPolicy policy = AgentCompactionPolicy.For(1_048_576, true, LocalModelSizeClass.Unknown, 16384, 4000);
            var history = Enumerable.Range(0, 80).Select(i => new AgentExchange(
                Call(AgentToolNames.RunCommand, ("command", "step " + i)), new string('x', 2000))).ToList();

            Assert.Equal(int.MaxValue, policy.MaxExchanges);
            Assert.Equal(AgentCompactionPolicy.CloudMaxTriggerTokens, policy.TriggerTokens);
            Assert.False(new AgentRunCompactor(policy).ShouldCompact("goal", history));
            Assert.True(AgentRunCompactor.RecentCount(history, policy) > 20);
            Assert.Equal(3, AgentRunCompactor.RecentCount(history,
                AgentCompactionPolicy.For(4096, false, LocalModelSizeClass.OneToFourB, 2048, 500)));
        }

        [Theory]
        [InlineData("& cmd /c \"echo progress 1>&2\" 2>&1", true)]
        [InlineData("& cmd /c \"echo progress 1>&2\" 2>&1 | Select-Object -Last 5", true)]
        [InlineData("& cmd /c exit 3", false)]
        [InlineData("& cmd /c \"echo bad 1>&2 & exit 2\" 2>&1", false)]
        [InlineData("Get-Item C:\\axiom-does-not-exist", false)]
        [InlineData("'done' # trailing comment", true)]
        public async Task ACommandSucceedsWhenItsProgramDid(string command, bool expected)
        {
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            AgentToolResult result = await executor.ExecuteAsync(Call(AgentToolNames.RunCommand, ("command", command)), CancellationToken.None);
            Assert.True(expected == result.Succeeded, result.Error ?? result.Output);
        }

        [Fact]
        public void TheAgentMayStopOnlyProcessesItStarted()
        {
            AgentScope scope = AgentScope.WholeComputer();
            AgentPermission Review(string command) => CommandSafetyReviewer.Evaluate(
                Call(AgentToolNames.RunCommand, ("command", command)), scope, CommandReviewMode.AutoReview,
                isOwnedProcess: pid => pid is 100 or 200).Permission;

            Assert.Equal(AgentPermission.Allow, Review("Stop-Process -Id 100,200 -Force"));
            Assert.Equal(AgentPermission.Allow, Review("taskkill /PID 100 /T /F; taskkill /PID 200 /F"));
            Assert.Equal(AgentPermission.Allow, Review("Stop-Process -Id 100 -Force; Start-Sleep -Seconds 1; Get-Process -Id 100 -ErrorAction SilentlyContinue"));
            Assert.Equal(AgentPermission.Deny, Review("Stop-Process -Id 100; Remove-Item C:/data -Recurse"));
            Assert.Equal(AgentPermission.Deny, Review("Stop-Process -Id 100,999 -Force"));
            Assert.Equal(AgentPermission.Deny, Review("Stop-Process -Name notepad"));
            Assert.Equal(AgentPermission.Deny, Review("Get-Process chrome | Stop-Process; Stop-Process -Id 100"));
            Assert.Equal(AgentPermission.Deny, Review("Get-CimInstance Win32_Process | Invoke-CimMethod -MethodName Terminate"));
            Assert.Equal(AgentPermission.Deny, Review("(Get-Process -Id 999).Kill()"));
            Assert.Equal(AgentPermission.Deny, Review("wmic process where processid=999 delete"));
        }

        [Fact]
        public void AStartedShellIsOwnedAndAxiomItselfIsNot()
        {
            using Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 20 127.0.0.1")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            })!;
            try
            {
                Assert.False(AgentProcessOwnership.IsOwned(process.Id));
                AgentProcessOwnership.Claim(process);
                Assert.True(AgentProcessOwnership.IsOwned(process.Id));
                Assert.False(AgentProcessOwnership.IsOwned(Environment.ProcessId));
            }
            finally
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }
}
