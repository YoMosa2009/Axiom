using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxiomSubscriptionGateway;

internal static class SubscriptionSandbox
{
    public static async Task<int> RunAsync()
    {
        string? openRouterKey = Environment.GetEnvironmentVariable("AXIOM_GATEWAY_OPENROUTER_KEY");
        string? stripeKey = Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_SECRET_KEY");
        string? databasePath = Environment.GetEnvironmentVariable("AXIOM_GATEWAY_TEST_DB");
        if (string.IsNullOrWhiteSpace(openRouterKey) || string.IsNullOrWhiteSpace(stripeKey)
            || string.IsNullOrWhiteSpace(databasePath))
        {
            Console.Error.WriteLine("The sandbox needs the OpenRouter key, Stripe test key, and a local database path.");
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        try
        {
            await VerifyAsync(openRouterKey, stripeKey, databasePath, deadline.Token);
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            Console.Error.WriteLine("The sandbox verification stopped. Review Stripe test Checkout and OpenRouter Activity before retrying.");
            return 1;
        }
    }

    private static async Task VerifyAsync(string openRouterKey, string stripeKey, string databasePath, CancellationToken cancellationToken)
    {
        var stripe = new StripeTestCheckout(stripeKey);
        using var keyCheckHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        await new OpenRouterRouteProbe(keyCheckHttp, openRouterKey.Trim()).CheckKeyLimitAsync(cancellationToken);

        string runId = Guid.NewGuid().ToString("N");
        Console.Error.WriteLine("Creating a $9/month Stripe sandbox price and a sandbox customer.");
        StripeTestWebhook billing = await stripe.ProvisionAsync(runId, cancellationToken);

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        await using var app = LocalGateway.Create(token, databasePath, port: 0, stripeWebhook: billing,
            stripeCheckout: stripe, inference: new OpenRouterInference(openRouterKey));
        await app.StartAsync(cancellationToken);
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var prompt = new
        {
            model = LocalGateway.Model, stream = true, max_tokens = 32,
            messages = new[] { new { role = "user", content = "Reply with exactly AXIOM_SUBSCRIPTION_OK." } }
        };
        using (var unpaid = await client.PostAsJsonAsync("v1/chat/completions", prompt, cancellationToken))
        {
            if (unpaid.StatusCode != HttpStatusCode.PaymentRequired)
                throw new InvalidOperationException("The gateway did not enforce the unpaid allowance.");
        }

        using var checkoutResponse = await client.PostAsync("stripe/test/checkout", null, cancellationToken);
        if (!checkoutResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe sandbox Checkout did not open (HTTP {(int)checkoutResponse.StatusCode}).");
        using var checkout = JsonDocument.Parse(await checkoutResponse.Content.ReadAsStringAsync(cancellationToken));
        string url = checkout.RootElement.GetProperty("url").GetString()!;
        Console.Error.WriteLine("Complete the Stripe test checkout in the browser using the test card 4242 4242 4242 4242.");
        Console.Error.WriteLine("Use any future expiration date, any three-digit CVC, and test details.");
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.Error.WriteLine($"Open the test Checkout URL in your browser: {url}");
        }

        AllowanceBalance before;
        do
        {
            before = await SyncAsync(client, cancellationToken);
            if (before.AllowanceMicros == 0)
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
        } while (before.AllowanceMicros == 0);

        if (before != new AllowanceBalance(5_000_000, 0, 0))
            throw new InvalidOperationException("The first paid invoice did not grant the expected $5 allowance exactly once.");

        Console.Error.WriteLine("The test invoice is paid. Checking the managed stream and recorded debit.");
        using var reply = await client.PostAsJsonAsync("v1/chat/completions", prompt, cancellationToken);
        if (!reply.IsSuccessStatusCode)
            throw new InvalidOperationException($"The managed reply failed (HTTP {(int)reply.StatusCode}).");
        string sse = await reply.Content.ReadAsStringAsync(cancellationToken);
        if (!HasExpectedReply(sse))
            throw new InvalidOperationException("The managed reply did not finish with verified usage.");

        var after = await SyncAsync(client, cancellationToken);
        if (after.AllowanceMicros != before.AllowanceMicros || after.SpentMicros <= 0 || after.ReservedMicros != 0)
            throw new InvalidOperationException("The managed reply did not settle cleanly or a replay changed the allowance.");

        using var portal = await client.PostAsync("v1/subscription/portal", null, cancellationToken);
        if (!portal.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe sandbox customer portal creation failed (HTTP {(int)portal.StatusCode}).");

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            check = "subscription-sandbox", runId, stripeCustomerId = billing.CustomerId,
            stripePriceId = billing.PriceId, monthlyUsd = 9, allowanceMicros = after.AllowanceMicros,
            spentMicros = after.SpentMicros, availableMicros = after.AvailableMicros, portalCreated = true
        }));
    }

    private static async Task<AllowanceBalance> SyncAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsync("stripe/test/sync", null, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Stripe invoice verification returned HTTP {(int)response.StatusCode}.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        return json.RootElement.GetProperty("balance").Deserialize<AllowanceBalance>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The gateway did not report a balance.");
    }

    private static bool HasExpectedReply(string stream)
    {
        var text = new StringBuilder();
        foreach (string line in stream.Split('\n'))
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            string data = line[5..].Trim();
            if (data == "[DONE]")
                return text.ToString().Trim() == "AXIOM_SUBSCRIPTION_OK";

            using var frame = JsonDocument.Parse(data);
            if (frame.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
                text.Append(content.GetString());
        }
        return false;
    }
}
