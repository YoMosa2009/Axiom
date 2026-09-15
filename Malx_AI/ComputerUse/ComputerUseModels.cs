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
        public string Text { get; init; } = "";
        public string Keys { get; init; } = "";
        public int Ms { get; init; }
        public string Button { get; init; } = "left";
        public string TargetId { get; init; } = "";
        public string ExpectedState { get; init; } = "";
        public string Summary { get; init; } = "";
        public string Explanation { get; init; } = "";
        public string Outcome { get; init; } = "";

        public string ShortLabel => Type switch
        {
            ComputerUseActionType.Move => $"Move to ({X}, {Y})",
            ComputerUseActionType.Click => $"Click {Button} at ({X}, {Y})",
            ComputerUseActionType.DoubleClick => $"Double-click at ({X}, {Y})",
            ComputerUseActionType.RightClick => $"Right-click at ({X}, {Y})",
            ComputerUseActionType.Drag => $"Drag from ({X}, {Y}) to ({X2}, {Y2})",
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

        public string Describe()
            => $"{Id}: {Role} \"{Name}\" center=({ImageX},{ImageY})" +
                (Value.Length > 0 ? $" value=\"{Value}\"" : "");
    }
}
