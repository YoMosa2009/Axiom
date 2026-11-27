using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Malx_AI.Agent;

namespace Malx_AI;

/// <summary>
/// How an agent run card writes its plan and tool activity, so the chat renderer can draw them
/// as their own quiet blocks (WorkplaceMarkdownDocumentFactory) instead of bold bullet lists.
/// </summary>
/// <remarks>
/// Owner screenshot (2026-11-26): plan items and step rows ran together as one bold bullet list,
/// and struck-through plan items rendered as stray "~" with italics. Codex and OpenCode show the
/// plan as a compact checklist and tool calls as muted one-liners ("Edited hero.tsx +16 -6").
/// Both are fenced blocks with a private info string, so any other Markdown view still shows
/// readable plain lines.
/// </remarks>
internal static class AgentCardMarkup
{
    public const string ActivityFence = "axiom-activity";
    public const string PlanFence = "axiom-plan";

    /// <summary>The steps as one muted activity block. Empty when there are none.</summary>
    public static string Activity(IEnumerable<string> lines)
    {
        List<string> rows = (lines ?? [])
            .Select(l => (l ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Replace("```", "'''").Trim())
            .Where(l => l.Length > 0)
            .ToList();
        return rows.Count == 0 ? string.Empty : "```" + ActivityFence + "\n" + string.Join("\n", rows) + "\n```";
    }

    /// <summary>The plan as a compact checklist block. Empty for an empty plan.</summary>
    public static string Plan(AgentPlan plan)
    {
        if (plan == null || plan.IsEmpty)
            return string.Empty;
        var builder = new StringBuilder();
        builder.Append("```").Append(PlanFence).Append('\n');
        builder.Append($"Plan · {plan.CompletedCount} of {plan.Items.Count} done\n");
        foreach (AgentPlanItem item in plan.Items)
        {
            string step = (item.Step ?? string.Empty).Replace("\n", " ").Replace("```", "'''").Trim();
            builder.Append(item.Status switch
            {
                AgentPlanStatus.Completed => "✓ ",
                AgentPlanStatus.InProgress => "▸ ",
                _ => "○ "
            }).Append(step).Append('\n');
        }
        builder.Append("```");
        return builder.ToString();
    }

    /// <summary>
    /// "+N -M" line counts for a file change, from the tool arguments: write_file and
    /// append_file add their content's lines; edit_file swaps old_string for new_string.
    /// Empty for other tools or when nothing is known.
    /// </summary>
    public static string LineCounts(AgentToolCall call)
    {
        if (call == null)
            return string.Empty;
        static int Lines(string text) => string.IsNullOrEmpty(text) ? 0 : text.TrimEnd('\n', '\r').Split('\n').Length;
        switch (call.Tool.ToLowerInvariant())
        {
            case AgentToolNames.WriteFile:
            case AgentToolNames.AppendFile:
                int added = Lines(call.Arg("content"));
                return added > 0 ? $"+{added} -0" : string.Empty;
            case AgentToolNames.EditFile:
                int plus = Lines(call.Arg("new_string"));
                int minus = Lines(call.Arg("old_string"));
                return plus + minus > 0 ? $"+{plus} -{minus}" : string.Empty;
            default:
                return string.Empty;
        }
    }

    /// <summary>The collapsed activity line on a finished card ("Worked for 9 steps").</summary>
    public static string WorkedForLabel(int steps)
        => steps == 1 ? "Worked for 1 step · View activity" : $"Worked for {steps} steps · View activity";
}
