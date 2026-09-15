using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseApplicationVerificationTests
    {
        [Fact]
        public void IsRequestedApplicationVisible_MatchesForegroundProcessOrTitle()
        {
            var capture = CreateCapture("msedge", "New tab - Microsoft Edge");

            Assert.True(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Microsoft Edge", capture));
            Assert.True(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Edge", capture));
        }

        [Fact]
        public void IsRequestedApplicationVisible_RejectsDesktopForeground()
        {
            var capture = CreateCapture("explorer", "Program Manager");

            Assert.False(ComputerUseApplicationVerification.IsRequestedApplicationVisible("Microsoft Edge", capture));
        }

        private static ComputerUseCapture CreateCapture(string process, string title) => new()
        {
            JpegBytes = [1],
            ImageWidth = 1,
            ImageHeight = 1,
            ScreenWidth = 1,
            ScreenHeight = 1,
            ForegroundProcessName = process,
            ForegroundWindowTitle = title
        };
    }
}
