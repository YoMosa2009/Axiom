using System;
using System.IO;
using System.Linq;
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

        /// <summary>
        /// The attached Skill, if any, that turns this turn into a rendered artifact. An explicit
        /// @ProjectCanvas mention still works on its own; a Skill simply means the user does not
        /// have to type it for work the Skill exists to render.
        /// </summary>
        private SkillCanvasDirective? ResolveNormalChatCanvasDirective(string userMessage)
            => _capabilityRegistry.ResolveCanvasDirective(userMessage);

        private bool ShouldRouteNormalChatToCanvas(string userMessage)
            => IsProjectCanvasRequested(userMessage) || ResolveNormalChatCanvasDirective(userMessage) != null;

        private string BuildNormalChatProjectCanvasInstruction(string userMessage, LocalModelCapabilityProfile? capability = null)
        {
            SkillCanvasDirective? directive = ResolveNormalChatCanvasDirective(userMessage);
            if (directive != null)
            {
                // A Skill's own contract is more specific than the generic canvas prompt, and the
                // two stacked would contradict each other for small models.
                return directive.BuildSystemInstruction(
                    "Normal Chat",
                    LocalModelCapabilityProfile.ResolveCanvasTier(capability));
            }

            if (!IsProjectCanvasRequested(userMessage))
                return string.Empty;

            return """
[PROJECT CANVAS MODE]
The user explicitly invoked @ProjectCanvas. Produce a concrete renderable artifact, not only an explanation. Return the complete artifact source in the final answer so Axiom can route it to Project Canvas. Prefer one self-contained HTML document with inline CSS and JavaScript for interactive, animated, calculated, or stateful work; standalone SVG is suitable for static vector work; Markdown is suitable for a formatted document. Do not use external URLs, CDNs, fonts, scripts, stylesheets, images, or libraries because the canvas is offline. Make the artifact responsive to its container and avoid fixed viewport or page widths. Before the artifact, write one or two short, natural sentences to the user saying what you made; Axiom shows them in the chat while the artifact opens in Project Canvas, so do not describe it at length or repeat it. Calculator, Python, and Java sandbox tools are optional accuracy aids: use them only when they materially help with math, data, validation, or code execution. Do not claim a tool was used unless its result is present. Skip extended step-by-step deliberation before answering: do not silently draft or rewrite the artifact in a hidden reasoning pass first. Go straight to writing the complete artifact as your visible answer.
[/PROJECT CANVAS MODE]
""";
        }

        /// <summary>
        /// Routes a finished Normal Chat reply's artifact into Project Canvas and, when it lands
        /// there, swaps the bubble to the conversational reply so the artifact is not rendered a
        /// second time inside the chat. Same behaviour for local, Hybrid Local, and cloud models.
        /// </summary>
        private void ApplyNormalChatCanvasRouting(ChatMessage? message, string userMessage, string responseText)
        {
            ArtifactRenderInfo? artifact = TryRouteNormalChatArtifact(userMessage, responseText);
            if (artifact == null && message?.HasCanvasArtifact == true)
            {
                // No Skill or @ProjectCanvas asked for it, but the model answered with a whole
                // HTML document anyway; the bubble already shows a reply, so show the page too.
                ArtifactRenderInfo raw = ArtifactRenderService.DetectForNormalChat(responseText);
                if (raw.SupportsPreview)
                    ShowNormalProjectCanvasArtifact(raw);
                return;
            }

            if (message == null || artifact == null)
                return;

            string reply = ArtifactRenderService.BuildCanvasChatReply(responseText, artifact);
            message.CanvasReplyText = reply;

            foreach (var branch in _branches)
            {
                ChatMessageState? state = branch.Messages.FirstOrDefault(m => m.Id == message.Id);
                if (state != null)
                    state.CanvasReplyText = reply;
            }
        }

        private void OpenMessageInProjectCanvas_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ChatMessage message || string.IsNullOrWhiteSpace(message.Content))
                return;

            ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(message.Content);
            if (!artifact.SupportsPreview || artifact.Kind == ArtifactKind.Document)
            {
                // Small-model replies are structured text Axiom composed the artifact from.
                foreach (string format in new[] { SkillSmallModelFormats.Outline, SkillSmallModelFormats.Chart, SkillSmallModelFormats.Markdown })
                {
                    if (SkillArtifactComposer.TryCompose(format, message.Content, out string composedHtml))
                    {
                        ArtifactRenderInfo composed = ArtifactRenderService.DetectForNormalChat(composedHtml);
                        if (composed.SupportsPreview)
                        {
                            artifact = composed;
                            break;
                        }
                    }
                }
            }

            if (artifact.SupportsPreview)
                ShowNormalProjectCanvasArtifact(artifact);
            else
                ShowTransientStatus("This reply's artifact could not be reopened.");
        }

        private ArtifactRenderInfo? TryRouteNormalChatArtifact(string userMessage, string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
                return null;

            SkillCanvasDirective? directive = ResolveNormalChatCanvasDirective(userMessage);
            if (directive == null && !IsProjectCanvasRequested(userMessage))
                return null;

            ArtifactRenderInfo artifact = ArtifactRenderService.DetectForNormalChat(responseText);

            // Small models answer in the outline format, and large ones sometimes return prose
            // where the artifact should be. Both land here, so compose the deliverable from the
            // structure the response does have rather than showing an empty canvas.
            if (directive != null && (!artifact.SupportsPreview || artifact.Kind == ArtifactKind.Document)
                && SkillArtifactComposer.TryCompose(directive.SmallModelFormat, responseText, out string composedHtml))
            {
                artifact = ArtifactRenderService.DetectForNormalChat(composedHtml);
            }

            if (!artifact.SupportsPreview)
            {
                NormalProjectCanvasStatusText.Text = "The response did not contain a renderable artifact.";
                return null;
            }

            if (directive != null)
                ShowTransientStatus($"{directive.SkillName} rendered the result in Project Canvas.");

            ShowNormalProjectCanvasArtifact(artifact);
            return artifact;
        }

        private void ShowNormalProjectCanvasArtifact(ArtifactRenderInfo artifact)
        {
            _normalProjectCanvasArtifact = artifact;
            _normalProjectCanvasPreviewMode = true;
            _normalProjectCanvasNavigationSource = string.Empty;
            NormalProjectCanvasSourceView.Text = artifact.RawSource ?? string.Empty;
            string artifactTitle = ArtifactRenderService.ExtractArtifactTitle(artifact);
            NormalProjectCanvasStatusText.Text = string.IsNullOrWhiteSpace(artifactTitle) ? artifact.DisplayTitle : artifactTitle;
            NormalProjectCanvasPreviewButton.IsEnabled = true;
            NormalProjectCanvasSourceButton.IsEnabled = true;
            NormalProjectCanvasCopyButton.IsEnabled = true;
            NormalProjectCanvasSaveButton.IsEnabled = true;
            NormalProjectCanvasEmptyState.Visibility = Visibility.Collapsed;
            RefreshNormalProjectCanvasMode();
            SetNormalProjectCanvasExpanded(true, animated: true);
            _ = RenderNormalProjectCanvasAsync();
        }

        private enum ProjectCanvasToggleTarget
        {
            None,
            NormalChat,
            Workplace
        }

        private ProjectCanvasToggleTarget _projectCanvasToggleTarget = ProjectCanvasToggleTarget.NormalChat;
        private bool _isSyncingProjectCanvasToggle;

        // Called on every tab switch: the tab-bar toggle belongs to whichever tab has a canvas.
        private void SetProjectCanvasToggleTarget(ProjectCanvasToggleTarget target)
        {
            _projectCanvasToggleTarget = target;
            ProjectCanvasToggleButton.Visibility = target == ProjectCanvasToggleTarget.None
                ? Visibility.Collapsed
                : Visibility.Visible;
            SyncProjectCanvasToggle();
        }

        // Mirrors the active tab's canvas state onto the toggle without re-triggering it.
        private void SyncProjectCanvasToggle()
        {
            bool isOpen = _projectCanvasToggleTarget switch
            {
                ProjectCanvasToggleTarget.NormalChat => _normalProjectCanvasExpanded,
                ProjectCanvasToggleTarget.Workplace => WorkplaceViewControl.IsProjectCanvasShown,
                _ => false
            };

            _isSyncingProjectCanvasToggle = true;
            try
            {
                ProjectCanvasToggleButton.IsChecked = isOpen;
                ProjectCanvasToggleButton.ToolTip = isOpen ? "Hide Project Canvas" : "Show Project Canvas";
            }
            finally
            {
                _isSyncingProjectCanvasToggle = false;
            }
        }

        private void WorkplaceView_ProjectCanvasShownChanged(object? sender, EventArgs e)
        {
            if (_projectCanvasToggleTarget == ProjectCanvasToggleTarget.Workplace)
                SyncProjectCanvasToggle();
        }

        // Driven by Checked/Unchecked rather than Click so mouse, keyboard and UI Automation
        // toggles all behave the same.
        private void ProjectCanvasToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isSyncingProjectCanvasToggle)
                return;

            bool open = ProjectCanvasToggleButton.IsChecked == true;
            switch (_projectCanvasToggleTarget)
            {
                case ProjectCanvasToggleTarget.NormalChat when open != _normalProjectCanvasExpanded:
                    SetNormalProjectCanvasExpanded(open, animated: true);
                    if (open)
                        _ = RenderNormalProjectCanvasAsync();
                    break;
                case ProjectCanvasToggleTarget.Workplace:
                    WorkplaceViewControl.SetProjectCanvasShown(open);
                    break;
            }

            SyncProjectCanvasToggle();
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
            // The pane is docked beside the whole chat view, so size it from that view: the
            // workspace grid is what remains AFTER the pane and would shrink as it opens.
            double available = ChatView?.ActualWidth ?? ActualWidth;
            if (double.IsNaN(available) || available <= 0)
                available = 1100;

            double proportional = Math.Clamp(available * 0.40, 320, 640);
            return Math.Max(300, Math.Min(proportional, Math.Max(300, available - 470)));
        }

        private void SetNormalProjectCanvasExpanded(bool expanded, bool animated)
        {
            _normalProjectCanvasExpanded = expanded;
            if (_projectCanvasToggleTarget == ProjectCanvasToggleTarget.NormalChat)
                SyncProjectCanvasToggle();
            NormalProjectCanvasPane.BeginAnimation(WidthProperty, null);
            _normalProjectCanvasPaneAnimating = false;

            if (expanded)
            {
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
