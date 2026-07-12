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

    [Theory]
    [InlineData(" https://Example.com/path/// ", "https://Example.com/path")]
    [InlineData("", "")]
    public void NormalizeUrl_TrimsWhitespaceAndTrailingSlashes(string input, string expected)
    {
        Assert.Equal(expected, WebSearchService.NormalizeUrl(input));
    }

    [Fact]
    public void NormalizeAndDeduplicateUrls_IsCaseInsensitiveAndStable()
    {
        IReadOnlyList<string> result = WebSearchService.NormalizeAndDeduplicateUrls(
            [" https://example.com/a/ ", "HTTPS://EXAMPLE.COM/A", "https://example.com/b", "  "]);

        Assert.Equal(["https://example.com/a", "https://example.com/b"], result);
    }
}
