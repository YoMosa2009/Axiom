using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class CanvasChatReplyTests
{
    private const string PaperHtml =
        "<!DOCTYPE html><html><head><title>Large Language Models: A Technical Overview</title>" +
        "<style>.page{width:8.5in}</style></head><body><div class='page'><h1>LLMs</h1><p>Body</p></div></body></html>";

    [Fact]
    public void Reply_UsesTheModelsOwnLeadIn_AndDropsTheFencedArtifact()
    {
        string response = "Here's a print-ready overview of how large language models work, from tokenization to scaling laws.\n\n```html\n" + PaperHtml + "\n```";
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(response);

        string reply = ArtifactRenderService.BuildCanvasChatReply(response, artifact);

        Assert.Equal("Here's a print-ready overview of how large language models work, from tokenization to scaling laws.", reply);
        Assert.DoesNotContain("<html", reply, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Reply_DropsAnUnfencedHtmlDocument()
    {
        // The reported bug: an unfenced document was rendered inside the chat bubble itself.
        string response = "I put together the report you asked for, with a comparison table of attention variants.\n" + PaperHtml;
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(response);

        string reply = ArtifactRenderService.BuildCanvasChatReply(response, artifact);

        Assert.StartsWith("I put together the report", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_FallsBackToTheArtifactsRealTitle_WhenTheResponseIsOnlyTheArtifact()
    {
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(PaperHtml);

        string reply = ArtifactRenderService.BuildCanvasChatReply(PaperHtml, artifact);

        Assert.Contains("Large Language Models: A Technical Overview", reply, StringComparison.Ordinal);
        Assert.Contains("Project Canvas", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("<", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Reply_ForMarkdownDocument_KeepsOnlyTheLeadInBeforeTheFirstHeading()
    {
        string response = "Here's the quarterly summary you asked for.\n\n# Q3 Summary\n\n## Revenue\n\n| Month | Revenue |\n|---|---|\n| Jul | 10 |\n| Aug | 12 |\n| Sep | 14 |\n\n## Costs\n\nText.";
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(response);
        Assert.Equal(ArtifactKind.Document, artifact.Kind);

        string reply = ArtifactRenderService.BuildCanvasChatReply(response, artifact);

        Assert.Equal("Here's the quarterly summary you asked for.", reply);
    }

    [Fact]
    public void Reply_ForMarkdownDocumentWithoutLeadIn_UsesTheDocumentTitle()
    {
        string response = "# Solar Capacity Brief\n\n## Growth\n\nText.\n\n## Outlook\n\nMore text.";
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(response);

        string reply = ArtifactRenderService.BuildCanvasChatReply(response, artifact);

        Assert.Contains("Solar Capacity Brief", reply, StringComparison.Ordinal);
        Assert.DoesNotContain("## Growth", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void StreamingPreview_ShowsLeadInThenProgress_NeverRawSource()
    {
        string partial = "Here's your deck on renewable energy.\n\n```html\n<!DOCTYPE html><html><head><style>body{";

        string preview = ArtifactRenderService.BuildCanvasStreamingPreview(partial, "Building...");

        Assert.Equal("Here's your deck on renewable energy.\n\nBuilding...", preview);
    }

    [Fact]
    public void StreamingPreview_LeavesPlainProseUntouched()
    {
        Assert.Equal("Working on it", ArtifactRenderService.BuildCanvasStreamingPreview("Working on it", "Building..."));
        Assert.Equal("Building...", ArtifactRenderService.BuildCanvasStreamingPreview("<!DOCTYPE html><html>", "Building..."));
    }

    [Fact]
    public void RawHtmlDocumentReply_GetsAConversationalReply_FencedCodeDoesNot()
    {
        // A saved reply that IS a bare HTML document (the screenshot case) is shown as text.
        Assert.True(ArtifactRenderService.ContainsUnfencedHtmlDocument(PaperHtml));
        Assert.Contains("Large Language Models: A Technical Overview", ArtifactRenderService.BuildCanvasReplyForRawHtmlDocument(PaperHtml), StringComparison.Ordinal);

        // Code the user asked to see stays a code block.
        string fenced = "Here's the page:\n\n```html\n" + PaperHtml + "\n```";
        Assert.False(ArtifactRenderService.ContainsUnfencedHtmlDocument(fenced));
        Assert.Equal(string.Empty, ArtifactRenderService.BuildCanvasReplyForRawHtmlDocument(fenced));

        // A prose answer that merely mentions tags is untouched.
        Assert.False(ArtifactRenderService.ContainsUnfencedHtmlDocument("Wrap the page in an <html> element and add a <body>."));
    }

    [Fact]
    public void RenderedHtml_CarriesTheFitToWidthScript()
    {
        ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(PaperHtml);

        Assert.Contains("function fitWidth()", artifact.RenderSource, StringComparison.Ordinal);
        Assert.Contains("_canvas_normalize", artifact.RenderSource, StringComparison.Ordinal);
    }
}
