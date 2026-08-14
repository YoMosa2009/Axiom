using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private const string BackgroundExecutionSettingKey = "allow_background_execution";
        private const string SystemTraySettingKey = "keep_in_system_tray";

        private WinForms.NotifyIcon? _systemTrayIcon;
        private WinForms.ContextMenuStrip? _systemTrayMenu;
        private bool _allowBackgroundExecution;
        private bool _keepInSystemTray;
        private bool _isLoadingBackgroundExecutionSettings;
        private bool _isExplicitShutdownRequested;
        private bool _hasShownSystemTrayHint;
        private bool _isHiddenInSystemTray;
        private bool _isUpdateShutdownRequested;
        private bool _wasCouncilPetVisibleBeforeTray;
        private int _backgroundOptimizationPending;

        internal void PrepareForSystemShutdown()
            => _isExplicitShutdownRequested = true;

        private void LoadBackgroundExecutionSettings()
        {
            _isLoadingBackgroundExecutionSettings = true;
            try
            {
                _allowBackgroundExecution = string.Equals(
                    _database?.GetSetting(BackgroundExecutionSettingKey),
                    "true",
                    StringComparison.OrdinalIgnoreCase);
                _keepInSystemTray = _allowBackgroundExecution && string.Equals(
                    _database?.GetSetting(SystemTraySettingKey),
                    "true",
                    StringComparison.OrdinalIgnoreCase);

                BackgroundExecutionToggle.IsChecked = _allowBackgroundExecution;
                SystemTrayToggle.IsChecked = _keepInSystemTray;
                RefreshBackgroundExecutionUi();
            }
            finally
            {
                _isLoadingBackgroundExecutionSettings = false;
            }
        }

        private void InitializeSystemTray()
        {
            if (_systemTrayIcon != null)
            {
                ApplySystemTrayVisibility();
                return;
            }

            try
            {
                _systemTrayMenu = new WinForms.ContextMenuStrip();
                _systemTrayMenu.Items.Add("Open Axiom", null, (_, _) => Dispatcher.Invoke(RestoreFromSystemTray));
                _systemTrayMenu.Items.Add(new WinForms.ToolStripSeparator());
                _systemTrayMenu.Items.Add("Exit Axiom", null, (_, _) => Dispatcher.Invoke(ShutdownAxiomCompletely));

                _systemTrayIcon = new WinForms.NotifyIcon
                {
                    Text = "Axiom",
                    ContextMenuStrip = _systemTrayMenu
                };

                string? processPath = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath))
                    _systemTrayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(processPath);

                _systemTrayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromSystemTray);
                ApplySystemTrayVisibility();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"System tray initialization failed: {ex.Message}");
                _keepInSystemTray = false;
                if (SystemTrayToggle != null)
                    SystemTrayToggle.IsChecked = false;
                RefreshBackgroundExecutionUi();
            }
        }

        private void BackgroundExecutionToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingBackgroundExecutionSettings)
                return;

            _allowBackgroundExecution = BackgroundExecutionToggle.IsChecked == true;
            if (!_allowBackgroundExecution)
            {
                _isLoadingBackgroundExecutionSettings = true;
                try
                {
                    _keepInSystemTray = false;
                    SystemTrayToggle.IsChecked = false;
                }
                finally
                {
                    _isLoadingBackgroundExecutionSettings = false;
                }
            }

            SaveBackgroundExecutionSettings();
            if (_allowBackgroundExecution && _keepInSystemTray)
                ApplySystemTrayVisibility();
            else
                DisposeSystemTray();
            RefreshBackgroundExecutionUi();
        }

        private void SystemTrayToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (_isLoadingBackgroundExecutionSettings)
                return;

            _keepInSystemTray = _allowBackgroundExecution && SystemTrayToggle.IsChecked == true;
            if (_keepInSystemTray)
                InitializeSystemTray();
            else
                DisposeSystemTray();
            SaveBackgroundExecutionSettings();
            ApplySystemTrayVisibility();
            RefreshBackgroundExecutionUi();
        }

        private void SaveBackgroundExecutionSettings()
        {
            _database?.SaveSetting(BackgroundExecutionSettingKey, _allowBackgroundExecution ? "true" : "false");
            _database?.SaveSetting(SystemTraySettingKey, _keepInSystemTray ? "true" : "false");
        }

        private void RefreshBackgroundExecutionUi()
        {
            if (BackgroundExecutionToggle == null || SystemTrayToggle == null)
                return;

            SystemTrayToggle.IsEnabled = _allowBackgroundExecution;
            BackgroundExecutionStatusText.Text = _allowBackgroundExecution
                ? "Allowed. Axiom can continue active work after the window closes when tray mode is enabled."
                : "Disabled. Closing Axiom fully stops the process and all background work.";
            SystemTrayStatusText.Text = !_allowBackgroundExecution
                ? "Enable background operation first."
                : _keepInSystemTray
                    ? "Enabled. Closing the window hides Axiom here; use the tray icon to reopen or exit it."
                    : "Disabled. Closing the window exits Axiom completely.";
        }

        private void ApplySystemTrayVisibility()
        {
            if (_systemTrayIcon != null)
                _systemTrayIcon.Visible = _allowBackgroundExecution && _keepInSystemTray;
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            bool canHideToTray = !_isExplicitShutdownRequested
                && _allowBackgroundExecution
                && _keepInSystemTray
                && _systemTrayIcon?.Visible == true;

            if (canHideToTray)
            {
                e.Cancel = true;
                HideToSystemTray();
                return;
            }

            base.OnClosing(e);
        }

        private void HideToSystemTray()
        {
            _isHiddenInSystemTray = true;
            _neuronTimer.Stop();
            _toolActivityTimer.Stop();
            _wasCouncilPetVisibleBeforeTray = _councilPetWindow?.IsVisible == true;
            _councilPetWindow?.Hide();
            WorkplaceViewControl?.SetNativePreviewSuppressedForOverlay(true);
            ShowInTaskbar = false;
            Hide();

            if (!_hasShownSystemTrayHint && _systemTrayIcon != null)
            {
                _hasShownSystemTrayHint = true;
                _systemTrayIcon.ShowBalloonTip(
                    2500,
                    "Axiom is still running",
                    "Double-click the tray icon to reopen Axiom, or choose Exit Axiom to stop it completely.",
                    WinForms.ToolTipIcon.Info);
            }

            _ = OptimizeBackgroundResourcesAsync();
        }

        private async Task OptimizeBackgroundResourcesAsync()
        {
            if (!_isHiddenInSystemTray || Interlocked.Exchange(ref _backgroundOptimizationPending, 1) != 0)
                return;

            try
            {
                // Active work is never interrupted. Once it finishes, release the heavy local
                // model caches while the UI stays hidden; the selected chat model is restored on
                // demand when Axiom is reopened.
                while (_isHiddenInSystemTray && (_isProcessing || WorkplaceViewControl?.HasActiveWork == true))
                    await Task.Delay(1000);

                if (!_isHiddenInSystemTray)
                    return;

                await ReleaseChatModelForCouncilAsync(CancellationToken.None);
                WorkplaceViewControl?.ReleaseCachedCouncilModels();
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundOptimizationPending, 0);
            }
        }

        private void RestoreFromSystemTray()
        {
            if (_isExplicitShutdownRequested)
                return;

            _isHiddenInSystemTray = false;
            ShowInTaskbar = true;
            Show();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            WorkplaceViewControl?.SetNativePreviewSuppressedForOverlay(SettingsPanel.Visibility == Visibility.Visible);
            if (_wasCouncilPetVisibleBeforeTray && _councilPetWindow != null)
                _councilPetWindow.Show();
            _wasCouncilPetVisibleBeforeTray = false;

            if (NeuronView.Visibility == Visibility.Visible)
                _neuronTimer.Start();
            if (!string.IsNullOrWhiteSpace(_activeToolIndicatorLabel))
                _toolActivityTimer.Start();
            if (ChatView.Visibility == Visibility.Visible && _chatModelReleasedForCouncil)
                _ = RestoreChatModelAfterCouncilAsync();

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        internal void RestoreFromNotification()
        {
            if (_isHiddenInSystemTray)
            {
                RestoreFromSystemTray();
                return;
            }

            if (!IsVisible)
                Show();
            ShowInTaskbar = true;
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void ShutdownAxiomCompletely()
        {
            _isExplicitShutdownRequested = true;
            DisposeSystemTray();
            Application.Current.Shutdown();
        }

        private async Task PrepareForUpdateShutdownAsync()
        {
            _isUpdateShutdownRequested = true;
            SaveCurrentWorkplaceChat();
            try
            {
                await QueueCoordinatedChatPersistenceAsync(
                        includeChatSession: true,
                        includeWorkspaceState: true,
                        includeAdvancedState: true,
                        includeKvState: false)
                    .WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException ex)
            {
                await BackendLogService.LogErrorAsync("UpdateShutdown.PersistenceTimeout", ex);
            }
        }

        private void ShutdownAxiomForUpdate()
        {
            _isUpdateShutdownRequested = true;
            _isExplicitShutdownRequested = true;
            DisposeSystemTray();

            var watchdog = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(12));
                try { NativeDecodeForensics.MarkCleanShutdown(); } catch { }
                Environment.Exit(0);
            })
            {
                IsBackground = true,
                Name = "Axiom update shutdown watchdog"
            };
            watchdog.Start();
            Application.Current.Shutdown();
        }

        private void DisposeSystemTray()
        {
            if (_systemTrayIcon != null)
            {
                _systemTrayIcon.Visible = false;
                _systemTrayIcon.Dispose();
                _systemTrayIcon = null;
            }

            _systemTrayMenu?.Dispose();
            _systemTrayMenu = null;
        }
    }
}
