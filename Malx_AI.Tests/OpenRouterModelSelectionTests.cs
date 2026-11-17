using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class OpenRouterModelSelectionTests
{
    [Fact]
    public void Threnody10Profile_RoutesToDeepSeekV41FlashAndExposesExpectedMetadata()
    {
        var service = new OpenRouterChatService();

        Assert.Equal("threnody-1.0", OpenRouterChatService.Threnody10ModelId);
        Assert.Equal("Threnody 1.0", OpenRouterChatService.Threnody10ModelLabel);
        Assert.Equal("deepseek/deepseek-v4.1-flash", OpenRouterChatService.Threnody10ApiModelId);

        Assert.Contains(service.SelectableModels, m =>
            m.Id == OpenRouterChatService.Threnody10ModelId
            && m.Label == OpenRouterChatService.Threnody10ModelLabel);

        Assert.Equal(
            OpenRouterChatService.Threnody10ModelId,
            service.NormalizeSelectableModelId("DeepSeek: DeepSeek V4.1 Flash"));
        Assert.Equal(
            OpenRouterChatService.Threnody10ModelId,
            service.NormalizeSelectableModelId("deepseek/deepseek-v4.1-flash"));
        Assert.Equal(
            OpenRouterChatService.Threnody10ModelId,
            service.NormalizeSelectableModelId("Threnody 1.0"));

        Assert.Equal("Threnody 1.0", service.ResolveModelLabel(OpenRouterChatService.Threnody10ModelId));
        Assert.True(service.SupportsImageInput(OpenRouterChatService.Threnody10ModelId));
        Assert.Equal(1048576, service.GetApproximateContextWindowTokens(OpenRouterChatService.Threnody10ModelId));
        Assert.Equal("Threnody 1.0", service.GetInferenceSettingsSnapshot(OpenRouterChatService.Threnody10ModelId).ModelLabel);
    }
}
