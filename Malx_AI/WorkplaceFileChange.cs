using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Malx_AI;

/// <summary>An immutable, observed before/after pair, never inferred from model prose.</summary>
public sealed record WorkplaceFileChange
{
    public string Path { get; init; } = "";
    public string DisplayPath { get; init; } = "";
    public string Kind { get; init; } = "Edited";
    public string? BeforeText { get; init; }
    public string? AfterText { get; init; }
    public int? Additions { get; init; }
    public int? Removals { get; init; }
    public string? UnavailableReason { get; init; }
    public string AddedLabel => Additions is int count ? $"+{count}" : "";
    public string RemovedLabel => Removals is int count ? $"−{count}" : "";
    public string CountsDescription => Additions.HasValue ? $"{AddedLabel} {RemovedLabel}" : "Not counted";

    public static WorkplaceFileChange? FromText(string path, string before, string after,
        bool existedBefore = true, bool existsAfter = true, string? displayPath = null, bool canvas = false)
    {
        if (existedBefore == existsAfter && string.Equals(before, after, StringComparison.Ordinal)) return null;
        var diff = LineDiff.Build(before, after);
        return new WorkplaceFileChange
        {
            Path = path, DisplayPath = displayPath ?? path,
            Kind = canvas ? "Canvas" : !existsAfter ? "Deleted" : !existedBefore ? "Created" : "Edited",
            BeforeText = before, AfterText = after,
            Additions = diff.Count(line => line.Kind == LineDiffKind.Added),
            Removals = diff.Count(line => line.Kind == LineDiffKind.Removed)
        };
    }

    public static WorkplaceFileChange? FromFiles(string path, string? beforeCopy, bool existedBefore, string? projectRoot = null)
    {
        string display = path;
        try
        {
            if (!string.IsNullOrWhiteSpace(projectRoot))
            {
                string relative = System.IO.Path.GetRelativePath(projectRoot, path);
                if (!System.IO.Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + System.IO.Path.DirectorySeparatorChar))
                    display = relative.Replace('\\', '/');
            }
            bool existsAfter = File.Exists(path);
            if (!existedBefore && !existsAfter) return null;
            string before = existedBefore ? ReadText(beforeCopy) : "";
            string after = existsAfter ? ReadText(path) : "";
            return FromText(path, before, after, existedBefore, existsAfter, display);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new WorkplaceFileChange { Path = path, DisplayPath = display, UnavailableReason = ex.Message };
        }
    }

    private static string ReadText(string? path)
    {
        if (path == null) throw new IOException("No readable before-copy was recorded.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > 512 * 1024) throw new IOException("Text diff unavailable for files larger than 512 KB.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        if (text.Contains('\0')) throw new IOException("Binary file; line counts are unavailable.");
        return text;
    }

    public string BuildReviewText()
    {
        if (BeforeText == null || AfterText == null) return UnavailableReason ?? "No captured text diff is available.";
        var text = new StringBuilder();
        text.AppendLine("--- Before: " + DisplayPath).AppendLine("+++ After: " + DisplayPath);
        foreach (var line in LineDiff.Build(BeforeText, AfterText))
            text.Append(line.Kind == LineDiffKind.Added ? "+ " : line.Kind == LineDiffKind.Removed ? "− " : "  ").AppendLine(line.Text);
        if (Additions == 0 && Removals == 0) text.AppendLine($"{Kind} empty file.");
        return text.ToString();
    }

    public static IReadOnlyList<WorkplaceFileChange> Merge(IEnumerable<WorkplaceFileChange> changes)
    {
        var merged = new Dictionary<string, WorkplaceFileChange>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (merged.TryGetValue(change.Path, out var first) && first.BeforeText != null && change.AfterText != null)
            {
                var net = FromText(change.Path, first.BeforeText, change.AfterText,
                    first.Kind != "Created", change.Kind != "Deleted", first.DisplayPath, first.Kind == "Canvas");
                if (net == null) merged.Remove(change.Path); else merged[change.Path] = net;
            }
            else if (merged.TryGetValue(change.Path, out first))
                merged[change.Path] = change with { BeforeText = null, Additions = null, Removals = null,
                    Kind = first.Kind, UnavailableReason = first.UnavailableReason ?? "The initial before-state was not captured." };
            else merged[change.Path] = change;
        }
        return merged.Values.ToArray();
    }
}
