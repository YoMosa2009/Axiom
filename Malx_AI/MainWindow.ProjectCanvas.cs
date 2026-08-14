using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Malx_AI.Mcp;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Malx_AI
{
    public partial class MainWindow
    {
        internal const string ProjectCanvasMentionHandle = "ProjectCanvas";
        private const string ProjectCanvasMentionId = "axiom-project-canvas";
        private const string NormalProjectCanvasVirtualHostName = "normal-canvas.local";
        private static readonly string NormalProjectCanvasVirtualRoot =
            Path.Combine(Path.GetTempPath(), "Axiom", "NormalProjectCanvas");

        private ArtifactRenderInfo _normalProjectCanvasArtifact = ArtifactRenderInfo.None(string.Empty);
        private bool _normalProjectCanvasExpanded;
        private bool _normalProjectCanvasPreviewMode = true;
        private bool _normalProjectCanvasWebViewReady;
        private bool _normalProjectCanvasWebViewInitializing;
        private bool _normalProjectCanvasPaneAnimating;
        private string _normalProjectCanvasNavigationSource = string.Empty;

        private static McpConnectorInfo CreateProjectCanvasMentionOption() => new()
        {
            Id = ProjectCanvasMentionId,
            Handle = ProjectCanvasMentionHandle,
            DisplayName = "Project Canvas",
            Description = "Render the completed response as an artifact in Normal Chat.",
            Kind = McpConnectorKind.GitHub,
            LogoGlyph = "\u25C7",
            IsConnected = true,
            AccountLabel = "Artifact rendering \u00B7 all models"
        };

        private static bool ProjectCanvasMentionMatches(string? query)
        {
            string normalized = (query ?? string.Empty).Trim();
            return normalized.Length == 0
                || ProjectCanvasMentionHandle.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || "Project Canvas".Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || "artifact".Contains(normalized, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProjectCanvasRequested(string? userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage))
                return false;

            return McpMentionHelper.GetCompleteMentionHandles(
                    userMessage,
                    new[] { ProjectCanvasMentionHandle })
                .Count > 0;
        }

        private static string BuildNormalChatProjectCanvasInstruction(string userMessage)
        {
            if (!IsProjectCanvasRequested(userMessage))
                return string.Empty;

            return """
[PROJECT CANVAS MODE]
The user explicitly invoked @ProjectCanvas. Produce a concrete renderable artifact, not only an explanation. Return the complete artifact source in the final answer so Axiom can route it to Project Canvas. Prefer one self-contained HTML document with inline CSS and JavaScript for interactive, animated, calculated, or stateful work; standalone SVG is suitable for static vector work; Markdown is suitable for a formatted document. Do not use external URLs, CDNs, fonts, scripts, stylesheets, images, or libraries because the canvas is offline. Make the artifact responsive to its container and avoid fixed viewport assumptions. Calculator, Python, and Java sandbox tools are optional accuracy aids: use them only when they materially help with math, data, validation, or code execution. Do not claim a tool was used unless its result is present.
[/PROJECT CANVAS MODE]
""";
        }

        private void TryRouteNormalChatArtifact(string userMessage, string responseText)
        {
            if (!IsProjectCanvasRequested(userMessage) || string.IsNullOrWhiteSpace(responseText))
                return;

            ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(responseText);
            if (!artifact.SupportsPreview)
            {
                NormalProjectCanvasStatusText.Text = "The response did not contain a renderable artifact.";
                return;
            }

            _normalProjectCanvasArtifact = artifact;
            _normalProjectCanvasPreviewMode = true;
            _normalProjectCanvasNavigationSource = string.Empty;
            NormalProjectCanvasSourceView.Text = artifact.RawSource ?? string.Empty;
            NormalProjectCanvasStatusText.Text = artifact.DisplayTitle;
            NormalProjectCanvasPreviewButton.IsEnabled = true;
            NormalProjectCanvasSourceButton.IsEnabled = true;
            NormalProjectCanvasCopyButton.IsEnabled = true;
            NormalProjectCanvasSaveButton.IsEnabled = true;
            NormalProjectCanvasEmptyState.Visibility = Visibility.Collapsed;
            RefreshNormalProjectCanvasMode();
            SetNormalProjectCanvasExpanded(true, animated: true);
            _ = RenderNormalProjectCanvasAsync();
        }

        private void NormalProjectCanvasOpen_Click(object sender, RoutedEventArgs e)
        {
            SetNormalProjectCanvasExpanded(true, animated: true);
            _ = RenderNormalProjectCanvasAsync();
        }

        private void NormalProjectCanvasClose_Click(object sender, RoutedEventArgs e)
            => SetNormalProjectCanvasExpanded(false, animated: true);

        private void NormalProjectCanvasPreview_Click(object sender, RoutedEventArgs e)
        {
            if (!_normalProjectCanvasArtifact.SupportsPreview)
                return;

            _normalProjectCanvasPreviewMode = true;
            RefreshNormalProjectCanvasMode();
            _ = RenderNormalProjectCanvasAsync();
        }

        private void NormalProjectCanvasSource_Click(object sender, RoutedEventArgs e)
        {
            if (!_normalProjectCanvasArtifact.SupportsPreview)
                return;

            _normalProjectCanvasPreviewMode = false;
            RefreshNormalProjectCanvasMode();
        }

        private void RefreshNormalProjectCanvasMode()
        {
            bool hasArtifact = _normalProjectCanvasArtifact.SupportsPreview;
            NormalProjectCanvasEmptyState.Visibility = hasArtifact ? Visibility.Collapsed : Visibility.Visible;
            NormalProjectCanvasWebView.Visibility = hasArtifact && _normalProjectCanvasPreviewMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            NormalProjectCanvasSourceView.Visibility = hasArtifact && !_normalProjectCanvasPreviewMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            NormalProjectCanvasPreviewButton.Opacity = _normalProjectCanvasPreviewMode ? 1 : 0.62;
            NormalProjectCanvasSourceButton.Opacity = _normalProjectCanvasPreviewMode ? 0.62 : 1;
        }

        private double GetNormalProjectCanvasTargetWidth()
        {
            double available = NormalChatWorkspaceGrid?.ActualWidth ?? ActualWidth;
            if (double.IsNaN(available) || available <= 0)
                available = 1100;

            double proportional = Math.Clamp(available * 0.40, 320, 640);
            return Math.Max(300, Math.Min(proportional, Math.Max(300, available - 470)));
        }

        private void SetNormalProjectCanvasExpanded(bool expanded, bool animated)
        {
            _normalProjectCanvasExpanded = expanded;
            NormalProjectCanvasPane.BeginAnimation(WidthProperty, null);
            _normalProjectCanvasPaneAnimating = false;

            if (expanded)
            {
                NormalProjectCanvasHandle.Visibility = Visibility.Collapsed;
                NormalProjectCanvasPane.Visibility = Visibility.Visible;
                double targetWidth = GetNormalProjectCanvasTargetWidth();
                if (!animated)
                {
                    NormalProjectCanvasPane.Width = targetWidth;
                    return;
                }

                double from = NormalProjectCanvasPane.ActualWidth > 1 ? NormalProjectCanvasPane.ActualWidth : 0;
                var animation = new DoubleAnimation(from, targetWidth, TimeSpan.FromMilliseconds(240))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                _normalProjectCanvasPaneAnimating = true;
                animation.Completed += (_, _) =>
                {
                    _normalProjectCanvasPaneAnimating = false;
                    NormalProjectCanvasPane.BeginAnimation(WidthProperty, null);
                    NormalProjectCanvasPane.Width = GetNormalProjectCanvasTargetWidth();
                };
                NormalProjectCanvasPane.BeginAnimation(WidthProperty, animation);
                return;
            }

            if (!animated || NormalProjectCanvasPane.Visibility != Visibility.Visible)
            {
                NormalProjectCanvasPane.Width = 0;
                NormalProjectCanvasPane.Visibility = Visibility.Collapsed;
                NormalProjectCanvasHandle.Visibility = Visibility.Visible;
                return;
            }

            var closeAnimation = new DoubleAnimation(
                Math.Max(0, NormalProjectCanvasPane.ActualWidth),
                0,
                TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };
            _normalProjectCanvasPaneAnimating = true;
            closeAnimation.Completed += (_, _) =>
            {
                _normalProjectCanvasPaneAnimating = false;
                NormalProjectCanvasPane.BeginAnimation(WidthProperty, null);
                NormalProjectCanvasPane.Width = 0;
                NormalProjectCanvasPane.Visibility = Visibility.Collapsed;
                NormalProjectCanvasHandle.Visibility = Visibility.Visible;
            };
            NormalProjectCanvasPane.BeginAnimation(WidthProperty, closeAnimation);
        }

        private void NormalChatWorkspaceGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_normalProjectCanvasExpanded
                || _normalProjectCanvasPaneAnimating
                || NormalProjectCanvasPane.Visibility != Visibility.Visible)
                return;

            double targetWidth = GetNormalProjectCanvasTargetWidth();
            if (Math.Abs(NormalProjectCanvasPane.Width - targetWidth) > 1)
                NormalProjectCanvasPane.Width = targetWidth;
        }

        private void ResetNormalProjectCanvas()
        {
            _normalProjectCanvasArtifact = ArtifactRenderInfo.None(string.Empty);
            _normalProjectCanvasPreviewMode = true;
            _normalProjectCanvasNavigationSource = string.Empty;
            NormalProjectCanvasSourceView.Text = string.Empty;
            NormalProjectCanvasStatusText.Text = "Use @ProjectCanvas to render an artifact.";
            NormalProjectCanvasPreviewButton.IsEnabled = false;
            NormalProjectCanvasSourceButton.IsEnabled = false;
            NormalProjectCanvasCopyButton.IsEnabled = false;
            NormalProjectCanvasSaveButton.IsEnabled = false;
            RefreshNormalProjectCanvasMode();
            SetNormalProjectCanvasExpanded(false, animated: false);
        }

        private async Task EnsureNormalProjectCanvasWebViewInitializedAsync()
        {
            if (_normalProjectCanvasWebViewReady || _normalProjectCanvasWebViewInitializing)
                return;

            _normalProjectCanvasWebViewInitializing = true;
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: AppDataPaths.WebView2UserData,
                    options: WebView2GpuPolicy.CreateEnvironmentOptions());
                await NormalProjectCanvasWebView.EnsureCoreWebView2Async(environment);
                Directory.CreateDirectory(NormalProjectCanvasVirtualRoot);
                NormalProjectCanvasWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    NormalProjectCanvasVirtualHostName,
                    NormalProjectCanvasVirtualRoot,
                    CoreWebView2HostResourceAccessKind.Allow);
                await WebView2OfflinePolicy.ConfigureAsync(
                    NormalProjectCanvasWebView.CoreWebView2,
                    NormalProjectCanvasVirtualHostName);
                _normalProjectCanvasWebViewReady = true;
            }
            catch (Exception ex)
            {
                NormalProjectCanvasStatusText.Text = "Preview unavailable \u00B7 " + ex.Message;
                _normalProjectCanvasPreviewMode = false;
                RefreshNormalProjectCanvasMode();
                await BackendLogService.LogErrorAsync("NormalProjectCanvas.Initialize", ex);
            }
            finally
            {
                _normalProjectCanvasWebViewInitializing = false;
            }
        }

        private async Task RenderNormalProjectCanvasAsync()
        {
            if (!_normalProjectCanvasArtifact.SupportsPreview || !_normalProjectCanvasPreviewMode)
                return;

            await EnsureNormalProjectCanvasWebViewInitializedAsync();
            if (!_normalProjectCanvasWebViewReady || NormalProjectCanvasWebView.CoreWebView2 == null)
                return;

            string html = _normalProjectCanvasArtifact.RenderSource ?? string.Empty;
            if (string.Equals(_normalProjectCanvasNavigationSource, html, StringComparison.Ordinal))
                return;

            _normalProjectCanvasNavigationSource = html;
            try
            {
                if (System.Text.Encoding.UTF8.GetByteCount(html) > 1_400_000)
                {
                    string path = Path.Combine(NormalProjectCanvasVirtualRoot, "artifact.html");
                    await File.WriteAllTextAsync(path, html);
                    NormalProjectCanvasWebView.CoreWebView2.Navigate(
                        $"https://{NormalProjectCanvasVirtualHostName}/artifact.html");
                }
                else
                {
                    NormalProjectCanvasWebView.NavigateToString(html);
                }
            }
            catch (Exception ex)
            {
                _normalProjectCanvasNavigationSource = string.Empty;
                NormalProjectCanvasStatusText.Text = "Could not render artifact \u00B7 " + ex.Message;
                await BackendLogService.LogErrorAsync("NormalProjectCanvas.Render", ex);
            }
        }

        private void NormalProjectCanvasCopy_Click(object sender, RoutedEventArgs e)
        {
            if (!_normalProjectCanvasArtifact.SupportsPreview)
                return;

            try
            {
                Clipboard.SetText(_normalProjectCanvasArtifact.RawSource ?? string.Empty);
                ShowTransientStatus("Project Canvas source copied");
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogErrorAsync("NormalProjectCanvas.Copy", ex);
            }
        }

        private void NormalProjectCanvasSave_Click(object sender, RoutedEventArgs e)
        {
            if (!_normalProjectCanvasArtifact.SupportsPreview)
                return;

            var dialog = new SaveFileDialog
            {
                FileName = "axiom-project-canvas" + _normalProjectCanvasArtifact.SuggestedFileExtension,
                DefaultExt = _normalProjectCanvasArtifact.SuggestedFileExtension,
                Filter = _normalProjectCanvasArtifact.Kind switch
                {
                    ArtifactKind.Html => "HTML files (*.html)|*.html|All files (*.*)|*.*",
                    ArtifactKind.Svg => "SVG files (*.svg)|*.svg|All files (*.*)|*.*",
                    ArtifactKind.Chart => "PNG files (*.png)|*.png|All files (*.*)|*.*",
                    ArtifactKind.Document => "Markdown files (*.md)|*.md|All files (*.*)|*.*",
                    ArtifactKind.InteractiveJavaScript => "JavaScript files (*.js)|*.js|All files (*.*)|*.*",
                    _ => "All files (*.*)|*.*"
                }
            };

            if (dialog.ShowDialog() != true)
                return;

            try
            {
                if (!string.IsNullOrWhiteSpace(_normalProjectCanvasArtifact.BinaryBase64))
                    File.WriteAllBytes(dialog.FileName, Convert.FromBase64String(_normalProjectCanvasArtifact.BinaryBase64));
                else
                    File.WriteAllText(dialog.FileName, _normalProjectCanvasArtifact.SaveContent);
                ShowTransientStatus("Project Canvas artifact saved");
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogErrorAsync("NormalProjectCanvas.Save", ex);
                ShowTransientStatus("Could not save Project Canvas artifact");
            }
        }
    }
}
