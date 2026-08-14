using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Malx_AI
{
    internal static class UpdateProcessGuard
    {
        private static readonly long StartTimeToleranceTicks = TimeSpan.FromSeconds(2).Ticks;

        internal static bool MatchesExpectedProcess(
            string expectedExecutablePath,
            string actualExecutablePath,
            long? expectedStartTimeUtcTicks,
            long actualStartTimeUtcTicks)
        {
            if (string.IsNullOrWhiteSpace(expectedExecutablePath)
                || string.IsNullOrWhiteSpace(actualExecutablePath))
            {
                return false;
            }

            string expected = Path.GetFullPath(expectedExecutablePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string actual = Path.GetFullPath(actualExecutablePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                return false;

            return !expectedStartTimeUtcTicks.HasValue
                || Math.Abs(actualStartTimeUtcTicks - expectedStartTimeUtcTicks.Value) <= StartTimeToleranceTicks;
        }

        internal static void WaitForExitOrTerminate(
            Process process,
            string expectedExecutablePath,
            long? expectedStartTimeUtcTicks,
            TimeSpan gracefulTimeout,
            TimeSpan forcedTimeout,
            Action<string>? log = null)
        {
            ArgumentNullException.ThrowIfNull(process);
            if (process.HasExited)
                return;

            string actualExecutablePath;
            long actualStartTimeUtcTicks;
            try
            {
                actualExecutablePath = process.MainModule?.FileName ?? string.Empty;
                actualStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                throw new InvalidOperationException(
                    "Axiom could not verify the process that must close before updating, so no process was terminated.",
                    ex);
            }

            if (!MatchesExpectedProcess(
                    expectedExecutablePath,
                    actualExecutablePath,
                    expectedStartTimeUtcTicks,
                    actualStartTimeUtcTicks))
            {
                throw new InvalidOperationException(
                    "The process waiting to close no longer matches the installed Axiom executable. The update was cancelled safely.");
            }

            if (process.WaitForExit((int)gracefulTimeout.TotalMilliseconds))
                return;

            log?.Invoke(
                $"Axiom process {process.Id} did not exit after {gracefulTimeout.TotalSeconds:F0} seconds; " +
                "terminating the verified installed process so the requested update can continue.");
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (!process.WaitForExit((int)forcedTimeout.TotalMilliseconds))
            {
                throw new TimeoutException(
                    "The verified Axiom process could not be stopped, so the update was cancelled without changing the installation.");
            }
        }
    }
}
