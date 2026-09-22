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

        private static string CompactObservation(AgentToolCall call, string observation, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(observation))
                return "(no output)";

            string tool = call.Tool.ToLowerInvariant();
            string path = call.Arg("path");

            if (observation.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase) || observation.Contains("Exception", StringComparison.OrdinalIgnoreCase))
            {
                string firstLine = observation.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? observation;
                return firstLine.Length <= maxChars ? firstLine : firstLine[..maxChars] + "…";
            }

            return tool switch
            {
                AgentToolNames.ReadFile => $"[Read {path}]",
                AgentToolNames.WriteFile => $"[Wrote {path}]",
                AgentToolNames.EditFile => $"[Edited {path}]",
                AgentToolNames.ListDirectory => $"[Listed {path}]",
                AgentToolNames.FindFiles => $"[Found files matching '{call.Arg("pattern")}']",
                AgentToolNames.SearchText => $"[Searched '{call.Arg("pattern")}']",
                AgentToolNames.RunCommand => observation.Length <= maxChars ? observation : $"[Ran {call.Arg("command")}: ok]",
                _ => observation.Length <= maxChars ? observation : observation[..maxChars] + "…"
            };
        }

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
            IReadOnlyCollection<string>? verifiedDependencies = null)
        {
            var builder = new StringBuilder();
            builder.AppendLine("[CONTINUING ACTIVE TASK]");
            builder.Append("Original task: ").AppendLine(originalGoal);

            if (touchedFiles != null && touchedFiles.Count > 0)
            {
                builder.Append("Files touched so far: ");
                builder.AppendLine(string.Join(", ", touchedFiles.Select(Path.GetFileName)));
            }

            if (verifiedDependencies != null && verifiedDependencies.Count > 0)
            {
                builder.Append("Verified packages/tools already installed and working: ");
                builder.AppendLine(string.Join(", ", verifiedDependencies));
            }

            if (!string.IsNullOrWhiteSpace(lastErrorOrStatus))
            {
                builder.Append("Current state: ").AppendLine(lastErrorOrStatus);
            }

            if (!IsContinuationPhrase(userQuery))
            {
                builder.Append("User follow-up / change request: ").AppendLine(userQuery);
                builder.AppendLine("IMPORTANT: Dependencies and project setup are already completed. Work directly on the existing project files using read_file, edit_file, or write_file. DO NOT re-install packages or re-download dependencies.");
            }

            builder.AppendLine("Resume execution from your current progress and finish the task.");
            return builder.ToString();
        }
    }

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
    }
}
