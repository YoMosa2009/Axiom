using System;
using System.Runtime.InteropServices;
using System.Windows;
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

            NavigationBar.Padding = compact ? new Thickness(10, 7, 10, 7) : new Thickness(12, 8, 12, 8);
            ChatHeaderBorder.Padding = compact ? new Thickness(20, 10, 20, 2) : new Thickness(28, 14, 28, 4);
            TokenUsagePanel.Width = compact ? 300 : wide ? 380 : 340;
            ChatDisplay.Padding = compact
                ? new Thickness(20, 12, 20, 18)
                : wide
                    ? new Thickness(40, 22, 40, 28)
                    : new Thickness(28, 18, 28, 24);

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
