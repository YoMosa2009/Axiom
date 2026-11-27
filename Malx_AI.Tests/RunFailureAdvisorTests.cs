using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public class RunFailureAdvisorTests
{
    [Theory]
    [InlineData(429, "", RunFailureKind.RateLimit)]
    [InlineData(null, "Rate limit reached for requests", RunFailureKind.RateLimit)]
    [InlineData(402, "", RunFailureKind.QuotaExhausted)]
    [InlineData(null, "You exceeded your current quota", RunFailureKind.QuotaExhausted)]
    [InlineData(400, "This model's maximum context length is 8192 tokens", RunFailureKind.ContextTooLong)]
    [InlineData(413, "", RunFailureKind.ContextTooLong)]
    [InlineData(401, "", RunFailureKind.Authentication)]
    [InlineData(null, "Incorrect API key provided", RunFailureKind.Authentication)]
    [InlineData(503, "", RunFailureKind.ServerError)]
    [InlineData(null, "No such host is known", RunFailureKind.Network)]
    [InlineData(null, "The operation was canceled.", RunFailureKind.Cancelled)]
    [InlineData(null, "something odd", RunFailureKind.Unknown)]
    [InlineData(null, null, RunFailureKind.Unknown)]
    public void Classify_PicksKind(int? status, string? message, RunFailureKind expected)
        => Assert.Equal(expected, RunFailureAdvisor.Classify(status, message).Kind);

    [Fact]
    public void Classify_ContextOverflowBeatsRateLimitText()
        => Assert.Equal(RunFailureKind.ContextTooLong,
            RunFailureAdvisor.Classify(400, "too many tokens in the prompt").Kind);

    [Fact]
    public void Classify_NeverEchoesTheRawMessage()
    {
        var advice = RunFailureAdvisor.Classify(401, "Incorrect API key provided: sk-abc123SECRET");
        Assert.DoesNotContain("abc123", advice.Title + advice.Hint);
    }

    [Fact]
    public void Classify_EveryAdviceHasAnActionAndText()
    {
        foreach (int? s in new int?[] { null, 401, 402, 413, 429, 500 })
        {
            var a = RunFailureAdvisor.Classify(s, "x");
            Assert.NotEmpty(a.Actions);
            Assert.False(string.IsNullOrWhiteSpace(a.Title));
            Assert.False(string.IsNullOrWhiteSpace(a.Hint));
            Assert.Equal(a.Actions.Count, a.Actions.Distinct().Count());
        }
    }

    [Theory]
    [InlineData("Retry-After: 30", 30)]
    [InlineData("please try again in 20s", 20)]
    [InlineData("retry after 2 minutes", 120)]
    [InlineData("try again in 1500 ms", 2)]
    [InlineData("retry after 999999", 3600)]
    [InlineData("no hint here", null)]
    [InlineData("", null)]
    public void ParseRetryAfterSeconds_Parses(string text, int? expected)
        => Assert.Equal(expected, RunFailureAdvisor.ParseRetryAfterSeconds(text));

    [Fact]
    public void RateLimit_HintMentionsParsedWait()
    {
        var a = RunFailureAdvisor.Classify(429, "Rate limit. Retry after 12 seconds");
        Assert.Equal(12, a.RetryAfterSeconds);
        Assert.Contains("12 s", a.Hint);
    }
}
