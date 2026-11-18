using System;
using System.Globalization;
using System.Text.Json;

namespace Malx_AI;

public sealed record OpenRouterKeyInfo(
    int RequestsUsed, int RequestsLimit, bool FetchSucceeded,
    int RequestsRemaining = 0, bool HasDailyRequestUsage = false);

// /key.limit and /key.usage are USD amounts. Only this named request counter
// can supply the free-model quota; an absent spending cap is not unlimited requests.
internal static class OpenRouterKeyUsage
{
    public static OpenRouterKeyInfo Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
                return new(0, 0, false);

            if (!data.TryGetProperty("free_model_daily_requests", out var quota) ||
                quota.ValueKind != JsonValueKind.Object ||
                !ReadCount(quota, "used", out int used) ||
                !ReadCount(quota, "limit", out int limit))
                return new(0, 0, true);

            int remaining = Math.Max(0, limit - used);
            if (quota.TryGetProperty("remaining", out _))
            {
                if (!ReadCount(quota, "remaining", out remaining) || remaining > limit)
                    return new(0, 0, true);
            }

            return new(used, limit, true, remaining, HasDailyRequestUsage: true);
        }
        catch (JsonException) { return new(0, 0, false); }
        catch (ArgumentException) { return new(0, 0, false); }
    }

    private static bool ReadCount(JsonElement element, string name, out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var number) &&
            number.ValueKind == JsonValueKind.Number && number.TryGetInt32(out value) && value >= 0;
    }

    public static double RemainingPercent(OpenRouterKeyInfo info) =>
        info.FetchSucceeded && info.HasDailyRequestUsage && info.RequestsLimit > 0
            ? Math.Clamp(info.RequestsRemaining * 100d / info.RequestsLimit, 0, 100) : 0;

    public static string Label(OpenRouterKeyInfo info) =>
        !info.FetchSucceeded ? "Unable to refresh usage" :
        !info.HasDailyRequestUsage ? "Daily usage unavailable" :
        string.Create(CultureInfo.CurrentCulture,
            $"{RemainingPercent(info):0.#}% left · {info.RequestsRemaining:N0} / {info.RequestsLimit:N0} requests");

    public static bool IsFresh(DateTimeOffset? fetchedAt, DateTimeOffset now) =>
        fetchedAt is { } fetched && fetched.UtcDateTime.Date == now.UtcDateTime.Date &&
        now >= fetched && now - fetched < TimeSpan.FromSeconds(30);
}
