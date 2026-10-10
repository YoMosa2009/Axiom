namespace Malx_AI.Tests
{
    using System;
    using Malx_AI;
    using Xunit;

    public class GenerationStatsTests
    {
        private static readonly DateTime T0 = new(2026, 10, 9, 12, 0, 0);

        [Fact]
        public void SpeedIsMeasuredFromTheFirstText()
        {
            var tracker = new GenerationSpeedTracker();
            tracker.Begin(T0);
            tracker.RecordCharacters(40, T0.AddSeconds(2));
            tracker.RecordCharacters(400, T0.AddSeconds(12));
            var snapshot = tracker.Snapshot(T0.AddSeconds(12));

            Assert.Equal(100, snapshot.OutputTokens);
            Assert.True(snapshot.TokensAreEstimated);
            Assert.Equal(10.0, snapshot.TokensPerSecond, 3.0);
            Assert.Equal(12.0, snapshot.ElapsedSeconds, 3.0);
            Assert.NotNull(snapshot.FirstTokenSeconds);
            Assert.Equal(2.0, snapshot.FirstTokenSeconds.Value, 3.0);
        }

        [Fact]
        public void CountedTokensBeatTheEstimate()
        {
            var tracker = new GenerationSpeedTracker();
            tracker.Begin(T0);
            tracker.RecordCharacters(400, T0.AddSeconds(1));
            tracker.RecordTokens(10, T0.AddSeconds(1));
            tracker.RecordTokens(90, T0.AddSeconds(4));
            var snapshot = tracker.Snapshot(T0.AddSeconds(4));

            Assert.Equal(90, snapshot.OutputTokens);
            Assert.False(snapshot.TokensAreEstimated);
            Assert.Equal(30, snapshot.TokensPerSecond, 3);
        }

        [Fact]
        public void ReportedTokensBeatEverything()
        {
            var tracker = new GenerationSpeedTracker();
            tracker.Begin(T0);
            tracker.RecordTokens(50, T0.AddSeconds(1));
            tracker.RecordTokens(80, T0.AddSeconds(3));
            var snapshot = tracker.Snapshot(T0.AddSeconds(3), reportedCompletionTokens: 120);

            Assert.Equal(120, snapshot.OutputTokens);
            Assert.False(snapshot.TokensAreEstimated);
        }

        [Fact]
        public void AReplyThatArrivesAllAtOnceHasNoSpeed()
        {
            var tracker = new GenerationSpeedTracker();
            tracker.Begin(T0);
            tracker.RecordCharacters(800, T0.AddSeconds(5));
            var snapshot = tracker.Snapshot(T0.AddSeconds(5));

            Assert.Equal(0, snapshot.TokensPerSecond);
            Assert.Equal("~200 tokens", GenerationStatsFormatter.FormatSummary(snapshot));
        }

        [Fact]
        public void NothingRecordedBeforeBegin()
        {
            var tracker = new GenerationSpeedTracker();
            tracker.RecordCharacters(100, T0);
            var snapshot = tracker.Snapshot(T0);

            Assert.Equal(0, snapshot.OutputTokens);
            Assert.False(tracker.HasStarted);
        }

        [Fact]
        public void FormatCountUsesShortForms()
        {
            Assert.Equal("999", GenerationStatsFormatter.FormatCount(999));
            Assert.Equal("1k", GenerationStatsFormatter.FormatCount(1000));
            Assert.Equal("1.2k", GenerationStatsFormatter.FormatCount(1234));
            Assert.Equal("12k", GenerationStatsFormatter.FormatCount(12345));
            Assert.Equal("2.5M", GenerationStatsFormatter.FormatCount(2500000));
        }

        [Fact]
        public void FormatSpeedRoundsSensibly()
        {
            Assert.Equal("4.5 tok/s", GenerationStatsFormatter.FormatSpeed(4.54));
            Assert.Equal("31 tok/s", GenerationStatsFormatter.FormatSpeed(31.4));
            Assert.Equal("", GenerationStatsFormatter.FormatSpeed(0));
            Assert.Equal("", GenerationStatsFormatter.FormatSpeed(double.NaN));
        }

        [Fact]
        public void SummaryShowsSpeedAndCount()
        {
            var snapshot = new GenerationStatsSnapshot(412, false, 31.2, 14, 0.8);
            Assert.Equal("31 tok/s \u00B7 412 tokens", GenerationStatsFormatter.FormatSummary(snapshot));
        }

        [Fact]
        public void TooltipExplainsEstimates()
        {
            var snapshot = new GenerationStatsSnapshot(1234, true, 25, 50.04, 1.26);
            var tooltip = GenerationStatsFormatter.FormatTooltip(snapshot, "Hybrid Local");

            Assert.StartsWith("Last reply (Hybrid Local): about 1,234 tokens in 50.0 s", tooltip);
            Assert.Contains("Speed: 25 tok/s once the reply started", tooltip);
            Assert.Contains("First text after 1.3 s", tooltip);
            Assert.Contains("Hybrid Local doesn't report exact token counts", tooltip);
        }

        [Fact]
        public void EmptyStatsFormatAsNothing()
        {
            var snapshot = new GenerationStatsSnapshot(0, true, 0, 0, null);
            Assert.Equal("", GenerationStatsFormatter.FormatSummary(snapshot));
            Assert.Equal("", GenerationStatsFormatter.FormatTooltip(snapshot, "Cloud"));
        }
    }
}
