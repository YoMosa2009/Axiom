using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxiomSubscriptionGateway;

internal sealed class OpenRouterInference
{
    private const string BaseUrl = "https://openrouter.ai/api/v1/";
    private const int MaxOutputTokens = 2_048;
    // Worst case at the pinned input and output prices, rounded up to USD micro-units.
    internal const long ReservationMicros = 190_219;
    internal const int ContextTokens = 1_048_576;
    private static readonly HttpClient OpenRouterHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly HttpClient _http;
    private readonly string _key;

    public OpenRouterInference(string key, HttpClient? http = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _key = key.Trim();
        _http = http ?? OpenRouterHttp;
    }

    public static OpenRouterInference? FromEnvironment()
    {
        var key = Environment.GetEnvironmentVariable("AXIOM_GATEWAY_OPENROUTER_KEY");
        return key is null ? null : new OpenRouterInference(key);
    }

    public async Task<IResult> CompleteAsync(HttpContext context, JsonElement clientRequest, bool stream, AllowanceLedger ledger)
    {
        JsonObject payload;
        try
        {
            payload = BuildPayload(clientRequest, stream);
        }
        catch (ArgumentException ex)
        {
            return LocalGateway.Error(ex.Message, "invalid_request", 400);
        }

        using var request = WithAuthentication(HttpMethod.Post, "chat/completions");
        request.Content = JsonContent.Create(payload);
        string id = Guid.NewGuid().ToString("N");
        context.RequestAborted.ThrowIfCancellationRequested();
        var reserve = ledger.TryReserveDetailed(id, ReservationMicros);
        if (reserve.Status == ReserveStatus.SessionLimitReached)
        {
            return LocalGateway.Error(
                "You reached your 3-hour usage limit for DeepSeek 4.1F-1017."
                + (reserve.ResetsAt is { } resetsAt ? $" It resets at {resetsAt:O}." : string.Empty),
                "axiom_session_limit_reached", 429);
        }
        if (reserve.Status == ReserveStatus.WeeklyLimitReached)
        {
            return LocalGateway.Error(
                "You reached your weekly usage limit for DeepSeek 4.1F-1017."
                + (reserve.ResetsAt is { } resetsAt ? $" It resets on {resetsAt:O}." : string.Empty),
                "axiom_weekly_limit_reached", 429);
        }
        if (reserve.Status != ReserveStatus.Reserved)
            return LocalGateway.Error("Axiom's allowance is too low to start this request.", "axiom_allowance_exhausted", 402);

        // Once dispatched, continue reading the upstream receipt if the desktop disconnects.
        // Release an unconfirmed hold only after a definitive reject or confirmed cost.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
            {
                if (IsDefiniteRejection(response.StatusCode))
                {
                    ledger.TrySettle(id, 0);
                    if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                    {
                        string errorBody = await response.Content.ReadAsStringAsync(deadline.Token);
                        string? detail = TryExtractProviderErrorMessage(errorBody);
                        string code = IsUnsupportedCapabilityMessage(detail) ? "unsupported_capability" : "invalid_request";
                        string message = detail ?? $"The configured inference route rejected the request (HTTP {(int)response.StatusCode}).";
                        return LocalGateway.Error(message, code, 400);
                    }
                    return LocalGateway.Error($"The configured inference route returned HTTP {(int)response.StatusCode}.",
                        "upstream_unavailable", 502);
                }
                return UsagePending();
            }

            if (stream)
                return await StreamAsync(context, response, ledger, id, deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            using var completion = JsonDocument.Parse(body);
            if (!RecordReceipt(completion.RootElement, ledger, id))
                return UsagePending();

            return Results.Content(body, "application/json");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or JsonException)
        {
            if (context.Response.HasStarted)
            {
                context.Abort();
                return Results.Empty;
            }
            return UsagePending();
        }
    }

    public async Task<int> ReconcileAsync(AllowanceLedger ledger, CancellationToken cancellationToken, bool allAccounts = false)
    {
        int count = 0;
        foreach (var pending in ledger.PendingUpstreamRequests(allAccounts))
        {
            using var request = WithAuthentication(HttpMethod.Get, "generation?id=" + Uri.EscapeDataString(pending.GenerationId));
            using var response = await _http.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                continue;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            JsonElement data = Read(doc.RootElement, "data");
            if (ReadString(data, "id") == pending.GenerationId
                && ReadCost(data, "total_cost") is long actual
                && ledger.ForAccount(pending.AccountId).TrySettle(pending.RequestId, actual))
                count++;
        }
        return count;
    }

    private static async Task<IResult> StreamAsync(HttpContext context, HttpResponseMessage response, AllowanceLedger ledger,
        string id, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            return UsagePending();

        context.Response.ContentType = "text/event-stream";
        context.Response.Headers.CacheControl = "no-cache";
        await using var bytes = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(bytes);
        string? generationId = null;
        bool settled = false;
        bool clientConnected = !context.RequestAborted.IsCancellationRequested;
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
                continue;
            string data = line[5..].TrimStart();
            if (data == "[DONE]")
            {
                if (settled && clientConnected)
                    await WriteStreamLineAsync(context, data);
                break;
            }

            using var frame = JsonDocument.Parse(data);
            if (frame.RootElement.ValueKind != JsonValueKind.Object)
                continue;
            if (generationId is null && ReadString(frame.RootElement, "id") is { } upstreamId)
            {
                ledger.RecordUpstreamGeneration(id, upstreamId);
                generationId = upstreamId;
            }
            if (!settled && ReadCost(Read(frame.RootElement, "usage"), "cost") is long actual)
                settled = ledger.TrySettle(id, actual);

            if (clientConnected)
                clientConnected = await WriteStreamLineAsync(context, data);
        }

        if (!settled && clientConnected)
        {
            var error = JsonSerializer.Serialize(new { error = new { code = "usage_pending",
                message = "The inference stream closed before usage was confirmed." } });
            await WriteStreamLineAsync(context, error);
        }
        return Results.Empty;
    }

    private static async Task<bool> WriteStreamLineAsync(HttpContext context, string data)
    {
        try
        {
            await context.Response.WriteAsync($"data: {data}\n\n", context.RequestAborted);
            await context.Response.Body.FlushAsync(context.RequestAborted);
            return true;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private static JsonObject BuildPayload(JsonElement request, bool stream)
    {
        foreach (var message in request.GetProperty("messages").EnumerateArray())
        {
            if (message.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("Each message must be an object.");

            JsonElement content = Read(message, "content");
            if (content.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var item in content.EnumerateArray())
            {
                switch (ReadString(item, "type"))
                {
                    case "text":
                        break;
                    case "image_url" when IsInlineImage(ReadString(Read(item, "image_url"), "url")):
                        break;
                    default:
                        throw new ArgumentException("Send text or inline PNG, JPEG, or WebP images.");
                }
            }
        }

        int maxTokens = Read(request, "max_tokens") is { ValueKind: JsonValueKind.Number } requested
            && requested.TryGetInt32(out int count) ? Math.Clamp(count, 1, MaxOutputTokens) : MaxOutputTokens;
        var payload = new JsonObject
        {
            ["model"] = LocalGateway.Model,
            ["stream"] = stream,
            ["max_tokens"] = maxTokens,
            ["reasoning"] = new JsonObject { ["enabled"] = false },
            ["provider"] = JsonSerializer.SerializeToNode(new
            {
                only = new[] { LocalGateway.Provider },
                allow_fallbacks = false,
                require_parameters = true,
                max_price = new { prompt = 0.18m, completion = 0.72m, request = 0m, image = 0m }
            })
        };
        // DigitalOcean's 1017 OpenRouter endpoint does not list "stop" in supported_parameters,
        // so forwarding client stop sequences would fail require_parameters routing.
        foreach (string property in new[] { "messages", "tools", "tool_choice", "temperature", "top_p" })
        {
            if (request.TryGetProperty(property, out var value))
                payload[property] = JsonNode.Parse(value.GetRawText());
        }
        return payload;
    }

    private static bool IsInlineImage(string? url)
        => url is not null && (url.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            || url.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            || url.StartsWith("data:image/webp;base64,", StringComparison.Ordinal));

    private static bool RecordReceipt(JsonElement response, AllowanceLedger ledger, string id)
    {
        if (ReadString(response, "id") is { } upstream)
            ledger.RecordUpstreamGeneration(id, upstream);
        return ReadCost(Read(response, "usage"), "cost") is long actual && ledger.TrySettle(id, actual);
    }

    private static JsonElement Read(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement element, string name)
        => Read(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? ReadCost(JsonElement element, string name)
    {
        var raw = Read(element, name);
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out decimal value)
            || value < 0 || value > ReservationMicros / 1_000_000m)
            return null;
        return (long)Math.Ceiling(value * 1_000_000m);
    }

    private static bool IsDefiniteRejection(HttpStatusCode status)
        => (int)status is 400 or 401 or 402 or 403 or 404 or 413 or 422 or 429;

    internal static string? TryExtractProviderErrorMessage(string? json, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            JsonElement error = Read(doc.RootElement, "error");
            if (error.ValueKind != JsonValueKind.Object)
                return null;

            string? candidate = null;
            if (ReadString(Read(error, "metadata"), "raw") is { Length: > 0 } raw)
                candidate = TryReadNestedErrorMessage(raw) ?? raw;

            candidate ??= ReadString(error, "message");
            return SanitizeErrorSummary(candidate, maxLength);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadNestedErrorMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            return ReadString(Read(doc.RootElement, "error"), "message") ?? ReadString(doc.RootElement, "message");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SanitizeErrorSummary(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maxLength + 3));
        bool previousSpace = false;
        foreach (char ch in text.Trim())
        {
            char normalized = char.IsControl(ch) || char.IsWhiteSpace(ch) ? ' ' : ch;
            if (normalized == ' ')
            {
                if (previousSpace || builder.Length == 0)
                    continue;
                previousSpace = true;
            }
            else
            {
                previousSpace = false;
            }

            if (builder.Length >= maxLength)
            {
                builder.Append("...");
                break;
            }
            builder.Append(normalized);
        }

        string cleaned = builder.ToString().TrimEnd();
        return cleaned.Length > 0 ? cleaned : null;
    }

    private static bool IsUnsupportedCapabilityMessage(string? message)
        => !string.IsNullOrWhiteSpace(message)
            && (message.Contains("image", StringComparison.OrdinalIgnoreCase)
                || message.Contains("multimodal", StringComparison.OrdinalIgnoreCase)
                || message.Contains("vision", StringComparison.OrdinalIgnoreCase)
                || message.Contains("modality", StringComparison.OrdinalIgnoreCase)
                || message.Contains("unsupported", StringComparison.OrdinalIgnoreCase));

    private static IResult UsagePending()
        => LocalGateway.Error("Usage is unconfirmed for this request. The allowance reservation is held for reconciliation.",
            "usage_pending", 409);

    private HttpRequestMessage WithAuthentication(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _key);
        return request;
    }
}

internal sealed class OpenRouterSettlementWorker(
    OpenRouterInference inference, AllowanceLedger ledger, ILogger<OpenRouterSettlementWorker> logger,
    TimeProvider? clock = null, bool allAccounts = false) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock ?? TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    deadline.CancelAfter(TimeSpan.FromSeconds(30));
                    await inference.ReconcileAsync(ledger, deadline.Token, allAccounts);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested
                    && ex is HttpRequestException or OperationCanceledException or JsonException)
                {
                    logger.LogWarning("OpenRouter settlement check failed. Pending reservations will be checked again.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown cancels the wait for the next reconciliation tick.
        }
    }
}
