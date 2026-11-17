using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Malx_AI.Tests
{
    public sealed class UserProfileStatsTests
    {
        [Fact]
        public void ProfileStats_TracksLifetimeTokensStreaksAndDisplayName()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"axiom_profile_{Guid.NewGuid():N}.json");
            try
            {
                var svc = new UserProfileStatsService(tempFile);
                UserProfileSummary initial = svc.GetSummary(new DateTime(2026, 11, 15));
                Assert.Equal("Your Name", initial.DisplayName);
                Assert.Equal(0, initial.LifetimeTokens);

                svc.SetDisplayName("Alex");
                svc.RecordTokens(1500, new DateTime(2026, 11, 10));
                svc.RecordTokens(1000, new DateTime(2026, 11, 11));
                svc.RecordTokens(1000, new DateTime(2026, 11, 12));
                svc.RecordTokens(2500, new DateTime(2026, 11, 14));
                svc.RecordTokens(3000, new DateTime(2026, 11, 15));

                // Reload from disk in fresh instance to verify persistence
                var reloaded = new UserProfileStatsService(tempFile);
                UserProfileSummary summary = reloaded.GetSummary(new DateTime(2026, 11, 15));
                Assert.Equal("Alex", summary.DisplayName);
                Assert.Equal(9000, summary.LifetimeTokens);
                Assert.Equal("9.0K", summary.FormattedLifetimeTokens);
                Assert.Equal(2, summary.CurrentStreakDays);
                Assert.Equal(3, summary.LongestStreakDays);
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }

        [Fact]
        public void BuildHeatmap_SupportsDailyWeeklyAndCumulativeModesOver52Weeks()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"axiom_heatmap_{Guid.NewGuid():N}.json");
            try
            {
                var today = new DateTime(2026, 11, 15);
                var svc = new UserProfileStatsService(tempFile);
                svc.RecordTokens(100, today.AddDays(-7));
                svc.RecordTokens(250, today);

                TokenHeatmapGrid daily = svc.BuildHeatmap(TokenActivityViewMode.Daily, today, weeks: 52);
                TokenHeatmapGrid weekly = svc.BuildHeatmap(TokenActivityViewMode.Weekly, today, weeks: 52);
                TokenHeatmapGrid cumulative = svc.BuildHeatmap(TokenActivityViewMode.Cumulative, today, weeks: 52);

                Assert.Equal(52 * 7, daily.Cells.Count);
                Assert.Equal(52, daily.MonthLabels.Count);
                Assert.Equal(250, daily.Cells.First(c => c.Date == today).Tokens);
                Assert.Equal(350, cumulative.Cells.First(c => c.Date == today).Tokens);
                Assert.True(weekly.Cells.Count(c => c.Tokens > 0) >= daily.Cells.Count(c => c.Tokens > 0));
            }
            finally
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
        }
    }
}
