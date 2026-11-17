using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Drawing = System.Drawing;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseScreenCapture
    {
        private const int MonitorDefaultToNearest = 2;
        private static readonly SemaphoreSlim AgentCaptureGate = new(1, 1);

        public static async Task<Malx_AI.Agent.AgentScreenshot> CaptureForAgentAsync(
            string? window, int maxWidth, CancellationToken token)
        {
            // PrintWindow is synchronous and cannot be cancelled. The worker owns its bitmap
            // until it returns; keep the gate held even after a timeout so retries cannot leak
            // an unbounded number of blocked workers/GDI handles.
            if (!AgentCaptureGate.Wait(0))
                throw new InvalidOperationException("A previous window capture is still waiting for the target app. Continue with a different verification method.");
            var capture = Task.Run(() =>
            {
                try { return CaptureForAgent(window, maxWidth); }
                finally { AgentCaptureGate.Release(); }
            });
            _ = capture.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            try
            {
                return await capture.WaitAsync(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException("The target app did not render a screenshot within 5 seconds. No window was hidden or minimized. Use another verification method.");
            }
        }

        public static ComputerUseCapture CaptureDesktop(params Window[] hideWindows)
            => CaptureDesktop(0, hideWindows);

        public static ComputerUseCapture CaptureDesktop(int detailLevel, params Window[] hideWindows)
            => CaptureDesktopForApplication(detailLevel, "", hideWindows);

        public static ComputerUseCapture CaptureDesktopForApplication(int detailLevel, string application, params Window[] hideWindows)
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
                GetWindowThreadProcessId(targetWindow, out uint foregroundProcessId);
                bool hostInFront = foregroundProcessId == Environment.ProcessId;
                bool preferredFound = false;
                if (!string.IsNullOrWhiteSpace(application))
                {
                    IntPtr preferred = IntPtr.Zero;
                    EnumWindows((handle, _) =>
                    {
                        GetWindowThreadProcessId(handle, out uint candidateProcessId);
                        if (candidateProcessId != Environment.ProcessId && IsActivatableApplicationWindow(handle)
                            && GetForegroundWindowIdentity(handle).ProcessName.Equals(application, StringComparison.OrdinalIgnoreCase))
                        { preferred = handle; return false; }
                        return true;
                    }, IntPtr.Zero);
                    if (preferred != IntPtr.Zero)
                    {
                        preferredFound = true;
                        hostInFront = preferred != targetWindow;
                        targetWindow = preferred;
                        (windowTitle, processName) = GetForegroundWindowIdentity(preferred);
                    }
                }
                if (preferredFound && IsIconic(targetWindow))
                {
                    targetWindow = IntPtr.Zero;
                    hostInFront = true;
                    (windowTitle, processName) = ($"Previous work application '{application}' is minimized. Use open to restore that application before sending input.", "");
                }
                else if (!preferredFound && !string.IsNullOrWhiteSpace(application))
                {
                    // A missing work app is not permission to operate whichever unrelated app
                    // happens to be in front. Give the actor an empty target and an honest state.
                    targetWindow = IntPtr.Zero;
                    hostInFront = true;
                    (windowTitle, processName) = ($"Previous work application '{application}' is not available. Restore/open the intended application before sending input.", "");
                }
                else if (!preferredFound && (hostInFront || IsShellWindow(processName, windowTitle)))
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
                    else if (hostInFront)
                    {
                        // No work app is open yet. Keep Axiom out of the target catalog while
                        // allowing the actor to launch the requested application.
                        targetWindow = IntPtr.Zero;
                        (windowTitle, processName) = ("No external application is open", "");
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
                    if (hostInFront)
                    {
                        graphics.Clear(Drawing.Color.Black);
                        if (targetWindow != IntPtr.Zero)
                        {
                            using var targetImage = CaptureWindowBitmapBounded(targetWindow);
                            if (targetImage == null || !GetWindowRect(targetWindow, out RECT targetRect))
                                throw new InvalidOperationException("The work window could not be captured behind Axiom. No window was hidden or minimized.");
                            graphics.DrawImageUnscaled(targetImage, targetRect.Left - originX, targetRect.Top - originY);
                        }
                    }
                    else
                    {
                        graphics.CopyFromScreen(originX, originY, 0, 0, source.Size, CopyPixelOperation.SourceCopy);
                    }
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
                    Regions = ComputerUseImageGeometry.SelectRegions(
                        ComputerUseUiCatalog.CaptureRegions(targetWindow, originX, originY, width, height),
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

        /// <summary>
        /// The raw pixels of one screen area, for measuring what an action changed there.
        /// </summary>
        /// <remarks>
        /// Read straight from the screen at native resolution, with no scaling and no JPEG, so a
        /// thin stroke that disappears in the model's screenshot is still counted. Returns null
        /// when the area cannot be read; callers treat that as "unknown", never as "unchanged".
        /// </remarks>
        public static int[]? CaptureRegionArgb(int screenX, int screenY, int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > 8_000_000)
                return null;

            try
            {
                using var bitmap = new Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(screenX, screenY, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
                }

                BitmapData data = bitmap.LockBits(new Drawing.Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                try
                {
                    var pixels = new int[width * height];
                    for (int row = 0; row < height; row++)
                        Marshal.Copy(data.Scan0 + row * data.Stride, pixels, row * width, width);
                    return pixels;
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }
            catch
            {
                return null;
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

                    GetWindowThreadProcessId(handle, out uint processId);
                    if (processId == Environment.ProcessId)
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

        /// <summary>
        /// Takes the agent's screenshot: a named window, or the top external application.
        /// </summary>
        /// <remarks>
        /// A named window is drawn straight from the window itself (PrintWindow), so it comes out
        /// whole even when another window covers it and without taking the focus from whatever
        /// the user is doing. That removes the reason a model reached for AppActivate and SendKeys:
        /// a screen grab shows whatever happens to be in front, which was often not its own page.
        /// </remarks>
        public static Malx_AI.Agent.AgentScreenshot CaptureForAgent(string? windowQuery, int maxWidth = 1280, params Window[] hideWindows)
        {
            string query = (windowQuery ?? string.Empty).Trim();
            int ownProcessId = Environment.ProcessId;
            var windows = new List<(IntPtr Handle, string Title, string Process)>();
            try
            {
                EnumWindows((handle, _) =>
                {
                    if (IsActivatableApplicationWindow(handle))
                    {
                        GetWindowThreadProcessId(handle, out uint processId);
                        if (processId != ownProcessId)
                        {
                            (string title, string process) = GetForegroundWindowIdentity(handle);
                            windows.Add((handle, title, process));
                        }
                    }

                    return windows.Count < 48;
                }, IntPtr.Zero);
            }
            catch
            {
            }

            List<string> names = windows.Select(window => window.Process + ": " + window.Title).ToList();
            if (query.Length == 0)
            {
                // The top external application is the default work window. Never hide Axiom to
                // uncover it, and never label an image of a covering window as the requested app.
                if (windows.Count == 0)
                    return new Malx_AI.Agent.AgentScreenshot([], 0, 0, "No external application window is available.", names);
                query = windows[0].Title;
            }

            var match = windows.FirstOrDefault(window =>
                window.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || window.Process.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match.Handle == IntPtr.Zero)
                return new Malx_AI.Agent.AgentScreenshot([], 0, 0, "", names);

            using Drawing.Bitmap? window = CaptureWindowBitmap(match.Handle);
            if (window == null)
                return new Malx_AI.Agent.AgentScreenshot([], 0, 0,
                    $"Window '{match.Title}' could not render a capture (it may be minimized or use an unsupported surface). No window was moved, restored, hidden or minimized.", names);

            (byte[] bytes, int outWidth, int outHeight) = EncodePng(window, maxWidth);
            return new Malx_AI.Agent.AgentScreenshot(bytes, outWidth, outHeight,
                $"the window '{match.Title}' ({match.Process})", names);
        }

        private static Drawing.Bitmap? CaptureWindowBitmap(IntPtr handle)
        {
            // Capturing must not change window state, including the target's state.
            if (IsIconic(handle))
                return null;

            if (!GetWindowRect(handle, out RECT rect))
                return null;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 1 || height <= 1 || (long)width * height > 40_000_000)
                return null;

            var bitmap = new Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            bool drawn = false;
            try
            {
                using var graphics = Graphics.FromImage(bitmap);
                IntPtr hdc = graphics.GetHdc();
                try
                {
                    // PW_RENDERFULLCONTENT: required for GPU-composited windows (browsers, WebGL).
                    drawn = PrintWindow(handle, hdc, PrintWindowRenderFullContent);
                }
                finally
                {
                    graphics.ReleaseHdc(hdc);
                }
            }
            catch
            {
                drawn = false;
            }

            if (drawn && !IsSingleColor(bitmap))
                return bitmap;

            bitmap.Dispose();
            return null;
        }

        private static Drawing.Bitmap? CaptureWindowBitmapBounded(IntPtr handle)
        {
            if (!AgentCaptureGate.Wait(0))
                return null;
            var pending = Task.Run(() =>
            {
                try { return CaptureWindowBitmap(handle); }
                finally { AgentCaptureGate.Release(); }
            });
            try
            {
                return pending.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            catch
            {
                // A timed-out PrintWindow still owns its GDI resources. Dispose the result if
                // it eventually returns, and observe any eventual exception.
                _ = pending.ContinueWith(done =>
                {
                    if (done.Status == TaskStatus.RanToCompletion) done.Result?.Dispose();
                    else _ = done.Exception;
                }, TaskScheduler.Default);
                return null;
            }
        }

        private static bool IsSingleColor(Drawing.Bitmap bitmap)
        {
            int first = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2).ToArgb();
            for (int y = 8; y < bitmap.Height; y += Math.Max(1, bitmap.Height / 24))
            {
                for (int x = 8; x < bitmap.Width; x += Math.Max(1, bitmap.Width / 32))
                {
                    if (bitmap.GetPixel(x, y).ToArgb() != first)
                        return false;
                }
            }

            return true;
        }

        private static (byte[] PngBytes, int Width, int Height) EncodePng(Drawing.Bitmap source, int maxWidth)
        {
            int outWidth = source.Width;
            int outHeight = source.Height;
            if (maxWidth > 0 && outWidth > maxWidth)
            {
                outHeight = Math.Max(1, (int)Math.Round(outHeight * (maxWidth / (double)outWidth)));
                outWidth = maxWidth;
            }

            using var scaled = new Drawing.Bitmap(outWidth, outHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(scaled))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, 0, 0, outWidth, outHeight);
            }

            using var stream = new MemoryStream();
            scaled.Save(stream, ImageFormat.Png);
            return (stream.ToArray(), outWidth, outHeight);
        }

        private const uint PrintWindowRenderFullContent = 2;

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hwnd);


        public static (byte[] PngBytes, int Width, int Height) CapturePrimaryScreenPng(int maxWidth = 1280, params Window[] hideWindows)
        {
            // Legacy callers may still supply windows. Screen capture never changes their state.
            IntPtr targetWindow = GetForegroundWindow();
            (string windowTitle, string processName) = GetForegroundWindowIdentity(targetWindow);
            if (targetWindow == IntPtr.Zero || IsShellWindow(processName, windowTitle))
            {
                IntPtr fallback = FindTopmostApplicationWindow();
                if (fallback != IntPtr.Zero)
                    targetWindow = fallback;
            }

            RECT bounds = targetWindow != IntPtr.Zero
                ? GetMonitorBounds(targetWindow)
                : new RECT
                {
                    Left = 0,
                    Top = 0,
                    Right = Math.Max(1, (int)Math.Round(SystemParameters.PrimaryScreenWidth)),
                    Bottom = Math.Max(1, (int)Math.Round(SystemParameters.PrimaryScreenHeight))
                };

            int originX = bounds.Left;
            int originY = bounds.Top;
            int srcWidth = Math.Max(1, bounds.Right - bounds.Left);
            int srcHeight = Math.Max(1, bounds.Bottom - bounds.Top);

            using var source = new Drawing.Bitmap(srcWidth, srcHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(source))
            {
                graphics.CopyFromScreen(originX, originY, 0, 0, source.Size, CopyPixelOperation.SourceCopy);
            }

            int outWidth = srcWidth;
            int outHeight = srcHeight;
            Drawing.Bitmap finalBitmap = source;
            Drawing.Bitmap? resized = null;
            if (maxWidth > 0 && srcWidth > maxWidth)
            {
                outWidth = maxWidth;
                outHeight = Math.Max(1, (int)Math.Round(srcHeight * (maxWidth / (double)srcWidth)));
                resized = new Drawing.Bitmap(outWidth, outHeight, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var graphics = Graphics.FromImage(resized))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(source, 0, 0, outWidth, outHeight);
                }
                finalBitmap = resized;
            }

            try
            {
                using var ms = new MemoryStream();
                finalBitmap.Save(ms, ImageFormat.Png);
                return (ms.ToArray(), outWidth, outHeight);
            }
            finally
            {
                resized?.Dispose();
            }
        }
    }
}
