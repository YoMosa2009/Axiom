using System;
using System.IO;
using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class AgentCardLabelsTests
{
    [Theory]
    [InlineData(null, "Hybrid Local")]
    [InlineData("", "Hybrid Local")]
    [InlineData("   ", "Hybrid Local")]
    [InlineData("qwen3:8b", "qwen3:8b")]
    [InlineData("meta-llama/Llama-3.1-8B-Instruct", "Llama-3.1-8B-Instruct")]
    [InlineData("C:\\models\\gemma-3-4b-it-Q4_K_M.gguf", "gemma-3-4b-it-Q4_K_M")]
    [InlineData("org/model/", "model")]
    [InlineData(" Gemma 4 E4B ", "Gemma 4 E4B")]
    public void HybridDisplayNameIsTheConfiguredModel(string? id, string expected)
        => Assert.Equal(expected, AgentCardLabels.HybridModelDisplayName(id));

    [Fact]
    public void HybridDisplayNameNeverUsesTheOldProductName()
        => Assert.DoesNotContain("Kestral", AgentCardLabels.HybridModelDisplayName("gpt-oss-20b"));

    [Theory]
    [InlineData("DeepSeek 4.1F", "Agent Access", "DeepSeek 4.1F")]
    [InlineData("DeepSeek 4.1F", null, "DeepSeek 4.1F")]
    [InlineData("DeepSeek 4.1F", "Computer Use", "DeepSeek 4.1F · Computer Use")]
    [InlineData(null, "Agent Access", "Agent")]
    [InlineData("  ", "Computer Use", "Agent · Computer Use")]
    public void AgentCardHeaderIsTheModelName(string? model, string? kind, string expected)
        => Assert.Equal(expected, AgentCardLabels.AgentCardHeader(model, kind));

    [Fact]
    public void ShortRunsShowEveryStep()
    {
        string[] steps = { "✓ a", "✓ b", "✓ c", "✓ d" };
        Assert.Equal(steps, AgentCardLabels.CompactLiveSteps(steps));
        Assert.Empty(AgentCardLabels.CompactLiveSteps(null));
    }

    [Fact]
    public void LongRunsFoldEarlierStepsIntoOneLine()
    {
        string[] steps = { "✓ a", "✗ b — failed", "✓ c", "✓ d", "✓ e", "✓ f", "✓ g" };
        var compact = AgentCardLabels.CompactLiveSteps(steps);
        Assert.Equal(4, compact.Count);
        Assert.Equal("⋯ 4 earlier actions (1 failed)", compact[0]);
        Assert.Equal(new[] { "✓ e", "✓ f", "✓ g" }, compact.Skip(1));
    }

    [Fact]
    public void ResolvedCustomEndpointLabelIsTheConfiguredModel()
    {
        var service = new OpenRouterChatService();
        Assert.Equal("Hybrid Local", service.ResolveModelLabel(OpenRouterChatService.CustomEndpointModelId));
    }
}

public sealed class AppFontPolicyTests
{
    [Theory]
    [InlineData("AnthropicSans-Text-Regular-Static.otf", true)]
    [InlineData("C:\\Users\\x\\AppData\\Local\\Microsoft\\Windows\\Fonts\\AnthropicSans-Text-Bold-Static.otf", true)]
    [InlineData("anthropicsans-text-medium.ttf", true)]
    [InlineData("AnthropicSans-Display-Regular-Static.otf", false)]
    [InlineData("Geist-Regular.ttf", false)]
    [InlineData(null, false)]
    public void RecognisesInstalledAnthropicSansText(string? path, bool expected)
        => Assert.Equal(expected, AppFontPolicy.IsPreferredFontFile(path));

    [Fact]
    public void FindsTheFolderThatHoldsTheFont()
    {
        string empty = Directory.CreateTempSubdirectory("axiom-font-empty").FullName;
        string withFont = Directory.CreateTempSubdirectory("axiom-font-has").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(withFont, "AnthropicSans-Text-Regular-Static.otf"), new byte[] { 0 });
            Assert.Equal(withFont, AppFontPolicy.FindPreferredFontFolder(new[] { Path.Combine(empty, "missing"), empty, withFont }));
            Assert.Null(AppFontPolicy.FindPreferredFontFolder(new[] { empty }));
        }
        finally
        {
            Directory.Delete(empty, true);
            Directory.Delete(withFont, true);
        }
    }
}
