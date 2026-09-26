using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests
{
    // Shares the collection with the updater tests that terminate powershell.exe processes,
    // so they never run at the same time as a test that runs a real command.
    [Collection("ProcessLifecycleCollection")]
    public class AgentWorkVerifierTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomVerifierTests", Guid.NewGuid().ToString("N"));

        public AgentWorkVerifierTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private AgentStep Wrote(string relativePath, string content)
        {
            string full = Path.Combine(_folder, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
            return new AgentStep(
                new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = relativePath, ["content"] = content }),
                AgentPermission.Allow,
                AgentToolResult.Ok("ok"),
                "auto");
        }

        [Fact]
        public void ASummaryClaimingAFileThatWasNeverWrittenIsCaught()
        {
            // The live "make me a website" run: script.js was listed but never created.
            var steps = new[]
            {
                Wrote("index.html", "<html><head><link rel=\"stylesheet\" href=\"styles.css\"></head><body><script src=\"script.js\"></script></body></html>"),
                Wrote("styles.css", "body{}")
            };

            IReadOnlyList<string> problems = AgentWorkVerifier.FindProblems(
                "Created index.html, styles.css and script.js.", steps, _folder);

            Assert.Contains(problems, p => p.Contains("script.js", StringComparison.Ordinal));
            Assert.DoesNotContain(problems, p => p.Contains("styles.css", StringComparison.Ordinal));
        }

        [Fact]
        public void CompleteWorkPassesAndExternalLinksAreIgnored()
        {
            var steps = new[]
            {
                Wrote("site/index.html", "<a href=\"https://example.com/x.html\">x</a><img src=\"img/logo.svg\"><a href=\"#top\">top</a>"),
                Wrote("site/img/logo.svg", "<svg/>")
            };

            Assert.Empty(AgentWorkVerifier.FindProblems("Built site/index.html with its logo.svg.", steps, _folder));
        }

        [Fact]
        public void NothingWrittenMeansNothingToVerify()
        {
            Assert.Empty(AgentWorkVerifier.FindProblems("Your config.json looks fine.", Array.Empty<AgentStep>(), _folder));
        }

        [Fact]
        public async Task TheSessionSendsAFalseSummaryBackForFixingBeforeFinishing()
        {
            var session = new AgentSession(AgentScope.Folder(_folder), 8);
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.WriteFile,
                    new Dictionary<string, string> { ["path"] = "index.html", ["content"] = "<script src=\"app.js\"></script>" })),
                AgentModelReply.Answer("Done: index.html and app.js."),
                AgentModelReply.Tool(new AgentToolCall(AgentToolNames.WriteFile,
                    new Dictionary<string, string> { ["path"] = "app.js", ["content"] = "console.log(1);" })),
                AgentModelReply.Answer("Done: index.html and app.js."));

            AgentRunResult result = await session.RunAsync(
                "make a page", AgentApprovalMode.Auto, model,
                (c, r, t) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None);

            Assert.True(File.Exists(Path.Combine(_folder, "app.js")));
            Assert.Equal("Done: index.html and app.js.", result.FinalMessage);
            Assert.Equal(2, result.Steps.Count);
        }

        [Fact]
        public async Task CommandsContainingDoubleQuotesReachPowerShellIntact()
        {
            // The live Hybrid run: Start-Process "<path>" arrived as Start-Process `<path> and
            // failed. Any quoted argument was mangled by the old -Command "..." wrapping.
            string target = Path.Combine(_folder, "quoted name.txt");
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var call = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string>
            {
                ["command"] = $"Set-Content -Path \"{target}\" -Value 'a \"quoted\" value'; Get-Content \"{target}\""
            });

            AgentToolResult result = await executor.ExecuteAsync(call, CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.True(File.Exists(target));
            Assert.Contains("quoted", File.ReadAllText(target), StringComparison.Ordinal);
        }

        [Fact]
        public async Task AnAlternatingRetryCycleIsBrokenInsteadOfEatingTheStepBudget()
        {
            var session = new AgentSession(AgentScope.Folder(_folder), 30);
            AgentToolCall write = new(AgentToolNames.WriteFile, new Dictionary<string, string> { ["path"] = "index.html", ["content"] = "<p>x</p>" });
            AgentToolCall fail = new(AgentToolNames.RunCommand, new Dictionary<string, string> { ["command"] = "exit 1" });
            var replies = new List<AgentModelReply>();
            for (int i = 0; i < 12; i++)
            {
                replies.Add(AgentModelReply.Tool(write));
                replies.Add(AgentModelReply.Tool(fail));
            }

            AgentRunResult result = await session.RunAsync(
                "open the page", AgentApprovalMode.Auto, new ScriptedAgentModel(replies.ToArray()),
                (c, r, t) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, CancellationToken.None);

            Assert.False(result.StoppedOnStepLimit);
            Assert.Contains("repeating the same action", result.FinalMessage, StringComparison.Ordinal);
            Assert.True(result.Steps.Count < 12, $"took {result.Steps.Count} steps");
        }
    }
}
