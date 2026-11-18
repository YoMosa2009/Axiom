using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Rendering;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private string _sessionDiffSummary = string.Empty;
        private CanvasSyntaxContrast? _canvasSyntaxContrast;

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
            bool canPreview = hasArtifact && !_connectedWorkspace.CodebaseEditAccessEnabled;
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

            CanvasActionsRow.Visibility = isCanvas && (hasSource || canPreview || isCanvasDiff || hasRevision)
                ? Visibility.Visible : Visibility.Collapsed;
            CanvasTitleBlock.Text = isSessionDiff
                ? "Changed files & diffs"
                : isCanvas && hasArtifact && !string.IsNullOrWhiteSpace(_canvasArtifact.DisplayTitle)
                    ? _canvasArtifact.DisplayTitle
                    : "Project Canvas";
            CanvasSubtitleBlock.Text = isSessionDiff ? _sessionDiffSummary
                : isCanvasDiff ? "Source changes"
                : showPreview ? "Artifact preview"
                : showSource ? "Source"
                : string.Empty;
            CanvasSubtitleBlock.Visibility = string.IsNullOrEmpty(CanvasSubtitleBlock.Text)
                ? Visibility.Collapsed : Visibility.Visible;
            CanvasDiffSummaryBlock.Text = hasRevision
                ? $"Latest revision: +{_canvasDiffAdditionCount} / −{_canvasDiffRemovalCount} lines"
                : string.Empty;
            CanvasDiffSummaryBlock.Visibility = isCanvas && hasRevision ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
