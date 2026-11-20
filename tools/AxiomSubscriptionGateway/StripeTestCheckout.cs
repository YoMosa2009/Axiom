using System.Net.Http.Headers;
using System.Text.Json;

namespace AxiomSubscriptionGateway;

internal sealed class StripeTestCheckout
{
    private const string Api = "https://api.stripe.com/v1/";
    private const string Version = "2025-06-30.basil";
    private static readonly HttpClient StripeHttp = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    private readonly string _secretKey;
    private readonly HttpClient _http;

    public bool IsLiveMode { get; }

    public StripeTestCheckout(string secretKey, HttpClient? http = null, bool allowLiveMode = false)
    {
        bool isLive = secretKey.StartsWith("sk_live_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_live_", StringComparison.Ordinal);
        bool isTest = secretKey.StartsWith("sk_test_", StringComparison.Ordinal)
            || secretKey.StartsWith("rk_test_", StringComparison.Ordinal);
        if (!isTest && !(allowLiveMode && isLive))
            throw new ArgumentException("Configure a valid Stripe API key matching the gateway mode.", nameof(secretKey));
        _secretKey = secretKey;
        _http = http ?? StripeHttp;
        IsLiveMode = isLive;
    }

    public static StripeTestCheckout? FromEnvironment(bool allowLiveMode = false)
    {
        if (allowLiveMode && Environment.GetEnvironmentVariable("AXIOM_STRIPE_SECRET_KEY") is { Length: > 0 } configured)
            return new StripeTestCheckout(configured, allowLiveMode: true);
        string? key = Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_SECRET_KEY");
        return key is null ? null : new StripeTestCheckout(key, allowLiveMode: allowLiveMode);
    }

    public async Task<StripeTestWebhook> ProvisionAsync(string runId, CancellationToken cancellationToken)
    {
        string price = await CreateTestEntityAsync("prices", "price_", $"axiom-sandbox-price-{runId}",
            new Dictionary<string, string>
            {
                ["product_data[name]"] = "Axiom managed inference (sandbox)",
                ["currency"] = "usd",
                ["unit_amount"] = "900",
                ["recurring[interval]"] = "month",
                ["metadata[axiom_sandbox_run]"] = runId
            }, cancellationToken);

        string customer = await CreateTestEntityAsync("customers", "cus_", $"axiom-sandbox-customer-{runId}",
            new Dictionary<string, string>
            {
                ["name"] = "Axiom sandbox subscriber",
                ["metadata[axiom_sandbox_run]"] = runId
            }, cancellationToken);

        string portal = await CreateTestEntityAsync("billing_portal/configurations", "bpc_", $"axiom-sandbox-portal-{runId}",
            new Dictionary<string, string>
            {
                ["features[invoice_history][enabled]"] = "true",
                ["features[payment_method_update][enabled]"] = "true",
                ["features[subscription_cancel][enabled]"] = "true",
                ["features[subscription_cancel][mode]"] = "at_period_end",
                ["features[subscription_cancel][proration_behavior]"] = "none",
                ["metadata[axiom_sandbox_run]"] = runId
            }, cancellationToken);

        return new StripeTestWebhook(customer, price, null, portal, IsLiveMode);
    }

    public async Task<LiveStripeProvisionResult> ProvisionLiveAsync(Uri publicOrigin, CancellationToken cancellationToken)
    {
        if (!IsLiveMode)
            throw new InvalidOperationException("Live provisioning requires a Stripe live secret key (sk_live_ or rk_live_).");
        if (publicOrigin.Scheme != Uri.UriSchemeHttps
            || publicOrigin.AbsolutePath != "/"
            || !string.IsNullOrEmpty(publicOrigin.Query)
            || !string.IsNullOrEmpty(publicOrigin.Fragment)
            || !string.IsNullOrEmpty(publicOrigin.UserInfo))
            throw new ArgumentException("Public origin must be a clean HTTPS origin (for example https://api.example.com).", nameof(publicOrigin));

        string price = await CreateTestEntityAsync("prices", "price_", "axiom-live-price-9usd-v1",
            new Dictionary<string, string>
            {
                ["product_data[name]"] = "Axiom Cloud — DeepSeek 4.1F-1017",
                ["currency"] = "usd",
                ["unit_amount"] = "900",
                ["recurring[interval]"] = "month",
                ["metadata[axiom_plan]"] = "axiom_cloud_9_usd"
            }, cancellationToken);

        string portal = await CreateTestEntityAsync("billing_portal/configurations", "bpc_", "axiom-live-portal-v1",
            new Dictionary<string, string>
            {
                ["features[invoice_history][enabled]"] = "true",
                ["features[payment_method_update][enabled]"] = "true",
                ["features[subscription_cancel][enabled]"] = "true",
                ["features[subscription_cancel][mode]"] = "at_period_end",
                ["features[subscription_cancel][proration_behavior]"] = "none",
                ["metadata[axiom_plan]"] = "axiom_cloud_9_usd"
            }, cancellationToken);

        string webhookUrl = publicOrigin.GetLeftPart(UriPartial.Authority) + StripeTestWebhook.LivePath;
        using var request = CreateRequest(HttpMethod.Post, "webhook_endpoints");
        request.Headers.Add("Idempotency-Key", "axiom-live-webhook-v1");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["url"] = webhookUrl,
            ["enabled_events[0]"] = "invoice.paid",
            ["enabled_events[1]"] = "invoice.payment_failed",
            ["enabled_events[2]"] = "customer.subscription.deleted",
            ["enabled_events[3]"] = "charge.refunded",
            ["enabled_events[4]"] = "charge.dispute.created",
            ["metadata[axiom_plan]"] = "axiom_cloud_9_usd"
        });
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe webhook_endpoints returned HTTP {(int)response.StatusCode}.");

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("livemode", out var live)
            || live.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("id", out var idEl)
            || idEl.GetString() is not { } webhookId
            || !webhookId.StartsWith("we_", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Stripe did not return a live webhook endpoint.");
        }

        string? webhookSecret = root.TryGetProperty("secret", out var secretEl) && secretEl.ValueKind == JsonValueKind.String
            ? secretEl.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(webhookSecret) || !webhookSecret.StartsWith("whsec_", StringComparison.Ordinal))
            throw new InvalidOperationException("Stripe created or reused the webhook endpoint without returning a signing secret; copy the signing secret from Stripe Developers -> Webhooks.");

        return new LiveStripeProvisionResult(price, portal, webhookId, webhookSecret);
    }

    public Task<IResult> CreateSessionAsync(
        StripeTestWebhook billing, AllowanceLedger ledger, int port, CancellationToken cancellationToken,
        Uri? publicOrigin = null)
        => CreateSessionCoreAsync(billing, ledger, port, cancellationToken, publicOrigin, idempotencySuffix: null)
            .ContinueWith(task => task.Result.Result, cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    internal async Task<(IResult Result, Uri? SessionUrl)> CreateSessionWithUrlAsync(
        StripeTestWebhook billing, AllowanceLedger ledger, int port, CancellationToken cancellationToken,
        Uri? publicOrigin = null, string? idempotencySuffix = null)
        => await CreateSessionCoreAsync(billing, ledger, port, cancellationToken, publicOrigin, idempotencySuffix);

    private async Task<(IResult Result, Uri? SessionUrl)> CreateSessionCoreAsync(
        StripeTestWebhook billing, AllowanceLedger ledger, int port, CancellationToken cancellationToken,
        Uri? publicOrigin, string? idempotencySuffix)
    {
        if (ledger.GetBalance().AllowanceMicros > 0)
            return (Results.Conflict(new { error = IsLiveMode ? "This customer already has an active paid period." : "This test customer already has a paid period." }), null);

        string returnBase = ReturnOrigin(port, publicOrigin) + (IsLiveMode ? "/stripe/checkout" : "/stripe/test/checkout");
        using var request = CreateRequest(HttpMethod.Post, "checkout/sessions");
        string idempotencyKey = string.IsNullOrWhiteSpace(idempotencySuffix)
            ? $"axiom-local-checkout-{billing.CustomerId}-{billing.PriceId}"
            : $"axiom-local-checkout-{billing.CustomerId}-{billing.PriceId}-{idempotencySuffix}";
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["customer"] = billing.CustomerId,
            ["line_items[0][price]"] = billing.PriceId,
            ["line_items[0][quantity]"] = "1",
            ["success_url"] = returnBase + "/complete",
            ["cancel_url"] = returnBase + "/cancel"
        });

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                if (ReadValidatedUrl(json.RootElement, "checkout.stripe.com", IsLiveMode) is { } checkout)
                    return (Results.Ok(new { url = checkout.AbsoluteUri }), checkout);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Keep the gateway's error free of upstream request and credential details.
        }

        string modeWord = IsLiveMode ? "live" : "test";
        return (Results.Json(new { error = $"Stripe could not create a {modeWord} Checkout session." }, statusCode: 502), null);
    }

    public async Task<IResult> CreatePortalAsync(StripeTestWebhook billing, int port, CancellationToken cancellationToken,
        Uri? publicOrigin = null)
    {
        using var request = CreateRequest(HttpMethod.Post, "billing_portal/sessions");
        string returnPath = IsLiveMode ? "/stripe/portal/return" : "/stripe/test/portal/return";
        var fields = new Dictionary<string, string>
        {
            ["customer"] = billing.CustomerId,
            ["return_url"] = ReturnOrigin(port, publicOrigin) + returnPath
        };
        if (billing.PortalConfigurationId is not null)
            fields["configuration"] = billing.PortalConfigurationId;
        request.Content = new FormUrlEncodedContent(fields);
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                if (ReadValidatedUrl(doc.RootElement, "billing.stripe.com", IsLiveMode) is { } portal)
                    return Results.Ok(new { url = portal.AbsoluteUri });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Keep private upstream details on the server.
        }

        return Results.Json(new { error = "Stripe could not open the billing portal." }, statusCode: 502);
    }

    public Task<string> CreateCustomerAsync(AxiomAccount account, CancellationToken cancellationToken)
        => CreateTestEntityAsync("customers", "cus_", $"axiom-account-customer-{account.Id}",
            new Dictionary<string, string>
            {
                ["email"] = account.Email,
                ["metadata[axiom_account_id]"] = account.Id
            }, cancellationToken);

    public async Task<string?> ResolveDisputeCustomerAsync(JsonElement disputeObject, CancellationToken cancellationToken)
    {
        if (disputeObject.ValueKind == JsonValueKind.Object
            && disputeObject.TryGetProperty("charge", out var chargeElement)
            && chargeElement.ValueKind == JsonValueKind.String
            && chargeElement.GetString() is { } chargeId
            && chargeId.StartsWith("ch_", StringComparison.Ordinal))
        {
            try
            {
                using var request = CreateRequest(HttpMethod.Get, "charges/" + Uri.EscapeDataString(chargeId));
                using var response = await _http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object
                        && doc.RootElement.TryGetProperty("livemode", out var live)
                        && live.ValueKind == (IsLiveMode ? JsonValueKind.True : JsonValueKind.False)
                        && doc.RootElement.TryGetProperty("customer", out var cust)
                        && cust.ValueKind == JsonValueKind.String)
                    {
                        return cust.GetString();
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException
                || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
            }
        }
        return null;
    }

    public async Task VerifyMonthlyPriceAsync(string priceId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "prices/" + Uri.EscapeDataString(priceId));
        using var response = await _http.SendAsync(request, cancellationToken);
        JsonValueKind expectedLive = IsLiveMode ? JsonValueKind.True : JsonValueKind.False;
        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var price = doc.RootElement;
            if (price.TryGetProperty("id", out var id) && id.GetString() == priceId
                && price.TryGetProperty("livemode", out var live) && live.ValueKind == expectedLive
                && price.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True
                && price.TryGetProperty("currency", out var currency) && currency.GetString() == "usd"
                && price.TryGetProperty("unit_amount", out var amount) && amount.TryGetInt32(out int cents) && cents == 900
                && price.TryGetProperty("recurring", out var recurring) && recurring.ValueKind == JsonValueKind.Object
                && recurring.TryGetProperty("interval", out var interval) && interval.GetString() == "month"
                && recurring.TryGetProperty("interval_count", out var intervalCount) && intervalCount.TryGetInt32(out int months) && months == 1)
                return;
        }
        throw new InvalidOperationException($"The configured Stripe price must be an active $9 USD monthly {(IsLiveMode ? "live" : "sandbox")} price.");
    }

    private static string ReturnOrigin(int port, Uri? publicOrigin)
        => publicOrigin?.GetLeftPart(UriPartial.Authority) ?? $"http://localhost:{port}";

    public async Task<IResult> SynchronizeAsync(
        StripeTestWebhook billing, AllowanceLedger ledger, long allowanceMicros, CancellationToken cancellationToken,
        Action? onPeriodApplied = null)
    {
        try
        {
            using var request = CreateRequest(HttpMethod.Get,
                $"invoices?customer={Uri.EscapeDataString(billing.CustomerId)}&status=paid&limit=20");
            using var response = await _http.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var invoiceList = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
                if (invoiceList.RootElement.ValueKind == JsonValueKind.Object
                    && invoiceList.RootElement.TryGetProperty("data", out var invoices)
                    && invoices.ValueKind == JsonValueKind.Array)
                {
                    int applied = 0;
                    foreach (var invoice in invoices.EnumerateArray())
                    {
                        if (billing.TryApplyPaidInvoice(invoice, ledger, allowanceMicros))
                        {
                            applied++;
                            ledger.SetBillingStatus("active");
                            onPeriodApplied?.Invoke();
                        }
                    }
                    return Results.Ok(new { applied, balance = ledger.GetBalance() });
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // The balance remains unchanged when Stripe cannot prove the invoice.
        }
        return Results.Json(new { error = "Stripe invoices could not be verified." }, statusCode: 502);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _secretKey);
        request.Headers.Add("Stripe-Version", Version);
        return request;
    }

    private static Uri? ReadValidatedUrl(JsonElement root, string expectedHost, bool expectedLiveMode)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("livemode", out var live)
            && live.ValueKind == (expectedLiveMode ? JsonValueKind.True : JsonValueKind.False)
            && root.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var target)
            && target.Scheme == Uri.UriSchemeHttps
            && target.IsDefaultPort
            && string.IsNullOrEmpty(target.UserInfo)
            && target.Host == expectedHost ? target : null;

    private async Task<string> CreateTestEntityAsync(
        string resource, string idPrefix, string idempotencyKey, Dictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, resource);
        request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Content = new FormUrlEncodedContent(values);
        using var response = await _http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe {resource} returned HTTP {(int)response.StatusCode}.");

        JsonValueKind expectedLive = IsLiveMode ? JsonValueKind.True : JsonValueKind.False;
        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("livemode", out var live)
            && live.ValueKind == expectedLive
            && doc.RootElement.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() is { } value
            && value.StartsWith(idPrefix, StringComparison.Ordinal))
            return value;

        throw new InvalidOperationException($"Stripe did not confirm a {(IsLiveMode ? "live" : "test-mode")} {resource} object.");
    }
}

internal sealed record LiveStripeProvisionResult(
    string PriceId,
    string PortalConfigurationId,
    string WebhookEndpointId,
    string WebhookSecret);
