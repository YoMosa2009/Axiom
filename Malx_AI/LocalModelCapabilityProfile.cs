using System;
using System.IO;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    internal enum LocalModelSizeClass
    {
        Unknown,
        SubOneB,
        OneBOrLarger
    }

    internal sealed class LocalModelCapabilityProfile
    {
        private const long OneBillionParameters = 1_000_000_000L;

        public LocalModelSizeClass SizeClass { get; init; } = LocalModelSizeClass.Unknown;
        public long? ParameterCount { get; init; }
        public string Evidence { get; init; } = string.Empty;

        public bool IsSubOneB => SizeClass == LocalModelSizeClass.SubOneB;

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
                SizeClass = count < OneBillionParameters
                    ? LocalModelSizeClass.SubOneB
                    : LocalModelSizeClass.OneBOrLarger
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
