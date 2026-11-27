using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AxiomSubscriptionGateway;
using Malx_AI.Agent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Malx_AI.Tests;

public sealed class ManagedInferenceGatewayTests : IDisposable
{
    private const string LocalToken = "local-managed-gateway-fixture";
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"axiom-managed-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public async Task PaidSandboxInvoiceUnlocksRealTransportAndAccountsStreamOnce()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var stripe = new StripeTestWebhook("cus_fixture", "price_fixture", null, "bpc_fixture");
        using var stripeHttp = new HttpClient(new StripeInvoicesFixture());
        var upstream = new InferenceFixture(HttpStatusCode.OK, """
            data: {"id":"gen-stream-fixture","choices":[{"delta":{"content":"Hello"},"index":0}]}

            data: {"id":"gen-stream-fixture","choices":[],"usage":{"cost":0.00008046,"prompt_tokens":275,"completion_tokens":43}}

            data: [DONE]

            """, "text/event-stream");
        using var openRouter = new HttpClient(upstream);
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0,
            stripeWebhook: stripe, stripeCheckout: new StripeTestCheckout("sk_test_fixture", stripeHttp),
            inference: new OpenRouterInference("openrouter-fixture", openRouter));
        await gateway.StartAsync(timeout.Token);
        using var client = AuthenticatedClient(gateway.Urls.Single());
        var desktop = new OpenRouterChatService();
        desktop.SetCustomEndpoint(new Uri(client.BaseAddress!, "v1").AbsoluteUri, LocalToken, LocalGateway.Model);
        await desktop.RefreshCustomEndpointMetadataAsync(timeout.Token, force: true);
        Assert.True(desktop.SupportsImageInput(LocalGateway.Model));
        Assert.Equal(OpenRouterInference.ContextTokens, desktop.CustomEndpointResolvedContextWindowTokens);
        var unpaidSubscription = await desktop.ReadAxiomSubscriptionAsync();
        Assert.NotNull(unpaidSubscription);
        Assert.Equal(9, unpaidSubscription.MonthlyUsd);
        Assert.Equal(0, unpaidSubscription.Balance.AllowanceMicros);
        var request = new
        {
            model = LocalGateway.Model,
            provider = new { only = new[] { "caller-provider" } },
            plugins = new[] { new { id = "billable-plugin" } },
            stop = new[] { "END" },
            max_tokens = 99_999,
            stream = true,
            messages = new[] { new { role = "user", content = "Hello" } }
        };

        using var unpaid = await client.PostAsJsonAsync("v1/chat/completions", request, timeout.Token);
        Assert.Equal(HttpStatusCode.PaymentRequired, unpaid.StatusCode);
        Assert.Equal(0, upstream.Calls);

        using var firstSync = await client.PostAsync("stripe/test/sync", null, timeout.Token);
        using var applied = JsonDocument.Parse(await firstSync.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(1, applied.RootElement.GetProperty("applied").GetInt32());

        using var streamed = await client.PostAsJsonAsync("v1/chat/completions", request, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
        Assert.Equal("text/event-stream", streamed.Content.Headers.ContentType?.MediaType);
        Assert.Contains("data: [DONE]", await streamed.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(1, upstream.Calls);

        using var payload = JsonDocument.Parse(upstream.Payload!);
        Assert.Equal(LocalGateway.Model, payload.RootElement.GetProperty("model").GetString());
        var routing = payload.RootElement.GetProperty("provider");
        Assert.Equal("price", routing.GetProperty("sort").GetString());
        Assert.True(routing.GetProperty("allow_fallbacks").GetBoolean());
        Assert.True(routing.GetProperty("require_parameters").GetBoolean());
        Assert.False(routing.TryGetProperty("only", out _));
        Assert.Equal(OpenRouterInference.MaxOutputTokens, payload.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(payload.RootElement.TryGetProperty("plugins", out _));
        Assert.False(payload.RootElement.TryGetProperty("stop", out _));

        using var secondSync = await client.PostAsync("stripe/test/sync", null, timeout.Token);
        using var replay = JsonDocument.Parse(await secondSync.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(0, replay.RootElement.GetProperty("applied").GetInt32());
        Assert.Equal(new AllowanceBalance(5_000_000, 81, 0),
            await client.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));
        var paidSubscription = await desktop.ReadAxiomSubscriptionAsync();
        Assert.NotNull(paidSubscription);
        Assert.NotNull(paidSubscription.RenewsAt);
        Assert.Equal(4_999_919, paidSubscription.Balance.AvailableMicros);
        var portal = await desktop.CreateAxiomSubscriptionLinkAsync(portal: true);
        Assert.Equal("https://billing.stripe.com/p/session/test_fixture", portal?.AbsoluteUri);
    }

    [Fact]
    public async Task DetailedSubscriptionLinkSurfacesStructuredGatewayErrorWithoutBreakingSimpleOverload()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0);
        gateway.MapPost("/v1/subscription/checkout", () =>
            Microsoft.AspNetCore.Http.Results.Json(
                new { error = new { message = "Founding batch full.", code = "founding_seats_full" } },
                statusCode: StatusCodes.Status503ServiceUnavailable));
        await gateway.StartAsync(timeout.Token);

        var desktop = new OpenRouterChatService();
        desktop.SetCustomEndpoint(new Uri(new Uri(gateway.Urls.Single()), "v1").AbsoluteUri, LocalToken, LocalGateway.Model);

        var simple = await desktop.CreateAxiomSubscriptionLinkAsync(portal: false);
        Assert.Null(simple);

        var detailed = await desktop.CreateAxiomSubscriptionLinkDetailedAsync(portal: false);
        Assert.Null(detailed.Url);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, detailed.StatusCode);
        Assert.Equal("founding_seats_full", detailed.ErrorCode);
        Assert.Equal("Founding batch full.", detailed.ErrorMessage);
    }

    [Theory]
    [InlineData(429, 502, false)]
    [InlineData(503, 409, true)]
    [InlineData(200, 409, true)]
    public async Task RejectionReleasesHoldAndAmbiguousOutcomePreservesIt(int upstreamStatus, int gatewayStatus, bool preserveHold)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upstream = new InferenceFixture((HttpStatusCode)upstreamStatus, """{"id":"gen-fixture"}""", "application/json");
        using var openRouter = new HttpClient(upstream);
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0,
            inference: new OpenRouterInference("openrouter-fixture", openRouter));
        await gateway.StartAsync(timeout.Token);
        using var client = AuthenticatedClient(gateway.Urls.Single());
        var request = new { model = LocalGateway.Model, messages = new[] { new { role = "user", content = "Hello" } } };
        using var requestDoc = JsonDocument.Parse(JsonSerializer.Serialize(request));
        long dynamicHold = OpenRouterInference.EstimateReservationMicros(requestDoc.RootElement, OpenRouterInference.DefaultMaxOutputTokens);
        Assert.InRange(dynamicHold, OpenRouterInference.MinReservationMicros, 9_999);

        using var rejected = await client.PostAsJsonAsync("v1/chat/completions", request, timeout.Token);

        Assert.Equal((HttpStatusCode)gatewayStatus, rejected.StatusCode);
        Assert.Equal(new AllowanceBalance(5_000_000, 0, preserveHold ? dynamicHold : 0),
            await client.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));
    }

    [Fact]
    public async Task UpstreamCapabilityRejectionReleasesHoldAndFailsFastWithProviderMessage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upstream = new InferenceFixture(
            HttpStatusCode.BadRequest,
            """{"error":{"message":"Provider returned error","code":400,"metadata":{"raw":"{\"error\":{\"message\":\"multimodal preprocessing error: unsupported format\"}}"}}}""",
            "application/json");
        using var openRouter = new HttpClient(upstream);
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0,
            inference: new OpenRouterInference("openrouter-fixture", openRouter));
        await gateway.StartAsync(timeout.Token);
        using var client = AuthenticatedClient(gateway.Urls.Single());
        var desktop = new OpenRouterChatService();
        desktop.SetCustomEndpoint(new Uri(client.BaseAddress!, "v1").AbsoluteUri, LocalToken, LocalGateway.Model);

        var chatError = await Assert.ThrowsAsync<OpenRouterCustomEndpointFatalException>(() =>
            desktop.SendConversationStreamAsync(
                [new OpenRouterMessage("user", "Inspect image")],
                systemPrompt: "Reply concisely.",
                modelId: OpenRouterChatService.CustomEndpointModelId,
                cancellationToken: timeout.Token));
        Assert.Contains("multimodal preprocessing error: unsupported format", chatError.Message);
        Assert.Equal(1, upstream.Calls);

        var agentModel = new CloudAgentModel(desktop, LocalGateway.Model, "You are a helpful agent.")
        {
            Delay = (_, _) => Task.CompletedTask
        };
        var agentError = await Assert.ThrowsAsync<AgentFatalException>(() =>
            agentModel.NextAsync("Inspect workspace", Array.Empty<AgentExchange>(), timeout.Token));
        Assert.Contains("multimodal preprocessing error: unsupported format", agentError.Message);
        Assert.Equal(2, upstream.Calls);

        Assert.Equal(new AllowanceBalance(5_000_000, 0, 0),
            await client.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));
    }

    [Fact]
    public async Task RevokedManagedSessionSurfacesSignInRequiredWithoutRetryingAgent()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0);
        await gateway.StartAsync(timeout.Token);
        var desktop = new OpenRouterChatService();
        desktop.SetCustomEndpoint(new Uri(new Uri(gateway.Urls.Single()), "v1").AbsoluteUri, "wrong-or-revoked-token", LocalGateway.Model);

        var chatError = await Assert.ThrowsAsync<OpenRouterCustomEndpointFatalException>(() =>
            desktop.SendConversationStreamAsync(
                [new OpenRouterMessage("user", "Hello")],
                systemPrompt: "Reply concisely.",
                modelId: OpenRouterChatService.CustomEndpointModelId,
                cancellationToken: timeout.Token));
        Assert.Contains("no longer active", chatError.Message);

        var agentModel = new CloudAgentModel(desktop, LocalGateway.Model, "You are a helpful agent.")
        {
            Delay = (_, _) => Task.CompletedTask
        };
        var agentError = await Assert.ThrowsAsync<AgentFatalException>(() =>
            agentModel.NextAsync("Hello", Array.Empty<AgentExchange>(), timeout.Token));
        Assert.Contains("no longer active", agentError.Message);
    }

    public void Dispose() => File.Delete(_database);

    [Fact]
    public async Task SettlementWorkerCompletesNormallyOnHostShutdown()
    {
        var ledger = new AllowanceLedger(_database, 5_000_000);
        var clock = new TimerObservedClock();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var worker = new OpenRouterSettlementWorker(new OpenRouterInference("openrouter-fixture"), ledger,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OpenRouterSettlementWorker>.Instance, clock);
        await worker.StartAsync(timeout.Token);
        await clock.Started.Task.WaitAsync(timeout.Token);
        await worker.StopAsync(timeout.Token);
        await worker.ExecuteTask!;
    }

    private sealed class TimerObservedClock : TimeProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            Started.TrySetResult();
            return timer;
        }
    }

    [Fact]
    public void PaidRenewalResetsItsOwnAllowanceAndLateSettlementStaysInTheOriginalPeriod()
    {
        const long monthAllowance = 5_000_000;
        var start = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new MutableClock(start);
        long monthOne = start.ToUnixTimeSeconds();
        long monthTwo = start.AddMonths(1).ToUnixTimeSeconds();
        long monthThree = start.AddMonths(2).ToUnixTimeSeconds();
        var first = new AllowanceLedger(_database, monthAllowance, "cus_first", clock);
        var other = new AllowanceLedger(_database, monthAllowance, "cus_other", clock);
        Assert.False(first.TryReserve("unpaid", 1));
        Assert.True(first.TryGrantPeriod("in_first", monthOne, monthTwo, monthAllowance));
        Assert.True(first.TryReserve("started-last-month", 10_000));
        Assert.True(other.TryGrantPeriod("in_other", monthOne, monthThree, monthAllowance));
        Assert.False(other.TrySettle("started-last-month", 500));

        clock.Now = start.AddMonths(1);
        Assert.Equal(0, first.GetBalance().AvailableMicros);
        Assert.False(first.TryReserve("unpaid-renewal", 1));
        Assert.True(first.TryGrantPeriod("in_renewal", monthTwo, monthThree, monthAllowance));
        Assert.False(first.TryGrantPeriod("in_renewal", monthTwo, monthThree, monthAllowance));
        Assert.True(first.TrySettle("started-last-month", 500));
        Assert.Equal(new AllowanceBalance(monthAllowance, 0, 0), first.GetBalance());
        Assert.Equal(new AllowanceBalance(monthAllowance, 0, 0), other.GetBalance());
        Assert.Equal(start.AddMonths(2), first.GetPeriod().RenewsAt);
    }

    [Fact]
    public async Task SandboxProvisioningCreatesOnlyTheTestPlanCustomerAndPortal()
    {
        using var stripeHttp = new HttpClient(new StripeProvisioningFixture());
        var stripe = new StripeTestCheckout("sk_test_fixture", stripeHttp);

        var config = await stripe.ProvisionAsync("fixture-run", CancellationToken.None);

        Assert.Equal("price_fixture", config.PriceId);
        Assert.Equal("cus_fixture", config.CustomerId);
        Assert.Equal("bpc_fixture", config.PortalConfigurationId);
        Assert.Null(config.SigningSecret);
        Assert.Throws<ArgumentException>(() => new StripeTestCheckout("sk_live_fixture", stripeHttp));
    }

    [Fact]
    public async Task ManagedEndpointStaysIndependentFromHybridCustomEndpointAndReadsSubscriptionUsage()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var desktop = new OpenRouterChatService();
        desktop.SetCustomEndpoint("http://127.0.0.1:8080/v1", "custom-token", OpenRouterChatService.CustomEndpointModelId);
        desktop.SetManagedEndpoint("https://staging.example.com/v1", "managed-token");

        Assert.True(desktop.HasValidManagedEndpoint);
        Assert.True(desktop.HasValidCloudCredential);
        Assert.True(desktop.HasCredentialForModel(OpenRouterChatService.AxiomManagedModelId));
        Assert.True(desktop.IsManagedEndpointModel(OpenRouterChatService.AxiomManagedModelId));
        Assert.False(desktop.IsCustomEndpointModel(OpenRouterChatService.AxiomManagedModelId));
        Assert.True(desktop.IsCustomEndpointModel(OpenRouterChatService.CustomEndpointModelId));
        Assert.Equal("DeepSeek 4.1F-1017", OpenRouterChatService.AxiomManagedModelLabel);
        Assert.True(desktop.SupportsImageInput(OpenRouterChatService.AxiomManagedModelId));

        var stripe = new StripeTestWebhook("cus_fixture", "price_fixture", null, "bpc_fixture");
        using var stripeHttp = new HttpClient(new StripeInvoicesFixture());
        var upstream = new InferenceFixture(HttpStatusCode.OK, """
            {"id":"gen-pace","choices":[{"message":{"content":"Hi"}}],"usage":{"cost":0.0005,"prompt_tokens":10,"completion_tokens":5}}
            """, "application/json");
        using var openRouter = new HttpClient(upstream);
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0,
            stripeWebhook: stripe, stripeCheckout: new StripeTestCheckout("sk_test_fixture", stripeHttp),
            inference: new OpenRouterInference("openrouter-fixture", openRouter));
        await gateway.StartAsync(timeout.Token);

        using var client = AuthenticatedClient(gateway.Urls.Single());
        using (var sync = await client.PostAsync("stripe/test/sync", null, timeout.Token))
            sync.EnsureSuccessStatusCode();

        desktop.SetManagedEndpoint(new Uri(client.BaseAddress!, "v1").AbsoluteUri, LocalToken);
        var subscription = await desktop.ReadAxiomSubscriptionAsync();
        Assert.NotNull(subscription);
        Assert.Equal(OpenRouterChatService.AxiomManagedModelLabel, subscription!.ModelLabel);
        Assert.NotNull(subscription.Usage);
    }

    [Fact]
    public async Task DynamicReservationUnlocksTailBalanceAndSizesImagesSafely()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upstream = new InferenceFixture(HttpStatusCode.OK, """
            {"id":"gen-small-bal","choices":[{"message":{"content":"Done"}}],"usage":{"cost":0.00012,"prompt_tokens":40,"completion_tokens":12}}
            """, "application/json");
        using var openRouter = new HttpClient(upstream);
        // Only $0.02 (20,000 micro-USD) remaining: previously blocked by the fixed $0.1902 hold, now succeeds.
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0, allowanceMicros: 20_000,
            inference: new OpenRouterInference("openrouter-fixture", openRouter));
        await gateway.StartAsync(timeout.Token);
        using var client = AuthenticatedClient(gateway.Urls.Single());

        using var shortRes = await client.PostAsJsonAsync("v1/chat/completions", new
        {
            model = LocalGateway.Model,
            max_tokens = 2_048,
            messages = new[] { new { role = "user", content = "Hello" } }
        }, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, shortRes.StatusCode);

        string bigImage = "data:image/png;base64," + new string('A', 200_000);
        using var imageDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "text", text = "Describe" },
                        new { type = "image_url", image_url = new { url = bigImage } }
                    }
                }
            }
        }));
        long imageHold = OpenRouterInference.EstimateReservationMicros(imageDoc.RootElement, 4_096);
        using var textDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = "Describe" } }
        }));
        long textHold = OpenRouterInference.EstimateReservationMicros(textDoc.RootElement, 4_096);
        Assert.True(imageHold > textHold);
        // Must count the bounded per-image estimate rather than 200KB * 1.25 raw base64 chars (~45,000 micro-USD alone).
        Assert.True(imageHold < 12_000, $"Expected image hold < 12000 micro-USD, got {imageHold}");

        using var heavyDoc = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            messages = new[] { new { role = "user", content = new string('x', 80_000) } }
        }));
        long heavyHold = OpenRouterInference.EstimateReservationMicros(heavyDoc.RootElement, OpenRouterInference.MaxOutputTokens);
        Assert.True(heavyHold > textHold * 3);
        Assert.InRange(heavyHold, OpenRouterInference.MinReservationMicros, OpenRouterInference.MaxReservationMicros);
    }

    [Fact]
    public async Task ManagedDesktopRequestsHonorReasoningOptInAndKeepToolsSchemaWhenDisablingToolCalls()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var upstream = new InferenceFixture(HttpStatusCode.OK, """
            data: {"id":"gen-cache-test","choices":[{"delta":{"content":"Synthesized"},"index":0}]}

            data: {"id":"gen-cache-test","choices":[],"usage":{"cost":0.00005,"prompt_tokens":100,"completion_tokens":8}}

            data: [DONE]

            """, "text/event-stream");
        using var openRouter = new HttpClient(upstream);
        await using var gateway = LocalGateway.Create(LocalToken, _database, port: 0,
            inference: new OpenRouterInference("openrouter-fixture", openRouter));
        await gateway.StartAsync(timeout.Token);

        var desktop = new OpenRouterChatService();
        desktop.SetManagedEndpoint(new Uri(new Uri(gateway.Urls.Single()), "v1").AbsoluteUri, LocalToken);

        var cloudAgent = new CloudAgentModel(desktop, OpenRouterChatService.AxiomManagedModelId, "Static agent system.");
        Assert.Null(cloudAgent.Unavailable);

        var tools = AgentToolSchemas.All();
        var conversation = new List<OpenRouterMessage>
        {
            new("user", "Run tool"),
            new("assistant", string.Empty, ToolCalls: [new OpenRouterToolCall("call_1", "list_directory", "{}")]),
            new("tool", "[]", ToolCallId: "call_1")
        };

        await desktop.SendConversationStreamAsync(
            conversation,
            systemPrompt: "Static prompt.",
            thinkingEnabled: true,
            modelId: OpenRouterChatService.AxiomManagedModelId,
            tools: tools,
            stopSequences: ["HALT"],
            allowModelFallback: false,
            disableToolCalls: false,
            cancellationToken: timeout.Token);

        using var firstSent = JsonDocument.Parse(upstream.Payload!);
        Assert.Equal("auto", firstSent.RootElement.GetProperty("tool_choice").GetString());
        string firstToolsJson = firstSent.RootElement.GetProperty("tools").GetRawText();

        await desktop.SendConversationStreamAsync(
            conversation,
            systemPrompt: "Static prompt.",
            thinkingEnabled: true,
            modelId: OpenRouterChatService.AxiomManagedModelId,
            tools: tools,
            stopSequences: ["HALT"],
            allowModelFallback: false,
            disableToolCalls: true,
            cancellationToken: timeout.Token);

        using var sent = JsonDocument.Parse(upstream.Payload!);
        Assert.True(sent.RootElement.GetProperty("reasoning").GetProperty("enabled").GetBoolean());
        Assert.False(sent.RootElement.TryGetProperty("stop", out _));
        Assert.Equal("none", sent.RootElement.GetProperty("tool_choice").GetString());
        Assert.Equal(tools.Count, sent.RootElement.GetProperty("tools").GetArrayLength());
        Assert.Equal(firstToolsJson, sent.RootElement.GetProperty("tools").GetRawText());
        Assert.Equal("call_1",
            sent.RootElement.GetProperty("messages")[2].GetProperty("tool_calls")[0].GetProperty("id").GetString());
    }

    [Fact]
    public void AgentPromptBuilderKeepsStaticPrefixIdenticalAcrossScopesAndTrackers()
    {
        string first = AgentPromptBuilder.Build(
            AgentTier.Full,
            AgentScope.Folder(@"C:\RepoOne"),
            AgentApprovalMode.Auto,
            nativeToolCalling: true,
            trackerFilePath: @"C:\RepoOne\.tracker.md",
            browserSummary: "Edge",
            visionCapable: true);

        string second = AgentPromptBuilder.Build(
            AgentTier.Full,
            AgentScope.Folder(@"D:\CompletelyDifferentProject\Sub"),
            AgentApprovalMode.Manual,
            nativeToolCalling: true,
            trackerFilePath: @"D:\CompletelyDifferentProject\Sub\AXIOM_TASK_TRACKER.md",
            browserSummary: "Chrome",
            visionCapable: true);

        int sessionIndexFirst = first.IndexOf("[SESSION CONTEXT]", StringComparison.Ordinal);
        int sessionIndexSecond = second.IndexOf("[SESSION CONTEXT]", StringComparison.Ordinal);
        Assert.True(sessionIndexFirst > 1500, $"Expected invariant prefix > 1500 chars, got {sessionIndexFirst}");
        Assert.Equal(sessionIndexFirst, sessionIndexSecond);
        Assert.Equal(first[..sessionIndexFirst], second[..sessionIndexSecond]);
        Assert.Contains(@"C:\RepoOne", first[sessionIndexFirst..]);
        Assert.Contains(@"D:\CompletelyDifferentProject\Sub", second[sessionIndexSecond..]);
    }

    private static HttpClient AuthenticatedClient(string address)
    {
        var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", LocalToken);
        return http;
    }

    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class InferenceFixture(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://openrouter.ai/api/v1/chat/completions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer openrouter-fixture", request.Headers.Authorization?.ToString());
            Calls++;
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType)
            };
        }
    }

    private sealed class StripeInvoicesFixture : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.stripe.com", request.RequestUri?.Host);
            Assert.Equal("Bearer sk_test_fixture", request.Headers.Authorization?.ToString());
            if (request.RequestUri?.AbsolutePath == "/v1/billing_portal/sessions")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                string form = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(cancellationToken));
                Assert.Contains("customer=cus_fixture", form);
                Assert.Contains("configuration=bpc_fixture", form);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { livemode = false, url = "https://billing.stripe.com/p/session/test_fixture" })
                };
            }
            Assert.Equal("https://api.stripe.com/v1/invoices?customer=cus_fixture&status=paid&limit=20",
                request.RequestUri?.AbsoluteUri);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var json = JsonSerializer.Serialize(new
            {
                data = new[] { new
                {
                    id = "in_fixture", livemode = false, status = "paid", customer = "cus_fixture",
                    billing_reason = "subscription_create",
                    parent = new { subscription_details = new { subscription = "sub_fixture" } },
                    lines = new { data = new[] { new
                    {
                        period = new { start = now - 60, end = now + 2_592_000 },
                        parent = new { subscription_item_details = new { proration = false } },
                        pricing = new { price_details = new { price = "price_fixture" } }
                    } } }
                } }
            });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class StripeProvisioningFixture : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("api.stripe.com", request.RequestUri?.Host);
            Assert.Equal("Bearer sk_test_fixture", request.Headers.Authorization?.ToString());
            Assert.Contains("fixture-run", request.Headers.GetValues("Idempotency-Key").Single());
            string form = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Contains("metadata[axiom_sandbox_run]=fixture-run", form);
            string id;
            switch (request.RequestUri?.AbsolutePath)
            {
                case "/v1/prices":
                    Assert.Contains("unit_amount=900", form);
                    Assert.Contains("currency=usd", form);
                    Assert.Contains("recurring[interval]=month", form);
                    id = "price_fixture";
                    break;
                case "/v1/customers":
                    id = "cus_fixture";
                    break;
                case "/v1/billing_portal/configurations":
                    Assert.Contains("features[subscription_cancel][mode]=at_period_end", form);
                    id = "bpc_fixture";
                    break;
                default:
                    throw new InvalidOperationException("Unexpected Stripe provisioning request.");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { id, livemode = false })
            };
        }
    }
}
