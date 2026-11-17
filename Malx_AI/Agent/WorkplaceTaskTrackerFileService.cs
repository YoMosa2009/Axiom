using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Creates, updates, and reads each Workplace session's persistent personal <c>.md</c>
    /// tracker file stored inside the user's chosen project directory (or profile fallback),
    /// ensuring all models across all parameter sizes retain task state, modified files, and
    /// progress even after context compaction.
    /// </summary>
    public static class WorkplaceTaskTrackerFileService
    {
        public const string DefaultProjectTrackerFileName = "AXIOM_TASK_TRACKER.md";

        public static string ResolveTrackerPath(string? projectFolder, int workplaceSessionId, string? fallbackRoot = null)
        {
            if (!string.IsNullOrWhiteSpace(projectFolder) && Directory.Exists(projectFolder))
                return Path.Combine(Path.GetFullPath(projectFolder), DefaultProjectTrackerFileName);

            string? envOverride = Environment.GetEnvironmentVariable("AXIOM_DATA_DIR");
            string baseDir = !string.IsNullOrWhiteSpace(fallbackRoot)
                ? fallbackRoot
                : !string.IsNullOrWhiteSpace(envOverride)
                    ? Path.Combine(envOverride.Trim(), "WorkplaceTrackers")
                    : Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Axiom-Dev",
                        "WorkplaceTrackers");

            return Path.Combine(baseDir, $"workplace_{Math.Max(1, workplaceSessionId)}_tracker.md");
        }

        public static void InitializeOrUpdateTracker(
            string trackerPath,
            string sessionTitle,
            string userGoal,
            IReadOnlyList<string>? completedSteps = null,
            IReadOnlyCollection<string>? touchedFiles = null,
            IReadOnlyList<AgentBackgroundTaskSnapshot>? backgroundTasks = null,
            string? latestTurnSummary = null,
            string? originalTask = null,
            IReadOnlyList<AgentTurnRecord>? earlierTurns = null)
        {
            if (string.IsNullOrWhiteSpace(trackerPath))
                return;

            string? dir = Path.GetDirectoryName(trackerPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            string cleanTitle = string.IsNullOrWhiteSpace(sessionTitle) ? "Workplace Session" : sessionTitle.Trim();
            sb.AppendLine($"# Task Tracker: {cleanTitle}");
            sb.AppendLine();
            sb.AppendLine($"- **Last Updated:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            if (!string.IsNullOrEmpty(dir))
                sb.AppendLine($"- **Workspace:** `{dir}`");
            sb.AppendLine();

            // A follow-up such as "Is it done?" used to replace the objective outright, so the
            // tracker forgot what the task was the moment the user asked about it.
            if (!string.IsNullOrWhiteSpace(originalTask)
                && !string.Equals(originalTask.Trim(), (userGoal ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                sb.AppendLine("## Task");
                sb.AppendLine(originalTask.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("## Current Objective");
            sb.AppendLine(string.IsNullOrWhiteSpace(userGoal) ? "_Pending user objective._" : userGoal.Trim());
            sb.AppendLine();

            if (earlierTurns != null && earlierTurns.Count > 0)
            {
                sb.AppendLine("## Earlier Turns");
                int number = Math.Max(0, earlierTurns.Count - 6);
                foreach (AgentTurnRecord turn in earlierTurns.TakeLast(6))
                {
                    number++;
                    string request = OneLine(turn.Request, 160);
                    string summary = (turn.Summary ?? string.Empty).Trim();
                    if (summary.Length > 700)
                        summary = summary[..700] + "…";
                    sb.AppendLine($"### Turn {number}: {request}");
                    sb.AppendLine($"_{turn.Steps} step(s){(turn.StoppedOnStepLimit ? ", stopped at the step limit" : "")}_");
                    sb.AppendLine();
                    sb.AppendLine(summary.Length == 0 ? "_No summary recorded._" : summary);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("## Progress & Steps");
            if (completedSteps != null && completedSteps.Count > 0)
            {
                foreach (string step in completedSteps.TakeLast(40))
                {
                    string clean = (step ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
                    if (clean.Length > 0)
                        sb.AppendLine($"- [x] {clean}");
                }
            }
            else
            {
                sb.AppendLine("- [ ] Initialized task tracker for this session.");
            }
            sb.AppendLine();

            sb.AppendLine("## Relevant & Modified Files");
            if (touchedFiles != null && touchedFiles.Count > 0)
            {
                foreach (string file in touchedFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"- `{file}`");
            }
            else
            {
                sb.AppendLine("- _None recorded yet._");
            }
            sb.AppendLine();

            if (backgroundTasks != null && backgroundTasks.Count > 0)
            {
                sb.AppendLine("## Background Tasks");
                foreach (AgentBackgroundTaskSnapshot task in backgroundTasks.Take(12))
                {
                    string status = task.IsRunning
                        ? $"running ({(int)Math.Round(task.Elapsed.TotalSeconds)}s, wake={task.WakeAfterSeconds}s)"
                        : task.WasStoppedByUser
                            ? "stopped"
                            : $"finished (exit={task.ExitCode?.ToString() ?? "0"})";
                    sb.AppendLine($"- `{task.Id}` **{task.Title}** — {status}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("## Latest Turn Summary & Memory");
            sb.AppendLine(string.IsNullOrWhiteSpace(latestTurnSummary)
                ? "_Run in progress or awaiting model completion._"
                : latestTurnSummary.Trim());

            AtomicFileWriter.WriteAllText(trackerPath, sb.ToString());
        }

        private static string OneLine(string? text, int max)
        {
            string flat = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
            return flat.Length <= max ? flat : flat[..max] + "…";
        }

        public static string ReadTrackerForCompactionContext(string? trackerPath, int maxChars = 4000)
        {
            if (string.IsNullOrWhiteSpace(trackerPath) || !File.Exists(trackerPath))
                return string.Empty;

            try
            {
                string content = File.ReadAllText(trackerPath).Trim();
                if (content.Length == 0)
                    return string.Empty;

                if (content.Length > maxChars)
                    content = content[..maxChars] + "\n… [tracker truncated]";

                return $"[PERSISTENT TASK TRACKER (.md): {trackerPath}]\n{content}\n[/PERSISTENT TASK TRACKER]";
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
