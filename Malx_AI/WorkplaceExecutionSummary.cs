using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI;

internal sealed record WorkplaceExecutionSummary(string Body, string Steps, int Count)
{
    private static readonly Regex Header = new(@"^###[ \t]+Steps Executed[ \t]+\((\d+)\)[ \t]*$", RegexOptions.Compiled);

    public static WorkplaceExecutionSummary Parse(string? content, bool allowActivity)
    {
        string original = content ?? string.Empty;
        if (!allowActivity) return new(original, string.Empty, 0);
        string[] lines = original.Replace("\r\n", "\n").Split('\n');
        int candidate = -1, expected = 0, fenceLength = 0;
        char fence = '\0';
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            int run = 0;
            if (trimmed.Length > 0 && trimmed[0] is '`' or '~')
                while (run < trimmed.Length && trimmed[run] == trimmed[0]) run++;
            if (run >= 3)
            {
                if (fenceLength == 0) { fence = trimmed[0]; fenceLength = run; }
                else if (trimmed[0] == fence && run >= fenceLength && string.IsNullOrWhiteSpace(trimmed[run..])) fenceLength = 0;
                continue;
            }
            if (fenceLength != 0) continue;
            var match = Header.Match(lines[i]);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int count) && count > 0)
            { candidate = i; expected = count; }
        }
        if (candidate < 0) return new(original, string.Empty, 0);

        int actual = 0;
        foreach (string line in lines.Skip(candidate + 1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("- ", StringComparison.Ordinal)) actual++;
            else if (actual == 0 || !char.IsWhiteSpace(line[0]))
                return new(original, string.Empty, 0); // Never hide a following answer paragraph.
        }
        if (actual != expected) return new(original, string.Empty, 0);
        return new(string.Join("\n", lines.Take(candidate)).TrimEnd(),
            string.Join("\n", lines.Skip(candidate + 1)).Trim(), actual);
    }
}
