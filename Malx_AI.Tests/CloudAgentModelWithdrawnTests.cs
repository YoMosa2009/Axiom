using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests;

public sealed class CloudAgentModelWithdrawnTests
{
    [Theory]
    [InlineData("OpenRouter request for Dots3-Note failed with status 404 (NotFound): {\"error\":{\"message\":\"dots-studio/dots-3-note-preview:free is no longer available on OpenRouter\",\"code\":404}}")]
    [InlineData("failed with status 404 (NotFound): No endpoints found for x/y:free.")]
    [InlineData("status 404: x is not a valid model ID")]
    public void WithdrawnModelIsRecognised(string message)
    {
        Assert.True(CloudAgentModel.IsModelWithdrawnFailure(message));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("status 503 (ServiceUnavailable): upstream overloaded")]
    [InlineData("status 404 (NotFound): page missing")]
    [InlineData("status 429: rate limited, no longer available for 10s")]
    public void OtherFailuresAreNotTreatedAsWithdrawn(string? message)
    {
        Assert.False(CloudAgentModel.IsModelWithdrawnFailure(message));
    }
}
