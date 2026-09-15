using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseScreenCapture
    {
        private const int MonitorDefaultToNearest = 2;

        public static ComputerUseCapture CaptureDesktop(params Window[] hideWindows)
            => CaptureDesktop(0, hideWindows);

        public static ComputerUseCapture CaptureDesktop(int detailLevel, params Window[] hideWindows)
        {
            int level = ComputerUseImageGeometry.ClampDetailLevel(detailLevel);
            long jpegQuality = ComputerUseImageGeometry.JpegQualityForDetailLevel(level);
            Window[] windows = (hideWindows ?? []).Where(window => window != null).ToArray();
            bool[] wasVisible = windows.Select(window => window.IsVisible).ToArray();
            try
            {
                foreach (Window window in windows)
                {
                    try { window.Hide(); } catch { }
                }

                // Hiding the session panel moves focus, and Windows routes it through the desktop
                // shell on the way to wherever it lands. Reading the foreground too early captures
                // that intermediate state and reports the shell instead of the application on
                // screen. This is long enough for focus to settle without being felt as lag.
                System.Threading.Thread.Sleep(140);
                List<string> visibleWindows = EnumerateVisibleWindows();
                IntPtr targetWindow = GetForegroundWindow();
                (string windowTitle, string processName) = GetForegroundWindowIdentity(targetWindow);
                if (IsShellWindow(processName, windowTitle))
                {
                    // Focus landed on the desktop rather than on any application. Aiming input
                    // there would send it to the shell; prefer the top visible application window,
                    // which is what the screenshot actually shows.
                    IntPtr fallback = FindTopmostApplicationWindow();
                    if (fallback != IntPtr.Zero)
                    {
                        targetWindow = fallback;
                        (windowTitle, processName) = GetForegroundWindowIdentity(fallback);
                    }
                }
                RECT bounds = GetMonitorBounds(targetWindow);
                int originX = bounds.Left;
                int originY = bounds.Top;
                int width = Math.Max(1, bounds.Right - bounds.Left);
                int height = Math.Max(1, bounds.Bottom - bounds.Top);

                using var source = new Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(source))
                {
                    graphics.CopyFromScreen(originX, originY, 0, 0, source.Size, CopyPixelOperation.SourceCopy);
                }

                var imageSize = ComputerUseImageGeometry.GetSizeForDetailLevel(width, height, level);
                using var modelImage = new Drawing.Bitmap(imageSize.Width, imageSize.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(modelImage))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, 0, 0, modelImage.Width, modelImage.Height);
                }
                OverlayCoordinateGuides(modelImage);

                using var stream = new MemoryStream();
                ImageCodecInfo? jpeg = ImageCodecInfo.GetImageEncoders().FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
                if (jpeg == null)
                {
                    modelImage.Save(stream, ImageFormat.Jpeg);
                }
                else
                {
                    using var parameters = new EncoderParameters(1);
                    parameters.Param[0] = new EncoderParameter(Encoder.Quality, jpegQuality);
                    modelImage.Save(stream, jpeg, parameters);
                }

                return new ComputerUseCapture
                {
                    JpegBytes = stream.ToArray(),
                    ScreenX = originX,
                    ScreenY = originY,
                    ScreenWidth = width,
                    ScreenHeight = height,
                    ImageWidth = modelImage.Width,
                    ImageHeight = modelImage.Height,
                    DetailLevel = level,
                    TargetWindowHandle = targetWindow,
                    ForegroundWindowTitle = windowTitle,
                    VisibleWindows = visibleWindows,
                    ForegroundProcessName = processName,
                    UiTargets = ComputerUseImageGeometry.ScaleTargets(
                        ComputerUseUiCatalog.Capture(targetWindow, originX, originY, width, height),
                        width, height, modelImage.Width, modelImage.Height),
                    BrowserState = ComputerUseUiCatalog.CaptureBrowserState(targetWindow)
                };
            }
            finally
            {
                for (int i = 0; i < windows.Length; i++)
                {
                    if (!wasVisible[i])
                        continue;
                    try { windows[i].Show(); } catch { }
                }
            }
        }

        public static System.Windows.Point DipFromScreen(Window window, int screenX, int screenY)
        {
            var source = PresentationSource.FromVisual(window) as HwndSource;
            System.Windows.Media.Matrix transform = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
            if (transform.IsIdentity)
            {
                DpiScale dpi = VisualTreeHelper.GetDpi(window);
                double scaleX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
                double scaleY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;
                transform = new System.Windows.Media.Matrix(1d / scaleX, 0, 0, 1d / scaleY, 0, 0);
            }

            return transform.Transform(new System.Windows.Point(screenX, screenY));
        }

        private static void OverlayCoordinateGuides(Drawing.Bitmap image)
        {
            using var graphics = Graphics.FromImage(image);
            graphics.SmoothingMode = SmoothingMode.None;
            graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            int stepX = image.Width >= 1600 ? 200 : 100;
            int stepY = image.Height >= 1000 ? 200 : 100;
            int overlayBottom = Math.Max(0, image.Height - 80);
            using var gridPen = new Drawing.Pen(Drawing.Color.FromArgb(55, 255, 200, 70), 1);
            using var font = new Font("Segoe UI", 9f, Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var textBrush = new SolidBrush(Drawing.Color.FromArgb(235, 255, 214, 90));
            using var labelBack = new SolidBrush(Drawing.Color.FromArgb(170, 28, 22, 18));

            for (int x = 0; x < image.Width; x += stepX)
                graphics.DrawLine(gridPen, x, 0, x, overlayBottom);
            for (int y = 0; y < overlayBottom; y += stepY)
                graphics.DrawLine(gridPen, 0, y, image.Width, y);

            DrawLabel(graphics, font, textBrush, labelBack, 4, 4, "0,0 image");
            DrawLabel(graphics, font, textBrush, labelBack, image.Width - 110, 4, $"x={image.Width - 1}");
            DrawLabel(graphics, font, textBrush, labelBack, 4, Math.Max(4, overlayBottom - 18), $"y={image.Height - 1}");

            for (int x = stepX; x < image.Width - 40; x += stepX)
                DrawLabel(graphics, font, textBrush, labelBack, x + 2, 4, x.ToString());
            for (int y = stepY; y < overlayBottom - 18; y += stepY)
                DrawLabel(graphics, font, textBrush, labelBack, 4, y + 2, y.ToString());
        }

        private static void DrawLabel(Graphics graphics, Font font, SolidBrush textBrush, SolidBrush background, int x, int y, string text)
        {
            Drawing.SizeF size = graphics.MeasureString(text, font);
            graphics.FillRectangle(background, x, y, size.Width + 2, size.Height);
            graphics.DrawString(text, font, textBrush, x, y);
        }

        private static RECT GetMonitorBounds(IntPtr targetWindow)
        {
            IntPtr monitor = targetWindow == IntPtr.Zero
                ? MonitorFromPoint(new POINT { X = 0, Y = 0 }, MonitorDefaultToNearest)
                : MonitorFromWindow(targetWindow, MonitorDefaultToNearest);
            var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                return info.Monitor;

            return new RECT
            {
                Left = 0,
                Top = 0,
                Right = Math.Max(1, GetSystemMetrics(0)),
                Bottom = Math.Max(1, GetSystemMetrics(1))
            };
        }

        // Shell surfaces are not applications: focus passes through them, and treating one as the
        // active app makes "is Paint open" answer no while Paint fills the screen.
        private static readonly HashSet<string> ShellProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "shellexperiencehost", "searchhost", "startmenuexperiencehost",
            "applicationframehost", "textinputhost", "dwm"
        };

        private static bool IsShellWindow(string processName, string windowTitle)
            => ShellProcessNames.Contains(processName.Trim())
                && string.IsNullOrWhiteSpace(windowTitle);

        /// <summary>
        /// Whether a window is one a person could actually switch to.
        /// </summary>
        /// <remarks>
        /// IsWindowVisible alone is not this test, and assuming it was is what broke activation.
        /// Windows keeps suspended app windows CLOAKED: still "visible", still titled, present in
        /// the enumeration, and impossible to bring to the foreground. Picking one as the target
        /// meant every action was refused because the window could not be activated. This is the
        /// same set of checks the alt-tab list uses.
        /// </remarks>
        private static bool IsActivatableApplicationWindow(IntPtr handle)
        {
            if (!IsWindowVisible(handle))
                return false;

            // A cloaked window is composited away; it cannot take the foreground.
            if (DwmGetWindowAttribute(handle, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;

            // Owned windows are dialogs and popups belonging to another window.
            if (GetWindow(handle, GwOwner) != IntPtr.Zero)
                return false;

            // Tool windows are palettes and overlays, deliberately absent from alt-tab.
            if ((GetWindowLong(handle, GwlExStyle) & WsExToolWindow) != 0)
                return false;

            if (!GetWindowRect(handle, out RECT rect))
                return false;
            if (rect.Right - rect.Left <= 1 || rect.Bottom - rect.Top <= 1)
                return false;

            (string title, string process) = GetForegroundWindowIdentity(handle);
            return !string.IsNullOrWhiteSpace(title)
                && !string.IsNullOrWhiteSpace(process)
                && !IsShellWindow(process, title);
        }

        /// <summary>Switchable top-level windows as "process: title", front to back.</summary>
        private static List<string> EnumerateVisibleWindows()
        {
            var windows = new List<string>();
            try
            {
                EnumWindows((handle, _) =>
                {
                    if (IsActivatableApplicationWindow(handle))
                    {
                        (string title, string process) = GetForegroundWindowIdentity(handle);
                        windows.Add(process + ": " + title);
                    }

                    return windows.Count < 64;
                }, IntPtr.Zero);
            }
            catch
            {
                // Enumeration is supporting evidence; never let it stop a capture.
            }

            return windows;
        }

        private static IntPtr FindTopmostApplicationWindow()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                // EnumWindows walks front to back, so the first match is the frontmost real window.
                EnumWindows((handle, _) =>
                {
                    if (!IsActivatableApplicationWindow(handle))
                        return true;

                    found = handle;
                    return false;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            return found;
        }

        private const int DwmwaCloaked = 14;
        private const int GwOwner = 4;
        private const int GwlExStyle = -20;
        private const int WsExToolWindow = 0x00000080;

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        private static (string Title, string ProcessName) GetForegroundWindowIdentity(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return (string.Empty, string.Empty);

            string title = string.Empty;
            try
            {
                var text = new System.Text.StringBuilder(512);
                if (GetWindowText(windowHandle, text, text.Capacity) > 0)
                    title = text.ToString().Trim();
            }
            catch
            {
            }

            string processName = string.Empty;
            try
            {
                GetWindowThreadProcessId(windowHandle, out uint processId);
                if (processId != 0)
                    processName = Process.GetProcessById((int)processId).ProcessName ?? string.Empty;
            }
            catch
            {
            }

            return (title, processName);
        }

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT Work;
            public uint Flags;
        }
    }
}
