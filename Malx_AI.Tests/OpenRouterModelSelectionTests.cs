using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class OpenRouterModelSelectionTests
{
    [Fact]
    public void DeepSeekV4F1017Profile_RoutesToDeepSeekV41Flash1017AndExposesExpectedMetadata()
    {
        var service = new OpenRouterChatService();

        Assert.Equal("deepseek-v4f-1017", OpenRouterChatService.DeepSeekV4F1017ModelId);
        Assert.Equal("DeepSeek V4F 1017", OpenRouterChatService.DeepSeekV4F1017ModelLabel);
        Assert.Equal("deepseek/deepseek-v4.1-flash-1017", OpenRouterChatService.DeepSeekV4F1017ApiModelId);

        Assert.Contains(service.SelectableModels, m =>
            m.Id == OpenRouterChatService.DeepSeekV4F1017ModelId
            && m.Label == OpenRouterChatService.DeepSeekV4F1017ModelLabel);

        Assert.Equal(
            OpenRouterChatService.DeepSeekV4F1017ModelId,
            service.NormalizeSelectableModelId("DeepSeek: DeepSeek V4.1 Flash 1017"));
        Assert.Equal(
            OpenRouterChatService.DeepSeekV4F1017ModelId,
            service.NormalizeSelectableModelId("deepseek/deepseek-v4.1-flash"));
        Assert.Equal(
            OpenRouterChatService.DeepSeekV4F1017ModelId,
            service.NormalizeSelectableModelId("DeepSeek V4F 1017"));

        Assert.Equal("DeepSeek V4F 1017", service.ResolveModelLabel(OpenRouterChatService.DeepSeekV4F1017ModelId));
        Assert.True(service.SupportsImageInput(OpenRouterChatService.DeepSeekV4F1017ModelId));
        Assert.Equal(1048576, service.GetApproximateContextWindowTokens(OpenRouterChatService.DeepSeekV4F1017ModelId));
        Assert.Equal("DeepSeek V4F 1017", service.GetInferenceSettingsSnapshot(OpenRouterChatService.DeepSeekV4F1017ModelId).ModelLabel);
    }

    [Theory]
    [InlineData("threnody-1.0")]
    [InlineData("Threnody 1.0")]
    [InlineData("DeepSeek: DeepSeek V4.1 Flash")]
    [InlineData("deepseek/deepseek-v4.1-flash")]
    [InlineData("deepseek/deepseek-v4.1-flash-1017")]
    public void LegacyThrenodySelections_NormalizeToDeepSeekV4F1017(string saved)
    {
        var service = new OpenRouterChatService();

        Assert.Equal("threnody-1.0", OpenRouterChatService.LegacyThrenody10ModelId);
        Assert.Equal(OpenRouterChatService.DeepSeekV4F1017ModelId, service.NormalizeSelectableModelId(saved));
    }

    [Fact]
    public void MigrateLegacyModelLabel_MapsThrenodyAndPassesOthersThrough()
    {
        Assert.Equal("DeepSeek V4F 1017", OpenRouterChatService.MigrateLegacyModelLabel("Threnody 1.0"));
        Assert.Equal("DeepSeek V4F 1017", OpenRouterChatService.MigrateLegacyModelLabel(" threnody 1.0 "));
        Assert.Equal("Edios 1.5", OpenRouterChatService.MigrateLegacyModelLabel("Edios 1.5"));
        Assert.Equal(string.Empty, OpenRouterChatService.MigrateLegacyModelLabel(null));
    }
}
