using System;
using System.Collections.Generic;
using System.Text;

namespace Malx_AI
{
    // Pure logic extracted from WorkplaceView.xaml.cs's CompressChatHistoryIfNeededAsync so it can
    // be unit-tested directly (that file is a WPF-coupled partial class too large and dependency-
    // heavy to link into the test project as-is).
    public static class WorkplaceCompactionEngine
    {
        // Returns 0 when there isn't enough old content outside the protected recent window to be
        // worth compacting -- the caller should skip entirely rather than force a small compaction
        // that encroaches on the protected recent messages.
        public static int ComputeMessagesToCompress(int totalCount, int recentMessagesToKeep)
        {
            int messagesToCompress = Math.Max(0, totalCount - recentMessagesToKeep);
            return messagesToCompress < 2 ? 0 : messagesToCompress;
        }

        public static string BuildFallbackSummary(List<(string Role, string Content)> messages)
        {
            var sb = new StringBuilder();
            foreach (var msg in messages)
            {
                // A prior "[Context Summary]" entry re-entering compaction on a later cycle would
                // otherwise get re-truncated to the same 120 chars as ordinary chatter each time,
                // compounding into a rapidly-decaying stub. Give it more room instead.
                int budget = msg.Content.StartsWith("[Context Summary]", StringComparison.Ordinal) ? 1000 : 120;
                string truncated = msg.Content.Length > budget ? msg.Content[..budget] + "..." : msg.Content;
                sb.AppendLine($"{msg.Role}: {truncated}");
            }
            return sb.ToString();
        }
    }
}
