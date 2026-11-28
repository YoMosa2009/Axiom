using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>What an Ultra fresh review gets: the request, the agent's summary and the changed files.</summary>
    public sealed record AgentReviewRequest(string Goal, string Summary, IReadOnlyList<string> ChangedFiles);

    /// <summary>What a message sent after an agent run is asking for.</summary>
    public enum AgentFollowUpKind
    {
        /// <summary>No task is active, or the user asked to start over.</summary>
        NewTask,

        /// <summary>"continue", "keep going": carry on with the unfinished run.</summary>
        Resume,

        /// <summary>A question about the work ("Is it done?", "What did you do?"). Answer it.</summary>
        Question,

        /// <summary>A change, fix or addition to the existing work.</summary>
        Change,

        /// <summary>The model interprets the message using the task record and conversation.</summary>
        Contextual
    }

    /// <summary>Limits one agent turn runs under, chosen from what the user asked for.</summary>
    /// <param name="AnswerOnly">
    /// The turn answers a question: only tools that look are allowed, nothing that changes files
    /// or runs commands.
    /// </param>
    /// <param name="VerificationBudget">
    /// How many steps in a row may pass without changing a file once the run has changed one,
    /// before the run is told to wrap up. 0 turns the limit off.
    /// </param>
    public sealed record AgentTurnPolicy(bool AnswerOnly = false, int VerificationBudget = 0,
        bool LimitFileInspection = true)
    {
        /// <summary>
        /// Steps a run may take without changing any file before it is reminded, once, to start
        /// on the deliverable. 0 turns the reminder off. A logged run spent 59 steps probing an
        /// API with one-liners and ended with nothing built.
        /// </summary>
        public int ExplorationBudget { get; init; }

        /// <summary>
        /// Extra High and Ultra: a run that changed files must check them (read them back, run
        /// them or open them) after its last change before it may finish. Sent back once.
        /// </summary>
        public bool RequireCheckedFinish { get; init; }

        /// <summary>
        /// Ultra: a fresh review of the finished work, with a clean context that sees the request
        /// and the changed files, not the agent's own account. Problems it finds go back into the
        /// same run to be fixed (once). Replaces the Critic, which only commented.
        /// </summary>
        public Func<AgentReviewRequest, CancellationToken, Task<IReadOnlyList<string>>>? Reviewer { get; init; }

        /// <summary>How far a run carries on by itself. Null: it ends where it used to and waits for the user.</summary>
        public AgentAutonomy? Autonomy { get; init; }

        /// <summary>Longest tool result put in front of the model.</summary>
        public int ObservationChars { get; init; } = 6000;

        /// <summary>Longest plan update_plan accepts. Small models get a short one.</summary>
        public int MaxPlanItems { get; init; } = AgentPlan.MaxItemsFull;

        /// <summary>When and how the run's history is compacted while it works. Null: never during the run.</summary>
        public AgentCompactionPolicy? Compaction { get; init; }

        /// <summary>Adds the effort-level quality steps to a turn's policy.</summary>
        public AgentTurnPolicy WithEffort(EffortLevel effort, AgentTier tier)
        {
            if (AnswerOnly)
                return this;
            return this with
            {
                ExplorationBudget = tier switch
                {
                    AgentTier.Micro => 5,
                    AgentTier.Compact => 8,
                    _ => effort >= EffortLevel.ExtraHigh ? 20 : 15
                },
                // Small models loop on extra rules; the check is for models that can act on it.
                RequireCheckedFinish = effort >= EffortLevel.ExtraHigh && tier == AgentTier.Full,
            };
        }

        /// <summary>Steps a question turn gets: two quick looks and the answer.</summary>
        public const int QuestionTurnSteps = 3;

        public static AgentTurnPolicy Default { get; } = new();

        /// <summary>
        /// The policy for a turn, sized to the model. Smaller models wander sooner, so they get
        /// less room to check before being asked to finish.
        /// </summary>
        public static AgentTurnPolicy For(AgentFollowUpKind kind, AgentTier tier) => new(
            AnswerOnly: kind == AgentFollowUpKind.Question,
            VerificationBudget: tier switch
            {
                AgentTier.Micro => 4,
                AgentTier.Compact => 7,
                _ => 12
            },
            // The small flat-protocol tiers do not expose search_text. Their reads are already
            // smaller; do not block them with advice to use an unavailable tool.
            LimitFileInspection: tier == AgentTier.Full)
        {
            MaxPlanItems = tier == AgentTier.Full ? AgentPlan.MaxItemsFull : AgentPlan.MaxItemsCompact,
        };
    }

    /// <summary>
    /// What a run may do instead of stopping and waiting for "continue".
    /// </summary>
    /// <param name="MaxContinues">Times an unfinished answer is sent back to carry on.</param>
    /// <param name="BudgetExtensions">Extra step budgets, each as large as the first, while work is advancing.</param>
    /// <param name="Recoveries">Times a run that is going round in circles is told to change approach instead of closed.</param>
    /// <param name="ModelRetries">Times a failing or silent model is waited for and asked again.</param>
    public sealed record AgentAutonomy(int MaxContinues, int BudgetExtensions, int Recoveries, int ModelRetries)
    {
        /// <summary>No further extension or send-back starts after a run has lasted this long.</summary>
        public TimeSpan MaxDuration { get; init; } = TimeSpan.FromHours(4);

        /// <summary>How the run waits before retrying a model; replaced in tests.</summary>
        public Func<TimeSpan, System.Threading.CancellationToken, System.Threading.Tasks.Task> Wait { get; init; } =
            System.Threading.Tasks.Task.Delay;

        /// <summary>Marks an ending that really does need the user.</summary>
        public const string NeedsUserMarker = "NEEDS YOU:";

        public const string Instructions =
            "[WORK TO COMPLETION]\n"
            + "You are expected to finish the whole request in this run without the user prompting you again. "
            + "Do not stop to report progress, to ask whether to continue, or to announce what you will do next: do it. "
            + "For work of 3 or more steps keep a plan with update_plan, and keep going until every item is done and checked, "
            + "or closed with the reason it cannot be done. Choose sensible defaults for small decisions and say which you chose. "
            + "Stop for the user only when you need information or credentials only they have, when the next step is destructive "
            + "or hard to undo and they have not approved it, or when they or their instructions told you to wait. Then finish "
            + "everything else first and end your reply with a line that starts \"" + NeedsUserMarker + "\" followed by the one question.\n"
            + "[/WORK TO COMPLETION]";

        /// <summary>Small models loop, so they get a little rope; capable ones get a lot.</summary>
        public static AgentAutonomy For(AgentTier tier, bool metered) => tier == AgentTier.Full
            ? new AgentAutonomy(MaxContinues: 25, BudgetExtensions: metered ? 1 : 3, Recoveries: 2, ModelRetries: 3)
            : new AgentAutonomy(MaxContinues: 3, BudgetExtensions: 1, Recoveries: 1, ModelRetries: 2);

        private static readonly Regex AsksToContinueRegex = new(
            @"\b(?:(?:shall|should|may|can)\s+i\s+(?:continue|proceed|go\s+ahead|carry\s+on|start|move\s+on)"
            + @"|(?:do\s+you\s+want|would\s+you\s+like)\s+(?:me\s+to|to\s+have\s+me)\s+(?:continue|proceed|go\s+ahead|carry\s+on|start|move\s+on)"
            + @"|(?:say|send|reply|type)\s+[\*""'`]*(?:continue|go|proceed)\b"
            + @"|let\s+me\s+know\s+(?:if|when|whether)\s+(?:you(?:'d|\s+would)?\s+(?:like|want)\s+me\s+to|i\s+should)\s+(?:continue|proceed|go\s+ahead|carry\s+on)"
            + @"|i\s+(?:will|'ll)\s+(?:continue|proceed)\s+(?:in|with)\s+the\s+next\s+(?:turn|reply|message))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>True when an answer ends by waiting to be told to carry on.</summary>
        public static bool AsksToContinue(string? answer)
        {
            string text = (answer ?? string.Empty).Trim();
            // Only the ending counts: a report may quote such words earlier on.
            return AsksToContinueRegex.IsMatch(text.Length <= 600 ? text : text[^600..]);
        }

        private static readonly Regex AnnouncementRegex = new(
            @"^(?:(?:sure|okay|ok|alright|great)\b[,.!:]*\s*)?(?:now\s+)?(?:i(?:'ll|\s+will|'m\s+going\s+to|\s+am\s+going\s+to)\b|let\s+me\b(?!\s+know)|(?:creating|writing|running|reading|checking|starting|building|installing|updating)\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True for a short reply that only says what is about to happen. A local 7B model answered
        /// "Creating hello.py ... and running it" and ended the run without doing either.
        /// </summary>
        public static bool OnlyAnnounces(string? answer)
        {
            string text = (answer ?? string.Empty).Trim();
            return text.Length is > 0 and <= 300 && !text.Contains('?') && !text.Contains("```", StringComparison.Ordinal)
                && AnnouncementRegex.IsMatch(text);
        }

        public static bool NeedsUser(string? answer) =>
            (answer ?? string.Empty).Contains(NeedsUserMarker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides what a follow-up message wants, and builds the record of the previous run that
    /// lets the model answer it without redoing the work.
    /// </summary>
    /// <remarks>
    /// A logged session asked "Is it done?" after a 60-step build. The follow-up was sent as a
    /// change request with no record of what the first run had done and an instruction to
    /// "inspect, modify, or verify", so the model re-listed the folder, re-read every file in
    /// slices and took 17 more screenshots until it hit the 60-step limit again — and never
    /// answered. Two things were missing: knowing that a question is a question, and handing the
    /// model the facts it would otherwise have to rediscover.
    /// </remarks>
    public static class AgentFollowUp
    {
        private static readonly Regex QuestionOpenerRegex = new(
            @"^\s*(?:so\s*,?\s*|and\s+|ok(?:ay)?\s*,?\s*|hey\s*,?\s*|wait\s*,?\s*)?(?:is|are|was|were|did|does|do|has|have|had|what|what's|whats|where|where's|why|how|how's|which|who|when|will|would|should|am|isn't|aren't|didn't|doesn't)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StatusPhraseRegex = new(
            @"^\s*(?:status|progress|update|summary|any\s+(?:luck|progress|update)|done\s+yet|finished\s+yet|all\s+done|you\s+done|done)\s*[?.!]*\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "Can you add…", "could you fix…", "please make…": a request wearing a question mark.
        private static readonly Regex PoliteRequestRegex = new(
            @"\b(?:can|could|would|will)\s+you\b|\bplease\b|\bi\s+(?:want|need|would\s+like|'d\s+like)\b|\blet'?s\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private const string ChangeVerbs =
            "add|make|change|fix|update|remove|delete|rename|create|implement|build|write|replace|move|refactor|"
            + "install|uninstall|run|start|launch|open|close|stop|restart|rebuild|redo|undo|revert|convert|"
            + "improve|optimi[sz]e|clean|use|switch|set|enable|disable|increase|decrease|reduce|put|give|turn|"
            + "try|test|deploy|commit|push|generate|draw|show\\s+me|apply|adjust|tweak|swap|insert|append|edit|modify|"
            + "speed|slow|resize|recolou?r|colou?r|style|wire|hook|connect|include|drop|merge|split|extend|finish";

        // A sentence that opens with an instruction, optionally after a soft lead-in.
        private static readonly Regex ImperativeSentenceRegex = new(
            @"(?:^|[.!?\n;]\s*|,\s*(?:then\s+|and\s+)?)(?:(?:now|then|also|and|so|just|next|instead|ok(?:ay)?|but)\s*,?\s+)*(?:" + ChangeVerbs + @")\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>Classifies a message sent while a task is active.</summary>
        public static AgentFollowUpKind Classify(string? userQuery, bool hasActiveTask)
        {
            string text = (userQuery ?? string.Empty).Trim();
            if (!hasActiveTask || AgentContextManager.IsNewTaskPhrase(text))
                return AgentFollowUpKind.NewTask;
            if (AgentContextManager.IsContinuationPhrase(text))
                return AgentFollowUpKind.Resume;
            return IsQuestion(text) ? AgentFollowUpKind.Question : AgentFollowUpKind.Change;
        }

        /// <summary>
        /// True when the message only asks something. A request phrased as a question ("can you
        /// make it blue?") and a question followed by an instruction ("why is it blank? fix it")
        /// are both changes: when unsure the answer is "change", because wrongly refusing to act
        /// is worse than a turn that does a little more than was needed.
        /// </summary>
        public static bool IsQuestion(string? userQuery)
        {
            string text = (userQuery ?? string.Empty).Trim();
            if (text.Length == 0 || text.Length > 400)
                return false;
            if (StatusPhraseRegex.IsMatch(text))
                return true;

            bool looksLikeQuestion = text.Contains('?') || QuestionOpenerRegex.IsMatch(text);
            if (!looksLikeQuestion)
                return false;

            if (PoliteRequestRegex.IsMatch(text) || ImperativeSentenceRegex.IsMatch(text))
                return false;

            return true;
        }

        /// <summary>
        /// A compact, factual record of the last run, built by Axiom from the steps it executed.
        /// </summary>
        public static string BuildRunDigest(
            IReadOnlyList<AgentExchange>? exchanges,
            string? finalMessage,
            bool stoppedOnStepLimit,
            IReadOnlyList<string>? backgroundTasks = null,
            int maxFinalMessageChars = 1500)
        {
            var facts = RunFacts.From(exchanges);
            var builder = new StringBuilder();
            builder.AppendLine("[RECORD OF THE PREVIOUS RUN — written by Axiom from the steps that actually ran]");
            builder.AppendLine(stoppedOnStepLimit
                ? "How it ended: it ran out of steps before the model declared the task finished."
                : "How it ended: the run returned the message below. This alone does not prove the requested result works.");
            facts.AppendTo(builder);
            if (backgroundTasks is { Count: > 0 })
                builder.Append("Background tasks now: ").AppendLine(string.Join("; ", backgroundTasks.Take(6)));

            string message = (finalMessage ?? string.Empty).Trim();
            if (message.Length > 0)
            {
                builder.AppendLine("Its final message to the user:");
                builder.AppendLine(message.Length <= maxFinalMessageChars ? message : message[..maxFinalMessageChars] + "…");
            }

            builder.Append("[/RECORD OF THE PREVIOUS RUN]");
            return builder.ToString();
        }

        /// <summary>
        /// The summary shown when a run hits its step ceiling and the model did not write one.
        /// Always starts with "I reached the N-step limit" so the rest of the app recognises it.
        /// </summary>
        public static string BuildStepLimitSummary(IReadOnlyList<AgentStep> steps, int maxSteps)
        {
            var exchanges = (steps ?? [])
                .Where(step => step.Result != null)
                .Select(step => new AgentExchange(step.Call, step.Result!.ToObservation(400)))
                .ToList();
            var facts = RunFacts.From(exchanges);
            var builder = new StringBuilder();
            builder.AppendLine($"I reached the {maxSteps}-step limit for this model before finishing. Ask me to continue if the work so far looks right.");
            builder.AppendLine();
            builder.AppendLine("### What was done this turn");
            if (facts.IsEmpty)
                builder.AppendLine("- Nothing was changed on disk.");
            else
                facts.AppendTo(builder, bullets: true);
            AgentStep? last = (steps ?? []).LastOrDefault();
            if (last != null)
            {
                builder.AppendLine();
                builder.AppendLine("### Where it stopped");
                builder.AppendLine("- Last action: " + last.Call.DescribeShort()
                    + (last.Result == null ? $" ({last.Note})" : last.Result.Succeeded ? "" : " (failed)"));
                builder.AppendLine("- The task was not confirmed finished. Say **continue** to pick up from here, or tell me what to change.");
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>What a run did, grouped the way a person would report it.</summary>
        private sealed class RunFacts
        {
            private readonly List<string> _written = new();
            private readonly List<string> _edited = new();
            private readonly List<string> _commands = new();
            private readonly List<string> _background = new();
            private readonly List<string> _pages = new();
            private int _screenshots;
            private int _reads;
            private int _failed;
            private int _blocked;

            public bool IsEmpty => _written.Count + _edited.Count + _commands.Count + _background.Count + _pages.Count + _screenshots + _reads == 0;

            public static RunFacts From(IReadOnlyList<AgentExchange>? exchanges)
            {
                var facts = new RunFacts();
                foreach (AgentExchange exchange in exchanges ?? [])
                {
                    string tool = exchange.Call.Tool.ToLowerInvariant();
                    if (tool.StartsWith('('))
                        continue;

                    string observation = exchange.Observation ?? string.Empty;
                    if (observation.StartsWith("Blocked:", StringComparison.OrdinalIgnoreCase)
                        || observation.StartsWith("Refused:", StringComparison.OrdinalIgnoreCase)
                        || observation.StartsWith("The user declined", StringComparison.OrdinalIgnoreCase))
                    {
                        facts._blocked++;
                        continue;
                    }

                    if (observation.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
                    {
                        facts._failed++;
                        continue;
                    }

                    string path = exchange.Call.Arg("path");
                    switch (tool)
                    {
                        case AgentToolNames.WriteFile:
                            Add(facts._written, path);
                            break;
                        case AgentToolNames.EditFile:
                        case AgentToolNames.AppendFile:
                            if (!facts._written.Contains(path, StringComparer.OrdinalIgnoreCase))
                                Add(facts._edited, path);
                            break;
                        case AgentToolNames.RunCommand:
                            // Line breaks go, but spaces stay: collapsing them turned the four-space
                            // indent of a Set-Content Python body into one, and a model read it back wrong.
                            Add(facts._commands, Clip(Regex.Replace((exchange.Call.Arg("command") ?? string.Empty).Trim(), @"\s*[\r\n]+\s*", " "), 120));
                            break;
                        case AgentToolNames.StartBackgroundTask:
                            Add(facts._background, Flatten(exchange.Call.Arg("title", exchange.Call.Arg("command")), 90));
                            break;
                        case AgentToolNames.BrowserControl:
                            Add(facts._pages, Flatten(exchange.Call.Arg("action", "open") + " " + exchange.Call.Arg("url"), 90));
                            break;
                        case AgentToolNames.TakeScreenshot:
                            facts._screenshots++;
                            break;
                        case AgentToolNames.ReadFile:
                        case AgentToolNames.ListDirectory:
                        case AgentToolNames.FindFiles:
                        case AgentToolNames.SearchText:
                            facts._reads++;
                            break;
                    }
                }

                return facts;
            }

            public void AppendTo(StringBuilder builder, bool bullets = false)
            {
                string lead = bullets ? "- " : "";
                if (_written.Count > 0)
                    builder.Append(lead).Append("Files created or rewritten: ").AppendLine(string.Join(", ", _written.Take(24)));
                if (_edited.Count > 0)
                    builder.Append(lead).Append("Files edited: ").AppendLine(string.Join(", ", _edited.Take(24)));
                if (_background.Count > 0)
                    builder.Append(lead).Append("Background tasks started: ").AppendLine(string.Join("; ", _background.Take(6)));
                if (_commands.Count > 0)
                {
                    // One command per line. Joined with " | " they read as a single PowerShell
                    // pipeline, and a simulated small model concluded the file was never run.
                    var shown = _commands.TakeLast(4).ToList();
                    builder.Append(lead).AppendLine(_commands.Count > shown.Count
                        ? $"Commands run ({_commands.Count}), the last {shown.Count} in order:"
                        : $"Commands run ({_commands.Count}), in order:");
                    for (int i = 0; i < shown.Count; i++)
                        builder.Append("    ").Append(i + 1).Append(". ").AppendLine(shown[i]);
                }
                if (_pages.Count > 0)
                    builder.Append(lead).Append("Browser: ").AppendLine(string.Join("; ", _pages.TakeLast(3)));
                if (_screenshots + _reads > 0)
                    builder.Append(lead).AppendLine($"Checks: {_reads} file/folder look-ups, {_screenshots} screenshot(s).");
                if (_failed + _blocked > 0)
                    builder.Append(lead).AppendLine($"Steps that did not go through: {_failed} failed, {_blocked} blocked or refused.");
            }

            private static void Add(List<string> list, string value)
            {
                if (!string.IsNullOrWhiteSpace(value) && !list.Contains(value, StringComparer.OrdinalIgnoreCase))
                    list.Add(value);
            }

            private static string Clip(string value, int max) => value.Length <= max ? value : value[..max] + "…";

            private static string Flatten(string value, int max)
            {
                string flat = Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
                return flat.Length <= max ? flat : flat[..max] + "…";
            }
        }
    }
}
