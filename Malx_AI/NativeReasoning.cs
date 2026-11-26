using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Malx_AI
{
    /// <summary>What a model's provider says about its built-in thinking.</summary>
    /// <param name="Supported">The model has a native thinking (reasoning) phase.</param>
    /// <param name="Mandatory">It always thinks; asking it not to is an error on some providers.</param>
    /// <param name="Efforts">Effort levels the provider accepts, e.g. low, medium, high, xhigh, none.</param>
    /// <param name="DefaultEnabled">Whether it thinks when nothing is said; null when not stated.</param>
    public sealed record NativeReasoningInfo(bool Supported, bool Mandatory, IReadOnlyList<string> Efforts, bool? DefaultEnabled)
    {
        public static NativeReasoningInfo None { get; } = new(false, false, [], null);

        public string Describe() => !Supported ? "no native thinking"
            : (Mandatory ? "always thinks" : "thinking can be switched")
              + (Efforts.Count > 0 ? " (efforts: " + string.Join(", ", Efforts) + ")" : string.Empty);
    }

    public enum NativeReasoningMode
    {
        /// <summary>Send nothing about reasoning.</summary>
        Omit,

        /// <summary>Ask for no thinking (reasoning:{enabled:false}).</summary>
        Disable,

        /// <summary>Ask for thinking at <see cref="NativeReasoningDecision.Effort"/>.</summary>
        Enable
    }

    public sealed record NativeReasoningDecision(NativeReasoningMode Mode, string? Effort)
    {
        public string Describe() => Mode switch
        {
            NativeReasoningMode.Enable => "thinking on" + (Effort != null ? $" (effort {Effort})" : string.Empty),
            NativeReasoningMode.Disable => "thinking off",
            _ => "provider default"
        };
    }

    /// <summary>
    /// Makes a model's own thinking and Axiom's effort levels work together instead of against
    /// each other.
    /// </summary>
    /// <remarks>
    /// Owner request (2026-11-26). Before this, Axiom decided "thinking on/off" from the effort
    /// level alone and mapped High, Extra High and Ultra all to the same "high". Two conflicts
    /// followed: a model that always thinks (DeepSeek R1, GPT-5 Codex, Qwen3 Thinking) was told
    /// to stop thinking at Light and Medium, which some providers reject outright, and the top
    /// effort levels never reached a model's own top setting (GPT-5.1 Codex Max accepts xhigh).
    /// OpenRouter's model list now says per model whether reasoning is mandatory and which
    /// efforts it accepts (the "reasoning" object); this reads it and decides the request.
    /// </remarks>
    public static class NativeReasoning
    {
        private static readonly string[] EffortOrder = ["none", "minimal", "low", "medium", "high", "xhigh"];

        /// <summary>Reads one entry of OpenRouter's /api/v1/models list.</summary>
        public static NativeReasoningInfo FromOpenRouterModel(JsonElement model)
        {
            bool listsParameter = false;
            if (model.TryGetProperty("supported_parameters", out JsonElement parameters) && parameters.ValueKind == JsonValueKind.Array)
            {
                listsParameter = parameters.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String
                    && (string.Equals(p.GetString(), "reasoning", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.GetString(), "include_reasoning", StringComparison.OrdinalIgnoreCase)));
            }

            if (!model.TryGetProperty("reasoning", out JsonElement reasoning) || reasoning.ValueKind != JsonValueKind.Object)
                return listsParameter ? new NativeReasoningInfo(true, false, [], null) : NativeReasoningInfo.None;

            bool mandatory = reasoning.TryGetProperty("mandatory", out JsonElement m) && m.ValueKind == JsonValueKind.True;
            bool? defaultEnabled = reasoning.TryGetProperty("default_enabled", out JsonElement d)
                && d.ValueKind is JsonValueKind.True or JsonValueKind.False ? d.GetBoolean() : null;
            var efforts = new List<string>();
            if (reasoning.TryGetProperty("supported_efforts", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in list.EnumerateArray())
                {
                    string value = (item.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                    if (value.Length > 0 && !efforts.Contains(value))
                        efforts.Add(value);
                }
            }
            return new NativeReasoningInfo(true, mandatory, efforts, defaultEnabled);
        }

        /// <summary>
        /// Native thinking signals in a chat template (GGUF tokenizer.chat_template, or a
        /// llama.cpp server's /props). Returns (has thinking, can be switched off).
        /// </summary>
        public static (bool Thinks, bool Switchable) FromChatTemplate(string? template)
        {
            if (string.IsNullOrWhiteSpace(template))
                return (false, false);
            bool switchable = template.Contains("enable_thinking", StringComparison.Ordinal)
                || template.Contains("reasoning_effort", StringComparison.Ordinal)
                || template.Contains("/no_think", StringComparison.Ordinal);
            bool thinks = switchable
                || template.Contains("<think>", StringComparison.Ordinal)
                || template.Contains("</think>", StringComparison.Ordinal)
                || template.Contains("<|channel|>analysis", StringComparison.Ordinal)
                || template.Contains("reasoning_content", StringComparison.Ordinal);
            return (thinks, switchable);
        }

        /// <summary>
        /// The effort Axiom asks for at each level, before it is fitted to what the model accepts.
        /// Ultra asks for the model's top setting (xhigh where offered).
        /// </summary>
        public static string RequestedEffort(EffortLevel level) => level switch
        {
            EffortLevel.Light => "low",
            EffortLevel.Medium => "medium",
            EffortLevel.High => "high",
            EffortLevel.ExtraHigh => "high",
            EffortLevel.Ultra => "xhigh",
            _ => "medium"
        };

        /// <summary>
        /// What to send. Thinking is wanted when the user turned it on or the effort level asks for
        /// it; a model that cannot stop thinking is never told to, it is given a low effort instead.
        /// </summary>
        public static NativeReasoningDecision Decide(NativeReasoningInfo info, bool thinkingWanted, EffortLevel level)
        {
            if (!info.Supported)
                return new(NativeReasoningMode.Omit, null);

            if (!thinkingWanted)
            {
                if (!info.Mandatory)
                    return new(NativeReasoningMode.Disable, null);
                // Always-thinking model: keep its thinking short rather than refuse it.
                return new(NativeReasoningMode.Enable, Fit(info.Efforts, "low"));
            }

            return new(NativeReasoningMode.Enable, Fit(info.Efforts, RequestedEffort(level)));
        }

        /// <summary>
        /// The nearest accepted effort to the one wanted, preferring the next lower one ("xhigh" on a
        /// model that tops out at "high" becomes "high"). With no list, plain low/medium/high are
        /// assumed and "xhigh" is sent as "high".
        /// </summary>
        public static string? Fit(IReadOnlyList<string> accepted, string wanted)
        {
            int wantedRank = Array.IndexOf(EffortOrder, wanted);
            if (accepted == null || accepted.Count == 0)
                return wanted == "xhigh" ? "high" : wanted == "minimal" ? "low" : wanted;

            var ranked = accepted.Select(e => (Effort: e, Rank: Array.IndexOf(EffortOrder, e)))
                .Where(e => e.Rank > 0) // "none" is not a thinking effort
                .OrderBy(e => e.Rank)
                .ToList();
            if (ranked.Count == 0)
                return null;
            var atOrBelow = ranked.LastOrDefault(e => e.Rank <= wantedRank);
            return atOrBelow.Effort ?? ranked[0].Effort;
        }
    }
}
