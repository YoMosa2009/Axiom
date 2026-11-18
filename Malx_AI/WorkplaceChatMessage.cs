using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Malx_AI
{
    public sealed class WorkplaceChatMessage : INotifyPropertyChanged
    {
        private string _content = "";
        private string? _formattedContentCache;
        private WorkplaceExecutionSummary? _executionSummary;
        private IReadOnlyList<string> _attachedImagePaths = Array.Empty<string>();
        private IReadOnlyList<ImageSource>? _attachedImagesCache;
        private IReadOnlyList<string> _viewedImagePaths = Array.Empty<string>();
        private IReadOnlyList<ImageSource>? _viewedImagesCache;
        private IReadOnlyList<WorkplaceFileChange> _fileChanges = Array.Empty<WorkplaceFileChange>();
        private bool _fileChangesExpanded;
        private bool _canUndoFileChanges;

        public IReadOnlyList<WorkplaceFileChange> FileChanges
        {
            get => _fileChanges;
            set
            {
                _fileChanges = value ?? Array.Empty<WorkplaceFileChange>();
                foreach (string name in new[] { nameof(FileChanges), nameof(HasFileChanges), nameof(FileChangesTitle),
                    nameof(AddedLinesLabel), nameof(RemovedLinesLabel), nameof(FileChangesScope), nameof(VisibleFileChanges),
                    nameof(HasMoreFileChanges), nameof(MoreFileChangesLabel) }) OnPropertyChanged(name);
            }
        }
        public bool HasFileChanges => FileChanges.Count > 0;
        public string FileChangesTitle
        {
            get
            {
                int files = FileChanges.Count(c => c.Kind != "Canvas");
                return files == 0 ? "Updated canvas" : $"Edited {files} file{(files == 1 ? "" : "s")}" + (files < FileChanges.Count ? " · Canvas updated" : "");
            }
        }
        public string AddedLinesLabel => FileChanges.Any(c => c.Additions.HasValue) ? $"+{FileChanges.Sum(c => (long)(c.Additions ?? 0)):N0}" : "Not counted";
        public string RemovedLinesLabel => FileChanges.Any(c => c.Removals.HasValue) ? $"−{FileChanges.Sum(c => (long)(c.Removals ?? 0)):N0}" : "";
        public string FileChangesScope => "Recorded file-tool and applied patch edits, plus canvas updates. Shell-command changes may not be included."
            + (FileChanges.Any(c => !c.Additions.HasValue) ? " Some files could not be counted; totals cover readable text only." : "");
        public IReadOnlyList<WorkplaceFileChange> VisibleFileChanges => FileChangesExpanded ? FileChanges : FileChanges.Take(3).ToArray();
        public bool HasMoreFileChanges => FileChanges.Count > 3;
        public string MoreFileChangesLabel => FileChangesExpanded ? "Show fewer files  ⌃" : $"Show {FileChanges.Count - 3} more file{(FileChanges.Count == 4 ? "" : "s")}  ⌄";
        public bool FileChangesExpanded
        {
            get => _fileChangesExpanded;
            set { _fileChangesExpanded = value; OnPropertyChanged(nameof(VisibleFileChanges)); OnPropertyChanged(nameof(MoreFileChangesLabel)); }
        }
        public bool CanUndoFileChanges
        {
            get => _canUndoFileChanges;
            set { _canUndoFileChanges = value; OnPropertyChanged(); }
        }

        public string Role { get; init; } = "system";

        public IReadOnlyList<string> AttachedImagePaths
        {
            get => _attachedImagePaths;
            set
            {
                _attachedImagePaths = value ?? Array.Empty<string>();
                _attachedImagesCache = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AttachedImages));
                OnPropertyChanged(nameof(HasAttachedImages));
            }
        }

        public IReadOnlyList<ImageSource> AttachedImages => _attachedImagesCache ??= LoadImageSources(_attachedImagePaths);

        // Transient activity only. Snapshot persistence intentionally uses AttachedImagePaths,
        // never these screenshot previews, and completion clears them before final output.
        public IReadOnlyList<string> ViewedImagePaths
        {
            get => _viewedImagePaths;
            set
            {
                _viewedImagePaths = value ?? Array.Empty<string>();
                _viewedImagesCache = null;
                OnPropertyChanged(nameof(ViewedImages));
                OnPropertyChanged(nameof(HasViewedImages));
            }
        }
        public IReadOnlyList<ImageSource> ViewedImages => _viewedImagesCache ??= LoadImageSources(_viewedImagePaths, 160);
        public bool HasViewedImages => ViewedImages.Count > 0;

        public bool HasAttachedImages => AttachedImages.Count > 0;

        internal static IReadOnlyList<ImageSource> LoadImageSources(IEnumerable<string>? sources, int decodePixelWidth = 320)
        {
            if (sources == null)
                return Array.Empty<ImageSource>();

            var list = new List<ImageSource>();
            foreach (string raw in sources)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;
                try
                {
                    if (raw.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        int comma = raw.IndexOf(',');
                        if (comma >= 0 && comma + 1 < raw.Length)
                        {
                            byte[] bytes = Convert.FromBase64String(raw[(comma + 1)..]);
                            using var ms = new MemoryStream(bytes);
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.StreamSource = ms;
                            bmp.DecodePixelWidth = decodePixelWidth;
                            bmp.EndInit();
                            bmp.Freeze();
                            list.Add(bmp);
                        }
                    }
                    else if (File.Exists(raw))
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.UriSource = new Uri(raw, UriKind.Absolute);
                        bmp.DecodePixelWidth = decodePixelWidth;
                        bmp.EndInit();
                        bmp.Freeze();
                        list.Add(bmp);
                    }
                }
                catch
                {
                    // Skip unreadable image paths gracefully.
                }
            }
            return list;
        }

        public string Content
        {
            get => _content;
            set
            {
                if (_content == value) return;
                _content = value;
                _formattedContentCache = null;
                _executionSummary = null;
                OnPropertyChanged();
                OnPropertyChanged(nameof(FormattedContent));
                OnPropertyChanged(nameof(FormattedDocument));
                OnPropertyChanged(nameof(IsGeneratingStatus));
                OnPropertyChanged(nameof(ShowGeneratingStatusInCard));
                OnPropertyChanged(nameof(GenerationStatusText));
                OnPropertyChanged(nameof(HasExecutionSteps));
                OnPropertyChanged(nameof(ExecutionStepsLabel));
                OnPropertyChanged(nameof(ExecutionStepsDocument));
            }
        }

        // Cached per content change: bindings can re-read this several times per update, and
        // the markdown strip is regex work proportional to the message size.
        public string FormattedContent => _formattedContentCache ??= MarkdownParser.ToDisplayText(Content);

        // NEVER cache this document. WPF allows a FlowDocument to be hosted by exactly ONE
        // FlowDocumentScrollViewer; the council re-adds streaming cards (and "Show earlier
        // messages" re-materializes containers), so a cached instance gets claimed by a second
        // viewer and throws "Document belongs to another FlowDocumentScrollViewer already" on
        // every refresh. A fresh instance per read gives each viewer its own document. Reads
        // happen once per PropertyChanged, which is already throttled during streaming.
        private WorkplaceExecutionSummary ExecutionSummary => _executionSummary ??=
            WorkplaceExecutionSummary.Parse(Content, !string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase));
        public bool HasExecutionSteps => ExecutionSummary.Count > 0;
        public string ExecutionStepsLabel => $"{ExecutionSummary.Count} steps · View activity";
        public FlowDocument ExecutionStepsDocument => WorkplaceMarkdownDocumentFactory.Create(ExecutionSummary.Steps);
        public FlowDocument FormattedDocument => WorkplaceMarkdownDocumentFactory.Create(ExecutionSummary.Body);
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
            "agent" => "Agent generating",
            "builder" => "Builder generating",
            "architect" => "Architect generating",
            "critic" or "critic-final" => "Critic generating",
            _ => "Generating"
        };

        public bool ShowGeneratingStatusInCard => IsGeneratingStatus
            && !string.Equals(Role, "builder", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(Role, "agent", StringComparison.OrdinalIgnoreCase);

        public DateTime Timestamp { get; init; } = DateTime.Now;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public string RoleLabel => Role switch
        {
            "user" => "You",
            "agent" => "Agent",
            "architect" => "Architect",
            "builder" => "Builder",
            "critic" => "Critic",
            "critic-final" => "Critic — Final Review",
            "error" => "Error",
            "sandbox" => "Sandbox",
            "warning" => "Warning",
            "notice" => "Notice",
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
            "agent" => BuilderCardBrush,
            "architect" => ArchitectCardBrush,
            "builder" => BuilderCardBrush,
            "critic" or "critic-final" => CriticCardBrush,
            "error" => ErrorCardBrush,
            "sandbox" => SandboxCardBrush,
            "warning" => CriticCardBrush,
            "notice" => CriticCardBrush,
            "memory" => MemoryCardBrush,
            _ => SystemCardBrush
        };

        public SolidColorBrush AccentBrush => Role switch
        {
            "user" => UserAccentBrush,
            "agent" => BuilderAccentBrush,
            "architect" => ArchitectAccentBrush,
            "builder" => BuilderAccentBrush,
            "critic" or "critic-final" => CriticAccentBrush,
            "error" => ErrorAccentBrush,
            "sandbox" => SandboxAccentBrush,
            "warning" => WarningAccentBrush,
            "notice" => WarningAccentBrush,
            "memory" => MemoryAccentBrush,
            _ => SystemAccentBrush
        };
    }
}
