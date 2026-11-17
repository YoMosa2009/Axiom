using System.Text;
using System.Text.Json;
using Malx_AI;
using Malx_AI.Agent;
using Malx_AI.ComputerUse;

namespace AxSim;

/// <summary>
/// Lets an outside agent stand in for the model loaded in Axiom. The harness runs the app's real
/// prompt builders, tool executor and controller logic; each model call is written to a request
/// file and the run waits for the matching reply file.
/// </summary>
internal static class Program
{
    // Renders the simulated Edge through a short scripted visit, for checking the stand-in itself.
    private static int EdgeShot(string work)
    {
        using var desktop = new SimDesktop(work);
        var steps = new (string Label, Malx_AI.ComputerUse.ComputerUseAction Action, bool Address)[]
        {
            ("open", new() { Type = Malx_AI.ComputerUse.ComputerUseActionType.Open, Text = "Microsoft Edge" }, false),
            ("type url", new() { Type = Malx_AI.ComputerUse.ComputerUseActionType.Type, Text = "wikipedia.org" }, true),
            ("ctrl+t", new() { Type = Malx_AI.ComputerUse.ComputerUseActionType.Key, Keys = "Ctrl+T" }, false),
            ("type query", new() { Type = Malx_AI.ComputerUse.ComputerUseActionType.Type, Text = "alan turing" }, true),
            ("enter", new() { Type = Malx_AI.ComputerUse.ComputerUseActionType.Key, Keys = "Enter" }, false),
        };
        foreach (var step in steps)
        {
            var capture = desktop.Capture();
            string result = desktop.Execute(step.Action, capture, step.Address);
            var after = desktop.Capture();
            Console.WriteLine($"{step.Label}: {result} -> {after.ForegroundWindowTitle} | addr='{after.BrowserState?.Address}' doc='{after.BrowserState?.DocumentAddress}' tabs={after.BrowserState?.TabCount} focus={after.BrowserState?.AddressHasFocus} | {desktop.LastImagePath}");
            foreach (var target in after.UiTargets) Console.WriteLine("   " + target.Describe());
        }
        return 0;
    }

    [STAThread]
    private static async Task<int> Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: axsim agent|cu|paintshot|dragprobe <workdir>");
            return 1;
        }

        string work = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(work);
        try
        {
            return args[0] switch
            {
                "agent" => await AgentSim.RunAsync(work, args.Skip(2).ToArray()),
                "cu" => await ComputerUseSim.RunAsync(work, args.Skip(2).ToArray()),
                "paintshot" => PaintShot.Run(work),
                "dragprobe" => await DragProbe.RunAsync(work),
                "edgeshot" => EdgeShot(work),
                "capturecheck" => await CaptureCheck.RunAsync(work, args.ElementAtOrDefault(2)),
                "pagecheck" => await CaptureCheck.RunPageAsync(work),
                "applicationcheck" => CaptureCheck.CheckApplication(work, args[2]),
                _ => 1
            };
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(work, "CRASH.txt"), ex.ToString());
            Console.WriteLine(ex);
            return 9;
        }
    }
}

/// <summary>Request/reply files for one role. A reply is awaited by polling.</summary>
internal sealed class RoleChannel
{
    private readonly string _dir;
    private int _seq;

    public RoleChannel(string protoRoot, string role)
    {
        _dir = Path.Combine(protoRoot, role);
        Directory.CreateDirectory(_dir);
    }

    public int Requests => _seq;

    public async Task<string> AskAsync(string request, CancellationToken token)
    {
        int seq = ++_seq;
        string req = Path.Combine(_dir, $"req-{seq:000}.md");
        string rep = Path.Combine(_dir, $"rep-{seq:000}.txt");
        File.WriteAllText(req, request, new UTF8Encoding(false));
        DateTime deadline = DateTime.UtcNow.AddMinutes(45);
        while (!File.Exists(rep))
        {
            token.ThrowIfCancellationRequested();
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"No reply for {req}");
            await Task.Delay(250, token);
        }

        await Task.Delay(150, token);
        return File.ReadAllText(rep);
    }

    public void Finish(string message)
        => File.WriteAllText(Path.Combine(_dir, "done.md"), message, new UTF8Encoding(false));
}

/// <summary>
/// The agent model as a file channel. Renders the conversation the way CloudAgentModel sends it
/// to a provider: system prompt and tool definitions, seeded chat turns, the goal, then each
/// tool result as it arrives.
/// </summary>
internal sealed class FileAgentModel : IAgentModel
{
    private readonly RoleChannel _channel;
    private readonly string _systemPrompt;
    private readonly IReadOnlyList<(string Role, string Content)> _priorChat;
    private readonly string _imageDir;
    private int _rendered;
    private bool _goalSent;
    private int _images;

    public FileAgentModel(RoleChannel channel, string systemPrompt, IReadOnlyList<(string Role, string Content)> priorChat, string imageDir)
    {
        _channel = channel;
        _systemPrompt = systemPrompt;
        _priorChat = priorChat;
        _imageDir = imageDir;
        Directory.CreateDirectory(imageDir);
    }

    public string? Unavailable => null;
    public List<string> Transcript { get; } = new();

    public async Task<AgentModelReply> NextAsync(string goal, IReadOnlyList<AgentExchange> history, CancellationToken token)
    {
        var request = new StringBuilder();
        if (!_goalSent)
        {
            request.AppendLine("=== SYSTEM PROMPT (exactly what Axiom sends the model) ===");
            request.AppendLine(_systemPrompt);
            request.AppendLine();
            request.AppendLine("=== TOOLS (native function definitions attached to the request) ===");
            foreach (OpenRouterToolDefinition tool in AgentToolSchemas.All())
                request.AppendLine($"- {tool.Name}: {tool.Description}\n  parameters: {tool.ParametersSchema.ToJsonString()}");
            request.AppendLine();
            var seeded = _priorChat.Take(Math.Max(0, _priorChat.Count - 1)).TakeLast(8).ToList();
            if (seeded.Count > 0)
            {
                request.AppendLine("=== EARLIER CONVERSATION (seeded chat turns) ===");
                foreach ((string role, string content) in seeded)
                    request.AppendLine($"[{(role == "user" ? "user" : "assistant")}]\n{content}\n");
            }

            for (int i = 0; i < history.Count; i++)
                AppendExchange(request, history[i]);
            _rendered = history.Count;
            request.AppendLine("=== USER MESSAGE ===");
            request.AppendLine(goal);
            _goalSent = true;
        }
        else
        {
            for (int i = _rendered; i < history.Count; i++)
                AppendExchange(request, history[i]);
            _rendered = history.Count;
        }

        request.AppendLine();
        request.AppendLine("=== YOUR TURN ===");
        request.AppendLine("Reply with EITHER one tool call as JSON {\"tool\":\"name\",\"arguments\":{...}} OR your final answer as plain Markdown text.");
        string text = request.ToString();
        Transcript.Add("REQUEST:\n" + text);
        string reply = (await _channel.AskAsync(text, token)).Trim();
        Transcript.Add("REPLY:\n" + reply);

        string body = reply;
        if (body.StartsWith("```", StringComparison.Ordinal))
        {
            int firstNewline = body.IndexOf('\n');
            int lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline > 0 && lastFence > firstNewline)
                body = body[(firstNewline + 1)..lastFence].Trim();
        }

        if (body.StartsWith('{') && body.Contains("\"tool\"", StringComparison.Ordinal))
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(body);
                string tool = doc.RootElement.GetProperty("tool").GetString() ?? "";
                var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (doc.RootElement.TryGetProperty("arguments", out JsonElement argumentElement) && argumentElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty property in argumentElement.EnumerateObject())
                        arguments[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? "" : property.Value.ToString();
                }

                if (!AgentToolNames.IsKnown(tool))
                    return AgentModelReply.Malformed($"'{tool}' is not an available tool. Use one of: {string.Join(", ", AgentToolNames.All)}.");
                return AgentModelReply.Tool(new AgentToolCall(tool.ToLowerInvariant(), arguments));
            }
            catch (Exception ex)
            {
                return AgentModelReply.Malformed("The tool call was not valid JSON: " + ex.Message);
            }
        }

        return reply.Length == 0
            ? AgentModelReply.Malformed("That reply was empty. Call a tool, or answer the question in words.")
            : AgentModelReply.Answer(reply);
    }

    private void AppendExchange(StringBuilder request, AgentExchange exchange)
    {
        if (exchange.Call.Tool.StartsWith('('))
        {
            bool fromUser = string.Equals(exchange.Call.Tool, AgentSession.UserMessageTool, StringComparison.Ordinal);
            request.AppendLine("=== USER MESSAGE ===");
            request.AppendLine(fromUser ? exchange.Observation : "[Axiom] " + exchange.Observation);
            return;
        }

        request.AppendLine($"=== TOOL RESULT for {exchange.Call.Tool} ===");
        request.AppendLine(exchange.Observation);
        if (!string.IsNullOrWhiteSpace(exchange.ImageDataUrl))
        {
            int comma = exchange.ImageDataUrl.IndexOf(',');
            string path = Path.Combine(_imageDir, $"observation-{++_images:00}.png");
            File.WriteAllBytes(path, Convert.FromBase64String(exchange.ImageDataUrl[(comma + 1)..]));
            request.AppendLine("=== USER MESSAGE ===");
            request.AppendLine("[Visual observation attached from tool output above — inspect this image carefully to verify visual output, rendering, and UI layout.]");
            request.AppendLine($"(IMAGE ATTACHED: open this file to see it: {path})");
        }
    }
}

internal static class AgentSim
{
    private static readonly string[] DefaultTurns =
    [
        "Make me a small animated solar system web page (separate index.html, style.css and main.js files, drawn on a canvas) under a new folder, serve it locally and check that it works.",
        "Is it done?",
        "Make the sun twice as big."
    ];

    public static async Task<int> RunAsync(string work, string[] extra)
    {
        string ws = Path.Combine(work, "ws");
        string proto = Path.Combine(work, "proto");
        Directory.CreateDirectory(ws);
        Directory.CreateDirectory(proto);
        var report = new StringBuilder();
        var scope = AgentScope.Folder(ws);
        string tracker = WorkplaceTaskTrackerFileService.ResolveTrackerPath(ws, 1);
        using var background = new AgentBackgroundTaskManager();
        var browser = new BrowserControlService();
        DetectedBrowserInfo info = BrowserControlService.DetectSingleBrowser();
        string browserSummary = $"{info.DisplayName} (search: {info.DefaultSearchEngineName})";
        // --tier=compact or --tier=micro runs a local GGUF tier: text protocol, no native tools.
        AgentTier tier = AgentTier.Full;
        if (extra.Length > 0 && extra[0].StartsWith("--tier=", StringComparison.OrdinalIgnoreCase))
        {
            tier = extra[0][7..].ToLowerInvariant() switch { "micro" => AgentTier.Micro, "compact" => AgentTier.Compact, _ => AgentTier.Full };
            extra = extra.Skip(1).ToArray();
        }
        bool native = tier == AgentTier.Full;
        string[] turns = extra.Length > 0 ? extra : DefaultTurns;

        // Mirrors WorkplaceView.RunComputerAgentTurnCoreAsync: same classification, same goal
        // construction, same policy, same step ceiling (cloud model, High effort).
        AgentActiveTaskState? state = null;
        var chat = new List<(string Role, string Content)>();
        var sessionTouched = new List<string>();
        using var cancel = new CancellationTokenSource();
        try
        {
            for (int turn = 0; turn < turns.Length; turn++)
            {
                string userQuery = turns[turn];
                string role = $"turn{turn + 1}";
                var channel = new RoleChannel(proto, role);
                chat.Add(("user", userQuery));

                AgentFollowUpKind kind = AgentFollowUp.Classify(userQuery, state != null);
                string effectiveGoal = userQuery;
                IReadOnlyList<AgentExchange>? initialHistory = null;
                List<string>? allowList = null;
                if (state != null && kind != AgentFollowUpKind.NewTask)
                {
                    allowList = state.SessionAllowList;
                    bool resume = state.StoppedOnStepLimit && AgentContextManager.IsContinuationPhrase(userQuery);
                    string? carry = resume || string.Equals(state.LastStatusMessage?.Trim(), "Stopped.", StringComparison.Ordinal)
                        ? state.LastStatusMessage : null;
                    string digest = resume ? string.Empty : AgentFollowUp.BuildRunDigest(
                        state.AccumulatedExchanges, state.LastStatusMessage, state.StoppedOnStepLimit,
                        background.ListTasks().Select(task => $"{task.Id} {task.Title} ({(task.IsRunning ? "running" : "finished")})").ToList());
                    effectiveGoal = AgentContextManager.BuildContinuationGoal(
                        state.OriginalGoal, userQuery, state.TouchedFiles, carry, state.VerifiedDependencies,
                        state.FileBackup!.Saved.Count > 0 ? state.FileBackup.Root : null, kind, digest);
                    initialHistory = resume ? AgentContextManager.CompactExchanges(state.AccumulatedExchanges, recentKeepCount: 8) : null;
                    state.LastTurnGoal = userQuery;
                }
                else
                {
                    state = new AgentActiveTaskState
                    {
                        OriginalGoal = userQuery,
                        LastTurnGoal = userQuery,
                        FileBackup = new AgentFileBackup(Path.Combine(work, "backups", DateTime.Now.ToString("yyyy-MM-dd_HHmmss")))
                    };
                }

                string systemPrompt = AgentPromptBuilder.Build(
                    tier, scope, AgentApprovalMode.Auto, nativeToolCalling: native,
                    trackerFilePath: tracker, browserSummary: browserSummary, visionCapable: native);
                var fileModel = new FileAgentModel(channel, systemPrompt, chat.ToList(), Path.Combine(work, "images", role));
                var transcriptLog = new List<string>();
                // The app's own local model wrapper (WorkplaceView.BuildAgentModel): it renders the
                // transcript and parses the flat text reply back. The prior chat is the app's too.
                IAgentModel model = native ? fileModel : new TextProtocolAgentModel(
                    async (prompt, transcript, ct) =>
                    {
                        var request = new StringBuilder();
                        if (transcriptLog.Count == 0)
                            request.AppendLine("=== SYSTEM PROMPT (exactly what Axiom sends the model) ===\n" + prompt + "\n");
                        else
                            request.AppendLine("=== SYSTEM PROMPT: identical to the one in your first request ===\n");
                        request.AppendLine("=== PROMPT (the whole transcript, exactly as Axiom renders it for a local model) ===");
                        request.AppendLine(transcript);
                        request.AppendLine("\n=== YOUR TURN: reply exactly as the system prompt tells you to. You have NO native tools; a tool call is plain text in the format the prompt gives. ===");
                        transcriptLog.Add(request.ToString());
                        string reply = await channel.AskAsync(request.ToString(), ct);
                        transcriptLog.Add("[reply]\n" + reply);
                        return reply;
                    },
                    systemPrompt,
                    tier == AgentTier.Micro ? 1200 : 3000,
                    chat.Take(Math.Max(0, chat.Count - 1)).ToList());
                int maxSteps = native
                    ? AgentPromptBuilder.MaxStepsFor(AgentTier.Full, EffortLevel.High)
                    : AgentPromptBuilder.MaxStepsFor(tier, EffortLevel.Medium, null, false);
                AgentTurnPolicy policy = AgentTurnPolicy.For(kind, tier);
                if (policy.AnswerOnly)
                    maxSteps = Math.Min(maxSteps, AgentTurnPolicy.QuestionTurnSteps);

                var executor = new AgentToolExecutor(
                    scope, state.FileBackup, background, browser,
                    screenCaptureAsync: (maxWidth, ct) => Task.Run(() => ComputerUseScreenCapture.CapturePrimaryScreenPng(maxWidth), ct),
                    windowCaptureAsync: (window, maxWidth, ct) => Task.Run(() => ComputerUseScreenCapture.CaptureForAgent(window, maxWidth), ct));
                var session = new AgentSession(scope, maxSteps, executor);
                var tracked = new List<string>();
                void UpdateTracker(string? summary) => WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(
                    tracker, "Simulated session", userQuery, tracked, sessionTouched, background.ListTasks(), summary,
                    state!.OriginalGoal, state.Turns);
                UpdateTracker("Agent execution started");

                AgentRunResult result = await session.RunAsync(
                    effectiveGoal, AgentApprovalMode.Auto, model,
                    (_, _, _) => Task.FromResult(AgentApprovalOutcome.Approve), _ => { }, cancel.Token,
                    initialHistory, allowList,
                    onStep: step =>
                    {
                        tracked.Add(step.Call.DescribeShort());
                        string path = step.Call.Arg("path");
                        if (step.Result is { Succeeded: true } && path.Length > 0
                            && step.Call.Tool is AgentToolNames.WriteFile or AgentToolNames.EditFile or AgentToolNames.AppendFile
                            && !sessionTouched.Contains(path, StringComparer.OrdinalIgnoreCase))
                            sessionTouched.Add(path);
                        UpdateTracker(step.Call.DescribeShort());
                    },
                    commandReviewMode: CommandReviewMode.AutoReview,
                    policy: policy);

                state.StoppedOnStepLimit = result.StoppedOnStepLimit;
                state.LastStatusMessage = result.FinalMessage;
                if (result.Exchanges is { Count: > 0 })
                {
                    state.AccumulatedExchanges.Clear();
                    state.AccumulatedExchanges.AddRange(result.Exchanges);
                    foreach (string file in AgentContextManager.ExtractTouchedFiles(result.Exchanges))
                        state.TouchedFiles.Add(file);
                    foreach (string dep in AgentContextManager.ExtractVerifiedDependencies(result.Exchanges))
                        state.VerifiedDependencies.Add(dep);
                }

                UpdateTracker(result.FinalMessage);
                state.Turns.Add(new AgentTurnRecord(userQuery, result.FinalMessage, result.Steps.Count, result.StoppedOnStepLimit));
                chat.Add(("assistant", result.FinalMessage));

                report.AppendLine($"## Turn {turn + 1}: {userQuery}");
                report.AppendLine($"- tier: {tier}; classified as: {kind}; step ceiling {maxSteps}; answer-only: {policy.AnswerOnly}");
                report.AppendLine($"- steps used: {result.Steps.Count}; stopped on limit: {result.StoppedOnStepLimit}; model calls: {channel.Requests}");
                foreach (AgentStep step in result.Steps)
                {
                    string outcome = step.Result == null ? "refused: " + step.Note : step.Result.Succeeded ? "ok" : "FAILED: " + (step.Result.Error ?? "").Split('\n')[0];
                    report.AppendLine($"  - {step.Call.Tool} {Short(step.Call)} -> {outcome}");
                }
                report.AppendLine("- final message:");
                report.AppendLine(Indent(result.FinalMessage));
                report.AppendLine();
                File.WriteAllText(Path.Combine(work, $"goal-turn{turn + 1}.txt"), effectiveGoal);
                File.WriteAllText(Path.Combine(work, $"transcript-turn{turn + 1}.txt"), string.Join("\n\n----------\n\n", native ? fileModel.Transcript : transcriptLog));
                channel.Finish("SIMULATION TURN FINISHED. The run accepted your final answer. Stop now and report back.");
            }
        }
        finally
        {
            background.StopAll();
            report.AppendLine("## Tracker file at the end");
            report.AppendLine(File.Exists(tracker) ? File.ReadAllText(tracker) : "(missing)");
            File.WriteAllText(Path.Combine(work, "REPORT.md"), report.ToString());
        }

        Console.WriteLine(report.ToString());
        return 0;
    }

    private static string Short(AgentToolCall call)
    {
        string detail = call.Arg("path");
        if (detail.Length == 0) detail = call.Arg("command");
        if (detail.Length == 0) detail = call.Arg("url");
        if (detail.Length == 0) detail = call.Arg("window");
        detail = detail.Replace('\r', ' ').Replace('\n', ' ');
        return detail.Length <= 100 ? detail : detail[..100] + "…";
    }

    private static string Indent(string text) => "    " + (text ?? "").Replace("\n", "\n    ");
}
