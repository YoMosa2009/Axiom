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
    /// <summary>
    /// The live report: a Hybrid Local agent run was stopped so the user could add "make it in a
    /// completely new folder", and every run after that sat on "Starting..." (the server was still
    /// generating the abandoned reply). Meanwhile the first run had written its files over an
    /// unrelated project on F:. These cover the fixes: messages reach a running agent without a
    /// Stop, overwritten files are recoverable, and a bash-style command is pointed at PowerShell.
    /// </summary>
    public class AgentSteeringAndBackupTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomAgentSteering", Guid.NewGuid().ToString("N"));

        public AgentSteeringAndBackupTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private static Task<AgentApprovalOutcome> AlwaysApprove(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Approve);

        private static AgentToolCall Write(string path, string content) =>
            new(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = path, ["content"] = content });

        [Fact]
        public async Task AMessageSentMidRunReachesTheModelAtItsNextStep()
        {
            var inbox = new AgentUserMessageInbox();
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write("scene.html", "<canvas></canvas>")),
                AgentModelReply.Answer("Moved it."));

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 6).RunAsync(
                "make an animation", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None,
                onStep: _ => inbox.Post("you need to make it in a completely new folder"),
                inbox: inbox);

            Assert.Equal("Moved it.", result.FinalMessage);
            IReadOnlyList<AgentExchange> secondTurn = model.SeenHistories[1];
            AgentExchange delivered = secondTurn.Last();
            Assert.Equal(AgentSession.UserMessageTool, delivered.Call.Tool);
            Assert.Contains("completely new folder", delivered.Observation);
            Assert.False(inbox.HasPending);
        }

        [Fact]
        public async Task AMessageThatArrivesWhileTheModelFinishesIsNotDropped()
        {
            // The model was already writing its final answer when the user typed. Ending the run
            // there would silently lose the correction.
            var inbox = new AgentUserMessageInbox();
            var model = new MessageDuringAnswerModel(inbox);

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 6).RunAsync(
                "make an animation", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None,
                inbox: inbox);

            Assert.Equal("Done in the new folder.", result.FinalMessage);
            Assert.Equal(2, model.Turns);
            AgentExchange delivered = model.LastHistory.Last();
            Assert.Equal(AgentSession.UserMessageTool, delivered.Call.Tool);
            Assert.Contains("put it on F:", delivered.Observation);
            Assert.Contains("about to finish with", delivered.Observation);
        }

        private sealed class MessageDuringAnswerModel(AgentUserMessageInbox inbox) : IAgentModel
        {
            public int Turns { get; private set; }
            public IReadOnlyList<AgentExchange> LastHistory { get; private set; } = [];
            public string? Unavailable => null;

            public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
            {
                Turns++;
                LastHistory = history.ToList();
                if (Turns == 1)
                {
                    inbox.Post("put it on F: instead");
                    return Task.FromResult(AgentModelReply.Answer("Done in the working folder."));
                }

                return Task.FromResult(AgentModelReply.Answer("Done in the new folder."));
            }
        }

        [Fact]
        public async Task TheTextProtocolShowsAMidRunMessageAsTheUsersWords()
        {
            string? transcript = null;
            var model = new TextProtocolAgentModel(
                (system, text, token) => { transcript = text; return Task.FromResult("ok"); },
                "system",
                2000);
            var history = new List<AgentExchange>
            {
                new(new AgentToolCall(AgentSession.UserMessageTool, new Dictionary<string, string>()),
                    AgentUserMessageInbox.FormatForModel(["use a new folder"]))
            };

            await model.NextAsync("make an animation", history, CancellationToken.None);

            Assert.Contains("The user sent a new message while you were working", transcript);
            Assert.DoesNotContain("You ran: (user)", transcript);
        }

        [Fact]
        public void AFileThatAlreadyExistedIsCopiedBeforeTheAgentOverwritesIt()
        {
            string project = Path.Combine(_folder, "IllustratedAnimation");
            Directory.CreateDirectory(project);
            string index = Path.Combine(project, "index.html");
            File.WriteAllText(index, "<h1>the user's own page</h1>");
            var backup = new AgentFileBackup(Path.Combine(_folder, "backups"));
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder), backup);

            AgentToolResult result = executor.ExecuteAsync(Write(index, "<canvas>fisherman</canvas>"), CancellationToken.None).Result;

            Assert.True(result.Succeeded);
            AgentBackedUpFile saved = Assert.Single(backup.Saved);
            Assert.Equal(index, saved.OriginalPath);
            Assert.Equal("<h1>the user's own page</h1>", File.ReadAllText(saved.CopyPath));
            Assert.Contains("previous version was saved", result.Output);
        }

        [Fact]
        public void OnlyTheFirstChangeIsBackedUpAndNewFilesAreNot()
        {
            string existing = Path.Combine(_folder, "style.css");
            File.WriteAllText(existing, "body { color: red; }");
            var backup = new AgentFileBackup(Path.Combine(_folder, "backups"));
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder), backup);

            executor.ExecuteAsync(Write(existing, "body { color: blue; }"), CancellationToken.None).Wait();
            executor.ExecuteAsync(Write(existing, "body { color: green; }"), CancellationToken.None).Wait();
            executor.ExecuteAsync(Write(Path.Combine(_folder, "Fisherman", "index.html"), "new"), CancellationToken.None).Wait();

            // The copy is the user's version, not one of the agent's intermediate ones.
            AgentBackedUpFile saved = Assert.Single(backup.Saved);
            Assert.Equal("body { color: red; }", File.ReadAllText(saved.CopyPath));
        }

        [Fact]
        public void BackupLocationsMirrorTheOriginalPath()
        {
            Assert.Equal(Path.Combine("F", "IllustratedAnimation", "index.html"),
                AgentFileBackup.RelativeLocationFor(@"F:\IllustratedAnimation\index.html"));
        }

        [Theory]
        [InlineData(@"ls -la /mnt/F 2>/dev/null  ls -la F:\", true)]
        [InlineData("mkdir foo && cd foo", true)]
        [InlineData("rm -rf build", true)]
        [InlineData(@"Get-ChildItem -Force 'F:\'", false)]
        [InlineData(@"Start-Process 'F:\Fisherman\index.html'", false)]
        public void ABashStyleCommandGetsAPowerShellHint(string command, bool expectHint)
        {
            string hint = AgentToolExecutor.ShellMismatchHint(command);
            Assert.Equal(expectHint, hint.Contains("Windows PowerShell"));
        }

        [Fact]
        public void AStoppedRunIsDescribedAsAnInterruptionAndTheFollowUpWins()
        {
            string goal = AgentContextManager.BuildContinuationGoal(
                "make me a fisherman animation on the F: drive",
                "you need to make it in a completely new folder",
                [@"F:\IllustratedAnimation\index.html"],
                "Stopped.",
                backupFolder: @"C:\Axiom\AgentBackups\run");

            Assert.Contains("stopped the previous run part-way", goal);
            Assert.Contains("takes priority over the original task", goal);
            Assert.Contains(@"C:\Axiom\AgentBackups\run", goal);
            Assert.DoesNotContain("Work directly on the existing project files", goal);
        }

        [Fact]
        public void ThePromptAsksForANewFolderAndPowerShell()
        {
            string prompt = AgentPromptBuilder.Build(AgentTier.Full, AgentScope.WholeComputer(), AgentApprovalMode.Auto, nativeToolCalling: true);

            Assert.Contains("New work goes in a new folder", prompt);
            Assert.Contains("Windows PowerShell 5.1, not bash", prompt);
            Assert.Contains("send a new message while you work", prompt);
        }
    }
}
