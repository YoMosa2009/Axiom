using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class StreamReasoningAccumulatorTests
    {
        [Fact]
        public void AlternatingReasoningAndDetailsChunksAreNotDoubled()
        {
            var acc = new StreamReasoningAccumulator();
            acc.Add("Let me sleep ", null, null);
            acc.Add(null, "Let me sleep ", null);
            acc.Add("900 and check.", null, null);
            acc.Add(null, "900 and check.", null);

            Assert.Equal("Let me sleep 900 and check.", acc.ToString());
            Assert.Equal(StreamReasoningAccumulator.Source.Plain, acc.LockedSource);
        }

        [Fact]
        public void BothFieldsInOneChunkCountOnce()
        {
            var acc = new StreamReasoningAccumulator();
            acc.Add("stats", "stats", null);
            acc.Add(" and", " and", null);

            Assert.Equal("stats and", acc.ToString());
        }

        [Fact]
        public void DetailsOnlyStreamIsCollected()
        {
            var acc = new StreamReasoningAccumulator();
            acc.Add(null, "Plan ", null);
            acc.Add(string.Empty, "first.", null);

            Assert.Equal("Plan first.", acc.ToString());
            Assert.Equal(StreamReasoningAccumulator.Source.Details, acc.LockedSource);
        }

        [Fact]
        public void ContentReasoningIsIgnoredOnceAFieldIsLocked()
        {
            var acc = new StreamReasoningAccumulator();
            acc.Add("think", null, null);
            Assert.Equal(string.Empty, acc.Add(null, null, "think"));

            Assert.Equal("think", acc.ToString());
        }

        [Fact]
        public void EmptyChunksDoNotLockASource()
        {
            var acc = new StreamReasoningAccumulator();
            acc.Add(null, null, null);
            acc.Add(string.Empty, string.Empty, string.Empty);

            Assert.Equal(StreamReasoningAccumulator.Source.None, acc.LockedSource);
            acc.Add(null, null, "inline");
            Assert.Equal("inline", acc.ToString());
        }
    }
}
