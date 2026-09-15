using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Malx_AI
{
    /// <summary>
    /// Paints a window's native title bar to match the application's own colours.
    /// </summary>
    /// <remarks>
    /// WPF does not own the caption area, so a window keeps the system's light title bar however
    /// dark its content is — the bright strip above a dark app. The alternative to this is
    /// <c>WindowStyle="None"</c> with a hand-drawn caption, which means reimplementing dragging,
    /// snapping, maximise, and the system menu, and getting all of them subtly wrong. Asking DWM to
    /// colour the real caption keeps every native behaviour intact.
    /// </remarks>
    internal static class WindowTitleBarTheme
    {
        // Documented DWM window attributes. Caption/text colouring needs Windows 11 build 22000+;
        // dark mode works from Windows 10 1809. Unsupported values simply return an error code,
        // which is why every call here is best-effort.
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;
        private const int DwmwaBorderColor = 34;

        /// <summary>
        /// Applies the dark caption once the window has a handle, and on every later reopen.
        /// </summary>
        public static void Apply(Window window)
        {
            ArgumentNullException.ThrowIfNull(window);

            if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
                ApplyToHandle(window);
            else
                window.SourceInitialized += (_, _) => ApplyToHandle(window);
        }

        private static void ApplyToHandle(Window window)
        {
            try
            {
                IntPtr handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero)
                    return;

                int darkMode = 1;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));

                // Match the app's own surfaces exactly rather than settling for the generic dark
                // caption, so the title bar reads as part of the window instead of a strip on top.
                // This uses the navigation bar's own brush, the surface directly beneath the
                // caption, so the two read as one continuous dark band.
                int caption = ToColorRef(TryFindColor(window, "DarkSidebarBrush", 0x17, 0x16, 0x15));
                DwmSetWindowAttribute(handle, DwmwaCaptionColor, ref caption, sizeof(int));

                int text = ToColorRef(TryFindColor(window, "NotebookTextBrush", 0xED, 0xE8, 0xE3));
                DwmSetWindowAttribute(handle, DwmwaTextColor, ref text, sizeof(int));

                int border = ToColorRef(TryFindColor(window, "NotebookBorderBrush", 0x30, 0x2D, 0x2A));
                DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(int));
            }
            catch (Exception)
            {
                // An older Windows build simply keeps its default caption. Never let chrome
                // decoration stop a window from opening.
            }
        }

        private static Color TryFindColor(Window window, string resourceKey, byte fallbackR, byte fallbackG, byte fallbackB)
        {
            if (window.TryFindResource(resourceKey) is SolidColorBrush brush)
                return brush.Color;
            if (Application.Current?.TryFindResource(resourceKey) is SolidColorBrush appBrush)
                return appBrush.Color;
            return Color.FromRgb(fallbackR, fallbackG, fallbackB);
        }

        // DWM takes a COLORREF: 0x00BBGGRR, not the ARGB order a colour literal reads in.
        private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    }
}
