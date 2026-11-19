using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private string _sessionDiffSummary = string.Empty;
        private CanvasSyntaxContrast? _canvasSyntaxContrast;
        private readonly List<WorkplaceCanvasFileLink> _canvasFiles = new();
        private string _activeCanvasFileKey = string.Empty;
        private bool _suppressCanvasFileSelectorChange;

        private void ApplyCanvasSyntaxContrast()
        {
            var transformers = ProjectCanvasEditor.TextArea.TextView.LineTransformers;
            _canvasSyntaxContrast ??= new CanvasSyntaxContrast(this);
            // AvalonEdit replaces its highlighter when the language changes. Keep contrast last.
            transformers.Remove(_canvasSyntaxContrast);
            transformers.Add(_canvasSyntaxContrast);
        }

        private sealed class CanvasSyntaxContrast(WorkplaceView view) : IVisualLineTransformer
        {
            private readonly Dictionary<(Color Source, Color Background, Color Text), Brush> _colors = new();

            public void Transform(ITextRunConstructionContext context, IList<VisualLineElement> elements)
            {
                if (view.CodeOutputPanel.Background is not SolidColorBrush background
                    || view.ProjectCanvasEditor.Foreground is not SolidColorBrush text)
                    return;

                double backgroundLuminance = Luminance(background.Color);
                // Light canvases can use AvalonEdit's default palette as-is.
                if (backgroundLuminance >= 0.18)
                    return;

                foreach (VisualLineElement element in elements)
                {
                    var properties = element.TextRunProperties;
                    if (properties.ForegroundBrush is not SolidColorBrush foreground
                        || foreground.Color == text.Color)
                        continue;

                    Color source = foreground.Color;
                    var key = (source, background.Color, text.Color);
                    if (!_colors.TryGetValue(key, out Brush? adjusted))
                    {
                        double sourceLuminance = Luminance(source);
                        double targetLuminance = (backgroundLuminance + 0.05) * 4.6 - 0.05;
                        double textLuminance = Luminance(text.Color);
                        adjusted = foreground;
                        if (sourceLuminance < targetLuminance && textLuminance > sourceLuminance)
                        {
                            float mix = (float)Math.Min(1, (targetLuminance - sourceLuminance) / (textLuminance - sourceLuminance));
                            var blended = new SolidColorBrush(Color.FromScRgb(
                                1,
                                source.ScR + (text.Color.ScR - source.ScR) * mix,
                                source.ScG + (text.Color.ScG - source.ScG) * mix,
                                source.ScB + (text.Color.ScB - source.ScB) * mix));
                            blended.Freeze();
                            adjusted = blended;
                        }
                        _colors.Add(key, adjusted);
                    }
                    properties.SetForegroundBrush(adjusted);
                }
            }

            private static double Luminance(Color color) => 0.2126 * color.ScR + 0.7152 * color.ScG + 0.0722 * color.ScB;
        }

        private void RefreshCanvasPresentation()
        {
            bool isCanvas = _rightSidebarTab == WorkplaceRightSidebarTab.Canvas;
            bool isSessionDiff = _rightSidebarTab == WorkplaceRightSidebarTab.Diff;
            bool isCanvasDiff = isCanvas && _isDiffViewActive;
            bool hasArtifact = _canvasArtifact.SupportsPreview;
            bool canPreview = hasArtifact;
            string source = ProjectCanvasEditor.Text;
            bool hasSource = !string.IsNullOrWhiteSpace(source)
                && !source.Trim().Equals("// Builder output appears here", StringComparison.OrdinalIgnoreCase);
            bool showPreview = isCanvas && !isCanvasDiff && canPreview && _isCanvasPreviewMode;
            bool showSource = isCanvas && !isCanvasDiff && !showPreview && hasSource;
            bool hasRevision = HasCanvasDiffAvailable();

            CanvasEmptyState.Visibility = isCanvas && !isCanvasDiff && !showPreview && !hasSource
                ? Visibility.Visible : Visibility.Collapsed;
            ProjectCanvasEditor.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
            CanvasArtifactPreviewHost.Visibility = showPreview && !_suppressCanvasNativePreviewForOverlay
                ? Visibility.Visible : Visibility.Collapsed;
            DiffViewerScroller.Visibility = isCanvasDiff ? Visibility.Visible : Visibility.Collapsed;
            SessionDiffScroller.Visibility = isSessionDiff ? Visibility.Visible : Visibility.Collapsed;

            CanvasArtifactTogglePanel.Visibility = isCanvas && canPreview ? Visibility.Visible : Visibility.Collapsed;
            CanvasPreviewViewButton.Tag = showPreview ? "Selected" : null;
            CanvasCodeViewButton.Tag = showSource ? "Selected" : null;
            // Copy the source for the current view. Preview can be composed from a different payload.
            CopyCanvasArtifactSourceButton.Visibility = showPreview ? Visibility.Visible : Visibility.Collapsed;
            CopyCanvasButton.Visibility = showSource ? Visibility.Visible : Visibility.Collapsed;
            SaveCanvasArtifactButton.Visibility = isCanvas && canPreview && !isCanvasDiff
                ? Visibility.Visible : Visibility.Collapsed;
            RunCodeButton.Visibility = showSource && DetectLanguage(source) is ("python" or "java" or "html")
                ? Visibility.Visible : Visibility.Collapsed;
            ShowDiffButton.Visibility = isCanvas && hasRevision ? Visibility.Visible : Visibility.Collapsed;
            ShowDiffButton.Content = isCanvasDiff && !canPreview ? "Back to source" : "Diff";
            ShowDiffButton.Tag = isCanvasDiff ? "Selected" : null;

            SyncCanvasFileSelectorUi();
            CanvasActionsRow.Visibility = isCanvas && (hasSource || canPreview || isCanvasDiff || hasRevision)
                ? Visibility.Visible : Visibility.Collapsed;
            CanvasTitleBlock.Text = isSessionDiff
                ? "Changed files & diffs"
                : isCanvas && hasArtifact && !string.IsNullOrWhiteSpace(_canvasArtifact.DisplayTitle)
                    ? _canvasArtifact.DisplayTitle
                    : "Project Canvas";
            CanvasSubtitleBlock.Text = isSessionDiff ? _sessionDiffSummary
                : isCanvasDiff ? "Source changes"
                : showPreview ? "Formatted preview"
                : showSource ? "Source"
                : string.Empty;
            CanvasSubtitleBlock.Visibility = string.IsNullOrEmpty(CanvasSubtitleBlock.Text)
                ? Visibility.Collapsed : Visibility.Visible;
            CanvasDiffSummaryBlock.Text = hasRevision
                ? $"Latest revision: +{_canvasDiffAdditionCount} / −{_canvasDiffRemovalCount} lines"
                : string.Empty;
            CanvasDiffSummaryBlock.Visibility = isCanvas && hasRevision ? Visibility.Visible : Visibility.Collapsed;
        }

        private static WorkplaceCanvasFileLink BuildCanvasFileLink(string displayName, string filePath, string? inlineContent = null)
        {
            string cleanName = string.IsNullOrWhiteSpace(displayName)
                ? Path.GetFileName(filePath)
                : displayName.Replace('\\', '/').Trim();
            string fileName = Path.GetFileName(cleanName);
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            string badge = CanvasFileArtifact.GetFileTypeBadge(ext);
            return new WorkplaceCanvasFileLink(
                DisplayName: cleanName,
                FilePath: filePath,
                Extension: ext,
                TypeBadge: badge,
                ActionLabel: $"Open {fileName} in Project Canvas",
                InlineContent: inlineContent);
        }

        private void RegisterCanvasFile(WorkplaceCanvasFileLink link)
        {
            if (string.IsNullOrWhiteSpace(link.DisplayName) && string.IsNullOrWhiteSpace(link.FilePath))
                return;

            int existing = _canvasFiles.FindIndex(existingLink =>
                (!string.IsNullOrWhiteSpace(link.FilePath) && string.Equals(existingLink.FilePath, link.FilePath, StringComparison.OrdinalIgnoreCase))
                || string.Equals(existingLink.DisplayName, link.DisplayName, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                _canvasFiles[existing] = link;
            else
                _canvasFiles.Add(link);
        }

        private void SyncCanvasFileSelectorUi()
        {
            if (FindName("CanvasFileSelectorCombo") is not ComboBox combo)
                return;

            bool show = _rightSidebarTab == WorkplaceRightSidebarTab.Canvas && _canvasFiles.Count > 0;
            combo.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (!show)
                return;

            _suppressCanvasFileSelectorChange = true;
            try
            {
                combo.Items.Clear();
                ComboBoxItem? selected = null;
                foreach (WorkplaceCanvasFileLink link in _canvasFiles)
                {
                    var item = new ComboBoxItem
                    {
                        Content = $"{link.DisplayName}  ·  {link.TypeBadge}",
                        Tag = link,
                        ToolTip = link.FilePath
                    };
                    combo.Items.Add(item);
                    if (!string.IsNullOrWhiteSpace(_activeCanvasFileKey)
                        && (string.Equals(link.FilePath, _activeCanvasFileKey, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(link.DisplayName, _activeCanvasFileKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        selected = item;
                    }
                }

                combo.SelectedItem = selected ?? combo.Items[^1];
            }
            finally
            {
                _suppressCanvasFileSelectorChange = false;
            }
        }

        private bool OpenFileInProjectCanvas(string pathOrName, string? fallbackContent = null, bool showSidebar = true)
        {
            string candidate = (pathOrName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
                return false;

            if (candidate.StartsWith("canvas:", StringComparison.OrdinalIgnoreCase))
                candidate = candidate["canvas:".Length..].Trim();

            WorkplaceCanvasFileLink? matchedLink = _canvasFiles.LastOrDefault(l =>
                string.Equals(l.FilePath, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(l.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(l.DisplayName), Path.GetFileName(candidate), StringComparison.OrdinalIgnoreCase));

            string diskPath = matchedLink?.FilePath ?? candidate;
            if (!Path.IsPathRooted(diskPath) && !string.IsNullOrWhiteSpace(SelectedProjectFolder))
                diskPath = Path.Combine(SelectedProjectFolder, diskPath);

            CanvasFile? loadedFile = null;
            string? readError = null;
            if (!string.IsNullOrWhiteSpace(diskPath) && File.Exists(diskPath))
                CanvasFileArtifact.TryFromDiskFile(diskPath, matchedLink?.DisplayName ?? candidate, out loadedFile, out readError);

            if (loadedFile == null)
            {
                string? content = fallbackContent ?? matchedLink?.InlineContent;
                if (content != null)
                {
                    string name = Path.GetFileName(matchedLink?.DisplayName ?? candidate);
                    string ext = Path.GetExtension(name).ToLowerInvariant();
                    if (CanvasFileArtifact.IsBinaryFormat(ext))
                    {
                        readError ??= $"Binary format '{ext}' cannot be previewed as text in Project Canvas.";
                    }
                    else
                    {
                        loadedFile = new CanvasFile(string.IsNullOrWhiteSpace(name) ? "file.txt" : name, ext, content);
                    }
                }
            }

            if (loadedFile == null)
            {
                if (showSidebar && !string.IsNullOrWhiteSpace(readError))
                    AppendVisibleNotice(readError);
                return false;
            }

            WorkplaceCanvasFileLink activeLink = BuildCanvasFileLink(
                matchedLink?.DisplayName ?? candidate,
                File.Exists(diskPath) ? diskPath : candidate,
                loadedFile.Content);
            RegisterCanvasFile(activeLink);
            _activeCanvasFileKey = string.IsNullOrWhiteSpace(activeLink.FilePath) ? activeLink.DisplayName : activeLink.FilePath;

            ExitCanvasDiffView();
            _canvasArtifact = CanvasFileArtifact.ToArtifact(loadedFile);
            ProjectCanvasEditor.Text = loadedFile.Content;
            SetCanvasHighlighting(DetectLanguage(loadedFile.Content));
            _isCanvasPreviewMode = true;
            _rightSidebarTab = WorkplaceRightSidebarTab.Canvas;

            if (showSidebar && !_isProjectCanvasShown)
                SetProjectCanvasShown(true);

            ApplyRightSidebarTabVisibility();
            HighlightRightSidebarButtons();
            _ = RenderCanvasArtifactPreviewAsync();
            return true;
        }

        private void OpenCanvasFileLink_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: WorkplaceCanvasFileLink link })
                OpenFileInProjectCanvas(string.IsNullOrWhiteSpace(link.FilePath) ? link.DisplayName : link.FilePath, link.InlineContent, showSidebar: true);
        }

        private void WorkplaceCardContentText_HyperlinkClick(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is Hyperlink { Tag: string target } && !string.IsNullOrWhiteSpace(target))
            {
                e.Handled = true;
                OpenFileInProjectCanvas(target, showSidebar: true);
            }
        }

        private void CanvasFileSelectorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressCanvasFileSelectorChange || sender is not ComboBox { SelectedItem: ComboBoxItem { Tag: WorkplaceCanvasFileLink link } })
                return;

            OpenFileInProjectCanvas(string.IsNullOrWhiteSpace(link.FilePath) ? link.DisplayName : link.FilePath, link.InlineContent, showSidebar: false);
        }
    }
}
