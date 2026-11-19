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

    public StripeTestCheckout(string secretKey, HttpClient? http = null)
    {
        if (!secretKey.StartsWith("sk_test_", StringComparison.Ordinal)
            && !secretKey.StartsWith("rk_test_", StringComparison.Ordinal))
            throw new ArgumentException("The local gateway only accepts Stripe test API keys.", nameof(secretKey));
        _secretKey = secretKey;
        _http = http ?? StripeHttp;
    }

    public static StripeTestCheckout? FromEnvironment()
    {
        string? key = Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_SECRET_KEY");
        return key is null ? null : new StripeTestCheckout(key);
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

        return new StripeTestWebhook(customer, price, null, portal);
    }

    public async Task<IResult> CreateSessionAsync(
        StripeTestWebhook billing, AllowanceLedger ledger, int port, CancellationToken cancellationToken,
        Uri? publicOrigin = null)
    {
        if (ledger.GetBalance().AllowanceMicros > 0)
            return Results.Conflict(new { error = "This test customer already has a paid period." });

        string returnBase = ReturnOrigin(port, publicOrigin) + "/stripe/test/checkout";
        using var request = CreateRequest(HttpMethod.Post, "checkout/sessions");
        // One open Checkout attempt per configured test customer and price. Retries within
        // Stripe's idempotency window return the same session instead of creating a second one.
        request.Headers.Add("Idempotency-Key", $"axiom-local-checkout-{billing.CustomerId}-{billing.PriceId}");
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
                if (ReadTestUrl(json.RootElement, "checkout.stripe.com") is { } checkout)
                    return Results.Ok(new { url = checkout.AbsoluteUri });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Keep the gateway's error free of upstream request and credential details.
        }

        return Results.Json(new { error = "Stripe could not create a test Checkout session." }, statusCode: 502);
    }

    public async Task<IResult> CreatePortalAsync(StripeTestWebhook billing, int port, CancellationToken cancellationToken,
        Uri? publicOrigin = null)
    {
        using var request = CreateRequest(HttpMethod.Post, "billing_portal/sessions");
        var fields = new Dictionary<string, string>
        {
            ["customer"] = billing.CustomerId,
            ["return_url"] = ReturnOrigin(port, publicOrigin) + "/stripe/test/portal/return"
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
                if (ReadTestUrl(doc.RootElement, "billing.stripe.com") is { } portal)
                    return Results.Ok(new { url = portal.AbsoluteUri });
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Keep private upstream details on the server.
        }

        return Results.Json(new { error = "Stripe could not open the sandbox billing portal." }, statusCode: 502);
    }

    public Task<string> CreateCustomerAsync(AxiomAccount account, CancellationToken cancellationToken)
        => CreateTestEntityAsync("customers", "cus_", $"axiom-account-customer-{account.Id}",
            new Dictionary<string, string>
            {
                ["email"] = account.Email,
                ["metadata[axiom_account_id]"] = account.Id
            }, cancellationToken);

    public async Task VerifyMonthlyPriceAsync(string priceId, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "prices/" + Uri.EscapeDataString(priceId));
        using var response = await _http.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var price = doc.RootElement;
            if (price.TryGetProperty("id", out var id) && id.GetString() == priceId
                && price.TryGetProperty("livemode", out var live) && live.ValueKind == JsonValueKind.False
                && price.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True
                && price.TryGetProperty("currency", out var currency) && currency.GetString() == "usd"
                && price.TryGetProperty("unit_amount", out var amount) && amount.TryGetInt32(out int cents) && cents == 900
                && price.TryGetProperty("recurring", out var recurring) && recurring.ValueKind == JsonValueKind.Object
                && recurring.TryGetProperty("interval", out var interval) && interval.GetString() == "month"
                && recurring.TryGetProperty("interval_count", out var intervalCount) && intervalCount.TryGetInt32(out int months) && months == 1)
                return;
        }
        throw new InvalidOperationException("The configured Stripe price must be an active $9 USD monthly sandbox price.");
    }

    private static string ReturnOrigin(int port, Uri? publicOrigin)
        => publicOrigin?.GetLeftPart(UriPartial.Authority) ?? $"http://localhost:{port}";

    public async Task<IResult> SynchronizeAsync(
        StripeTestWebhook billing, AllowanceLedger ledger, long allowanceMicros, CancellationToken cancellationToken)
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
                            applied++;
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

    private static Uri? ReadTestUrl(JsonElement root, string expectedHost)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("livemode", out var live)
            && live.ValueKind == JsonValueKind.False
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
            throw new InvalidOperationException($"Stripe sandbox {resource} returned HTTP {(int)response.StatusCode}.");

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
        if (doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("livemode", out var live)
            && live.ValueKind == JsonValueKind.False
            && doc.RootElement.TryGetProperty("id", out var id)
            && id.ValueKind == JsonValueKind.String
            && id.GetString() is { } value
            && value.StartsWith(idPrefix, StringComparison.Ordinal))
            return value;

        throw new InvalidOperationException($"Stripe did not confirm a test-mode {resource} object.");
    }
}
