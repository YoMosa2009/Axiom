using System;

namespace Malx_AI
{
    /// <summary>
    /// How long to wait before the next automatic update check. A single check at launch is not
    /// enough: it competes with startup work and can time out, and a release published while
    /// Axiom stays open would otherwise never be seen.
    /// </summary>
    internal static class UpdateCheckSchedule
    {
        /// <summary>Wait after a successful check.</summary>
        internal static readonly TimeSpan RegularInterval = TimeSpan.FromHours(2);

        private static readonly TimeSpan[] RetryDelays =
        [
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(30)
        ];

        /// <param name="consecutiveFailures">Failed checks since the last successful one; 0 after a success.</param>
        internal static TimeSpan NextDelay(int consecutiveFailures)
            => consecutiveFailures <= 0
                ? RegularInterval
                : RetryDelays[Math.Min(consecutiveFailures, RetryDelays.Length) - 1];
    }
}
