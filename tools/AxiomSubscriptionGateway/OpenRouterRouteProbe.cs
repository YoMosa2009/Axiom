using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace AxiomSubscriptionGateway;

internal enum ProbeKind { Text, Tool, Image }

internal sealed class OpenRouterRouteProbe(HttpClient http, string key)
{
    private const string Api = "https://openrouter.ai/api/v1/";
    private const string Marker = "AXIOM_PROBE_OK";
    private const string FunctionName = "record_status";
    private const string Dated1017Model = "deepseek/deepseek-v4.1-flash-20261017";
    private const int MaxOutputTokens = 128;
    private const decimal ReviewThresholdUsd = 0.01m;
    private const decimal MaximumKeyLimitUsd = 0.50m;
    private static readonly int[] AccountingRetryMilliseconds = [500, 1_000, 2_000, 4_000, 8_000, 12_000];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // Synthetic, uniform red 128×128 PNG. No desktop screenshots or user data leave this tool.
    private const string RedImage = "data:image/png;base64,"
        + "iVBORw0KGgoAAAANSUhEUgAAAIAAAACACAIAAABMXPacAAAAxElEQVR42u3RMQ0AAAjAsPk3DTLgaDIFa1M6zAIAAAQAgAAA"
        + "EAAAAgBAAAAIAAABACAAAAQAgAAAEAAAAgBAAAAIAAABACAAAAQAgAAAEAAAAgBAAAAIAAABACAAAAQAgAAAEAAAAgBAAAAI"
        + "AAABAADAAgAABACAAAAQAAACAEAAAAgAAAEAIAAABACAAAAQAAACAEAAAAgAAAEAIAAABACAAAAQAAACAEAAAAgAAAEAIAAA"
        + "BACAAAAQAAACAEAAAAjAixYgaMOy89oM6gAAAABJRU5ErkJggg==";

    public async Task<ProbeResult> RunAsync(ProbeKind kind, CancellationToken cancellationToken)
    {
        await CheckKeyLimitAsync(cancellationToken);
        using var request = CreateRequest(HttpMethod.Post, "chat/completions");
        request.Content = JsonContent.Create(BuildPayload(kind));
        // Never retry generation. A failed or interrupted response may already have incurred cost.
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            string? detail = OpenRouterInference.TryExtractProviderErrorMessage(errorBody);
            string suffix = string.IsNullOrWhiteSpace(detail) ? string.Empty : $" ({detail})";
            throw new InvalidOperationException($"OpenRouter generation returned HTTP {(int)response.StatusCode}{suffix}.");
        }

        var completion = await response.Content.ReadFromJsonAsync<Completion>(Json, cancellationToken);
        if (completion?.Id is not { Length: > 0 } id)
            throw new InvalidOperationException("OpenRouter did not return a generation ID.");

        var generation = await ReadGenerationAsync(id, cancellationToken);
        if (!IsExpectedRoute(generation, id))
            throw new InvalidOperationException($"Generation {id} did not confirm the expected shared DigitalOcean 1017 route.");

        if (completion.Usage is not { Cost: >= 0 and <= ReviewThresholdUsd } usage
            || generation.TotalCost is not (>= 0 and <= ReviewThresholdUsd)
            || Math.Abs(usage.Cost.Value - generation.TotalCost.Value) > 0.000001m)
            throw new InvalidOperationException($"Review the reported and settled cost for generation {id} before running another probe.");

        var message = completion.Choices?.FirstOrDefault()?.Message;
        bool accepted = kind switch
        {
            ProbeKind.Text => message?.Content?.Trim() == Marker,
            ProbeKind.Tool => HasExpectedToolCall(message?.ToolCalls),
            ProbeKind.Image => string.Equals(message?.Content?.Trim().TrimEnd('.'), "red", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
        if (!accepted)
            throw new InvalidOperationException($"Generation {id} reached the expected route, but did not pass the {kind} capability check.");

        return new ProbeResult(kind.ToString(), id, generation.Model!, generation.ProviderName!,
            usage.PromptTokens, usage.CompletionTokens, usage.Cost.Value, generation.TotalCost.Value);
    }

    public async Task<GenerationAccounting> LookupAsync(string id, CancellationToken cancellationToken)
    {
        var generation = await ReadGenerationAsync(id, cancellationToken);
        if (generation.Id != id)
            throw new InvalidOperationException($"OpenRouter returned accounting for a different generation than {id}.");

        return new GenerationAccounting(id, generation.Model, generation.ProviderName, IsExpectedRoute(generation, id),
            generation.NativeTokensPrompt ?? generation.TokensPrompt,
            generation.NativeTokensCompletion ?? generation.TokensCompletion,
            generation.TotalCost, generation.FinishReason, generation.IsByok, generation.Cancelled);
    }

    private static bool IsExpectedRoute(Generation generation, string id)
        => generation.Id == id
            && string.Equals(generation.ProviderName, LocalGateway.Provider, StringComparison.OrdinalIgnoreCase)
            && generation.Model is LocalGateway.Model or Dated1017Model
            && generation.Cancelled != true
            && generation.IsByok == false;

    internal async Task CheckKeyLimitAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, "key");
        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenRouter key check returned HTTP {(int)response.StatusCode}. No generation was requested.");

        var info = await response.Content.ReadFromJsonAsync<KeyLookup>(Json, cancellationToken);
        var keyInfo = info?.Data;
        if (keyInfo is not { Limit: > 0 and <= MaximumKeyLimitUsd, LimitRemaining: >= ReviewThresholdUsd })
        {
            string limit = DescribeAmount(keyInfo?.Limit, "no limit reported");
            string remaining = DescribeAmount(keyInfo?.LimitRemaining, "unavailable");
            throw new InvalidOperationException(
                $"OpenRouter reported a key limit of {limit} and a remaining key allowance of {remaining}. "
                + FormattableString.Invariant(
                    $"Set a positive key limit of ${MaximumKeyLimitUsd:0.00} or less with at least ${ReviewThresholdUsd:0.00} remaining. No generation was requested."));
        }
    }

    private static string DescribeAmount(decimal? amount, string missing)
        => amount is decimal value ? "$" + value.ToString("G29", CultureInfo.InvariantCulture) : missing;

    private async Task<Generation> ReadGenerationAsync(string id, CancellationToken cancellationToken)
    {
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                using var request = CreateRequest(HttpMethod.Get, "generation?id=" + Uri.EscapeDataString(id));
                using var response = await http.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var lookup = await response.Content.ReadFromJsonAsync<Lookup>(Json, cancellationToken);
                    return lookup?.Data ?? throw new InvalidOperationException($"Generation {id} returned no accounting data.");
                }
                if (response.StatusCode != HttpStatusCode.NotFound)
                    throw new InvalidOperationException($"Generation {id} accounting returned HTTP {(int)response.StatusCode}.");
                if (attempt == AccountingRetryMilliseconds.Length)
                    break;

                await Task.Delay(AccountingRetryMilliseconds[attempt], cancellationToken);
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException($"Accounting for generation {id} timed out. Check OpenRouter Activity or use --lookup-openrouter with this ID.", ex);
        }

        throw new InvalidOperationException($"Accounting for generation {id} is unavailable. Check OpenRouter Activity or use --lookup-openrouter with this ID.");
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }

    private static Dictionary<string, object> BuildPayload(ProbeKind kind)
    {
        object content = kind switch
        {
            ProbeKind.Text => $"Reply with exactly {Marker}.",
            ProbeKind.Tool => $"Call {FunctionName} with status {Marker}.",
            ProbeKind.Image => new object[]
            {
                new { type = "text", text = "What color fills this image? Reply with one color word." },
                new { type = "image_url", image_url = new { url = RedImage } }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var payload = new Dictionary<string, object>
        {
            ["model"] = LocalGateway.Model,
            ["messages"] = new[] { new { role = "user", content } },
            ["max_tokens"] = MaxOutputTokens,
            ["stream"] = false,
            ["reasoning"] = new { enabled = false },
            ["provider"] = new
            {
                only = new[] { LocalGateway.Provider },
                allow_fallbacks = false,
                require_parameters = true,
                max_price = new { prompt = 0.18m, completion = 0.72m, request = 0m, image = 0m }
            }
        };
        if (kind == ProbeKind.Tool)
        {
            payload["tools"] = new[] { new
            {
                type = "function",
                function = new
                {
                    name = FunctionName,
                    description = "Record the synthetic probe status.",
                    parameters = new
                    {
                        type = "object",
                        properties = new { status = new { type = "string" } },
                        required = new[] { "status" }
                    }
                }
            } };
            payload["tool_choice"] = new { type = "function", function = new { name = FunctionName } };
        }
        return payload;
    }

    private static bool HasExpectedToolCall(ToolCall[]? calls)
    {
        if (calls is not [{ Function: { Name: FunctionName, Arguments: { } arguments } }])
            return false;
        try
        {
            return JsonSerializer.Deserialize<ToolArguments>(arguments, Json)?.Status == Marker;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Completion(string? Id, Choice[]? Choices, Usage? Usage);
    private sealed record Choice(Message? Message);
    private sealed record Message(string? Content, ToolCall[]? ToolCalls);
    private sealed record ToolCall(ToolFunction? Function);
    private sealed record ToolFunction(string? Name, string? Arguments);
    private sealed record ToolArguments(string? Status);
    private sealed record Usage(int PromptTokens, int CompletionTokens, decimal? Cost);
    private sealed record KeyLookup(KeyData? Data);
    private sealed record KeyData(decimal? Limit, decimal? LimitRemaining);
    private sealed record Lookup(Generation? Data);
    private sealed record Generation(string? Id, string? Model, string? ProviderName, decimal? TotalCost,
        bool? Cancelled, bool? IsByok, int? NativeTokensPrompt, int? NativeTokensCompletion,
        int? TokensPrompt, int? TokensCompletion, string? FinishReason);
}

internal sealed record ProbeResult(string Check, string GenerationId, string Model, string Provider,
    int PromptTokens, int CompletionTokens, decimal UsageCostUsd, decimal AccountedCostUsd);

internal sealed record GenerationAccounting(string GenerationId, string? Model, string? Provider, bool ExpectedRoute,
    int? PromptTokens, int? CompletionTokens, decimal? AccountedCostUsd, string? FinishReason,
    bool? IsByok, bool? Cancelled);
