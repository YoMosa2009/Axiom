using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    public enum AgentPlanStatus
    {
        Pending,
        InProgress,
        Completed
    }

    public sealed record AgentPlanItem(string Step, AgentPlanStatus Status);

    /// <summary>
    /// The agent's live checklist, written by the model through <c>update_plan</c>.
    /// </summary>
    /// <remarks>
    /// Same shape the established harnesses settled on (Codex update_plan, OpenCode todowrite,
    /// DeepSeek Harness todo_write, Kimi SetTodoList; research 2026-11-25): every call sends the
    /// whole list and replaces the previous one; statuses are pending / in_progress / completed;
    /// no ids or priorities. Like DeepSeek Harness, the rules are checked in code (non-empty,
    /// no duplicates, at most one in progress), so a bad list is a fixable error instead of a
    /// silently wrong plan. The parser is lenient because the same tool serves cloud models
    /// (JSON array) and small local models (one checklist line per step).
    /// </remarks>
    public sealed class AgentPlan
    {
        public const int MaxItemsFull = 12;
        public const int MaxItemsCompact = 5;
        private const int MaxStepChars = 160;

        public IReadOnlyList<AgentPlanItem> Items { get; }
        public string? Explanation { get; }

        public AgentPlan(IReadOnlyList<AgentPlanItem> items, string? explanation = null)
        {
            Items = items ?? [];
            Explanation = string.IsNullOrWhiteSpace(explanation) ? null : explanation.Trim();
        }

        public static AgentPlan Empty { get; } = new([]);

        public bool IsEmpty => Items.Count == 0;
        public int CompletedCount => Items.Count(item => item.Status == AgentPlanStatus.Completed);
        public IEnumerable<AgentPlanItem> Open => Items.Where(item => item.Status != AgentPlanStatus.Completed);
        public bool HasOpenItems => Items.Any(item => item.Status != AgentPlanStatus.Completed);
        public AgentPlanItem? Current => Items.FirstOrDefault(item => item.Status == AgentPlanStatus.InProgress);

        private static readonly Regex CheckboxLineRegex = new(
            @"^\s*(?:[-*]\s*|\d+[.)]\s*)?\[(?<mark>[ xX✓✔>~\-*/.])?\]\s*(?<text>.+?)\s*$",
            RegexOptions.Compiled);

        private static readonly Regex StatusPrefixRegex = new(
            @"^\s*(?:[-*]\s*|\d+[.)]\s*)?(?<status>pending|todo|to\s*do|in[_\s-]?progress|doing|active|current|completed|complete|done|finished)\s*[:\-|]\s*(?<text>.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex StatusSuffixRegex = new(
            @"^\s*(?:[-*]\s*|\d+[.)]\s*)?(?<text>.+?)\s*[\(\[](?<status>pending|todo|in[_\s-]?progress|doing|active|completed|complete|done)[\)\]]\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Reads a plan from an update_plan call's text. Returns null with an error the model can
        /// act on when the list breaks a rule; never guesses a status it could not read.
        /// </summary>
        public static AgentPlan? TryParse(string? planText, string? explanation, int maxItems, out string? error)
        {
            error = null;
            string text = (planText ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                error = "update_plan needs the plan: one entry per step with its status (pending, in_progress or completed).";
                return null;
            }

            // "[x] done" and "[ ] todo" are checklist lines, not JSON: a JSON list starts [{ or [",
            // or is the empty list [] that clears the plan.
            List<AgentPlanItem>? items = text.StartsWith('{') || Regex.IsMatch(text, @"^\[\s*(?:[\{""]|\]\s*$)")
                ? TryParseJson(text, ref explanation, out error)
                : TryParseLines(text, out error);
            if (items == null)
                return null;

            if (items.Count == 0)
                return new AgentPlan([], explanation);

            if (items.Count > maxItems)
            {
                error = $"The plan has {items.Count} steps; keep it to {maxItems} or fewer by merging small steps.";
                return null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AgentPlanItem item in items)
            {
                if (!seen.Add(item.Step))
                {
                    error = $"The step \"{item.Step}\" appears twice. List each step once.";
                    return null;
                }
            }

            int inProgress = items.Count(item => item.Status == AgentPlanStatus.InProgress);
            if (inProgress > 1)
            {
                error = $"At most one step may be in_progress (got {inProgress}). Mark the others pending or completed.";
                return null;
            }

            return new AgentPlan(items, explanation);
        }

        private static List<AgentPlanItem>? TryParseJson(string text, ref string? explanation, out string? error)
        {
            error = null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (explanation == null && root.TryGetProperty("explanation", out JsonElement why) && why.ValueKind == JsonValueKind.String)
                        explanation = why.GetString();
                    if (!TryGetArray(root, out root))
                    {
                        error = "update_plan expects a list of steps, each with step and status.";
                        return null;
                    }
                }

                if (root.ValueKind != JsonValueKind.Array)
                {
                    error = "update_plan expects a list of steps, each with step and status.";
                    return null;
                }

                var items = new List<AgentPlanItem>();
                foreach (JsonElement entry in root.EnumerateArray())
                {
                    string step;
                    string statusText;
                    if (entry.ValueKind == JsonValueKind.String)
                    {
                        AgentPlanItem? fromLine = ParseLine(entry.GetString() ?? string.Empty, out error);
                        if (fromLine == null)
                            return null;
                        items.Add(fromLine);
                        continue;
                    }

                    if (entry.ValueKind != JsonValueKind.Object)
                    {
                        error = "Each plan entry needs a step and a status.";
                        return null;
                    }

                    step = ReadString(entry, "step", "content", "title", "task", "text", "description");
                    statusText = ReadString(entry, "status", "state");
                    if (string.IsNullOrWhiteSpace(step))
                    {
                        error = "A plan entry has no step text. Give every entry a short description.";
                        return null;
                    }

                    if (!TryReadStatus(statusText, out AgentPlanStatus status))
                    {
                        error = $"Unknown status \"{statusText}\" for \"{Clip(step)}\". Use pending, in_progress or completed.";
                        return null;
                    }

                    items.Add(new AgentPlanItem(Clip(step), status));
                }

                return items;
            }
            catch (JsonException)
            {
                error = "The plan was not valid JSON. Send a list like [{\"step\": \"...\", \"status\": \"in_progress\"}].";
                return null;
            }
        }

        private static bool TryGetArray(JsonElement root, out JsonElement array)
        {
            foreach (string name in new[] { "plan", "todos", "items", "steps" })
            {
                if (root.TryGetProperty(name, out array) && array.ValueKind == JsonValueKind.Array)
                    return true;
            }

            array = default;
            return false;
        }

        private static string ReadString(JsonElement entry, params string[] names)
        {
            foreach (string name in names)
            {
                foreach (JsonProperty property in entry.EnumerateObject())
                {
                    if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString() ?? string.Empty;
                }
            }

            return string.Empty;
        }

        private static List<AgentPlanItem>? TryParseLines(string text, out string? error)
        {
            error = null;
            // Small models put the whole list on one line separated by '|'.
            IEnumerable<string> lines = text.Contains('\n')
                ? text.Split('\n')
                : text.Split('|');
            var items = new List<AgentPlanItem>();
            foreach (string raw in lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                AgentPlanItem? item = ParseLine(raw, out error);
                if (item == null)
                    return null;
                items.Add(item);
            }

            return items;
        }

        private static AgentPlanItem? ParseLine(string line, out string? error)
        {
            error = null;
            Match checkbox = CheckboxLineRegex.Match(line);
            if (checkbox.Success)
            {
                string mark = checkbox.Groups["mark"].Value;
                AgentPlanStatus status = mark switch
                {
                    "x" or "X" or "✓" or "✔" => AgentPlanStatus.Completed,
                    ">" or "~" or "-" or "*" or "/" or "." => AgentPlanStatus.InProgress,
                    _ => AgentPlanStatus.Pending
                };
                return new AgentPlanItem(Clip(checkbox.Groups["text"].Value), status);
            }

            foreach (Regex regex in new[] { StatusPrefixRegex, StatusSuffixRegex })
            {
                Match match = regex.Match(line);
                if (match.Success && TryReadStatus(match.Groups["status"].Value, out AgentPlanStatus parsed))
                    return new AgentPlanItem(Clip(match.Groups["text"].Value), parsed);
            }

            error = $"Could not read a status for \"{Clip(line.Trim())}\". Write each step as [ ] pending, [>] in progress or [x] done.";
            return null;
        }

        internal static bool TryReadStatus(string? text, out AgentPlanStatus status)
        {
            string normalized = Regex.Replace((text ?? string.Empty).Trim().ToLowerInvariant(), @"[\s\-]+", "_");
            switch (normalized)
            {
                case "pending":
                case "todo":
                case "to_do":
                case "not_started":
                case "": // a JSON entry with no status is a step not started yet
                    status = AgentPlanStatus.Pending;
                    return true;
                case "in_progress":
                case "inprogress":
                case "doing":
                case "active":
                case "current":
                case "started":
                    status = AgentPlanStatus.InProgress;
                    return true;
                case "completed":
                case "complete":
                case "done":
                case "finished":
                    status = AgentPlanStatus.Completed;
                    return true;
                default:
                    status = AgentPlanStatus.Pending;
                    return false;
            }
        }

        private static string Clip(string text)
        {
            string flat = Regex.Replace(text.Trim(), @"\s+", " ");
            return flat.Length <= MaxStepChars ? flat : flat[..(MaxStepChars - 1)].TrimEnd() + "…";
        }

        /// <summary>The checklist as text: "[x] done", "[>] doing", "[ ] to do".</summary>
        public string Render()
        {
            if (IsEmpty)
                return "(the plan is empty)";
            var builder = new StringBuilder();
            foreach (AgentPlanItem item in Items)
            {
                string mark = item.Status switch
                {
                    AgentPlanStatus.Completed => "[x]",
                    AgentPlanStatus.InProgress => "[>]",
                    _ => "[ ]"
                };
                builder.Append(mark).Append(' ').AppendLine(item.Step);
            }

            return builder.ToString().TrimEnd();
        }

        /// <summary>
        /// What update_plan returns: counts and the current step, not the whole list echoed back
        /// (OpenCode and DeepSeek Harness do the same; an echo only spends small-model context).
        /// </summary>
        public string DescribeUpdate()
        {
            if (IsEmpty)
                return "Plan cleared.";
            int pending = Items.Count(item => item.Status == AgentPlanStatus.Pending);
            string text = $"Plan updated: {CompletedCount}/{Items.Count} completed, {(Current != null ? 1 : 0)} in progress, {pending} pending.";
            if (Current != null)
                text += $" Now: \"{Current.Step}\".";
            else if (!HasOpenItems)
                text += " Every step is completed.";
            return text;
        }

        /// <summary>A note added to an observation when the plan has gone stale.</summary>
        public string StaleReminder(int stepsSinceUpdate) =>
            $"\n[PLAN: {stepsSinceUpdate} steps since the plan was last updated. Current plan:\n{Render()}\n"
            + "If a step is done, call update_plan with the whole list and its new statuses; otherwise keep working.]";

        /// <summary>What the run is told when it tries to finish with steps still open.</summary>
        public string OpenItemsAtFinishMessage() =>
            "Not finished yet - your plan still has open steps:\n" + string.Join("\n", Open.Select(item =>
                (item.Status == AgentPlanStatus.InProgress ? "[>] " : "[ ] ") + item.Step))
            + "\nDo them now, or, if a step is no longer needed or already done, call update_plan marking it completed "
            + "and say in your summary why it was dropped. Then give your final summary.";

        /// <summary>The plan section of the .md task tracker.</summary>
        public string RenderMarkdown()
        {
            if (IsEmpty)
                return string.Empty;
            var builder = new StringBuilder();
            foreach (AgentPlanItem item in Items)
            {
                builder.Append(item.Status switch
                {
                    AgentPlanStatus.Completed => "- [x] ",
                    AgentPlanStatus.InProgress => "- [ ] **(in progress)** ",
                    _ => "- [ ] "
                }).AppendLine(item.Step);
            }

            return builder.ToString().TrimEnd();
        }
    }
}
