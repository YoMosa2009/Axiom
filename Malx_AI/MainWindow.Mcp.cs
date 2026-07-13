using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Malx_AI.Mcp;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private CancellationTokenSource? _mcpConnectCts;
        private bool _mcpConnectInProgress;

        private void InitializeMcpConnectors()
        {
            try
            {
                _mcpConnectorService = new McpConnectorService(_database);
                _mcpConnectorService.Changed += () =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        RefreshMcpConnectorsUi();
                        UpdateInputMcpMentionHighlight();
                    });
                };

                RefreshMcpConnectorsUi();
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogErrorAsync("MainWindow.InitializeMcpConnectors", ex);
                _mcpConnectorService = null;
            }
        }

        private IReadOnlyList<string> ResolveMentionedMcpHandles(string userMessage)
        {
            if (_mcpConnectorService == null || string.IsNullOrWhiteSpace(userMessage))
                return Array.Empty<string>();

            return McpMentionHelper.GetCompleteMentionHandles(userMessage, _mcpConnectorService.GetKnownHandles());
        }

        private void RefreshMcpConnectorsUi()
        {
            if (_isRefreshingMcpConnectorsUi)
                return;

            _isRefreshingMcpConnectorsUi = true;
            try
            {
                if (McpConnectorsPanel == null || _mcpConnectorService == null)
                    return;

                bool cloudReady = _openRouterChatService.HasValidKey;
                if (McpConnectorsCloudHintText != null)
                {
                    McpConnectorsCloudHintText.Text = cloudReady
                        ? "Click Connect — sign in with Google in your browser. Full Gmail + Drive tools (read/write/share). After app updates that add permissions, Disconnect and Connect again. Type @ in chat to mention a connector."
                        : "Connect anytime. Tools activate when Cloud Mode is on with an OpenRouter key. Type @ in chat to mention a connector.";
                }

                IReadOnlyList<McpConnectorInfo> connectors = _mcpConnectorService.GetConnectors();
                bool googleConnected = connectors.Any(c =>
                    (c.Id == McpConnectorService.GmailId || c.Id == McpConnectorService.GoogleDriveId) && c.IsConnected);
                bool githubConnected = connectors.Any(c => c.Id == McpConnectorService.GitHubId && c.IsConnected);
                bool todoistConnected = connectors.Any(c => c.Id == McpConnectorService.TodoistId && c.IsConnected);
                string? googleAccount = connectors.FirstOrDefault(c =>
                    (c.Id == McpConnectorService.GmailId || c.Id == McpConnectorService.GoogleDriveId)
                    && c.IsConnected && !string.IsNullOrWhiteSpace(c.AccountLabel))?.AccountLabel;
                string? githubAccount = connectors.FirstOrDefault(c =>
                    c.Id == McpConnectorService.GitHubId && c.IsConnected)?.AccountLabel;
                string? todoistAccount = connectors.FirstOrDefault(c =>
                    c.Id == McpConnectorService.TodoistId && c.IsConnected)?.AccountLabel;

                if (McpGoogleAccountStatusText != null)
                {
                    if (googleConnected)
                    {
                        McpGoogleAccountStatusText.Text = string.IsNullOrWhiteSpace(googleAccount)
                            ? "Connected"
                            : $"Connected as {googleAccount}";
                        McpGoogleAccountStatusText.Foreground = AppBrushCache.Get("#B8924A");
                    }
                    else
                    {
                        McpGoogleAccountStatusText.Text = "Not connected — opens your browser to sign in";
                        McpGoogleAccountStatusText.Foreground = AppBrushCache.Get("#8A8279");
                    }
                }

                if (McpGitHubAccountStatusText != null)
                {
                    if (githubConnected)
                    {
                        McpGitHubAccountStatusText.Text = string.IsNullOrWhiteSpace(githubAccount)
                            ? "Connected"
                            : $"Connected as {githubAccount}";
                        McpGitHubAccountStatusText.Foreground = AppBrushCache.Get("#B8924A");
                    }
                    else
                    {
                        McpGitHubAccountStatusText.Text = "Not connected — opens browser device login";
                        McpGitHubAccountStatusText.Foreground = AppBrushCache.Get("#8A8279");
                    }
                }

                if (McpConnectGoogleButton != null)
                {
                    McpConnectGoogleButton.Visibility = googleConnected ? Visibility.Collapsed : Visibility.Visible;
                    McpConnectGoogleButton.IsEnabled = !_mcpConnectInProgress;
                    McpConnectGoogleButton.Content = _mcpConnectInProgress ? "Connecting…" : "Connect";
                }

                if (McpDisconnectGoogleButton != null)
                {
                    McpDisconnectGoogleButton.Visibility = googleConnected ? Visibility.Visible : Visibility.Collapsed;
                    McpDisconnectGoogleButton.IsEnabled = !_mcpConnectInProgress;
                }

                if (McpConnectGitHubButton != null)
                {
                    McpConnectGitHubButton.Visibility = githubConnected ? Visibility.Collapsed : Visibility.Visible;
                    McpConnectGitHubButton.IsEnabled = !_mcpConnectInProgress;
                    McpConnectGitHubButton.Content = _mcpConnectInProgress ? "Connecting…" : "Connect";
                }

                if (McpDisconnectGitHubButton != null)
                {
                    McpDisconnectGitHubButton.Visibility = githubConnected ? Visibility.Visible : Visibility.Collapsed;
                    McpDisconnectGitHubButton.IsEnabled = !_mcpConnectInProgress;
                }

                if (McpTodoistAccountStatusText != null)
                {
                    if (todoistConnected)
                    {
                        McpTodoistAccountStatusText.Text = string.IsNullOrWhiteSpace(todoistAccount)
                            ? "Connected"
                            : $"Connected as {todoistAccount}";
                        McpTodoistAccountStatusText.Foreground = AppBrushCache.Get("#B8924A");
                    }
                    else
                    {
                        McpTodoistAccountStatusText.Text = "Not connected — opens browser to sign in";
                        McpTodoistAccountStatusText.Foreground = AppBrushCache.Get("#8A8279");
                    }
                }

                if (McpConnectTodoistButton != null)
                {
                    McpConnectTodoistButton.Visibility = todoistConnected ? Visibility.Collapsed : Visibility.Visible;
                    McpConnectTodoistButton.IsEnabled = !_mcpConnectInProgress;
                    McpConnectTodoistButton.Content = _mcpConnectInProgress ? "Connecting…" : "Connect";
                }

                if (McpDisconnectTodoistButton != null)
                {
                    McpDisconnectTodoistButton.Visibility = todoistConnected ? Visibility.Visible : Visibility.Collapsed;
                    McpDisconnectTodoistButton.IsEnabled = !_mcpConnectInProgress;
                }

                // Dropbox stays hidden until App Console review is approved.
                if (McpDropboxConnectorCard != null)
                {
                    McpDropboxConnectorCard.Visibility = McpFeatureFlags.DropboxConnectorEnabled
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                if (McpConnectDropboxButton != null)
                    McpConnectDropboxButton.IsEnabled = McpFeatureFlags.DropboxConnectorEnabled && !_mcpConnectInProgress;

                if (McpDropboxAccountStatusText != null && !McpFeatureFlags.DropboxConnectorEnabled)
                {
                    McpDropboxAccountStatusText.Text = "Coming soon — pending Dropbox review";
                    McpDropboxAccountStatusText.Foreground = AppBrushCache.Get("#8A8279");
                }

                McpConnectorsPanel.Children.Clear();
                foreach (McpConnectorInfo connector in connectors)
                    McpConnectorsPanel.Children.Add(BuildConnectorStatusCard(connector));
            }
            finally
            {
                _isRefreshingMcpConnectorsUi = false;
            }
        }

        private Border BuildConnectorStatusCard(McpConnectorInfo connector)
        {
            var card = new Border
            {
                Background = AppBrushCache.Get("#171615"),
                BorderBrush = AppBrushCache.Get(connector.IsConnected ? "#3A3226" : "#302D2A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var logo = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(8),
                Background = AppBrushCache.Get("#211F1D"),
                BorderBrush = AppBrushCache.Get(connector.IsConnected ? "#B8924A" : "#302D2A"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 0, 10, 0),
                Child = new TextBlock
                {
                    Text = connector.LogoGlyph,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(logo, 0);

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Text = connector.DisplayName,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppBrushCache.Get("#EDE8E3")
            });
            textStack.Children.Add(new TextBlock
            {
                Text = connector.IsConnected
                    ? $"Ready · @{connector.Handle}"
                    : $"{connector.Description} · @{connector.Handle}",
                FontSize = 10,
                Foreground = AppBrushCache.Get(connector.IsConnected ? "#B8924A" : "#8A8279"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(textStack, 1);

            var status = new TextBlock
            {
                Text = connector.IsConnected ? "Connected" : "—",
                FontSize = 11,
                FontWeight = FontWeights.Medium,
                Foreground = AppBrushCache.Get(connector.IsConnected ? "#B8924A" : "#8A8279"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(status, 2);

            grid.Children.Add(logo);
            grid.Children.Add(textStack);
            grid.Children.Add(status);
            card.Child = grid;
            return card;
        }

        private async void McpConnectGoogleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpConnectorService == null || _mcpConnectInProgress)
                return;

            _mcpConnectInProgress = true;
            _mcpConnectCts?.Cancel();
            _mcpConnectCts = new CancellationTokenSource();
            SetMcpConnectProgress("Opening your browser to sign in with Google…", visible: true);
            RefreshMcpConnectorsUi();

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.InvokeAsync(() => HandleMcpConnectProgressMessage(msg));
            });

            try
            {
                string account = await _mcpConnectorService
                    .ConnectGoogleAccountAsync(_mcpConnectCts.Token, progress)
                    .ConfigureAwait(true);

                BringAxiomToForeground();
                SetMcpConnectProgress(string.Empty, visible: false);
                ShowTransientStatus($"Connected as {account}");
            }
            catch (OperationCanceledException)
            {
                SetMcpConnectProgress(string.Empty, visible: false);
            }
            catch (Exception ex)
            {
                BringAxiomToForeground();
                SetMcpConnectProgress(string.Empty, visible: false);
                MessageBox.Show(
                    ex.Message,
                    "Connect Google",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            finally
            {
                _mcpConnectInProgress = false;
                RefreshMcpConnectorsUi();
            }
        }

        private void McpDisconnectGoogleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpConnectorService == null)
                return;

            _mcpConnectorService.DisconnectAllGoogle();
            ShowTransientStatus("Google account disconnected.");
            RefreshMcpConnectorsUi();
            UpdateInputMcpMentionHighlight();
        }

        private async void McpConnectGitHubButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpConnectorService == null || _mcpConnectInProgress)
                return;

            _mcpConnectInProgress = true;
            _mcpConnectCts?.Cancel();
            _mcpConnectCts = new CancellationTokenSource();
            SetMcpConnectProgress("Starting GitHub device login…", visible: true);
            RefreshMcpConnectorsUi();

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.InvokeAsync(() => HandleMcpConnectProgressMessage(msg));
            });

            try
            {
                string account = await _mcpConnectorService
                    .ConnectGitHubAsync(_mcpConnectCts.Token, progress)
                    .ConfigureAwait(true);

                BringAxiomToForeground();
                SetMcpConnectProgress(string.Empty, visible: false);
                ShowTransientStatus($"GitHub connected as {account}");
            }
            catch (OperationCanceledException)
            {
                SetMcpConnectProgress(string.Empty, visible: false);
            }
            catch (Exception ex)
            {
                BringAxiomToForeground();
                SetMcpConnectProgress(string.Empty, visible: false);
                MessageBox.Show(ex.Message, "Connect GitHub", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                _mcpConnectInProgress = false;
                RefreshMcpConnectorsUi();
            }
        }

        private void McpDisconnectGitHubButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpConnectorService == null)
                return;

            _mcpConnectorService.DisconnectGitHub();
            ShowTransientStatus("GitHub disconnected.");
            RefreshMcpConnectorsUi();
            UpdateInputMcpMentionHighlight();
        }

        private async void McpConnectTodoistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpConnectorService == null || _mcpConnectInProgress)
                return;

            _mcpConnectInProgress = true;
            _mcpConnectCts?.Cancel();
            _mcpConnectCts = new CancellationTokenSource();
            SetMcpConnectProgress("Opening Todoist sign-in…", visible: true);
            RefreshMcpConnectorsUi();

            var progress = new Progress<string>(msg =>
            {
                Dispatcher.InvokeAsync(() => HandleMcpConnectProgressMessage(msg));
            });

            try
            {
                string account = await _mcpConnectorService
                    .ConnectTodoistAsync(_mcpConnectCts.Token, progress)
                    .ConfigureAwait(true);

                BringAxiomToForeground();
                SetMcpConnectProgress(string.Empty, visible: false);
                ShowTransientStatus($"Todoist connected as {account}");
            }
            catch (OperationCanceledException)
            {
                SetMcpConnectProgress(string.Empty, visible: false);
            }
            catch (Exception ex)
            {
                BringAxiomToForeground();
                SetMcpConnectProgress(string.Empty, visible: false);
                MessageBox.Show(ex.Message, "Connect Todoist", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            finally
            {
                _mcpConnectInProgress = false;
                RefreshMcpConnectorsUi();
            }
        }

        private void McpDisconnectTodoistButton_Click(object sender, RoutedEventArgs e)
        {
            if (_mcpConnectorService == null)
                return;

            _mcpConnectorService.DisconnectTodoist();
            ShowTransientStatus("Todoist disconnected.");
            RefreshMcpConnectorsUi();
            UpdateInputMcpMentionHighlight();
        }

        private void SetMcpConnectProgress(string message, bool visible)
        {
            if (McpConnectProgressText == null)
                return;

            McpConnectProgressText.Text = message ?? string.Empty;
            McpConnectProgressText.Visibility = visible && !string.IsNullOrWhiteSpace(message)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        /// <summary>
        /// Handles connect progress, including GitHub DEVICE_CODE|userCode|uri which must be
        /// shown in a dialog because the browser page asks for a code from the app.
        /// </summary>
        private void HandleMcpConnectProgressMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return;

            if (message.StartsWith("DEVICE_CODE|", StringComparison.Ordinal))
            {
                string[] parts = message.Split('|');
                string userCode = parts.Length > 1 ? parts[1] : "";
                string verifyUri = parts.Length > 2 ? parts[2] : "https://github.com/login/device";
                if (!string.IsNullOrWhiteSpace(userCode))
                {
                    try { Clipboard.SetText(userCode); } catch { /* ignore */ }
                    SetMcpConnectProgress($"GitHub code: {userCode}  (copied — paste it in the browser)", visible: true);
                    MessageBox.Show(
                        "Enter this code on the GitHub page:\n\n" +
                        "        " + userCode + "\n\n" +
                        "(It is also copied to your clipboard — Ctrl+V on the GitHub page.)\n\n" +
                        "1. Paste/type the code on GitHub\n" +
                        "2. Click Continue and Authorize Axiom\n" +
                        "3. Return here — Axiom finishes automatically\n\n" +
                        "Page: " + verifyUri,
                        "GitHub device code",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }
                return;
            }

            SetMcpConnectProgress(message, visible: true);
        }

        private void BringAxiomToForeground()
        {
            try
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                Activate();
                Topmost = true;
                Topmost = false;
                Focus();
            }
            catch
            {
                // best-effort
            }
        }

        // ── Chat input @mention UX ───────────────────────────────────────────

        private void HandleMcpMentionInputTextChanged()
        {
            if (_isApplyingMcpMentionCompletion)
                return;

            UpdateInputMcpMentionHighlight();
            UpdateMcpMentionPopup();
        }

        private bool HandleMcpMentionPreviewKeyDown(KeyEventArgs e)
        {
            if (!_mcpMentionPopupOpen || McpMentionList == null || McpMentionPopup == null)
                return false;

            if (e.Key == Key.Escape)
            {
                CloseMcpMentionPopup();
                e.Handled = true;
                return true;
            }

            int count = McpMentionList.Items.Count;
            if (count <= 0)
                return false;

            if (e.Key == Key.Down)
            {
                _mcpMentionSelectedIndex = Math.Min(count - 1, _mcpMentionSelectedIndex + 1);
                McpMentionList.SelectedIndex = _mcpMentionSelectedIndex;
                McpMentionList.ScrollIntoView(McpMentionList.SelectedItem);
                e.Handled = true;
                return true;
            }

            if (e.Key == Key.Up)
            {
                _mcpMentionSelectedIndex = Math.Max(0, _mcpMentionSelectedIndex - 1);
                McpMentionList.SelectedIndex = _mcpMentionSelectedIndex;
                McpMentionList.ScrollIntoView(McpMentionList.SelectedItem);
                e.Handled = true;
                return true;
            }

            if (e.Key is Key.Enter or Key.Return or Key.Tab)
            {
                McpConnectorInfo? connector = McpMentionList.SelectedItem as McpConnectorInfo;
                if (connector == null
                    && _mcpMentionSelectedIndex >= 0
                    && _mcpMentionSelectedIndex < count)
                {
                    connector = McpMentionList.Items[_mcpMentionSelectedIndex] as McpConnectorInfo;
                }

                if (connector != null)
                {
                    ApplyMcpMentionCompletion(connector);
                    e.Handled = true;
                    return true;
                }
            }

            return false;
        }

        private void McpMentionList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (McpMentionList?.SelectedItem is McpConnectorInfo connector)
                ApplyMcpMentionCompletion(connector);
        }

        private void ApplyMcpMentionCompletion(McpConnectorInfo connector)
        {
            if (InputBox == null || connector == null || _mcpMentionAtIndex < 0)
                return;

            _isApplyingMcpMentionCompletion = true;
            try
            {
                int caret = InputBox.CaretIndex;
                string next = McpMentionHelper.ApplyMentionCompletion(InputBox.Text ?? string.Empty, _mcpMentionAtIndex, caret, connector.Handle);
                InputBox.Text = next;
                int newCaret = _mcpMentionAtIndex + 1 + connector.Handle.Length + 1;
                InputBox.CaretIndex = Math.Min(newCaret, next.Length);
                InputBox.Focus();
            }
            finally
            {
                _isApplyingMcpMentionCompletion = false;
            }

            CloseMcpMentionPopup();
            UpdateInputMcpMentionHighlight();
        }

        private void UpdateMcpMentionPopup()
        {
            if (InputBox == null || McpMentionPopup == null || McpMentionList == null || _mcpConnectorService == null)
            {
                CloseMcpMentionPopup();
                return;
            }

            if (!_cloudModeActive || !_openRouterChatService.HasValidKey)
            {
                CloseMcpMentionPopup();
                return;
            }

            string text = InputBox.Text ?? string.Empty;
            int caret = InputBox.CaretIndex;
            if (!McpMentionHelper.TryGetActiveMentionQuery(text, caret, out int atIndex, out string query))
            {
                CloseMcpMentionPopup();
                return;
            }

            IReadOnlyList<McpConnectorInfo> matches = McpMentionHelper.FilterConnectors(
                _mcpConnectorService.GetConnectors(),
                query,
                connectedOnly: false);

            if (matches.Count == 0)
            {
                CloseMcpMentionPopup();
                return;
            }

            if (!string.IsNullOrEmpty(query)
                && matches.Count == 1
                && string.Equals(matches[0].Handle, query, StringComparison.OrdinalIgnoreCase))
            {
                CloseMcpMentionPopup();
                _mcpMentionAtIndex = atIndex;
                return;
            }

            _mcpMentionAtIndex = atIndex;
            McpMentionList.ItemsSource = matches;
            _mcpMentionSelectedIndex = 0;
            McpMentionList.SelectedIndex = 0;
            McpMentionPopup.IsOpen = true;
            _mcpMentionPopupOpen = true;
        }

        private void CloseMcpMentionPopup()
        {
            _mcpMentionPopupOpen = false;
            _mcpMentionAtIndex = -1;
            if (McpMentionPopup != null)
                McpMentionPopup.IsOpen = false;
            if (McpMentionList != null)
                McpMentionList.ItemsSource = null;
        }

        private void UpdateInputMcpMentionHighlight()
        {
            if (InputBox == null || InputBoxMentionOverlay == null)
                return;

            string text = InputBox.Text ?? string.Empty;
            IReadOnlyList<string> known = _mcpConnectorService?.GetKnownHandles() ?? Array.Empty<string>();
            IReadOnlyList<McpMentionSpan> mentions = _cloudModeActive
                ? McpMentionHelper.FindMentions(text, known)
                : Array.Empty<McpMentionSpan>();

            bool hasComplete = mentions.Any(m => m.IsComplete);
            if (!hasComplete || string.IsNullOrEmpty(text))
            {
                InputBox.Foreground = AppBrushCache.Get("#EDE8E3");
                InputBoxMentionOverlay.Inlines.Clear();
                InputBoxMentionOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            InputBox.Foreground = Brushes.Transparent;
            InputBoxMentionOverlay.Visibility = Visibility.Visible;
            InputBoxMentionOverlay.Inlines.Clear();

            var completeSpans = mentions.Where(m => m.IsComplete).OrderBy(m => m.Start).ToList();
            int cursor = 0;
            Brush normal = AppBrushCache.Get("#EDE8E3");
            Brush gold = AppBrushCache.Get("#B8924A");

            foreach (McpMentionSpan span in completeSpans)
            {
                if (span.Start > cursor)
                {
                    InputBoxMentionOverlay.Inlines.Add(new Run(text.Substring(cursor, span.Start - cursor))
                    {
                        Foreground = normal
                    });
                }

                InputBoxMentionOverlay.Inlines.Add(new Run(text.Substring(span.Start, span.Length))
                {
                    Foreground = gold,
                    FontWeight = FontWeights.SemiBold
                });
                cursor = span.Start + span.Length;
            }

            if (cursor < text.Length)
            {
                InputBoxMentionOverlay.Inlines.Add(new Run(text[cursor..])
                {
                    Foreground = normal
                });
            }
        }
    }
}
