using System.Text.Json;
using System.Text.Json.Nodes;
using AxiomSubscriptionGateway;
using Xunit;

namespace Malx_AI.Tests;

public class OpenRouterProviderRoutingTests
{
    private static string[] Strings(JsonNode? node) => node!.AsArray().Select(n => n!.GetValue<string>()).ToArray();

    [Fact]
    public void DefaultIsCheapestFirstWithFallbacksAndRequiredParameters()
    {
        JsonObject json = OpenRouterProviderRouting.FromEnvironment(_ => null).ToJson(0.18m, 0.72m);

        Assert.Equal("price", json["sort"]!.GetValue<string>());
        Assert.True(json["allow_fallbacks"]!.GetValue<bool>());
        Assert.True(json["require_parameters"]!.GetValue<bool>());
        Assert.Null(json["only"]);
        Assert.Null(json["data_collection"]);
        Assert.Equal(0.18m, json["max_price"]!["prompt"]!.GetValue<decimal>());
        Assert.Equal(0.72m, json["max_price"]!["completion"]!.GetValue<decimal>());
    }

    [Fact]
    public void EnvironmentOverridesApplyAndInvalidValuesKeepDefaults()
    {
        var env = new Dictionary<string, string>
        {
            [OpenRouterProviderRouting.OnlyEnv] = "wafer/us, ambient/fp4",
            [OpenRouterProviderRouting.AllowFallbacksEnv] = "false",
            [OpenRouterProviderRouting.DataCollectionEnv] = "deny",
            [OpenRouterProviderRouting.QuantizationsEnv] = "fp8,bf16",
            [OpenRouterProviderRouting.SortEnv] = "bogus",
            [OpenRouterProviderRouting.RequireParametersEnv] = "maybe"
        };

        JsonObject json = OpenRouterProviderRouting.FromEnvironment(k => env.GetValueOrDefault(k)).ToJson(1m, 2m);

        Assert.Equal(new[] { "wafer/us", "ambient/fp4" }, Strings(json["only"]));
        Assert.False(json["allow_fallbacks"]!.GetValue<bool>());
        Assert.Equal("deny", json["data_collection"]!.GetValue<string>());
        Assert.Equal(new[] { "fp8", "bf16" }, Strings(json["quantizations"]));
        Assert.Equal("price", json["sort"]!.GetValue<string>());
        Assert.True(json["require_parameters"]!.GetValue<bool>());
    }

    [Fact]
    public void ExplicitOrderReplacesSortAndSortNoneDisablesIt()
    {
        var ordered = (OpenRouterProviderRouting.Default with { Order = new[] { "wafer/us" } }).ToJson(1m, 1m);
        Assert.Null(ordered["sort"]);
        Assert.Equal("wafer/us", ordered["order"]![0]!.GetValue<string>());

        var env = new Dictionary<string, string> { [OpenRouterProviderRouting.SortEnv] = "none" };
        var unsorted = OpenRouterProviderRouting.FromEnvironment(k => env.GetValueOrDefault(k)).ToJson(1m, 1m);
        Assert.Null(unsorted["sort"]);
    }

    [Fact]
    public void OpenRouterPayloadCarriesRoutingCappedAtAllowanceRates()
    {
        using var doc = JsonDocument.Parse("""{"messages":[{"role":"user","content":"hi"}],"tools":[{"type":"function"}]}""");
        var options = UpstreamInferenceOptions.OpenRouter("sk-or-test");

        JsonObject payload = OpenRouterInference.BuildPayload(doc.RootElement, stream: true, options, out _);

        var provider = payload["provider"]!.AsObject();
        Assert.Equal("price", provider["sort"]!.GetValue<string>());
        Assert.True(provider["require_parameters"]!.GetValue<bool>());
        Assert.Equal(0.18m, provider["max_price"]!["prompt"]!.GetValue<decimal>());
        Assert.NotNull(payload["tools"]);
        Assert.Null(payload["stop"]);
    }
}
