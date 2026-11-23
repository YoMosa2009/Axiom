using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI;

/// <summary>
/// One wording for tool activity across Workplace modes. Agent Access and @ComputerUse already
/// list their steps in the run card and fold them under "N steps · View activity"; Council and
/// Single Model use these helpers so their cards (and the tool banner) read the same way.
/// </summary>
internal static class WorkplaceRunSteps
{
    public const string ToolStartPrefix = "Using a tool";
    public const string ToolFailedPrefix = "That tool failed";
    public const string ResumePrefix = "Back to writing";

    private static readonly Regex StartPattern = new(
        @"^" + Regex.Escape(ToolStartPrefix) + @" (\d+) of (\d+) · (.+)$", RegexOptions.Compiled);
    private static readonly Regex BudgetPattern = new(
        @"(?:tool (\d+) of (\d+))|(?:(\d+) of (\d+) tools used)|(?:Using a tool (\d+) of (\d+))", RegexOptions.Compiled);

    public static string ToolStarted(string label, int used, int max)
        => $"{ToolStartPrefix} {Math.Max(0, used)} of {Math.Max(1, max)} · {Clean(label)}";

    public static string Resumed(int used, int max)
        => $"{ResumePrefix} · {Math.Max(0, used)} of {Math.Max(1, max)} tools used";

    public static string ToolFailed(int used, int max)
        => $"{ToolFailedPrefix} · {ResumePrefix.ToLowerInvariant()} · {Math.Max(0, used)} of {Math.Max(1, max)} tools used";

    /// <summary>The label of a <see cref="ToolStarted"/> message, or null for any other status.</summary>
    public static string? TryGetStartedLabel(string? status)
    {
        var match = StartPattern.Match(status ?? string.Empty);
        return match.Success ? match.Groups[3].Value.Trim() : null;
    }

    public static bool TryParseBudget(string? status, out int used, out int max)
    {
        used = max = 0;
        var match = BudgetPattern.Match(status ?? string.Empty);
        if (!match.Success) return false;
        for (int g = 1; g < match.Groups.Count; g += 2)
        {
            if (match.Groups[g].Success
                && int.TryParse(match.Groups[g].Value, out used)
                && int.TryParse(match.Groups[g + 1].Value, out max))
                return true;
        }
        return false;
    }

    /// <summary>Whether a cloud tool observation reports that the tool did not do its job.</summary>
    public static bool LooksLikeFailure(string? observation)
    {
        string text = (observation ?? string.Empty).TrimStart();
        return text.Length == 0
            || text.StartsWith("error", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("tool error", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("tool call was empty", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("unknown tool", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("failed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("web search failed", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("web search is disabled", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Same mark-plus-text shape as Agent Access step lines.</summary>
    public static string StepLine(string label, bool succeeded = true)
        => (succeeded ? "✓ " : "✗ ") + Clean(label);

    /// <summary>Marks the latest step as failed (used when a tool's failure is reported after it started).</summary>
    public static void MarkLastFailed(IList<string> steps)
    {
        if (steps.Count == 0) return;
        string last = steps[^1];
        if (last.StartsWith("✓ ", StringComparison.Ordinal))
            steps[^1] = "✗ " + last[2..];
    }

    /// <summary>
    /// Appends the "### Steps Executed (N)" block that <see cref="WorkplaceExecutionSummary"/> folds
    /// into the card's activity footer. Content that already has one is left alone.
    /// </summary>
    public static string AppendStepsFooter(string? content, IReadOnlyCollection<string>? steps)
    {
        string body = (content ?? string.Empty).TrimEnd();
        if (steps is null || steps.Count == 0 || body.Length == 0
            || body.Contains("### Steps Executed (", StringComparison.Ordinal))
            return body;
        return body + $"\n\n### Steps Executed ({steps.Count})\n" + string.Join("\n", steps.Select(s => "- " + Clean(s)));
    }

    private static string Clean(string? text)
    {
        string single = Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " ");
        if (single.EndsWith("...", StringComparison.Ordinal)) single = single[..^3].TrimEnd();
        if (single.EndsWith("…", StringComparison.Ordinal)) single = single[..^1].TrimEnd();
        return single.Length <= 110 ? single : single[..109].TrimEnd() + "…";
    }
}
