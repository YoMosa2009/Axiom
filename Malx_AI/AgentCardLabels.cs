using System;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI;

/// <summary>
/// Display text for agent cards and the Hybrid Local model. Dependency-free so the tests can
/// link it directly.
/// </summary>
internal static class AgentCardLabels
{
    /// <summary>Shown for Hybrid Local when no model id is configured yet.</summary>
    public const string HybridFallbackLabel = "Hybrid Local";

    /// <summary>Live run cards show at most this many recent steps; older ones fold into one line.</summary>
    public const int LiveVisibleSteps = 3;

    /// <summary>
    /// The name of the model the user configured for Hybrid Local, as they would recognise it:
    /// the last path segment of the id ("org/Model-7B" → "Model-7B"), with a ".gguf" suffix
    /// dropped. Falls back to "Hybrid Local" when nothing is configured.
    /// </summary>
    public static string HybridModelDisplayName(string? configuredModelId)
    {
        string id = (configuredModelId ?? string.Empty).Trim();
        if (id.Length == 0)
            return HybridFallbackLabel;

        string trimmed = id.TrimEnd('/', '\\');
        int slash = trimmed.LastIndexOfAny(new[] { '/', '\\' });
        string name = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
        if (name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase) && name.Length > 5)
            name = name[..^5];
        return name.Length == 0 ? HybridFallbackLabel : name;
    }

    /// <summary>
    /// Header for an agent card: the model's name, plus the run kind when it is not the default
    /// Agent Access run (for example "Model · Computer Use").
    /// </summary>
    public static string AgentCardHeader(string? modelName, string? runKind)
    {
        string model = string.IsNullOrWhiteSpace(modelName) ? "Agent" : modelName.Trim();
        string kind = (runKind ?? string.Empty).Trim();
        if (kind.Length == 0 || string.Equals(kind, "Agent Access", StringComparison.OrdinalIgnoreCase))
            return model;
        return $"{model} · {kind}";
    }

    /// <summary>
    /// The step lines a live run card shows: the latest <paramref name="visible"/> steps, with
    /// everything earlier folded into one "N earlier actions" line, so a long run does not pile
    /// up down the chat. The completed card still lists every step under "View activity".
    /// </summary>
    public static IReadOnlyList<string> CompactLiveSteps(IReadOnlyList<string>? steps, int visible = LiveVisibleSteps)
    {
        if (steps is null || steps.Count == 0)
            return Array.Empty<string>();
        visible = Math.Max(1, visible);
        // Folding a single step would save nothing, so up to visible + 1 steps all show.
        if (steps.Count <= visible + 1)
            return steps.ToArray();

        int hidden = steps.Count - visible;
        int failed = steps.Take(hidden).Count(s => s.StartsWith("✗", StringComparison.Ordinal));
        string folded = $"⋯ {hidden} earlier actions"
            + (failed > 0 ? $" ({failed} failed)" : string.Empty);
        var result = new List<string>(visible + 1) { folded };
        result.AddRange(steps.Skip(hidden));
        return result;
    }
}
