using System;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseApplicationVerification
    {
        private static readonly HashSet<string> IgnoredWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "microsoft", "windows", "app", "application", "the"
        };

        public static bool IsRequestedApplicationVisible(string? requestedApp, ComputerUseCapture? capture)
        {
            if (capture == null || string.IsNullOrWhiteSpace(requestedApp))
                return false;

            // The foreground window first — it is the strongest signal when it is available — then
            // every other visible window. An application the user can see is open whether or not it
            // happens to hold focus at the instant of the screenshot.
            var candidates = new List<string>
            {
                capture.ForegroundProcessName + " " + capture.ForegroundWindowTitle
            };
            candidates.AddRange(capture.VisibleWindows);

            return candidates.Any(candidate => Matches(requestedApp, candidate));
        }

        private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer", "shellexperiencehost", "startmenuexperiencehost", "searchhost", "searchapp", "lockapp", "unknown"
        };

        /// <summary>
        /// Whether the requested application is where the user is: in front, or on screen while
        /// focus is only passing through the Windows shell.
        /// </summary>
        /// <remarks>
        /// "Open X" / "take me to X" means X ends up in front. Accepting X merely being open
        /// somewhere behind another app finished "take me to Microsoft Edge" in one second with
        /// Edge still in the background and another application in front. Focus that lands on
        /// the shell (the session panel hides just before capture) is the one case where "on
        /// screen" has to stand in for "in front".
        /// </remarks>
        public static bool IsRequestedApplicationInFront(string? requestedApp, ComputerUseCapture? capture)
        {
            if (capture == null || string.IsNullOrWhiteSpace(requestedApp))
                return false;

            if (Matches(requestedApp, capture.ForegroundProcessName + " " + capture.ForegroundWindowTitle))
                return true;

            return IsShellForeground(capture) && IsRequestedApplicationVisible(requestedApp, capture);
        }

        private static bool IsShellForeground(ComputerUseCapture capture)
        {
            string process = (capture.ForegroundProcessName ?? string.Empty).Trim();
            if (process.Length == 0 || !ShellProcesses.Contains(process))
                return false;

            // Explorer is also File Explorer; only its desktop/taskbar windows are the shell.
            string title = (capture.ForegroundWindowTitle ?? string.Empty).Trim();
            return !string.Equals(process, "explorer", StringComparison.OrdinalIgnoreCase)
                || title.Length == 0
                || string.Equals(title, "Program Manager", StringComparison.OrdinalIgnoreCase)
                || string.Equals(title, "unknown", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Whether one window's identity names the requested application.</summary>
        public static bool Matches(string requestedApp, string? windowIdentity)
        {
            string candidate = Normalize(windowIdentity);
            if (candidate.Length == 0)
                return false;

            string requested = Normalize(requestedApp);
            if (requested.Length > 0 && candidate.Contains(requested, StringComparison.OrdinalIgnoreCase))
                return true;

            string[] meaningfulTokens = Tokenize(requestedApp)
                .Where(token => token.Length >= 3 && !IgnoredWords.Contains(token))
                .ToArray();
            return meaningfulTokens.Length > 0
                && meaningfulTokens.Any(token => candidate.Contains(Normalize(token), StringComparison.OrdinalIgnoreCase));
        }

        private static IEnumerable<string> Tokenize(string value)
            => (value ?? string.Empty).Split([' ', '\t', '\r', '\n', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string Normalize(string? value)
            => new string((value ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }
}
