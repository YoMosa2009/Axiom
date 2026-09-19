using Xunit;

namespace Malx_AI.Tests;

public sealed class DocumentRetrieverTests
{
    [Fact]
    public void RetrieveRelevantChunks_PrioritizesRareExactIdentifier()
    {
        var retriever = new DocumentRetriever();
        retriever.AddChunks(
        [
            Chunk("notes.md", 0, "The deployment uses the standard retry configuration and common defaults."),
            Chunk("release.md", 0, "Rollout TX-9C12 requires the database migration before enabling the feature flag."),
            Chunk("other.md", 0, "Feature flags are evaluated during application startup.")
        ]);

        List<DocumentChunk> results = retriever.RetrieveRelevantChunks("What does TX-9C12 require?", 2, allowFallback: false);

        Assert.NotEmpty(results);
        DocumentChunk first = results[0];
        Assert.Equal("release.md", first.FileName);
        Assert.Contains("TX-9C12", first.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RemoveChunksForFile_RemovesOnlyTheRequestedKnowledgeSource()
    {
        var retriever = new DocumentRetriever();
        retriever.AddChunks(
        [
            Chunk("project-a/readme.md", 0, "Alpha deployment token resolves the startup issue."),
            Chunk("project-b/readme.md", 0, "Beta deployment token resolves the network issue.")
        ]);

        retriever.RemoveChunksForFile("project-a/readme.md");

        Assert.Equal(string.Empty, retriever.GetAllTextForFile("project-a/readme.md"));
        DocumentChunk remaining = Assert.Single(retriever.RetrieveRelevantChunks("beta deployment token", 3, allowFallback: false));
        Assert.Equal("project-b/readme.md", remaining.FileName);
    }

    private static DocumentChunk Chunk(string fileName, int chunkId, string content) => new()
    {
        FileName = fileName,
        ChunkId = chunkId,
        Content = content,
        TokenCount = Math.Max(1, content.Length / 4)
    };
}
