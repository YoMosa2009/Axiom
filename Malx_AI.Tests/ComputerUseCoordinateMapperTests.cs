using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests
{
    public class ComputerUseCoordinateMapperTests
    {
        [Fact]
        public void MapToScreen_ScalesImagePixelsToNativeMonitor()
        {
            var capture = new ComputerUseCapture
            {
                JpegBytes = [1],
                ScreenX = 0,
                ScreenY = 0,
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                ImageWidth = 1280,
                ImageHeight = 720
            };

            (int x, int y) = ComputerUseCoordinateMapper.MapToScreen(capture, 640, 360);
            Assert.InRange(x, 959, 961);
            Assert.InRange(y, 539, 541);
        }

        [Fact]
        public void MapToScreen_ClampsCoordinatesToTheScreenshotSpace()
        {
            var capture = new ComputerUseCapture
            {
                JpegBytes = [1],
                ScreenX = 0,
                ScreenY = 0,
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                ImageWidth = 1280,
                ImageHeight = 720
            };

            (int x, int y) = ComputerUseCoordinateMapper.MapToScreen(capture, 1800, 1000);
            Assert.Equal(1919, x);
            Assert.Equal(1079, y);
        }

        [Fact]
        public void MapToScreen_DoesNotTreatTaskbarImageYAsNativeHeight()
        {
            var capture = new ComputerUseCapture
            {
                JpegBytes = [1],
                ScreenX = 0,
                ScreenY = 0,
                ScreenWidth = 1920,
                ScreenHeight = 1080,
                ImageWidth = 1280,
                ImageHeight = 720
            };

            (int x, int y) = ComputerUseCoordinateMapper.MapToScreen(capture, 110, 700);
            Assert.InRange(x, 164, 166);
            Assert.InRange(y, 1049, 1051);

            (int overflowX, int overflowY) = ComputerUseCoordinateMapper.MapToScreen(capture, 110, 795);
            Assert.InRange(overflowX, 164, 166);
            Assert.InRange(overflowY, 1078, 1079);
        }

        [Fact]
        public void IsSameTarget_DetectsRepeatedClicks()
        {
            var first = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 110, Y = 795 };
            var second = new ComputerUseAction { Type = ComputerUseActionType.Click, X = 118, Y = 800 };
            Assert.True(ComputerUseCoordinateMapper.IsSameTarget(first, second));
        }

        [Fact]
        public void MapToScreen_UsesOneToOneWhenImageMatchesMonitor()
        {
            var capture = new ComputerUseCapture
            {
                JpegBytes = [1],
                ScreenX = 0,
                ScreenY = 0,
                ScreenWidth = 2256,
                ScreenHeight = 1504,
                ImageWidth = 2256,
                ImageHeight = 1504
            };

            (int x, int y) = ComputerUseCoordinateMapper.MapToScreen(capture, 1120, 1250);
            Assert.Equal(1120, x);
            Assert.Equal(1250, y);
        }

        [Fact]
        public void IsLikelyTaskbarClick_DetectsScaledTaskbarGuess()
        {
            var capture = new ComputerUseCapture
            {
                JpegBytes = [1],
                ScreenX = 0,
                ScreenY = 0,
                ScreenWidth = 2256,
                ScreenHeight = 1504,
                ImageWidth = 1920,
                ImageHeight = 1280
            };

            Assert.True(ComputerUseCoordinateMapper.IsLikelyTaskbarClick(capture, 1120, 1250));
            Assert.False(ComputerUseCoordinateMapper.IsLikelyTaskbarClick(capture, 400, 400));
        }
    }
}
