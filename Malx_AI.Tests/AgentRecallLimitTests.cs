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
    public class AgentRecallLimitTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomAgentRecallTests", Guid.NewGuid().ToString("N"));

        public AgentRecallLimitTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private static AgentModelReply Recall(string query) =>
            AgentModelReply.Tool(new AgentToolCall(AgentToolNames.RecallContext, new Dictionary<string, string> { ["query"] = query }));

        private static Task<AgentApprovalOutcome> AlwaysApprove(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Approve);

        [Fact]
        public async Task RecallContextIsBlockedAfterThreeInARowAndAllowedAgainAfterOtherWork()
        {
            var list = new AgentToolCall(AgentToolNames.ListDirectory, new Dictionary<string, string> { ["path"] = "." });
            var model = new ScriptedAgentModel(
                Recall("visualizer"), Recall("index.html"), Recall("server.py"), Recall("fetch.py"),
                AgentModelReply.Tool(list),
                Recall("charts"),
                AgentModelReply.Answer("Done."));

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 20).RunAsync(
                "continue the visualizer", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            List<AgentStep> recalls = result.Steps.Where(s => s.Call.Tool == AgentToolNames.RecallContext).ToList();
            Assert.Equal(5, recalls.Count);
            Assert.All(recalls.Take(AgentSession.MaxRecallsInARow), s => Assert.NotEqual("recall limit", s.Note));
            Assert.Equal("recall limit", recalls[3].Note);
            Assert.Contains("Act on what you have", recalls[3].Result!.Error);
            Assert.NotEqual("recall limit", recalls[4].Note);
            Assert.Equal("Done.", result.FinalMessage);
        }
    }
}
