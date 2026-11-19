using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AxiomSubscriptionGateway;
using Xunit;

namespace Malx_AI.Tests;

public sealed class OpenRouterRouteProbeTests
{
    [Theory]
    [InlineData("Text", """{"content":"AXIOM_PROBE_OK"}""")]
    [InlineData("Tool", """{"tool_calls":[{"function":{"name":"record_status","arguments":"{\"status\":\"AXIOM_PROBE_OK\"}"}}]}""")]
    [InlineData("Image", """{"content":"Red."}""")]
    public async Task UsesPinnedAndCappedRouteThenChecksCapabilityAndCost(string check, string message)
    {
        var fixture = new OpenRouterFixture(message);
        using var http = new HttpClient(fixture);
        var result = await new OpenRouterRouteProbe(http, "fixture-key").RunAsync(Enum.Parse<ProbeKind>(check), default);

        Assert.Equal(check, result.Check);
        Assert.Equal("DigitalOcean", result.Provider);
        Assert.Equal("deepseek/deepseek-v4.1-flash-1017", result.Model);
        Assert.Equal(0.00002m, result.UsageCostUsd);
        Assert.Equal(result.UsageCostUsd, result.AccountedCostUsd);
        Assert.Equal(1, fixture.GenerationPosts);
        Assert.Equal(1, fixture.AccountingGets);

        using var document = JsonDocument.Parse(fixture.Payload!);
        var payload = document.RootElement;
        Assert.Equal(result.Model, payload.GetProperty("model").GetString());
        Assert.Equal(128, payload.GetProperty("max_tokens").GetInt32());
        Assert.False(payload.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        var provider = payload.GetProperty("provider");
        Assert.Equal("digitalocean", Assert.Single(provider.GetProperty("only").EnumerateArray()).GetString());
        Assert.False(provider.GetProperty("allow_fallbacks").GetBoolean());
        var price = provider.GetProperty("max_price");
        Assert.Equal(0.18m, price.GetProperty("prompt").GetDecimal());
        Assert.Equal(0.72m, price.GetProperty("completion").GetDecimal());
        Assert.Equal(0m, price.GetProperty("request").GetDecimal());
        Assert.Equal(0m, price.GetProperty("image").GetDecimal());

        if (check == "Tool")
            Assert.Equal("record_status", payload.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString());
        if (check == "Image")
        {
            var image = payload.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString();
            Assert.StartsWith("data:image/png;base64,", image);
            byte[] png = Convert.FromBase64String(image!["data:image/png;base64,".Length..]);
            Assert.Equal("57CA6D43B0AA973AC473F7176D3E699AB1C7C115496149EFF1D8C3BB464E83D1",
                Convert.ToHexString(SHA256.HashData(png)));
        }
    }

    [Theory]
    [InlineData("""{"limit":null,"limit_remaining":null}""", "no limit reported", "unavailable")]
    [InlineData("""{"limit":0.51,"limit_remaining":0.50}""", "$0.51", "$0.5")]
    [InlineData("""{"limit":0.500,"limit_remaining":0.001}""", "$0.5", "$0.001")]
    public async Task RejectsIneligibleKeyBeforeDispatch(string keyInfo, string reportedLimit, string reportedRemaining)
    {
        var fixture = new OpenRouterFixture("""{"content":"AXIOM_PROBE_OK"}""") { KeyInfo = keyInfo };
        using var http = new HttpClient(fixture);
        var probe = new OpenRouterRouteProbe(http, "fixture-key");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RunAsync(ProbeKind.Text, default));
        Assert.Contains($"key limit of {reportedLimit} and a remaining key allowance of {reportedRemaining}", error.Message);
        Assert.Contains("No generation was requested", error.Message);
        Assert.Equal(0, fixture.GenerationPosts);
    }

    [Fact]
    public async Task NonSuccessGenerationResponseIncludesSanitizedProviderError()
    {
        var fixture = new OpenRouterFixture("")
        {
            GenerationStatus = HttpStatusCode.BadRequest,
            GenerationErrorJson = """
                {"error":{"message":"Provider returned error","code":400,"metadata":{"raw":"{\"error\":{\"message\":\"unsupported multimodal format\"}}"}}}
                """
        };
        using var http = new HttpClient(fixture);
        var probe = new OpenRouterRouteProbe(http, "fixture-key");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RunAsync(ProbeKind.Text, default));
        Assert.Contains("HTTP 400", error.Message);
        Assert.Contains("unsupported multimodal format", error.Message);
        Assert.Equal(1, fixture.GenerationPosts);
    }

    [Theory]
    [InlineData("OtherProvider", false, 0.00002)]
    [InlineData("DigitalOcean", true, 0.00002)]
    [InlineData("DigitalOcean", false, 0.0001)]
    public async Task RejectsUnexpectedProviderOrCostWithoutRepeatingGeneration(string provider, bool byok, decimal cost)
    {
        var fixture = new OpenRouterFixture("""{"content":"AXIOM_PROBE_OK"}""")
        {
            AccountedProvider = provider,
            IsByok = byok,
            AccountedCost = cost
        };
        using var http = new HttpClient(fixture);
        var probe = new OpenRouterRouteProbe(http, "fixture-key");

        await Assert.ThrowsAsync<InvalidOperationException>(() => probe.RunAsync(ProbeKind.Text, default));
        Assert.Equal(1, fixture.GenerationPosts);
    }

    [Theory]
    [InlineData("deepseek/deepseek-v4.1-flash-20261017", true)]
    [InlineData("deepseek/deepseek-v4.1-flash", false)]
    public async Task LookupOnlyRetriesPendingAccountingWithoutStartingAGeneration(string accountedModel, bool expectedRoute)
    {
        var fixture = new OpenRouterFixture("") { PendingAccountingReads = 1, AccountedModel = accountedModel };
        using var http = new HttpClient(fixture);
        var probe = new OpenRouterRouteProbe(http, "fixture-key");

        var result = await probe.LookupAsync("gen-fixture", default);

        Assert.Equal(expectedRoute, result.ExpectedRoute);
        Assert.Equal(accountedModel, result.Model);
        Assert.Equal("DigitalOcean", result.Provider);
        Assert.Equal(0.00002m, result.AccountedCostUsd);
        Assert.Equal(50, result.PromptTokens);
        Assert.Equal(15, result.CompletionTokens);
        Assert.Equal(2, fixture.AccountingGets);
        Assert.Equal(0, fixture.KeyGets);
        Assert.Equal(0, fixture.GenerationPosts);
    }

    private sealed class OpenRouterFixture(string message) : HttpMessageHandler
    {
        public string KeyInfo { get; init; } = """{"limit":0.500,"limit_remaining":0.500}""";
        public string AccountedProvider { get; init; } = "DigitalOcean";
        public string AccountedModel { get; init; } = LocalGateway.Model;
        public bool IsByok { get; init; }
        public decimal AccountedCost { get; init; } = 0.00002m;
        public int PendingAccountingReads { get; init; }
        public HttpStatusCode GenerationStatus { get; init; } = HttpStatusCode.OK;
        public string? GenerationErrorJson { get; init; }
        public int KeyGets { get; private set; }
        public int GenerationPosts { get; private set; }
        public int AccountingGets { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("openrouter.ai", request.RequestUri?.Host);
            Assert.Equal("Bearer fixture-key", request.Headers.Authorization?.ToString());
            switch (request.RequestUri?.PathAndQuery)
            {
                case "/api/v1/key":
                    Assert.Equal(HttpMethod.Get, request.Method);
                    KeyGets++;
                    return Ok("""{"data":""" + KeyInfo + "}");
                case "/api/v1/chat/completions":
                    Assert.Equal(HttpMethod.Post, request.Method);
                    GenerationPosts++;
                    Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
                    if (GenerationStatus != HttpStatusCode.OK)
                    {
                        return new HttpResponseMessage(GenerationStatus)
                        {
                            Content = new StringContent(GenerationErrorJson ?? string.Empty, System.Text.Encoding.UTF8, "application/json")
                        };
                    }
                    return Ok("""{"id":"gen-fixture","choices":[{"message":""" + message
                        + """}],"usage":{"prompt_tokens":50,"completion_tokens":15,"cost":0.00002}}""");
                case "/api/v1/generation?id=gen-fixture":
                    Assert.Equal(HttpMethod.Get, request.Method);
                    AccountingGets++;
                    if (AccountingGets <= PendingAccountingReads)
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    return Ok(JsonSerializer.Serialize(new
                    {
                        data = new
                        {
                            id = "gen-fixture", model = AccountedModel, provider_name = AccountedProvider,
                            cancelled = false, is_byok = IsByok, total_cost = AccountedCost,
                            native_tokens_prompt = 50, native_tokens_completion = 15, finish_reason = "stop"
                        }
                    }));
                default:
                    throw new InvalidOperationException("Unexpected fixture request.");
            }
        }

        private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
