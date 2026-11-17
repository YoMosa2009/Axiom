using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Malx_AI.ComputerUse;
using Drawing = System.Drawing;

namespace AxSim;

/// <summary>
/// Real-input probe: opens a fresh Paint window and sends the same injected mouse input the
/// Computer Use controller sends (DragPathAsync, LeftClickAt), then measures the canvas the way
/// the controller does. Moves the real mouse for a few seconds. Leaves Paint open.
/// </summary>
internal static class DragProbe
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    public static async Task<int> RunAsync(string work)
    {
        var report = new StringBuilder();
        void Log(string line) { Console.WriteLine(line); report.AppendLine(line); }

        var before = Process.GetProcessesByName("mspaint").Select(process => process.Id).ToHashSet();
        Process.Start(new ProcessStartInfo("mspaint.exe") { UseShellExecute = true });
        Process? mine = null;
        for (int i = 0; i < 40 && mine == null; i++)
        {
            Thread.Sleep(500);
            mine = Process.GetProcessesByName("mspaint").FirstOrDefault(process => !before.Contains(process.Id) && process.MainWindowHandle != IntPtr.Zero);
        }

        if (mine == null)
        {
            Log("Paint did not open.");
            return 2;
        }

        Thread.Sleep(3500);
        mine.Refresh();
        IntPtr handle = mine.MainWindowHandle;
        var cursorAtStart = ComputerUseNativeInput.GetCursor();
        int screenWidth = GetSystemMetrics(0), screenHeight = GetSystemMetrics(1);
        GetWindowRect(handle, out Rect window);
        Log($"Paint pid {mine.Id}, window {window.Left},{window.Top}-{window.Right},{window.Bottom}, screen {screenWidth}x{screenHeight}");

        var regions = ComputerUseUiCatalog.CaptureRegions(handle, 0, 0, screenWidth, screenHeight);
        var canvas = regions
            .Where(region => region.Name.Contains("Canvas", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(region => (long)(region.Right - region.Left) * (region.Bottom - region.Top))
            .FirstOrDefault();
        if (canvas == null)
        {
            Log("No canvas region found. Regions: " + string.Join("; ", regions.Select(region => region.Describe())));
            return 3;
        }

        Log("Canvas: " + canvas.Describe());
        int cx = (canvas.Left + canvas.Right) / 2, cy = (canvas.Top + canvas.Bottom) / 2;
        int span = Math.Min(canvas.Right - canvas.Left, canvas.Bottom - canvas.Top);
        int r = Math.Max(30, span / 8);

        bool activated = ComputerUseNativeInput.ActivateWindow(handle);
        Log($"ActivateWindow: {activated}; foreground is Paint: {ComputerUseNativeInput.ForegroundWindow() == handle}");
        await Task.Delay(300);
        SaveWindow(window, Path.Combine(work, "probe-0-before.png"));

        int passed = 0, total = 0;
        async Task Stroke(string name, IReadOnlyList<(int X, int Y)> path)
        {
            total++;
            var area = ComputerUseStrokeGeometry.Bounds([path], 14, 0, 0, screenWidth, screenHeight);
            int[]? pre = ComputerUseScreenCapture.CaptureRegionArgb(area.Left, area.Top, area.Width, area.Height);
            var watch = Stopwatch.StartNew();
            bool sent = await ComputerUseNativeInput.DragPathAsync(path, null, CancellationToken.None);
            await Task.Delay(220);
            int[]? post = ComputerUseScreenCapture.CaptureRegionArgb(area.Left, area.Top, area.Width, area.Height);
            int changed = ComputerUseObservationEvidence.CountChangedPixels(pre, post);
            double length = ComputerUseStrokeGeometry.Length(path);
            bool visible = ComputerUseObservationEvidence.IsDragEffectVisible(changed, length);
            if (visible && sent) passed++;
            Log($"{name}: sent={sent} changed={changed} pathLength={length:0} verdict={(visible ? "[DRAG VERIFIED]" : "[DRAG NO EFFECT]")} ({watch.ElapsedMilliseconds} ms)");
        }

        // 1. Default tool, freehand ellipse (the "head").
        await Stroke("ellipse stroke, default tool", ComputerUseStrokeGeometry.Ellipse(cx - r * 2, cy - r, r, r));
        // 2. Straight line (the "body").
        await Stroke("straight line, default tool", [(cx - r * 2, cy), (cx - r * 2, cy + r * 2)]);

        // 3. Select the Rectangle shape tool with a real click, then drag corner to corner.
        var targets = ComputerUseUiCatalog.Capture(handle, 0, 0, screenWidth, screenHeight);
        var rectangle = targets.FirstOrDefault(target => target.Name.Equals("Rectangle", StringComparison.OrdinalIgnoreCase));
        if (rectangle == null)
        {
            Log("Rectangle tool not found in the accessibility tree; skipped the shape test.");
        }
        else
        {
            Log("Clicking " + rectangle.Describe());
            ComputerUseNativeInput.LeftClickAt(rectangle.ImageX, rectangle.ImageY);
            await Task.Delay(400);
            var after = ComputerUseUiCatalog.Capture(handle, 0, 0, screenWidth, screenHeight)
                .FirstOrDefault(target => target.Name.Equals("Rectangle", StringComparison.OrdinalIgnoreCase));
            Log("After click: " + (after?.Describe() ?? "(not found)"));
            await Stroke("rectangle shape, corner to corner", [(cx + r, cy - r), (cx + r * 3, cy + r)]);
        }

        SaveWindow(window, Path.Combine(work, "probe-1-after.png"));
        SetCursorPos(cursorAtStart.X, cursorAtStart.Y);
        Log($"RESULT: {passed}/{total} strokes measured as drawn. Paint (pid {mine.Id}) is left open: close it and choose Don't save.");
        File.WriteAllText(Path.Combine(work, "probe-report.txt"), report.ToString());
        return passed == total ? 0 : 4;
    }

    private static void SaveWindow(Rect window, string path)
    {
        int width = window.Right - window.Left, height = window.Bottom - window.Top;
        if (width <= 0 || height <= 0)
            return;
        using var bitmap = new Drawing.Bitmap(width, height);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(window.Left, window.Top, 0, 0, bitmap.Size);
        bitmap.Save(path, ImageFormat.Png);
    }
}
