using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Malx_AI
{
    public sealed class WorkplaceChatMessage : INotifyPropertyChanged
    {
        private string _content = "";

        public string Role { get; init; } = "system";

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedContent));
            }
        }

        public string FormattedContent => MarkdownParser.ToDisplayText(Content);
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

        public SolidColorBrush CardBackground => Role switch
        {
            "user" => new(Color.FromRgb(0x1E, 0x20, 0x28)),
            "architect" => new(Color.FromRgb(0x1A, 0x1D, 0x2A)),
            "builder" => new(Color.FromRgb(0x1A, 0x24, 0x20)),
            "critic" or "critic-final" => new(Color.FromRgb(0x24, 0x20, 0x1A)),
            "error" => new(Color.FromRgb(0x24, 0x1A, 0x1A)),
            "sandbox" => new(Color.FromRgb(0x1A, 0x1D, 0x20)),
            "warning" => new(Color.FromRgb(0x24, 0x20, 0x1A)),
            "memory" => new(Color.FromRgb(0x1D, 0x1A, 0x24)),
            _ => new(Color.FromRgb(0x30, 0x30, 0x2E))
        };

        public SolidColorBrush AccentBrush => Role switch
        {
            "user" => new(Color.FromRgb(0xC2, 0xC0, 0xB6)),
            "architect" => new(Color.FromRgb(0x63, 0x66, 0xF1)),
            "builder" => new(Color.FromRgb(0x22, 0xC5, 0x5E)),
            "critic" or "critic-final" => new(Color.FromRgb(0xF5, 0x9E, 0x0B)),
            "error" => new(Color.FromRgb(0xFF, 0x3B, 0x3B)),
            "sandbox" => new(Color.FromRgb(0x06, 0xB6, 0xD4)),
            "warning" => new(Color.FromRgb(0xF9, 0x73, 0x16)),
            "memory" => new(Color.FromRgb(0xA7, 0x8B, 0xFA)),
            _ => new(Color.FromRgb(0x4B, 0x55, 0x63))
        };
    }
}
