using Xunit;

namespace Malx_AI.Tests;

public sealed class LineDiffTests
{
    [Fact]
    public void Build_TracksAddedRemovedAndUnchangedLines()
    {
        IReadOnlyList<LineDiffEntry> diff = LineDiff.Build("alpha\nbeta\ngamma", "alpha\nBETA\ngamma\ndelta");

        Assert.Contains(diff, entry => entry.Kind == LineDiffKind.Removed && entry.Text == "beta" && entry.OldLineNumber == 2);
        Assert.Contains(diff, entry => entry.Kind == LineDiffKind.Added && entry.Text == "BETA" && entry.NewLineNumber == 2);
        Assert.Contains(diff, entry => entry.Kind == LineDiffKind.Added && entry.Text == "delta" && entry.NewLineNumber == 4);
        Assert.Equal(2, diff.Count(entry => entry.Kind == LineDiffKind.Unchanged));
    }

    [Fact]
    public void Build_HandlesEmptyInputs()
    {
        Assert.Empty(LineDiff.Build(null, null));
        Assert.Single(LineDiff.Build(null, "added"), entry => entry.Kind == LineDiffKind.Added);
    }
}
