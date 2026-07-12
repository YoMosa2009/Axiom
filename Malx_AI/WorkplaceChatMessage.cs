using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Malx_AI
{
    public sealed class WorkplaceChatMessage : INotifyPropertyChanged
    {
        private string _content = "";
        private string? _formattedContentCache;

        public string Role { get; init; } = "system";

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                _formattedContentCache = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedContent));
                OnPropertyChanged(nameof(IsGeneratingStatus));
                OnPropertyChanged(nameof(ShowGeneratingStatusInCard));
                OnPropertyChanged(nameof(GenerationStatusText));
            }
        }

        // Cached per content change: bindings can re-read this property several times per
        // update, and the markdown strip is regex work proportional to the message size.
        public string FormattedContent => _formattedContentCache ??= MarkdownParser.ToDisplayText(Content);
        public bool IsGeneratingStatus
        {
            get
            {
                string normalized = (Content ?? string.Empty).Trim();
                return string.IsNullOrWhiteSpace(normalized)
                    || (normalized.Contains("Generating", StringComparison.OrdinalIgnoreCase) && normalized.Length <= 48);
            }
        }

        public string GenerationStatusText => Role switch
        {
            "builder" => "Builder generating",
            "architect" => "Architect generating",
            "critic" or "critic-final" => "Critic generating",
            _ => "Generating"
        };

        public bool ShowGeneratingStatusInCard => IsGeneratingStatus && !string.Equals(Role, "builder", StringComparison.OrdinalIgnoreCase);

        public DateTime Timestamp { get; init; } = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string RoleLabel => Role switch
        {
            "user" => "You",
            "architect" => "Architect",
            "builder" => "Builder",
            "critic" => "Critic",
            "critic-final" => "Critic — Final Review",
            "error" => "Error",
            "sandbox" => "Sandbox",
            "warning" => "Warning",
            "memory" => "Memory",
            _ => "System"
        };

        public string TimestampLabel => Timestamp.ToString("HH:mm:ss");

        // Brushes are shared, frozen singletons: the old per-access `new SolidColorBrush(...)`
        // allocated a fresh brush every time a card rendered or its bindings refreshed, which is
        // constant churn while a role streams. Frozen brushes are also safely shareable across
        // the UI. Backgrounds are warm-tinted to sit naturally on the app's notebook theme
        // instead of the old cool blue-gray tints.
        private static SolidColorBrush Frozen(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        private static readonly SolidColorBrush UserCardBrush = Frozen(0x24, 0x22, 0x1E);
        private static readonly SolidColorBrush ArchitectCardBrush = Frozen(0x1E, 0x1D, 0x26);
        private static readonly SolidColorBrush BuilderCardBrush = Frozen(0x1B, 0x21, 0x1C);
        private static readonly SolidColorBrush CriticCardBrush = Frozen(0x25, 0x20, 0x18);
        private static readonly SolidColorBrush ErrorCardBrush = Frozen(0x26, 0x1A, 0x19);
        private static readonly SolidColorBrush SandboxCardBrush = Frozen(0x19, 0x20, 0x22);
        private static readonly SolidColorBrush MemoryCardBrush = Frozen(0x20, 0x1D, 0x26);
        private static readonly SolidColorBrush SystemCardBrush = Frozen(0x21, 0x1F, 0x1D);

        private static readonly SolidColorBrush UserAccentBrush = Frozen(0xC2, 0xC0, 0xB6);
        private static readonly SolidColorBrush ArchitectAccentBrush = Frozen(0x8B, 0x8D, 0xF5);
        private static readonly SolidColorBrush BuilderAccentBrush = Frozen(0x34, 0xD1, 0x78);
        private static readonly SolidColorBrush CriticAccentBrush = Frozen(0xF5, 0xA6, 0x23);
        private static readonly SolidColorBrush ErrorAccentBrush = Frozen(0xFF, 0x5C, 0x5C);
        private static readonly SolidColorBrush SandboxAccentBrush = Frozen(0x22, 0xB8, 0xCE);
        private static readonly SolidColorBrush WarningAccentBrush = Frozen(0xF9, 0x73, 0x16);
        private static readonly SolidColorBrush MemoryAccentBrush = Frozen(0xA7, 0x8B, 0xFA);
        private static readonly SolidColorBrush SystemAccentBrush = Frozen(0x8A, 0x82, 0x79);

        public SolidColorBrush CardBackground => Role switch
        {
            "user" => UserCardBrush,
            "architect" => ArchitectCardBrush,
            "builder" => BuilderCardBrush,
            "critic" or "critic-final" => CriticCardBrush,
            "error" => ErrorCardBrush,
            "sandbox" => SandboxCardBrush,
            "warning" => CriticCardBrush,
            "memory" => MemoryCardBrush,
            _ => SystemCardBrush
        };

        public SolidColorBrush AccentBrush => Role switch
        {
            "user" => UserAccentBrush,
            "architect" => ArchitectAccentBrush,
            "builder" => BuilderAccentBrush,
            "critic" or "critic-final" => CriticAccentBrush,
            "error" => ErrorAccentBrush,
            "sandbox" => SandboxAccentBrush,
            "warning" => WarningAccentBrush,
            "memory" => MemoryAccentBrush,
            _ => SystemAccentBrush
        };
    }
}
