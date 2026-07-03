using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    internal enum LocalModelSizeClass
    {
        Unknown,
        SubOneB,      // < 1B — micro models: deterministic tool routing only, minimal prompts
        OneToFourB,   // 1B–4B — compact models: capable but easily distracted by long context
        FourToTenB,   // 4B–10B — mid-size models: full pipeline with standard budgets
        TenBPlus      // >= 10B — large models: full pipeline, no small-model constraints
    }

    internal sealed class LocalModelCapabilityProfile
    {
        private const long OneBillionParameters = 1_000_000_000L;

        // Families that emit a chain-of-thought phase before the deliverable (R1 distills, QwQ,
        // *-Thinking variants, GPT-OSS harmony, Magistral, EXAONE Deep, Nemotron hybrids,
        // SmolLM3, reasoning fine-tunes). "r1" is boundary-guarded so it never matches inside
        // version strings like "3.1-8b". Base Qwen3 hybrids are deliberately NOT matched: the
        // app disables their thinking with /no_think, so they need no reasoning headroom.
        private static readonly Regex ReasoningModelNameRegex = new(
            @"(?<![a-z0-9])r1(?![0-9])|qwq|think|reason|gpt[-_ ]?oss|magistral|exaone[-_ ]?deep|nemotron|smollm3",
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// True when the model name/path indicates a reasoning-tuned local model whose output
        /// begins with a thinking phase. Used to grant generation-budget headroom so the
        /// chain-of-thought cannot consume the entire deliverable budget, and to route
        /// think-aware retry guidance.
        /// </summary>
        public static bool IsLikelyReasoningModel(string? modelPathOrName, string? displayName = null)
        {
            static bool Matches(string? text)
            {
                if (string.IsNullOrWhiteSpace(text))
                    return false;
                string name = Path.GetFileNameWithoutExtension(text);
                if (string.IsNullOrWhiteSpace(name))
                    name = text;
                return ReasoningModelNameRegex.IsMatch(name);
            }

            return Matches(modelPathOrName) || Matches(displayName);
        }

        public LocalModelSizeClass SizeClass { get; init; } = LocalModelSizeClass.Unknown;
        public long? ParameterCount { get; init; }
        public string Evidence { get; init; } = string.Empty;

        public bool IsSubOneB => SizeClass == LocalModelSizeClass.SubOneB;

        /// <summary>
        /// True for 1B–4B models: strong enough for the full pipeline, but they lose the thread
        /// on very long histories, so they get a milder version of the sub-1B context trims.
        /// Unknown sizes stay false — never constrain a model we could not measure.
        /// </summary>
        public bool IsCompactClass => SizeClass == LocalModelSizeClass.OneToFourB;

        /// <summary>
        /// How many preflight tools the model itself may choose per Builder run (on top of the
        /// deterministic router, which is size-independent). Sub-1B models pick arbitrary tools
        /// when forced through a decision grammar — the tool line becomes their answer — so they
        /// get none. Compact models route usably but drift on the second decision. Unknown sizes
        /// keep full routing so an unmeasured large model is never handicapped.
        /// </summary>
        public int MaxModelChosenPreflightTools => SizeClass switch
        {
            LocalModelSizeClass.SubOneB => 0,
            LocalModelSizeClass.OneToFourB => 1,
            _ => 2
        };

        public static LocalModelCapabilityProfile FromModel(string? modelPathOrName)
        {
            if (string.IsNullOrWhiteSpace(modelPathOrName))
                return new LocalModelCapabilityProfile();

            try
            {
                if (File.Exists(modelPathOrName))
                {
                    GgufModelMetadata? metadata = GgufMetadataReader.TryRead(modelPathOrName);
                    if (metadata?.ParameterCount is > 0)
                    {
                        return FromParameterCount(
                            metadata.ParameterCount.Value,
                            "GGUF general.parameter_count");
                    }

                    if (TryParseParameterCount(metadata?.SizeLabel, out long labelCount))
                        return FromParameterCount(labelCount, "GGUF general.size_label");
                }
            }
            catch
            {
                // Capability profiling is an optimization hint. Unknown is safer than failing load.
            }

            string name = Path.GetFileNameWithoutExtension(modelPathOrName);
            if (TryParseParameterCount(name, out long fileNameCount))
                return FromParameterCount(fileNameCount, "model filename");

            return new LocalModelCapabilityProfile();
        }

        private static LocalModelCapabilityProfile FromParameterCount(long count, string evidence)
        {
            return new LocalModelCapabilityProfile
            {
                ParameterCount = count,
                Evidence = evidence,
                SizeClass = count switch
                {
                    < OneBillionParameters => LocalModelSizeClass.SubOneB,
                    < 4 * OneBillionParameters => LocalModelSizeClass.OneToFourB,
                    < 10 * OneBillionParameters => LocalModelSizeClass.FourToTenB,
                    _ => LocalModelSizeClass.TenBPlus
                }
            };
        }

        private static bool TryParseParameterCount(string? text, out long parameterCount)
        {
            parameterCount = 0;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            Match match = Regex.Match(
                text,
                @"(?<![A-Za-z0-9])(?<value>\d+(?:[._]\d+)?|\d+)\s*(?<unit>[bm])\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (!match.Success)
                return false;

            string valueText = match.Groups["value"].Value.Replace('_', '.');
            if (!double.TryParse(valueText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
                return false;

            string unit = match.Groups["unit"].Value.ToLowerInvariant();
            double multiplier = unit == "b" ? 1_000_000_000d : 1_000_000d;
            parameterCount = (long)Math.Round(value * multiplier);
            return parameterCount > 0;
        }
    }
}
