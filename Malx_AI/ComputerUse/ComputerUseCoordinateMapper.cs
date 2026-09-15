using System;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseCoordinateMapper
    {
        public static (int ScreenX, int ScreenY) MapToScreen(ComputerUseCapture capture, int x, int y)
        {
            if (capture == null)
                return (x, y);

            int imageWidth = Math.Max(1, capture.ImageWidth);
            int imageHeight = Math.Max(1, capture.ImageHeight);
            int screenWidth = Math.Max(1, capture.ScreenWidth);
            int screenHeight = Math.Max(1, capture.ScreenHeight);

            // The model always returns screenshot coordinates. Treating a large pair as native
            // coordinates creates a second coordinate system and inaccurate near-edge clicks.
            int sourceX = Math.Clamp(x, 0, imageWidth - 1);
            int sourceY = Math.Clamp(y, 0, imageHeight - 1);
            if (imageWidth == screenWidth && imageHeight == screenHeight)
                return (capture.ScreenX + sourceX, capture.ScreenY + sourceY);

            int mappedX = capture.ScreenX + (int)Math.Round((sourceX + 0.5d) * (screenWidth / (double)imageWidth));
            int mappedY = capture.ScreenY + (int)Math.Round((sourceY + 0.5d) * (screenHeight / (double)imageHeight));
            mappedX = Math.Clamp(mappedX, capture.ScreenX, capture.ScreenX + screenWidth - 1);
            mappedY = Math.Clamp(mappedY, capture.ScreenY, capture.ScreenY + screenHeight - 1);
            return (mappedX, mappedY);
        }

        public static bool IsLikelyTaskbarClick(ComputerUseCapture capture, int x, int y, int bandPixels = 72)
        {
            if (capture == null)
                return false;

            (_, int screenY) = MapToScreen(capture, x, y);
            int taskbarTop = capture.ScreenY + Math.Max(1, capture.ScreenHeight) - Math.Max(48, bandPixels);
            return screenY >= taskbarTop;
        }

        public static bool IsSameTarget(ComputerUseAction left, ComputerUseAction right, int pixelSlop = 18)
        {
            if (left == null || right == null || left.Type != right.Type)
                return false;

            return left.Type switch
            {
                ComputerUseActionType.Click or ComputerUseActionType.DoubleClick or ComputerUseActionType.RightClick or ComputerUseActionType.Move
                    => !string.IsNullOrWhiteSpace(left.TargetId) || !string.IsNullOrWhiteSpace(right.TargetId)
                        ? string.Equals(left.TargetId, right.TargetId, StringComparison.OrdinalIgnoreCase)
                        : Math.Abs(left.X - right.X) <= pixelSlop && Math.Abs(left.Y - right.Y) <= pixelSlop,
                ComputerUseActionType.Key
                    => string.Equals(left.Keys, right.Keys, StringComparison.OrdinalIgnoreCase),
                ComputerUseActionType.Type or ComputerUseActionType.Open
                    => string.Equals(left.Text, right.Text, StringComparison.OrdinalIgnoreCase),
                ComputerUseActionType.Scroll or ComputerUseActionType.Zoom
                    => left.Dx == right.Dx && left.Dy == right.Dy && Math.Abs(left.X - right.X) <= pixelSlop && Math.Abs(left.Y - right.Y) <= pixelSlop,
                _ => false
            };
        }
    }
}
