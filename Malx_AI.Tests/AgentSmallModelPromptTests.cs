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
    /// Pins what simulated small local models (the Micro and Compact text-protocol tiers) tripped
    /// over: rules naming tools their prompt did not list, a Markdown summary demanded inside a
    /// plain-text protocol, a transcript that cut their own commands to 40 characters, and a run
    /// record that joined commands with " | " so they read as one pipeline.
    /// </summary>
    public class AgentSmallModelPromptTests
    {
        private static readonly string Root = Path.Combine(Path.GetTempPath(), "AxiomSmallModelPrompt");

        private static string FlatPrompt(AgentTier tier) =>
            AgentPromptBuilder.Build(tier, AgentScope.Folder(Root), AgentApprovalMode.Auto, nativeToolCalling: false);

        private static AgentToolCall Call(string tool, params (string Key, string Value)[] arguments) =>
            new(tool, arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

        [Theory]
        [InlineData(AgentTier.Micro)]
        [InlineData(AgentTier.Compact)]
        public void AFlatTierIsOnlyToldAboutToolsItHas(AgentTier tier)
        {
            string prompt = FlatPrompt(tier);

            Assert.Contains("TOOL write_file", prompt);
            Assert.Contains("TOOL finish\nsummary:", prompt.Replace("\r\n", "\n"));
            Assert.DoesNotContain("append_file", prompt);
            Assert.DoesNotContain("edit_file", prompt);
            Assert.DoesNotContain("Markdown summary", prompt);
            Assert.Contains("When the work is done, reply with TOOL finish", prompt);
        }

        [Fact]
        public void TheWriteExampleAFlatTierIsShownParsesAsAWrite()
        {
            const string reply = "TOOL write_file\npath: C:\\folder\\hello.py\ncontent: <<<\nfor i in range(1, 11):\n    print(i)\n>>>\nEND";

            Assert.True(AgentToolCallParser.TryParse(reply, out AgentToolCall? call, out _));
            Assert.Equal(AgentToolNames.WriteFile, call!.Tool);
            Assert.Contains("    print(i)", call.Arg("content"));
        }

        [Fact]
        public void TheFullTierKeepsItsLongFileAndSummaryRules()
        {
            string prompt = AgentPromptBuilder.Build(AgentTier.Full, AgentScope.Folder(Root), AgentApprovalMode.Auto, nativeToolCalling: true);

            Assert.Contains("append_file", prompt);
            Assert.Contains("read_file, edit_file, or write_file", prompt);
            Assert.Contains("Markdown summary", prompt);
        }

        [Fact]
        public void APromptSaysWhereCommandsStartAndWhereASingleNamedFileGoes()
        {
            string prompt = FlatPrompt(AgentTier.Compact);

            Assert.Contains($"Commands start in the working folder '{Root}'", prompt);
            Assert.Contains("A single file the user names", prompt);
            Assert.Contains("New work goes in a new folder", prompt);
        }

        [Fact]
        public async Task TheTextTranscriptShowsTheWholeCommandAndBothWaysToReply()
        {
            string? seen = null;
            var model = new TextProtocolAgentModel(
                (prompt, transcript, token) => { seen = transcript; return Task.FromResult("TOOL finish\nsummary: done\nEND"); },
                "system", observationBudget: 1200);
            const string command = "Set-Content -Path hello.py -Value 'for i in range(1, 11):','    print(i)'";
            var history = new List<AgentExchange>
            {
                new(Call(AgentToolNames.RunCommand, ("command", command)), "[Exit code: 0]"),
                new(Call(AgentToolNames.WriteFile, ("path", @"C:\work\hello.py"), ("content", "a\nb")), "Wrote 2 lines")
            };

            await model.NextAsync("Create hello.py", history, CancellationToken.None);

            Assert.Contains(command, seen);
            Assert.Contains(@"write_file (path: C:\work\hello.py; content: (2 lines, 3 chars))", seen);
            Assert.DoesNotContain("You ran: Ran ", seen);
            Assert.Contains("if the task is a question, the answer in plain words", seen);
        }

        [Fact]
        public void TheRunRecordListsCommandsOnePerLineAndKeepsTheirSpacing()
        {
            var exchanges = new List<AgentExchange>
            {
                new(Call(AgentToolNames.RunCommand, ("command", "dir")), "listing"),
                new(Call(AgentToolNames.RunCommand, ("command", "Set-Content -Path hello.py -Value 'for i in range(1, 11):','    print(i)'")), "[Exit code: 0]"),
                new(Call(AgentToolNames.RunCommand, ("command", "python hello.py")), "1..10")
            };

            string digest = AgentFollowUp.BuildRunDigest(exchanges, "Done.", stoppedOnStepLimit: false).Replace("\r\n", "\n");

            Assert.Contains("Commands run (3), in order:\n    1. dir\n    2. Set-Content", digest);
            Assert.Contains("'    print(i)'", digest);
            Assert.Contains("    3. python hello.py", digest);
            Assert.DoesNotContain(" | ", digest);
        }
    }
}
