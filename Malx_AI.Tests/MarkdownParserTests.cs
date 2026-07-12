using Xunit;

namespace Malx_AI.Tests;

public sealed class MarkdownParserTests
{
    [Fact]
    public void Parse_RecognizesCoreFormattingAndCodeBlocks()
    {
        MarkdownParser.ParsedMarkdown parsed = MarkdownParser.Parse("# Title\n**bold** *italic* `code`\n```\nblock\n```");

        Assert.True(parsed.HasCodeBlocks);
        Assert.Contains("<h1>Title</h1>", parsed.Html);
        Assert.Contains("<bold>bold</bold>", parsed.Html);
        Assert.Contains("<italic>italic</italic>", parsed.Html);
        Assert.Contains("<code>code</code>", parsed.Html);
        Assert.Contains("<codeblock>", parsed.Html);
    }

    [Fact]
    public void ExtractCodeFromBlock_RemovesFencesAndLanguageLine()
    {
        Assert.Equal("line one\nline two", MarkdownParser.ExtractCodeFromBlock("```csharp\nline one\nline two\n```"));
    }

    [Fact]
    public void ToDisplayText_StripsBoldMarkers()
    {
        Assert.Equal("A bold result", MarkdownParser.ToDisplayText("A **bold** result"));
    }
}
