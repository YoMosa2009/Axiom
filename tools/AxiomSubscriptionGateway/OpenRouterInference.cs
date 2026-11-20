using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AxiomSubscriptionGateway;

internal enum UpstreamProviderKind
{
    OpenRouter,
    DigitalOcean,
    DeepSeek,
    CustomOpenAiCompatible
}

internal sealed record UpstreamInferenceOptions(
    UpstreamProviderKind ProviderKind,
    string BaseUrl,
    string ApiKey,
    string UpstreamModel,
    long InputMicrosPerMillion,
    long CachedInputMicrosPerMillion,
    long OutputMicrosPerMillion,
    string CatalogOwnedBy,
    string? OpenRouterProviderTag = LocalGateway.Provider)
{
    public static UpstreamInferenceOptions OpenRouter(
        string key,
        string model = LocalGateway.Model,
        string providerTag = LocalGateway.Provider)
        => new(
            UpstreamProviderKind.OpenRouter,
            "https://openrouter.ai/api/v1/",
            key.Trim(),
            model,
            180_000,
            3_600,
            720_000,
            string.IsNullOrWhiteSpace(providerTag) ? LocalGateway.Provider : providerTag,
            string.IsNullOrWhiteSpace(providerTag) ? LocalGateway.Provider : providerTag);

    public static UpstreamInferenceOptions DigitalOcean(
        string key,
        string model = "deepseek-v4.1-flash-1017",
        string baseUrl = "https://inference.do-ai.run/v1/")
        => new(
            UpstreamProviderKind.DigitalOcean,
            NormalizeBaseUrl(baseUrl),
            key.Trim(),
            model,
            300_000,
            6_000,
            1_200_000,
            "digitalocean",
            null);

    public static UpstreamInferenceOptions DeepSeek(
        string key,
        string model = "deepseek-chat",
        string baseUrl = "https://api.deepseek.com/v1/")
        => new(
            UpstreamProviderKind.DeepSeek,
            NormalizeBaseUrl(baseUrl),
            key.Trim(),
            model,
            300_000,
            3_000,
            1_200_000,
            "deepseek",
            null);

    public static UpstreamInferenceOptions CustomOpenAiCompatible(
        string key,
        string baseUrl,
        string model,
        long inputMicrosPerMillion = 300_000,
        long cachedInputMicrosPerMillion = 6_000,
        long outputMicrosPerMillion = 1_200_000,
        string catalogOwnedBy = "axiom-upstream")
        => new(
            UpstreamProviderKind.CustomOpenAiCompatible,
            NormalizeBaseUrl(baseUrl),
            key.Trim(),
            model,
            inputMicrosPerMillion,
            cachedInputMicrosPerMillion,
            outputMicrosPerMillion,
            catalogOwnedBy,
            null);

    internal static string NormalizeBaseUrl(string url)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        return url.TrimEnd('/') + "/";
    }
}

internal sealed class OpenRouterInference
{
    private const string DefaultOpenRouterBaseUrl = "https://openrouter.ai/api/v1/";
    internal const int DefaultMaxOutputTokens = 4_096;
    internal const int MaxOutputTokens = 16_384;
    internal const long MinReservationMicros = 1_000;
    // Upper bound covering 1M uncached input ($0.18/M) + 16,384 output ($0.72/M) + safety margin.
    internal const long MaxReservationMicros = 205_000;
    internal const long ReservationMicros = MaxReservationMicros;
    internal const int ContextTokens = 1_048_576;
    private const long ConservativeTokensPerInlineImage = 16_384;
    private const long PromptFramingTokenOverhead = 2_048;
    private static readonly HttpClient OpenRouterHttp = new(new HttpClientHandler { AllowAutoRedirect = false });
    private readonly UpstreamInferenceOptions _options;
    private readonly HttpClient _http;

    internal UpstreamProviderKind ProviderKind => _options.ProviderKind;
    internal string CatalogOwnedBy => _options.CatalogOwnedBy;
    internal UpstreamInferenceOptions Options => _options;

    public OpenRouterInference(string key, HttpClient? http = null)
        : this(UpstreamInferenceOptions.OpenRouter(key), http)
    {
    }

    public OpenRouterInference(UpstreamInferenceOptions options, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ApiKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UpstreamModel);
        _options = options with
        {
            BaseUrl = UpstreamInferenceOptions.NormalizeBaseUrl(options.BaseUrl),
            ApiKey = options.ApiKey.Trim(),
            UpstreamModel = options.UpstreamModel.Trim()
        };
        _http = http ?? OpenRouterHttp;
    }

    public static OpenRouterInference? FromEnvironment()
    {
        string rawProvider = (Environment.GetEnvironmentVariable("AXIOM_UPSTREAM_PROVIDER") ?? "openrouter")
            .Trim().ToLowerInvariant();

        UpstreamProviderKind kind = rawProvider switch
        {
            "" or "openrouter" => UpstreamProviderKind.OpenRouter,
            "digitalocean" or "do" => UpstreamProviderKind.DigitalOcean,
            "deepseek" => UpstreamProviderKind.DeepSeek,
            "custom" => UpstreamProviderKind.CustomOpenAiCompatible,
            _ => throw new InvalidOperationException($"Unsupported AXIOM_UPSTREAM_PROVIDER '{rawProvider}'. Expected openrouter, digitalocean, deepseek, or custom.")
        };

        string? key = Environment.GetEnvironmentVariable("AXIOM_UPSTREAM_API_KEY");
        if (string.IsNullOrWhiteSpace(key) && kind == UpstreamProviderKind.OpenRouter)
            key = Environment.GetEnvironmentVariable("AXIOM_GATEWAY_OPENROUTER_KEY");
        if (string.IsNullOrWhiteSpace(key))
            return null;

        UpstreamInferenceOptions defaults = kind switch
        {
            UpstreamProviderKind.OpenRouter => UpstreamInferenceOptions.OpenRouter(key),
            UpstreamProviderKind.DigitalOcean => UpstreamInferenceOptions.DigitalOcean(key),
            UpstreamProviderKind.DeepSeek => UpstreamInferenceOptions.DeepSeek(key),
            _ => UpstreamInferenceOptions.CustomOpenAiCompatible(
                key,
                Environment.GetEnvironmentVariable("AXIOM_UPSTREAM_BASE_URL")
                    ?? throw new InvalidOperationException("Set AXIOM_UPSTREAM_BASE_URL when AXIOM_UPSTREAM_PROVIDER=custom."),
                Environment.GetEnvironmentVariable("AXIOM_UPSTREAM_MODEL")
                    ?? throw new InvalidOperationException("Set AXIOM_UPSTREAM_MODEL when AXIOM_UPSTREAM_PROVIDER=custom."))
        };

        string? baseUrlOverride = Environment.GetEnvironmentVariable("AXIOM_UPSTREAM_BASE_URL");
        string? modelOverride = Environment.GetEnvironmentVariable("AXIOM_UPSTREAM_MODEL");
        long inputRate = ReadEnvRate("AXIOM_UPSTREAM_INPUT_MICROS_PER_M") ?? defaults.InputMicrosPerMillion;
        long cachedRate = ReadEnvRate("AXIOM_UPSTREAM_CACHED_INPUT_MICROS_PER_M") ?? defaults.CachedInputMicrosPerMillion;
        long outputRate = ReadEnvRate("AXIOM_UPSTREAM_OUTPUT_MICROS_PER_M") ?? defaults.OutputMicrosPerMillion;

        var configured = defaults with
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrlOverride) ? defaults.BaseUrl : UpstreamInferenceOptions.NormalizeBaseUrl(baseUrlOverride),
            UpstreamModel = string.IsNullOrWhiteSpace(modelOverride) ? defaults.UpstreamModel : modelOverride.Trim(),
            InputMicrosPerMillion = inputRate,
            CachedInputMicrosPerMillion = cachedRate,
            OutputMicrosPerMillion = outputRate
        };
        return new OpenRouterInference(configured);
    }

    private static long? ReadEnvRate(string name)
        => long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out long value)
            && value >= 0
            ? value
            : null;

    public async Task<IResult> CompleteAsync(HttpContext context, JsonElement clientRequest, bool stream, AllowanceLedger ledger)
    {
        JsonObject payload;
        int effectiveMaxTokens;
        try
        {
            payload = BuildPayload(clientRequest, stream, _options, out effectiveMaxTokens);
        }
        catch (ArgumentException ex)
        {
            return LocalGateway.Error(ex.Message, "invalid_request", 400);
        }

        using var request = WithAuthentication(HttpMethod.Post, "chat/completions");
        request.Content = JsonContent.Create(payload);
        string id = Guid.NewGuid().ToString("N");
        context.RequestAborted.ThrowIfCancellationRequested();
        long reservationMicros = EstimateReservationMicros(clientRequest, effectiveMaxTokens, _options);
        var reserve = ledger.TryReserveDetailed(id, reservationMicros);
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
                return await StreamAsync(context, response, ledger, id, reservationMicros, deadline.Token);

            var body = await response.Content.ReadAsStringAsync(deadline.Token);
            using var completion = JsonDocument.Parse(body);
            if (!RecordReceipt(completion.RootElement, ledger, id, reservationMicros))
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
        if (_options.ProviderKind != UpstreamProviderKind.OpenRouter)
            return 0;

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
                && ReadCost(data, "total_cost", ComputeMaxReservationMicros(_options)) is long actual
                && ledger.ForAccount(pending.AccountId).TrySettle(pending.RequestId, actual))
                count++;
        }
        return count;
    }

    private async Task<IResult> StreamAsync(HttpContext context, HttpResponseMessage response, AllowanceLedger ledger,
        string id, long reservationMicros, CancellationToken cancellationToken)
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
            if (!settled && ResolveUsageCostMicros(Read(frame.RootElement, "usage"), reservationMicros, _options) is long actual)
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

    internal static JsonObject BuildPayload(JsonElement request, bool stream, UpstreamInferenceOptions options, out int effectiveMaxTokens)
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

        effectiveMaxTokens = Read(request, "max_tokens") is { ValueKind: JsonValueKind.Number } requested
            && requested.TryGetInt32(out int count) ? Math.Clamp(count, 1, MaxOutputTokens) : DefaultMaxOutputTokens;
        var payload = new JsonObject
        {
            ["model"] = options.UpstreamModel,
            ["stream"] = stream,
            ["max_tokens"] = effectiveMaxTokens
        };

        if (options.ProviderKind == UpstreamProviderKind.OpenRouter)
        {
            payload["reasoning"] = BuildReasoningPayload(request);
            payload["provider"] = JsonSerializer.SerializeToNode(new
            {
                only = new[] { options.OpenRouterProviderTag ?? LocalGateway.Provider },
                allow_fallbacks = false,
                require_parameters = true,
                max_price = new
                {
                    prompt = options.InputMicrosPerMillion / 1_000_000m,
                    completion = options.OutputMicrosPerMillion / 1_000_000m,
                    request = 0m,
                    image = 0m
                }
            });
        }
        else if (stream)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        // DigitalOcean's 1017 OpenRouter endpoint does not list "stop" in supported_parameters,
        // so forwarding client stop sequences would fail require_parameters routing.
        foreach (string property in new[] { "messages", "tools", "tool_choice", "temperature", "top_p" })
        {
            if (request.TryGetProperty(property, out var value))
                payload[property] = JsonNode.Parse(value.GetRawText());
        }
        return payload;
    }

    internal static JsonObject BuildReasoningPayload(JsonElement request)
    {
        JsonElement reasoning = Read(request, "reasoning");
        if (reasoning.ValueKind != JsonValueKind.Object)
            return new JsonObject { ["enabled"] = false };

        if (reasoning.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.False)
            return new JsonObject { ["enabled"] = false };

        string? effort = ReadString(reasoning, "effort")?.Trim().ToLowerInvariant() switch
        {
            "low" or "medium" or "high" => ReadString(reasoning, "effort")!.Trim().ToLowerInvariant(),
            _ => null
        };
        bool explicitlyEnabled = reasoning.TryGetProperty("enabled", out enabledElement)
            && enabledElement.ValueKind == JsonValueKind.True;

        if (!explicitlyEnabled && effort is null)
            return new JsonObject { ["enabled"] = false };

        var result = new JsonObject { ["enabled"] = true };
        if (effort is not null)
            result["effort"] = effort;
        if (reasoning.TryGetProperty("exclude", out var excludeElement)
            && excludeElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result["exclude"] = excludeElement.GetBoolean();
        }
        return result;
    }

    internal static long EstimateReservationMicros(JsonElement request, int effectiveMaxTokens)
        => EstimateReservationMicros(request, effectiveMaxTokens, UpstreamInferenceOptions.OpenRouter("unused"));

    internal static long EstimateReservationMicros(JsonElement request, int effectiveMaxTokens, UpstreamInferenceOptions options)
    {
        long textBytes = 0;
        long imageCount = 0;

        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("messages", out var messages)
            && messages.ValueKind == JsonValueKind.Array)
        {
            foreach (var message in messages.EnumerateArray())
            {
                if (message.ValueKind != JsonValueKind.Object)
                    continue;

                JsonElement content = Read(message, "content");
                if (content.ValueKind == JsonValueKind.String)
                {
                    textBytes += System.Text.Encoding.UTF8.GetByteCount(content.GetString() ?? string.Empty);
                }
                else if (content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (string.Equals(ReadString(part, "type"), "image_url", StringComparison.Ordinal))
                            imageCount++;
                        else if (ReadString(part, "text") is string textPart)
                            textBytes += System.Text.Encoding.UTF8.GetByteCount(textPart);
                    }
                }

                if (message.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    textBytes += System.Text.Encoding.UTF8.GetByteCount(toolCalls.GetRawText());
                if (ReadString(message, "tool_call_id") is string toolCallId)
                    textBytes += System.Text.Encoding.UTF8.GetByteCount(toolCallId);
            }
        }

        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("tools", out var tools))
            textBytes += System.Text.Encoding.UTF8.GetByteCount(tools.GetRawText());
        if (request.ValueKind == JsonValueKind.Object && request.TryGetProperty("tool_choice", out var toolChoice))
            textBytes += System.Text.Encoding.UTF8.GetByteCount(toolChoice.GetRawText());

        long estimatedInputTokens = Math.Min(
            ContextTokens,
            (long)Math.Ceiling(textBytes * 1.25m) + (imageCount * ConservativeTokensPerInlineImage) + PromptFramingTokenOverhead);
        long estimatedOutputTokens = (long)Math.Ceiling(Math.Clamp(effectiveMaxTokens, 1, MaxOutputTokens) * 1.15m);
        long inputMicros = (long)Math.Ceiling(estimatedInputTokens * (options.InputMicrosPerMillion / 1_000_000m));
        long outputMicros = (long)Math.Ceiling(estimatedOutputTokens * (options.OutputMicrosPerMillion / 1_000_000m));
        return Math.Clamp(inputMicros + outputMicros, MinReservationMicros, ComputeMaxReservationMicros(options));
    }

    private static long ComputeMaxReservationMicros(UpstreamInferenceOptions options)
    {
        long maxByRates = (long)Math.Ceiling(
            (ContextTokens * (options.InputMicrosPerMillion / 1_000_000m))
            + (MaxOutputTokens * 1.15m * (options.OutputMicrosPerMillion / 1_000_000m))) + 10_000;
        return Math.Max(MaxReservationMicros, maxByRates);
    }

    private static bool IsInlineImage(string? url)
        => url is not null && (url.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            || url.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            || url.StartsWith("data:image/webp;base64,", StringComparison.Ordinal));

    private bool RecordReceipt(JsonElement response, AllowanceLedger ledger, string id, long reservationMicros)
    {
        if (ReadString(response, "id") is { } upstream)
            ledger.RecordUpstreamGeneration(id, upstream);
        return ResolveUsageCostMicros(Read(response, "usage"), reservationMicros, _options) is long actual
            && ledger.TrySettle(id, actual);
    }

    internal static long? ResolveUsageCostMicros(JsonElement usage, long maxAllowedMicros, UpstreamInferenceOptions options)
    {
        if (ReadCost(usage, "cost", maxAllowedMicros) is long directCost)
            return directCost;

        if (usage.ValueKind != JsonValueKind.Object)
            return null;

        bool hasPrompt = TryReadNonNegativeLong(usage, "prompt_tokens", out long promptTokens);
        bool hasCompletion = TryReadNonNegativeLong(usage, "completion_tokens", out long completionTokens);
        if (!hasPrompt && !hasCompletion)
            return null;

        long cachedTokens = 0;
        if (TryReadNonNegativeLong(usage, "prompt_cache_hit_tokens", out long cacheHit))
            cachedTokens = cacheHit;
        else if (TryReadNonNegativeLong(Read(usage, "prompt_tokens_details"), "cached_tokens", out long cachedDetail))
            cachedTokens = cachedDetail;

        cachedTokens = Math.Clamp(cachedTokens, 0, promptTokens);
        long uncachedInputTokens = Math.Max(0, promptTokens - cachedTokens);
        decimal rawCostMicros =
            (uncachedInputTokens * (options.InputMicrosPerMillion / 1_000_000m))
            + (cachedTokens * (options.CachedInputMicrosPerMillion / 1_000_000m))
            + (completionTokens * (options.OutputMicrosPerMillion / 1_000_000m));

        long computed = (long)Math.Ceiling(rawCostMicros);
        return Math.Clamp(computed, 0, maxAllowedMicros);
    }

    private static bool TryReadNonNegativeLong(JsonElement element, string name, out long value)
    {
        value = 0;
        var raw = Read(element, name);
        return raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out value) && value >= 0;
    }

    private static JsonElement Read(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    private static string? ReadString(JsonElement element, string name)
        => Read(element, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static long? ReadCost(JsonElement element, string name, long maxAllowedMicros)
    {
        var raw = Read(element, name);
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out decimal value)
            || value < 0 || value > maxAllowedMicros / 1_000_000m)
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
        var request = new HttpRequestMessage(method, _options.BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
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
