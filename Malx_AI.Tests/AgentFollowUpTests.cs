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
    /// Pins the fixes for the logged rocket-visualizer session: a build that spent 42 of its 60
    /// steps refocusing and screenshotting a browser, then a follow-up "Is it done?" that ran
    /// another 60 steps of re-verification and never answered.
    /// </summary>
    public class AgentFollowUpTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomFollowUpTests", Guid.NewGuid().ToString("N"));

        public AgentFollowUpTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private static Task<AgentApprovalOutcome> AlwaysApprove(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Approve);

        private static AgentToolCall Call(string tool, params (string Key, string Value)[] arguments) =>
            new(tool, arguments.ToDictionary(pair => pair.Key, pair => pair.Value));

        private static AgentToolCall Write(string path, string content = "x") =>
            Call(AgentToolNames.WriteFile, ("path", path), ("content", content));

        private static AgentToolCall List(string path) => Call(AgentToolNames.ListDirectory, ("path", path));

        private Task<AgentRunResult> Run(AgentSession session, IAgentModel model, AgentTurnPolicy? policy = null) =>
            session.RunAsync("goal", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None, policy: policy);

        // ───────────────────────────── what a follow-up is asking for

        [Theory]
        [InlineData("Is it done?", AgentFollowUpKind.Question)]
        [InlineData("is it done", AgentFollowUpKind.Question)]
        [InlineData("What did you do?", AgentFollowUpKind.Question)]
        [InlineData("did it work", AgentFollowUpKind.Question)]
        [InlineData("How do I run it?", AgentFollowUpKind.Question)]
        [InlineData("Where are the files?", AgentFollowUpKind.Question)]
        [InlineData("does it use three.js?", AgentFollowUpKind.Question)]
        [InlineData("status?", AgentFollowUpKind.Question)]
        [InlineData("Why is the screen black?", AgentFollowUpKind.Question)]
        [InlineData("Can you make the rocket blue?", AgentFollowUpKind.Change)]
        [InlineData("Why is it blank? Fix it.", AgentFollowUpKind.Change)]
        [InlineData("make the exhaust flame bigger", AgentFollowUpKind.Change)]
        [InlineData("i opened up the localhost, and the 3D model IS NOT SHOWING", AgentFollowUpKind.Change)]
        [InlineData("Add a night launch toggle button to the HUD and explain how it works.", AgentFollowUpKind.Change)]
        [InlineData("Is the server running? please start it if not", AgentFollowUpKind.Change)]
        [InlineData("continue", AgentFollowUpKind.Resume)]
        [InlineData("keep going", AgentFollowUpKind.Resume)]
        [InlineData("new task: build a calculator", AgentFollowUpKind.NewTask)]
        public void AFollowUpIsRecognisedForWhatItAsks(string message, AgentFollowUpKind expected)
        {
            Assert.Equal(expected, AgentFollowUp.Classify(message, hasActiveTask: true));
        }

        [Fact]
        public void WithNoActiveTaskEverythingIsANewTask()
        {
            Assert.Equal(AgentFollowUpKind.NewTask, AgentFollowUp.Classify("Is it done?", hasActiveTask: false));
        }

        // ───────────────────────────── what the model is told on a follow-up

        [Fact]
        public void AQuestionIsToldToBeAnsweredNotWorkedOn()
        {
            string digest = AgentFollowUp.BuildRunDigest(
                [new AgentExchange(Write(@"G:\RocketVisualizer3D\main.js"), "Wrote main.js")],
                "Built the visualizer and verified it loads.",
                stoppedOnStepLimit: false);

            string goal = AgentContextManager.BuildContinuationGoal(
                "Make me a 3D rocket visualizer that launches under a new folder",
                "Is it done?",
                [@"G:\RocketVisualizer3D\index.html", @"G:\RocketVisualizer3D\main.js"],
                null,
                kind: AgentFollowUpKind.Question,
                previousRunDigest: digest);

            Assert.Contains("QUESTION", goal);
            Assert.Contains("Is it done?", goal);
            Assert.Contains("Answer it directly", goal);
            Assert.Contains("Do NOT edit files", goal);
            Assert.Contains("at most 2 quick read-only checks", goal);
            Assert.Contains("[RECORD OF THE PREVIOUS RUN", goal);
            Assert.Contains("Built the visualizer and verified it loads.", goal);
            // The instruction that turned a question into a second verification run.
            Assert.DoesNotContain("inspect, modify, or verify", goal);
            Assert.DoesNotContain("change request", goal);
        }

        [Fact]
        public void AFollowUpIsInterpretedByTheModelWhenTheCallerDoesNotSay()
        {
            string goal = AgentContextManager.BuildContinuationGoal("build a site", "Is it done?", [], null);

            Assert.Contains("Interpret this message from its meaning", goal);
            Assert.Contains("Is it done?", goal);
            Assert.DoesNotContain("The user is asking a QUESTION", goal);
        }

        [Theory]
        [InlineData("Pick up wherever you left off.")]
        [InlineData("Before going further, tell me what actually works.")]
        [InlineData("This still shows nothing when I press play.")]
        [InlineData("Let's leave this and work on something else.")]
        public void FollowUpsReachTheModelWithoutAKeywordSelectedToolRestriction(string request)
        {
            string goal = AgentContextManager.BuildContinuationGoal("build a site", request,
                ["index.html"], "Visual check failed", previousRunDigest: "The canvas was blank.");
            Assert.Contains(request, goal);
            Assert.Contains("The canvas was blank.", goal);
            Assert.Contains("Interpret this message from its meaning", goal);
            Assert.DoesNotContain("This turn is for answering, not for working", goal);
            Assert.DoesNotContain("Resume execution from your current progress and finish the task.", goal);
            Assert.False(AgentTurnPolicy.For(AgentFollowUpKind.Contextual, AgentTier.Full).AnswerOnly);
        }

        [Fact]
        public void AChangeRequestGetsFullPathsAndIsKeptNarrow()
        {
            string goal = AgentContextManager.BuildContinuationGoal(
                "Make me a 3D rocket visualizer",
                "make the exhaust flame bigger",
                [@"G:\RocketVisualizer3D\main.js"],
                null,
                kind: AgentFollowUpKind.Change);

            // A bare "main.js" sent the model looking for the folder before it could do anything.
            Assert.Contains(@"G:\RocketVisualizer3D\main.js", goal);
            Assert.Contains("Work only on what this request names", goal);
            Assert.Contains("only what you do in THIS turn", goal);
            Assert.DoesNotContain("QUESTION", goal);
        }

        [Fact]
        public void TheRecordOfARunSaysWhatItActuallyDid()
        {
            var exchanges = new List<AgentExchange>
            {
                new(Write(@"G:\Rocket\index.html"), "Wrote index.html"),
                new(Write(@"G:\Rocket\main.js"), "Wrote main.js"),
                new(Call(AgentToolNames.EditFile, ("path", @"G:\Rocket\style.css"), ("old_string", "a"), ("new_string", "b")), "Edited"),
                new(Call(AgentToolNames.EditFile, ("path", @"G:\Rocket\main.js"), ("old_string", "a"), ("new_string", "b")), "Edited"),
                new(Call(AgentToolNames.StartBackgroundTask, ("command", "node server.js"), ("title", "Rocket static server")), "bg-1 started"),
                new(Call(AgentToolNames.RunCommand, ("command", "curl.exe -s -o NUL -w '%{http_code}' http://localhost:8080/")), "200"),
                new(Call(AgentToolNames.TakeScreenshot), "Captured screenshot"),
                new(Call(AgentToolNames.TakeScreenshot), "Blocked: Screenshot limit reached"),
                new(Call(AgentToolNames.RunCommand, ("command", "nope")), "ERROR: not recognized"),
                new(new AgentToolCall("(verification)", new Dictionary<string, string>()), "note")
            };

            string digest = AgentFollowUp.BuildRunDigest(exchanges, "All done.", stoppedOnStepLimit: true, ["bg-1 Rocket static server (running)"]);

            Assert.Contains("ran out of steps", digest);
            Assert.Contains(@"Files created or rewritten: G:\Rocket\index.html, G:\Rocket\main.js", digest);
            Assert.Contains(@"Files edited: G:\Rocket\style.css", digest);
            Assert.Contains("Rocket static server", digest);
            Assert.Contains("curl.exe", digest);
            Assert.Contains("1 screenshot(s)", digest);
            Assert.Contains("1 failed, 1 blocked or refused", digest);
            Assert.Contains("bg-1 Rocket static server (running)", digest);
            Assert.Contains("All done.", digest);
        }

        // ───────────────────────────── a question turn only answers

        [Fact]
        public async Task AQuestionTurnRefusesToChangeAnythingAndStillAnswers()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write("oops.txt")),
                AgentModelReply.Answer("Yes - it is finished."));
            var session = new AgentSession(AgentScope.Folder(_folder), AgentTurnPolicy.QuestionTurnSteps);

            AgentRunResult result = await Run(session, model, new AgentTurnPolicy(AnswerOnly: true));

            Assert.Equal("Yes - it is finished.", result.FinalMessage);
            Assert.False(File.Exists(Path.Combine(_folder, "oops.txt")));
            Assert.False(result.Steps[0].Result!.Succeeded);
            Assert.StartsWith("Blocked:", result.Steps[0].Result!.Error);
            Assert.Contains("only answers", result.Steps[0].Result!.Error);
        }

        [Fact]
        public async Task AQuestionTurnAllowsTwoLooksThenDemandsTheAnswer()
        {
            File.WriteAllText(Path.Combine(_folder, "a.txt"), "hello");
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(List(".")),
                AgentModelReply.Tool(Call(AgentToolNames.ReadFile, ("path", "a.txt"))),
                AgentModelReply.Answer("It is done."));
            var session = new AgentSession(AgentScope.Folder(_folder), AgentTurnPolicy.QuestionTurnSteps);

            AgentRunResult result = await Run(session, model, new AgentTurnPolicy(AnswerOnly: true));

            Assert.Equal("It is done.", result.FinalMessage);
            Assert.False(result.StoppedOnStepLimit);
            Assert.Equal(2, result.Steps.Count);
            Assert.Contains("Answer the user's question now", model.SeenHistories[2][1].Observation);
        }

        [Fact]
        public async Task AQuestionTurnThatKeepsCallingToolsIsAskedOnceMoreForTheAnswer()
        {
            File.WriteAllText(Path.Combine(_folder, "a.txt"), "hello");
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(List(".")),
                AgentModelReply.Tool(Call(AgentToolNames.ReadFile, ("path", "a.txt"))),
                AgentModelReply.Tool(Call(AgentToolNames.SearchText, ("pattern", "hello"))),
                AgentModelReply.Answer("Yes, the build finished and the server is running."));
            var session = new AgentSession(AgentScope.Folder(_folder), AgentTurnPolicy.QuestionTurnSteps);

            AgentRunResult result = await Run(session, model, new AgentTurnPolicy(AnswerOnly: true));

            Assert.Contains("Yes, the build finished", result.FinalMessage);
            Assert.Equal(3, result.Steps.Count);
            Assert.Equal(AgentSession.WrapUpTool, model.SeenHistories[^1][^1].Call.Tool);
            Assert.Contains("Answer the user's question now", model.SeenHistories[^1][^1].Observation);
        }

        // ───────────────────────────── checking is the short part

        [Fact]
        public async Task ARunThatOnlyKeepsCheckingAfterBuildingIsAskedForItsSummary()
        {
            var replies = new List<AgentModelReply> { AgentModelReply.Tool(Write("index.html")) };
            replies.AddRange(Enumerable.Range(0, 12).Select(i => AgentModelReply.Tool(List("sub" + i))));
            var model = new ScriptedAgentModel(replies.Append(AgentModelReply.Answer("## Summary\nBuilt index.html and checked it.")).ToArray());
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 60);

            AgentRunResult result = await Run(session, model, new AgentTurnPolicy(VerificationBudget: 4));

            // 1 write + 4 checks, then the fifth check is refused and the run is closed.
            Assert.Equal(6, result.Steps.Count);
            Assert.False(result.StoppedOnStepLimit);
            Assert.Contains("checking phase is over", result.Steps[^1].Result!.Error);
            Assert.Equal(AgentSession.WrapUpTool, model.SeenHistories[^1][^1].Call.Tool);
            Assert.Contains("final Markdown summary", model.SeenHistories[^1][^1].Observation);
        }

        [Fact]
        public async Task TheModelIsWarnedBeforeTheCheckingBudgetRunsOut()
        {
            var replies = new List<AgentModelReply> { AgentModelReply.Tool(Write("index.html")) };
            replies.AddRange(Enumerable.Range(0, 3).Select(i => AgentModelReply.Tool(List("sub" + i))));
            replies.Add(AgentModelReply.Answer("Done and summarised."));
            var model = new ScriptedAgentModel(replies.ToArray());
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 60);

            AgentRunResult result = await Run(session, model, new AgentTurnPolicy(VerificationBudget: 6));

            Assert.Equal("Done and summarised.", result.FinalMessage);
            Assert.Contains(model.SeenHistories[^1], exchange => exchange.Observation.Contains("WRAP-UP WARNING"));
        }

        [Fact]
        public async Task EditingAgainResetsTheCheckingBudget()
        {
            var replies = new List<AgentModelReply>();
            for (int round = 0; round < 4; round++)
            {
                replies.Add(AgentModelReply.Tool(Write($"file{round}.txt")));
                replies.Add(AgentModelReply.Tool(List("a" + round)));
                replies.Add(AgentModelReply.Tool(List("b" + round)));
            }
            replies.Add(AgentModelReply.Answer("All four written."));
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 60);

            AgentRunResult result = await Run(session, new ScriptedAgentModel(replies.ToArray()), new AgentTurnPolicy(VerificationBudget: 3));

            Assert.Equal("All four written.", result.FinalMessage);
            Assert.Equal(12, result.Steps.Count);
        }

        [Fact]
        public async Task ARunThatNeverChangedAFileIsNotCutShortForLooking()
        {
            var replies = Enumerable.Range(0, 10).Select(i => AgentModelReply.Tool(List("dir" + i))).ToList();
            replies.Add(AgentModelReply.Answer("Here is what I found."));
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 60);

            AgentRunResult result = await Run(session, new ScriptedAgentModel(replies.ToArray()), new AgentTurnPolicy(VerificationBudget: 3));

            Assert.Equal("Here is what I found.", result.FinalMessage);
            Assert.Equal(10, result.Steps.Count);
        }

        [Fact]
        public async Task ThreeRefusedCallsInARowEndTheToolCallingAndAskForTheSummary()
        {
            int captures = 0;
            var executor = new AgentToolExecutor(
                AgentScope.Folder(_folder),
                screenCaptureAsync: (_, _) =>
                {
                    captures++;
                    return Task.FromResult((new byte[] { 1, 2, 3 }, 800, 600));
                });
            var replies = Enumerable.Range(0, 9)
                .Select(i => AgentModelReply.Tool(Call(AgentToolNames.TakeScreenshot, ("save_path", $"shot{i}.png"))))
                .Append(AgentModelReply.Answer("The page renders correctly."))
                .ToArray();
            var model = new ScriptedAgentModel(replies);
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 60, executor: executor);

            AgentRunResult result = await Run(session, model);

            // Two screenshots are taken, three more are refused, and then the run is closed
            // instead of being allowed to burn the remaining 55 steps the same way.
            Assert.Equal(2, captures);
            Assert.Equal(5, result.Steps.Count);
            Assert.False(result.StoppedOnStepLimit);
            Assert.Equal(AgentSession.WrapUpTool, model.SeenHistories[^1][^1].Call.Tool);
        }

        // ───────────────────────────── a run always ends with an account of itself

        [Fact]
        public async Task AtTheStepLimitTheModelIsAskedForASummaryAndItIsUsed()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Write("a.txt")),
                AgentModelReply.Tool(Write("b.txt")),
                AgentModelReply.Answer("## Summary\nWrote a.txt and b.txt. c.txt is still missing."));
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 2);

            AgentRunResult result = await Run(session, model);

            Assert.True(result.StoppedOnStepLimit);
            Assert.StartsWith("I reached the 2-step limit", result.FinalMessage);
            Assert.Contains("c.txt is still missing", result.FinalMessage);
            Assert.Equal(2, result.Steps.Count);
        }

        [Fact]
        public async Task WhenTheModelWillNotSummariseAxiomWritesTheSummaryFromTheSteps()
        {
            var replies = Enumerable.Range(0, 10).Select(i => AgentModelReply.Tool(Write($"f{i}.txt"))).ToArray();
            var session = new AgentSession(AgentScope.Folder(_folder), maxSteps: 3);

            AgentRunResult result = await Run(session, new ScriptedAgentModel(replies));

            Assert.True(result.StoppedOnStepLimit);
            Assert.StartsWith("I reached the 3-step limit", result.FinalMessage);
            Assert.Contains("### What was done this turn", result.FinalMessage);
            Assert.Contains("f0.txt", result.FinalMessage);
            Assert.Contains("f2.txt", result.FinalMessage);
            Assert.Contains("### Where it stopped", result.FinalMessage);
            Assert.Equal(3, result.Steps.Count);
            Assert.False(File.Exists(Path.Combine(_folder, "f3.txt")), "a tool call made in the summary turn must not run");
        }

        // ───────────────────────────── the loop guards

        [Fact]
        public async Task DifferentSlicesOfOneUnchangedFileCannotConsumeTheWholeRun()
        {
            File.WriteAllLines(Path.Combine(_folder, "index.html"), Enumerable.Range(1, 100).Select(i => "line " + i));
            var replies = Enumerable.Range(1, 9).Select(i => AgentModelReply.Tool(
                Call(AgentToolNames.ReadFile, ("path", "index.html"), ("start_line", i.ToString()), ("line_count", "1")))).ToList();
            replies.Add(AgentModelReply.Answer("The animation remains unverified."));
            var result = await Run(new AgentSession(AgentScope.Folder(_folder), 60), new ScriptedAgentModel(replies.ToArray()));

            Assert.Equal(6, result.Steps.Count(s => s.Result?.Succeeded == true));
            Assert.Equal(3, result.Steps.Count(s => s.Note == "repeated file inspection"));
            Assert.Contains("remains unverified", result.FinalMessage);
        }

        [Fact]
        public async Task EditingTheFileAllowsFreshInspection()
        {
            File.WriteAllLines(Path.Combine(_folder, "index.html"), Enumerable.Repeat("before", 10));
            var replies = Enumerable.Range(1, 6).Select(i => AgentModelReply.Tool(
                Call(AgentToolNames.ReadFile, ("path", "index.html"), ("start_line", i.ToString()), ("line_count", "1")))).ToList();
            replies.Add(AgentModelReply.Tool(Write("index.html")));
            replies.Add(AgentModelReply.Tool(Call(AgentToolNames.ReadFile, ("path", "./index.html"))));
            replies.Add(AgentModelReply.Answer("Changed the file; visual playback remains unverified."));
            var result = await Run(new AgentSession(AgentScope.Folder(_folder), 20), new ScriptedAgentModel(replies.ToArray()));
            Assert.Equal(7, result.Steps.Count(s => s.Call.Tool == AgentToolNames.ReadFile && s.Result?.Succeeded == true));
            Assert.DoesNotContain(result.Steps, s => s.Note == "repeated file inspection");
        }

        [Fact]
        public async Task PathAliasesDoNotBypassTheInspectionLimit()
        {
            File.WriteAllLines(Path.Combine(_folder, "index.html"), Enumerable.Repeat("same file", 10));
            var replies = Enumerable.Range(1, 7).Select(i => AgentModelReply.Tool(
                Call(AgentToolNames.ReadFile, ("path", i % 2 == 0 ? "./index.html" : "index.html"),
                    ("start_line", i.ToString()), ("line_count", "1")))).ToList();
            replies.Add(AgentModelReply.Answer("I need a targeted search to continue diagnosing."));
            var result = await Run(new AgentSession(AgentScope.Folder(_folder), 20), new ScriptedAgentModel(replies.ToArray()));
            Assert.Equal(6, result.Steps.Count(s => s.Result?.Succeeded == true));
            Assert.Contains(result.Steps, s => s.Note == "repeated file inspection");
        }

        [Fact]
        public async Task SearchAcceptsAFilePathAndStillConfinesItToTheProject()
        {
            File.WriteAllText(Path.Combine(_folder, "index.html"), "header\nfunction forward() { broken(); }\nfooter");
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var found = await executor.ExecuteAsync(Call(AgentToolNames.SearchText,
                ("path", "index.html"), ("pattern", "forward")), CancellationToken.None);
            Assert.True(found.Succeeded, found.Error);
            Assert.Contains("index.html:2:", found.Output);
            var denied = await executor.ExecuteAsync(Call(AgentToolNames.SearchText,
                ("path", "../outside.html"), ("pattern", "forward")), CancellationToken.None);
            Assert.False(denied.Succeeded);
            Assert.Contains("outside", denied.Error);
        }

        [Fact]
        public async Task LongReadsReportWhereTheModelActuallyStopped()
        {
            File.WriteAllLines(Path.Combine(_folder, "index.html"), Enumerable.Repeat(new string('x', 500), 100));
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
            var result = await executor.ExecuteAsync(Call(AgentToolNames.ReadFile,
                ("path", "index.html")), CancellationToken.None);
            Assert.True(result.Succeeded);
            string observation = result.ToObservation();
            Assert.Contains("Read stopped before line 10", observation);
            Assert.Contains("100 total lines", observation);
            Assert.DoesNotContain("output truncated", observation);
        }

        [Theory]
        [InlineData(AgentTier.Micro, 1200)]
        [InlineData(AgentTier.Compact, 3000)]
        public async Task SmallModelReadsFitTheirTranscriptBudgetWithoutAdviceToUseMissingTools(AgentTier tier, int budget)
        {
            File.WriteAllLines(Path.Combine(_folder, "index.html"), Enumerable.Repeat(new string('x', 100), 100));
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder), readBudgetChars: budget - 256);
            var result = await executor.ExecuteAsync(Call(AgentToolNames.ReadFile, ("path", "index.html")), CancellationToken.None);
            Assert.True(result.Succeeded);
            Assert.True(result.ToObservation().Length <= budget);
            Assert.Contains("Read stopped before line", result.Output);
            Assert.False(AgentTurnPolicy.For(AgentFollowUpKind.Contextual, tier).LimitFileInspection);
        }

        [Fact]
        public async Task UnsupportedWindowCaptureReportsItsRealFailure()
        {
            var executor = new AgentToolExecutor(AgentScope.Folder(_folder),
                windowCaptureAsync: (_, _, _) => Task.FromResult(new AgentScreenshot([], 0, 0,
                    "Window is minimized; no window state was changed.", ["msedge: Test"])));
            var result = await executor.ExecuteAsync(Call(AgentToolNames.TakeScreenshot,
                ("window", "Edge"), ("delay_ms", "0")), CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Contains("minimized", result.Error);
            Assert.DoesNotContain("No open window matches", result.Error);
        }

        [Theory]
        [InlineData("Start-Sleep -Seconds 2", true)]
        [InlineData("start-sleep 5", true)]
        [InlineData("sleep 3", true)]
        [InlineData("Start-Sleep 1; Get-ChildItem", false)]
        [InlineData("npm run build", false)]
        public void WaitingIsRecognisedHoweverLongItWaits(string command, bool isSleep)
        {
            Assert.Equal(isSleep, AgentSession.IsSleepOnlyCommand(command));
        }

        [Fact]
        public void EveryWaitCountsAsTheSameRepeatedCall()
        {
            string first = AgentSession.CallSignature(Call(AgentToolNames.RunCommand, ("command", "Start-Sleep -Seconds 2")));
            string second = AgentSession.CallSignature(Call(AgentToolNames.RunCommand, ("command", "Start-Sleep -Seconds 5")));
            string other = AgentSession.CallSignature(Call(AgentToolNames.RunCommand, ("command", "npm test")));

            Assert.Equal(first, second);
            Assert.NotEqual(first, other);
        }

        // ───────────────────────────── screenshots say what they show

        [Theory]
        [InlineData("$ws = New-Object -ComObject WScript.Shell; $ws.AppActivate('Edge')")]
        [InlineData("[Native]::ShowWindow($handle, 2)")]
        [InlineData("[Native]::ShowWindowAsync($handle, 0)")]
        [InlineData("[Native]::SetForegroundWindow($handle)")]
        [InlineData("[Native]::SetWindowPos($handle, 0, 0, 0, 0, 0, 0)")]
        [InlineData("(New-Object -ComObject Shell.Application).MinimizeAll()")]
        [InlineData("$app.CloseMainWindow()")]
        public async Task WindowControlCommandsAreBlockedBeforeTheirFirstExecution(string command)
        {
            foreach (string tool in new[] { AgentToolNames.RunCommand, AgentToolNames.StartBackgroundTask })
            {
                var executor = new AgentToolExecutor(AgentScope.Folder(_folder));
                var result = await executor.ExecuteAsync(Call(tool, ("command", command)), CancellationToken.None);
                Assert.False(result.Succeeded);
                Assert.Contains("must not hide, minimize", result.Error);
            }
        }

        [Fact]
        public async Task TheSessionDoesNotAskApprovalForAWindowControlCommand()
        {
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Call(AgentToolNames.RunCommand, ("command", "[Native]::ShowWindow($h, 2)"))),
                AgentModelReply.Answer("I will use a background page capture."));
            int approvals = 0;
            var session = new AgentSession(AgentScope.Folder(_folder), 5);
            var result = await session.RunAsync("Check the page", AgentApprovalMode.Manual, model,
                (_, _, _) => { approvals++; return Task.FromResult(AgentApprovalOutcome.Approve); },
                _ => { }, CancellationToken.None);
            Assert.Equal(0, approvals);
            Assert.Single(result.Steps);
            Assert.False(result.Steps[0].Result!.Succeeded);
        }

        [Fact]
        public void PageCaptureAcceptsLocalProjectFilesAndRejectsScopeEscapes()
        {
            string path = Path.Combine(_folder, "index.html");
            File.WriteAllText(path, "<h1>Test</h1>");
            var scope = AgentScope.Folder(_folder);
            Assert.Equal(path, AgentPageCapture.ResolveUrl(new Uri(path).AbsoluteUri, scope).LocalPath);
            Assert.Throws<ArgumentException>(() => AgentPageCapture.ResolveUrl("file:///C:/outside/index.html", scope));
            Assert.Throws<ArgumentException>(() => AgentPageCapture.ResolveUrl("file://server/share/index.html", scope));
            Assert.Throws<ArgumentException>(() => AgentPageCapture.ResolveUrl("javascript:alert(1)", scope));
        }

        [Fact]
        public async Task AScreenshotOfANamedWindowSaysWhichWindowItShows()
        {
            string? asked = null;
            var executor = new AgentToolExecutor(
                AgentScope.Folder(_folder),
                windowCaptureAsync: (window, _, _) =>
                {
                    asked = window;
                    return Task.FromResult(new AgentScreenshot(
                        [1, 2, 3], 1280, 800, "the window 'Rocket Visualizer - Microsoft Edge' (msedge)",
                        ["msedge: Rocket Visualizer - Microsoft Edge", "Code: main.js - Visual Studio Code"]));
                });

            AgentToolResult result = await executor.ExecuteAsync(
                Call(AgentToolNames.TakeScreenshot, ("window", "Edge"), ("delay_ms", "0")), CancellationToken.None);

            Assert.True(result.Succeeded, result.Error);
            Assert.Equal("Edge", asked);
            Assert.Contains("the window 'Rocket Visualizer - Microsoft Edge' (msedge)", result.Output);
            Assert.Contains("Open windows: msedge: Rocket Visualizer", result.Output);
            Assert.Contains("Never use AppActivate", result.Output);
            Assert.NotNull(result.ImageDataUrl);
            Assert.NotNull(result.ImagePath);
            Assert.True(File.Exists(result.ImagePath));
        }

        [Fact]
        public async Task AskingForAWindowThatIsNotOpenListsTheOnesThatAre()
        {
            var executor = new AgentToolExecutor(
                AgentScope.Folder(_folder),
                windowCaptureAsync: (_, _, _) => Task.FromResult(new AgentScreenshot(
                    [], 0, 0, "", ["msedge: Rocket Visualizer - Microsoft Edge"])));

            AgentToolResult result = await executor.ExecuteAsync(
                Call(AgentToolNames.TakeScreenshot, ("window", "Firefox"), ("delay_ms", "0")), CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Contains("No open window matches 'Firefox'", result.Error);
            Assert.Contains("msedge: Rocket Visualizer", result.Error);
        }

        [Fact]
        public void TheScreenshotToolAdvertisesItsWindowArgument()
        {
            OpenRouterToolDefinition tool = AgentToolSchemas.All().Single(definition => definition.Name == AgentToolNames.TakeScreenshot);

            Assert.Contains("window", tool.ParametersSchema.ToJsonString());
            Assert.Contains("never refocus", tool.Description);
        }

        // ───────────────────────────── prompt and tracker

        [Fact]
        public void ThePromptSaysToVerifyOnceStopAndAnswerQuestions()
        {
            string prompt = AgentPromptBuilder.Build(
                AgentTier.Full, AgentScope.Folder(_folder), AgentApprovalMode.Auto,
                nativeToolCalling: true, trackerFilePath: Path.Combine(_folder, "AXIOM_TASK_TRACKER.md"), visionCapable: true);

            Assert.Contains("Verify it ONCE", prompt);
            Assert.Contains("STOP as soon as the check passes", prompt);
            Assert.Contains("ANSWER it in words", prompt);
            Assert.Contains("Do NOT spend steps writing or rewriting it", prompt);
            Assert.Contains("window='Edge'", prompt);
            Assert.DoesNotContain("MANDATORY PERSONAL TASK TRACKER", prompt);
        }

        [Fact]
        public void TheTrackerKeepsTheTaskAndEarlierTurnsWhenAFollowUpArrives()
        {
            string path = Path.Combine(_folder, "tracker.md");

            WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(
                path,
                "Rocket visualizer",
                "Is it done?",
                completedSteps: ["Read index.html"],
                latestTurnSummary: "Yes - it is finished and running on port 8080.",
                originalTask: "Make me a 3D rocket visualizer that launches under a new folder",
                earlierTurns: [new AgentTurnRecord("Make me a 3D rocket visualizer that launches under a new folder",
                    "Built G:\\RocketVisualizer3D with four files and started the server.", 18, false)]);

            string tracker = File.ReadAllText(path);
            Assert.Contains("## Task", tracker);
            Assert.Contains("Make me a 3D rocket visualizer", tracker);
            Assert.Contains("## Current Objective", tracker);
            Assert.Contains("Is it done?", tracker);
            Assert.Contains("## Earlier Turns", tracker);
            Assert.Contains("Built G:\\RocketVisualizer3D with four files", tracker);
            Assert.Contains("18 step(s)", tracker);
            Assert.Contains("Yes - it is finished and running on port 8080.", tracker);
        }
    }
}
