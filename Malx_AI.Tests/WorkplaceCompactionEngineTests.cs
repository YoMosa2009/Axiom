using System.Collections.Generic;
using Xunit;

namespace Malx_AI.Tests;

public sealed class WorkplaceCompactionEngineTests
{
    [Theory]
    [InlineData(20, 6, 14)]   // plenty of old content outside the recent window
    [InlineData(4, 6, 0)]     // fewer messages than the recent-window floor -- skip entirely
    [InlineData(7, 6, 0)]     // Count - recentToKeep == 1 -- below the "worth compacting" floor
    [InlineData(8, 6, 2)]     // exactly at the floor -- still worth compacting
    public void ComputeMessagesToCompress_NeverEncroachesOnProtectedRecentWindow(
        int totalCount, int recentMessagesToKeep, int expected)
    {
        Assert.Equal(expected, WorkplaceCompactionEngine.ComputeMessagesToCompress(totalCount, recentMessagesToKeep));
    }

    [Fact]
    public void BuildFallbackSummary_TruncatesOrdinaryMessagesTo120Chars()
    {
        string longContent = new string('a', 200);
        var messages = new List<(string Role, string Content)> { ("user", longContent) };

        string result = WorkplaceCompactionEngine.BuildFallbackSummary(messages);

        Assert.Contains(new string('a', 120) + "...", result);
        Assert.DoesNotContain(new string('a', 121), result);
    }

    [Fact]
    public void BuildFallbackSummary_GivesAPriorContextSummaryALargerBudgetToAvoidCompoundingDecay()
    {
        string priorSummary = "[Context Summary] " + new string('b', 500);
        var messages = new List<(string Role, string Content)> { ("system", priorSummary) };

        string result = WorkplaceCompactionEngine.BuildFallbackSummary(messages);

        // The full 500-char body (plus the "[Context Summary] " prefix) fits under the 1000-char
        // budget, so it should NOT be truncated down to the ordinary 120-char limit.
        Assert.Contains(new string('b', 500), result);
    }
}
