using System.Text.Json;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests;

public sealed class WorkplaceFileChangeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "axiom-change-summary-" + Guid.NewGuid().ToString("N"));
    public WorkplaceFileChangeTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);
    private string FilePath(string name) => Path.Combine(_root, name);

    [Fact]
    public void JournalUsesActualBeforeCopyEvenWithoutAReadToolCall()
    {
        string path = FilePath("app.cs");
        File.WriteAllText(path, "old\nkept\n");
        var journal = new AgentRunJournal(FilePath("journal"));
        journal.BeforeChange(path);
        File.WriteAllText(path, "new\nkept\nextra\n");
        journal.Seal();
        var change = Assert.Single(journal.CaptureFileChanges(_root));
        Assert.Equal("app.cs", change.DisplayPath);
        Assert.Equal(2, change.Additions); Assert.Equal(1, change.Removals);
        File.WriteAllText(path, "later user edits");
        Assert.Contains("+ extra", change.BuildReviewText());
        Assert.DoesNotContain("later user edits", change.BuildReviewText());
    }

    [Fact]
    public void UnchangedOrFailedWriteIsNotReportedAsAnEdit()
    {
        string path = FilePath("same.txt"); File.WriteAllText(path, "same");
        var journal = new AgentRunJournal(FilePath("journal")); journal.BeforeChange(path);
        journal.BeforeChange(FilePath("never-created.txt")); journal.Seal();
        Assert.Empty(journal.CaptureFileChanges());
    }

    [Fact]
    public void CreatedAndDeletedEmptyFilesStillHaveARealChange()
    {
        Assert.Equal("Created", WorkplaceFileChange.FromText("new", "", "", false, true)!.Kind);
        Assert.Equal("Deleted", WorkplaceFileChange.FromText("old", "", "", true, false)!.Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnsupportedFilesHaveUnknownCountsInsteadOfFabricatedZero(bool oversized)
    {
        string path = FilePath("data");
        File.WriteAllBytes(path, oversized ? new byte[512 * 1024 + 1] : new byte[] { 1, 0, 2 });
        var change = WorkplaceFileChange.FromFiles(path, null, false)!;
        Assert.Null(change.Additions); Assert.Null(change.Removals);
        Assert.Null(change.AfterText); Assert.NotNull(change.UnavailableReason);
    }

    [Fact]
    public void MissingBeforeCopyIsNotCountedAsANewFile()
    {
        string path = FilePath("existing"); File.WriteAllText(path, "changed");
        var change = WorkplaceFileChange.FromFiles(path, null, true)!;
        Assert.Null(change.Additions); Assert.NotNull(change.UnavailableReason);
    }

    [Fact]
    public void RepeatedPatchesShowNetChangesOncePerFile()
    {
        var first = WorkplaceFileChange.FromText("file", "old", "middle")!;
        var second = WorkplaceFileChange.FromText("file", "middle", "final\nextra")!;
        var change = Assert.Single(WorkplaceFileChange.Merge(new[] { first, second }));
        Assert.Equal("old", change.BeforeText); Assert.Equal("final\nextra", change.AfterText);
        Assert.Equal(2, change.Additions); Assert.Equal(1, change.Removals);
    }

    [Fact]
    public void ARunThatRevertsItsOwnChangesHasNoNetEdit()
    {
        Assert.Empty(WorkplaceFileChange.Merge(new[] {
            WorkplaceFileChange.FromText("file", "old", "middle")!,
            WorkplaceFileChange.FromText("file", "middle", "old")! }));
    }

    [Fact]
    public void LaterPatchCannotInventTheMissingInitialBaseline()
    {
        var unknown = new WorkplaceFileChange { Path = "file", UnavailableReason = "Missing original" };
        var later = WorkplaceFileChange.FromText("file", "intermediate", "final")!;
        var merged = Assert.Single(WorkplaceFileChange.Merge(new[] { unknown, later }));
        Assert.Null(merged.Additions); Assert.Null(merged.BeforeText);
        Assert.Equal("Missing original", merged.BuildReviewText());
    }

    [Fact]
    public void CapturedDiffSurvivesSerializationWithoutReReadingDisk()
    {
        var original = WorkplaceFileChange.FromText("now-missing.txt", "before", "after")!;
        var restored = JsonSerializer.Deserialize<WorkplaceFileChange>(JsonSerializer.Serialize(original))!;
        Assert.Equal(original, restored);
        Assert.Contains("− before", restored.BuildReviewText());
        Assert.Contains("+ after", restored.BuildReviewText());
    }

    [Fact]
    public void CanvasChangeIsExplicitlyAnArtifact()
    {
        var change = WorkplaceFileChange.FromText("axiom:canvas", "before", "after", canvas: true)!;
        Assert.Equal("Canvas", change.Kind);
    }
}
