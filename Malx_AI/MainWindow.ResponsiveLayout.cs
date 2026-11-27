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

            NavigationBar.Padding = new Thickness(0);
            double chatLeftPad = compact ? 20 : 28;
            ChatHeaderBorder.Padding = compact ? new Thickness(chatLeftPad, 10, 20, 2) : new Thickness(chatLeftPad, 14, 28, 4);
            TokenUsagePanel.Width = compact ? 300 : wide ? 380 : 340;
            // Bottom padding stays small: it is a dead band inside the scroll viewport where
            // messages are clipped, which read as an empty strip above the composer.
            UpdateChatLaneLayout();

            SettingsPanelCard.Margin = compact ? new Thickness(16) : new Thickness(28, 26, 28, 26);
            SettingsPanelCard.Padding = compact ? new Thickness(18, 14, 18, 14) : new Thickness(24, 18, 24, 18);

            double workplaceWidth = Math.Max(0, ContentContainer.ActualWidth);
            WorkplaceViewControl.ApplyDesktopLayout(workplaceWidth);
        }

        private void ChatDisplay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged) UpdateChatLaneLayout();
        }

        private void UpdateChatLaneLayout()
        {
            if (InputContainerBorder == null || ChatDisplay == null) return;
            // Panels change the chat column without changing the window. Base gutters on
            // that column, and let every message row use the composer's measured width.
            bool narrow = ChatDisplay.ActualWidth < 760;
            ChatDisplay.Padding = new Thickness(0, narrow ? 12 : 18, 0, 6);
            InputContainerBorder.Margin = new Thickness(narrow ? 16 : 24, 0, narrow ? 16 : 24, 18);
        }

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
                Grid.SetRow(ComposerRightTools, 0);
                Grid.SetColumn(ComposerRightTools, 1);
                Grid.SetColumnSpan(ComposerRightTools, 1);
                ComposerRightTools.Margin = new Thickness(0);
                ApplyComposerLabels(compact: false);
                if (MeasureComposerRowWidth() <= available)
                    return;

                ApplyComposerLabels(compact: true);
                if (MeasureComposerRowWidth() <= available)
                    return;

                // On two rows each group has the full width to itself, so the labels come back
                // whenever both groups still fit with them.
                ApplyComposerLabels(compact: false);
                MeasureComposerRowWidth();
                if (Math.Max(ComposerLeftTools.DesiredSize.Width, ComposerRightTools.DesiredSize.Width) > available)
                    ApplyComposerLabels(compact: true);

                Grid.SetRow(ComposerRightTools, 1);
                Grid.SetColumn(ComposerRightTools, 0);
                Grid.SetColumnSpan(ComposerRightTools, 2);
                ComposerRightTools.Margin = new Thickness(0, 4, 0, 0);

                // Still too wide on its own row: shorten the model name rather than clip it.
                MeasureComposerRowWidth();
                double overflow = ComposerRightTools.DesiredSize.Width - available;
                if (overflow > 0)
                    CloudModelPickerLabel.MaxWidth = Math.Max(24, CloudModelPickerLabel.DesiredSize.Width - overflow);
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
            // A label change only dirties the label. Without this the panels hand back the size
            // they cached for the previous mode, and the row wraps when icons alone would fit.
            InvalidateMeasureTree(ComposerLeftTools);
            InvalidateMeasureTree(ComposerRightTools);
            ComposerLeftTools.Measure(unbounded);
            ComposerRightTools.Measure(unbounded);
            // Desired sizes include each panel's own margins; keep a small gap between groups.
            return ComposerLeftTools.DesiredSize.Width + ComposerRightTools.DesiredSize.Width + 12;
        }

        private static void InvalidateMeasureTree(DependencyObject node)
        {
            (node as UIElement)?.InvalidateMeasure();
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
                InvalidateMeasureTree(VisualTreeHelper.GetChild(node, i));
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
            SkillsButton.MinWidth = compact ? 30 : 0;
            PluginsButton.MinWidth = compact ? 30 : 0;
            CloudModelPickerLabel.MaxWidth = compact ? 112 : 180;
        }

        private double GetResponsiveSidebarWidth()
        {
            if (ActualWidth < CompactDesktopBreakpoint)
                return 256;
            return ActualWidth >= WideDesktopBreakpoint ? 276 : 268;
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
