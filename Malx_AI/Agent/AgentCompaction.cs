using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    public enum AgentCompactionMode
    {
        /// <summary>Axiom shortens old steps itself. Used under 10B: a small model summarising itself is unreliable.</summary>
        Deterministic,

        /// <summary>The run's own model writes a structured checkpoint (cloud, Hybrid Local and 10B+ local).</summary>
        ModelSummary
    }

    /// <summary>When and how a running agent's history is compacted.</summary>
    /// <param name="ContextTokens">The model's context window in tokens.</param>
    /// <param name="ReserveTokens">Room kept free for the reply (the turn's output cap).</param>
    /// <param name="FixedPromptTokens">The system prompt and tool definitions, which compaction cannot shrink.</param>
    public sealed record AgentCompactionPolicy(
        int ContextTokens,
        AgentCompactionMode Mode,
        int ReserveTokens = 2048,
        int FixedPromptTokens = 0)
    {
        /// <summary>Fraction of the window that starts a compaction (Kimi and OpenCode use 0.85 with measured counts; Axiom estimates, so it starts earlier).</summary>
        public double TriggerRatio { get; init; } = 0.7;

        /// <summary>Where a compaction aims to leave the history, as a fraction of the trigger, so it does not run again next step.</summary>
        public double TargetRatio { get; init; } = 0.5;

        /// <summary>Most exchanges kept before compacting regardless of size (the cloud request trimmer drops old messages past a count).</summary>
        public int MaxExchanges { get; init; } = int.MaxValue;

        /// <summary>The latest exchanges kept word for word.</summary>
        public int KeepRecentExchanges { get; init; } = 4;

        /// <summary>A recent observation longer than this is clipped head and tail (DeepSeek Harness prunes at about 8K).</summary>
        public int RecentObservationMaxChars { get; init; } = 8000;

        /// <summary>The token count compaction starts at.</summary>
        public int TriggerTokens => Math.Max(1024, Math.Min((int)(ContextTokens * TriggerRatio), ContextTokens - ReserveTokens));

        /// <summary>
        /// The policy for a model, from its window and size. Under 10B (and unknown local sizes)
        /// stays deterministic; cloud, Hybrid Local and 10B+ local get a model-written checkpoint.
        /// </summary>
        internal static AgentCompactionPolicy For(int contextTokens, bool cloudOrHybrid, LocalModelSizeClass sizeClass,
            int reserveTokens, int fixedPromptTokens, int maxHistoryMessages = int.MaxValue)
        {
            bool summarise = cloudOrHybrid || sizeClass == LocalModelSizeClass.TenBPlus;
            int maxExchanges = maxHistoryMessages == int.MaxValue
                ? int.MaxValue
                // Two messages per exchange, plus room for the goal, seeded chat turns and images.
                : Math.Max(8, (maxHistoryMessages - 16) / 2);
            return new AgentCompactionPolicy(
                Math.Max(2048, contextTokens),
                summarise ? AgentCompactionMode.ModelSummary : AgentCompactionMode.Deterministic,
                reserveTokens,
                fixedPromptTokens)
            {
                MaxExchanges = maxExchanges,
                KeepRecentExchanges = sizeClass is LocalModelSizeClass.SubOneB or LocalModelSizeClass.OneToFourB && !cloudOrHybrid ? 3 : 4,
                RecentObservationMaxChars = cloudOrHybrid ? 8000 : 4000,
            };
        }
    }

    /// <summary>A model that can write a compaction checkpoint of the run so far.</summary>
    public interface IAgentSummarizer
    {
        /// <summary>
        /// Asks the model, with the run's own history in front of it, to answer
        /// <paramref name="instruction"/> in plain text with no tool call. Null on any failure.
        /// </summary>
        Task<string?> SummarizeAsync(string goal, IReadOnlyList<AgentExchange> history, string instruction, CancellationToken token);
    }

    /// <summary>
    /// Keeps a long run inside the model's context without losing its thread.
    /// </summary>
    /// <remarks>
    /// Before this, a run only ever grew: cloud requests had their oldest messages dropped by the
    /// request trimmer once past a count (silently, and with a new prompt prefix on every turn,
    /// so the cache missed), and local transcripts were cut from the front to fit, which can drop
    /// the task itself. The design follows the 2026-11-25 harness research:
    /// 1. Trigger on an estimated share of the window, or on the message count the transport keeps.
    /// 2. Cheap pruning first (DeepSeek Harness, Claude Code context editing): older steps become
    ///    one-line records and file bodies in old calls are dropped; long recent output is clipped
    ///    head and tail. Often that is enough and no model call is made.
    /// 3. Then, for models that can do it, a checkpoint written by the same model to a fixed
    ///    template (OpenCode/Kimi/DeepSeek headings), sent after the run's own history so the
    ///    prompt cache still applies. The "files changed" section is never the model's: Axiom
    ///    replaces it with its own record of what the run changed.
    /// 4. Fail closed: an empty, untemplated or oversized checkpoint is rejected (one nudge),
    ///    and the deterministic digest is used instead. After two failures, or two compactions
    ///    that leave the run still over its limit, compaction stops for the run (Claude Code's
    ///    "thrashing" guard).
    /// </remarks>
    public sealed class AgentRunCompactor
    {
        /// <summary>The pseudo-tool a compacted span of the run is recorded under.</summary>
        public const string SummaryTool = "(context_summary)";

        private const int MaxSummaryFailures = 2;
        private const int MaxIneffectiveCompactions = 2;
        private const int MinExchangesBetweenCompactions = 3;
        private const int DigestMaxLines = 40;

        /// <summary>Pruned older steps must be bigger than this (about a 900-word checkpoint) before a summary is worth writing.</summary>
        internal const int CheckpointWorthTokens = 1500;

        private readonly AgentCompactionPolicy _policy;
        private int _summaryFailures;
        private int _ineffectiveInARow;
        private int _exchangesAtLastCompaction = -1;

        public AgentRunCompactor(AgentCompactionPolicy policy) => _policy = policy ?? throw new ArgumentNullException(nameof(policy));

        public int Compactions { get; private set; }
        public bool Disabled { get; private set; }

        /// <summary>Token estimate for text. Code is token-dense, so chars/3, not the usual /4.</summary>
        internal static int EstimateTokens(string? text) => string.IsNullOrEmpty(text) ? 0 : (text.Length + 2) / 3;

        internal static int EstimateTokens(AgentExchange exchange)
        {
            int chars = exchange.Call.Tool.Length + exchange.Observation.Length + 16;
            foreach ((string key, string value) in exchange.Call.Arguments)
                chars += key.Length + (value?.Length ?? 0) + 4;
            return (chars + 2) / 3 + (exchange.ImageDataUrl != null ? 1000 : 0);
        }

        internal int EstimateTokens(string goal, IReadOnlyList<AgentExchange> history) =>
            _policy.FixedPromptTokens + EstimateTokens(goal) + history.Sum(EstimateTokens);

        public bool ShouldCompact(string goal, IReadOnlyList<AgentExchange> history)
        {
            if (Disabled || history.Count <= _policy.KeepRecentExchanges + 1)
                return false;
            if (_exchangesAtLastCompaction >= 0 && history.Count - _exchangesAtLastCompaction < MinExchangesBetweenCompactions
                && history.Count <= _policy.MaxExchanges)
                return false;
            return history.Count > _policy.MaxExchanges || EstimateTokens(goal, history) >= _policy.TriggerTokens;
        }

        private int TargetTokens => (int)(_policy.TriggerTokens * _policy.TargetRatio);

        private bool FitsTarget(string goal, List<AgentExchange> history)
        {
            int countTarget = _policy.MaxExchanges == int.MaxValue
                ? int.MaxValue
                : Math.Max(_policy.KeepRecentExchanges + 1, _policy.MaxExchanges / 2);
            return EstimateTokens(goal, history) <= TargetTokens && history.Count <= countTarget;
        }

        /// <summary>
        /// Compacts <paramref name="history"/> in place when it is due. Returns a one-line
        /// description of what was done, or null when nothing was.
        /// </summary>
        public async Task<string?> CompactIfNeededAsync(
            string goal,
            List<AgentExchange> history,
            IAgentModel model,
            IReadOnlyCollection<string> changedFiles,
            AgentPlan? plan,
            AgentActivityReporter? reportActivity,
            CancellationToken token)
        {
            if (!ShouldCompact(goal, history))
                return null;

            int before = EstimateTokens(goal, history);
            int countBefore = history.Count;
            reportActivity?.Invoke("Compacting the run's context");
            string how;
            try
            {
                List<AgentExchange> pruned = Prune(history, _policy);
                how = "pruned old steps";
                int keep = Math.Min(_policy.KeepRecentExchanges, pruned.Count);
                int splitAt = pruned.Count - keep;
                // Pruned older steps that are already shorter than a checkpoint would be cannot be
                // shrunk by one: asking the model then only costs requests (a live run on Hybrid
                // Local spent two ~1-minute requests per compaction this way).
                bool summaryCanHelp = pruned.Take(splitAt).Sum(EstimateTokens) > CheckpointWorthTokens
                    || pruned.Count > _policy.MaxExchanges;
                if (!FitsTarget(goal, pruned) && summaryCanHelp)
                {
                    // Pruning keeps every entry in place, so the same index splits the original
                    // history. The checkpoint's size check and the digest read the ORIGINAL steps:
                    // measured against the already-pruned stubs (a few hundred characters), every
                    // real checkpoint looked "longer than what it replaces" and was rejected
                    // (live run on Hybrid Local, 2026-11-25), and the digest showed placeholder sizes.
                    List<AgentExchange> older = history.Take(splitAt).ToList();
                    List<AgentExchange> recent = pruned.Skip(splitAt).ToList();

                    string? summary = null;
                    string? rejected = null;
                    if (_policy.Mode == AgentCompactionMode.ModelSummary && model is IAgentSummarizer summarizer
                        && _summaryFailures < MaxSummaryFailures)
                    {
                        (summary, rejected) = await WriteCheckpointAsync(summarizer, goal, history, older, changedFiles, plan, token).ConfigureAwait(false);
                        if (summary == null)
                            _summaryFailures++;
                    }

                    if (summary != null)
                        how = "model-written checkpoint";
                    else
                    {
                        summary = BuildDigest(older, changedFiles, plan);
                        how = rejected == null ? "deterministic digest" : $"deterministic digest (checkpoint rejected: {rejected.TrimEnd('.')})";
                    }

                    var summarised = new List<AgentExchange>(recent.Count + 1)
                    {
                        new(new AgentToolCall(SummaryTool, new Dictionary<string, string>()), summary)
                    };
                    summarised.AddRange(recent);
                    // A summary of a few short steps can be longer than the steps themselves (a
                    // live run grew from ~8,100 to ~8,190 tokens); then the pruned steps stay.
                    if (EstimateTokens(goal, summarised) < EstimateTokens(goal, pruned) || pruned.Count > _policy.MaxExchanges)
                        pruned = summarised;
                    else
                        how = "pruned old steps (a summary would not have been shorter)";
                }

                history.Clear();
                history.AddRange(pruned);
            }
            finally
            {
                reportActivity?.Invoke(null);
            }

            Compactions++;
            _exchangesAtLastCompaction = history.Count;
            int after = EstimateTokens(goal, history);
            if (after >= _policy.TriggerTokens)
            {
                if (++_ineffectiveInARow >= MaxIneffectiveCompactions)
                    Disabled = true;
            }
            else
            {
                _ineffectiveInARow = 0;
            }

            try { model.HistoryRewritten(); }
            catch { /* a model that cannot rebuild still sees the shorter history next turn */ }

            return $"{how}: {countBefore} -> {history.Count} steps, ~{before:N0} -> ~{after:N0} tokens"
                + (Disabled ? " (still over the limit; compaction off for this run)" : string.Empty);
        }

        /// <summary>
        /// Stage 1, no model call: older steps become their short record (errors and refusals keep
        /// their first line), file bodies in older calls are replaced by their size, and long
        /// recent output is clipped head and tail. User messages and earlier checkpoints stay.
        /// </summary>
        internal static List<AgentExchange> Prune(IReadOnlyList<AgentExchange> history, AgentCompactionPolicy policy)
        {
            var result = new List<AgentExchange>(history.Count);
            int splitAt = Math.Max(0, history.Count - policy.KeepRecentExchanges);
            for (int i = 0; i < history.Count; i++)
            {
                AgentExchange exchange = history[i];
                bool pseudo = exchange.Call.Tool.StartsWith('(');
                if (i >= splitAt)
                {
                    result.Add(exchange.Observation.Length > policy.RecentObservationMaxChars && !pseudo
                        ? exchange with
                        {
                            Observation = AgentOutputClip.HeadTail(exchange.Observation, policy.RecentObservationMaxChars,
                                "Shortened when the run's context was compacted. Use recall_context to read the full output.")
                        }
                        : exchange);
                    continue;
                }

                if (pseudo)
                {
                    // The user's own words and earlier checkpoints are kept; Axiom's notes shrink.
                    bool keepWhole = exchange.Call.Tool == AgentSession.UserMessageTool || exchange.Call.Tool == SummaryTool;
                    result.Add(keepWhole || exchange.Observation.Length <= 300
                        ? exchange
                        : exchange with { Observation = FirstLine(exchange.Observation, 300) });
                    continue;
                }

                result.Add(new AgentExchange(
                    ShrinkCall(exchange.Call),
                    AgentContextManager.CompactObservationForHistory(exchange.Call, exchange.Observation, 200)));
            }

            return result;
        }

        private static AgentToolCall ShrinkCall(AgentToolCall call)
        {
            bool large = call.Arguments.Any(pair => pair.Key is "content" or "old_string" or "new_string" && (pair.Value?.Length ?? 0) > 200);
            if (!large)
                return call;
            var arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string key, string value) in call.Arguments)
            {
                arguments[key] = key is "content" or "old_string" or "new_string" && (value?.Length ?? 0) > 200
                    ? $"(omitted after compaction: {value!.Split('\n').Length} lines, {value.Length} chars)"
                    : value ?? string.Empty;
            }

            return new AgentToolCall(call.Tool, arguments);
        }

        private static string FirstLine(string text, int max)
        {
            string line = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? text;
            return line.Length <= max ? line : line[..max] + "…";
        }

        // ---- the deterministic digest (stage 3, and the fallback for a failed checkpoint) ----

        internal static string BuildDigest(IReadOnlyList<AgentExchange> older, IReadOnlyCollection<string> changedFiles, AgentPlan? plan)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[EARLIER STEPS OF THIS RUN, COMPACTED BY AXIOM]");
            builder.AppendLine("The full tool results are not repeated here; use recall_context to read one again.");
            var lines = new List<string>();
            foreach (AgentExchange exchange in older)
            {
                if (exchange.Call.Tool == SummaryTool)
                {
                    // An earlier digest or checkpoint keeps its content but not its file list, plan
                    // or header: those are added once, current, below (a live run repeated them).
                    lines.AddRange(EarlierSummaryLines(exchange.Observation));
                    continue;
                }

                if (exchange.Call.Tool == AgentSession.UserMessageTool)
                {
                    lines.Add("User said: " + FirstLine(exchange.Observation, 400));
                    continue;
                }

                if (exchange.Call.Tool.StartsWith('('))
                    continue;

                lines.Add("- " + exchange.Call.DescribeForModel(100) + " -> "
                    + FirstLine(AgentContextManager.CompactObservationForHistory(exchange.Call, exchange.Observation, 120), 120));
            }

            int skipped = Math.Max(0, lines.Count - DigestMaxLines);
            if (skipped > 0)
                builder.AppendLine($"({skipped} earlier steps omitted)");
            foreach (string line in lines.Skip(skipped))
                builder.AppendLine(line);

            AppendLedger(builder, changedFiles);
            if (plan is { IsEmpty: false })
                builder.AppendLine("Current plan:").AppendLine(plan.Render());
            return builder.ToString().TrimEnd();
        }

        private static IEnumerable<string> EarlierSummaryLines(string summary)
        {
            bool inPlan = false;
            foreach (string raw in summary.Replace("\r", string.Empty).Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("Current plan", StringComparison.Ordinal))
                {
                    inPlan = true;
                    continue;
                }
                if (inPlan && (line.StartsWith("[x] ", StringComparison.Ordinal) || line.StartsWith("[>] ", StringComparison.Ordinal)
                        || line.StartsWith("[ ] ", StringComparison.Ordinal)))
                    continue;
                inPlan = false;
                if (line.Length == 0
                    || line.StartsWith("[EARLIER STEPS OF THIS RUN", StringComparison.Ordinal)
                    || line.StartsWith("The full tool results are not repeated", StringComparison.Ordinal)
                    || line.StartsWith("Files changed in this run", StringComparison.Ordinal)
                    || line.StartsWith("Older tool results can be read again", StringComparison.Ordinal))
                    continue;
                yield return line;
            }
        }

        private static void AppendLedger(StringBuilder builder, IReadOnlyCollection<string> changedFiles)
        {
            builder.AppendLine(changedFiles.Count == 0
                ? "Files changed in this run (Axiom's record): none yet."
                : "Files changed in this run (Axiom's record): " + string.Join(", ", changedFiles));
        }

        // ---- the model-written checkpoint ----

        internal static readonly string[] TemplateHeadings =
        [
            "## Objective",
            "## User requests",
            "## Decisions",
            "## Work state",
            "## Errors and fixes",
            "## Next step",
            "## Files changed",
            "## Important context"
        ];

        internal static string CheckpointInstruction(string? previousCheckpoint) =>
            "[Axiom] CONTEXT CHECKPOINT. The steps above are about to be removed from your context to make room. "
            + "Write a handoff checkpoint so you (or another model) can continue this run from it alone. "
            + "Do NOT call a tool. Reply with exactly these Markdown headings, in this order, writing (none) under any that is empty:\n"
            + string.Join("\n", TemplateHeadings) + "\n\n"
            + "Rules: keep exact file paths, commands, error messages, URLs and identifiers word for word. "
            + "Under User requests quote, word for word, every request the user made that is not fully done yet. "
            + "Under Work state list Completed, In progress and Blocked items. Under Errors and fixes keep each error message in full with what fixed it, if anything. "
            + "Do not describe checks you did not run. Keep it under 900 words."
            + (string.IsNullOrWhiteSpace(previousCheckpoint)
                ? string.Empty
                : "\nAn earlier checkpoint is part of the history above: update it rather than copying it. Newer facts win; finished items move out of In progress.");

        private static async Task<(string? Checkpoint, string? Problem)> WriteCheckpointAsync(
            IAgentSummarizer summarizer,
            string goal,
            IReadOnlyList<AgentExchange> fullHistory,
            IReadOnlyList<AgentExchange> older,
            IReadOnlyCollection<string> changedFiles,
            AgentPlan? plan,
            CancellationToken token)
        {
            string? previous = older.FirstOrDefault(exchange => exchange.Call.Tool == SummaryTool)?.Observation;
            string instruction = CheckpointInstruction(previous);
            int replacedChars = older.Sum(exchange => exchange.Observation.Length
                + exchange.Call.Arguments.Sum(pair => pair.Value?.Length ?? 0));

            string? problem = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string? reply;
                try
                {
                    // The full history, not the pruned copy: the request then starts with the
                    // same messages the model has already seen, so the provider's cache applies.
                    reply = await summarizer.SummarizeAsync(goal, fullHistory, instruction, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return (null, "the request failed: " + ex.Message);
                }

                string? checkpoint = ValidateAndRepair(reply, changedFiles, plan, replacedChars, out problem);
                if (checkpoint != null)
                    return (checkpoint, null);

                // One nudge (OpenCode does the same when the template is not filled in).
                instruction = CheckpointInstruction(previous) + $"\nYour last reply was not usable: {problem} Reply again with the headings filled in.";
            }

            return (null, problem);
        }

        private static readonly Regex HeadingRegex = new(@"(?m)^\s{0,3}#{1,3}\s*(?<name>[^\r\n#]+?)\s*:?\s*$", RegexOptions.Compiled);
        private static readonly Regex PathLikeRegex = new(
            @"(?<![\w/\\.])(?:[A-Za-z]:[\\/]|\.{0,2}[\\/])?(?:[\w.\-]+[\\/])*[\w\-]+\.[A-Za-z0-9]{1,8}(?![\w/\\])",
            RegexOptions.Compiled);

        /// <summary>
        /// Accepts a checkpoint only when it is real: not empty, most template headings present,
        /// and shorter than what it replaces. Its "Files changed" section is replaced by Axiom's
        /// own record, and files the model claims to have changed that the record does not show
        /// are named as unverified. Returns null (with the reason) when it is not usable.
        /// </summary>
        internal static string? ValidateAndRepair(string? reply, IReadOnlyCollection<string> changedFiles, AgentPlan? plan,
            int replacedChars, out string? problem)
        {
            problem = null;
            string text = AgentReplyText.StripThinking(reply ?? string.Empty).Trim();
            if (text.Length < 80)
            {
                problem = "it was empty or too short.";
                return null;
            }

            var sections = SplitSections(text);
            int matched = TemplateHeadings.Count(heading => sections.ContainsKey(NormalizeHeading(heading)));
            if (matched < 5)
            {
                problem = $"only {matched} of the {TemplateHeadings.Length} headings were there.";
                return null;
            }

            if (replacedChars > 0 && text.Length >= replacedChars)
            {
                problem = "it was longer than the steps it replaces.";
                return null;
            }

            string filesKey = NormalizeHeading("## Files changed");
            var claimed = new List<string>();
            if (sections.TryGetValue(filesKey, out string? filesBody))
            {
                foreach (Match match in PathLikeRegex.Matches(filesBody))
                {
                    string path = match.Value.Trim();
                    bool recorded = changedFiles.Any(file => PathsMatch(file, path));
                    if (!recorded && !claimed.Contains(path, StringComparer.OrdinalIgnoreCase))
                        claimed.Add(path);
                }
            }

            var builder = new StringBuilder();
            builder.AppendLine("[CHECKPOINT OF THIS RUN SO FAR - written by you before older steps were removed; Axiom checked the file list]");
            foreach (string heading in TemplateHeadings)
            {
                string key = NormalizeHeading(heading);
                builder.AppendLine(heading);
                if (key == filesKey)
                {
                    builder.AppendLine(changedFiles.Count == 0
                        ? "(none - Axiom's record shows no file changed by this run yet)"
                        : string.Join("\n", changedFiles.Select(file => "- " + file)) + "\n(from Axiom's record of this run)");
                    if (claimed.Count > 0)
                        builder.AppendLine("Not in Axiom's record, so treat as NOT changed: " + string.Join(", ", claimed.Take(10)));
                }
                else
                {
                    builder.AppendLine(sections.TryGetValue(key, out string? body) && body.Length > 0 ? body : "(none)");
                }

                builder.AppendLine();
            }

            if (plan is { IsEmpty: false })
                builder.AppendLine("Current plan (update_plan):").AppendLine(plan.Render());
            builder.Append("Older tool results can be read again with recall_context.");
            return builder.ToString().TrimEnd();
        }

        private static bool PathsMatch(string recorded, string claimed)
        {
            string a = recorded.Replace('\\', '/').Trim().TrimEnd('/');
            string b = claimed.Replace('\\', '/').Trim().TrimEnd('/');
            return a.Equals(b, StringComparison.OrdinalIgnoreCase)
                || a.EndsWith("/" + b.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase)
                || b.EndsWith("/" + a.TrimStart('.', '/'), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeHeading(string heading) =>
            Regex.Replace(heading.TrimStart('#', ' ').Trim().TrimEnd(':').ToLowerInvariant(), @"\s+", " ");

        private static Dictionary<string, string> SplitSections(string text)
        {
            var sections = new Dictionary<string, string>(StringComparer.Ordinal);
            MatchCollection headings = HeadingRegex.Matches(text);
            for (int i = 0; i < headings.Count; i++)
            {
                int start = headings[i].Index + headings[i].Length;
                int end = i + 1 < headings.Count ? headings[i + 1].Index : text.Length;
                string key = NormalizeHeading(headings[i].Groups["name"].Value);
                if (!sections.ContainsKey(key))
                    sections[key] = text[start..end].Trim();
            }

            return sections;
        }
    }

    /// <summary>Strips a leaked reasoning block from a plain-text reply.</summary>
    internal static class AgentReplyText
    {
        private static readonly Regex ThinkBlockRegex = new(@"(?is)<think>.*?</think>", RegexOptions.Compiled);

        public static string StripThinking(string text)
        {
            string cleaned = ThinkBlockRegex.Replace(text, string.Empty);
            int close = cleaned.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            return close >= 0 ? cleaned[(close + "</think>".Length)..] : cleaned;
        }
    }
}
