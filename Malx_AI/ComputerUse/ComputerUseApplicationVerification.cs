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
