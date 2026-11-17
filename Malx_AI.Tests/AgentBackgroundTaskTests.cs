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
    [Collection("ProcessLifecycleCollection")]
    public sealed class AgentBackgroundTaskTests
    {
        private sealed class ScriptedModel(Queue<AgentModelReply> replies) : IAgentModel
        {
            public string? Unavailable => null;
            public List<IReadOnlyList<AgentExchange>> SeenHistories { get; } = new();

            public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
            {
                SeenHistories.Add(history.ToList());
                return Task.FromResult(replies.Count > 0 ? replies.Dequeue() : AgentModelReply.Answer("Done."));
            }
        }

        [Fact]
        public async Task StartBackgroundTask_RunsConcurrentlyAndCapturesOutput()
        {
            using var mgr = new AgentBackgroundTaskManager();
            string tempDir = Path.Combine(Path.GetTempPath(), "AxiomBgTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                AgentBackgroundTaskSnapshot snap = mgr.StartBackgroundTask(
                    "Write-Output 'hello-from-bg'",
                    tempDir,
                    wakeAfterSeconds: 5,
                    title: "Echo greeting");

                Assert.False(string.IsNullOrWhiteSpace(snap.Id));
                Assert.Equal("Echo greeting", snap.Title);

                (AgentBackgroundTaskSnapshot? finalSnap, bool wokeByCompletion, string report) =
                    await mgr.WaitForTaskOrWakeTimerAsync(snap.Id, 10, CancellationToken.None);

                Assert.NotNull(finalSnap);
                Assert.True(wokeByCompletion);
                Assert.False(finalSnap!.IsRunning);
                Assert.Equal(0, finalSnap.ExitCode);
                Assert.Contains("hello-from-bg", finalSnap.OutputTail);
                Assert.Contains("BACKGROUND TASK COMPLETE", report);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }

        [Fact]
        public async Task WaitForTaskOrWakeTimer_WakesAtRequestedDurationBeforeLongTaskFinishes()
        {
            using var mgr = new AgentBackgroundTaskManager();
            AgentBackgroundTaskSnapshot snap = mgr.StartBackgroundTask(
                "Start-Sleep -Seconds 8; Write-Output 'done-later'",
                Path.GetTempPath(),
                wakeAfterSeconds: 1,
                title: "Sleeper");

            (AgentBackgroundTaskSnapshot? wokeSnap, bool wokeByCompletion, string report) =
                await mgr.WaitForTaskOrWakeTimerAsync(snap.Id, wakeAfterSeconds: 1, CancellationToken.None);

            Assert.NotNull(wokeSnap);
            Assert.False(wokeByCompletion);
            Assert.True(wokeSnap!.IsRunning);
            Assert.Contains("BACKGROUND TASK WAKE (1s)", report);

            (bool stopped, _) = mgr.StopTask(snap.Id);
            Assert.True(stopped);
        }

        [Fact]
        public async Task StopTask_KillsRunningProcessAndMarksStoppedByUser()
        {
            using var mgr = new AgentBackgroundTaskManager();
            AgentBackgroundTaskSnapshot snap = mgr.StartBackgroundTask(
                "Start-Sleep -Seconds 20",
                Path.GetTempPath(),
                wakeAfterSeconds: 10,
                title: "Long wait");

            (bool stopped, string msg) = mgr.StopTask(snap.Id);
            Assert.True(stopped);
            Assert.Contains("Stopped background task", msg);

            AgentBackgroundTaskSnapshot? after = mgr.GetTask(snap.Id);
            Assert.NotNull(after);
            Assert.False(after!.IsRunning);
            Assert.True(after.WasStoppedByUser);
        }

        [Fact]
        public async Task AgentSession_InjectsBackgroundTaskWakeAndContinuesOtherWork()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "AxiomBgAgent_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                using var bg = new AgentBackgroundTaskManager();
                var scope = AgentScope.Folder(tempDir);
                var executor = new AgentToolExecutor(scope, backgroundTasks: bg);
                var session = new AgentSession(scope, maxSteps: 8, executor: executor);

                var replies = new Queue<AgentModelReply>(
                [
                    AgentModelReply.Tool(new AgentToolCall(AgentToolNames.StartBackgroundTask, new Dictionary<string, string>
                    {
                        ["command"] = "Start-Sleep -Milliseconds 300; Write-Output 'bg-finished-ok'",
                        ["wake_seconds"] = "1",
                        ["wait"] = "false",
                        ["title"] = "Quick build"
                    })),
                    AgentModelReply.Tool(new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string>
                    {
                        ["path"] = "notes.txt",
                        ["content"] = "worked while waiting"
                    })),
                    AgentModelReply.Tool(new AgentToolCall(AgentToolNames.CheckBackgroundTask, new Dictionary<string, string>
                    {
                        ["task_id"] = "bg-1",
                        ["wake_seconds"] = "5"
                    })),
                    AgentModelReply.Answer("Background task and note file both finished.")
                ]);

                var model = new ScriptedModel(replies);
                AgentRunResult result = await session.RunAsync(
                    "Start bg task, write note, check completion",
                    AgentApprovalMode.Auto,
                    model,
                    (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve),
                    _ => { },
                    CancellationToken.None,
                    commandReviewMode: CommandReviewMode.AutoApprove);

                Assert.Equal(3, result.Steps.Count);
                Assert.Contains("finished", result.FinalMessage, StringComparison.OrdinalIgnoreCase);
                Assert.True(File.Exists(Path.Combine(tempDir, "notes.txt")));
                Assert.Contains("bg-finished-ok", result.Steps[2].Result?.Output ?? string.Empty);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
