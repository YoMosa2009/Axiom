using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxiomSubscriptionGateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Malx_AI.Tests;

public sealed class SubscriptionProductionReadinessTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"axiom-prod-tests-{Guid.NewGuid():N}");

    public SubscriptionProductionReadinessTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData("DigitalOcean", "digitalocean", "deepseek-v4.1-flash-1017")]
    [InlineData("DeepSeek", "deepseek", "deepseek-chat")]
    public async Task DirectProviders_SendStreamOptionsAndCalculateCostAndSkipOpenRouterReconcile(
        string providerName, string expectedOwner, string expectedModel)
    {
        UpstreamProviderKind kind = Enum.Parse<UpstreamProviderKind>(providerName, ignoreCase: true);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        UpstreamInferenceOptions options = kind == UpstreamProviderKind.DigitalOcean
            ? UpstreamInferenceOptions.DigitalOcean("do-fixture-key", expectedModel)
            : UpstreamInferenceOptions.DeepSeek("deepseek-fixture-key", expectedModel);

        var capture = new CaptureUpstreamHandler("""
            data: {"id":"direct-1","choices":[{"delta":{"content":"Hi"},"index":0}]}

            data: {"id":"direct-1","choices":[],"usage":{"prompt_tokens":1000,"prompt_cache_hit_tokens":400,"completion_tokens":200}}

            data: [DONE]

            """, "text/event-stream");
        using var http = new HttpClient(capture);
        var inference = new OpenRouterInference(options, http);
        Assert.Equal(expectedOwner, inference.CatalogOwnedBy);

        string dbPath = NewDbPath("direct");
        var ledger = new AllowanceLedger(dbPath, 5_000_000, "cus_direct");
        Assert.True(ledger.TryGrantPeriod("in_direct", 100, 9_999_999_999, 5_000_000));

        // Seed an unsettled upstream generation entry to prove ReconcileAsync skips OpenRouter polling.
        Assert.True(ledger.TryReserve("pending-req", 1_000));
        ledger.RecordUpstreamGeneration("pending-req", "gen-should-not-poll");

        Assert.Equal(0, await inference.ReconcileAsync(ledger, timeout.Token));
        Assert.Equal(0, capture.Calls);

        // Execute stream via LocalGateway wired with the direct provider.
        await using var gateway = LocalGateway.Create("direct-token", NewDbPath("direct-gw"), port: 0, inference: inference);
        await gateway.StartAsync(timeout.Token);
        using var client = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "direct-token");

        using var response = await client.PostAsJsonAsync("v1/chat/completions", new
        {
            model = LocalGateway.Model,
            stream = true,
            messages = new[] { new { role = "user", content = "Hi" } }
        }, timeout.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data: [DONE]", await response.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(1, capture.Calls);

        using var sent = JsonDocument.Parse(capture.LastPayload!);
        Assert.Equal(expectedModel, sent.RootElement.GetProperty("model").GetString());
        Assert.False(sent.RootElement.TryGetProperty("provider", out _));
        Assert.True(sent.RootElement.GetProperty("stream_options").GetProperty("include_usage").GetBoolean());

        using var usageDoc = JsonDocument.Parse("""{"prompt_tokens":1000000,"prompt_cache_hit_tokens":500000,"completion_tokens":100000}""");
        long? costMicros = OpenRouterInference.ResolveUsageCostMicros(usageDoc.RootElement, 1_000_000, options);
        long expectedCost = (500_000L * options.InputMicrosPerMillion / 1_000_000L)
            + (500_000L * options.CachedInputMicrosPerMillion / 1_000_000L)
            + (100_000L * options.OutputMicrosPerMillion / 1_000_000L);
        Assert.Equal(expectedCost, costMicros);
    }

    [Fact]
    public async Task StripeModeStrictness_EnforcesLiveAndSandboxBoundaries()
    {
        using var http = new HttpClient(new StubStripeHandler(liveMode: true));

        // Sandbox constructor must reject live Stripe keys.
        Assert.Throws<ArgumentException>(() => new StripeTestCheckout("sk_live_fixture", http, allowLiveMode: false));

        var sandboxStripe = new StripeTestCheckout("sk_test_fixture", http, allowLiveMode: false);
        Assert.False(sandboxStripe.IsLiveMode);

        var liveStripe = new StripeTestCheckout("sk_live_fixture", http, allowLiveMode: true);
        Assert.True(liveStripe.IsLiveMode);

        // Live webhook handler must ignore livemode=false payloads cleanly.
        var ctxBuilder = WebApplication.CreateSlimBuilder();
        ctxBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        var app = ctxBuilder.Build();
        bool dispatched = false;
        app.MapPost("/hook", (Func<HttpContext, Task<IResult>>)(ctx =>
            StripeTestWebhook.ReadLifecycleEventAsync(ctx, "whsec_fixture", expectedLiveMode: true, _ =>
            {
                dispatched = true;
                return Task.CompletedTask;
            })));
        await app.StartAsync();
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            byte[] wrongModePayload = Encoding.UTF8.GetBytes("""{"id":"evt_test","type":"invoice.paid","livemode":false,"data":{"object":{"customer":"cus_fixture"}}}""");
            using var msg = new HttpRequestMessage(HttpMethod.Post, "/hook") { Content = new ByteArrayContent(wrongModePayload) };
            msg.Headers.Add("Stripe-Signature", SignStripeWebhook(wrongModePayload, "whsec_fixture"));
            using var res = await client.SendAsync(msg);
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
            Assert.False(dispatched);
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task AccountGateway_LiveStripeLifecycle_HandlesPaidFailedCanceledAndIdempotentRevocations()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string dbPath = NewDbPath("lifecycle");
        var emailStub = new CaptureSignInEmail();
        var stripeHttp = new HttpClient(new StubStripeHandler(liveMode: true));
        var stripe = new StripeTestCheckout("sk_live_fixture", stripeHttp, allowLiveMode: true);
        var upstream = new CaptureUpstreamHandler("""{"id":"ok","choices":[{"message":{"content":"ok"}}],"usage":{"cost":0.0001,"prompt_tokens":5,"completion_tokens":5}}""", "application/json");
        var inference = new OpenRouterInference("openrouter-fixture", new HttpClient(upstream));

        await using var gateway = AccountGateway.Create(
            new AccountGatewayOptions(dbPath, "price_fixture", "whsec_fixture", "bpc_fixture"),
            emailStub, stripe, inference, port: 0);
        await gateway.StartAsync(timeout.Token);

        string token = await SignInAsync(gateway.Urls.Single(), emailStub, "buyer@example.com", timeout.Token);
        using var authed = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Bind Stripe customer through Checkout.
        using (var checkout = await authed.PostAsync("v1/subscription/checkout", null, timeout.Token))
            Assert.Equal(HttpStatusCode.OK, checkout.StatusCode);

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long end = now + 2_592_000;

        // 1. Wrong-mode (livemode:false) webhook must not activate a live gateway subscription.
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            BuildPaidInvoiceEvent("evt_wrong_mode", "in_wrong", "cus_fixture", "price_fixture", now, end, liveMode: false), timeout.Token);
        var subAfterTestEvt = await authed.GetFromJsonAsync<GatewaySubSnapshot>("v1/subscription", timeout.Token);
        Assert.Equal("none", subAfterTestEvt!.BillingStatus);
        Assert.False(subAfterTestEvt.Sandbox);
        Assert.Equal(0, subAfterTestEvt.Balance.AllowanceMicros);

        // 2. Live invoice.paid activates $5.25 allowance and status = "active".
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            BuildPaidInvoiceEvent("evt_paid_1", "in_live_1", "cus_fixture", "price_fixture", now, end, liveMode: true), timeout.Token);
        var subPaid = await authed.GetFromJsonAsync<GatewaySubSnapshot>("v1/subscription", timeout.Token);
        Assert.Equal("active", subPaid!.BillingStatus);
        Assert.False(subPaid.Sandbox);
        Assert.Equal(5_250_000, subPaid.Balance.AvailableMicros);

        // 3. invoice.payment_failed flags payment_failed while keeping existing paid balance usable.
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            """{"id":"evt_fail_1","type":"invoice.payment_failed","livemode":true,"data":{"object":{"customer":"cus_fixture"}}}""", timeout.Token);
        var subFailed = await authed.GetFromJsonAsync<GatewaySubSnapshot>("v1/subscription", timeout.Token);
        Assert.Equal("payment_failed", subFailed!.BillingStatus);
        Assert.Equal(5_250_000, subFailed.Balance.AvailableMicros);

        // 4. charge.refunded revokes active allowance idempotently and sets billingStatus = "revoked".
        string refundEvent = """{"id":"evt_refund_1","type":"charge.refunded","livemode":true,"data":{"object":{"customer":"cus_fixture"}}}""";
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture", refundEvent, timeout.Token);
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture", refundEvent, timeout.Token);
        var subRevoked = await authed.GetFromJsonAsync<GatewaySubSnapshot>("v1/subscription", timeout.Token);
        Assert.Equal("revoked", subRevoked!.BillingStatus);
        Assert.Equal(0, subRevoked.Balance.AvailableMicros);

        // 5. Subsequent renewal invoice.paid + customer.subscription.deleted cancels at ended_at.
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            BuildPaidInvoiceEvent("evt_paid_2", "in_live_2", "cus_fixture", "price_fixture", now, end, liveMode: true), timeout.Token);
        long endedAt = now + 5;
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            JsonSerializer.Serialize(new
            {
                id = "evt_cancel_1",
                type = "customer.subscription.deleted",
                livemode = true,
                data = new { @object = new { customer = "cus_fixture", ended_at = endedAt } }
            }), timeout.Token);
        var subCanceled = await authed.GetFromJsonAsync<GatewaySubSnapshot>("v1/subscription", timeout.Token);
        Assert.Equal("canceled", subCanceled!.BillingStatus);

        // 6. charge.dispute.created resolves customer via /v1/charges/{ch_...} when customer is omitted on the dispute object.
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            BuildPaidInvoiceEvent("evt_paid_3", "in_live_3", "cus_fixture", "price_fixture", now, end, liveMode: true), timeout.Token);
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            """{"id":"evt_dispute_1","type":"charge.dispute.created","livemode":true,"data":{"object":{"charge":"ch_fixture"}}}""", timeout.Token);
        var subDisputed = await authed.GetFromJsonAsync<GatewaySubSnapshot>("v1/subscription", timeout.Token);
        Assert.Equal("revoked", subDisputed!.BillingStatus);
        Assert.Equal(0, subDisputed.Balance.AvailableMicros);
    }

    [Fact]
    public async Task AccountGateway_DuplicateCheckoutLock_ReusesOpenUrlAndClearsAfterPayment()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        string dbPath = NewDbPath("checkout-lock");
        var emailStub = new CaptureSignInEmail();
        var stripeHandler = new StubStripeHandler(liveMode: true);
        var stripe = new StripeTestCheckout("sk_live_fixture", new HttpClient(stripeHandler), allowLiveMode: true);
        var inference = new OpenRouterInference("openrouter-fixture", new HttpClient(new CaptureUpstreamHandler("{}", "application/json")));

        await using var gateway = AccountGateway.Create(
            new AccountGatewayOptions(dbPath, "price_fixture", "whsec_fixture", "bpc_fixture"),
            emailStub, stripe, inference, port: 0);
        await gateway.StartAsync(timeout.Token);

        string token = await SignInAsync(gateway.Urls.Single(), emailStub, "doubleclick@example.com", timeout.Token);
        using var authed = new HttpClient { BaseAddress = new Uri(gateway.Urls.Single()) };
        authed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var first = await authed.PostAsync("v1/subscription/checkout", null, timeout.Token);
        using var second = await authed.PostAsync("v1/subscription/checkout", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(1, stripeHandler.CheckoutSessionCalls);

        using var doc1 = JsonDocument.Parse(await first.Content.ReadAsStringAsync(timeout.Token));
        using var doc2 = JsonDocument.Parse(await second.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal(doc1.RootElement.GetProperty("url").GetString(), doc2.RootElement.GetProperty("url").GetString());

        // Invoice paid clears lock (and active period prevents Checkout until period ends).
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            BuildPaidInvoiceEvent("evt_lock_paid", "in_lock_paid", "cus_fixture", "price_fixture", now, now + 86_400, liveMode: true), timeout.Token);

        // Simulate period cancellation at now - 1 so user can re-subscribe cleanly in next cycle.
        await PostSignedWebhookAsync(authed, StripeTestWebhook.LivePath, "whsec_fixture",
            """{"id":"evt_lock_refund","type":"charge.refunded","livemode":true,"data":{"object":{"customer":"cus_fixture"}}}""", timeout.Token);

        using var third = await authed.PostAsync("v1/subscription/checkout", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, third.StatusCode);
        Assert.Equal(2, stripeHandler.CheckoutSessionCalls);
    }

    [Fact]
    public void AccountDatabaseBackup_RunBackupCycle_VerifiesIntegrityScrubsSecretsAndPrunesRetention()
    {
        string dbPath = NewDbPath("backup-src");
        string backupDir = Path.Combine(_tempRoot, "backups");
        var clock = new AdvancingClock(new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero));

        var store = new AxiomAccountStore(dbPath, clock);
        var ledger = new AllowanceLedger(dbPath, 5_000_000, "cus_bkp", clock);
        string? code = store.IssueCode("user@example.com");
        Assert.NotNull(code);
        var session = store.CompleteSignIn("user@example.com", code!);
        Assert.NotNull(session);
        var account = store.Authenticate(session!.Token);
        Assert.NotNull(account);
        store.SaveActiveCheckoutUrl(account!.Id, "https://checkout.stripe.com/c/pay/cs_test", TimeSpan.FromMinutes(25));
        store.IssueCode("pending@example.com");

        for (int i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            AccountDatabaseBackup.RunBackupCycle(dbPath, backupDir, retentionCount: 2, clock);
        }

        string[] files = Directory.GetFiles(backupDir, "axiom-accounts-*.sqlite");
        Assert.Equal(2, files.Length);

        string latest = files.OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase).First();
        using var conn = new SqliteConnection($"Data Source={latest};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", cmd.ExecuteScalar() as string);

        cmd.CommandText = "SELECT COUNT(*) FROM axiom_account;";
        Assert.Equal(1L, Convert.ToInt64(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT COUNT(*) FROM axiom_account_session;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT COUNT(*) FROM axiom_email_challenge;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));

        cmd.CommandText = "SELECT COUNT(*) FROM axiom_checkout_lock;";
        Assert.Equal(0L, Convert.ToInt64(cmd.ExecuteScalar()));
    }

    [Theory]
    [InlineData(false, "payment_failed")]
    [InlineData(true, "active")]
    public async Task OpenRouterChatService_ReadAxiomSubscriptionDetailed_AcceptsLiveAndSandboxAndBillingStatus(
        bool sandbox, string billingStatus)
    {
        string payload = JsonSerializer.Serialize(new
        {
            service = "axiom-managed-inference",
            sandbox,
            monthlyUsd = 9,
            modelLabel = "DeepSeek 4.1F-1017",
            billingStatus,
            balance = new { allowanceMicros = 5_000_000, spentMicros = 125_000, reservedMicros = 0, availableMicros = 4_875_000 },
            renewsAt = DateTimeOffset.UtcNow.AddDays(30)
        });
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapGet("/v1/subscription", () => Results.Content(payload, "application/json"));
        await app.StartAsync();

        var desktop = new OpenRouterChatService();
        desktop.SetManagedEndpoint(new Uri(new Uri(app.Urls.Single()), "v1").AbsoluteUri, "managed-token");

        var detailed = await desktop.ReadAxiomSubscriptionDetailedAsync();
        Assert.False(detailed.Unauthorized);
        Assert.NotNull(detailed.Status);
        Assert.Equal(sandbox, detailed.Status!.Sandbox);
        Assert.Equal(billingStatus, detailed.Status.BillingStatus);
        Assert.Equal(4_875_000, detailed.Status.Balance.AvailableMicros);
    }

    [Fact]
    public void CloudUpgradeSuggestionPolicy_EvaluatesContextualTriggersAndRespectsDismissal()
    {
        var slowStats = new GenerationStatsSnapshot(OutputTokens: 32, TokensAreEstimated: false, TokensPerSecond: 4.0, ElapsedSeconds: 8.0, FirstTokenSeconds: 0.2);
        var tooShortStats = new GenerationStatsSnapshot(OutputTokens: 12, TokensAreEstimated: false, TokensPerSecond: 4.0, ElapsedSeconds: 3.0, FirstTokenSeconds: 0.2);
        var fastStats = new GenerationStatsSnapshot(OutputTokens: 80, TokensAreEstimated: false, TokensPerSecond: 40.0, ElapsedSeconds: 2.0, FirstTokenSeconds: 0.1);

        var slowHit = CloudUpgradeSuggestionPolicy.TryEvaluateSlowLocal(isCloudMode: false, hasPaidAxiomSubscription: false, alreadyDismissed: false, slowStats);
        Assert.NotNull(slowHit);
        Assert.Equal(CloudUpgradeTriggerKind.SlowLocalGeneration, slowHit!.Kind);

        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateSlowLocal(false, false, false, tooShortStats));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateSlowLocal(false, false, false, fastStats));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateSlowLocal(true, false, false, slowStats));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateSlowLocal(false, true, false, slowStats));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateSlowLocal(false, false, true, slowStats));

        var byokHit = CloudUpgradeSuggestionPolicy.TryEvaluateByokExhausted(isCustomEndpoint: false, hasPaidAxiomSubscription: false, alreadyDismissed: false, isDailyLimit: true);
        Assert.NotNull(byokHit);
        Assert.Equal(CloudUpgradeTriggerKind.ByokQuotaExhausted, byokHit!.Kind);
        Assert.Contains("daily cap", byokHit.Detail, StringComparison.OrdinalIgnoreCase);

        var creditHit = CloudUpgradeSuggestionPolicy.TryEvaluateByokExhausted(false, false, false, isDailyLimit: false);
        Assert.NotNull(creditHit);
        Assert.Contains("credits", creditHit!.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateByokExhausted(true, false, false, true));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateByokExhausted(false, true, false, true));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateByokExhausted(false, false, true, true));

        var visionHit = CloudUpgradeSuggestionPolicy.TryEvaluateLocalVisionMissing(isCloudMode: false, hasPaidAxiomSubscription: false, alreadyDismissed: false, hasLocalVisionProjector: false);
        Assert.NotNull(visionHit);
        Assert.Equal(CloudUpgradeTriggerKind.LocalVisionUnavailable, visionHit!.Kind);
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateLocalVisionMissing(false, false, false, hasLocalVisionProjector: true));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateLocalVisionMissing(false, true, false, false));
        Assert.Null(CloudUpgradeSuggestionPolicy.TryEvaluateLocalVisionMissing(false, false, true, false));
    }

    [Fact]
    public async Task StripeProvisionLive_RequiresLiveKeyAndProvisionsPricePortalAndWebhook()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var handler = new StubStripeHandler(liveMode: true);
        using var http = new HttpClient(handler);

        var sandbox = new StripeTestCheckout("sk_test_fixture", http, allowLiveMode: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sandbox.ProvisionLiveAsync(new Uri("https://api.example.com"), timeout.Token));

        var live = new StripeTestCheckout("sk_live_fixture", http, allowLiveMode: true);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            live.ProvisionLiveAsync(new Uri("http://api.example.com"), timeout.Token));

        var result = await live.ProvisionLiveAsync(new Uri("https://api.example.com"), timeout.Token);
        Assert.Equal("price_live_fixture", result.PriceId);
        Assert.Equal("bpc_live_fixture", result.PortalConfigurationId);
        Assert.Equal("we_live_fixture", result.WebhookEndpointId);
        Assert.Equal("whsec_live_provisioned_fixture", result.WebhookSecret);
        Assert.Contains("v1/prices", handler.RequestedPaths);
        Assert.Contains("v1/billing_portal/configurations", handler.RequestedPaths);
        Assert.Contains("v1/webhook_endpoints", handler.RequestedPaths);
        Assert.Contains("https%3A%2F%2Fapi.example.com%2Fstripe%2Fwebhook", handler.LastWebhookBody!);
        Assert.Contains("invoice.paid", handler.LastWebhookBody);
        Assert.Contains("invoice.payment_failed", handler.LastWebhookBody);
        Assert.Contains("customer.subscription.deleted", handler.LastWebhookBody);
        Assert.Contains("charge.refunded", handler.LastWebhookBody);
        Assert.Contains("charge.dispute.created", handler.LastWebhookBody);
    }

    private string NewDbPath(string prefix) => Path.Combine(_tempRoot, $"{prefix}-{Guid.NewGuid():N}.sqlite");

    private static async Task<string> SignInAsync(string baseUrl, CaptureSignInEmail emailStub, string address, CancellationToken ct)
    {
        using var anon = new HttpClient { BaseAddress = new Uri(baseUrl) };
        using (var begin = await anon.PostAsJsonAsync("v1/accounts/request-code", new { email = address }, ct))
            begin.EnsureSuccessStatusCode();

        string? code = emailStub.LastCodeFor(address);
        Assert.False(string.IsNullOrWhiteSpace(code));

        using var complete = await anon.PostAsJsonAsync("v1/accounts/sign-in", new { email = address, code }, ct);
        complete.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await complete.Content.ReadAsStringAsync(ct));
        return doc.RootElement.GetProperty("token").GetString()!;
    }

    private static async Task PostSignedWebhookAsync(HttpClient client, string path, string signingSecret, string jsonPayload, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(jsonPayload);
        using var req = new HttpRequestMessage(HttpMethod.Post, path.TrimStart('/'))
        {
            Content = new ByteArrayContent(bytes)
        };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Add("Stripe-Signature", SignStripeWebhook(bytes, signingSecret));
        using var res = await client.SendAsync(req, ct);
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    private static string SignStripeWebhook(byte[] body, string signingSecret)
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        byte[] prefix = Encoding.UTF8.GetBytes($"{ts}.");
        byte[] signedPayload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedPayload, 0);
        body.CopyTo(signedPayload, prefix.Length);
        string sig = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), signedPayload));
        return $"t={ts},v1={sig}";
    }

    private static string BuildPaidInvoiceEvent(string eventId, string invoiceId, string customerId, string priceId,
        long periodStart, long periodEnd, bool liveMode)
        => $$"""
        {
          "id": "{{eventId}}",
          "type": "invoice.paid",
          "livemode": {{(liveMode ? "true" : "false")}},
          "data": {
            "object": {
              "id": "{{invoiceId}}",
              "livemode": {{(liveMode ? "true" : "false")}},
              "status": "paid",
              "customer": "{{customerId}}",
              "billing_reason": "subscription_create",
              "parent": { "subscription_details": { "subscription": "sub_fixture" } },
              "lines": {
                "data": [
                  {
                    "period": { "start": {{periodStart}}, "end": {{periodEnd}} },
                    "pricing": { "price_details": { "price": "{{priceId}}" } },
                    "parent": { "subscription_item_details": { "proration": false } }
                  }
                ]
              }
            }
          }
        }
        """;

    private sealed record GatewaySubSnapshot(string Service, bool Sandbox, string BillingStatus, AllowanceBalance Balance);

    private sealed class CaptureSignInEmail : IAxiomSignInEmail
    {
        private readonly Dictionary<string, string> _codes = new(StringComparer.OrdinalIgnoreCase);

        public Task SendCodeAsync(string email, string code, CancellationToken cancellationToken)
        {
            _codes[email] = code;
            return Task.CompletedTask;
        }

        public string? LastCodeFor(string email) => _codes.TryGetValue(email, out string? code) ? code : null;
    }

    private sealed class StubStripeHandler(bool liveMode) : HttpMessageHandler
    {
        private int _checkoutSessionCalls;
        public int CheckoutSessionCalls => _checkoutSessionCalls;
        public List<string> RequestedPaths { get; } = [];
        public string? LastWebhookBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri?.AbsolutePath ?? string.Empty;
            RequestedPaths.Add(path.TrimStart('/'));
            string liveJson = liveMode ? "true" : "false";
            if (path.EndsWith("/prices", StringComparison.Ordinal))
            {
                return JsonResponse($$"""{"id":"price_live_fixture","livemode":{{liveJson}}}""");
            }
            if (path.EndsWith("/billing_portal/configurations", StringComparison.Ordinal))
            {
                return JsonResponse($$"""{"id":"bpc_live_fixture","livemode":{{liveJson}}}""");
            }
            if (path.EndsWith("/webhook_endpoints", StringComparison.Ordinal))
            {
                LastWebhookBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                return JsonResponse($$"""{"id":"we_live_fixture","livemode":{{liveJson}},"secret":"whsec_live_provisioned_fixture"}""");
            }
            if (path.EndsWith("/customers", StringComparison.Ordinal))
            {
                return JsonResponse($$"""{"id":"cus_fixture","livemode":{{liveJson}}}""");
            }
            if (path.EndsWith("/checkout/sessions", StringComparison.Ordinal))
            {
                int n = Interlocked.Increment(ref _checkoutSessionCalls);
                return JsonResponse($$"""{"id":"cs_fixture_{{n}}","livemode":{{liveJson}},"url":"https://checkout.stripe.com/c/pay/cs_fixture_{{n}}"}""");
            }
            if (path.Contains("/charges/", StringComparison.Ordinal))
            {
                return JsonResponse($$"""{"id":"ch_fixture","livemode":{{liveJson}},"customer":"cus_fixture"}""");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class CaptureUpstreamHandler(string responseBody, string mediaType) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? LastPayload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastPayload = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, mediaType)
            };
        }
    }

    private sealed class StaticJsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private sealed class AdvancingClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset _now = initial;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
