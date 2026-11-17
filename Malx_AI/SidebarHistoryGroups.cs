using System;

namespace Malx_AI
{
    /// <summary>The date group a chat sits under in the sidebar ("Today", "Previous 7 days", …).</summary>
    internal static class SidebarHistoryGroups
    {
        public static string LabelFor(DateTime updatedAt, DateTime now, bool pinned)
        {
            if (pinned)
                return "Pinned";

            DateTime today = now.Date;
            DateTime day = updatedAt.Date;
            if (day >= today)
                return "Today";
            if (day == today.AddDays(-1))
                return "Yesterday";
            if (day > today.AddDays(-7))
                return "Previous 7 days";
            if (day > today.AddDays(-30))
                return "Previous 30 days";
            return "Older";
        }

        /// <summary>Compact relative age indicator for the Workplace Projects tree ("now", "5m", "2d", "2mo").</summary>
        public static string FormatRelativeAge(DateTime updatedAt, DateTime now)
        {
            if (updatedAt == default)
                return "now";

            DateTime u = updatedAt.Kind == DateTimeKind.Utc ? updatedAt.ToLocalTime() : updatedAt;
            DateTime n = now.Kind == DateTimeKind.Utc ? now.ToLocalTime() : now;
            TimeSpan span = n - u;
            if (span.TotalSeconds < 60)
                return "now";
            if (span.TotalMinutes < 60)
                return $"{Math.Max(1, (int)span.TotalMinutes)}m";
            if (span.TotalHours < 24)
                return $"{Math.Max(1, (int)span.TotalHours)}h";
            if (span.TotalDays < 30)
                return $"{Math.Max(1, (int)span.TotalDays)}d";
            if (span.TotalDays < 365)
                return $"{Math.Max(1, (int)(span.TotalDays / 30.0))}mo";
            return $"{Math.Max(1, (int)(span.TotalDays / 365.0))}y";
        }
    }
}
