using System;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class SidebarHistoryGroupsTests
    {
        private static readonly DateTime Now = new(2026, 10, 9, 15, 30, 0);

        [Theory]
        [InlineData(0, "Today")]
        [InlineData(1, "Yesterday")]
        [InlineData(2, "Previous 7 days")]
        [InlineData(6, "Previous 7 days")]
        [InlineData(7, "Previous 30 days")]
        [InlineData(29, "Previous 30 days")]
        [InlineData(30, "Older")]
        public void ChatsAreGroupedByCalendarDay(int daysAgo, string expected)
        {
            DateTime updated = Now.Date.AddDays(-daysAgo).AddHours(9);

            Assert.Equal(expected, SidebarHistoryGroups.LabelFor(updated, Now, pinned: false));
        }

        [Fact]
        public void PinnedChatsAreAlwaysInThePinnedGroup()
        {
            Assert.Equal("Pinned", SidebarHistoryGroups.LabelFor(Now.AddYears(-1), Now, pinned: true));
        }

        [Fact]
        public void ADateInTheFutureCountsAsToday()
        {
            Assert.Equal("Today", SidebarHistoryGroups.LabelFor(Now.AddDays(2), Now, pinned: false));
        }

        [Fact]
        public void EarlyThisMorningIsStillToday()
        {
            Assert.Equal("Today", SidebarHistoryGroups.LabelFor(Now.Date.AddMinutes(1), Now, pinned: false));
        }
    }
}
