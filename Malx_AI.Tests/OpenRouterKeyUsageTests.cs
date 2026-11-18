using Malx_AI;
using System.Text.Json;
using Xunit;

namespace Malx_AI.Tests;

public class OpenRouterKeyUsageTests
{
    [Fact]
    public void NullCreditLimitDoesNotHideFreeModelQuota()
    {
        var info = OpenRouterKeyUsage.Parse("""{"data":{"limit":null,"usage":0,"free_model_daily_requests":{"used":12,"limit":50,"remaining":38}}}""");
        Assert.True(info.FetchSucceeded);
        Assert.True(info.HasDailyRequestUsage);
        Assert.Equal(12, info.RequestsUsed);
        Assert.Equal(50, info.RequestsLimit);
        Assert.Equal(76, OpenRouterKeyUsage.RemainingPercent(info));
        Assert.Contains("76% left", OpenRouterKeyUsage.Label(info));
    }

    [Fact]
    public void SpendingAmountsAndDeprecatedRateLimitAreNeverRequestCounts()
    {
        var info = OpenRouterKeyUsage.Parse("""{"data":{"usage":25.5,"limit":100,"usage_daily":17,"limit_remaining":74.5,"rate_limit":{"requests":1000,"interval":"1h"}}}""");
        Assert.True(info.FetchSucceeded);
        Assert.False(info.HasDailyRequestUsage);
        Assert.Equal(0, OpenRouterKeyUsage.RemainingPercent(info));
        Assert.Equal("Daily usage unavailable", OpenRouterKeyUsage.Label(info));
    }

    [Fact]
    public void PaidAccountStillHasItsSeparateFreeRequestCounter()
    {
        var info = OpenRouterKeyUsage.Parse("""{"data":{"is_free_tier":false,"limit":100,"usage":25.5,"free_model_daily_requests":{"used":280,"limit":1000,"remaining":720}}}""");
        Assert.Equal(72, OpenRouterKeyUsage.RemainingPercent(info));
        Assert.Equal(1000, info.RequestsLimit);
    }

    [Theory]
    [InlineData(0, 50, 50, 100)]
    [InlineData(25, 50, 25, 50)]
    [InlineData(50, 50, 0, 0)]
    [InlineData(51, 50, 0, 0)]
    [InlineData(0, 0, 0, 0)]
    public void RemainingPercentageMatchesReportedQuota(int used, int limit, int remaining, double percent)
    {
        var info = OpenRouterKeyUsage.Parse(JsonSerializer.Serialize(new { data = new { free_model_daily_requests = new { used, limit, remaining } } }));
        Assert.True(info.HasDailyRequestUsage);
        Assert.Equal(remaining, info.RequestsRemaining);
        Assert.Equal(percent, OpenRouterKeyUsage.RemainingPercent(info));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"used\":-1,\"limit\":50}")]
    [InlineData("{\"used\":1.5,\"limit\":50}")]
    [InlineData("{\"used\":1,\"limit\":null}")]
    [InlineData("{\"used\":1,\"limit\":50,\"remaining\":51}")]
    [InlineData("{\"used\":1,\"limit\":50,\"remaining\":-1}")]
    [InlineData("{\"used\":1,\"limit\":50,\"remaining\":\"49\"}")]
    public void UnknownOrMalformedCounterDoesNotClaimAvailableRequests(string counter)
    {
        var info = OpenRouterKeyUsage.Parse("{\"data\":{\"free_model_daily_requests\":" + counter + "}}");
        Assert.True(info.FetchSucceeded);
        Assert.False(info.HasDailyRequestUsage);
        Assert.DoesNotContain("%", OpenRouterKeyUsage.Label(info));
    }

    [Fact]
    public void RemainingCanBeDerivedOnlyFromNamedFreeModelCounter()
    {
        var info = OpenRouterKeyUsage.Parse("""{"data":{"free_model_daily_requests":{"used":20,"limit":50}}}""");
        Assert.Equal(30, info.RequestsRemaining);
        Assert.Equal(60, OpenRouterKeyUsage.RemainingPercent(info));
    }

    [Fact]
    public void ExplicitRemainingIsAuthoritative()
    {
        var info = OpenRouterKeyUsage.Parse("""{"data":{"free_model_daily_requests":{"used":20,"limit":50,"remaining":29}}}""");
        Assert.Equal(29, info.RequestsRemaining);
        Assert.Equal(58, OpenRouterKeyUsage.RemainingPercent(info));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"data\":null}")]
    public void BadResponseIsAFetchFailure(string payload)
    {
        var info = OpenRouterKeyUsage.Parse(payload);
        Assert.False(info.FetchSucceeded);
        Assert.False(info.HasDailyRequestUsage);
    }

    [Fact]
    public void CacheExpiresAfterThirtySecondsAndAcrossUtcMidnight()
    {
        var beforeMidnight = new DateTimeOffset(2026, 11, 17, 23, 59, 59, TimeSpan.Zero);
        Assert.True(OpenRouterKeyUsage.IsFresh(beforeMidnight, beforeMidnight.AddMilliseconds(500)));
        Assert.False(OpenRouterKeyUsage.IsFresh(beforeMidnight, beforeMidnight.AddSeconds(2)));
        Assert.False(OpenRouterKeyUsage.IsFresh(beforeMidnight, beforeMidnight.AddSeconds(-1)));
        Assert.False(OpenRouterKeyUsage.IsFresh(null, beforeMidnight));
        var noon = beforeMidnight.AddHours(-12);
        Assert.True(OpenRouterKeyUsage.IsFresh(noon, noon.AddSeconds(29)));
        Assert.False(OpenRouterKeyUsage.IsFresh(noon, noon.AddSeconds(30)));
    }
}
