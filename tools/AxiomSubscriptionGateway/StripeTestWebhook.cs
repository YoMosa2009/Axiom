using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AxiomSubscriptionGateway;

internal sealed record StripeTestWebhook(string CustomerId, string PriceId, string? SigningSecret, string? PortalConfigurationId = null)
{
    public const string Path = "/stripe/test/webhook";

    public static StripeTestWebhook? FromEnvironment()
    {
        string? customer = Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_CUSTOMER");
        string? price = Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_PRICE");
        string? secret = Environment.GetEnvironmentVariable("AXIOM_STRIPE_TEST_WEBHOOK_SECRET");
        if (customer is null && price is null && secret is null)
            return null;
        if (customer?.StartsWith("cus_", StringComparison.Ordinal) != true
            || price?.StartsWith("price_", StringComparison.Ordinal) != true
            || (secret is not null && !secret.StartsWith("whsec_", StringComparison.Ordinal)))
            throw new InvalidOperationException("Configure the Stripe test customer and price. If supplied, the signing secret must be a Stripe webhook secret.");

        return new StripeTestWebhook(customer, price, secret);
    }

    public Task<IResult> HandleAsync(HttpContext context, AllowanceLedger ledger, long allowanceMicros)
        => ReadPaidInvoiceAsync(context, SigningSecret, (_, invoice) => TryApplyPaidInvoice(invoice, ledger, allowanceMicros));

    internal static async Task<IResult> ReadPaidInvoiceAsync(
        HttpContext context, string? signingSecret, Action<string, JsonElement> apply)
    {
        using var payload = new MemoryStream();
        await context.Request.Body.CopyToAsync(payload, context.RequestAborted);
        byte[] body = payload.ToArray();
        var signature = context.Request.Headers["Stripe-Signature"].ToString();
        if (!HasValidSignature(signature, body, signingSecret))
            return Results.BadRequest(new { error = "Invalid Stripe webhook signature." });

        try
        {
            using var doc = JsonDocument.Parse(body);
            JsonElement evt = doc.RootElement;
            if (Read(evt, "livemode").ValueKind != JsonValueKind.False
                || ReadString(evt, "type") != "invoice.paid")
                return Results.NoContent();

            JsonElement invoice = Read(Read(evt, "data"), "object");
            if (ReadString(invoice, "customer") is { } customer)
                apply(customer, invoice);
            return Results.NoContent();
        }
        catch (JsonException)
        {
            return Results.BadRequest(new { error = "Invalid Stripe webhook payload." });
        }
    }

    public bool TryApplyPaidInvoice(JsonElement invoice, AllowanceLedger ledger, long allowanceMicros)
    {
        string? invoiceId = ReadString(invoice, "id");
        JsonElement subscription = Read(Read(invoice, "parent"), "subscription_details");
        JsonElement lines = Read(Read(invoice, "lines"), "data");
        if (invoiceId?.StartsWith("in_", StringComparison.Ordinal) != true
            || Read(invoice, "livemode").ValueKind != JsonValueKind.False
            || ReadString(invoice, "status") != "paid"
            || ReadString(invoice, "customer") != CustomerId
            || ReadString(invoice, "billing_reason") is not ("subscription_create" or "subscription_cycle")
            || ReadString(subscription, "subscription")?.StartsWith("sub_", StringComparison.Ordinal) != true
            || lines.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var line in lines.EnumerateArray())
        {
            JsonElement price = Read(Read(line, "pricing"), "price_details");
            JsonElement details = Read(Read(line, "parent"), "subscription_item_details");
            JsonElement period = Read(line, "period");
            if (ReadString(price, "price") == PriceId
                && Read(details, "proration").ValueKind == JsonValueKind.False
                && TryReadLong(period, "start", out long start)
                && TryReadLong(period, "end", out long end)
                && start > 0 && end > start)
                return ledger.TryGrantPeriod(invoiceId, start, end, allowanceMicros);
        }
        return false;
    }

    private static bool HasValidSignature(string header, byte[] body, string? signingSecret)
    {
        if (signingSecret is null)
            return false;

        string[] fields = header.Split(',');
        string? stamp = fields.FirstOrDefault(part => part.StartsWith("t=", StringComparison.Ordinal))?[2..];
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out long timestamp)
            || timestamp < now - 300 || timestamp > now + 300)
            return false;

        byte[] prefix = Encoding.UTF8.GetBytes(stamp + ".");
        byte[] signedPayload = new byte[prefix.Length + body.Length];
        prefix.CopyTo(signedPayload, 0);
        body.CopyTo(signedPayload, prefix.Length);
        byte[] expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingSecret), signedPayload);
        foreach (string field in fields)
        {
            if (field.Length != 67 || !field.StartsWith("v1=", StringComparison.Ordinal))
                continue;
            try
            {
                if (CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(field.AsSpan(3))))
                    return true;
            }
            catch (FormatException)
            {
                // Stripe may send multiple v1 signatures during secret rotation.
            }
        }
        return false;
    }

    private static JsonElement Read(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement element, string name)
        => Read(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool TryReadLong(JsonElement element, string name, out long number)
    {
        number = 0;
        return Read(element, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out number);
    }
}
