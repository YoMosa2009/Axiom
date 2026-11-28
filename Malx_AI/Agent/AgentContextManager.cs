using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Preserves context and task continuity across multi-turn agent runs, step limits,
    /// and long-horizon tasks (inspired by Claude Code, OpenCode, and Codex).
    /// </summary>
    public static class AgentContextManager
    {
        private static readonly Regex ContinuationPhraseRegex = new(
            @"^\s*(?:continue|keep\s+going|proceed|go\s+on|go\s+ahead|yes|yes\s+please|please\s+continue|finish\s+it|finish|do\s+it|retry|resume)\s*[.!]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NewTaskPhraseRegex = new(
            @"^\s*(?:new\s+task|new\s+project|start\s+(?:a\s+)?new\s+project|start\s+fresh|reset\s+agent|clear\s+agent|new\s+chat)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// True when the user's message is an explicit continuation command.
        /// </summary>
        public static bool IsContinuationPhrase(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return ContinuationPhraseRegex.IsMatch(text.Trim());
        }

        /// <summary>
        /// True when the user explicitly requests starting a brand new task or resetting project state.
        /// </summary>
        public static bool IsNewTaskPhrase(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;
            return NewTaskPhraseRegex.IsMatch(text.Trim());
        }

        /// <summary>
        /// Collects all file paths created, edited, read, or referenced by tool calls.
        /// </summary>
        public static HashSet<string> ExtractTouchedFiles(IEnumerable<AgentExchange> exchanges)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exchanges == null)
                return files;

            foreach (var exchange in exchanges)
            {
                string path = exchange.Call.Arg("path");
                if (!string.IsNullOrWhiteSpace(path))
                    files.Add(path);
            }
            return files;
        }

        /// <summary>
        /// Compacts older exchanges so that long-horizon tasks (20 to 128+ steps)
        /// don't overwhelm model context windows or cause context rot.
        /// The most recent <paramref name="recentKeepCount"/> steps are retained with full observations.
        /// </summary>
        public static IReadOnlyList<AgentExchange> CompactExchanges(
            IReadOnlyList<AgentExchange> history,
            int recentKeepCount = 5,
            int olderMaxChars = 200)
        {
            if (history == null || history.Count == 0)
                return [];

            if (history.Count <= recentKeepCount)
                return history;

            var compacted = new List<AgentExchange>(history.Count);
            int splitIndex = history.Count - recentKeepCount;

            for (int i = 0; i < history.Count; i++)
            {
                AgentExchange exchange = history[i];
                if (i >= splitIndex)
                {
                    // Recent turn: keep full fidelity
                    compacted.Add(exchange);
                }
                else
                {
                    // Older turn: compact observation to essential summary
                    string summary = CompactObservation(exchange.Call, exchange.Observation, olderMaxChars);
                    compacted.Add(new AgentExchange(exchange.Call, summary));
                }
            }

            return compacted;
        }

        private const int MaxCarriedExchanges = 400;
        private const string AnswerTool = "(answer)";

        /// <summary>
        /// Adds one finished turn to the task's history: the request, the steps that ran, and the
        /// reply. When the run compacted away the record it started from, its checkpoint already
        /// covers the earlier turns and replaces them.
        /// </summary>
        public static List<AgentExchange> AppendTurn(
            IReadOnlyList<AgentExchange> earlier,
            IReadOnlyList<AgentExchange> run,
            AgentExchange? carried,
            string userRequest,
            string finalMessage)
        {
            bool replaced = carried != null && run.Count > 0 && !run.Any(exchange => ReferenceEquals(exchange, carried));
            var history = new List<AgentExchange>();
            if (!replaced)
                history.AddRange(earlier);
            history.Add(Note(AgentSession.UserMessageTool, "USER ASKED: " + (userRequest ?? string.Empty).Trim()));
            history.AddRange(run
                .Where(exchange => !ReferenceEquals(exchange, carried)
                    && (!exchange.Call.Tool.StartsWith('(')
                        || exchange.Call.Tool is AgentSession.UserMessageTool or AgentRunCompactor.SummaryTool))
                .Select(exchange => exchange with { ImageDataUrl = null }));
            if (!string.IsNullOrWhiteSpace(finalMessage))
                history.Add(Note(AnswerTool, "YOU REPLIED: " + finalMessage.Trim()));
            if (history.Count > MaxCarriedExchanges)
                history.RemoveRange(0, history.Count - MaxCarriedExchanges);
            return history;

            static AgentExchange Note(string tool, string text) =>
                new(new AgentToolCall(tool, new Dictionary<string, string>()), text);
        }

        /// <summary>The steps of the latest turn in a task history.</summary>
        public static List<AgentExchange> LastTurn(IReadOnlyList<AgentExchange> history)
        {
            int start = history.Count;
            while (start > 0)
            {
                AgentExchange exchange = history[--start];
                if (exchange.Call.Tool == AgentSession.UserMessageTool
                    && exchange.Observation.StartsWith("USER ASKED: ", StringComparison.Ordinal))
                    break;
            }

            return history.Skip(start).ToList();
        }

        /// <summary>
        /// The earlier turns of a task as one history entry that every transport can send. A
        /// follow-up used to start with only file names and the last four commands, so a model
        /// re-guessed a server login it had used minutes before. Newest steps come with their
        /// output, older ones as one line each, within <paramref name="budgetChars"/>.
        /// </summary>
        public static AgentExchange? BuildCarryOver(IReadOnlyList<AgentExchange> earlier, int budgetChars)
        {
            if (earlier == null || earlier.Count == 0 || budgetChars < 500)
                return null;

            var entries = new string?[earlier.Count];
            int used = 0;
            int detailLimit = budgetChars * 7 / 10;

            // What the user asked is short and decides everything else, so it goes in first.
            for (int i = earlier.Count - 1; i >= 0; i--)
            {
                if (earlier[i].Call.Tool != AgentSession.UserMessageTool)
                    continue;
                string text = Cut(earlier[i].Observation, 600);
                if (used + text.Length > budgetChars / 4)
                    break;
                entries[i] = text;
                used += text.Length;
            }

            int omitted = 0;
            bool latestAnswer = true;
            for (int i = earlier.Count - 1; i >= 0; i--)
            {
                if (entries[i] != null)
                    continue;
                AgentExchange exchange = earlier[i];
                bool detailed = used < detailLimit;
                string text;
                if (exchange.Call.Tool == AgentRunCompactor.SummaryTool)
                {
                    text = AgentOutputClip.HeadTail(exchange.Observation, Math.Max(500, budgetChars / 3));
                }
                else if (exchange.Call.Tool == AnswerTool)
                {
                    text = Cut(exchange.Observation, latestAnswer ? 1500 : 400);
                    latestAnswer = false;
                }
                else if (exchange.Call.Tool.StartsWith('('))
                {
                    text = Cut(exchange.Observation, 400);
                }
                else
                {
                    string observation = exchange.Observation ?? string.Empty;
                    string status = observation.StartsWith("ERROR", StringComparison.Ordinal) ? "FAILED"
                        : IsBlockedOutcome(observation) ? "BLOCKED" : "ok";
                    text = detailed
                        ? $"- [{status}] {exchange.Call.DescribeForModel(1200)}\n{AgentOutputClip.HeadTail(observation, Math.Min(2000, budgetChars / 5))}"
                        : $"- [{status}] {exchange.Call.DescribeForModel(300)}" + (status == "ok" ? string.Empty : " -> " + Cut(FirstUsefulLine(observation), 160));
                }

                if (used + text.Length > budgetChars)
                {
                    omitted++;
                    continue;
                }

                entries[i] = text;
                used += text.Length;
            }

            var builder = new StringBuilder();
            builder.AppendLine("[EARLIER TURNS OF THIS TASK - recorded by Axiom from what actually ran, oldest first]");
            builder.AppendLine("Steps marked FAILED or BLOCKED did not happen. Reuse the commands, paths and connection details that are marked ok instead of guessing them again. recall_context returns any full result.");
            if (omitted > 0)
                builder.AppendLine($"({omitted} older entries left out for space)");
            foreach (string? entry in entries)
            {
                if (entry != null)
                    builder.AppendLine(entry);
            }

            builder.Append("[/EARLIER TURNS]");
            return new AgentExchange(
                new AgentToolCall(AgentRunCompactor.SummaryTool, new Dictionary<string, string>()),
                builder.ToString());

            static string Cut(string value, int max) => value.Length <= max ? value : value[..max] + "…";

            static string FirstUsefulLine(string value) =>
                value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault(line => !line.StartsWith("ERROR: [Exit code:", StringComparison.Ordinal) && line != "[stderr]")
                ?? value;
        }

        private static bool IsBlockedOutcome(string observation) =>
            observation.StartsWith("Refused:", StringComparison.OrdinalIgnoreCase)
            || observation.StartsWith("Blocked:", StringComparison.OrdinalIgnoreCase)
            || observation.StartsWith("Repetitive action blocked", StringComparison.OrdinalIgnoreCase)
            || observation.StartsWith("The user declined", StringComparison.OrdinalIgnoreCase);

        /// <summary>The short record an older step is reduced to when a history is compacted.</summary>
        internal static string CompactObservationForHistory(AgentToolCall call, string observation, int maxChars)
            => CompactObservation(call, observation, maxChars);

        private static string CompactObservation(AgentToolCall call, string observation, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(observation))
                return "(no output)";

            string tool = call.Tool.ToLowerInvariant();
            string path = call.Arg("path");

            // A command that ran and failed: its first line is only "[Exit code: 1 | Duration …]";
            // the error the model needs is at the end of the output.
            if (tool == AgentToolNames.RunCommand && observation.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                && ExitCodeRegex.IsMatch(observation))
                return observation.Length <= maxChars ? observation : "ERROR: " + CompactCommand(call.Arg("command"), observation, maxChars);

            if (DidNotRun(observation))
            {
                string firstLine = observation.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? observation;
                return firstLine.Length <= maxChars ? firstLine : firstLine[..maxChars] + "…";
            }

            return tool switch
            {
                AgentToolNames.ReadFile => $"[Read {path}]",
                AgentToolNames.WriteFile => $"[Wrote {path}]",
                AgentToolNames.AppendFile => $"[Appended to {path}]",
                AgentToolNames.EditFile => $"[Edited {path}]",
                AgentToolNames.ListDirectory => $"[Listed {path}]",
                AgentToolNames.FindFiles => $"[Found files matching '{call.Arg("pattern")}']",
                AgentToolNames.SearchText => $"[Searched '{call.Arg("pattern")}']",
                AgentToolNames.RunCommand => observation.Length <= maxChars ? observation : CompactCommand(call.Arg("command"), observation, maxChars),
                _ => observation.Length <= maxChars ? observation : observation[..maxChars] + "…"
            };
        }

        private static readonly Regex ExitCodeRegex = new(@"\[Exit code:\s*(-?\d+)", RegexOptions.Compiled);

        /// <summary>
        /// A long command result becomes one line. A failed command keeps its exit code and the
        /// end of its output, where the error is: it used to become "[Ran …: ok]", and a live run
        /// (Hybrid Local, 2026-11-25) lost a failing unit test that way after compaction.
        /// </summary>
        private static string CompactCommand(string command, string observation, int maxChars)
        {
            string shortCommand = command.Length <= 80 ? command : command[..80] + "…";
            Match exit = ExitCodeRegex.Match(observation);
            if (!exit.Success || exit.Groups[1].Value == "0")
                return $"[Ran {shortCommand}: ok]";

            string tail = string.Join(" | ", observation
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith("[Exit code:", StringComparison.Ordinal))
                .TakeLast(3));
            if (tail.Length > maxChars)
                tail = "…" + tail[^maxChars..];
            return $"[Ran {shortCommand}: FAILED, exit code {exit.Groups[1].Value}] {tail}";
        }

        /// <summary>
        /// Errors and steps that never ran (refused by policy, blocked as a repeat, declined by the
        /// user) keep their first line, so a compacted history never claims they happened.
        /// </summary>
        private static bool DidNotRun(string observation)
            => observation.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase)
                || observation.StartsWith("Refused:", StringComparison.OrdinalIgnoreCase)
                || observation.StartsWith("Blocked:", StringComparison.OrdinalIgnoreCase)
                || observation.StartsWith("The user declined", StringComparison.OrdinalIgnoreCase);

        private static readonly Regex DepCheckRegex = new(
            @"(?:import\s+([a-zA-Z0-9_\-]+)|pip\s+show\s+([a-zA-Z0-9_\-]+)|pip\s+install\s+([a-zA-Z0-9_\-]+))",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Collects packages and tools verified to be installed or working from successful command executions.
        /// </summary>
        public static HashSet<string> ExtractVerifiedDependencies(IEnumerable<AgentExchange> exchanges)
        {
            var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (exchanges == null)
                return deps;

            foreach (var ex in exchanges)
            {
                if (ex.Call.Tool.Equals(AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase))
                {
                    string cmd = ex.Call.Arg("command");
                    if (ex.Observation.Contains("[Exit code: 0") || (!ex.Observation.Contains("[Exit code:") && !ex.Observation.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)))
                    {
                        var matches = DepCheckRegex.Matches(cmd);
                        foreach (Match m in matches)
                        {
                            string pkg = m.Groups[1].Value;
                            if (string.IsNullOrWhiteSpace(pkg)) pkg = m.Groups[2].Value;
                            if (string.IsNullOrWhiteSpace(pkg)) pkg = m.Groups[3].Value;
                            if (!string.IsNullOrWhiteSpace(pkg) && !pkg.Equals("sys", StringComparison.OrdinalIgnoreCase) && !pkg.Equals("os", StringComparison.OrdinalIgnoreCase))
                            {
                                deps.Add(pkg);
                            }
                        }
                    }
                }
            }
            return deps;
        }

        /// <summary>
        /// Builds the contextual goal for continuing an agent task.
        /// </summary>
        public static string BuildContinuationGoal(
            string originalGoal,
            string userQuery,
            IReadOnlyCollection<string> touchedFiles,
            string? lastErrorOrStatus,
            IReadOnlyCollection<string>? verifiedDependencies = null,
            string? backupFolder = null,
            AgentFollowUpKind? kind = null,
            string? previousRunDigest = null)
        {
            var builder = new StringBuilder();
            AgentFollowUpKind followUp = kind ?? AgentFollowUpKind.Contextual;
            bool contextual = followUp == AgentFollowUpKind.Contextual;
            bool isContinuationOnly = followUp == AgentFollowUpKind.Resume;
            bool isQuestion = !isContinuationOnly && followUp == AgentFollowUpKind.Question;

            if (contextual)
            {
                builder.AppendLine("[CURRENT USER REQUEST — HIGHEST PRIORITY]");
                builder.AppendLine(userQuery);
                builder.AppendLine("The current request takes priority over the original task and any earlier plan.");
                builder.AppendLine("Interpret this message from its meaning and the conversation, not from matching particular words. Decide whether the user wants an answer, unfinished work resumed, a targeted change, or unrelated new work.");
                builder.AppendLine("If they want information or status, answer directly from the previous run record; do not restart work. If they want action, perform that action and verify the relevant result. A report that the result is broken calls for diagnosis and repair, not additional features. If they switch tasks, treat the previous task only as background and leave its files alone unless relevant.");
                builder.AppendLine("Keep completed work and verified dependencies. Do not repeat inspection just to reconstruct facts already recorded below. Explain uncertainty instead of claiming an unverified result works.");
                builder.AppendLine();
                builder.AppendLine("[EXISTING WORKSPACE CONTEXT]");
                builder.AppendLine("[CONTINUING ACTIVE TASK]");
                builder.Append("Original task (background, not an instruction to repeat): ").AppendLine(originalGoal);
            }
            else if (isQuestion)
            {
                // A question is answered, not worked on. The old wording told the model to
                // "inspect, modify, or verify" whatever the user sent, which turned "Is it done?"
                // into a second full verification run that never produced an answer.
                builder.AppendLine("[CURRENT USER REQUEST — HIGHEST PRIORITY]");
                builder.Append("The user is asking a QUESTION about the work: ").AppendLine(userQuery);
                builder.AppendLine("Answer it directly, in words, from the record of the previous run below. This turn is for answering, not for working:");
                builder.AppendLine("- Do NOT edit files, run builds or commands, start servers, or take screenshots.");
                builder.AppendLine("- If one fact you need is genuinely missing, make at most 2 quick read-only checks (read_file, list_directory, search_text, check_background_task). Otherwise call no tools at all.");
                builder.AppendLine("- Say plainly what is finished, what is not finished, and anything you could not confirm. Do NOT repeat an earlier summary word for word, and do not re-verify the project.");
                builder.AppendLine();
                builder.AppendLine("[EXISTING WORKSPACE CONTEXT]");
                builder.AppendLine("[CONTINUING ACTIVE TASK]");
                builder.Append("Original task: ").AppendLine(originalGoal);
            }
            else if (!isContinuationOnly)
            {
                builder.AppendLine("[CURRENT USER REQUEST — HIGHEST PRIORITY]");
                builder.Append("User follow-up / change request: ").AppendLine(userQuery);
                builder.AppendLine("This follow-up takes priority over the original task if the user asks to change, fix, replace, or rethink it.");
                builder.AppendLine("- Work only on what this request names: read just the file(s) involved, make the change, and check it once.");
                builder.AppendLine("- Do NOT rebuild, re-install, re-read every file, or re-verify the whole project.");
                builder.AppendLine("- Do NOT repeat your previous turn's summary. Your final summary covers only what you do in THIS turn.");
                builder.AppendLine();
                builder.AppendLine("[EXISTING WORKSPACE CONTEXT]");
                builder.AppendLine("[CONTINUING ACTIVE TASK]");
                builder.Append("Original task: ").AppendLine(originalGoal);
            }
            else
            {
                builder.AppendLine("[CONTINUING ACTIVE TASK]");
                builder.Append("Original task: ").AppendLine(originalGoal);
            }

            if (touchedFiles != null && touchedFiles.Count > 0)
            {
                // Full paths, not bare names: a follow-up that only knows "main.js" has to go
                // looking for the folder before it can do anything.
                builder.Append("Files touched so far: ");
                builder.AppendLine(string.Join(", ", touchedFiles.Where(file => !string.IsNullOrWhiteSpace(file)).Take(30)));
            }

            if (!string.IsNullOrWhiteSpace(previousRunDigest))
                builder.AppendLine(previousRunDigest.Trim());

            if (verifiedDependencies != null && verifiedDependencies.Count > 0)
            {
                builder.Append("Verified packages/tools already installed and working: ");
                builder.AppendLine(string.Join(", ", verifiedDependencies));
                builder.AppendLine("DO NOT re-install packages or re-download dependencies that are already verified above.");
            }

            if (!string.IsNullOrWhiteSpace(lastErrorOrStatus))
            {
                string trimmedStatus = lastErrorOrStatus.Trim();
                string compactStatus = string.Equals(trimmedStatus, "Stopped.", StringComparison.OrdinalIgnoreCase)
                    ? "The user stopped the previous run part-way to steer or correct it."
                    : trimmedStatus.Length <= 240 ? trimmedStatus : trimmedStatus[..240] + "…";
                builder.Append("Prior run status: ").AppendLine(compactStatus);
            }

            if (!string.IsNullOrWhiteSpace(backupFolder))
            {
                builder.Append("Backups of modified pre-existing files: ").AppendLine(backupFolder);
            }

            if (contextual)
            {
                builder.AppendLine("Respond to the CURRENT USER REQUEST according to its meaning. Answer when an answer is requested; act when action is requested. Keep your result concise and distinguish completed work from unverified claims.");
            }
            else if (isQuestion)
            {
                builder.AppendLine("Now answer the user's question directly. Start with the answer itself (for example \"Yes — …\" or \"Not yet — …\"), then the few facts that support it.");
            }
            else if (!isContinuationOnly)
            {
                builder.AppendLine("Now carry out the CURRENT USER REQUEST above with the smallest set of targeted reads and edits, then stop and summarise only this turn's changes.");
            }
            else
            {
                builder.AppendLine("Resume execution from your current progress and finish the task.");
            }

            return builder.ToString();
        }
    }

    /// <summary>One finished turn of a task, kept so the tracker and later turns can refer to it.</summary>
    public sealed record AgentTurnRecord(string Request, string Summary, int Steps, bool StoppedOnStepLimit);

    /// <summary>
    /// Tracks the persistent agent state across turns in a workplace chat.
    /// </summary>
    public sealed class AgentActiveTaskState
    {
        public string OriginalGoal { get; set; } = string.Empty;
        public string LastTurnGoal { get; set; } = string.Empty;
        public bool StoppedOnStepLimit { get; set; }
        public List<AgentExchange> AccumulatedExchanges { get; } = new();
        public HashSet<string> TouchedFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SessionAllowList { get; } = new();
        public HashSet<string> VerifiedDependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string LastStatusMessage { get; set; } = string.Empty;

        /// <summary>Each finished turn of this task: what was asked and how it ended.</summary>
        public List<AgentTurnRecord> Turns { get; } = new();

        /// <summary>The agent's checklist (update_plan) as the last run left it.</summary>
        public AgentPlan Plan { get; set; } = AgentPlan.Empty;

        /// <summary>Copies of pre-existing files the agent changed during this task.</summary>
        public AgentFileBackup? FileBackup { get; set; }
    }
}
