using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    /// <summary>
    /// The quality steps Extra High and Ultra add to Agent Access, in place of the Critic.
    /// </summary>
    /// <remarks>
    /// The Critic read the agent's own report and appended an opinion; nothing was fixed and it
    /// could not see the files. Feedback grounded in the real result works better than a model
    /// judging prose (self-correction without external feedback rarely helps, Huang et al. 2023),
    /// so these gates send the run back with concrete work: check what you changed (Extra High),
    /// and fix what a fresh reading of the changed files found (Ultra).
    /// </remarks>
    public static class AgentQualityGates
    {
        /// <summary>Tools that look at the result: reading a file back, running it, opening it.</summary>
        public static bool IsCheckTool(string? tool) =>
            string.Equals(tool, AgentToolNames.ReadFile, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.SearchText, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.BrowserControl, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.TakeScreenshot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, AgentToolNames.CheckBackgroundTask, StringComparison.OrdinalIgnoreCase);

        public static string CheckBeforeFinishMessage(IReadOnlyCollection<string> changedFiles)
        {
            string files = changedFiles.Count == 0
                ? "the files you changed"
                : string.Join(", ", changedFiles.Take(6)) + (changedFiles.Count > 6 ? $" and {changedFiles.Count - 6} more" : string.Empty);
            return "Not finished yet - nothing has checked your last changes. Before the final summary, check the result: "
                + $"read back or run what you changed ({files}) - run the script or tests, open the page, or read the file - "
                + "and fix anything wrong. Then give the final summary and say how you checked it.";
        }

        public static string ReviewFindingsMessage(IReadOnlyList<string> issues)
            => "Not finished yet - a fresh review of the changed files against the request found:\n- "
                + string.Join("\n- ", issues.Take(MaxReviewIssues))
                + "\nFix the ones that are real problems (ignore any that are wrong, and say why), check the result, then give your final summary.";

        public static string ExplorationNudge(int stepsSoFar)
            => $"\n[PROGRESS CHECK: {stepsSoFar} steps so far and no file has been written yet. If the user asked you to build, "
                + "create or change something, start writing it now with what you already know (write_file), then test and refine it. "
                + "Stop probing with one-off commands. If the user only asked a question, answer it now.]";

        public const int MaxReviewIssues = 6;
        private const int ReviewFileBudgetChars = 24_000;

        /// <summary>
        /// System prompt for the Ultra fresh review. Short and strict: the reviewer reports only
        /// concrete defects against the request, or says there are none.
        /// </summary>
        public const string ReviewSystemPrompt =
            "You review finished work for a user. You get the user's request and the current contents of the files that were changed. "
            + "List only concrete defects that would stop the work from doing what the user asked: bugs, missing requested features, broken references, "
            + "wrong values, files that would not run. Do not list style preferences or optional extras. "
            + "Reply with one defect per line, each starting with \"- \" and naming the file. If there are no such defects, reply with exactly: NO ISSUES";

        /// <summary>The review request: the user's goal and each changed file, trimmed to a budget.</summary>
        public static string BuildReviewPayload(string goal, IReadOnlyList<(string Path, string Content)> files)
        {
            var builder = new StringBuilder();
            builder.AppendLine("USER REQUEST:");
            builder.AppendLine(goal.Length > 4000 ? goal[..4000] + "…" : goal);
            builder.AppendLine();
            int perFile = files.Count == 0 ? ReviewFileBudgetChars : Math.Max(2000, ReviewFileBudgetChars / files.Count);
            foreach ((string path, string content) in files)
            {
                builder.AppendLine($"FILE: {path}");
                builder.AppendLine(content.Length > perFile ? content[..perFile] + "\n… (truncated)" : content);
                builder.AppendLine();
            }
            builder.AppendLine("List the concrete defects, or reply NO ISSUES.");
            return builder.ToString();
        }

        private static readonly Regex IssueLine = new(@"^\s*(?:[-*•]|\d+[.)])\s+(?<text>\S.*)$", RegexOptions.Multiline | RegexOptions.Compiled);

        /// <summary>Reads the reviewer's reply. "NO ISSUES", an empty reply or no list means none.</summary>
        public static IReadOnlyList<string> ParseReviewIssues(string? reply)
        {
            string text = (reply ?? string.Empty).Trim();
            if (text.Length == 0 || text.StartsWith("NO ISSUES", StringComparison.OrdinalIgnoreCase))
                return [];
            return IssueLine.Matches(text)
                .Select(m => m.Groups["text"].Value.Trim())
                .Where(t => t.Length >= 8 && !t.StartsWith("NO ISSUES", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxReviewIssues)
                .ToList();
        }
    }
}
