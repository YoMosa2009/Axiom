using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Malx_AI;

/// <summary>What went wrong with a model request, in terms the user can act on.</summary>
internal enum RunFailureKind
{
    Unknown,
    RateLimit,
    QuotaExhausted,
    ContextTooLong,
    Authentication,
    Network,
    ServerError,
    Cancelled,
}

/// <summary>A button on the failure card.</summary>
internal enum RunFailureAction
{
    Retry,
    SwitchModel,
    CompactAndRetry,
    OpenSettings,
}

/// <summary>Everything the failure card shows. <see cref="RetryAfterSeconds"/> is null when unknown.</summary>
internal sealed record RunFailureAdvice(
    RunFailureKind Kind,
    string Title,
    string Hint,
    IReadOnlyList<RunFailureAction> Actions,
    int? RetryAfterSeconds);

/// <summary>
/// Turns a raw provider error (HTTP status and/or message text) into a short title, a one-line
/// hint and the actions that make sense, the way OpenCode and Kimi show a limit card instead of a
/// raw exception. Dependency-free so the tests can link it. It never throws and never echoes the
/// raw message, which can contain request ids or fragments of a key.
/// </summary>
internal static class RunFailureAdvisor
{
    private static readonly Regex RetryAfterRegex = new(
        @"(?:retry[\s-]*after|try again in|wait)\D{0,12}(?<n>\d{1,5})\s*(?<u>ms|milliseconds?|s|sec(?:onds?)?|m|min(?:utes?)?)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static RunFailureAdvice Classify(int? httpStatus, string? message)
    {
        string text = (message ?? string.Empty).ToLowerInvariant();

        if (text.Contains("operation was canceled") || text.Contains("operation was cancelled")
            || text.Contains("task was canceled") || text.Contains("stopped by user"))
        {
            return new RunFailureAdvice(RunFailureKind.Cancelled, "Stopped",
                "The run was stopped before the model answered.",
                new[] { RunFailureAction.Retry }, null);
        }

        if (httpStatus == 413 || ContainsAny(text, "context length", "context window", "maximum context",
                "too many tokens", "prompt is too long", "context_length_exceeded", "reduce the length"))
        {
            return new RunFailureAdvice(RunFailureKind.ContextTooLong, "Conversation too long for this model",
                "Compact the older turns and retry, or switch to a model with a larger window.",
                new[] { RunFailureAction.CompactAndRetry, RunFailureAction.SwitchModel }, null);
        }

        if (httpStatus == 402 || ContainsAny(text, "insufficient credit", "insufficient_quota", "quota exceeded",
                "out of credits", "billing", "payment required", "exceeded your current quota"))
        {
            return new RunFailureAdvice(RunFailureKind.QuotaExhausted, "Usage limit reached",
                "This model's quota or credit is used up. Switch model, or top up in Settings.",
                new[] { RunFailureAction.SwitchModel, RunFailureAction.OpenSettings }, null);
        }

        if (httpStatus == 429 || ContainsAny(text, "rate limit", "rate_limit", "too many requests", "overloaded"))
        {
            int? wait = ParseRetryAfterSeconds(text);
            string hint = wait is int w
                ? $"The provider is rate limiting requests. Retry in about {w} s, or switch model."
                : "The provider is rate limiting requests. Retry in a moment, or switch model.";
            return new RunFailureAdvice(RunFailureKind.RateLimit, "Rate limited",
                hint, new[] { RunFailureAction.Retry, RunFailureAction.SwitchModel }, wait);
        }

        if (httpStatus is 401 or 403 || ContainsAny(text, "invalid api key", "incorrect api key", "unauthorized",
                "authentication", "invalid_api_key", "not signed in"))
        {
            return new RunFailureAdvice(RunFailureKind.Authentication, "Sign-in or API key problem",
                "The provider rejected the credentials. Check them in Settings.",
                new[] { RunFailureAction.OpenSettings, RunFailureAction.Retry }, null);
        }

        if (httpStatus is >= 500 and <= 599 || ContainsAny(text, "bad gateway", "service unavailable",
                "gateway timeout", "internal server error"))
        {
            return new RunFailureAdvice(RunFailureKind.ServerError, "The model server had a problem",
                "This is usually temporary. Retry, or switch model.",
                new[] { RunFailureAction.Retry, RunFailureAction.SwitchModel }, null);
        }

        if (ContainsAny(text, "no such host", "connection refused", "connection reset", "timed out",
                "timeout", "network is unreachable", "an error occurred while sending the request", "name or service not known"))
        {
            return new RunFailureAdvice(RunFailureKind.Network, "Can't reach the model",
                "Check your connection (and the server address for Hybrid Local), then retry.",
                new[] { RunFailureAction.Retry, RunFailureAction.OpenSettings }, null);
        }

        return new RunFailureAdvice(RunFailureKind.Unknown, "The request failed",
            "Retry, or switch model if it keeps happening. Details are in the backend log.",
            new[] { RunFailureAction.Retry, RunFailureAction.SwitchModel }, null);
    }

    /// <summary>Seconds named by a "retry after" style phrase, clamped to 1..3600; null if none.</summary>
    public static int? ParseRetryAfterSeconds(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        Match m = RetryAfterRegex.Match(text);
        if (!m.Success || !int.TryParse(m.Groups["n"].Value, out int n) || n <= 0)
            return null;

        string unit = m.Groups["u"].Value.ToLowerInvariant();
        double seconds = unit switch
        {
            "ms" or "millisecond" or "milliseconds" => Math.Ceiling(n / 1000.0),
            "m" or "min" or "minute" or "minutes" => n * 60.0,
            _ => n,
        };
        return (int)Math.Clamp(seconds, 1, 3600);
    }

    private static bool ContainsAny(string text, params string[] needles)
    {
        foreach (string n in needles)
            if (text.Contains(n, StringComparison.Ordinal))
                return true;
        return false;
    }

    public static string ActionLabel(RunFailureAction action) => action switch
    {
        RunFailureAction.Retry => "Retry",
        RunFailureAction.SwitchModel => "Switch model",
        RunFailureAction.CompactAndRetry => "Compact and retry",
        RunFailureAction.OpenSettings => "Open settings",
        _ => action.ToString(),
    };
}
