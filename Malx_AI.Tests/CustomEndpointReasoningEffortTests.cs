using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class CustomEndpointReasoningEffortTests
{
    [Fact]
    public void ModelListThatNamesReasoningEffortIsRecognised()
    {
        const string json = """
            {"object":"list","data":[
              {"id":"other","context_window":8192},
              {"id":"inkling-2-mini","context_window":131072,"supported_parameters":["tools","tool_choice","reasoning_effort"]}
            ]}
            """;

        var metadata = CustomEndpointMetadataParser.Parse(json, "inkling-2-mini");

        Assert.True(metadata.SupportsReasoningEffort);
        Assert.Equal(131072, metadata.ContextWindowTokens);
    }

    [Theory]
    [InlineData("""{"data":[{"id":"m","context_window":8192}]}""")]
    [InlineData("""{"data":[{"id":"m","supported_parameters":["tools"]}]}""")]
    [InlineData("""{"data":[{"id":"m","supported_parameters":"reasoning_effort"}]}""")]
    [InlineData("""{"models":[{"name":"m","details":{}}]}""")]
    public void ReasoningEffortIsNeverAssumed(string json)
    {
        Assert.False(CustomEndpointMetadataParser.Parse(json, "m").SupportsReasoningEffort);
    }

    [Fact]
    public void OnlyTheConfiguredModelsListingCounts()
    {
        const string json = """
            {"data":[
              {"id":"thinker","supported_parameters":["reasoning_effort"]},
              {"id":"plain","context_window":4096}
            ]}
            """;

        Assert.False(CustomEndpointMetadataParser.Parse(json, "plain").SupportsReasoningEffort);
    }

    [Theory]
    [InlineData(EffortLevel.High, "medium")]
    [InlineData(EffortLevel.ExtraHigh, "high")]
    [InlineData(EffortLevel.Ultra, "max")]
    public void EachThinkingLevelAsksForADifferentBudget(EffortLevel level, string expected)
    {
        Assert.Equal(expected, EffortPolicy.CustomEndpointReasoningEffort(level));
    }
}
