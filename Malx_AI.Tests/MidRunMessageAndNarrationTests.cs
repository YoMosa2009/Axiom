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
    /// Owner requests (2026-11-26): a message sent while the agent works should pause it, be read,
    /// and be acted on without losing the original task; the run should say in the model's own
    /// words what it is doing; and the live run that was stopped for "repeating" a browser
    /// inspect between clicks must not be stopped again.
    /// </summary>
    public class MidRunMessageAndNarrationTests : IDisposable
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "AxiomMidRun", Guid.NewGuid().ToString("N"));

        public MidRunMessageAndNarrationTests() => Directory.CreateDirectory(_folder);

        public void Dispose()
        {
            if (Directory.Exists(_folder))
                Directory.Delete(_folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        private static Task<AgentApprovalOutcome> AlwaysApprove(AgentToolCall call, string reason, CancellationToken token)
            => Task.FromResult(AgentApprovalOutcome.Approve);

        private static AgentToolCall Call(string tool, params (string Key, string Value)[] args) =>
            new(tool, args.ToDictionary(a => a.Key, a => a.Value));

        /// <summary>A model whose turns are scripted functions, so a test can make one "think" until cancelled.</summary>
        private sealed class StepModel : IAgentModel
        {
            private readonly Queue<Func<IReadOnlyList<AgentExchange>, CancellationToken, Task<AgentModelReply>>> _turns;
            public List<IReadOnlyList<AgentExchange>> SeenHistories { get; } = new();
            public bool Composing { get; set; }

            public StepModel(params Func<IReadOnlyList<AgentExchange>, CancellationToken, Task<AgentModelReply>>[] turns)
                => _turns = new(turns);

            public string? Unavailable => null;
            public bool IsComposingToolCall => Composing;

            public Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
            {
                SeenHistories.Add(history.ToList());
                return _turns.Count > 0 ? _turns.Dequeue()(history, token) : Task.FromResult(AgentModelReply.Answer("done"));
            }
        }

        private static async Task<AgentModelReply> ThinkUntilCancelled(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return AgentModelReply.Answer("never");
        }

        [Fact]
        public async Task AMessageSentWhileTheModelThinksPausesThatTurnAndIsReadAtOnce()
        {
            var inbox = new AgentUserMessageInbox();
            var model = new StepModel(
                (_, token) => ThinkUntilCancelled(token),
                (_, _) => Task.FromResult(AgentModelReply.Answer("Switched to blue as you asked.")));

            Task<AgentRunResult> run = new AgentSession(AgentScope.Folder(_folder), 6).RunAsync(
                "build a red landing page", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None,
                inbox: inbox);
            await Task.Delay(150);
            inbox.Post("make it blue instead");

            AgentRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("Switched to blue as you asked.", result.FinalMessage);
            Assert.Empty(result.Steps);
            AgentExchange delivered = model.SeenHistories[1].Last();
            Assert.Equal(AgentSession.UserMessageTool, delivered.Call.Tool);
            Assert.Contains("make it blue instead", delivered.Observation);
            // The original task is restated so the message adds to it instead of replacing it.
            Assert.Contains("build a red landing page", delivered.Observation);
        }

        [Fact]
        public async Task ATurnAlreadyWritingAToolCallFinishesBeforeTheMessageIsRead()
        {
            var inbox = new AgentUserMessageInbox();
            StepModel? model = null;
            model = new StepModel(
                async (_, token) =>
                {
                    model!.Composing = true;
                    inbox.Post("also add a footer");
                    await Task.Delay(100, token);
                    return AgentModelReply.Tool(Call(AgentToolNames.WriteFile, ("path", "index.html"), ("content", "<h1>hi</h1>")));
                },
                (_, _) => Task.FromResult(AgentModelReply.Answer("Added the footer too.")));

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 6).RunAsync(
                "make a page", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None, inbox: inbox);

            Assert.True(File.Exists(Path.Combine(_folder, "index.html")));
            Assert.Single(result.Steps);
            Assert.Contains("also add a footer", model.SeenHistories[1].Last().Observation);
            Assert.Equal("Added the footer too.", result.FinalMessage);
        }

        [Fact]
        public async Task SeveralQueuedMessagesAreDeliveredTogetherInOrder()
        {
            var inbox = new AgentUserMessageInbox();
            var model = new StepModel(
                (_, _) =>
                {
                    inbox.Post("first");
                    inbox.Post("second");
                    return Task.FromResult(AgentModelReply.Tool(Call(AgentToolNames.ListDirectory, ("path", "."))));
                },
                (_, _) => Task.FromResult(AgentModelReply.Answer("ok")));
            int delivered = 0;
            var session = new AgentSession(AgentScope.Folder(_folder), 6) { InterruptTurnsForMessages = false, MessagesDelivered = n => delivered = n };

            await session.RunAsync("look", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None, inbox: inbox);

            string text = model.SeenHistories[1].Last().Observation;
            Assert.True(text.IndexOf("first", StringComparison.Ordinal) < text.IndexOf("second", StringComparison.Ordinal));
            Assert.Equal(2, delivered);
        }

        [Fact]
        public async Task StoppingWithAMessageStillQueuedEndsCleanly()
        {
            var inbox = new AgentUserMessageInbox();
            using var stop = new CancellationTokenSource();
            var model = new StepModel((_, token) =>
            {
                inbox.Post("never read");
                stop.Cancel();
                return ThinkUntilCancelled(token);
            });

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 6).RunAsync(
                "task", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, stop.Token, inbox: inbox);

            Assert.True(result.Cancelled);
            Assert.True(inbox.HasPending);
        }

        [Fact]
        public async Task TheRunReportsTheModelsOwnNarrationOrADescriptionOfTheCall()
        {
            var narrations = new List<string>();
            var model = new ScriptedAgentModel(
                AgentModelReply.Tool(Call(AgentToolNames.ListDirectory, ("path", "."))) with { Narration = "Checking what is already in the folder." },
                AgentModelReply.Tool(Call(AgentToolNames.ReadFile, ("path", "notes.md"))),
                AgentModelReply.Answer("done"));
            var session = new AgentSession(AgentScope.Folder(_folder), 6) { Narrated = narrations.Add };

            await session.RunAsync("task", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal(["Checking what is already in the folder.", "Reading notes.md"], narrations);
        }

        [Fact]
        public async Task InspectingThePageBetweenClicksIsNotTreatedAsRepeating()
        {
            // The live run (2026-11-26) was stopped on its third inspect after two clicks.
            var replies = new List<AgentModelReply>();
            for (int i = 0; i < 5; i++)
            {
                replies.Add(AgentModelReply.Tool(Call(AgentToolNames.BrowserControl, ("action", "inspect"))));
                replies.Add(AgentModelReply.Tool(Call(AgentToolNames.BrowserControl, ("action", "click"), ("target_id", "b" + (i + 1)))));
            }
            replies.Add(AgentModelReply.Answer("Read the article."));
            var model = new ScriptedAgentModel(replies.ToArray());

            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 20).RunAsync(
                "research", AgentApprovalMode.Auto, model, AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Equal("Read the article.", result.FinalMessage);
        }

        [Fact]
        public async Task InspectingAnUnchangedPageOverAndOverIsStillStopped()
        {
            var replies = Enumerable.Range(0, 10)
                .Select(_ => AgentModelReply.Tool(Call(AgentToolNames.BrowserControl, ("action", "inspect"))))
                .Concat(Enumerable.Range(0, 10).Select(_ => AgentModelReply.Tool(Call(AgentToolNames.ListDirectory, ("path", "x")))))
                .ToArray();
            AgentRunResult result = await new AgentSession(AgentScope.Folder(_folder), 30).RunAsync(
                "research", AgentApprovalMode.Auto, new ScriptedAgentModel(replies), AlwaysApprove, _ => { }, CancellationToken.None);

            Assert.Contains("repeat", result.FinalMessage, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void NarrationTakesTheModelsSentenceBeforeTheToolCall()
        {
            Assert.Equal("Reading the Wikipedia article to confirm the best known bound.",
                AgentNarration.FromModelText("Reading the Wikipedia article to confirm the best known bound.\n```json\n{\"tool\":\"x\"}\n```"));
            Assert.Null(AgentNarration.FromModelText("{\"tool\":\"read_file\"}"));
            Assert.Null(AgentNarration.FromModelText("   "));
        }

        [Fact]
        public void NarrationIsAboutASentenceAndAHalf()
        {
            string longText = string.Join(" ", Enumerable.Repeat("I am checking every single file in this large project folder carefully.", 6));
            string narration = AgentNarration.FromModelText(longText)!;
            Assert.True(narration.Length <= AgentNarration.MaxChars + 1);
        }

        [Fact]
        public void NarrationFromReasoningPrefersTheLastStatedIntent()
        {
            string reasoning = "The user wants the bound verified. The sieve already ran to 100000 without failures. "
                + "Let me open the MathWorld page to cite the known result. It should list the bound.";
            Assert.Equal("Let me open the MathWorld page to cite the known result.", AgentNarration.FromReasoning(reasoning));
        }

        [Fact]
        public void NarrationFromAProtocolReplyUsesTheTextBeforeTheCallOrTheThinking()
        {
            Assert.Equal("Writing the test file next.", AgentNarration.FromProtocolReply("Writing the test file next.\nTOOL write_file\npath: a.py\nEND"));
            Assert.Equal("I need to list the folder first.",
                AgentNarration.FromProtocolReply("<think>The folder may be empty. I need to list the folder first.</think>\nTOOL list_directory\npath: .\nEND"));
        }

        [Fact]
        public void BrowserStepsGetReadableLabelsAndNarration()
        {
            var inspect = Call(AgentToolNames.BrowserControl, ("action", "inspect"));
            Assert.DoesNotContain("a command", inspect.DescribeShort());
            Assert.Equal("Looking at the controls on the page", AgentNarration.FromCall(inspect));
            Assert.Equal("Searching the web for Legendre conjecture",
                AgentNarration.FromCall(Call(AgentToolNames.BrowserControl, ("action", "search"), ("url", "Legendre conjecture"))));
            Assert.Equal("Searching the web for prime gaps", AgentNarration.FromChatTool("web_search", "{\"query\":\"prime gaps\"}"));
        }

        [Fact]
        public void AMidRunMessageIsDeliveredAsTheUsersWordsWithTheOriginalTask()
        {
            string text = AgentUserMessageInbox.FormatForModel(["what's the status?"], "solve the conjecture");
            Assert.Contains("\"what's the status?\"", text);
            Assert.Contains("answer it briefly", text);
            Assert.Contains("Your original task is still: \"solve the conjecture\"", text);
        }

        [Fact]
        public void SnapshotJsonIsParsedIntoControlsAndControlIdsAreValidated()
        {
            var snapshot = HeadlessBrowserSession.ParseSnapshot(
                "{\"url\":\"https://example.com/\",\"title\":\"Example\",\"text\":\"Hello\",\"controls\":[{\"id\":\"b1\",\"kind\":\"link\",\"name\":\"More\\ninfo\"}],\"links\":[\"More -> https://iana.org\"]}");
            Assert.Equal("https://example.com/", snapshot.Url);
            Assert.Equal("More info", snapshot.Controls.Single().Name);
            string described = HeadlessBrowserSession.Describe(snapshot, includeText: true);
            Assert.Contains("b1 [link] More info", described);
            Assert.Contains("untrusted", described);
            Assert.Equal("b12", HeadlessBrowserSession.SafeId(" b12 "));
            Assert.Equal("invalid", HeadlessBrowserSession.SafeId("b1\"]');alert(1)//"));
        }

        [Fact]
        public async Task TheBackgroundBrowserOpensClicksFillsAndScreenshotsAPageWithoutAWindow()
        {
            if (HeadlessBrowserSession.FindBrowser() == null)
                return; // No Chromium browser on this machine.

            string page = Path.Combine(_folder, "page.html");
            File.WriteAllText(page, "<html><head><title>Test page</title></head><body><h1>Start</h1>"
                + "<input id='q' placeholder='Search here'><button onclick=\"document.querySelector('h1').textContent='Clicked '+document.getElementById('q').value\">Go</button></body></html>");

            await using var browser = new HeadlessBrowserSession();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var opened = await browser.OpenAsync(new Uri(page).AbsoluteUri, limit.Token);
            Assert.Equal("Test page", opened.Title);
            var field = opened.Controls.Single(c => c.Kind == "field");
            var button = opened.Controls.Single(c => c.Name == "Go");

            Assert.True(await browser.FillAsync(field.Id, "axiom", limit.Token));
            Assert.Equal("ok", await browser.ClickAsync(button.Id, limit.Token));
            var after = await browser.SnapshotAsync(limit.Token);
            Assert.Contains("Clicked axiom", after.Text);

            AgentScreenshot shot = await browser.ScreenshotAsync(limit.Token);
            Assert.True(shot.Width >= 800 && shot.Height >= 400);
        }
    }
}
