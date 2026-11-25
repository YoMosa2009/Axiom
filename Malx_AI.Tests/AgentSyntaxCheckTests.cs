using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    public class AgentSyntaxCheckTests
    {
        [Fact]
        public void BrokenJsonIsReportedWithItsLine()
        {
            string note = AgentSyntaxCheck.Note(@"C:\p\settings.json", "{\n  \"a\": 1\n  \"b\": 2\n}");
            Assert.Contains("settings.json is not valid JSON (line 3)", note);
        }

        [Fact]
        public void JsonWithCommentsAndTrailingCommasPasses()
        {
            Assert.Equal("", AgentSyntaxCheck.Note("tsconfig.json", "{\n // comment\n \"a\": [1, 2,],\n}"));
        }

        [Fact]
        public void BrokenXmlProjectFilesAreReported()
        {
            string note = AgentSyntaxCheck.Note("App.csproj", "<Project><PropertyGroup></Project>");
            Assert.Contains("App.csproj is not well-formed XML", note);
        }

        [Fact]
        public void UncheckedFormatsAddNothing()
        {
            Assert.Equal("", AgentSyntaxCheck.Note("main.py", "def broken(:"));
        }

        [Fact]
        public async Task WriteFileResultCarriesTheSyntaxNote()
        {
            string folder = Path.Combine(Path.GetTempPath(), "AxiomSyntaxCheck", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var model = new ScriptedAgentModel(
                    AgentModelReply.Tool(new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = "data.json", ["content"] = "{ \"a\": }" })),
                    AgentModelReply.Answer("ok"));
                AgentRunResult result = await new AgentSession(AgentScope.Folder(folder), 6).RunAsync(
                    "write data.json", AgentApprovalMode.Auto, model, (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None);
                Assert.Contains("[Syntax check: data.json is not valid JSON", result.Steps[0].Result!.Output);
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
