using System.Text.Json.Nodes;

namespace AxiomSubscriptionGateway;

/// <summary>
/// Pure builder for the OpenRouter <c>provider</c> request field. No I/O and no gateway dependencies,
/// so the routing policy can be unit-tested directly.
/// Default policy: cheapest healthy provider (<c>sort: price</c>) that supports every parameter the
/// request sends (<c>require_parameters</c>), with fallbacks so one provider outage does not fail a request.
/// Price ceilings come from the allowance rates so a fallback can never cost more than the ledger assumes.
/// </summary>
internal sealed record OpenRouterProviderRouting(
    string? Sort = "price",
    string[]? Only = null,
    string[]? Order = null,
    bool AllowFallbacks = true,
    bool RequireParameters = true,
    string? DataCollection = null,
    bool? Zdr = null,
    string[]? Quantizations = null)
{
    public const string SortEnv = "AXIOM_OPENROUTER_PROVIDER_SORT";
    public const string OnlyEnv = "AXIOM_OPENROUTER_PROVIDER_ONLY";
    public const string OrderEnv = "AXIOM_OPENROUTER_PROVIDER_ORDER";
    public const string AllowFallbacksEnv = "AXIOM_OPENROUTER_ALLOW_FALLBACKS";
    public const string RequireParametersEnv = "AXIOM_OPENROUTER_REQUIRE_PARAMETERS";
    public const string DataCollectionEnv = "AXIOM_OPENROUTER_DATA_COLLECTION";
    public const string ZdrEnv = "AXIOM_OPENROUTER_ZDR";
    public const string QuantizationsEnv = "AXIOM_OPENROUTER_QUANTIZATIONS";

    public static OpenRouterProviderRouting Default { get; } = new();

    /// <summary>
    /// Reads the routing policy from environment variables; unset or invalid values keep the default.
    /// AXIOM_OPENROUTER_PROVIDER_SORT=none disables sorting (OpenRouter load-balances instead).
    /// </summary>
    public static OpenRouterProviderRouting FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var routing = Default;

        string? sort = read(SortEnv)?.Trim().ToLowerInvariant();
        if (sort == "none" || sort == "off")
            routing = routing with { Sort = null };
        else if (sort == "price" || sort == "throughput" || sort == "latency")
            routing = routing with { Sort = sort };

        if (ParseList(read(OnlyEnv)) is { Length: > 0 } only)
            routing = routing with { Only = only };
        if (ParseList(read(OrderEnv)) is { Length: > 0 } order)
            routing = routing with { Order = order };
        if (ParseBool(read(AllowFallbacksEnv)) is bool fallbacks)
            routing = routing with { AllowFallbacks = fallbacks };
        if (ParseBool(read(RequireParametersEnv)) is bool requireParameters)
            routing = routing with { RequireParameters = requireParameters };
        string? dataCollection = read(DataCollectionEnv)?.Trim().ToLowerInvariant();
        if (dataCollection == "allow" || dataCollection == "deny")
            routing = routing with { DataCollection = dataCollection };
        if (ParseBool(read(ZdrEnv)) is bool zdr)
            routing = routing with { Zdr = zdr };
        if (ParseList(read(QuantizationsEnv)) is { Length: > 0 } quantizations)
            routing = routing with { Quantizations = quantizations };
        return routing;
    }

    public JsonObject ToJson(decimal maxPromptUsdPerMillion, decimal maxCompletionUsdPerMillion)
    {
        var provider = new JsonObject
        {
            ["allow_fallbacks"] = AllowFallbacks,
            ["require_parameters"] = RequireParameters
        };
        // "only" restricts the candidate set; "order" is a preference list and is skipped in favour of
        // sorting only when no explicit order is given (OpenRouter ignores order once sort is set).
        if (Only is { Length: > 0 })
            provider["only"] = ToArray(Only);
        if (Order is { Length: > 0 })
            provider["order"] = ToArray(Order);
        else if (Sort is { Length: > 0 })
            provider["sort"] = Sort;
        if (DataCollection is not null)
            provider["data_collection"] = DataCollection;
        if (Zdr is not null)
            provider["zdr"] = Zdr;
        if (Quantizations is { Length: > 0 })
            provider["quantizations"] = ToArray(Quantizations);
        provider["max_price"] = new JsonObject
        {
            ["prompt"] = maxPromptUsdPerMillion,
            ["completion"] = maxCompletionUsdPerMillion,
            ["request"] = 0m,
            ["image"] = 0m
        };
        return provider;
    }

    private static JsonArray ToArray(string[] values)
    {
        var array = new JsonArray();
        foreach (string value in values)
            array.Add(value);
        return array;
    }

    private static string[]? ParseList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? null
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool? ParseBool(string? raw)
        => raw?.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null
        };
}
