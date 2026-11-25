using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Malx_AI
{
    /// <summary>Sampling settings. A null field means "leave whatever Axiom sends today".</summary>
    public sealed record ModelSampling(
        double Temperature,
        double? TopP = null,
        int? TopK = null,
        double? MinP = null,
        double? PresencePenalty = null,
        double? RepeatPenalty = null);

    /// <summary>How a family's thinking phase is switched, as its chat template documents it.</summary>
    public enum ThinkingControl
    {
        /// <summary>Not documented for the family; Axiom keeps its current handling.</summary>
        Unknown,

        /// <summary>No thinking phase.</summary>
        None,

        /// <summary>Qwen3: enable_thinking in the template, plus the /think and /no_think soft switches.</summary>
        QwenSoftSwitch,

        /// <summary>Thinking on by default; turned off only through chat_template_kwargs.enable_thinking (Qwen3.6, GLM-4.7). No /think.</summary>
        TemplateKwarg,

        /// <summary>Always reasons (R1 distills).</summary>
        AlwaysOn,

        /// <summary>GPT-OSS harmony: "Reasoning: low|medium|high" in the system prompt.</summary>
        HarmonyEffort
    }

    /// <summary>Which sampling profile a call wants.</summary>
    public enum ModelUse
    {
        General,

        /// <summary>Agent tool turns that write and edit code: the family's "precise coding" profile when it has one.</summary>
        Coding
    }

    /// <summary>
    /// One reviewed model family: how its ids are recognised and the settings its own model card
    /// recommends. Only facts with a primary source go in (DeepSeek-TUI's rule for its local
    /// reasoning registry); a field the card does not give stays null.
    /// </summary>
    public sealed record ModelFamilySettings(
        string Id,
        IReadOnlyList<string> Prefixes,
        string Source,
        ModelSampling? Thinking = null,
        ModelSampling? NonThinking = null,
        ModelSampling? CodingThinking = null,
        ThinkingControl ThinkingControl = ThinkingControl.Unknown,
        bool ReturnsReasoningWithTools = false,
        bool IgnoresSamplingWhenThinking = false)
    {
        /// <summary>True for an entry from the user's own model_settings.json.</summary>
        public bool FromUser { get; init; }
    }

    /// <summary>What the registry knows about one model id.</summary>
    public sealed record ModelSettings(ModelFamilySettings? Family)
    {
        public static ModelSettings Unknown { get; } = new((ModelFamilySettings?)null);

        public bool IsKnown => Family != null;

        /// <summary>
        /// The sampling to use, or null to keep Axiom's current behaviour (unknown model, or the
        /// card gives no recommendation for this mode).
        /// </summary>
        public ModelSampling? SamplingFor(bool thinking, ModelUse use = ModelUse.General)
        {
            if (Family == null)
                return null;
            if (thinking)
                return (use == ModelUse.Coding ? Family.CodingThinking : null) ?? Family.Thinking;
            return Family.NonThinking;
        }
    }

    /// <summary>
    /// A small, reviewed registry of per-model settings layered over Axiom's size classes.
    /// </summary>
    /// <remarks>
    /// Research (2026-11-25): OpenCode keys sampling off loose substring checks, Codex keeps a
    /// per-slug catalog matched by longest prefix, and DeepSeek-TUI keeps a reviewed table with
    /// pinned sources where unknown ids keep current behaviour. This follows the last two:
    /// longest prefix wins, a prefix must end at a separator (so "qwen3" does not claim
    /// "qwen3.5", which has no reviewed entry), and an unmatched id changes nothing.
    /// Precedence: the user's model_settings.json, then these entries, then the size class
    /// defaults already in Axiom. Context windows and tool format stay with their current owners
    /// (GGUF metadata, server metadata, size class): no card gave a reviewed reason to change them.
    /// </remarks>
    public static class ModelSettingsRegistry
    {
        /// <summary>Reviewed 2026-11-25 against each model card / API doc named in Source.</summary>
        public static IReadOnlyList<ModelFamilySettings> BuiltIn { get; } =
        [
            new("qwen3.6", ["qwen3.6", "qwen3_6"],
                "https://huggingface.co/Qwen/Qwen3.6-35B-A3B (model card, best practices)",
                Thinking: new(1.0, TopP: 0.95, TopK: 20, MinP: 0.0, PresencePenalty: 1.5),
                NonThinking: new(0.7, TopP: 0.8, TopK: 20, MinP: 0.0, PresencePenalty: 1.5),
                CodingThinking: new(0.6, TopP: 0.95, TopK: 20, MinP: 0.0, PresencePenalty: 0.0),
                ThinkingControl: ThinkingControl.TemplateKwarg),

            new("qwen3-coder", ["qwen3-coder", "qwen3_coder"],
                "https://huggingface.co/Qwen/Qwen3-Coder-30B-A3B-Instruct (model card, best practices)",
                NonThinking: new(0.7, TopP: 0.8, TopK: 20, RepeatPenalty: 1.05),
                ThinkingControl: ThinkingControl.None),

            new("qwen3", ["qwen3"],
                "https://huggingface.co/Qwen/Qwen3-8B (model card, best practices)",
                Thinking: new(0.6, TopP: 0.95, TopK: 20, MinP: 0.0),
                NonThinking: new(0.7, TopP: 0.8, TopK: 20, MinP: 0.0),
                ThinkingControl: ThinkingControl.QwenSoftSwitch),

            new("gemma-4", ["gemma-4", "gemma4", "gemma_4"],
                "https://huggingface.co/google/gemma-4-31B-it (model card, sampling for all use cases)",
                Thinking: new(1.0, TopP: 0.95, TopK: 64),
                NonThinking: new(1.0, TopP: 0.95, TopK: 64)),

            new("glm-4.7", ["glm-4.7", "glm4.7", "glm-4_7"],
                "https://huggingface.co/zai-org/GLM-4.7 (model card: default 1.0/0.95; terminal and SWE tasks 0.7/1.0)",
                Thinking: new(1.0, TopP: 0.95),
                CodingThinking: new(0.7, TopP: 1.0),
                ThinkingControl: ThinkingControl.TemplateKwarg),

            new("devstral", ["devstral"],
                "https://huggingface.co/mistralai/Devstral-Small-2507 (model card, vLLM example temperature 0.15)",
                NonThinking: new(0.15),
                ThinkingControl: ThinkingControl.None),

            new("mistral-small-3.2", ["mistral-small-3.2", "mistral-small-3_2"],
                "https://huggingface.co/mistralai/Mistral-Small-3.2-24B-Instruct-2506 (model card, temperature 0.15)",
                NonThinking: new(0.15),
                ThinkingControl: ThinkingControl.None),

            new("deepseek-r1-distill", ["deepseek-r1-distill"],
                "https://huggingface.co/deepseek-ai/DeepSeek-R1-Distill-Qwen-32B (usage recommendations: temperature 0.5-0.7, 0.6 suggested)",
                Thinking: new(0.6, TopP: 0.95),
                ThinkingControl: ThinkingControl.AlwaysOn),

            new("gpt-oss", ["gpt-oss", "gpt_oss", "gptoss"],
                "https://huggingface.co/openai/gpt-oss-20b (harmony format; reasoning level in the system prompt; no sampling recommendation)",
                ThinkingControl: ThinkingControl.HarmonyEffort),

            // DeepSeek's own API (Axiom Cloud runs a DeepSeek model): thinking mode ignores
            // temperature and penalties, and with tools every earlier reasoning_content must be
            // sent back. https://api-docs.deepseek.com/guides/thinking_mode
            new("deepseek", ["deepseek"],
                "https://api-docs.deepseek.com/guides/thinking_mode",
                ReturnsReasoningWithTools: true,
                IgnoresSamplingWhenThinking: true),
        ];

        /// <summary>
        /// The settings for a model id, file name or path. User entries are checked first, then
        /// the built-in ones; the longest matching prefix wins.
        /// </summary>
        public static ModelSettings Resolve(string? modelIdOrPath, IReadOnlyList<ModelFamilySettings>? userEntries = null)
        {
            string name = NormalizeId(modelIdOrPath);
            if (name.Length == 0)
                return ModelSettings.Unknown;

            ModelFamilySettings? best = BestMatch(name, userEntries) ?? BestMatch(name, BuiltIn);
            return best == null ? ModelSettings.Unknown : new ModelSettings(best);
        }

        private static ModelFamilySettings? BestMatch(string name, IReadOnlyList<ModelFamilySettings>? entries)
        {
            if (entries == null)
                return null;
            ModelFamilySettings? best = null;
            int bestLength = 0;
            foreach (ModelFamilySettings entry in entries)
            {
                foreach (string prefix in entry.Prefixes)
                {
                    string p = prefix.Trim().ToLowerInvariant();
                    if (p.Length > bestLength && MatchesPrefix(name, p))
                    {
                        best = entry;
                        bestLength = p.Length;
                    }
                }
            }

            return best;
        }

        /// <summary>The prefix must end at the end of the name or at a separator: "qwen3" matches "qwen3-8b" and "qwen3:8b", not "qwen3.5" or "qwen30".</summary>
        internal static bool MatchesPrefix(string name, string prefix)
        {
            if (prefix.Length == 0 || !name.StartsWith(prefix, StringComparison.Ordinal))
                return false;
            if (name.Length == prefix.Length)
                return true;
            char next = name[prefix.Length];
            return next is '-' or '_' or ':' or ' ' or '@';
        }

        /// <summary>
        /// Lower case, file name without .gguf, and without a "namespace/" (OpenRouter
        /// "qwen/qwen3-8b", Hugging Face "unsloth/Qwen3-8B-GGUF") or a Windows folder.
        /// </summary>
        internal static string NormalizeId(string? modelIdOrPath)
        {
            string text = (modelIdOrPath ?? string.Empty).Trim();
            if (text.Length == 0)
                return string.Empty;
            text = text.Replace('\\', '/');
            int slash = text.LastIndexOf('/');
            if (slash >= 0)
                text = text[(slash + 1)..];
            if (text.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
                text = text[..^5];
            return text.Trim().ToLowerInvariant();
        }

        /// <summary>
        /// Reads the user's overrides (profile\model_settings.json), an array of
        /// { "match": ["prefix", ...], "temperature": 0.6, "top_p": 0.95, "top_k": 20,
        ///   "min_p": 0, "presence_penalty": 0, "repeat_penalty": 1.05 }.
        /// The same sampling is used with and without thinking. Invalid entries are skipped;
        /// invalid JSON gives none (never a half-read file).
        /// </summary>
        public static IReadOnlyList<ModelFamilySettings> ParseUserOverrides(string? json, out string? error)
        {
            error = null;
            var entries = new List<ModelFamilySettings>();
            if (string.IsNullOrWhiteSpace(json))
                return entries;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                JsonElement root = document.RootElement;
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out JsonElement models))
                    root = models;
                if (root.ValueKind != JsonValueKind.Array)
                {
                    error = "model_settings.json must be a list of entries.";
                    return [];
                }

                int index = 0;
                foreach (JsonElement item in root.EnumerateArray())
                {
                    index++;
                    if (item.ValueKind != JsonValueKind.Object)
                        continue;
                    var prefixes = new List<string>();
                    if (item.TryGetProperty("match", out JsonElement match))
                    {
                        if (match.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(match.GetString()))
                            prefixes.Add(NormalizeId(match.GetString()));
                        else if (match.ValueKind == JsonValueKind.Array)
                            prefixes.AddRange(match.EnumerateArray()
                                .Where(value => value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                                .Select(value => NormalizeId(value.GetString())));
                    }

                    double? temperature = ReadNumber(item, "temperature");
                    if (prefixes.Count == 0 || temperature is null or < 0 or > 2)
                        continue;
                    double? topP = ReadNumber(item, "top_p");
                    double? minP = ReadNumber(item, "min_p");
                    double? topK = ReadNumber(item, "top_k");
                    var sampling = new ModelSampling(
                        temperature.Value,
                        topP is > 0 and <= 1 ? topP : null,
                        topK is >= 1 and <= 1000 ? (int)topK.Value : null,
                        minP is >= 0 and < 1 ? minP : null,
                        ReadNumber(item, "presence_penalty") is double presence and >= -2 and <= 2 ? presence : null,
                        ReadNumber(item, "repeat_penalty") is double repeat and > 0 and <= 2 ? repeat : null);
                    entries.Add(new ModelFamilySettings("user:" + prefixes[0], prefixes, "model_settings.json entry " + index,
                        Thinking: sampling, NonThinking: sampling) { FromUser = true });
                }
            }
            catch (JsonException ex)
            {
                error = "model_settings.json is not valid JSON: " + ex.Message;
                return [];
            }

            return entries;
        }

        /// <summary>
        /// Applies the registry to an OpenAI-style chat request. Only two things change:
        /// a self-hosted (Hybrid Local) server gets its family's sampling, including top_k /
        /// min_p / presence_penalty, which llama.cpp and vLLM read; and a family that ignores
        /// sampling in thinking mode (DeepSeek) gets none on thinking turns. OpenRouter's curated
        /// profiles keep their own tuning. An unknown model leaves the payload untouched.
        /// </summary>
        public static void ApplyToRequest(System.Text.Json.Nodes.JsonObject payload, string? modelId, bool thinking, bool coding,
            bool customEndpoint, IReadOnlyList<ModelFamilySettings>? userEntries = null)
        {
            ModelSettings settings = Resolve(modelId, userEntries);
            if (!settings.IsKnown)
                return;

            if (thinking && settings.Family!.IgnoresSamplingWhenThinking)
            {
                payload.Remove("temperature");
                payload.Remove("top_p");
                payload.Remove("presence_penalty");
                payload.Remove("frequency_penalty");
                return;
            }

            if (!customEndpoint)
                return;

            ModelSampling? sampling = settings.SamplingFor(thinking, coding ? ModelUse.Coding : ModelUse.General);
            if (sampling == null)
                return;

            payload["temperature"] = sampling.Temperature;
            if (sampling.TopP is double topP)
                payload["top_p"] = topP;
            if (sampling.TopK is int topK)
                payload["top_k"] = topK;
            if (sampling.MinP is double minP)
                payload["min_p"] = minP;
            if (sampling.PresencePenalty is double presence)
                payload["presence_penalty"] = presence;
            if (sampling.RepeatPenalty is double repeat)
                payload["repeat_penalty"] = repeat;
        }

        private static double? ReadNumber(JsonElement item, string name) =>
            item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number)
                ? number
                : null;

        // ---- loaded once per process from the profile; the app sets the path ----

        private static IReadOnlyList<ModelFamilySettings> _userEntries = [];
        private static string? _userPath;
        private static DateTime _userStamp;

        /// <summary>The user's entries, re-read when the file changes. Never throws.</summary>
        public static IReadOnlyList<ModelFamilySettings> UserEntries(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return [];
            try
            {
                if (!File.Exists(path))
                {
                    _userEntries = [];
                    _userPath = path;
                    return _userEntries;
                }

                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (!string.Equals(path, _userPath, StringComparison.OrdinalIgnoreCase) || stamp != _userStamp)
                {
                    _userEntries = ParseUserOverrides(File.ReadAllText(path), out _);
                    _userPath = path;
                    _userStamp = stamp;
                }

                return _userEntries;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
}
