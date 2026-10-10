using System;
using System.Threading;

namespace Malx_AI
{
    /// <summary>
    /// One view's speed readout: the reply in progress, or the last finished one. Streams report
    /// into it from background threads; the view reads it on a timer.
    /// </summary>
    internal sealed class GenerationMonitor
    {
        private readonly object _gate = new();
        private readonly GenerationSpeedTracker _tracker = new();
        private GenerationStatsSnapshot? _last;
        private string _modeLabel = "Local";
        private bool _live;

        /// <summary>Raised (on any thread) when a reply starts or ends.</summary>
        public event Action? LiveChanged;

        public void BeginReply(string modeLabel)
        {
            lock (_gate)
            {
                _tracker.Begin(DateTime.UtcNow);
                _modeLabel = modeLabel;
                _live = true;
            }

            LiveChanged?.Invoke();
        }

        public void ReportCharacters(int totalSoFar)
        {
            lock (_gate)
            {
                if (_live)
                    _tracker.RecordCharacters(totalSoFar, DateTime.UtcNow);
            }
        }

        public void ReportTokens(int totalSoFar)
        {
            lock (_gate)
            {
                if (_live)
                    _tracker.RecordTokens(totalSoFar, DateTime.UtcNow);
            }
        }

        /// <summary>
        /// Ends the reply. A reply that produced nothing (cancelled before any text) keeps the
        /// previous reply's numbers on screen rather than replacing them with zeros.
        /// </summary>
        public void EndReply(int reportedCompletionTokens = 0)
        {
            bool wasLive;
            lock (_gate)
            {
                wasLive = _live;
                if (!_live)
                    return;

                _live = false;
                GenerationStatsSnapshot snapshot = _tracker.Snapshot(DateTime.UtcNow, reportedCompletionTokens);
                if (snapshot.OutputTokens > 0)
                    _last = snapshot;
            }

            if (wasLive)
                LiveChanged?.Invoke();
        }

        /// <summary>The reply in progress if there is one, otherwise the last finished reply.</summary>
        public (GenerationStatsSnapshot? Stats, string ModeLabel, bool IsLive) Read()
        {
            lock (_gate)
            {
                if (_live)
                {
                    GenerationStatsSnapshot current = _tracker.Snapshot(DateTime.UtcNow);
                    return (current.OutputTokens > 0 ? current : null, _modeLabel, true);
                }

                return (_last, _modeLabel, false);
            }
        }
    }

    /// <summary>
    /// Connects streams to the readout of whichever view started the work, without passing a
    /// parameter through every call between them.
    /// </summary>
    /// <remarks>
    /// The monitor travels with the async flow (<see cref="AsyncLocal{T}"/>), so the cloud SSE loop
    /// and the local token loops can report into it however deep they sit, and a Chat reply and a
    /// Workplace run going at the same time each update their own readout.
    /// </remarks>
    internal static class GenerationTelemetry
    {
        private static readonly AsyncLocal<GenerationMonitor?> CurrentMonitor = new();

        public static GenerationMonitor? Current => CurrentMonitor.Value;

        /// <summary>Routes every reply started in this async flow to <paramref name="monitor"/>.</summary>
        public static IDisposable Use(GenerationMonitor monitor)
        {
            GenerationMonitor? previous = CurrentMonitor.Value;
            CurrentMonitor.Value = monitor;
            return new Scope(previous);
        }

        public static void BeginReply(string modeLabel) => Current?.BeginReply(modeLabel);

        public static void ReportCharacters(int totalSoFar) => Current?.ReportCharacters(totalSoFar);

        public static void ReportTokens(int totalSoFar) => Current?.ReportTokens(totalSoFar);

        public static void EndReply(int reportedCompletionTokens = 0) => Current?.EndReply(reportedCompletionTokens);

        private sealed class Scope(GenerationMonitor? previous) : IDisposable
        {
            public void Dispose() => CurrentMonitor.Value = previous;
        }
    }
}
