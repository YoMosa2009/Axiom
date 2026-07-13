using Xunit;

namespace Malx_AI.Tests;

public sealed class WebSearchServiceTests
{
    [Fact]
    public void QueryBuilding_RemovesFillerAndPreservesTechnicalTerms()
    {
        var service = new WebSearchService();

        string query = service.BuildStrategicSearchQuery("Please search the web for official .NET 10 WPF documentation");

        Assert.Contains("NET", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WPF", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("please", query, StringComparison.OrdinalIgnoreCase);
    }

    // NormalizeUrl canonicalizes for dedup: scheme and fragment are dropped, the host is
    // lowercased with "www." removed, trailing slashes are trimmed, and tracking query
    // parameters (utm_*, fbclid, ...) are stripped so the same article shared through
    // different tracking links collapses to one entry.
    [Theory]
    [InlineData(" https://Example.com/path/// ", "example.com/path")]
    [InlineData("https://www.example.com/article?utm_source=x&utm_campaign=y", "example.com/article")]
    [InlineData("http://example.com/article?id=7&fbclid=abc", "example.com/article?id=7")]
    [InlineData("https://example.com/page#section", "example.com/page")]
    [InlineData("not a url", "not a url")]
    [InlineData("", "")]
    public void NormalizeUrl_CanonicalizesForDeduplication(string input, string expected)
    {
        Assert.Equal(expected, WebSearchService.NormalizeUrl(input));
    }

    [Fact]
    public void NormalizeAndDeduplicateUrls_IsCaseInsensitiveAndStable()
    {
        IReadOnlyList<string> result = WebSearchService.NormalizeAndDeduplicateUrls(
            [" https://example.com/a/ ", "HTTPS://EXAMPLE.COM/A", "https://example.com/b", "  "]);

        Assert.Equal(["example.com/a", "example.com/b"], result);
    }
}
