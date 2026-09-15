using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseNativeInput
    {
        private const int InputMouse = 0;
        private const int InputKeyboard = 1;
        private const uint MouseLeftDown = 0x0002;
        private const uint MouseLeftUp = 0x0004;
        private const uint MouseRightDown = 0x0008;
        private const uint MouseRightUp = 0x0010;
        private const uint MouseWheel = 0x0800;
        private const uint MouseHWheel = 0x1000;
        private const uint KeyExtended = 0x0001;
        private const uint KeyUp = 0x0002;
        private const uint KeyUnicode = 0x0004;
        private const int WheelDelta = 120;

        public static (int X, int Y) GetCursor()
        {
            return GetCursorPos(out POINT point) ? (point.X, point.Y) : (0, 0);
        }

        public static void MoveCursor(int screenX, int screenY)
        {
            SetCursorPos(screenX, screenY);
        }

        /// <summary>
        /// Brings a window to the foreground, reporting whether it got there.
        /// </summary>
        /// <remarks>
        /// SetForegroundWindow is a request the shell may satisfy a moment later, so reading the
        /// foreground on the very next line reports a failure that has not happened yet. A window
        /// that is ALREADY frontmost needs no work at all — checking first avoids refusing an
        /// action over a no-op.
        /// </remarks>
        public static bool ActivateWindow(IntPtr windowHandle)
        {
            if (windowHandle == IntPtr.Zero)
                return false;
            if (GetForegroundWindow() == windowHandle)
                return true;

            // A minimised window cannot be brought forward without being restored first.
            if (IsIconic(windowHandle))
                ShowWindow(windowHandle, ShowWindowRestore);

            // Windows refuses SetForegroundWindow outright unless the calling process already owns
            // the foreground — a lock that exists to stop applications stealing focus. Sharing an
            // input queue with the current foreground thread and the target's thread is the
            // documented way to be allowed to do it deliberately, and without this the call simply
            // fails and every action is refused with "could not be activated".
            IntPtr foreground = GetForegroundWindow();
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
            uint targetThread = GetWindowThreadProcessId(windowHandle, out _);

            bool attachedForeground = foregroundThread != 0
                && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            bool attachedTarget = targetThread != 0
                && targetThread != currentThread
                && targetThread != foregroundThread
                && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                BringWindowToTop(windowHandle);
                SetForegroundWindow(windowHandle);

                // Activation is granted asynchronously, so the answer is not ready on the next line.
                for (int attempt = 0; attempt < 8; attempt++)
                {
                    if (GetForegroundWindow() == windowHandle)
                        return true;
                    Thread.Sleep(25);
                }
            }
            finally
            {
                // Input queues must always be detached again; leaving them joined would tie this
                // application's input handling to another process's.
                if (attachedTarget)
                    AttachThreadInput(currentThread, targetThread, false);
                if (attachedForeground)
                    AttachThreadInput(currentThread, foregroundThread, false);
            }

            return GetForegroundWindow() == windowHandle;
        }

        public static async Task SmoothMoveAsync(int fromX, int fromY, int toX, int toY, Action<int, int>? onStep, CancellationToken token)
        {
            int distance = Math.Max(Math.Abs(toX - fromX), Math.Abs(toY - fromY));
            int steps = Math.Clamp(distance / 18, 6, 28);
            for (int i = 1; i <= steps; i++)
            {
                token.ThrowIfCancellationRequested();
                double t = i / (double)steps;
                double eased = 1 - Math.Pow(1 - t, 3);
                int x = fromX + (int)Math.Round((toX - fromX) * eased);
                int y = fromY + (int)Math.Round((toY - fromY) * eased);
                MoveCursor(x, y);
                onStep?.Invoke(x, y);
                await Task.Delay(12, token).ConfigureAwait(false);
            }

            MoveCursor(toX, toY);
            onStep?.Invoke(toX, toY);
        }

        public static void LeftClick()
        {
            Send(Mouse(MouseLeftDown), Mouse(MouseLeftUp));
        }

        /// <summary>
        /// Presses and holds the left button, moves through to the end point, and releases.
        /// </summary>
        /// <remarks>
        /// A click is atomic: down and up at one point. Everything that is done by holding the
        /// button and moving — drawing a stroke, dragging a shape out to size, rubber-band
        /// selecting, moving a window by its title bar, dragging a slider — is impossible without
        /// this, and no combination of clicks substitutes for it.
        ///
        /// The intermediate moves are the point. An application draws what the cursor passes
        /// through while the button is down; jumping straight from start to end yields a single
        /// dot or a straight line where a stroke was intended.
        /// </remarks>
        public static async Task DragAsync(
            int fromX,
            int fromY,
            int toX,
            int toY,
            Action<int, int>? onStep,
            CancellationToken token)
        {
            MoveCursor(fromX, fromY);
            onStep?.Invoke(fromX, fromY);
            // Applications latch the button-down position on the message; give the move ahead of it
            // time to be processed first, or the stroke starts from wherever the cursor was before.
            await Task.Delay(60, token).ConfigureAwait(false);

            Send(Mouse(MouseLeftDown));
            try
            {
                await Task.Delay(40, token).ConfigureAwait(false);
                await SmoothMoveAsync(fromX, fromY, toX, toY, onStep, token).ConfigureAwait(false);
                await Task.Delay(40, token).ConfigureAwait(false);
            }
            finally
            {
                // The button must come up even if the session is cancelled mid-drag; leaving it
                // held would hand the user a desktop with a stuck mouse button.
                Send(Mouse(MouseLeftUp));
            }
        }

        public static void LeftClickAt(int screenX, int screenY)
        {
            MoveCursor(screenX, screenY);
            Thread.Sleep(20);
            LeftClick();
        }

        public static void DoubleClick()
        {
            LeftClick();
            Thread.Sleep(70);
            LeftClick();
        }

        public static void DoubleClickAt(int screenX, int screenY)
        {
            MoveCursor(screenX, screenY);
            Thread.Sleep(20);
            DoubleClick();
        }

        public static void RightClick()
        {
            Send(Mouse(MouseRightDown), Mouse(MouseRightUp));
        }

        public static void RightClickAt(int screenX, int screenY)
        {
            MoveCursor(screenX, screenY);
            Thread.Sleep(20);
            RightClick();
        }

        public static void Zoom(int steps)
        {
            int signed = steps == 0 ? 1 : Math.Clamp(steps, -6, 6);
            Send(Keyboard(0x11, 0));
            try
            {
                Send(Mouse(MouseWheel, 0, 0, signed * WheelDelta));
            }
            finally
            {
                Send(Keyboard(0x11, KeyUp));
            }
        }

        public static void Scroll(int dx, int dy)
        {
            if (dy != 0)
                Send(Mouse(MouseWheel, 0, 0, dy * WheelDelta));
            if (dx != 0)
                Send(Mouse(MouseHWheel, 0, 0, dx * WheelDelta));
        }

        public static async Task TypeTextAsync(string text, CancellationToken token)
        {
            if (string.IsNullOrEmpty(text))
                return;

            foreach (char character in text)
            {
                token.ThrowIfCancellationRequested();
                if (character is '\n' or '\r')
                    PressVirtualKey(0x0D);
                else if (character == '\t')
                    PressVirtualKey(0x09);
                else
                    Send(KeyboardUnicode(character, 0), KeyboardUnicode(character, KeyUp));

                await Task.Delay(12, token).ConfigureAwait(false);
            }
        }

        public static async Task OpenAppAsync(string appName, CancellationToken token)
        {
            string query = (appName ?? string.Empty).Trim();
            if (query.Length == 0)
                return;

            if (TryLaunchRegisteredApplication(query))
            {
                await Task.Delay(800, token).ConfigureAwait(false);
                return;
            }

            PressCombo("Win+S");
            await Task.Delay(450, token).ConfigureAwait(false);
            await TypeTextAsync(query, token).ConfigureAwait(false);
            await Task.Delay(700, token).ConfigureAwait(false);
            PressCombo("Enter");
            await Task.Delay(250, token).ConfigureAwait(false);
        }

        private static bool TryLaunchRegisteredApplication(string requestedApp)
        {
            string[] requestedTokens = Tokenize(requestedApp).ToArray();
            if (requestedTokens.Length == 0)
                return false;

            string? bestExecutable = null;
            int bestScore = 0;
            foreach (RegistryKey root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using RegistryKey? appPaths = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths");
                    if (appPaths == null)
                        continue;

                    foreach (string subKeyName in appPaths.GetSubKeyNames())
                    {
                        string candidate = System.IO.Path.GetFileNameWithoutExtension(subKeyName);
                        int score = ScoreApplicationName(candidate, requestedTokens);
                        if (score <= bestScore)
                            continue;

                        bestExecutable = subKeyName;
                        bestScore = score;
                    }
                }
                catch
                {
                    // The search fallback below remains available if this registry view is unreadable.
                }
            }

            if (string.IsNullOrWhiteSpace(bestExecutable))
                return false;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = bestExecutable,
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static int ScoreApplicationName(string candidate, IReadOnlyList<string> requestedTokens)
        {
            string normalizedCandidate = Normalize(candidate);
            int score = 0;
            foreach (string token in requestedTokens)
            {
                string normalizedToken = Normalize(token);
                if (normalizedToken.Length < 3 || normalizedToken is "microsoft" or "windows" or "app" or "application")
                    continue;

                if (normalizedCandidate.Contains(normalizedToken, StringComparison.OrdinalIgnoreCase))
                    score += normalizedToken.Length;
            }

            return score;
        }

        private static IEnumerable<string> Tokenize(string value)
            => (value ?? string.Empty).Split([' ', '\t', '\r', '\n', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string Normalize(string value)
            => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

        public static void PressCombo(string keys)
        {
            List<ushort> parsed = ParseCombo(keys);
            if (parsed.Count == 0)
                return;

            var inputs = new List<INPUT>(parsed.Count * 2);
            foreach (ushort key in parsed)
                inputs.Add(Keyboard(key, 0));
            for (int i = parsed.Count - 1; i >= 0; i--)
                inputs.Add(Keyboard(parsed[i], KeyUp));
            Send(inputs.ToArray());
        }

        public static List<ushort> ParseCombo(string? keys)
        {
            var result = new List<ushort>();
            if (string.IsNullOrWhiteSpace(keys))
                return result;

            foreach (string part in keys.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (TryMapKey(part, out ushort mapped) && !result.Contains(mapped))
                    result.Add(mapped);
            }

            return result;
        }

        private static bool TryMapKey(string token, out ushort key)
        {
            string normalized = token.Trim().ToLowerInvariant();
            key = normalized switch
            {
                "ctrl" or "control" => 0x11,
                "alt" or "option" => 0x12,
                "shift" => 0x10,
                "win" or "meta" or "cmd" or "super" => 0x5B,
                "enter" or "return" => 0x0D,
                "esc" or "escape" => 0x1B,
                "tab" => 0x09,
                "space" or "spacebar" => 0x20,
                "backspace" or "bksp" => 0x08,
                "delete" or "del" => 0x2E,
                "home" => 0x24,
                "end" => 0x23,
                "pageup" or "pgup" => 0x21,
                "pagedown" or "pgdn" => 0x22,
                "up" => 0x26,
                "down" => 0x28,
                "left" => 0x25,
                "right" => 0x27,
                "f1" => 0x70,
                "f2" => 0x71,
                "f3" => 0x72,
                "f4" => 0x73,
                "f5" => 0x74,
                "f6" => 0x75,
                "f7" => 0x76,
                "f8" => 0x77,
                "f9" => 0x78,
                "f10" => 0x79,
                "f11" => 0x7A,
                "f12" => 0x7B,
                _ => 0
            };

            if (key != 0)
                return true;
            if (normalized.Length != 1)
                return false;

            char c = char.ToUpperInvariant(normalized[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                key = c;
                return true;
            }

            return false;
        }

        private static void PressVirtualKey(ushort key)
        {
            Send(Keyboard(key, 0), Keyboard(key, KeyUp));
        }

        private static INPUT Mouse(uint flags, int x = 0, int y = 0, int data = 0)
        {
            return new INPUT
            {
                type = InputMouse,
                union = new InputUnion
                {
                    mouse = new MOUSEINPUT
                    {
                        dx = x,
                        dy = y,
                        mouseData = data,
                        dwFlags = flags
                    }
                }
            };
        }

        private static INPUT Keyboard(ushort vk, uint flags)
        {
            bool extended = vk is 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x24 or 0x23 or 0x21 or 0x22 or 0x5B or 0x5C;
            return new INPUT
            {
                type = InputKeyboard,
                union = new InputUnion
                {
                    keyboard = new KEYBDINPUT
                    {
                        wVk = vk,
                        wScan = 0,
                        dwFlags = flags | (extended ? KeyExtended : 0)
                    }
                }
            };
        }

        private static INPUT KeyboardUnicode(char character, uint extraFlags)
        {
            return new INPUT
            {
                type = InputKeyboard,
                union = new InputUnion
                {
                    keyboard = new KEYBDINPUT
                    {
                        wVk = 0,
                        wScan = character,
                        dwFlags = KeyUnicode | extraFlags
                    }
                }
            };
        }

        private static void Send(params INPUT[] inputs)
        {
            if (inputs.Length == 0)
                return;
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool SetCursorPos(int X, int Y);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private const int ShowWindowRestore = 9;

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public int type;
            public InputUnion union;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mouse;
            [FieldOffset(0)] public KEYBDINPUT keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public int mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }
    }
}
