using Xunit;

namespace Malx_AI.Tests;

public sealed class ReasoningParserTests
{
    [Fact]
    public void Parse_SegregatesClosedThinkBlock()
    {
        ReasoningParser.ParsedResponse parsed = ReasoningParser.Parse("<think>private reasoning</think>Public answer");

        Assert.True(parsed.HasThinking);
        Assert.Equal("private reasoning", parsed.ThinkingContent);
        Assert.Equal("Public answer", parsed.Answer);
    }

    [Fact]
    public void Parse_UnclosedThinkBlockIsMarkedTruncated()
    {
        ReasoningParser.ParsedResponse parsed = ReasoningParser.Parse("<think>unfinished reasoning");

        Assert.True(parsed.TruncatedInsideThinking);
        Assert.True(parsed.IsReasoningFallback);
        Assert.Equal("unfinished reasoning", parsed.ThinkingContent);
    }

    [Fact]
    public void Parse_RemovesEveryClosedThinkBlock()
    {
        ReasoningParser.ParsedResponse parsed = ReasoningParser.Parse("<think>one</think>Answer<think>two</think>");

        Assert.Equal("one\ntwo", parsed.ThinkingContent);
        Assert.Equal("Answer", parsed.Answer);
    }
}
