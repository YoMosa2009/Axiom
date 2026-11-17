using System;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// Pure geometry for held-button pointer paths: the points a drag passes through, the shapes a
    /// model may name instead of listing points, and the area a drag covers.
    /// </summary>
    /// <remarks>
    /// Kept free of any OS call so the paths the input layer will follow can be tested exactly.
    /// </remarks>
    public static class ComputerUseStrokeGeometry
    {
        /// <summary>Most strokes one action may carry. A simple figure needs six or seven.</summary>
        public const int MaxStrokesPerAction = 24;

        /// <summary>Most points one stroke may carry before it is thinned.</summary>
        public const int MaxPointsPerStroke = 96;

        /// <summary>
        /// Every point the cursor should visit, at most <paramref name="maxStepPixels"/> apart.
        /// </summary>
        /// <remarks>
        /// An application draws what the cursor passes through while the button is down. Moves
        /// that jump tens of pixels at a time — which is what an ease-out curve does at its start
        /// — are read by pointer-based canvases as a tap rather than a stroke. Even spacing with a
        /// small step is what a hand actually produces.
        /// </remarks>
        public static IReadOnlyList<(int X, int Y)> Interpolate(IReadOnlyList<(int X, int Y)> points, int maxStepPixels = 6)
        {
            var result = new List<(int X, int Y)>();
            if (points == null || points.Count == 0)
                return result;

            int stepSize = Math.Max(1, maxStepPixels);
            result.Add(points[0]);
            for (int i = 1; i < points.Count; i++)
            {
                (int fromX, int fromY) = points[i - 1];
                (int toX, int toY) = points[i];
                double distance = Math.Sqrt(Math.Pow(toX - fromX, 2) + Math.Pow(toY - fromY, 2));
                int steps = Math.Max(1, (int)Math.Ceiling(distance / stepSize));
                for (int step = 1; step <= steps; step++)
                {
                    double t = step / (double)steps;
                    var next = (fromX + (int)Math.Round((toX - fromX) * t), fromY + (int)Math.Round((toY - fromY) * t));
                    if (next != result[^1])
                        result.Add(next);
                }
            }

            return result;
        }

        /// <summary>A closed ellipse as a polyline, starting and ending at its rightmost point.</summary>
        public static IReadOnlyList<(int X, int Y)> Ellipse(int centerX, int centerY, int radiusX, int radiusY, int segments = 28)
        {
            int rx = Math.Max(2, Math.Abs(radiusX));
            int ry = Math.Max(2, Math.Abs(radiusY));
            int count = Math.Clamp(segments, 8, MaxPointsPerStroke - 1);
            var points = new List<(int X, int Y)>(count + 1);
            for (int i = 0; i <= count; i++)
            {
                double angle = 2 * Math.PI * i / count;
                points.Add((centerX + (int)Math.Round(rx * Math.Cos(angle)), centerY + (int)Math.Round(ry * Math.Sin(angle))));
            }

            return points;
        }

        /// <summary>The four sides of a rectangle as one closed stroke.</summary>
        public static IReadOnlyList<(int X, int Y)> Rectangle(int x1, int y1, int x2, int y2)
            => [(x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1)];

        /// <summary>Total length of a polyline in pixels.</summary>
        public static double Length(IReadOnlyList<(int X, int Y)> points)
        {
            double total = 0;
            for (int i = 1; i < (points?.Count ?? 0); i++)
                total += Math.Sqrt(Math.Pow(points![i].X - points[i - 1].X, 2) + Math.Pow(points[i].Y - points[i - 1].Y, 2));
            return total;
        }

        /// <summary>
        /// The box around every stroke, grown by <paramref name="padding"/> and kept inside the
        /// given area. This is where a drag's effect must show up, so it is the only part of the
        /// screen worth comparing before and after.
        /// </summary>
        public static (int Left, int Top, int Width, int Height) Bounds(
            IEnumerable<IReadOnlyList<(int X, int Y)>> strokes,
            int padding,
            int areaLeft,
            int areaTop,
            int areaWidth,
            int areaHeight)
        {
            var all = (strokes ?? []).SelectMany(stroke => stroke ?? []).ToList();
            if (all.Count == 0 || areaWidth <= 0 || areaHeight <= 0)
                return (areaLeft, areaTop, 0, 0);

            int left = Math.Max(areaLeft, all.Min(point => point.X) - padding);
            int top = Math.Max(areaTop, all.Min(point => point.Y) - padding);
            int right = Math.Min(areaLeft + areaWidth - 1, all.Max(point => point.X) + padding);
            int bottom = Math.Min(areaTop + areaHeight - 1, all.Max(point => point.Y) + padding);
            return (left, top, Math.Max(0, right - left + 1), Math.Max(0, bottom - top + 1));
        }

        /// <summary>
        /// A screen pixel as the 0..65535 value absolute mouse input expects across the virtual
        /// desktop.
        /// </summary>
        public static int ToAbsolute(int pixel, int virtualOrigin, int virtualSize)
        {
            int span = Math.Max(1, virtualSize - 1);
            int clamped = Math.Clamp(pixel - virtualOrigin, 0, span);
            return (int)Math.Round(clamped * 65535d / span);
        }

        /// <summary>Thins an over-long stroke evenly, always keeping its first and last point.</summary>
        public static IReadOnlyList<(int X, int Y)> Thin(IReadOnlyList<(int X, int Y)> points, int maxPoints = MaxPointsPerStroke)
        {
            if (points == null || points.Count <= maxPoints || maxPoints < 2)
                return points ?? [];

            var result = new List<(int X, int Y)>(maxPoints);
            for (int i = 0; i < maxPoints; i++)
                result.Add(points[(int)Math.Round(i * (points.Count - 1) / (double)(maxPoints - 1))]);
            return result;
        }
    }
}
