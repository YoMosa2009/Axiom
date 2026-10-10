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
    }
}
