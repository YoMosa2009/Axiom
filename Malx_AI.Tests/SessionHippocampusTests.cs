using Xunit;

namespace Malx_AI.Tests;

public sealed class SessionHippocampusTests
{
    [Fact]
    public void Write_DeduplicatesAndKeepsHigherPriority()
    {
        var store = new SessionHippocampus();
        store.Write(Entry("Alpha protocol handles retry timeout failures", 1));
        store.Write(Entry("Alpha protocol handles retry timeout failures", 3));

        SessionHippocampusEntry saved = Assert.Single(store.ExportEntries());
        Assert.Equal(3, saved.Priority);
    }

    [Fact]
    public void Consolidate_MergesHighlyOverlappingEntries()
    {
        var store = new SessionHippocampus();
        store.Write(Entry("alpha beta gamma delta epsilon zeta eta theta", 2, SessionHippocampusTag.Concept));
        store.Write(Entry("alpha beta gamma delta epsilon zeta eta iota", 2, SessionHippocampusTag.Summary));
        Assert.Equal(2, store.GetMetadata().TotalEntryCount);

        store.Consolidate();

        Assert.Single(store.ExportEntries());
    }

    [Fact]
    public void Query_RanksPriorityAndTopicalOverlap()
    {
        var store = new SessionHippocampus();
        store.Write(Entry("database migration retry transaction rollback", 3));
        store.Write(Entry("database migration changes table schema", 1));
        store.Write(Entry("watercolor palette and paper texture", 3));

        List<SessionHippocampusEntry> results = store.Query("database migration rollback", 2);

        Assert.Equal(2, results.Count);
        Assert.Contains("rollback", results[0].Content, StringComparison.OrdinalIgnoreCase);
        Assert.All(results, item => Assert.Contains("database", item.Content, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Capacity_EnforcesPerSourceAndGlobalLimits()
    {
        var store = new SessionHippocampus();
        for (int index = 0; index < 100; index++)
            store.Write(Entry($"unique architect memory number {index} token value{index}", 1));

        Assert.Equal(80, store.GetMetadata().TotalEntryCount);

        for (int index = 0; index < 100; index++)
            store.Write(Entry($"unique builder memory number {index} token build{index}", 1, source: SessionHippocampusSource.BuilderOutput));

        Assert.Equal(160, store.GetMetadata().TotalEntryCount);
    }

    private static SessionHippocampusEntry Entry(
        string content,
        int priority,
        SessionHippocampusTag tag = SessionHippocampusTag.SolutionPattern,
        SessionHippocampusSource source = SessionHippocampusSource.ArchitectOutput) => new()
        {
            Content = content,
            Priority = priority,
            Tag = tag,
            Source = source,
            Timestamp = DateTime.Now
        };
}
