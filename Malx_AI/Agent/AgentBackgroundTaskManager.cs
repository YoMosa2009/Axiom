using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    public sealed record AgentBackgroundTaskSnapshot(
        string Id,
        string Title,
        string Command,
        string WorkingDirectory,
        DateTime StartedAtUtc,
        DateTime? CompletedAtUtc,
        int WakeAfterSeconds,
        bool IsRunning,
        bool WasStoppedByUser,
        int? ExitCode,
        string OutputTail)
    {
        public TimeSpan Elapsed => (CompletedAtUtc ?? DateTime.UtcNow) - StartedAtUtc;
    }

    /// <summary>
    /// Runs and tracks background shell tasks for Workplace agents across all model tiers.
    /// Models can launch background tasks while continuing other work, OR pause/sleep for a
    /// chosen <c>wake_seconds</c> duration until the task completes or the wake timer fires.
    /// </summary>
    public sealed class AgentBackgroundTaskManager : IDisposable
    {
        private const int MaxStoredOutputChars = 16000;
        private const int MinWakeSeconds = 1;
        private const int MaxWakeSeconds = 3600;
        private const int DefaultWakeSeconds = 10;

        private sealed class TaskEntry
        {
            public required string Id { get; init; }
            public required string Title { get; init; }
            public required string Command { get; init; }
            public required string WorkingDirectory { get; init; }
            public required DateTime StartedAtUtc { get; init; }
            public DateTime? CompletedAtUtc { get; set; }
            public int WakeAfterSeconds { get; set; }
            public DateTime NextWakeAtUtc { get; set; }
            public bool IsRunning { get; set; }
            public bool WasStoppedByUser { get; set; }
            public int? ExitCode { get; set; }
            public bool CompletionNotified { get; set; }
            public bool TimerWakeNotified { get; set; }
            public Process? Process { get; set; }
            public StringBuilder Output { get; } = new();
            public ConversationEvidence? Evidence { get; init; }
            public TaskCompletionSource<bool> ExitSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public object Gate { get; } = new();
        }

        private readonly object _listGate = new();
        private readonly Dictionary<string, TaskEntry> _tasks = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _order = new();
        private int _counter;
        private bool _disposed;

        /// <summary>Raised whenever a background task starts, changes state, or exits.</summary>
        public event Action? Changed;

        public static int ClampWakeSeconds(int? requestedSeconds)
        {
            if (requestedSeconds is null or <= 0)
                return DefaultWakeSeconds;
            return Math.Clamp(requestedSeconds.Value, MinWakeSeconds, MaxWakeSeconds);
        }

        public static string BuildDefaultTitle(string command)
        {
            string flat = (command ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (flat.Contains("  ", StringComparison.Ordinal))
                flat = flat.Replace("  ", " ", StringComparison.Ordinal);
            if (flat.Length == 0)
                return "Background command";
            return flat.Length <= 42 ? flat : flat[..41].TrimEnd() + "…";
        }

        public AgentBackgroundTaskSnapshot StartBackgroundTask(
            string command,
            string workingDirectory,
            int wakeAfterSeconds,
            string? title = null)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(AgentBackgroundTaskManager));

            string normalizedCommand = AgentToolExecutor.NormalizeCommand(command);
            if (string.IsNullOrWhiteSpace(normalizedCommand))
                throw new ArgumentException("Background task command cannot be empty.", nameof(command));

            string cwd = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                ? workingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            int clampedWake = ClampWakeSeconds(wakeAfterSeconds);
            int number = Interlocked.Increment(ref _counter);
            string id = $"bg-{number}";
            string resolvedTitle = string.IsNullOrWhiteSpace(title) ? BuildDefaultTitle(normalizedCommand) : title.Trim();
            DateTime now = DateTime.UtcNow;

            var entry = new TaskEntry
            {
                Evidence = ConversationEvidence.Current,
                Id = id,
                Title = resolvedTitle,
                Command = normalizedCommand,
                WorkingDirectory = cwd,
                StartedAtUtc = now,
                WakeAfterSeconds = clampedWake,
                NextWakeAtUtc = now.AddSeconds(clampedWake),
                IsRunning = true
            };

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand "
                    + AgentToolExecutor.EncodeForPowerShell(normalizedCommand),
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            AgentToolExecutor.PrepareShellEnvironment(startInfo);
            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            entry.Process = process;

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    AppendOutput(entry, e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    AppendOutput(entry, "[stderr] " + e.Data);
            };
            process.Exited += (_, _) =>
            {
                int? exitCode = null;
                try
                {
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }
                catch { }

                lock (entry.Gate)
                {
                    entry.IsRunning = false;
                    entry.CompletedAtUtc ??= DateTime.UtcNow;
                    entry.ExitCode = exitCode;
                }
                entry.ExitSignal.TrySetResult(true);
                NotifyChanged();
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("Failed to start background PowerShell process.");
            }

            AgentProcessOwnership.Claim(process);

            lock (_listGate)
            {
                _tasks[id] = entry;
                _order.Add(id);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            NotifyChanged();
            return ToSnapshot(entry);
        }

        public async Task<(AgentBackgroundTaskSnapshot? Snapshot, bool WokeByCompletion, string Message)> WaitForTaskOrWakeTimerAsync(
            string taskId,
            int wakeAfterSeconds,
            CancellationToken token)
        {
            if (!TryGetEntry(taskId, out TaskEntry? entry) || entry == null)
                return (null, false, $"No background task found with id '{taskId}'.");

            int clampedWake = ClampWakeSeconds(wakeAfterSeconds);
            bool alreadyFinished;
            lock (entry.Gate)
            {
                alreadyFinished = !entry.IsRunning;
                entry.WakeAfterSeconds = clampedWake;
                entry.NextWakeAtUtc = DateTime.UtcNow.AddSeconds(clampedWake);
                entry.TimerWakeNotified = false;
            }

            if (alreadyFinished)
            {
                lock (entry.Gate)
                {
                    entry.CompletionNotified = true;
                    entry.TimerWakeNotified = true;
                }
                AgentBackgroundTaskSnapshot doneSnap = ToSnapshot(entry);
                return (doneSnap, true, FormatTaskReport(doneSnap, wokeByTimer: false));
            }

            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task delayTask = Task.Delay(TimeSpan.FromSeconds(clampedWake), delayCts.Token);
            Task finished = await Task.WhenAny(entry.ExitSignal.Task, delayTask).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            bool wokeByCompletion = ReferenceEquals(finished, entry.ExitSignal.Task);
            if (wokeByCompletion)
            {
                try { delayCts.Cancel(); } catch { }
            }

            lock (entry.Gate)
            {
                if (wokeByCompletion)
                    entry.CompletionNotified = true;
                entry.TimerWakeNotified = true;
            }

            AgentBackgroundTaskSnapshot snapshot = ToSnapshot(entry);
            return (snapshot, wokeByCompletion, FormatTaskReport(snapshot, wokeByTimer: !wokeByCompletion));
        }

        public IReadOnlyList<string> TakeDueWakeNotifications()
        {
            List<TaskEntry> entries;
            lock (_listGate)
                entries = _order.Select(id => _tasks[id]).ToList();

            var notifications = new List<string>();
            DateTime now = DateTime.UtcNow;

            foreach (TaskEntry entry in entries)
            {
                AgentBackgroundTaskSnapshot? notifySnap = null;
                bool notifyAsTimer = false;

                lock (entry.Gate)
                {
                    if (!entry.IsRunning && !entry.CompletionNotified)
                    {
                        entry.CompletionNotified = true;
                        entry.TimerWakeNotified = true;
                        notifySnap = ToSnapshotUnsafe(entry);
                        notifyAsTimer = false;
                    }
                    else if (entry.IsRunning && !entry.TimerWakeNotified && now >= entry.NextWakeAtUtc)
                    {
                        entry.TimerWakeNotified = true;
                        notifySnap = ToSnapshotUnsafe(entry);
                        notifyAsTimer = true;
                    }
                }

                if (notifySnap != null)
                {
                    notifications.Add(FormatTaskReport(notifySnap, notifyAsTimer));
                }
            }

            return notifications;
        }

        public AgentBackgroundTaskSnapshot? GetTask(string taskId)
            => TryGetEntry(taskId, out TaskEntry? entry) && entry != null ? ToSnapshot(entry) : null;

        public IReadOnlyList<AgentBackgroundTaskSnapshot> ListTasks()
        {
            List<TaskEntry> entries;
            lock (_listGate)
                entries = _order.Select(id => _tasks[id]).Reverse().ToList();

            return entries.Select(ToSnapshot).ToList();
        }

        public (bool Stopped, string Message) StopTask(string taskId)
        {
            if (!TryGetEntry(taskId, out TaskEntry? entry) || entry == null)
                return (false, $"No background task found with id '{taskId}'.");

            bool wasRunning;
            Process? proc;
            lock (entry.Gate)
            {
                wasRunning = entry.IsRunning;
                proc = entry.Process;
                if (wasRunning)
                {
                    entry.WasStoppedByUser = true;
                    entry.IsRunning = false;
                    entry.CompletedAtUtc ??= DateTime.UtcNow;
                }
            }

            if (wasRunning && proc != null)
            {
                try
                {
                    if (!proc.HasExited)
                        proc.Kill(entireProcessTree: true);
                }
                catch { }
            }

            entry.ExitSignal.TrySetResult(true);
            NotifyChanged();
            return (wasRunning, wasRunning
                ? $"Stopped background task '{entry.Id}' ({entry.Title})."
                : $"Background task '{entry.Id}' had already finished (exit code {entry.ExitCode?.ToString() ?? "unknown"}).");
        }

        public void StopAll()
        {
            List<string> ids;
            lock (_listGate)
                ids = _order.ToList();

            foreach (string id in ids)
                StopTask(id);
        }

        private bool TryGetEntry(string taskId, out TaskEntry? entry)
        {
            entry = null;
            if (string.IsNullOrWhiteSpace(taskId))
                return false;

            lock (_listGate)
                return _tasks.TryGetValue(taskId.Trim(), out entry);
        }

        private void AppendOutput(TaskEntry entry, string line)
        {
            lock (entry.Gate)
            {
                entry.Output.AppendLine(line);
                entry.Evidence?.Record("Background " + entry.Id, line);
                if (entry.Output.Length > MaxStoredOutputChars)
                {
                    int remove = entry.Output.Length - MaxStoredOutputChars;
                    entry.Output.Remove(0, remove);
                }
            }
            NotifyChanged();
        }

        private AgentBackgroundTaskSnapshot ToSnapshot(TaskEntry entry)
        {
            lock (entry.Gate)
                return ToSnapshotUnsafe(entry);
        }

        private static AgentBackgroundTaskSnapshot ToSnapshotUnsafe(TaskEntry entry)
        {
            string rawOut = entry.Output.ToString();
            string cleaned = AgentToolExecutor.CleanPowerShellErrorStream(rawOut).Trim();
            string tail = cleaned.Length <= 4000 ? cleaned : cleaned[^4000..];
            return new AgentBackgroundTaskSnapshot(
                entry.Id,
                entry.Title,
                entry.Command,
                entry.WorkingDirectory,
                entry.StartedAtUtc,
                entry.CompletedAtUtc,
                entry.WakeAfterSeconds,
                entry.IsRunning,
                entry.WasStoppedByUser,
                entry.ExitCode,
                tail);
        }

        internal static string FormatTaskReport(AgentBackgroundTaskSnapshot snapshot, bool wokeByTimer)
        {
            string status = snapshot.WasStoppedByUser
                ? "stopped"
                : snapshot.IsRunning
                    ? $"still running after {Math.Max(1, (int)Math.Round(snapshot.Elapsed.TotalSeconds))}s (wake timer {snapshot.WakeAfterSeconds}s reached)"
                    : $"finished in {Math.Max(0, (int)Math.Round(snapshot.Elapsed.TotalSeconds))}s (exit code {snapshot.ExitCode?.ToString() ?? "0"})";

            string header = wokeByTimer
                ? $"[BACKGROUND TASK WAKE ({snapshot.WakeAfterSeconds}s): {snapshot.Id}] {snapshot.Title} — {status}"
                : $"[BACKGROUND TASK COMPLETE: {snapshot.Id}] {snapshot.Title} — {status}";

            string output = string.IsNullOrWhiteSpace(snapshot.OutputTail)
                ? "(no output captured yet)"
                : snapshot.OutputTail;

            return $"{header}\nCommand: {snapshot.Command}\nOutput:\n{output}";
        }

        private void NotifyChanged()
        {
            try { Changed?.Invoke(); }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            StopAll();
        }
    }
}
