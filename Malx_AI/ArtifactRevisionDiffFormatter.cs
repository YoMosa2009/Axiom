using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Malx_AI
{
    internal static class ArtifactRevisionDiffFormatter
    {
        private const string TruncationMarker = "\n[...diff truncated to protect the Critic context budget...]";

        public static string Build(
            string previousArtifact,
            string currentArtifact,
            string previousCriticReview,
            bool baselineWasTruncated,
            int maxChars,
            int previousReviewCap)
        {
            maxChars = Math.Max(500, maxChars);
            previousReviewCap = Math.Clamp(previousReviewCap, 0, maxChars / 2);
            IReadOnlyList<LineDiffEntry> diff = LineDiff.Build(previousArtifact, currentArtifact);
            var changedIndices = diff
                .Select((entry, index) => (entry, index))
                .Where(item => item.entry.Kind != LineDiffKind.Unchanged)
                .Select(item => item.index)
                .ToList();

            int additions = diff.Count(entry => entry.Kind == LineDiffKind.Added);
            int removals = diff.Count(entry => entry.Kind == LineDiffKind.Removed);
            var block = new StringBuilder();
            block.AppendLine("Focus new findings on the changed regions below. Still verify that every previously reported issue was fixed, and inspect unchanged surrounding lines when needed to detect regressions.");
            block.AppendLine($"Change summary: +{additions} / -{removals} lines.");
            if (baselineWasTruncated)
            {
                block.AppendLine("Baseline note: the prior local artifact exceeded its context allowance, so this is a partial diff. Do not treat additions beyond the captured baseline as proof that those lines are new.");
            }

            if (!string.IsNullOrWhiteSpace(previousCriticReview) && previousReviewCap > 0)
            {
                string priorReview = previousCriticReview.Trim();
                if (priorReview.Length > previousReviewCap)
                    priorReview = priorReview[..previousReviewCap] + "\n[...previous review truncated...]";
                block.AppendLine();
                block.AppendLine("PREVIOUSLY REPORTED ISSUES TO RECHECK:");
                block.AppendLine(priorReview);
            }

            block.AppendLine();
            block.AppendLine("COMPACT LINE DIFF:");
            if (changedIndices.Count == 0)
            {
                block.AppendLine("No textual changes detected.");
                return Cap(block.ToString(), maxChars, includeMarker: false);
            }

            const int contextLines = 2;
            var visibleIndices = new SortedSet<int>();
            foreach (int changedIndex in changedIndices)
            {
                int start = Math.Max(0, changedIndex - contextLines);
                int end = Math.Min(diff.Count - 1, changedIndex + contextLines);
                for (int index = start; index <= end; index++)
                    visibleIndices.Add(index);
            }

            int previousVisibleIndex = -2;
            bool truncated = false;
            foreach (int index in visibleIndices)
            {
                if (index > previousVisibleIndex + 1)
                    block.AppendLine("...");

                LineDiffEntry entry = diff[index];
                string prefix = entry.Kind switch
                {
                    LineDiffKind.Removed => $"- old {entry.OldLineNumber,5}: ",
                    LineDiffKind.Added => $"+ new {entry.NewLineNumber,5}: ",
                    _ => $"  {entry.OldLineNumber,5}->{entry.NewLineNumber,5}: "
                };
                string text = entry.Text.Length > 400 ? entry.Text[..400] + "..." : entry.Text;
                string line = prefix + text;
                if (block.Length + line.Length + Environment.NewLine.Length + TruncationMarker.Length > maxChars)
                {
                    truncated = true;
                    break;
                }

                block.AppendLine(line);
                previousVisibleIndex = index;
            }

            return Cap(block.ToString(), maxChars, truncated);
        }

        private static string Cap(string value, int maxChars, bool includeMarker)
        {
            string marker = includeMarker ? TruncationMarker : string.Empty;
            if (value.Length + marker.Length <= maxChars)
                return value + marker;

            int contentLength = Math.Max(0, maxChars - marker.Length);
            return value[..Math.Min(value.Length, contentLength)] + marker;
        }
    }
}
