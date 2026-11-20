using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Console;

namespace AxiomSubscriptionGateway;

internal static class LocalGateway
{
    public const string Model = "deepseek/deepseek-v4.1-flash-1017";
    public const string Provider = "digitalocean";
    public const string MockReply = "This reply came from the local Axiom gateway simulation.";
    public const long MockCostMicros = 7_200;
    public const long ReservationMicros = 10_000;

    private const int MaxRequestBytes = 4 * 1024 * 1024;
    private const int MockPromptTokens = 20_000;
    private const int MockCompletionTokens = 5_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WebApplication Create(
        string token,
        string databasePath,
        int port,
        long allowanceMicros = 5_000_000,
        Func<CancellationToken, Task>? waitForMock = null,
        StripeTestWebhook? stripeWebhook = null,
        StripeTestCheckout? stripeCheckout = null,
        OpenRouterInference? inference = null)
    {
        if (stripeCheckout is not null && stripeWebhook is null)
            throw new InvalidOperationException("Test Checkout requires a Stripe sandbox customer and recurring price.");

        waitForMock ??= token => Task.Delay(TimeSpan.FromMilliseconds(150), token);
        var ledger = new AllowanceLedger(databasePath, allowanceMicros, stripeWebhook?.CustomerId);
        byte[] expectedToken = Encoding.UTF8.GetBytes(token);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.Configure<ConsoleLoggerOptions>(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        if (inference is not null)
        {
            builder.Services.AddSingleton(inference);
            builder.Services.AddSingleton(ledger);
            builder.Services.AddHostedService<OpenRouterSettlementWorker>();
        }
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, port);
            options.Limits.MaxRequestBodySize = MaxRequestBytes;
        });
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            if ((stripeWebhook?.SigningSecret is not null && HttpMethods.IsPost(context.Request.Method)
                    && context.Request.Path == StripeTestWebhook.Path)
                || (stripeCheckout is not null && HttpMethods.IsGet(context.Request.Method)
                    && (context.Request.Path == "/stripe/test/checkout/complete"
                        || context.Request.Path == "/stripe/test/checkout/cancel"
                        || context.Request.Path == "/stripe/test/portal/return")))
            {
                await next(context);
                return;
            }

            if (!AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization, out var auth)
                || !string.Equals(auth.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || auth.Parameter is null
                || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(auth.Parameter), expectedToken))
            {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                await Error("A local test access token is required.", "invalid_api_key", 401).ExecuteAsync(context);
                return;
            }

            await next(context);
        });

        if (stripeWebhook?.SigningSecret is not null)
        {
            app.MapPost(StripeTestWebhook.Path, async (HttpContext context) =>
            {
                return await stripeWebhook.HandleAsync(context, ledger, allowanceMicros);
            });
        }

        if (stripeCheckout is not null && stripeWebhook is not null)
        {
            app.MapPost("/stripe/test/checkout", (HttpContext context, CancellationToken cancellationToken) =>
                stripeCheckout.CreateSessionAsync(stripeWebhook, ledger, context.Connection.LocalPort, cancellationToken));
            app.MapPost("/stripe/test/sync", (CancellationToken cancellationToken) =>
                stripeCheckout.SynchronizeAsync(stripeWebhook, ledger, allowanceMicros, cancellationToken));
            app.MapPost("/stripe/test/portal", (HttpContext context, CancellationToken cancellationToken) =>
                stripeCheckout.CreatePortalAsync(stripeWebhook, context.Connection.LocalPort, cancellationToken));
            app.MapGet("/stripe/test/checkout/complete", () =>
                Results.Text("Test Checkout returned. The allowance activates when the gateway verifies the paid Stripe invoice."));
            app.MapGet("/stripe/test/checkout/cancel", () => Results.Text("Test Checkout was canceled."));
            app.MapGet("/stripe/test/portal/return", () => Results.Text("Return to Axiom to view your subscription."));
            app.MapGet("/v1/subscription", () =>
            {
                var (balance, renewsAt) = ledger.GetPeriod();
                var usage = ledger.GetUsage();
                return Results.Json(new
                {
                    service = "axiom-managed-inference",
                    sandbox = true,
                    monthlyUsd = 9,
                    modelLabel = "DeepSeek 4.1F-1017",
                    balance,
                    renewsAt,
                    usage
                });
            });
            app.MapPost("/v1/subscription/checkout", (HttpContext context, CancellationToken cancellationToken) =>
                stripeCheckout.CreateSessionAsync(stripeWebhook, ledger, context.Connection.LocalPort, cancellationToken));
            app.MapPost("/v1/subscription/sync", (CancellationToken cancellationToken) =>
                stripeCheckout.SynchronizeAsync(stripeWebhook, ledger, allowanceMicros, cancellationToken));
            app.MapPost("/v1/subscription/portal", (HttpContext context, CancellationToken cancellationToken) =>
                stripeCheckout.CreatePortalAsync(stripeWebhook, context.Connection.LocalPort, cancellationToken));
        }

        app.MapGet("/v1/models", () => ModelCatalog(inference));
        app.MapGet("/v1/balance", () => Results.Json(ledger.GetBalance()));
        if (inference is not null)
        {
            app.MapPost("/v1/reconcile", async (CancellationToken cancellationToken) =>
                Results.Ok(new { settled = await inference.ReconcileAsync(ledger, cancellationToken) }));
        }
        app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            JsonDocument body;
            try
            {
                body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            }
            catch (JsonException)
            {
                return Error("Send a valid JSON chat request.", "invalid_request", 400);
            }

            using (body)
            {
                JsonElement request = body.RootElement;
                if (!TryReadChatRequest(request, out bool stream))
                    return InvalidChatRequest();

                if (inference is not null)
                    return await inference.CompleteAsync(context, request, stream, ledger);

                var id = $"chatcmpl-local-{Guid.NewGuid():N}";
                if (!ledger.TryReserve(id, ReservationMicros))
                    return Error("Axiom's local test allowance is too low to start this request.", "axiom_allowance_exhausted", 402);

                // The simulator incurs a fixed charge at completion, even if delivery fails.
                // Before that point cancellation releases the full reservation.
                long actual = 0;
                try
                {
                    await waitForMock(context.RequestAborted);
                    actual = MockCostMicros;
                }
                finally
                {
                    ledger.TrySettle(id, actual);
                }

                var usage = new
                {
                    prompt_tokens = MockPromptTokens,
                    completion_tokens = MockCompletionTokens,
                    total_tokens = MockPromptTokens + MockCompletionTokens,
                    cost = MockCostMicros / 1_000_000m
                };
                var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!stream)
                {
                    return Results.Json(new
                    {
                        id, created, model = Model, @object = "chat.completion",
                        choices = new[] { new { index = 0, message = new { role = "assistant", content = MockReply }, finish_reason = "stop" } },
                        usage
                    });
                }

                context.Response.ContentType = "text/event-stream";
                context.Response.Headers.CacheControl = "no-cache";
                var first = new { role = "assistant", content = MockReply[..14] };
                var last = new { content = MockReply[14..] };
                await WriteEventAsync(context, new
                {
                    id, created, model = Model, @object = "chat.completion.chunk",
                    choices = new[] { new { index = 0, delta = first, finish_reason = (string?)null } }
                });
                await WriteEventAsync(context, new
                {
                    id, created, model = Model, @object = "chat.completion.chunk",
                    choices = new[] { new { index = 0, delta = last, finish_reason = "stop" } }
                });
                await WriteEventAsync(context, new
                {
                    id, created, model = Model, @object = "chat.completion.chunk",
                    choices = Array.Empty<object>(), usage
                });
                await context.Response.WriteAsync("data: [DONE]\n\n", context.RequestAborted);
                return Results.Empty;
            }
        });

        return app;
    }

    internal static IResult Error(string message, string code, int statusCode)
        => Results.Json(new { error = new { message, code } }, statusCode: statusCode);

    internal static IResult InvalidChatRequest()
        => Error($"Send a chat request with model {Model} and at least one message.", "invalid_request", 400);

    internal static bool TryReadChatRequest(JsonElement request, out bool stream)
    {
        stream = false;
        if (request.ValueKind != JsonValueKind.Object
            || !request.TryGetProperty("model", out var model)
            || model.ValueKind != JsonValueKind.String || model.GetString() != Model
            || !request.TryGetProperty("messages", out var messages)
            || messages.ValueKind != JsonValueKind.Array || messages.GetArrayLength() == 0)
            return false;

        stream = request.TryGetProperty("stream", out var shouldStream) && shouldStream.ValueKind == JsonValueKind.True;
        return true;
    }

    internal static IResult ModelCatalog(OpenRouterInference? inference)
    {
        var model = new
        {
            id = Model,
            @object = "model",
            owned_by = inference is null ? "axiom-local-simulation" : inference.CatalogOwnedBy,
            context_length = inference is null ? 32_768 : OpenRouterInference.ContextTokens,
            architecture = new
            {
                input_modalities = inference is null ? new[] { "text" } : new[] { "text", "image" },
                output_modalities = new[] { "text" }
            }
        };
        return Results.Json(new { @object = "list", data = new[] { model } });
    }

    private static async Task WriteEventAsync(HttpContext context, object value)
    {
        await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(value, JsonOptions)}\n\n", context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
}
