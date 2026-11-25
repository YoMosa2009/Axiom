using System.Text.Json;
using Xunit;

namespace Malx_AI.Tests
{
    public class PromptCacheUsageTests
    {
        [Fact]
        public void OpenAiStyleCachedTokensAreRead()
        {
            using var doc = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":15000,\"completion_tokens\":200,\"prompt_tokens_details\":{\"cached_tokens\":12000}}}");
            Assert.Equal(12000, OpenRouterTokenUsageParser.TryParse(doc.RootElement)!.CachedPromptTokens);
        }

        [Fact]
        public void DeepSeekCacheHitTokensAreRead()
        {
            using var doc = JsonDocument.Parse("{\"usage\":{\"prompt_tokens\":900,\"completion_tokens\":10,\"prompt_cache_hit_tokens\":640,\"prompt_cache_miss_tokens\":260}}");
            Assert.Equal(640, OpenRouterTokenUsageParser.TryParse(doc.RootElement)!.CachedPromptTokens);
        }

        [Fact]
        public void TheTooltipShowsHowMuchOfThePromptWasReused()
        {
            var stats = new GenerationStatsSnapshot(200, false, 40, 5, 1, PromptTokens: 15000, CachedPromptTokens: 12000);
            Assert.Contains("Prompt cache: 12,000 of 15,000 input tokens reused (80%)", GenerationStatsFormatter.FormatTooltip(stats, "Cloud"));
            Assert.DoesNotContain("Prompt cache", GenerationStatsFormatter.FormatTooltip(stats with { CachedPromptTokens = 0 }, "Cloud"));
        }
    }
}
