using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Malx_AI.ComputerUse;
using Malx_AI.Agent;

namespace AxSim;

/// <summary>Passive capture probe: no input, launches, focus changes or window state changes.</summary>
internal static class CaptureCheck
{
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr handle);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();

    public static int CheckApplication(string work, string process)
    {
        IntPtr before = GetForegroundWindow();
        var capture = ComputerUseScreenCapture.CaptureDesktopForApplication(0, process);
        bool preserved = before == GetForegroundWindow();
        File.WriteAllBytes(Path.Combine(work, "application.jpg"), capture.JpegBytes);
        var report = new { capture.ForegroundProcessName, capture.ForegroundWindowTitle,
            TargetWindowHandle = capture.TargetWindowHandle.ToInt64(), FocusUnchanged = preserved,
            PreferredApplicationSelected = capture.ForegroundProcessName.Equals(process, StringComparison.OrdinalIgnoreCase),
            MissingApplicationIsNotReplacedByUnrelatedWindow = capture.TargetWindowHandle == IntPtr.Zero };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(json); File.WriteAllText(Path.Combine(work, "application-report.json"), json);
        return (report.PreferredApplicationSelected || report.MissingApplicationIsNotReplacedByUnrelatedWindow) && preserved ? 0 : 2;
    }

    public static async Task<int> RunPageAsync(string work)
    {
        string page = Path.Combine(work, "index.html");
        File.WriteAllText(page, "<!doctype html><html><body style='margin:0;background:#176b40;color:white;font:40px sans-serif'><h1 style='margin:80px'>Rendered in background</h1><p style='margin:80px' id='state'>Loading</p><script>setTimeout(()=>document.getElementById('state').textContent='Script executed',100)</script></body></html>");
        var handles = Process.GetProcesses().Select(p =>
        {
            try { return p.MainWindowHandle; }
            catch { return IntPtr.Zero; }
            finally { p.Dispose(); }
        }).Where(h => h != IntPtr.Zero).ToArray();
        var before = handles.ToDictionary(h => h, h => (Visible: IsWindowVisible(h), Minimized: IsIconic(h)));
        IntPtr foreground = GetForegroundWindow();
        var timer = Stopwatch.StartNew();
        var shot = await AgentPageCapture.CaptureAsync(new Uri(page).AbsoluteUri, AgentScope.Folder(work), CancellationToken.None);
        using var stream = new MemoryStream(shot.PngBytes);
        using var bitmap = new System.Drawing.Bitmap(stream);
        var pixel = bitmap.GetPixel(20, 20);
        bool correctBackground = pixel.R == 23 && pixel.G == 107 && pixel.B == 64;
        bool stateUnchanged = handles.All(h => before[h] == (IsWindowVisible(h), IsIconic(h)));
        bool focusUnchanged = foreground == GetForegroundWindow();
        var report = new { shot.Description, shot.Width, shot.Height, ElapsedMs = timer.ElapsedMilliseconds,
            CorrectBackground = correctBackground, StateUnchanged = stateUnchanged, FocusUnchanged = focusUnchanged,
            WindowsChecked = handles.Length };
        File.WriteAllBytes(Path.Combine(work, "page.png"), shot.PngBytes);
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(work, "page-report.json"), json);
        Console.WriteLine(json);
        return correctBackground && stateUnchanged && focusUnchanged ? 0 : 2;
    }

    public static async Task<int> RunAsync(string work, string? query)
    {
        var handles = Process.GetProcesses().Select(p =>
        {
            try { return p.MainWindowHandle; }
            catch { return IntPtr.Zero; }
            finally { p.Dispose(); }
        }).Where(h => h != IntPtr.Zero).ToArray();
        var before = handles.ToDictionary(h => h, h => (Visible: IsWindowVisible(h), Minimized: IsIconic(h)));
        IntPtr foreground = GetForegroundWindow();
        var timer = Stopwatch.StartNew();
        var shot = await ComputerUseScreenCapture.CaptureForAgentAsync(query, 1280, CancellationToken.None);
        bool stateUnchanged = handles.All(h => before[h] == (IsWindowVisible(h), IsIconic(h)));
        bool focusUnchanged = foreground == GetForegroundWindow();
        var report = new
        {
            shot.Description, shot.Width, shot.Height, shot.OpenWindows,
            HasImage = shot.PngBytes.Length > 0,
            ElapsedMs = timer.ElapsedMilliseconds,
            StateUnchanged = stateUnchanged, FocusUnchanged = focusUnchanged,
            WindowsChecked = handles.Length
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(work, "capture-report.json"), json);
        if (shot.PngBytes.Length > 0)
            File.WriteAllBytes(Path.Combine(work, "capture.png"), shot.PngBytes);
        Console.WriteLine(json);
        return stateUnchanged && focusUnchanged && shot.PngBytes.Length > 0 ? 0 : 2;
    }
}
