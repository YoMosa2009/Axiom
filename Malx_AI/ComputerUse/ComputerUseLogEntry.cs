using System.Windows.Media;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// One line of session activity, as a short status badge plus its sentence.
    /// </summary>
    /// <remarks>
    /// Presentation only. <see cref="ComputerUseLogFormatting"/> decides what a line means; this
    /// only decides what that looks like, so the meaning stays testable without a display.
    /// </remarks>
    public sealed class ComputerUseLogEntry
    {
        public string Badge { get; init; } = "";
        public string Body { get; init; } = "";
        public bool HasBadge => Badge.Length > 0;
        public Brush BadgeForeground { get; init; } = QuietText;
        public Brush BadgeBackground { get; init; } = QuietFill;

        // Tinted against Axiom's own surfaces rather than saturated status colours, so a busy log
        // stays quiet enough to read and only the badge carries the signal.
        private static readonly Brush GoodText = Freeze("#8FBE72");
        private static readonly Brush GoodFill = Freeze("#1C241A");
        private static readonly Brush WarnText = Freeze("#D9A441");
        private static readonly Brush WarnFill = Freeze("#26200F");
        private static readonly Brush BadText = Freeze("#DE7767");
        private static readonly Brush BadFill = Freeze("#2A1815");
        private static readonly Brush InfoText = Freeze("#9FB0C2");
        private static readonly Brush InfoFill = Freeze("#1A1E23");
        private static readonly Brush QuietText = Freeze("#9A928A");
        private static readonly Brush QuietFill = Freeze("#211F1D");

        private static Brush Freeze(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        public static ComputerUseLogEntry Create(string line)
        {
            (string badge, ComputerUseLogSeverity severity, string body) = ComputerUseLogFormatting.Classify(line);
            (Brush foreground, Brush background) = Palette(severity);
            return new ComputerUseLogEntry
            {
                Badge = badge,
                Body = body,
                BadgeForeground = foreground,
                BadgeBackground = background
            };
        }

        private static (Brush Foreground, Brush Background) Palette(ComputerUseLogSeverity severity)
            => severity switch
            {
                ComputerUseLogSeverity.Verified => (GoodText, GoodFill),
                ComputerUseLogSeverity.Blocked => (BadText, BadFill),
                ComputerUseLogSeverity.Waiting or ComputerUseLogSeverity.Attention => (WarnText, WarnFill),
                ComputerUseLogSeverity.Observed => (InfoText, InfoFill),
                _ => (QuietText, QuietFill)
            };
    }
}
