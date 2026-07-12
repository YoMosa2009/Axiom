using System;
using System.Collections.Generic;

namespace Malx_AI
{
    internal enum LineDiffKind
    {
        Unchanged,
        Added,
        Removed
    }

    internal readonly record struct LineDiffEntry(
        LineDiffKind Kind,
        string Text,
        int? OldLineNumber,
        int? NewLineNumber);

    internal static class LineDiff
    {
        // Myers' shortest-edit-script algorithm. It avoids the O(n*m) memory cost of an LCS
        // matrix, which matters for generated HTML and other large single-file artifacts.
        public static IReadOnlyList<LineDiffEntry> Build(string? original, string? revised)
        {
            string[] oldLines = SplitLines(original);
            string[] newLines = SplitLines(revised);
            int oldCount = oldLines.Length;
            int newCount = newLines.Length;
            int commonPrefix = 0;
            while (commonPrefix < oldCount
                && commonPrefix < newCount
                && string.Equals(oldLines[commonPrefix], newLines[commonPrefix], StringComparison.Ordinal))
            {
                commonPrefix++;
            }

            int commonSuffix = 0;
            while (commonSuffix < oldCount - commonPrefix
                && commonSuffix < newCount - commonPrefix
                && string.Equals(
                    oldLines[oldCount - commonSuffix - 1],
                    newLines[newCount - commonSuffix - 1],
                    StringComparison.Ordinal))
            {
                commonSuffix++;
            }

            int changedOldLines = oldCount - commonPrefix - commonSuffix;
            int changedNewLines = newCount - commonPrefix - commonSuffix;
            // Myers stores one frontier snapshot per edit-distance step. A wholesale rewrite of a
            // very large generated file can therefore consume excessive memory. For that case,
            // return a correct coarse replacement diff; small/localized edits still use Myers.
            if (changedOldLines + changedNewLines > 2500)
                return BuildCoarseReplacement(oldLines, newLines, commonPrefix, commonSuffix);

            int max = oldCount + newCount;
            int offset = max + 1;
            var frontier = new int[(max * 2) + 3];
            var trace = new List<int[]>(max + 1);
            frontier[offset + 1] = 0;

            for (int editDistance = 0; editDistance <= max; editDistance++)
            {
                trace.Add((int[])frontier.Clone());
                for (int diagonal = -editDistance; diagonal <= editDistance; diagonal += 2)
                {
                    int index = offset + diagonal;
                    int oldIndex;
                    if (diagonal == -editDistance
                        || (diagonal != editDistance && frontier[index - 1] < frontier[index + 1]))
                    {
                        oldIndex = frontier[index + 1];
                    }
                    else
                    {
                        oldIndex = frontier[index - 1] + 1;
                    }

                    int newIndex = oldIndex - diagonal;
                    while (oldIndex < oldCount
                        && newIndex < newCount
                        && string.Equals(oldLines[oldIndex], newLines[newIndex], StringComparison.Ordinal))
                    {
                        oldIndex++;
                        newIndex++;
                    }

                    frontier[index] = oldIndex;
                    if (oldIndex >= oldCount && newIndex >= newCount)
                        return Backtrack(trace, oldLines, newLines, editDistance, offset);
                }
            }

            return Array.Empty<LineDiffEntry>();
        }

        private static IReadOnlyList<LineDiffEntry> BuildCoarseReplacement(
            IReadOnlyList<string> oldLines,
            IReadOnlyList<string> newLines,
            int commonPrefix,
            int commonSuffix)
        {
            var result = new List<LineDiffEntry>(oldLines.Count + newLines.Count);
            for (int index = 0; index < commonPrefix; index++)
            {
                result.Add(new LineDiffEntry(LineDiffKind.Unchanged, oldLines[index], index + 1, index + 1));
            }

            for (int index = commonPrefix; index < oldLines.Count - commonSuffix; index++)
            {
                result.Add(new LineDiffEntry(LineDiffKind.Removed, oldLines[index], index + 1, null));
            }

            for (int index = commonPrefix; index < newLines.Count - commonSuffix; index++)
            {
                result.Add(new LineDiffEntry(LineDiffKind.Added, newLines[index], null, index + 1));
            }

            for (int offset = commonSuffix; offset > 0; offset--)
            {
                int oldIndex = oldLines.Count - offset;
                int newIndex = newLines.Count - offset;
                result.Add(new LineDiffEntry(
                    LineDiffKind.Unchanged,
                    oldLines[oldIndex],
                    oldIndex + 1,
                    newIndex + 1));
            }

            return result;
        }

        private static IReadOnlyList<LineDiffEntry> Backtrack(
            IReadOnlyList<int[]> trace,
            IReadOnlyList<string> oldLines,
            IReadOnlyList<string> newLines,
            int finalEditDistance,
            int offset)
        {
            int oldIndex = oldLines.Count;
            int newIndex = newLines.Count;
            var reversed = new List<LineDiffEntry>(oldIndex + newIndex);

            for (int editDistance = finalEditDistance; editDistance >= 0; editDistance--)
            {
                int[] frontier = trace[editDistance];
                int diagonal = oldIndex - newIndex;
                int previousDiagonal;
                if (diagonal == -editDistance
                    || (diagonal != editDistance
                        && frontier[offset + diagonal - 1] < frontier[offset + diagonal + 1]))
                {
                    previousDiagonal = diagonal + 1;
                }
                else
                {
                    previousDiagonal = diagonal - 1;
                }

                int previousOldIndex = frontier[offset + previousDiagonal];
                int previousNewIndex = previousOldIndex - previousDiagonal;

                while (oldIndex > previousOldIndex && newIndex > previousNewIndex)
                {
                    reversed.Add(new LineDiffEntry(
                        LineDiffKind.Unchanged,
                        oldLines[oldIndex - 1],
                        oldIndex,
                        newIndex));
                    oldIndex--;
                    newIndex--;
                }

                if (editDistance == 0)
                    break;

                if (oldIndex == previousOldIndex)
                {
                    reversed.Add(new LineDiffEntry(
                        LineDiffKind.Added,
                        newLines[newIndex - 1],
                        null,
                        newIndex));
                    newIndex--;
                }
                else
                {
                    reversed.Add(new LineDiffEntry(
                        LineDiffKind.Removed,
                        oldLines[oldIndex - 1],
                        oldIndex,
                        null));
                    oldIndex--;
                }
            }

            reversed.Reverse();
            return reversed;
        }

        private static string[] SplitLines(string? text)
        {
            string normalized = (text ?? string.Empty)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n');
            if (normalized.Length == 0)
                return Array.Empty<string>();

            return normalized.Split('\n');
        }
    }
}
