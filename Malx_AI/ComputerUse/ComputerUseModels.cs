using System;

namespace Malx_AI.ComputerUse
{
    public enum ComputerUseMode
    {
        Ask,
        Autopilot
    }

    public enum ComputerUseActionType
    {
        Screenshot,
        Move,
        Click,
        DoubleClick,
        RightClick,
        /// <summary>Press at one point, move to another with the button held, release.</summary>
        Drag,
        Scroll,
        Type,
        Key,
        Wait,
        Zoom,
        Open,
        Done
    }

    public sealed class ComputerUseAction
    {
        public ComputerUseActionType Type { get; init; } = ComputerUseActionType.Screenshot;
        public int X { get; init; }
        public int Y { get; init; }
        public int Dx { get; init; }
        public int Dy { get; init; }
        /// <summary>End point of a drag, in screenshot pixels. Only meaningful for Drag.</summary>
        public int X2 { get; init; }
        public int Y2 { get; init; }

        /// <summary>
        /// Several held-button paths drawn by one action, in screenshot pixels. Empty for an
        /// ordinary two-point drag. A figure is many strokes; sending them together means a
        /// drawing takes one turn instead of one turn per line.
        /// </summary>
        public IReadOnlyList<IReadOnlyList<(int X, int Y)>> Strokes { get; init; } = Array.Empty<IReadOnlyList<(int X, int Y)>>();

        /// <summary>Every path this drag follows: its strokes, or its single start-to-end line.</summary>
        public IReadOnlyList<IReadOnlyList<(int X, int Y)>> DragPaths
            => Strokes.Count > 0 ? Strokes : [[(X, Y), (X2, Y2)]];
        public string Text { get; init; } = "";
        public string Keys { get; init; } = "";
        public int Ms { get; init; }
        public string Button { get; init; } = "left";
        public string TargetId { get; init; } = "";
        public string ExpectedState { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Explanation { get; init; } = "";
        public string Outcome { get; init; } = "";

        private string Where => !string.IsNullOrWhiteSpace(TargetId) && X == 0 && Y == 0
            ? $"on target {TargetId.Trim()}"
            : !string.IsNullOrWhiteSpace(TargetId) ? $"on target {TargetId.Trim()} at ({X}, {Y})" : $"at ({X}, {Y})";

        public string ShortLabel => Type switch
        {
            ComputerUseActionType.Move => $"Move to ({X}, {Y})",
            // A click aimed by target_id usually carries no coordinates. Labelled "at (0, 0)" it
            // read, in the log and in the observer's payload, like a click in the screen corner.
            ComputerUseActionType.Click => $"Click {Button} {Where}",
            ComputerUseActionType.DoubleClick => $"Double-click {Where}",
            ComputerUseActionType.RightClick => $"Right-click {Where}",
            ComputerUseActionType.Drag => Strokes.Count > 1
                ? $"Draw {Strokes.Count} strokes starting at ({X}, {Y})"
                : $"Drag from ({X}, {Y}) to ({X2}, {Y2})",
            ComputerUseActionType.Scroll => $"Scroll dx={Dx} dy={Dy}",
            ComputerUseActionType.Type => Text.Length == 0 ? "Type" : $"Type \"{TrimForUi(Text, 48)}\"",
            ComputerUseActionType.Key => $"Key {Keys}",
            ComputerUseActionType.Wait => $"Wait {Math.Max(0, Ms)} ms",
            ComputerUseActionType.Zoom => Dy < 0 || Dx < 0 ? "Zoom out" : "Zoom in",
            ComputerUseActionType.Open => string.IsNullOrWhiteSpace(Text) ? "Open app" : $"Open \"{TrimForUi(Text, 48)}\"",
            ComputerUseActionType.Done => string.IsNullOrWhiteSpace(Summary) ? "Done" : $"Done: {TrimForUi(Summary, 72)}",
            _ => "Screenshot"
        };

        private static string TrimForUi(string text, int max)
        {
            string compact = (text ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            return compact.Length <= max ? compact : compact[..max] + "...";
        }
    }

    public sealed class ComputerUseSafetyVerdict
    {
        public bool Ok { get; init; }
        public bool Dangerous { get; init; }
        public string Risk { get; init; } = "unknown";
        public string Causes { get; init; } = "";
        public string Effects { get; init; } = "";
        public string Reason { get; init; } = "";
        public bool RequiresAsk { get; init; }
    }

    public sealed class ComputerUseTurn
    {
        public string Thinking { get; init; } = "";
        public ComputerUseTaskProgress Progress { get; init; } = new();
        public ComputerUseSafetyVerdict Safety { get; init; } = new();
        public ComputerUseAction Action { get; init; } = new();
        public string RawText { get; init; } = "";
        public bool Parsed { get; init; }
        public string ParseError { get; init; } = "";
    }

    public sealed class ComputerUseTaskProgress
    {
        public IReadOnlyList<string> Completed { get; init; } = Array.Empty<string>();
        public string Current { get; init; } = "";
        public string Next { get; init; } = "";
        public IReadOnlyList<string> Remaining { get; init; } = Array.Empty<string>();
        public bool PlanInitialized { get; init; }
        public bool HasUpdate => Completed.Count > 0 || !string.IsNullOrWhiteSpace(Current) || !string.IsNullOrWhiteSpace(Next);
    }

    public sealed class ComputerUseCapabilityResult
    {
        public bool CanRun { get; init; }
        public bool CatalogVision { get; init; }
        public bool HeuristicVision { get; init; }
        public bool LocalProjectorAvailable { get; init; }
        public string ExecutionSurface { get; init; } = "";
        public string ActingModelLabel { get; init; } = "";
        public string Reason { get; init; } = "";
    }

    public sealed class ComputerUseCapture
    {
        public required byte[] JpegBytes { get; init; }
        public int ScreenX { get; init; }
        public int ScreenY { get; init; }
        public int ScreenWidth { get; init; }
        public int ScreenHeight { get; init; }
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }
        public string MimeType { get; init; } = "image/jpeg";
        public IntPtr TargetWindowHandle { get; init; }
        public string ForegroundWindowTitle { get; init; } = "";

        /// <summary>
        /// Every visible top-level window, as "process: title".
        /// </summary>
        /// <remarks>
        /// Whether an application is OPEN and whether it currently holds focus are different
        /// questions, and only the first one answers "is Paint on screen". Focus is in flux at the
        /// moment of capture — the session panel is hidden right before the screenshot, and Windows
        /// hands focus to the desktop shell while it settles — so judging presence by the
        /// foreground window reports the shell and contradicts the screenshot.
        /// </remarks>
        public IReadOnlyList<string> VisibleWindows { get; init; } = Array.Empty<string>();
        public string ForegroundProcessName { get; init; } = "";
        public IReadOnlyList<ComputerUseUiTarget> UiTargets { get; init; } = Array.Empty<ComputerUseUiTarget>();

        /// <summary>
        /// Large named areas of the window (a canvas, a document, an editor), in screenshot pixels.
        /// </summary>
        /// <remarks>
        /// Not clickable controls, so they never appeared among the targets, and the model had to
        /// guess from pixels where a canvas began and ended. Applications say so themselves:
        /// Paint exposes its canvas as "Using Brush tool on Canvas", which gives both the exact
        /// drawing rectangle and the tool that is currently active.
        /// </remarks>
        public IReadOnlyList<ComputerUseUiRegion> Regions { get; init; } = Array.Empty<ComputerUseUiRegion>();
        public ComputerUseBrowserState? BrowserState { get; init; }

        /// <summary>
        /// Index into the screenshot detail ladder this capture was encoded at. 0 is full detail;
        /// higher values are the cheaper payloads used after a provider interrupted a turn.
        /// </summary>
        public int DetailLevel { get; init; }

        public (int ScreenX, int ScreenY) MapImagePointToScreen(int imageX, int imageY)
        {
            if (ImageWidth <= 0 || ImageHeight <= 0)
                return (ScreenX, ScreenY);

            return ComputerUseCoordinateMapper.MapToScreen(this, imageX, imageY);
        }
    }

    /// <summary>
    /// Small, accessibility-derived browser observation captured alongside the screenshot.
    /// It is evidence for navigation/tab verification, not an alternative input surface.
    /// </summary>
    public sealed class ComputerUseBrowserState
    {
        public string WindowTitle { get; init; } = "";
        public string Address { get; init; } = "";
        public string DocumentAddress { get; init; } = "";
        public bool AddressHasFocus { get; init; }
        public string ActiveTabId { get; init; } = "";
        public IReadOnlyList<string> TabIds { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> TabTitles { get; init; } = Array.Empty<string>();
        public bool HasTabTelemetry { get; init; }
        public bool IsErrorPage { get; init; }

        public bool IsAvailable => !string.IsNullOrWhiteSpace(Address) || TabTitles.Count > 0;
        public int TabCount => TabTitles.Count;
    }

    /// <summary>A large named area of the foreground window, in screenshot pixels.</summary>
    public sealed class ComputerUseUiRegion
    {
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public int Left { get; init; }
        public int Top { get; init; }
        public int Right { get; init; }
        public int Bottom { get; init; }

        public bool Contains(int imageX, int imageY)
            => imageX >= Left && imageX <= Right && imageY >= Top && imageY <= Bottom;

        public string Describe()
            => $"{Role} \"{Name}\" spans x {Left}..{Right}, y {Top}..{Bottom}";
    }

    /// <summary>
    /// A screen-relative, point-in-time UI Automation target. The model may choose one of these
    /// instead of estimating a pixel from the screenshot.
    /// </summary>
    public sealed class ComputerUseUiTarget
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Role { get; init; } = "";
        public string Value { get; init; } = "";
        public int ImageX { get; init; }
        public int ImageY { get; init; }

        /// <summary>Size of the control in screenshot pixels, or 0 when unknown.</summary>
        public int ImageWidth { get; init; }
        public int ImageHeight { get; init; }

        /// <summary>
        /// "selected", "on" or "off" when the control reports it through accessibility, else empty.
        /// </summary>
        /// <remarks>
        /// This is the controller's own reading of the control, not a guess from pixels. A tool
        /// button's selected highlight is a faint tint that does not survive a downscaled JPEG,
        /// and an observer that could not see it told the model its click had failed.
        /// </remarks>
        public string State { get; init; } = "";

        public bool IsSelected => State is "selected" or "on";

        /// <summary>Whether a screenshot point falls on this control (with a small margin).</summary>
        public bool Contains(int imageX, int imageY, int margin = 3)
        {
            int halfWidth = Math.Max(ImageWidth / 2, 6) + margin;
            int halfHeight = Math.Max(ImageHeight / 2, 6) + margin;
            return Math.Abs(imageX - ImageX) <= halfWidth && Math.Abs(imageY - ImageY) <= halfHeight;
        }

        public string Describe()
            => $"{Id}: {Role} \"{Name}\" center=({ImageX},{ImageY})" +
                (State.Length > 0 ? $" state={State}" : "") +
                (Value.Length > 0 ? $" value=\"{Value}\"" : "");
    }
}
