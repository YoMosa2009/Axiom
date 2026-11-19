using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AxiomSubscriptionGateway;
using Xunit;

namespace Malx_AI.Tests;

public sealed class SubscriptionGatewayTests : IDisposable
{
    private const string TestToken = "local-gateway-integration-test";
    private const long TestAllowance = LocalGateway.ReservationMicros;
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"axiom-gateway-{Guid.NewGuid():N}.sqlite");

    [Fact]
    public async Task DesktopCloudClientStreamsReplyAndShowsCustomEndpointExhaustion()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var gateway = LocalGateway.Create(TestToken, _database, port: 0, TestAllowance);
        await gateway.StartAsync(timeout.Token);
        using var http = AuthenticatedClient(gateway.Urls.Single());

        var client = new OpenRouterChatService();
        client.SetCustomEndpoint(new Uri(http.BaseAddress!, "v1").AbsoluteUri, TestToken, LocalGateway.Model);
        var chunks = new List<string>();

        var reply = await client.SendConversationStreamAsync(
            [new OpenRouterMessage("user", "Hello")],
            systemPrompt: "Reply to the user.",
            modelId: OpenRouterChatService.CustomEndpointModelId,
            onToken: chunks.Add,
            cancellationToken: timeout.Token,
            requireCompleteResponse: true);

        Assert.Equal(LocalGateway.MockReply, reply.Text);
        Assert.Equal(LocalGateway.MockReply, string.Concat(chunks));
        Assert.True(chunks.Count > 1);
        Assert.Equal(new OpenRouterTokenUsage(20_000, 5_000, 25_000), reply.Usage);

        var balance = await http.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token);
        Assert.Equal(new AllowanceBalance(TestAllowance, LocalGateway.MockCostMicros, 0), balance);
        var exhausted = await Assert.ThrowsAsync<OpenRouterKeyExhaustedException>(() =>
            client.SendConversationStreamAsync(
                [new OpenRouterMessage("user", "Hello again")],
                modelId: OpenRouterChatService.CustomEndpointModelId,
                cancellationToken: timeout.Token));
        Assert.True(exhausted.IsCustomEndpoint);
        Assert.Contains("usage allowance", exhausted.Message);
    }

    [Fact]
    public async Task ConcurrentRequestCannotSpendAnotherRequestsReservation()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var upstreamStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishUpstream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = LocalGateway.Create(TestToken, _database, port: 0, TestAllowance, async token =>
        {
            upstreamStarted.TrySetResult();
            await finishUpstream.Task.WaitAsync(token);
        });
        await gateway.StartAsync(timeout.Token);
        using var http = AuthenticatedClient(gateway.Urls.Single());
        var request = new { model = LocalGateway.Model, messages = new[] { new { role = "user", content = "Hello" } } };

        Task<HttpResponseMessage> first = http.PostAsJsonAsync("v1/chat/completions", request, timeout.Token);
        try
        {
            await upstreamStarted.Task.WaitAsync(timeout.Token);
            Assert.Equal(new AllowanceBalance(TestAllowance, 0, TestAllowance),
                await http.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));
            using var second = await http.PostAsJsonAsync("v1/chat/completions", request, timeout.Token);
            Assert.Equal(HttpStatusCode.PaymentRequired, second.StatusCode);
            using var error = JsonDocument.Parse(await second.Content.ReadAsStringAsync(timeout.Token));
            Assert.Equal("axiom_allowance_exhausted", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        }
        finally
        {
            finishUpstream.TrySetResult();
        }

        using var completed = await first;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(new AllowanceBalance(TestAllowance, LocalGateway.MockCostMicros, 0),
            await http.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));
    }

    [Fact]
    public void LedgerPersistsBalanceAndSettlesRequestsOnlyOnce()
    {
        var ledger = new AllowanceLedger(_database, TestAllowance);
        Assert.True(ledger.TryReserve("request-1", TestAllowance));
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => ledger.TrySettle("request-1", TestAllowance + 1));
        Assert.Equal(new AllowanceBalance(TestAllowance, 0, TestAllowance), ledger.GetBalance());
        Assert.True(ledger.TrySettle("request-1", LocalGateway.MockCostMicros));
        Assert.False(ledger.TrySettle("request-1", LocalGateway.MockCostMicros));

        var reopened = new AllowanceLedger(_database, TestAllowance * 2);
        Assert.Equal(new AllowanceBalance(TestAllowance, LocalGateway.MockCostMicros, 0), reopened.GetBalance());
    }

    [Fact]
    public async Task StripeTestPaidInvoiceGrantsAllowanceOnceAndRejectsUntrustedEvents()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stripe = new StripeTestWebhook("cus_fixture", "price_fixture", "whsec_test_fixture");
        using var stripeApi = new HttpClient(new StripeCheckoutFixture());
        var checkout = new StripeTestCheckout("sk_test_fixture", stripeApi);
        await using var gateway = LocalGateway.Create(TestToken, _database, port: 0, TestAllowance,
            stripeWebhook: stripe, stripeCheckout: checkout);
        await gateway.StartAsync(timeout.Token);
        using var client = AuthenticatedClient(gateway.Urls.Single());
        using var stripeClient = new HttpClient { BaseAddress = client.BaseAddress };
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var billingPeriod = new { start = now - 60, end = now + 2_592_000 };

        string InvoiceEvent(string type, string priceId = "price_fixture", bool live = false,
            string invoiceId = "in_fixture") => JsonSerializer.Serialize(new
        {
            type,
            livemode = live,
            data = new
            {
                @object = new
                {
                    id = invoiceId, livemode = live, status = "paid", customer = stripe.CustomerId,
                    billing_reason = "subscription_create",
                    parent = new { subscription_details = new { subscription = "sub_fixture" } },
                    lines = new
                    {
                        data = new[]
                        {
                            new
                            {
                                period = billingPeriod,
                                parent = new { subscription_item_details = new { proration = false } },
                                pricing = new { price_details = new { price = priceId } }
                            }
                        }
                    }
                }
            }
        });

        async Task<HttpStatusCode> DeliverAsync(string body, string secret, long timestamp)
        {
            string stamp = timestamp.ToString(CultureInfo.InvariantCulture);
            byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(stamp + "." + body));
            using var request = new HttpRequestMessage(HttpMethod.Post, StripeTestWebhook.Path)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("Stripe-Signature", $"t={stamp},v1={Convert.ToHexString(signature)}");
            using var response = await stripeClient.SendAsync(request, timeout.Token);
            return response.StatusCode;
        }

        string paid = InvoiceEvent("invoice.paid");
        using var anonymousCheckout = await stripeClient.PostAsync("/stripe/test/checkout", null, timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousCheckout.StatusCode);
        using var initialCheckout = await client.PostAsync("/stripe/test/checkout", null, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, initialCheckout.StatusCode);
        using var checkoutResult = JsonDocument.Parse(await initialCheckout.Content.ReadAsStringAsync(timeout.Token));
        Assert.Equal("https://checkout.stripe.com/c/pay/cs_test_fixture",
            checkoutResult.RootElement.GetProperty("url").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, await DeliverAsync(paid, "wrong-secret", now));
        Assert.Equal(HttpStatusCode.BadRequest, await DeliverAsync(paid, stripe.SigningSecret, now - 600));
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(InvoiceEvent("invoice.payment_failed"), stripe.SigningSecret, now));
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(InvoiceEvent("invoice.paid", priceId: "price_other"), stripe.SigningSecret, now));
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(InvoiceEvent("invoice.paid", live: true), stripe.SigningSecret, now));
        Assert.Equal(new AllowanceBalance(0, 0, 0),
            await client.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));

        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(paid, stripe.SigningSecret, now));
        using var duplicateCheckout = await client.PostAsync("/stripe/test/checkout", null, timeout.Token);
        Assert.Equal(HttpStatusCode.Conflict, duplicateCheckout.StatusCode);
        var chat = new { model = LocalGateway.Model, messages = new[] { new { role = "user", content = "Hello" } } };
        using var completion = await client.PostAsJsonAsync("v1/chat/completions", chat, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, completion.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(paid, stripe.SigningSecret, now));
        Assert.Equal(HttpStatusCode.NoContent, await DeliverAsync(InvoiceEvent("invoice.paid", invoiceId: "in_overlap"), stripe.SigningSecret, now));
        Assert.Equal(new AllowanceBalance(TestAllowance, LocalGateway.MockCostMicros, 0),
            await client.GetFromJsonAsync<AllowanceBalance>("v1/balance", timeout.Token));
    }

    public void Dispose() => File.Delete(_database);

    private static HttpClient AuthenticatedClient(string address)
    {
        var http = new HttpClient { BaseAddress = new Uri(address) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        return http;
    }

    private sealed class StripeCheckoutFixture : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.stripe.com/v1/checkout/sessions", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer sk_test_fixture", request.Headers.Authorization?.ToString());
            Assert.Equal("axiom-local-checkout-cus_fixture-price_fixture", request.Headers.GetValues("Idempotency-Key").Single());
            string form = WebUtility.UrlDecode(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Contains("mode=subscription", form);
            Assert.Contains("customer=cus_fixture", form);
            Assert.Contains("line_items[0][price]=price_fixture", form);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"livemode":false,"url":"https://checkout.stripe.com/c/pay/cs_test_fixture"}""")
            };
        }
    }
}
