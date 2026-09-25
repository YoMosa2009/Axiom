using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private const double PreferredWindowWidth = 1600;
        private const double PreferredWindowHeight = 900;
        private const double CompactDesktopBreakpoint = 1280;
        private const double WideDesktopBreakpoint = 1700;

        private void InitializeResponsiveDesktopLayout()
        {
            SourceInitialized += (_, _) => FitInitialWindowToCurrentMonitor();
            Loaded += (_, _) => ApplyResponsiveDesktopLayout();
            SizeChanged += (_, _) => ApplyResponsiveDesktopLayout();
            StateChanged += (_, _) => ApplyResponsiveDesktopLayout();
        }

        private void FitInitialWindowToCurrentMonitor()
        {
            if (WindowState != WindowState.Normal)
                return;

            Rect workArea = GetCurrentMonitorWorkAreaInDips();
            if (workArea.Width <= 0 || workArea.Height <= 0)
                workArea = SystemParameters.WorkArea;

            // Keep the initial window entirely inside the monitor's usable area. WPF sizes are
            // device-independent pixels, so this remains correct at 100%, 125%, 150% and mixed DPI.
            MinWidth = Math.Min(1040, workArea.Width);
            MinHeight = Math.Min(640, workArea.Height);

            Width = Math.Max(MinWidth, Math.Min(PreferredWindowWidth, workArea.Width * 0.94));
            Height = Math.Max(MinHeight, Math.Min(PreferredWindowHeight, workArea.Height * 0.94));
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = workArea.Left + Math.Max(0, (workArea.Width - Width) / 2);
            Top = workArea.Top + Math.Max(0, (workArea.Height - Height) / 2);
        }

        private void ApplyResponsiveDesktopLayout()
        {
            if (!IsLoaded || ActualWidth <= 0)
                return;

            bool compact = ActualWidth < CompactDesktopBreakpoint;
            bool wide = ActualWidth >= WideDesktopBreakpoint;
            double sidebarWidth = GetResponsiveSidebarWidth();

            if (!_isSidebarCollapsed && !_isSidebarAnimating)
                SidebarColumn.Width = new GridLength(sidebarWidth);

            NavigationBar.Padding = compact ? new Thickness(12, 8, 12, 8) : new Thickness(20, 12, 20, 12);
            ChatHeaderBorder.Padding = compact ? new Thickness(20, 10, 20, 2) : new Thickness(28, 14, 28, 4);
            TokenUsagePanel.Width = compact ? 300 : wide ? 380 : 340;
            // Bottom padding stays small: it is a dead band inside the scroll viewport where
            // messages are clipped, which read as an empty strip above the composer.
            ChatDisplay.Padding = compact
                ? new Thickness(20, 12, 20, 6)
                : wide
                    ? new Thickness(40, 22, 40, 8)
                    : new Thickness(28, 18, 28, 6);

            InputContainerBorder.Margin = compact
                ? new Thickness(16, 0, 16, 18)
                : wide
                    ? new Thickness(32, 0, 32, 30)
                    : new Thickness(24, 0, 24, 24);

            SettingsPanelCard.Margin = compact ? new Thickness(16) : new Thickness(28, 26, 28, 26);
            SettingsPanelCard.Padding = compact ? new Thickness(18, 14, 18, 14) : new Thickness(24, 18, 24, 18);

            double workplaceWidth = Math.Max(0, ContentContainer.ActualWidth);
            WorkplaceViewControl.ApplyDesktopLayout(workplaceWidth);
        }

        private enum ComposerToolbarMode { Full, IconOnly, Stacked }

        private bool _isUpdatingComposerToolbar;

        // Also wired to both tool groups: a control appearing inside them (the cloud model
        // picker, the loading spinner) changes what fits without the row itself resizing.
        private void ComposerToolbarGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
                UpdateComposerToolbarLayout();
        }

        /// <summary>
        /// Keeps the composer's tool row tidy at every width. The chat column shrinks sharply
        /// when Project Canvas opens, and the old WrapPanel then broke "+ / Skills / Plugins"
        /// onto ragged extra lines. Instead the row degrades in deliberate steps: full labels,
        /// then icon-only buttons (tooltips keep the names), then the controls on a second row.
        /// </summary>
        private void UpdateComposerToolbarLayout()
        {
            if (_isUpdatingComposerToolbar || ComposerToolbarGrid == null)
                return;

            double available = ComposerToolbarGrid.ActualWidth;
            if (available <= 0)
                return;

            _isUpdatingComposerToolbar = true;
            try
            {
                ComposerToolbarMode mode = ComposerToolbarMode.Full;
                ApplyComposerLabels(compact: false);
                if (MeasureComposerRowWidth() > available)
                {
                    mode = ComposerToolbarMode.IconOnly;
                    ApplyComposerLabels(compact: true);
                    if (MeasureComposerRowWidth() > available)
                    {
                        // On two rows each group has the full width to itself, so the labels
                        // come back whenever both groups still fit with them.
                        mode = ComposerToolbarMode.Stacked;
                        ApplyComposerLabels(compact: false);
                        MeasureComposerRowWidth();
                        if (Math.Max(ComposerLeftTools.DesiredSize.Width, ComposerRightTools.DesiredSize.Width) > available)
                            ApplyComposerLabels(compact: true);
                    }
                }

                bool stacked = mode == ComposerToolbarMode.Stacked;
                Grid.SetRow(ComposerRightTools, stacked ? 1 : 0);
                Grid.SetColumn(ComposerRightTools, stacked ? 0 : 1);
                Grid.SetColumnSpan(ComposerRightTools, stacked ? 2 : 1);
                ComposerRightTools.Margin = stacked ? new Thickness(16, 0, 14, 10) : new Thickness(0, 0, 14, 0);
                ComposerLeftTools.Margin = stacked ? new Thickness(16, 10, 0, 6) : new Thickness(16, 10, 0, 10);
            }
            finally
            {
                _isUpdatingComposerToolbar = false;
            }
        }

        // The canvas pane can be quite narrow; rather than clipping the "Project Canvas" title,
        // the Preview / Source / hide buttons drop below it when the row cannot hold both.
        private void NormalProjectCanvasHeaderGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!e.WidthChanged)
                return;

            NormalProjectCanvasHeaderActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            bool stack = e.NewSize.Width < NormalProjectCanvasHeaderActions.DesiredSize.Width + 150;
            Grid.SetRow(NormalProjectCanvasHeaderActions, stack ? 1 : 0);
            Grid.SetColumn(NormalProjectCanvasHeaderActions, stack ? 0 : 1);
            NormalProjectCanvasHeaderActions.HorizontalAlignment = stack ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            NormalProjectCanvasHeaderActions.Margin = stack ? new Thickness(0, 8, 0, 0) : new Thickness(10, 0, 0, 0);
        }

        private double MeasureComposerRowWidth()
        {
            var unbounded = new Size(double.PositiveInfinity, double.PositiveInfinity);
            ComposerLeftTools.Measure(unbounded);
            ComposerRightTools.Measure(unbounded);
            // Desired sizes include each panel's own margins; keep a small gap between groups.
            return ComposerLeftTools.DesiredSize.Width + ComposerRightTools.DesiredSize.Width + 12;
        }

        private void ApplyComposerLabels(bool compact)
        {
            Visibility labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;
            var iconMargin = compact ? new Thickness(0) : new Thickness(0, 0, 7, 0);

            SkillsButtonLabel.Visibility = labelVisibility;
            PluginsButtonLabel.Visibility = labelVisibility;
            WebToggleLabel.Visibility = labelVisibility;
            SkillsButtonIcon.Margin = iconMargin;
            PluginsButtonIcon.Margin = iconMargin;
            WebToggleIcon.Margin = iconMargin;
            SkillsButton.MinWidth = compact ? 34 : 90;
            PluginsButton.MinWidth = compact ? 34 : 96;
            SkillsButton.Margin = compact ? new Thickness(4, 0, 4, 0) : new Thickness(6, 0, 6, 0);
            NormalEffortSelector.IsCompact = compact;
        }

        private double GetResponsiveSidebarWidth()
        {
            if (ActualWidth < CompactDesktopBreakpoint)
                return 248;
            return ActualWidth >= WideDesktopBreakpoint ? 304 : 280;
        }

        private Rect GetCurrentMonitorWorkAreaInDips()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info))
                return Rect.Empty;

            double scaleX;
            double scaleY;
            if (PresentationSource.FromVisual(this) is HwndSource source && source.CompositionTarget != null)
            {
                Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
                scaleX = fromDevice.M11;
                scaleY = fromDevice.M22;
            }
            else
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(this);
                scaleX = 1.0 / dpi.DpiScaleX;
                scaleY = 1.0 / dpi.DpiScaleY;
            }

            return new Rect(
                info.WorkArea.Left * scaleX,
                info.WorkArea.Top * scaleY,
                (info.WorkArea.Right - info.WorkArea.Left) * scaleX,
                (info.WorkArea.Bottom - info.WorkArea.Top) * scaleY);
        }

        private const uint MonitorDefaultToNearest = 2;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MonitorInfo
        {
            public int Size;
            public NativeRect Monitor;
            public NativeRect WorkArea;
            public uint Flags;
        }
    }
}
