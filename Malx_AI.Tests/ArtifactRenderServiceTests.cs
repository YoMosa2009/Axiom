using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class ArtifactRenderServiceTests
{
    [Fact]
    public void NormalChat_DetectsCompleteHtmlArtifact()
    {
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(
            "```html\n<!doctype html><html><body><h1>Canvas</h1></body></html>\n```");

        Assert.Equal(ArtifactKind.Html, artifact.Kind);
        Assert.True(artifact.SupportsPreview);
        Assert.Contains("<h1>Canvas</h1>", artifact.RenderSource, StringComparison.Ordinal);
    }

    [Fact]
    public void NormalChat_DetectsStandaloneSvgArtifact()
    {
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(
            "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 20 20\"><circle cx=\"10\" cy=\"10\" r=\"5\"/></svg>");

        Assert.Equal(ArtifactKind.Svg, artifact.Kind);
        Assert.True(artifact.SupportsPreview);
    }

    [Fact]
    public void NormalChat_DetectsMarkdownDocumentArtifact()
    {
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(
            "# Results\n\n| Name | Value |\n|---|---:|\n| A | 42 |");

        Assert.Equal(ArtifactKind.Document, artifact.Kind);
        Assert.True(artifact.SupportsPreview);
        Assert.Contains("<table>", artifact.RenderSource, StringComparison.OrdinalIgnoreCase);
    }
}
