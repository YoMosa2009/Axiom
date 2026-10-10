namespace Malx_AI
{
    /// <summary>Size and speed of one model reply; the same numbers for Local, Cloud and Hybrid Local.</summary>
    internal sealed record GenerationStatsSnapshot(
        int OutputTokens,
        bool TokensAreEstimated,
        double TokensPerSecond,
        double ElapsedSeconds,
        double? FirstTokenSeconds);

    /// <summary>Tracks one reply as it streams. Times are passed in so it can be tested.</summary>
    internal sealed class GenerationSpeedTracker
    {
        private DateTime? _start;
        private DateTime? _firstOutput;
        private DateTime? _lastOutput;
        private int _characters;
        private int _countedTokens;

        public bool HasStarted => _start != null;

        public void Begin(DateTime now)
        {
            _start = now;
            _firstOutput = null;
            _lastOutput = null;
            _characters = 0;
            _countedTokens = 0;
        }

        public void RecordCharacters(int totalCharactersSoFar, DateTime now)
        {
            if (_start == null || totalCharactersSoFar <= 0)
                return;
            if (_firstOutput == null)
                _firstOutput = now;
            _characters = Math.Max(_characters, totalCharactersSoFar);
            _lastOutput = now;
        }

        public void RecordTokens(int totalTokensSoFar, DateTime now)
        {
            if (_start == null || totalTokensSoFar <= 0)
                return;
            if (_firstOutput == null)
                _firstOutput = now;
            _countedTokens = Math.Max(_countedTokens, totalTokensSoFar);
            _lastOutput = now;
        }

        public GenerationStatsSnapshot Snapshot(DateTime now, int reportedCompletionTokens = 0)
        {
            if (_start == null)
                return new GenerationStatsSnapshot(0, true, 0, 0, null);

            int tokens = reportedCompletionTokens > 0
                ? reportedCompletionTokens
                : _countedTokens > 0
                    ? _countedTokens
                    : GenerationStatsFormatter.EstimateTokens(_characters);

            bool estimated = !(reportedCompletionTokens > 0 || _countedTokens > 0);

            double generationSeconds = (_firstOutput != null && _lastOutput != null)
                ? (_lastOutput.Value - _firstOutput.Value).TotalSeconds
                : 0;

            double tokensPerSecond = generationSeconds >= 0.25 ? tokens / generationSeconds : 0;

            double elapsedSeconds = ((_lastOutput ?? now) - _start.Value).TotalSeconds;

            double? firstTokenSeconds = _firstOutput == null
                ? null
                : (_firstOutput.Value - _start.Value).TotalSeconds;

            return new GenerationStatsSnapshot(tokens, estimated, tokensPerSecond, elapsedSeconds, firstTokenSeconds);
        }
    }

    internal static class GenerationStatsFormatter
    {
        public static int EstimateTokens(int characterCount)
        {
            if (characterCount <= 0)
                return 0;
            return (int)Math.Ceiling(characterCount / 4.0);
        }

        public static string FormatCount(int tokens)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (tokens < 1000)
                return tokens.ToString(invariant);
            if (tokens < 10000)
                return (tokens / 1000.0).ToString("0.#", invariant) + "k";
            if (tokens < 1000000)
                return Math.Round(tokens / 1000.0).ToString("0", invariant) + "k";
            return (tokens / 1000000.0).ToString("0.#", invariant) + "M";
        }

        public static string FormatSpeed(double tokensPerSecond)
        {
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            if (tokensPerSecond <= 0 || double.IsNaN(tokensPerSecond) || double.IsInfinity(tokensPerSecond))
                return "";
            if (tokensPerSecond < 10)
                return tokensPerSecond.ToString("0.0", invariant) + " tok/s";
            return Math.Round(tokensPerSecond).ToString("0", invariant) + " tok/s";
        }

        public static string FormatSummary(GenerationStatsSnapshot stats)
        {
            if (stats.OutputTokens <= 0)
                return "";

            string count = (stats.TokensAreEstimated ? "~" : "") + FormatCount(stats.OutputTokens) + " tokens";
            string speed = FormatSpeed(stats.TokensPerSecond);

            return speed == "" ? count : speed + " \u00B7 " + count;
        }

        public static string FormatTooltip(GenerationStatsSnapshot stats, string modeLabel)
        {
            if (stats.OutputTokens <= 0)
                return "";

            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            var lines = new List<string>();

            lines.Add($"Last reply ({modeLabel}): {(stats.TokensAreEstimated ? "about " : "")}{stats.OutputTokens.ToString("N0", invariant)} tokens in {stats.ElapsedSeconds.ToString("0.0", invariant)} s");

            string speedStr = FormatSpeed(stats.TokensPerSecond);
            if (speedStr != "")
                lines.Add("Speed: " + speedStr + " once the reply started");

            if (stats.FirstTokenSeconds.HasValue)
                lines.Add("First text after " + stats.FirstTokenSeconds.Value.ToString("0.0", invariant) + " s");

            if (stats.TokensAreEstimated)
                lines.Add(modeLabel + " doesn't report exact token counts, so this is estimated from the text length.");

            return string.Join("\n", lines);
        }
    }
}
