using Malx_AI.ComputerUse;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

// Builds the Computer Use session window for real and pushes controller strings through it.
// Compiling the app cannot prove a XAML file is valid: an unresolved StaticResource, a bad
// DataTemplate binding, or a renamed element only throws when the window is constructed — which,
// for this window, is the moment a user starts a session. This runs that moment on demand.
//
// Nothing is shown to the user: the window is positioned far off-screen and closed immediately.
Environment.SetEnvironmentVariable(
    "AXIOM_DATA_DIR",
    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "axiom-hud-smoke-" + Guid.NewGuid()));

int exitCode = 0;
bool paintMode = args.Contains("--paint", StringComparer.OrdinalIgnoreCase);
var thread = new Thread(() =>
{
    try
    {
        if (paintMode)
        {
            exitCode = VerifyPaintStroke() ? 0 : 1;
            return;
        }

        _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        var window = new ComputerUseSessionWindow { Left = -4000, Top = -4000 };
        window.Show();

        string[] observations =
        [
            "[OUTCOME VERIFIED] Open the Microsoft Edge browser application. Microsoft Edge is the foreground window.",
            "[ACTION NOT SENT] Target id 'none' is stale or unavailable.",
            "[NAVIGATION VERIFICATION PENDING] No loaded document URL is available yet.",
            "[VISUAL ASSESSMENT] The current page is weather.com.",
            "[EXECUTION LEDGER] Observed visible change after Key Ctrl+T.",
            "[OUTCOME STALLED] The required destination is confirmed loaded.",
            "Click left at (204, 14)",
            // A multi-line observation must become several entries, not one.
            "[BROWSER VERIFIED] A new tab is open.\n[TAB NOT OBSERVED] No additional tab was counted."
        ];
        foreach (string observation in observations)
            window.AppendLog(observation);

        window.SetStatus("Step 7/64 · thinking");
        window.BeginWait("Step 7/64 · waiting for the screen to settle");

        // Let layout and the item template actually run before inspecting anything.
        Pump();
        window.UpdateLayout();
        Pump();

        var entries = (ItemsControl)window.FindName("LogEntries");
        var hint = (UIElement)window.FindName("EmptyLogHint");
        var permission = (UIElement)window.FindName("PermissionPanel");
        int rendered = entries.Items.Count;

        // 8 observations, one of which carries two tagged lines.
        bool countsMatch = rendered == 9;
        bool hintHidden = hint.Visibility == Visibility.Collapsed;
        bool laidOut = window.ActualWidth > 0 && window.ActualHeight > 0;

        Console.WriteLine(countsMatch && hintHidden && laidOut
            ? "PASS: session window renders; XAML, resources and item template all resolve."
            : "FAIL: window constructed but did not render as expected.");
        Console.WriteLine($"  log entries rendered : {rendered} (expected 9)");
        Console.WriteLine($"  empty hint hidden    : {hintHidden}");
        Console.WriteLine($"  laid out             : {(int)window.ActualWidth} x {(int)window.ActualHeight}");
        Console.WriteLine($"  permission panel     : {permission.Visibility} (expected Collapsed until asked)");

        // The badge classification the panel relies on, exercised through the real window.
        (string badge, ComputerUseLogSeverity severity, _) =
            ComputerUseLogFormatting.Classify("[TAB VERIFICATION FAILED] Ctrl+T did not produce a tab.");
        bool failureStaysFailure = badge == "Blocked" && severity == ComputerUseLogSeverity.Blocked;
        Console.WriteLine($"  failure not shown as verified : {failureStaysFailure}");

        if (!countsMatch || !hintHidden || !laidOut || !failureStaysFailure)
            exitCode = 1;

        window.EndWait();
        window.Close();

        if (!VerifyDrag())
            exitCode = 1;

        if (!VerifyWindowTargeting())
            exitCode = 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine("FAIL: the session window could not be constructed or shown.");
        Console.WriteLine(ex);
        exitCode = 1;
    }
    finally
    {
        Dispatcher.CurrentDispatcher.InvokeShutdown();
    }
});

thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join(TimeSpan.FromSeconds(60));
return exitCode;

// Drives a REAL drag into a window of our own and checks the sequence an application receives.
// The last drag change compiled, passed every unit test, and then threw the moment it ran, because
// the pointer overlay was touched from the thread the input loop continues on. Only running it
// finds that.
static bool VerifyDrag()
{
    var received = new List<string>();
    var target = new Window
    {
        Title = "drag target",
        WindowStyle = WindowStyle.None,
        ShowInTaskbar = false,
        Topmost = true,
        Left = 60,
        Top = 60,
        Width = 400,
        Height = 300,
        Background = System.Windows.Media.Brushes.Black
    };
    target.MouseLeftButtonDown += (_, _) => received.Add("down");
    target.MouseMove += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) received.Add("move"); };
    target.MouseLeftButtonUp += (_, _) => received.Add("up");
    target.Show();
    target.Activate();
    Pump();
    Thread.Sleep(400);

    // The overlay is the object that crashed. Include it so the callback path is the real one.
    var pointer = new ComputerUsePointerWindow();
    pointer.Show();
    Pump();

    int callbackSteps = 0;
    string? callbackFailure = null;
    try
    {
        // Exactly the marshalling the controller now uses for every pointer step.
        Action<int, int> onStep = (x, y) =>
        {
            Interlocked.Increment(ref callbackSteps);
            pointer.Dispatcher.Invoke(() => pointer.PlaceAtScreen(x, y));
        };

        Task drag = ComputerUseNativeInput.DragAsync(140, 160, 380, 300, onStep, CancellationToken.None);
        while (!drag.IsCompleted)
        {
            Pump();
            Thread.Sleep(20);
        }

        drag.GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        callbackFailure = ex.ToString();
    }

    Pump();
    Thread.Sleep(200);
    Pump();
    pointer.Close();
    target.Close();

    int downs = received.Count(e => e == "down");
    int moves = received.Count(e => e == "move");
    int ups = received.Count(e => e == "up");
    bool sequenceOk = downs == 1 && ups == 1 && moves >= 3;
    bool callbackOk = callbackFailure == null && callbackSteps > 0;

    Console.WriteLine();
    Console.WriteLine(sequenceOk && callbackOk
        ? "PASS: a drag delivers press, movement while held, and release to a real window."
        : "FAIL: the drag did not deliver a usable stroke.");
    Console.WriteLine($"  button down          : {downs} (expected 1)");
    Console.WriteLine($"  moves while held     : {moves} (expected 3+; a stroke, not a jump)");
    Console.WriteLine($"  button up            : {ups} (expected 1)");
    Console.WriteLine($"  overlay steps        : {callbackSteps}, threw: {(callbackFailure == null ? "no" : "YES")}");
    if (callbackFailure != null)
        Console.WriteLine(callbackFailure);

    return sequenceOk && callbackOk;
}

// Checks the two things that just broke: a capture must name the windows really on screen, and
// the window it picks as the input target must actually be activatable. "IsWindowVisible" is not
// that test — Windows keeps suspended app windows cloaked, visible and titled but impossible to
// bring forward — and choosing one made every action fail with "could not be activated".
static bool VerifyWindowTargeting()
{
    const string uniqueTitle = "Axiom smoke target 8891";
    var app = new Window
    {
        Title = uniqueTitle,
        WindowStyle = WindowStyle.SingleBorderWindow,
        ShowInTaskbar = true,
        Left = 80,
        Top = 80,
        Width = 520,
        Height = 360,
        Background = System.Windows.Media.Brushes.DimGray
    };
    app.Show();
    app.Activate();
    Pump();
    Thread.Sleep(500);
    Pump();

    var handle = new System.Windows.Interop.WindowInteropHelper(app).Handle;
    bool ownWindowActivates = ComputerUseNativeInput.ActivateWindow(handle);

    ComputerUseCapture capture = ComputerUseScreenCapture.CaptureDesktop();
    bool listed = capture.VisibleWindows.Any(w => w.Contains(uniqueTitle, StringComparison.OrdinalIgnoreCase));
    bool recognised = ComputerUseApplicationVerification.IsRequestedApplicationVisible(uniqueTitle, capture);

    // The one that failed in production: whatever the capture chose as the target must be a window
    // input can actually be aimed at.
    bool targetActivates = capture.TargetWindowHandle != IntPtr.Zero
        && ComputerUseNativeInput.ActivateWindow(capture.TargetWindowHandle);

    // No cloaked window may reach the list; every entry must carry a real title.
    bool listClean = capture.VisibleWindows.All(w => w.Contains(": ") && w.Split(": ")[^1].Trim().Length > 0);

    app.Close();
    Pump();

    bool ok = ownWindowActivates && listed && recognised && targetActivates && listClean;
    Console.WriteLine();
    Console.WriteLine(ok
        ? "PASS: the capture names on-screen windows and targets one that input can be aimed at."
        : "FAIL: window targeting is wrong; actions would be refused.");
    Console.WriteLine($"  a real window activates      : {ownWindowActivates}");
    Console.WriteLine($"  listed among visible windows : {listed} (of {capture.VisibleWindows.Count} listed)");
    Console.WriteLine($"  recognised by name           : {recognised}");
    Console.WriteLine($"  captured target activates    : {targetActivates}");
    Console.WriteLine($"  no untitled/cloaked entries  : {listClean}");
    return ok;
}

// The exact production question, asked of the real application: when Computer Use drags across
// Paint's canvas, does a stroke appear? Launches Paint, activates it through the same code the
// controller uses, drags through the same code, reads the pixels along the path, and closes Paint
// without saving. Opt-in (--paint) because it takes over the mouse for about a second.
static bool VerifyPaintStroke()
{
    System.Diagnostics.Process? paint = null;
    try
    {
        paint = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("mspaint.exe") { UseShellExecute = true });
        IntPtr window = IntPtr.Zero;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && window == IntPtr.Zero)
        {
            Thread.Sleep(300);
            window = FindWindowByTitleFragment("Paint");
        }

        if (window == IntPtr.Zero)
        {
            Console.WriteLine("FAIL: Paint did not open a window.");
            return false;
        }

        Thread.Sleep(1500); // let the canvas finish drawing
        bool activated = ComputerUseNativeInput.ActivateWindow(window);
        Thread.Sleep(400);

        GetWindowRect(window, out Rect rect);
        int centreX = (rect.Left + rect.Right) / 2;
        int centreY = rect.Top + (int)((rect.Bottom - rect.Top) * 0.62);
        int startX = centreX - 180, endX = centreX + 180;
        int startY = centreY - 60, endY = centreY + 60;

        var samples = Enumerable.Range(1, 9)
            .Select(i => (X: startX + (endX - startX) * i / 10, Y: startY + (endY - startY) * i / 10))
            .ToArray();
        int[] before = samples.Select(p => Brightness(p.X, p.Y)).ToArray();

        var drag = ComputerUseNativeInput.DragAsync(startX, startY, endX, endY, null, CancellationToken.None);
        drag.GetAwaiter().GetResult();
        Thread.Sleep(600);

        int[] after = samples.Select(p => Brightness(p.X, p.Y)).ToArray();
        int darkened = before.Zip(after).Count(pair => pair.Second < pair.First - 60);

        bool onCanvas = before.Count(b => b > 200) >= 6;
        bool drew = darkened >= 3;

        Console.WriteLine(drew
            ? "PASS: a Computer Use drag draws a real stroke in Paint."
            : "FAIL: the drag reached Paint but left no stroke on the canvas.");
        Console.WriteLine($"  Paint window activated : {activated}");
        Console.WriteLine($"  path started on canvas : {onCanvas} (brightness before: {string.Join(",", before)})");
        Console.WriteLine($"  points darkened        : {darkened} of {samples.Length} (after: {string.Join(",", after)})");
        return drew;
    }
    finally
    {
        // Close without the save prompt; nothing drawn here should outlive the check.
        try { if (paint != null && !paint.HasExited) paint.Kill(entireProcessTree: true); } catch { }
        foreach (var leftover in System.Diagnostics.Process.GetProcessesByName("mspaint"))
        {
            try
            {
                if (leftover.StartTime > DateTime.Now.AddMinutes(-2))
                    leftover.Kill();
            }
            catch { }
        }
    }
}

static int Brightness(int x, int y)
{
    IntPtr dc = GetDC(IntPtr.Zero);
    try
    {
        uint colour = GetPixel(dc, x, y);
        int r = (int)(colour & 0xFF), g = (int)((colour >> 8) & 0xFF), b = (int)((colour >> 16) & 0xFF);
        return (r + g + b) / 3;
    }
    finally
    {
        ReleaseDC(IntPtr.Zero, dc);
    }
}

static IntPtr FindWindowByTitleFragment(string fragment)
{
    IntPtr found = IntPtr.Zero;
    EnumWindows((handle, _) =>
    {
        if (!IsWindowVisible(handle))
            return true;
        var text = new System.Text.StringBuilder(256);
        GetWindowText(handle, text, text.Capacity);
        if (text.ToString().EndsWith(fragment, StringComparison.OrdinalIgnoreCase))
        {
            found = handle;
            return false;
        }
        return true;
    }, IntPtr.Zero);
    return found;
}

[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern IntPtr GetDC(IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
[System.Runtime.InteropServices.DllImport("gdi32.dll")]
static extern uint GetPixel(IntPtr dc, int x, int y);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
[System.Runtime.InteropServices.DllImport("user32.dll")]
static extern bool IsWindowVisible(IntPtr hwnd);
[System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int max);

static void Pump()
{
    var frame = new DispatcherFrame();
    Dispatcher.CurrentDispatcher.BeginInvoke(
        DispatcherPriority.ApplicationIdle,
        new Action(() => frame.Continue = false));
    Dispatcher.PushFrame(frame);
}

delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
struct Rect { public int Left, Top, Right, Bottom; }
