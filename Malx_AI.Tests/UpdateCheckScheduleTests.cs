using System;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class UpdateCheckScheduleTests
    {
        [Theory]
        [InlineData(1, 30)]
        [InlineData(2, 120)]
        [InlineData(3, 600)]
        [InlineData(4, 1800)]
        [InlineData(500, 1800)]
        public void FailedChecksRetryWithBackoff(int failures, int expectedSeconds)
            => Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UpdateCheckSchedule.NextDelay(failures));

        [Fact]
        public void SuccessfulCheckIsRepeatedLater()
            => Assert.Equal(UpdateCheckSchedule.RegularInterval, UpdateCheckSchedule.NextDelay(0));
    }
}
