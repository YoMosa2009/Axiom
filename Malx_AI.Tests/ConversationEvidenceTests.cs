using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class ConversationEvidenceTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "AxiomEvidenceTests", Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

        [Fact]
        public void LongEvidenceCanBeRecoveredBeyondThePromptLimit()
        {
            using var evidence = new ConversationEvidence(_root, "chat-1", "Draw a stickman in Paint");
            string full = new string('x', 24000) + "IMPORTANT_END_OF_TOOL_RESULT";
            evidence.Record("tool", full);
            Assert.Contains("IMPORTANT_END_OF_TOOL_RESULT", evidence.Read("IMPORTANT_END_OF_TOOL_RESULT"));
            Assert.Contains("next offset=", evidence.Read(limit: 500));
            Assert.Contains("end=False", evidence.Read(limit: 500));
        }

        [Fact]
        public void OnlyReplayedTransportMessagesWithACallIdAreSuppressed()
        {
            using var evidence = new ConversationEvidence(_root, "chat-1", "Inspect the result");
            var first = new OpenRouterMessage("tool", "original outcome", ToolCallId: "call-1");
            var missingId = new OpenRouterMessage("tool", "unidentified outcome");

            evidence.RecordTransportToolResult(first);
            evidence.RecordTransportToolResult(first);
            evidence.RecordTransportToolResult(first with { ToolCallId = "call-2" });
            evidence.RecordTransportToolResult(first with { Text = "updated outcome" });
            evidence.RecordTransportToolResult(new OpenRouterMessage("tool", "original outcome", ToolCallId: "call-1"));
            evidence.RecordTransportToolResult(first);
            evidence.RecordTransportToolResult(missingId);
            evidence.RecordTransportToolResult(missingId);
            evidence.Record("Full command stdout", "execution output");
            evidence.Record("Full command stdout", "execution output");

            string history = evidence.Read();
            Assert.Equal(3, history.Split("original outcome").Length - 1);
            Assert.Equal(1, history.Split("updated outcome").Length - 1);
            Assert.Equal(2, history.Split("unidentified outcome").Length - 1);
            Assert.Equal(2, history.Split("execution output").Length - 1);
        }

        [Theory]
        [InlineData("now give the stickman a smiley face")]
        [InlineData("add eyes and keep the rest")]
        [InlineData("continue where you stopped")]
        public void DesktopContinuationUsesTheActiveSurfaceInsteadOfAKeywordList(string request)
        {
            Assert.True(ComputerUse.ComputerUseMention.ShouldRoute(request, "Open Paint and draw a stickman"));
            Assert.False(ComputerUse.ComputerUseMention.ShouldRoute(request, ""));
            Assert.True(ComputerUse.ComputerUseMention.ShouldRoute("@ComputerUse " + request, ""));
        }

        [Fact]
        public void DifferentChatsAndNestedTurnsCannotReadEachOthersEvidence()
        {
            using var first = new ConversationEvidence(_root, "chat-1", "first-chat-secret");
            using (var second = new ConversationEvidence(_root, "chat-2", "second-chat-only"))
            {
                Assert.Same(second, ConversationEvidence.Current);
                Assert.DoesNotContain("first-chat-secret", second.Read());
            }
            Assert.Same(first, ConversationEvidence.Current);
            Assert.DoesNotContain("second-chat-only", first.Read());
        }

        [Fact]
        public async Task RecallToolReadsTheArchiveWithoutFilesystemScopeEscapes()
        {
            using var evidence = new ConversationEvidence(_root, "chat-1", "Earlier Paint task: stickman");
            var executor = new AgentToolExecutor(AgentScope.Folder(_root));
            var result = await executor.ExecuteAsync(new AgentToolCall(AgentToolNames.RecallContext,
                new Dictionary<string,string> { ["query"] = "stickman" }), CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.Contains("Earlier Paint task", result.Output);
            Assert.True(AgentToolNames.IsReadOnly(AgentToolNames.RecallContext));
        }

        [Fact]
        public void ArchivesSurviveClosingAndReopeningTheChat()
        {
            using (var first = new ConversationEvidence(_root, "chat-1", "original-goal"))
                first.Record("tool", "Paint: original figure verified");
            using var reopened = new ConversationEvidence(_root, "chat-1", "Give it a smiley face");
            Assert.Contains("original figure verified", reopened.Read());
            Assert.Contains("Give it a smiley face", reopened.Read());
            Assert.Throws<ArgumentException>(() => new ConversationEvidence(_root, "..", "invalid"));
        }

        [Fact]
        public async Task RefusedActionsAreRecordedWithTheirActualFailure()
        {
            using var evidence = new ConversationEvidence(_root, "chat-1", "inspect the work");
            var model = new ScriptedAgentModel(AgentModelReply.Tool(new AgentToolCall(AgentToolNames.RunCommand,
                new Dictionary<string,string> { ["command"] = "[Native]::ShowWindow($h,2)" })), AgentModelReply.Answer("Blocked."));
            await new AgentSession(AgentScope.Folder(_root), 4).RunAsync("inspect", AgentApprovalMode.Auto, model,
                (_,_,_) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None);
            Assert.Contains("ShowWindow", evidence.Read());
            Assert.Contains("ERROR:", evidence.Read());
            Assert.Contains("must not hide", evidence.Read());
        }
    }
}
