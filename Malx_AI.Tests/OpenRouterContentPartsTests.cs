using System.Text.Json;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class OpenRouterContentPartsTests
{
    [Fact]
    public void PlainStringContent_IsVisibleTextButNotReasoning()
    {
        using JsonDocument document = JsonDocument.Parse("\"Hello from the model\"");

        Assert.Equal("Hello from the model", OpenRouterContentParts.ExtractText(document.RootElement, includeReasoningParts: false));
        Assert.Empty(OpenRouterContentParts.ExtractReasoningFromContent(document.RootElement));
    }

    [Fact]
    public void StructuredContent_SeparatesAnswerAndReasoningParts()
    {
        using JsonDocument document = JsonDocument.Parse(
            """
            [
              { "type": "text", "text": "Final answer" },
              { "type": "reasoning", "text": "Private analysis" }
            ]
            """);

        Assert.Equal("Final answer", OpenRouterContentParts.ExtractText(document.RootElement, includeReasoningParts: false));
        Assert.Equal("Private analysis", OpenRouterContentParts.ExtractReasoningFromContent(document.RootElement));
    }

    [Fact]
    public void UntypedNestedVisibleContent_IsNotMisclassifiedAsReasoning()
    {
        using JsonDocument document = JsonDocument.Parse("{ \"content\": { \"text\": \"Visible\" } }");

        Assert.Equal("Visible", OpenRouterContentParts.ExtractText(document.RootElement, includeReasoningParts: false));
        Assert.Empty(OpenRouterContentParts.ExtractReasoningFromContent(document.RootElement));
    }
}
