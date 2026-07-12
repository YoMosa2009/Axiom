using System;
using System.Collections.Concurrent;
using System.Windows.Media;

namespace Malx_AI
{
    internal static class AppBrushCache
    {
        private static readonly ConcurrentDictionary<uint, SolidColorBrush> Brushes = new();

        public static SolidColorBrush Get(string colorText)
        {
            if (string.Equals(colorText, "Transparent", StringComparison.OrdinalIgnoreCase))
                return Get(Colors.Transparent);
            return Get((Color)ColorConverter.ConvertFromString(colorText));
        }

        public static SolidColorBrush Get(Color color)
        {
            uint key = ((uint)color.A << 24) | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            return Brushes.GetOrAdd(key, _ =>
            {
                var brush = new SolidColorBrush(color);
                brush.Freeze();
                return brush;
            });
        }
    }
}
