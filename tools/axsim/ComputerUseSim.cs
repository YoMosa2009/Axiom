using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Malx_AI.ComputerUse;
using Drawing = System.Drawing;

namespace AxSim;

internal sealed class PaintSnapshot
{
    public int ScreenWidth { get; set; }
    public int ScreenHeight { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string Title { get; set; } = "Untitled - Paint";
    public List<SnapTarget> Targets { get; set; } = new();
    public List<SnapRegion> Regions { get; set; } = new();
}

internal sealed class SnapTarget
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string State { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
}

internal sealed class SnapRegion
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
}

/// <summary>
/// Reads the real Paint window: its picture and its accessibility tree. Sends no input.
/// </summary>
internal static class PaintShot
{
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr handle, out Rect rect);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }

    public static int Run(string work)
    {
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
            Console.WriteLine("Paint did not open.");
            return 2;
        }

        try
        {
            Thread.Sleep(3500);
            IntPtr handle = mine.MainWindowHandle;
            int screenWidth = GetSystemMetrics(0), screenHeight = GetSystemMetrics(1);
            GetWindowRect(handle, out Rect rect);
            var targets = ComputerUseUiCatalog.Capture(handle, 0, 0, screenWidth, screenHeight);
            var regions = ComputerUseUiCatalog.CaptureRegions(handle, 0, 0, screenWidth, screenHeight);
            var shot = ComputerUseScreenCapture.CaptureForAgent("Paint", 0);
            if (shot.PngBytes.Length == 0)
            {
                Console.WriteLine("Could not capture the Paint window.");
                return 3;
            }

            File.WriteAllBytes(Path.Combine(work, "paint.png"), shot.PngBytes);
            var snapshot = new PaintSnapshot
            {
                ScreenWidth = screenWidth, ScreenHeight = screenHeight,
                Left = rect.Left, Top = rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top,
                Targets = targets.Select(target => new SnapTarget
                {
                    Id = target.Id, Name = target.Name, Role = target.Role, State = target.State,
                    X = target.ImageX, Y = target.ImageY, W = target.ImageWidth, H = target.ImageHeight
                }).ToList(),
                Regions = regions.Select(region => new SnapRegion
                {
                    Name = region.Name, Role = region.Role, Left = region.Left, Top = region.Top, Right = region.Right, Bottom = region.Bottom
                }).ToList()
            };
            File.WriteAllText(Path.Combine(work, "paint.json"), JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{shot.Description} {shot.Width}x{shot.Height}; window at {rect.Left},{rect.Top}; screen {screenWidth}x{screenHeight}; {targets.Count} targets; {regions.Count} regions");
            foreach (var target in targets)
                Console.WriteLine("  " + target.Describe() + $" size={target.ImageWidth}x{target.ImageHeight}");
            foreach (var region in ComputerUseImageGeometry.SelectRegions(regions, screenWidth, screenHeight, screenWidth, screenHeight))
                Console.WriteLine("  REGION " + region.Describe());
            return 0;
        }
        finally
        {
            try { mine.CloseMainWindow(); } catch { }
        }
    }
}

/// <summary>
/// A stand-in for the desktop: the real picture and controls of Paint, with a canvas that draws
/// what a drag would draw and tool buttons that switch tools. Everything that decides what the
/// model is told is the app's own code.
/// </summary>
internal sealed class SimDesktop : IDisposable
{
    private static readonly string[] FreehandTools = ["Brush", "Pencil", "Eraser"];
    private readonly PaintSnapshot _paint;
    private readonly Drawing.Bitmap _paintImage;
    private readonly Drawing.Bitmap _ink;
    private readonly Drawing.Rectangle _canvas;
    private readonly string _imageDir;
    private int _captures;

    public bool PaintOpen { get; private set; }
    public string ActiveTool { get; private set; } = "Brush";
    private SimBrowser? _edge;
    /// <summary>The foreground application: "Code" (what was in front at the start), "Paint" or "Edge".</summary>
    public string Front { get; private set; } = "Code";
    public string BrowserUrl => _edge?.Url ?? "";

    public SimDesktop(string work)
    {
        _paint = JsonSerializer.Deserialize<PaintSnapshot>(File.ReadAllText(Path.Combine(work, "paint.json")))!;
        _paintImage = new Drawing.Bitmap(Path.Combine(work, "paint.png"));
        _ink = new Drawing.Bitmap(_paint.ScreenWidth, _paint.ScreenHeight, PixelFormat.Format32bppArgb);
        SnapRegion canvas = _paint.Regions.First(region => region.Name.Contains("Canvas", StringComparison.OrdinalIgnoreCase));
        _canvas = Drawing.Rectangle.FromLTRB(canvas.Left, canvas.Top, canvas.Right, canvas.Bottom);
        _imageDir = Path.Combine(work, "images", "cu");
        Directory.CreateDirectory(_imageDir);
    }

    public string LastImagePath { get; private set; } = "";

    private Drawing.Bitmap Compose()
    {
        var screen = new Drawing.Bitmap(_paint.ScreenWidth, _paint.ScreenHeight, PixelFormat.Format24bppRgb);
        using var graphics = Drawing.Graphics.FromImage(screen);
        graphics.Clear(Drawing.Color.FromArgb(24, 40, 72));
        if (Front == "Edge")
        {
            _edge!.Draw(graphics);
            using var taskbar = new Drawing.SolidBrush(Drawing.Color.FromArgb(20, 20, 20));
            graphics.FillRectangle(taskbar, 0, _paint.ScreenHeight - (int)(_paint.ScreenHeight * 0.048), _paint.ScreenWidth, _paint.ScreenHeight);
            return screen;
        }

        if (Front == "Code")
        {
            using var panel = new Drawing.SolidBrush(Drawing.Color.FromArgb(30, 30, 30));
            using var font = new Drawing.Font("Segoe UI", 20);
            graphics.FillRectangle(panel, 120, 90, _paint.ScreenWidth - 240, _paint.ScreenHeight - 260);
            graphics.DrawString("Axiom - Visual Studio Code", font, Drawing.Brushes.Gainsboro, 150, 110);
            graphics.DrawString("// some other application is in front; Paint is not open", font, Drawing.Brushes.Gray, 150, 190);
            return screen;
        }

        graphics.DrawImage(_paintImage, _paint.Left, _paint.Top, _paint.Width, _paint.Height);
        // The selected tool's highlight: as faint as the real one.
        foreach (SnapTarget target in _paint.Targets.Where(target => target.State is "on" or "selected"))
        {
            using var tint = new Drawing.SolidBrush(Drawing.Color.FromArgb(38, 0, 103, 192));
            graphics.FillRectangle(tint, target.X - target.W / 2, target.Y - target.H / 2, target.W, target.H);
        }
        graphics.DrawImage(_ink, 0, 0);
        return screen;
    }

    public ComputerUseCapture Capture()
    {
        using Drawing.Bitmap screen = Compose();
        var size = ComputerUseImageGeometry.GetSizeForDetailLevel(screen.Width, screen.Height, 0);
        using var model = new Drawing.Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Drawing.Graphics.FromImage(model))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(screen, 0, 0, model.Width, model.Height);
        }

        // The same yellow coordinate grid the app draws on every screenshot.
        typeof(ComputerUseScreenCapture)
            .GetMethod("OverlayCoordinateGuides", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [model]);

        using var stream = new MemoryStream();
        ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Drawing.Imaging.Encoder.Quality, ComputerUseImageGeometry.JpegQualityForDetailLevel(0));
        model.Save(stream, jpeg, parameters);
        byte[] bytes = stream.ToArray();
        LastImagePath = Path.Combine(_imageDir, $"screen-{++_captures:000}.jpg");
        File.WriteAllBytes(LastImagePath, bytes);

        var targets = Front == "Edge" ? _edge!.Targets()
            : Front != "Paint"
            ? []
            : _paint.Targets.Select(target => new ComputerUseUiTarget
            {
                Id = target.Id, Name = target.Name, Role = target.Role, State = target.State,
                ImageX = target.X, ImageY = target.Y, ImageWidth = target.W, ImageHeight = target.H
            }).ToList();
        var regions = Front != "Paint"
            ? []
            : _paint.Regions.Select(region => new ComputerUseUiRegion
            {
                Name = region.Name.Contains("Canvas", StringComparison.OrdinalIgnoreCase) ? $"Using {ActiveTool} tool on Canvas" : region.Name,
                Role = region.Role, Left = region.Left, Top = region.Top, Right = region.Right, Bottom = region.Bottom
            })
            .Select(region => ComputerUseImageGeometry.ClipToWindow(region, _paint.Left, _paint.Top, _paint.Left + _paint.Width, _paint.Top + _paint.Height))
            .Where(region => region != null).Select(region => region!).ToList();

        return new ComputerUseCapture
        {
            JpegBytes = bytes,
            ScreenX = 0, ScreenY = 0, ScreenWidth = screen.Width, ScreenHeight = screen.Height,
            ImageWidth = model.Width, ImageHeight = model.Height,
            TargetWindowHandle = new IntPtr(Front switch { "Paint" => 2, "Edge" => 3, _ => 1 }),
            ForegroundProcessName = Front switch { "Paint" => "mspaint", "Edge" => "msedge", _ => "Code" },
            ForegroundWindowTitle = Front switch { "Paint" => _paint.Title, "Edge" => _edge!.WindowTitle, _ => "Axiom - Visual Studio Code" },
            VisibleWindows = VisibleWindows(),
            BrowserState = Front == "Edge" ? _edge!.State() : null,
            UiTargets = ComputerUseImageGeometry.ScaleTargets(targets, screen.Width, screen.Height, model.Width, model.Height),
            Regions = ComputerUseImageGeometry.SelectRegions(regions, screen.Width, screen.Height, model.Width, model.Height)
        };
    }

    private int[] Pixels(Drawing.Rectangle area)
    {
        using Drawing.Bitmap screen = Compose();
        var pixels = new int[area.Width * area.Height];
        for (int y = 0; y < area.Height; y++)
            for (int x = 0; x < area.Width; x++)
                pixels[y * area.Width + x] = screen.GetPixel(area.Left + x, area.Top + y).ToArgb();
        return pixels;
    }

    private List<string> VisibleWindows()
    {
        var windows = new List<string>();
        void Add(string app) => windows.Add(app switch
        {
            "Paint" => "mspaint: " + _paint.Title,
            "Edge" => "msedge: " + _edge!.WindowTitle,
            _ => "Code: Axiom - Visual Studio Code"
        });
        Add(Front);
        if (Front != "Edge" && _edge != null) Add("Edge");
        if (Front != "Paint" && PaintOpen) Add("Paint");
        if (Front != "Code") Add("Code");
        return windows;
    }

    /// <summary>Carries out an action and reports it in the controller's own words.</summary>
    public string Execute(ComputerUseAction action, ComputerUseCapture capture, bool replaceAndCommitAddress = false)
    {
        if (Front == "Edge" && action.Type is ComputerUseActionType.Type or ComputerUseActionType.Key)
        {
            if (action.Type == ComputerUseActionType.Key)
            {
                _edge!.Key(action.Keys);
                return $"Pressed {action.Keys}.";
            }

            _edge!.Type(action.Text, replaceAndCommitAddress);
            return replaceAndCommitAddress && ComputerUseSessionController.TryLookLikeNavigationAddress(action.Text)
                ? $"Entered address ({action.Text.Length} character(s)) and pressed Enter."
                : $"Typed {action.Text.Length} character(s).";
        }

        switch (action.Type)
        {
            case ComputerUseActionType.Open:
            {
                string app = string.IsNullOrWhiteSpace(action.Text) ? action.Keys : action.Text;
                if (string.IsNullOrWhiteSpace(app))
                    return "[ACTION NOT SENT] Missing app name. Resend type=open with text set to the app.";
                if (app.Contains("paint", StringComparison.OrdinalIgnoreCase))
                {
                    PaintOpen = true;
                    Front = "Paint";
                }
                else if (app.Contains("edge", StringComparison.OrdinalIgnoreCase) || app.Contains("browser", StringComparison.OrdinalIgnoreCase))
                {
                    _edge ??= new SimBrowser(_paint.ScreenWidth, _paint.ScreenHeight);
                    Front = "Edge";
                }
                return $"Requested launch of \"{app}\" through Search; foreground verification is pending.";
            }
            case ComputerUseActionType.Click:
            case ComputerUseActionType.DoubleClick:
            case ComputerUseActionType.RightClick:
            case ComputerUseActionType.Move:
            {
                if (!ComputerUseSessionController.TryResolvePointerAim(action, capture, out var aim, out string refusal))
                    return refusal;
                if (ComputerUseSessionController.IsRedundantSelection(action, aim) && aim.Target!.Role is "Button" or "RadioButton" or "TabItem")
                {
                    return $"[ACTION NOT SENT] '{aim.Target!.Name}' ({aim.Target.Id}) is ALREADY selected — the application itself reports it. "
                        + "Clicking it again does nothing. Use it now: for a drawing tool, send a drag with strokes on the canvas.";
                }

                (int x, int y) = ComputerUseCoordinateMapper.MapToScreen(capture, aim.ImageX, aim.ImageY);
                if (action.Type == ComputerUseActionType.Move)
                    return $"Moved pointer to image ({aim.ImageX},{aim.ImageY}) / screen ({x},{y}).";
                if (Front == "Edge")
                {
                    _edge!.Click(x, y);
                    return aim.Target != null
                        ? $"[TARGET CLICKED] The click was delivered to the centre of the accessible control '{aim.Target.Name}' ({aim.Target.Id}) at image ({aim.ImageX},{aim.ImageY}). The control was hit; do not click it again to make sure. If its effect is not visible yet, continue with the next step.{aim.Note}"
                        : $"Clicked at image ({aim.ImageX},{aim.ImageY}) / screen ({x},{y}).{aim.Note}";
                }

                if (aim.Target != null && Front == "Paint")
                {
                    SelectTool(aim.Target.Name, aim.Target.Role);
                    return $"[TARGET CLICKED] The click was delivered to the centre of the accessible control '{aim.Target.Name}' ({aim.Target.Id}) at image ({aim.ImageX},{aim.ImageY}). The control was hit; do not click it again to make sure. If its effect is not visible yet, continue with the next step.{aim.Note}";
                }

                // A click on the canvas with a freehand tool leaves a dot, as it does in Paint.
                if (Front == "Paint" && _canvas.Contains(x, y) && FreehandTools.Contains(ActiveTool))
                    Draw([[(x, y), (x + 1, y)]]);
                return $"Clicked at image ({aim.ImageX},{aim.ImageY}) / screen ({x},{y}).{aim.Note}";
            }
            case ComputerUseActionType.Drag:
            {
                var paths = action.DragPaths
                    .Select(path => (IReadOnlyList<(int X, int Y)>)path
                        .Select(point => ComputerUseCoordinateMapper.MapToScreen(capture, point.X, point.Y))
                        .Select(point => (X: point.ScreenX, Y: point.ScreenY)).ToList())
                    .Where(path => path.Count >= 2 && path.Any(point => point != path[0]))
                    .ToList();
                if (paths.Count == 0)
                    return $"[ACTION NOT SENT] That drag starts and ends at the same point ({action.X}, {action.Y}), so it would draw nothing.";

                var area = ComputerUseStrokeGeometry.Bounds(paths, 14, 0, 0, capture.ScreenWidth, capture.ScreenHeight);
                var rectangle = new Drawing.Rectangle(area.Left, area.Top, area.Width, area.Height);
                int[] before = Pixels(rectangle);
                if (Front == "Paint")
                    Draw(paths);
                int[] after = Pixels(rectangle);
                int changed = ComputerUseObservationEvidence.CountChangedPixels(before, after);
                double length = paths.Sum(ComputerUseStrokeGeometry.Length);
                string what = paths.Count == 1
                    ? $"the drag from image ({action.X},{action.Y}) to ({action.X2},{action.Y2})"
                    : $"{paths.Count} strokes starting at image ({action.X},{action.Y})";
                if (ComputerUseObservationEvidence.IsDragEffectVisible(changed, length))
                {
                    return $"{ComputerUseSessionController.DragVerifiedTag} The controller sent {what} and measured {changed} changed pixels along the path, "
                        + "so it left a visible mark. These strokes are DONE — do not draw them again. Add only what is still missing, or finish.";
                }

                string surface = capture.Regions.Count > 0
                    ? "The application reports this work area: " + capture.Regions[0].Describe() + ". Keep every point inside it."
                    : "Check where the blank canvas really is in this screenshot and keep clear of toolbars and panels.";
                return $"{ComputerUseSessionController.DragNoEffectTag} The controller sent {what}, but nothing changed along the path ({Math.Max(0, changed)} pixels). "
                    + "Either the points are not on the drawing surface, or no drawing tool is active (select a brush once, using its target_id). "
                    + surface + " Fix that, then send the strokes again.";
            }
            case ComputerUseActionType.Type:
                return $"Typed {action.Text.Length} character(s).";
            case ComputerUseActionType.Key:
                return $"Pressed {action.Keys}.";
            case ComputerUseActionType.Wait:
                return "Waited 400 ms.";
            case ComputerUseActionType.Scroll:
                return $"Scrolled dx={action.Dx} dy={action.Dy}.";
            default:
                return "Screenshot taken.";
        }
    }

    private void SelectTool(string name, string role)
    {
        bool isTool = _paint.Targets.Any(target => target.Name == name && target.State is "on" or "off")
            || name == "Brushes";
        if (!isTool)
            return;
        foreach (SnapTarget target in _paint.Targets)
        {
            if (target.State is "on" or "off")
                target.State = target.Name == name && target.Role == role ? "on" : "off";
            if (target.Name == "Brushes")
                target.State = name == "Brushes" ? "selected" : "";
        }

        ActiveTool = name == "Brushes" ? "Brush" : name;
    }

    private void Draw(IReadOnlyList<IReadOnlyList<(int X, int Y)>> paths)
    {
        using var graphics = Drawing.Graphics.FromImage(_ink);
        graphics.SetClip(_canvas);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float width = ActiveTool switch { "Pencil" => 1f, "Eraser" => 0f, "Brush" => 5f, _ => 3f };
        if (width <= 0)
            return;
        using var pen = new Drawing.Pen(Drawing.Color.Black, width) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        foreach (var path in paths)
        {
            var first = path[0];
            var last = path[^1];
            var box = Drawing.Rectangle.FromLTRB(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), Math.Max(first.X, last.X), Math.Max(first.Y, last.Y));
            switch (ActiveTool)
            {
                case "Brush":
                case "Pencil":
                    graphics.DrawLines(pen, path.Select(point => new Drawing.Point(point.X, point.Y)).ToArray());
                    break;
                case "Oval":
                    if (box.Width > 1 && box.Height > 1) graphics.DrawEllipse(pen, box);
                    break;
                case "Rectangle":
                case "Rounded rectangle":
                    if (box.Width > 1 && box.Height > 1) graphics.DrawRectangle(pen, box);
                    break;
                case "Line":
                case "Curve":
                    graphics.DrawLine(pen, first.X, first.Y, last.X, last.Y);
                    break;
                // Fill, Text, Color picker, Magnifier and the rest do not draw on a drag.
            }
        }
    }

    public void Dispose()
    {
        _paintImage.Dispose();
        _ink.Dispose();
    }
}

internal static class ComputerUseSim
{
    public static async Task<int> RunAsync(string work, string[] extra)
    {
        string goal = extra.Length > 0 ? extra[0] : "Open the paint app and draw a person";
        string proto = Path.Combine(work, "proto");
        var actor = new RoleChannel(proto, "actor");
        var observer = new RoleChannel(proto, "observer");
        using var desktop = new SimDesktop(work);
        var log = new StringBuilder();
        void Log(string line) { log.AppendLine(line); File.WriteAllText(Path.Combine(work, "CU-REPORT.md"), log.ToString()); }
        CancellationToken token = CancellationToken.None;
        string? actorSystem = null, observerSystem = null;

        async Task<string> Ask(RoleChannel channel, string kind, string system, string payload, string? image, bool resendSystem)
        {
            var request = new StringBuilder();
            request.AppendLine($"=== REQUEST KIND: {kind} ===");
            request.AppendLine(resendSystem
                ? "=== SYSTEM PROMPT (exactly what Axiom sends the model) ===\n" + system
                : "=== SYSTEM PROMPT: identical to the one in your first request of this kind ===");
            request.AppendLine();
            request.AppendLine("=== USER PAYLOAD ===");
            request.AppendLine(payload);
            if (image != null)
                request.AppendLine($"\n(SCREENSHOT ATTACHED: open this image file before answering: {image})");
            request.AppendLine("\n=== YOUR TURN: reply with exactly the JSON object the system prompt asks for, nothing else. ===");
            return await channel.AskAsync(request.ToString(), token);
        }

        // ── What follows mirrors ComputerUseSessionController.RunCoreAsync step for step. Every
        //    decision is the app's own function; only the HUD, logging, inference recovery and
        //    Ask-mode permission prompts are left out. Keep it in step with RunCoreAsync.
        const ComputerUseMode mode = ComputerUseMode.Autopilot;
        int steps = 0;
        string summary = "Computer Use stopped.";
        bool completed = false;
        string? lastObservation = null;
        ComputerUseAction? lastExecuted = null;
        var recent = new List<(int Step, string Label, string Result, string Verdict)>();
        string lastActionResult = "";
        var lastVerdict = ComputerUseSessionController.StepVerdict.Confirmed;
        bool assessmentDue = false, taskVisuallyComplete = false;
        string? trackedOutcome = null;
        var taskProgress = new ComputerUseTaskProgress();
        ComputerUseTaskContract? contract = null;
        string? pendingLaunch = null;
        bool launchRecovery = false;
        int? pendingNewTabCount = null;
        bool separateTabRecovery = false;
        string? typedBrowserUrl = null;
        bool addressEntryExpected = false;
        string? pendingBrowserUrl = null;
        ComputerUseSessionController.PendingActionEvidence? pending = null;
        var ledger = new ComputerUseExecutionLedger();
        bool lastActionProducedEvidence = true, screenChanged = true;
        int repeatCount = 0, turnsWithoutInput = 0, actionsWithoutEffect = 0;
        int? lastKnownTabCount = null, tabCountBeforeAction = null;
        string? refusedOutcome = null;
        int outcomeRefusals = 0;
        const int RepeatAbortCount = 3;
        List<string> Recent() => recent.Select(action => $"{action.Step}. {action.Label} -> {action.Verdict}: {action.Result}").ToList();
        string Append(string? current, string addition) => string.IsNullOrWhiteSpace(current) ? addition : current + "\n" + addition;

        while (steps < ComputerUseSessionController.MaxSteps)
        {
            if (++turnsWithoutInput > 5)
            {
                summary = "Computer Use stopped without completing the task after five turns without desktop input. " + lastObservation;
                break;
            }

            steps++;
            ComputerUseCapture capture = desktop.Capture();
            if (contract == null)
            {
                var plan = await ComputerUsePlanning.CreateAsync(goal, "",
                    (system, payload, ct) => Ask(actor, "planner", system, payload, null, true), token,
                    (attempt, error, raw) => { Log($"PLAN REJECTED ({attempt}): {error}"); return Task.CompletedTask; });
                contract = plan.Contract;
                if (contract == null)
                {
                    summary = $"Computer Use could not start after {plan.Attempts} planning attempts: {plan.Error}";
                    break;
                }

                taskProgress = contract.Progress(taskProgress.Completed);
                Log($"PLAN accepted after {plan.Attempts} attempt(s){(string.IsNullOrWhiteSpace(plan.Warning) ? "" : " WARNING: " + plan.Warning)}: {plan.RawPlan.Replace("\n", " ")}\n{contract.Describe()}\n");
                continue;
            }

            if (capture.BrowserState?.HasTabTelemetry == true)
            {
                int observedTabCount = capture.BrowserState.TabCount;
                if (tabCountBeforeAction is int before && observedTabCount > before)
                {
                    lastObservation = Append(lastObservation, $"[BROWSER VERIFIED] A new tab is open (tab count went from {before} to {observedTabCount}). Do not open another.");
                    separateTabRecovery = false;
                    pendingNewTabCount = null;
                }

                lastKnownTabCount = observedTabCount;
                tabCountBeforeAction = null;
            }

            string applicationVerification = ComputerUseSessionController.ReconcileApplicationLaunch(capture, ref pendingLaunch, ref launchRecovery, ref taskProgress);
            if (applicationVerification.Length > 0)
                lastObservation = Append(lastObservation, applicationVerification);

            string verification = ComputerUseSessionController.ReconcileBrowserState(capture, ref pendingNewTabCount, ref pendingBrowserUrl,
                ref separateTabRecovery, ref taskProgress, contract);
            if (!string.IsNullOrWhiteSpace(verification))
            {
                lastObservation = Append(lastObservation, verification);
                Log($"Step {steps}: BROWSER: {verification.Replace("\n", " ")}");
            }

            ComputerUseTaskContract.Outcome? completedOutcome = contract.Current;
            string outcomeObservation = contract.ObserveCurrentOutcome(capture);
            if (!string.Equals(trackedOutcome, contract.Current?.Description, StringComparison.Ordinal))
            {
                trackedOutcome = contract.Current?.Description;
                recent.Clear();
                actionsWithoutEffect = 0;
            }

            if (!string.IsNullOrWhiteSpace(outcomeObservation))
            {
                if (outcomeObservation.StartsWith("[OUTCOME VERIFIED]", StringComparison.Ordinal))
                {
                    if (completedOutcome != null)
                    {
                        taskProgress = ComputerUseSessionController.AddVerifiedCompletion(taskProgress, completedOutcome.Description);
                        if (!string.IsNullOrWhiteSpace(completedOutcome.Destination))
                            taskProgress = ComputerUseSessionController.AddVerifiedCompletion(taskProgress, "Opened " + completedOutcome.Destination);
                    }

                    taskProgress = contract.Progress(taskProgress.Completed);
                    typedBrowserUrl = null;
                    pendingBrowserUrl = null;
                    addressEntryExpected = false;
                    Log($"Step {steps}: OUTCOME VERIFIED by controller: {completedOutcome?.Description}");
                }

                lastObservation = Append(lastObservation, outcomeObservation);
            }

            if (contract.Complete)
            {
                summary = "Computer Use finished every requested outcome: " + string.Join("; ", taskProgress.Completed);
                completed = true;
                break;
            }

            string actionEvidence = ComputerUseSessionController.ReconcileActionEvidence(capture, ref pending, ledger, ref screenChanged);
            lastActionProducedEvidence = screenChanged;
            if (actionEvidence.Length > 0)
                lastObservation = Append(lastObservation, actionEvidence);

            if (ledger.State != ComputerUseActionEvidenceState.None && assessmentDue)
            {
                assessmentDue = false;
                taskVisuallyComplete = false;
                string constraints = contract.Describe() + "\n" + applicationVerification
                    + (contract.Current?.Kind == "browser" ? "\n" + verification : "");
                string raw = await Ask(observer, "observer", ComputerUseVisualAssessment.SystemPrompt,
                    ComputerUseVisualAssessment.BuildPayload(goal, taskProgress, capture, ledger, constraints, lastActionResult, Recent()),
                    desktop.LastImagePath, observerSystem == null);
                observerSystem = "sent";
                var assessment = ComputerUseVisualAssessment.Parse(raw);
                bool unresolved = launchRecovery || separateTabRecovery
                    || (pendingNewTabCount.HasValue && contract.Current?.SeparateTab == true)
                    || !string.IsNullOrWhiteSpace(pendingBrowserUrl)
                    || capture.BrowserState?.IsErrorPage == true;
                if (actionEvidence.Length > 0)
                {
                    lastVerdict = ComputerUseSessionController.JudgeStep(lastActionResult, screenChanged, assessment.ActionSucceeded && !unresolved);
                    if (lastVerdict == ComputerUseSessionController.StepVerdict.Confirmed)
                        ledger.AssessExpectedState(true, ComputerUseSessionController.FirstNonEmpty(assessment.Evidence, lastActionResult));
                    else if (lastVerdict == ComputerUseSessionController.StepVerdict.Delivered)
                        ledger.MarkDelivered();
                    else
                        ledger.AssessExpectedState(false, assessment.Evidence);
                    if (recent.Count > 0)
                        recent[^1] = recent[^1] with { Verdict = ComputerUseSessionController.VerdictWord(lastVerdict) };
                    if (lastVerdict != ComputerUseSessionController.StepVerdict.NoEffect)
                        actionsWithoutEffect = 0;
                    else if (++actionsWithoutEffect >= 6)
                    {
                        summary = "Computer Use stopped after six actions in a row that changed nothing on screen. " + assessment.Evidence;
                        break;
                    }
                }

                lastActionProducedEvidence = lastVerdict != ComputerUseSessionController.StepVerdict.NoEffect;
                lastObservation = ComputerUseSessionController.DescribeStepVerdict(lastVerdict, ledger.ActionLabel)
                    + (string.IsNullOrWhiteSpace(lastActionResult) ? "" : "\n" + lastActionResult)
                    + (string.IsNullOrWhiteSpace(assessment.Evidence) ? "" : "\n[WHAT THE SCREENSHOT SHOWS] " + assessment.Evidence)
                    + (unresolved ? "\nController constraints remain unresolved; no completion accepted." : "");
                Log($"Step {steps}: VERDICT {lastVerdict} for '{ledger.ActionLabel}' (screenChanged={screenChanged}, observer action_succeeded={assessment.ActionSucceeded}, goal_completed={assessment.GoalCompleted}, unresolved={unresolved}); observer: {assessment.Evidence}");
                var required = contract.Current;
                if (!unresolved && string.IsNullOrWhiteSpace(typedBrowserUrl) && required != null)
                {
                    bool accepted = contract.TryComplete(capture, assessment, out string outcomeEvidence);
                    if (accepted)
                    {
                        refusedOutcome = null;
                        outcomeRefusals = 0;
                    }
                    else
                    {
                        if (!string.Equals(refusedOutcome, required.Description, StringComparison.Ordinal))
                        {
                            refusedOutcome = required.Description;
                            outcomeRefusals = 0;
                        }

                        if (++outcomeRefusals >= 4 && contract.CurrentDestinationReached)
                        {
                            outcomeEvidence += "\n[OUTCOME STALLED] The required destination is confirmed loaded, "
                                + $"but this outcome has not been accepted after {outcomeRefusals} checks. Do NOT navigate to it again. "
                                + "If it requires anything beyond arriving, do that now; otherwise return done with outcome=blocked and say what is missing.";
                            Log($"Step {steps}: OUTCOME STALLED ({outcomeRefusals} refusals)");
                        }
                    }

                    lastObservation += "\n" + (accepted || string.IsNullOrWhiteSpace(assessment.Evidence)
                        ? outcomeEvidence
                        : "[OUTCOME PROGRESS] The current outcome is not complete yet, which is expected while steps remain. Everything already done stays done — do not redo it. Do the next missing part now.");
                    if (accepted)
                    {
                        taskProgress = ComputerUseSessionController.AddVerifiedCompletion(taskProgress, required.Description);
                        if (required.Kind == "browser")
                            taskProgress = ComputerUseSessionController.AddVerifiedCompletion(taskProgress, "Opened " + required.Destination);
                        taskProgress = contract.Progress(taskProgress.Completed);
                        Log($"Step {steps}: OUTCOME ACCEPTED: {required.Description}");
                    }
                }

                taskVisuallyComplete = !unresolved && assessment.TaskCompleted && contract.Complete
                    && string.IsNullOrWhiteSpace(typedBrowserUrl)
                    && string.IsNullOrWhiteSpace(taskProgress.Current)
                    && string.IsNullOrWhiteSpace(taskProgress.Next) && taskProgress.Remaining.Count == 0;
            }

            string userPayload = ComputerUseSessionController.BuildUserPayload(goal, "", capture,
                contract.Describe() + "\n" + lastObservation, Recent(), taskProgress, ledger, steps, mode);
            string rawTurn = await Ask(actor, "actor", ComputerUseSessionController.BuildSystemPrompt(capture, mode), userPayload,
                desktop.LastImagePath, actorSystem == null);
            actorSystem = "sent";
            ComputerUseTurn turn = ComputerUseActionParser.Parse(rawTurn);
            if (!turn.Parsed)
            {
                lastObservation = "[COMPUTER USE PARSE ERROR]\nReturn only the required JSON object. " + turn.ParseError;
                Log($"Step {steps}: actor reply did not parse.");
                continue;
            }

            Log($"Step {steps}: ACTOR chose {turn.Action.ShortLabel}" + (turn.Action.TargetId.Length > 0 ? $" [target_id {turn.Action.TargetId}]" : "")
                + $" | thinking: {turn.Thinking}");
            taskProgress = ComputerUseSessionController.MergeTaskProgress(taskProgress, turn.Progress);
            if (!taskProgress.PlanInitialized && turn.Action.Type != ComputerUseActionType.Done)
            {
                lastObservation = "[PLAN REQUIRED] Enumerate the requested outcomes in task_progress.current, next, and remaining before acting.";
                continue;
            }

            ComputerUseSafetyVerdict safety = ComputerUseSafety.Evaluate(turn.Action, turn.Safety);
            string Blocked(string observation) { Log($"         BLOCKED: {observation.Replace("\n", " ")}"); return observation; }
            if (turn.Action.Type == ComputerUseActionType.Done)
            {
                if (string.Equals(turn.Action.Outcome, "blocked", StringComparison.OrdinalIgnoreCase))
                {
                    summary = "Computer Use could not complete the task: " + turn.Action.Summary;
                    break;
                }

                if (!contract.Complete && (!taskVisuallyComplete || !string.IsNullOrWhiteSpace(typedBrowserUrl) || separateTabRecovery))
                {
                    lastObservation = Blocked("[COMPLETION NOT VERIFIED] The full user request is not supported by fresh evidence. Continue the unfinished goal or return done with outcome=blocked and explain what remains.");
                    continue;
                }

                if (launchRecovery)
                {
                    lastObservation = Blocked("[APP LAUNCH VERIFICATION FAILED] The requested app is not the visible foreground window. Do not report success; retry type=open or use a visible launch recovery.");
                    continue;
                }

                if (!ledger.CanFinish)
                {
                    lastObservation = Blocked("[EXECUTION LEDGER BLOCK] The previous action is still " + ledger.State.ToString().ToLowerInvariant() + ". "
                        + "Capture and inspect its result or perform a recovery action before reporting completion.");
                    continue;
                }

                if (capture.BrowserState?.IsErrorPage == true)
                {
                    lastObservation = Blocked("[BROWSER VERIFICATION FAILED] The current browser page is an error page. Do not report success; recover the correct destination or finish as blocked.");
                    continue;
                }

                if (pendingNewTabCount.HasValue || !string.IsNullOrWhiteSpace(pendingBrowserUrl))
                {
                    lastObservation = Blocked("[BROWSER VERIFICATION PENDING] A requested tab or URL has not been verified in a fresh browser observation. Do not report success yet.");
                    continue;
                }

                if (!lastActionProducedEvidence)
                {
                    lastObservation = Blocked("[POST-ACTION VERIFICATION FAILED] The screenshot did not visibly change after the last action. Do not report completion; inspect the current screen and choose a recovery action.");
                    continue;
                }

                summary = string.IsNullOrWhiteSpace(turn.Action.Summary) ? "Computer Use finished." : turn.Action.Summary;
                completed = true;
                break;
            }

            if (turn.Action.Type == ComputerUseActionType.Screenshot)
            {
                lastObservation = "Screenshot captured. Choose the next GUI action.";
                assessmentDue = true;
                continue;
            }

            if (contract.BlocksAction(turn.Action, capture, out string contractBlock, addressEntryExpected))
            {
                lastObservation = Blocked(contractBlock);
                continue;
            }

            if (launchRecovery && ComputerUseSessionController.IsBlockedWhileRecoveringApplicationLaunch(turn.Action))
            {
                lastObservation = Blocked("[APP LAUNCH RECOVERY REQUIRED] The requested app has not appeared as the foreground window. Do not type text or send app-specific shortcuts to the desktop. "
                    + "Retry type=open with the requested app, or use a visible launch control.");
                continue;
            }

            if (separateTabRecovery && ComputerUseSessionController.IsBlockedWhileRecoveringSeparateTab(turn.Action, capture))
            {
                lastObservation = Blocked("[TAB RECOVERY REQUIRED] A requested separate browser tab was not observed. Do not focus an address bar, type a URL/query, press Enter, or navigate the existing tab. "
                    + "Retry Ctrl+T after browser focus is confirmed, or click only the accessible control explicitly named New tab using its target_id.");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(typedBrowserUrl) && ComputerUseSessionController.IsBlockedBeforeAddressCommit(turn.Action))
            {
                lastObservation = Blocked("[ADDRESS COMMIT REQUIRED] A complete URL was just typed into the address bar. Press Enter and inspect the fresh browser state before typing anything else. "
                    + "Do not append a query or another URL to the address field.");
                continue;
            }

            if (turn.Action.Type == ComputerUseActionType.Type
                && capture.BrowserState?.AddressHasFocus == true
                && ComputerUseTrustedNavigation.IsUnverifiedOwnedRepositoryNavigation(goal, "", turn.Action.Text))
            {
                lastObservation = Blocked("[OWNED RESOURCE EVIDENCE REQUIRED] This task names an owned GitHub repository, but its exact URL is not controller-verified. Do not type a guessed github.com owner/path. Finish this sub-goal as blocked and ask the user for the repository URL.");
                continue;
            }

            if (safety.Dangerous)
            {
                lastObservation = Blocked(ComputerUseSafety.BuildRefusalObservation(turn.Action, safety));
                continue;
            }

            if ((turn.Action.Type is ComputerUseActionType.Click or ComputerUseActionType.DoubleClick or ComputerUseActionType.RightClick)
                && ComputerUseCoordinateMapper.IsLikelyTaskbarClick(capture, turn.Action.X, turn.Action.Y))
            {
                lastObservation = Blocked("[COMPUTER USE TASKBAR BLOCK]\nTaskbar icon clicks are too imprecise and hit neighboring apps.\n"
                    + "Use type=open with text set to the exact app name, for example {\"type\":\"open\",\"text\":\"Microsoft Edge\"}.");
                continue;
            }

            if (lastExecuted != null && !screenChanged && ComputerUseSessionController.IsRepeatSensitiveAction(turn.Action)
                && ComputerUseCoordinateMapper.IsSameTarget(lastExecuted, turn.Action))
            {
                if (++repeatCount >= RepeatAbortCount)
                {
                    summary = "Computer Use stopped after repeating the same missed target. Use a keyboard shortcut on the next run if the click target is small.";
                    break;
                }

                lastObservation = Blocked("[COMPUTER USE REPEAT BLOCK]\nThe last pointer/type action used the same target and did not establish progress.\n"
                    + "Do not repeat it blindly; inspect the fresh screenshot and use a different target or recovery method.");
                continue;
            }

            bool isAddressTypeAction = turn.Action.Type == ComputerUseActionType.Type
                && (addressEntryExpected || capture.BrowserState?.AddressHasFocus == true);
            lastObservation = desktop.Execute(turn.Action, capture, isAddressTypeAction);
            bool autoCommittedAddress = isAddressTypeAction
                && !lastObservation.StartsWith("[ACTION NOT SENT]", StringComparison.Ordinal)
                && ComputerUseSessionController.TryLookLikeNavigationAddress(turn.Action.Text);
            Log($"         RESULT: {lastObservation}" + (desktop.Front == "Edge" ? $"  [sim: Edge at {desktop.BrowserUrl}]" : ""));
            if (lastObservation.StartsWith("[ACTION NOT SENT]", StringComparison.Ordinal))
                continue;

            lastActionResult = lastObservation;
            assessmentDue = true;
            bool needsEvidence = ComputerUseSessionController.RequiresVisualEvidence(turn.Action);
            if (needsEvidence)
                turnsWithoutInput = 0;
            ComputerUseSessionController.TrackApplicationLaunch(turn.Action, ref pendingLaunch);
            ComputerUseSessionController.TrackBrowserIntent(turn.Action, capture, ref pendingNewTabCount, ref typedBrowserUrl,
                ref pendingBrowserUrl, addressEntryExpected, lastKnownTabCount);
            if (autoCommittedAddress)
            {
                ComputerUseSessionController.TrackBrowserIntent(new ComputerUseAction { Type = ComputerUseActionType.Key, Keys = "Enter" },
                    capture, ref pendingNewTabCount, ref typedBrowserUrl, ref pendingBrowserUrl, addressEntryExpected, lastKnownTabCount);
                addressEntryExpected = false;
            }
            else
            {
                addressEntryExpected = ComputerUseSessionController.UpdateAddressEntryIntent(addressEntryExpected, turn.Action, capture);
            }

            tabCountBeforeAction = lastKnownTabCount;
            string expected = ComputerUseSessionController.GetImmediateExpectedState(turn.Action);
            pending = needsEvidence
                ? new ComputerUseSessionController.PendingActionEvidence
                {
                    BeforeScreenshot = capture.JpegBytes, ActionLabel = turn.Action.ShortLabel,
                    ExpectedState = expected, ControllerResult = lastActionResult
                }
                : null;
            if (needsEvidence)
                ledger.BeginAction(turn.Action, expected);
            lastExecuted = turn.Action;
            repeatCount = 0;
            string clipped = lastObservation.Replace('\n', ' ');
            recent.Add((steps, turn.Action.ShortLabel, clipped.Length <= 220 ? clipped : clipped[..220] + "…", needsEvidence ? "pending" : "done"));
            if (recent.Count > 10)
                recent.RemoveAt(0);
        }

        if (!completed && steps >= ComputerUseSessionController.MaxSteps)
            summary = "Computer Use reached the step limit.";
        desktop.Capture();
        Log($"\nSESSION ENDED — Completed:{completed}; steps:{steps}; {summary}\nFinal screen: {desktop.LastImagePath}\nActor model calls: {actor.Requests}; observer model calls: {observer.Requests}");
        string closing = $"SIMULATION FINISHED. Completed:{completed}. Stop now and report back.";
        actor.Finish(closing);
        observer.Finish(closing);
        Console.WriteLine(log.ToString());
        return completed ? 0 : 5;
    }
}
