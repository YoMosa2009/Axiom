using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Media;

namespace Malx_AI
{
    public sealed class WorkplaceChatMessage : INotifyPropertyChanged
    {
        private string _content = "";
        private FlowDocument _formattedDocument = WorkplaceMarkdownDocumentFactory.Create(string.Empty);

        public string Role { get; init; } = "system";

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                _formattedDocument = WorkplaceMarkdownDocumentFactory.Create(value);
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedContent));
                OnPropertyChanged(nameof(FormattedDocument));
                OnPropertyChanged(nameof(IsGeneratingStatus));
                OnPropertyChanged(nameof(ShowGeneratingStatusInCard));
                OnPropertyChanged(nameof(GenerationStatusText));
            }
        }

        public string FormattedContent => MarkdownParser.ToDisplayText(Content);
        public FlowDocument FormattedDocument => _formattedDocument;
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

        public SolidColorBrush CardBackground => Role switch
        {
            "user" => AppBrushCache.Get("#1E2028"),
            "architect" => AppBrushCache.Get("#1A1D2A"),
            "builder" => AppBrushCache.Get("#1A2420"),
            "critic" or "critic-final" => AppBrushCache.Get("#24201A"),
            "error" => AppBrushCache.Get("#241A1A"),
            "sandbox" => AppBrushCache.Get("#1A1D20"),
            "warning" => AppBrushCache.Get("#24201A"),
            "memory" => AppBrushCache.Get("#1D1A24"),
            _ => AppBrushCache.Get("#30302E")
        };

        public SolidColorBrush AccentBrush => Role switch
        {
            "user" => AppBrushCache.Get("#C2C0B6"),
            "architect" => AppBrushCache.Get("#6366F1"),
            "builder" => AppBrushCache.Get("#22C55E"),
            "critic" or "critic-final" => AppBrushCache.Get("#F59E0B"),
            "error" => AppBrushCache.Get("#FF3B3B"),
            "sandbox" => AppBrushCache.Get("#06B6D4"),
            "warning" => AppBrushCache.Get("#F97316"),
            "memory" => AppBrushCache.Get("#A78BFA"),
            _ => AppBrushCache.Get("#4B5563")
        };
    }
}
