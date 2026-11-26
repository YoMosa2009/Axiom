using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI.Agent
{
    /// <summary>Where the agent is allowed to operate.</summary>
    public sealed record AgentScope(string RootPath, bool EntireComputer)
    {
        public static AgentScope WholeComputer() => new(string.Empty, true);
        public static AgentScope Folder(string root) => new(root ?? string.Empty, false);

        /// <summary>The directory commands start in.</summary>
        public string WorkingDirectory =>
            !EntireComputer && Directory.Exists(RootPath)
                ? RootPath
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        /// <summary>
        /// True when a path may be touched. A folder scope confines the agent to that tree; the
        /// whole-computer scope confines nothing, which is the point of choosing it.
        /// </summary>
        public bool Allows(string absolutePath)
        {
            if (EntireComputer)
                return true;
            if (string.IsNullOrWhiteSpace(RootPath) || string.IsNullOrWhiteSpace(absolutePath))
                return false;

            try
            {
                string root = Path.GetFullPath(RootPath).TrimEnd(Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(absolutePath);
                return target.Equals(root, StringComparison.OrdinalIgnoreCase)
                    || target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public string Describe() => EntireComputer
            ? "the entire computer"
            : string.IsNullOrWhiteSpace(RootPath) ? "no folder yet" : RootPath;
    }

    /// <summary>Runs the agent's tools against the real machine.</summary>
    public sealed class AgentToolExecutor
    {
        private const int MaxOutputChars = 20000;
        // Tool observations are capped at 6000 characters. Report the actual stopping line
        // here rather than silently losing most of a 60K read when it reaches the model.
        private readonly int _maxReadChars;
        private readonly string _screenshotDirectory;
        private const int DefaultTimeoutSeconds = 120;

        private readonly AgentScope _scope;
        private readonly AgentFileBackup? _backup;
        private readonly AgentBackgroundTaskManager _backgroundTasks;
        private readonly BrowserControlService _browserControl;
        private readonly Func<AgentToolCall, CancellationToken, Task<AgentToolResult?>>? _workplaceToolAsync;
        private readonly Func<int, CancellationToken, Task<(byte[] PngBytes, int Width, int Height)>>? _screenCaptureAsync;
        private readonly Func<string, int, CancellationToken, Task<AgentScreenshot>>? _windowCaptureAsync;

        public AgentToolExecutor(
            AgentScope scope,
            AgentFileBackup? backup = null,
            AgentBackgroundTaskManager? backgroundTasks = null,
            BrowserControlService? browserControl = null,
            Func<int, CancellationToken, Task<(byte[] PngBytes, int Width, int Height)>>? screenCaptureAsync = null,
            Func<string, int, CancellationToken, Task<AgentScreenshot>>? windowCaptureAsync = null,
            int readBudgetChars = 5000,
            string? screenshotDirectory = null,
            Func<AgentToolCall, CancellationToken, Task<AgentToolResult?>>? workplaceToolAsync = null)
        {
            _scope = scope ?? AgentScope.WholeComputer();
            _backup = backup;
            _backgroundTasks = backgroundTasks ?? new AgentBackgroundTaskManager();
            _browserControl = browserControl ?? new BrowserControlService();
            _workplaceToolAsync = workplaceToolAsync;
            _screenCaptureAsync = screenCaptureAsync;
            _windowCaptureAsync = windowCaptureAsync;
            _maxReadChars = Math.Clamp(readBudgetChars, 256, 5000);
            _screenshotDirectory = screenshotDirectory ?? Path.Combine(Path.GetTempPath(), "AxiomScreenshots");
        }

        public AgentBackgroundTaskManager BackgroundTasks => _backgroundTasks;

        public BrowserControlService BrowserControl => _browserControl;

        /// <summary>Copies a file that already existed before its first change; says where, if it did.</summary>
        private string BackupNote(string path)
        {
            string? copy = _backup?.BeforeChange(path);
            return copy == null ? string.Empty : $" The previous version was saved to {copy}.";
        }

        public async Task<AgentToolResult> ExecuteAsync(AgentToolCall call, CancellationToken token)
        {
            // Also enforce at the execution boundary: direct callers and background commands
            // must not bypass the session guard, even after approval.
            if ((call.Tool.Equals(AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                    || call.Tool.Equals(AgentToolNames.StartBackgroundTask, StringComparison.OrdinalIgnoreCase))
                && AgentSession.IsUiFocusAutomationCommand(call.Arg("command")))
                return AgentToolResult.Fail(AgentSession.DesktopIsolationMessage);
            try
            {
                if (_workplaceToolAsync != null && call.Tool.ToLowerInvariant() is AgentToolNames.BrowserControl or AgentToolNames.UseSkill or AgentToolNames.TakeScreenshot)
                {
                    AgentToolResult? result = await _workplaceToolAsync(call, token);
                    if (result != null)
                        return result;
                }
                return call.Tool.ToLowerInvariant() switch
                {
                    AgentToolNames.RecallContext => AgentToolResult.Ok(ConversationEvidence.Current?.Read(call.Arg("query"),
                        int.TryParse(call.Arg("offset"), out int offset) ? offset : 0, _maxReadChars - 128) ?? "No active conversation evidence archive."),
                    AgentToolNames.RunCommand => await RunCommandAsync(call, token),
                    AgentToolNames.ReadFile => ReadFile(call),
                    AgentToolNames.WriteFile => WriteFile(call),
                    AgentToolNames.AppendFile => AppendFile(call),
                    AgentToolNames.EditFile => EditFile(call),
                    AgentToolNames.ListDirectory => ListDirectory(call),
                    AgentToolNames.FindFiles => FindFiles(call),
                    AgentToolNames.SearchText => SearchText(call),
                    AgentToolNames.StartBackgroundTask => await StartBackgroundTaskAsync(call, token),
                    AgentToolNames.CheckBackgroundTask => await CheckBackgroundTaskAsync(call, token),
                    AgentToolNames.StopBackgroundTask => StopBackgroundTask(call),
                    AgentToolNames.BrowserControl => await RunBrowserControlAsync(call, token),
                    AgentToolNames.UseSkill => AgentToolResult.Fail("No attached Skills are available in this session."),
                    AgentToolNames.TakeScreenshot => await TakeScreenshotAsync(call, token),
                    AgentToolNames.Finish => AgentToolResult.Ok("done"),
                    _ => AgentToolResult.Fail($"Unknown tool '{call.Tool}'.")
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return AgentToolResult.Fail(ex.Message);
            }
        }

        private async Task<AgentToolResult> RunCommandAsync(AgentToolCall call, CancellationToken token)
        {
            string command = call.Arg("command");
            if (string.IsNullOrWhiteSpace(command))
                return AgentToolResult.Fail("No command supplied.");

            command = NormalizeCommand(command);

            if (!_scope.EntireComputer && !string.IsNullOrWhiteSpace(_scope.RootPath))
            {
                if (CommandSafetyReviewer.ReferencesPathOutsideScope(command, _scope.RootPath, out string? offendingPath))
                {
                    return AgentToolResult.Fail(
                        $"Strict project scope violation: command references '{offendingPath}', which is outside '{_scope.RootPath}'.");
                }
            }

            string requestedCwd = call.Arg("cwd");
            if (!string.IsNullOrWhiteSpace(requestedCwd) && !TryResolve(requestedCwd, out _, out string cwdError))
                return AgentToolResult.Fail(cwdError);

            string workingDirectory = ResolveWorkingDirectory(requestedCwd);

            if (ParseBool(call.Arg("background")))
                return await StartBackgroundTaskCoreAsync(command, workingDirectory, call.Arg("wake_seconds"), call.Arg("title"), waitForWake: false, token);

            int timeoutSeconds = int.TryParse(call.Arg("timeout_seconds"), out int parsed) && parsed > 0
                ? Math.Min(parsed, 600)
                : DefaultTimeoutSeconds;

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                // -NoProfile keeps a user's profile script from changing what the agent sees, and
                // -NonInteractive turns a prompt into an error instead of a hang nobody can answer.
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + EncodeForPowerShell(command),
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.Exited += (_, _) => exited.TrySetResult();
            process.OutputDataReceived += (_, e) => CollectOutput(e.Data, stdout, stdoutClosed);
            process.ErrorDataReceived += (_, e) => CollectOutput(e.Data, stderr, stderrClosed);

            var stopwatch = Stopwatch.StartNew();
            if (!process.Start())
                return AgentToolResult.Fail("The command could not be started.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            bool outputStillOpen = false;
            try
            {
                // WaitForExitAsync also waits for redirected output to close. A child launched
                // by PowerShell can retain those handles after PowerShell itself exits.
                await exited.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), token);
                try
                {
                    await Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).WaitAsync(TimeSpan.FromSeconds(1), token);
                }
                catch (TimeoutException)
                {
                    outputStillOpen = true;
                }
            }
            catch (TimeoutException)
            {
                TryKill(process);
                stopwatch.Stop();
                string partialOutput = SnapshotOutput(stdout);
                string partialError = SnapshotOutput(stderr);
                RecordCommandOutput(partialOutput, partialError);
                return AgentToolResult.Fail(
                    $"The command timed out after {timeoutSeconds}s ({stopwatch.ElapsedMilliseconds}ms) and was stopped.\n"
                    + Combine(partialOutput, CleanPowerShellErrorStream(partialError)));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                RecordCommandOutput(SnapshotOutput(stdout), SnapshotOutput(stderr));
                throw;
            }

            stopwatch.Stop();
            long elapsedMs = stopwatch.ElapsedMilliseconds;

            string capturedOutput = SnapshotOutput(stdout);
            string rawError = SnapshotOutput(stderr);
            RecordCommandOutput(capturedOutput, rawError);
            string capturedError = CleanPowerShellErrorStream(rawError);
            string combined = Combine(capturedOutput, capturedError);
            if (outputStillOpen)
            {
                combined += "\n[An output stream remained open after the shell exited. "
                    + "Any later output from child processes is not included.]";
            }
            string exitPrefix = $"[Exit code: {process.ExitCode} | Duration: {elapsedMs}ms]";

            if (process.ExitCode == 0)
            {
                string body = string.IsNullOrWhiteSpace(combined)
                    ? "(Command completed successfully with no standard output)"
                    : combined;
                return AgentToolResult.Ok($"{exitPrefix}\n{body}");
            }
            else if (IsDirectoryAlreadyExistsScenario(command, capturedOutput, capturedError))
            {
                string body = $"Directory already exists and is ready for use.\n{combined}".Trim();
                return AgentToolResult.Ok($"[Exit code: 0 (Directory already exists) | Duration: {elapsedMs}ms]\n{body}");
            }
            else
            {
                string body = string.IsNullOrWhiteSpace(combined)
                    ? "(Command failed with no output)"
                    : combined;
                return AgentToolResult.Fail($"{exitPrefix}\n{body}{ShellMismatchHint(command)}{InlineScriptHint(command)}");
            }
        }

        private async Task<AgentToolResult> StartBackgroundTaskAsync(AgentToolCall call, CancellationToken token)
        {
            string command = call.Arg("command");
            if (string.IsNullOrWhiteSpace(command))
                return AgentToolResult.Fail("No command supplied for start_background_task.");

            command = NormalizeCommand(command);

            if (!_scope.EntireComputer && !string.IsNullOrWhiteSpace(_scope.RootPath))
            {
                if (CommandSafetyReviewer.ReferencesPathOutsideScope(command, _scope.RootPath, out string? offendingPath))
                {
                    return AgentToolResult.Fail(
                        $"Strict project scope violation: command references '{offendingPath}', which is outside '{_scope.RootPath}'.");
                }
            }

            string requestedCwd = call.Arg("cwd");
            if (!string.IsNullOrWhiteSpace(requestedCwd) && !TryResolve(requestedCwd, out _, out string cwdError))
                return AgentToolResult.Fail(cwdError);

            string workingDirectory = ResolveWorkingDirectory(requestedCwd);
            bool waitForWake = ParseBool(call.Arg("wait"));
            return await StartBackgroundTaskCoreAsync(command, workingDirectory, call.Arg("wake_seconds"), call.Arg("title"), waitForWake, token);
        }

        private async Task<AgentToolResult> StartBackgroundTaskCoreAsync(
            string command,
            string workingDirectory,
            string wakeSecondsArg,
            string title,
            bool waitForWake,
            CancellationToken token)
        {
            int? parsedWake = int.TryParse(wakeSecondsArg, out int w) && w > 0 ? w : null;
            int wakeSeconds = AgentBackgroundTaskManager.ClampWakeSeconds(parsedWake);

            AgentBackgroundTaskSnapshot snap = _backgroundTasks.StartBackgroundTask(command, workingDirectory, wakeSeconds, title);
            if (waitForWake)
            {
                (AgentBackgroundTaskSnapshot? waitedSnap, _, string report) =
                    await _backgroundTasks.WaitForTaskOrWakeTimerAsync(snap.Id, snap.WakeAfterSeconds, token).ConfigureAwait(false);
                return waitedSnap != null
                    ? AgentToolResult.Ok(report)
                    : AgentToolResult.Fail(report);
            }

            return AgentToolResult.Ok(
                $"Started background task '{snap.Id}' ({snap.Title}) with wake timer {snap.WakeAfterSeconds}s.\n"
                + $"Continue with other work (you will be notified when it completes or after {snap.WakeAfterSeconds}s), "
                + $"or call check_background_task(task_id='{snap.Id}', wake_seconds={snap.WakeAfterSeconds}) to pause and wait.");
        }

        private async Task<AgentToolResult> CheckBackgroundTaskAsync(AgentToolCall call, CancellationToken token)
        {
            string taskId = call.Arg("task_id");
            if (string.IsNullOrWhiteSpace(taskId))
            {
                IReadOnlyList<AgentBackgroundTaskSnapshot> tasks = _backgroundTasks.ListTasks();
                if (tasks.Count == 0)
                    return AgentToolResult.Ok("No background tasks have been started in this session.");

                var sb = new StringBuilder();
                foreach (AgentBackgroundTaskSnapshot t in tasks)
                {
                    string state = t.WasStoppedByUser
                        ? "stopped"
                        : t.IsRunning ? "running" : $"finished (exit {t.ExitCode?.ToString() ?? "0"})";
                    sb.AppendLine($"- {t.Id}: {t.Title} [{state}, {Math.Max(0, (int)Math.Round(t.Elapsed.TotalSeconds))}s]");
                    if (!string.IsNullOrWhiteSpace(t.OutputTail))
                    {
                        string tail = t.OutputTail.Length <= 400 ? t.OutputTail : t.OutputTail[^400..];
                        sb.AppendLine("  Output: " + tail.Replace('\r', ' ').Replace('\n', ' '));
                    }
                }
                return AgentToolResult.Ok(sb.ToString().TrimEnd());
            }

            if (int.TryParse(call.Arg("wake_seconds"), out int waitSec) && waitSec > 0)
            {
                (AgentBackgroundTaskSnapshot? snap, _, string report) =
                    await _backgroundTasks.WaitForTaskOrWakeTimerAsync(taskId, waitSec, token).ConfigureAwait(false);
                return snap != null ? AgentToolResult.Ok(report) : AgentToolResult.Fail(report);
            }

            AgentBackgroundTaskSnapshot? existing = _backgroundTasks.GetTask(taskId);
            if (existing == null)
                return AgentToolResult.Fail($"No background task found with id '{taskId}'.");

            return AgentToolResult.Ok(AgentBackgroundTaskManager.FormatTaskReport(existing, wokeByTimer: false));
        }

        private AgentToolResult StopBackgroundTask(AgentToolCall call)
        {
            string taskId = call.Arg("task_id");
            if (string.IsNullOrWhiteSpace(taskId))
                return AgentToolResult.Fail("No task_id supplied for stop_background_task.");

            (bool stopped, string message) = _backgroundTasks.StopTask(taskId);
            return stopped || message.Contains("already finished", StringComparison.OrdinalIgnoreCase)
                ? AgentToolResult.Ok(message)
                : AgentToolResult.Fail(message);
        }

        private async Task<AgentToolResult> RunBrowserControlAsync(AgentToolCall call, CancellationToken token)
        {
            string action = call.Arg("action", "detect");
            string urlOrQuery = call.Arg("url");
            string destination = call.Arg("destination");
            return await _browserControl.ExecuteAsync(
                action,
                urlOrQuery,
                string.IsNullOrWhiteSpace(destination) ? null : destination,
                _scope.WorkingDirectory,
                path => _scope.Allows(path),
                token).ConfigureAwait(false);
        }

        private async Task<AgentToolResult> TakeScreenshotAsync(AgentToolCall call, CancellationToken token)
        {
            string requestedUrl = call.Arg("url");
            if (string.IsNullOrWhiteSpace(requestedUrl) && _screenCaptureAsync == null && _windowCaptureAsync == null)
                return AgentToolResult.Fail("Screenshot capture is not available in this session.");

            int delayMs = int.TryParse(call.Arg("delay_ms"), out int parsedDelay)
                ? Math.Clamp(parsedDelay, 0, 5000)
                : 600;
            if (delayMs > 0)
                await Task.Delay(delayMs, token).ConfigureAwait(false);

            string requestedSavePath = call.Arg("save_path");
            string outputPath;
            if (!string.IsNullOrWhiteSpace(requestedSavePath))
            {
                if (!TryResolve(requestedSavePath, out outputPath, out string resolveError))
                    return AgentToolResult.Fail(resolveError);
            }
            else
            {
                string folder = _screenshotDirectory;
                Directory.CreateDirectory(folder);
                outputPath = Path.Combine(folder, $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            }

            // Which window the picture shows is said in words, every time. A model that cannot
            // tell whether it is looking at its own app or at something else in front of it has
            // no way to trust the image, and the logged run answered that doubt by refocusing the
            // browser with WScript.Shell and screenshotting again, forty times.
            string requestedWindow = call.Arg("window");
            string subject = "the screen";
            string windowList = string.Empty;
            byte[] pngBytes;
            int width;
            int height;
            if (!string.IsNullOrWhiteSpace(requestedUrl))
            {
                AgentScreenshot shot = await AgentPageCapture.CaptureAsync(requestedUrl, _scope, token).ConfigureAwait(false);
                (pngBytes, width, height, subject) = (shot.PngBytes, shot.Width, shot.Height, shot.Description);
            }
            else if (_windowCaptureAsync != null)
            {
                AgentScreenshot shot = await _windowCaptureAsync(requestedWindow, 1280, token).ConfigureAwait(false);
                windowList = AgentScreenshot.DescribeOpenWindows(shot.OpenWindows);
                if (shot.PngBytes == null || shot.PngBytes.Length == 0)
                {
                    return AgentToolResult.Fail(!string.IsNullOrWhiteSpace(shot.Description)
                        ? shot.Description + " " + windowList
                        : string.IsNullOrWhiteSpace(requestedWindow)
                        ? "Screenshot capture returned an empty image."
                        : $"No open window matches '{requestedWindow}'. {windowList} "
                          + "Call take_screenshot with window set to part of one of those titles.");
                }

                (pngBytes, width, height, subject) = (shot.PngBytes, shot.Width, shot.Height, shot.Description);
            }
            else
            {
                (pngBytes, width, height) = await _screenCaptureAsync!(1280, token).ConfigureAwait(false);
                if (pngBytes == null || pngBytes.Length == 0)
                    return AgentToolResult.Fail("Screenshot capture returned an empty image.");
            }

            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);
            await File.WriteAllBytesAsync(outputPath, pngBytes, token).ConfigureAwait(false);

            string dataUrl = "data:image/png;base64," + Convert.ToBase64String(pngBytes);
            return AgentToolResult.Ok(
                $"Captured screenshot of {subject} ({width}x{height}) saved to {outputPath}. Inspect the attached image closely to verify visual layout, rendering, and state."
                + (windowList.Length == 0
                    ? string.Empty
                    : $" {windowList} If this is not the app you meant to check, call take_screenshot ONCE more with window set to part of its title. Never use AppActivate, SendKeys or WScript.Shell to bring a window forward: the window argument captures it wherever it is."),
                dataUrl, outputPath);
        }

        private static bool ParseBool(string value)
        {
            string normalized = (value ?? string.Empty).Trim();
            return normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("1", StringComparison.Ordinal)
                || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase);
        }

        private AgentToolResult ReadFile(AgentToolCall call)
        {
            string requested = call.Arg("path");
            bool archivedImage = ConversationEvidence.Current?.OwnsImage(requested) == true;
            string path = archivedImage ? requested : "";
            string error = "";
            if (!archivedImage && !TryResolve(requested, out path, out error))
                return AgentToolResult.Fail(error);
            if (!File.Exists(path))
                return AgentToolResult.Fail($"No such file: {path}");

            string ext = Path.GetExtension(path).ToLowerInvariant();
            string? imageMime = ext switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                _ => null
            };
            if (imageMime != null)
            {
                var info = new FileInfo(path);
                if (info.Length > 10 * 1024 * 1024)
                    return AgentToolResult.Fail($"Image file '{path}' is too large ({info.Length:N0} bytes; limit 10 MB).");

                byte[] bytes = File.ReadAllBytes(path);
                string dataUrl = $"data:{imageMime};base64,{Convert.ToBase64String(bytes)}";
                return AgentToolResult.Ok($"Loaded image '{path}' ({bytes.Length:N0} bytes) for visual inspection.", dataUrl);
            }

            string[] lines = File.ReadAllLines(path);
            int start = int.TryParse(call.Arg("start_line"), out int s) && s > 0 ? s : 1;
            int count = int.TryParse(call.Arg("line_count"), out int c) && c > 0 ? c : lines.Length;

            IEnumerable<string> slice = lines.Skip(start - 1).Take(count);
            var builder = new StringBuilder();
            int number = start;
            foreach (string line in slice)
            {
                if (builder.Length > 0 && builder.Length + line.Length + 16 > _maxReadChars)
                {
                    builder.AppendLine($"[Read stopped before line {number}; {lines.Length} total lines. Use search_text on this file to locate the relevant function, or start_line={number} to continue.]");
                    break;
                }
                builder.Append(number++).Append('\t').AppendLine(line);
                if (builder.Length > _maxReadChars)
                {
                    builder.Length = _maxReadChars;
                    builder.AppendLine($"\n[Line {number - 1} is longer than the output budget and was truncated. Use search_text to locate specific text.]");
                    break;
                }
            }

            return AgentToolResult.Ok(builder.Length == 0 ? "(empty file)" : builder.ToString());
        }

        private AgentToolResult WriteFile(AgentToolCall call)
        {
            if (!TryResolve(call.Arg("path"), out string path, out string error))
                return AgentToolResult.Fail(error);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string content = call.Arg("content");
            string backupNote = BackupNote(path);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            var fileInfo = new FileInfo(path);
            int lineCount = CountLines(content);
            return AgentToolResult.Ok($"Successfully wrote {fileInfo.Length:N0} bytes ({lineCount} lines) to {path}. Verified on disk.{backupNote}{AgentSyntaxCheck.Note(path, content)}");
        }

        /// <summary>
        /// Adds text to the end of a file (creating it if needed). Lets a model write a large
        /// file in parts: one oversized write_file call is cut off at the model's output limit
        /// and arrives as broken JSON, which previously stalled the whole run.
        /// </summary>
        private AgentToolResult AppendFile(AgentToolCall call)
        {
            if (!TryResolve(call.Arg("path"), out string path, out string error))
                return AgentToolResult.Fail(error);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            string content = call.Arg("content");
            string backupNote = BackupNote(path);
            File.AppendAllText(path, content, new UTF8Encoding(false));
            var fileInfo = new FileInfo(path);
            string full = File.ReadAllText(path);
            int lineCount = CountLines(full);
            return AgentToolResult.Ok($"Appended {content.Length:N0} characters. {path} is now {fileInfo.Length:N0} bytes ({lineCount} lines).{backupNote}{AgentSyntaxCheck.Note(path, full)}");
        }

        private AgentToolResult EditFile(AgentToolCall call)
        {
            if (!TryResolve(call.Arg("path"), out string path, out string error))
                return AgentToolResult.Fail(error);
            if (!File.Exists(path))
                return AgentToolResult.Fail($"No such file: {path}");

            string oldString = call.Arg("old_string");
            string newString = call.Arg("new_string");
            if (string.IsNullOrEmpty(oldString))
                return AgentToolResult.Fail("old_string was empty. Use write_file to replace a whole file.");

            string original = File.ReadAllText(path);
            AgentEditMatcher.Result match = AgentEditMatcher.Find(original, oldString, newString);
            if (!match.Found)
                return AgentToolResult.Fail(match.Error ?? "old_string was not found in the file. Read the file and copy the exact text.");

            string replaced = original[..match.Start] + match.Replacement + original[(match.Start + match.Length)..];
            string matchNote = match.Tier is "exact" or "line endings" ? "" : $" Matched {match.Tier}; check the result.";
            string backupNote = BackupNote(path);
            File.WriteAllText(path, replaced, new UTF8Encoding(false));
            var fileInfo = new FileInfo(path);
            int lineCount = CountLines(replaced);
            return AgentToolResult.Ok($"Successfully edited {path} ({fileInfo.Length:N0} bytes, {lineCount} lines). Verified on disk.{matchNote}{backupNote}{AgentSyntaxCheck.Note(path, replaced)}");
        }

        private AgentToolResult ListDirectory(AgentToolCall call)
        {
            if (!TryResolve(call.Arg("path", "."), out string path, out string error))
                return AgentToolResult.Fail(error);
            if (!Directory.Exists(path))
                return AgentToolResult.Fail($"No such directory: {path}");

            var builder = new StringBuilder();
            foreach (string directory in Directory.EnumerateDirectories(path).Take(400))
                builder.Append("dir   ").AppendLine(Path.GetFileName(directory));
            foreach (string file in Directory.EnumerateFiles(path).Take(400))
            {
                var info = new FileInfo(file);
                builder.Append("file  ").Append(info.Name).Append("  ").Append(info.Length).AppendLine(" bytes");
            }

            return AgentToolResult.Ok(builder.Length == 0 ? "(empty directory)" : builder.ToString());
        }

        private AgentToolResult FindFiles(AgentToolCall call)
        {
            string pattern = call.Arg("pattern", "*");
            if (!TryResolve(call.Arg("path", "."), out string root, out string error))
                return AgentToolResult.Fail(error);
            if (!Directory.Exists(root))
                return AgentToolResult.Fail($"No such directory: {root}");

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 12
            };

            List<string> matches = Directory
                .EnumerateFiles(root, pattern, options)
                .Where(file => !IsNoiseDirectory(file))
                .Take(300)
                .ToList();

            return AgentToolResult.Ok(matches.Count == 0
                ? "(no matching files)"
                : string.Join('\n', matches));
        }

        private AgentToolResult SearchText(AgentToolCall call)
        {
            string pattern = call.Arg("pattern");
            if (string.IsNullOrWhiteSpace(pattern))
                return AgentToolResult.Fail("No search pattern supplied.");
            if (!TryResolve(call.Arg("path", "."), out string root, out string error))
                return AgentToolResult.Fail(error);
            bool singleFile = File.Exists(root);
            if (!singleFile && !Directory.Exists(root))
                return AgentToolResult.Fail($"No such file or directory: {root}");

            string glob = call.Arg("file_pattern", "*");
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 12
            };

            var builder = new StringBuilder();
            int hits = 0;
            foreach (string file in singleFile ? new[] { root } : Directory.EnumerateFiles(root, glob, options))
            {
                if (hits >= 100 || IsNoiseDirectory(file) || !LooksTextual(file))
                    continue;

                int lineNumber = 0;
                foreach (string line in File.ReadLines(file))
                {
                    lineNumber++;
                    if (line.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Append(file).Append(':').Append(lineNumber).Append(": ")
                               .AppendLine(line.Trim().Length > 200 ? line.Trim()[..200] : line.Trim());
                        if (++hits >= 100)
                            break;
                    }
                }
            }

            return AgentToolResult.Ok(builder.Length == 0 ? "(no matches)" : builder.ToString());
        }

        private string ResolveWorkingDirectory(string requested)
        {
            if (!string.IsNullOrWhiteSpace(requested) && TryResolve(requested, out string resolved, out _) && Directory.Exists(resolved))
                return resolved;
            return _scope.WorkingDirectory;
        }

        private bool TryResolve(string requested, out string absolute, out string error)
        {
            absolute = string.Empty;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(requested))
            {
                error = "No path supplied.";
                return false;
            }

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'));
                absolute = Path.IsPathRooted(expanded)
                    ? Path.GetFullPath(expanded)
                    : Path.GetFullPath(Path.Combine(_scope.WorkingDirectory, expanded));
            }
            catch (Exception ex)
            {
                error = $"'{requested}' is not a usable path: {ex.Message}";
                return false;
            }

            if (!_scope.Allows(absolute))
            {
                error = $"'{absolute}' is outside the folder this agent is scoped to ({_scope.Describe()}).";
                return false;
            }

            return true;
        }

        private static bool IsNoiseDirectory(string path)
        {
            string lowered = path.ToLowerInvariant();
            return lowered.Contains(@"\node_modules\", StringComparison.Ordinal)
                || lowered.Contains(@"\.git\", StringComparison.Ordinal)
                || lowered.Contains(@"\bin\", StringComparison.Ordinal)
                || lowered.Contains(@"\obj\", StringComparison.Ordinal);
        }

        private static bool LooksTextual(string path)
        {
            try
            {
                return new FileInfo(path).Length <= 2_000_000;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Counts lines in text the same way a terminal counts them.</summary>
        internal static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;
            int newlines = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                    newlines++;
            }
            // A trailing newline (\n or \r\n) ends the last line rather than starting a new one.
            return text[^1] == '\n' ? newlines : newlines + 1;
        }

        private static void CollectOutput(string? line, StringBuilder output, TaskCompletionSource closed)
        {
            if (line == null)
            {
                closed.TrySetResult();
                return;
            }

            lock (output)
                output.AppendLine(line);
        }

        private static string SnapshotOutput(StringBuilder output)
        {
            lock (output)
                return output.ToString();
        }

        private static void RecordCommandOutput(string stdout, string stderr)
        {
            ConversationEvidence.Current?.Record("Full command stdout", stdout);
            ConversationEvidence.Current?.Record("Full command stderr", stderr);
        }

        private static string Combine(string stdout, string stderr)
        {
            var builder = new StringBuilder();
            builder.Append(stdout);
            if (stderr.Length > 0)
                builder.AppendLine().Append("[stderr]").AppendLine().Append(stderr);
            string text = builder.ToString().TrimEnd();
            return AgentOutputClip.HeadTail(text, MaxOutputChars);
        }

        private static readonly Regex UnixShellSyntaxRegex = new(
            @"(?:^|[\s;|])(?:ls|rm|cp|mv|mkdir)\s+-[a-zA-Z]{1,4}\b|/dev/null|(?:^|\s)/mnt/[a-z]\b|&&|\|\||(?:^|[\s;])(?:export\s+\w+=|touch\s|which\s|sudo\s|chmod\s|grep\s|cat\s+<<|head\s+-n|tail\s+-n)",
            RegexOptions.Compiled);

        /// <summary>
        /// A pointer back to PowerShell when a failed command was written for bash. Models trained
        /// mostly on Linux transcripts open with "ls -la /mnt/F 2>/dev/null", fail, and then
        /// guess again; one line naming the real shell settles it on the next step.
        /// </summary>
        internal static string ShellMismatchHint(string command) =>
            UnixShellSyntaxRegex.IsMatch(command ?? string.Empty)
                ? "\n[Hint: commands run in Windows PowerShell 5.1, not bash. Use PowerShell forms: Get-ChildItem -Force 'F:\\', Test-Path, New-Item, Select-String, ';' between commands (not '&&'), '2>$null' (not '2>/dev/null'), and Windows paths such as F:\\folder (not /mnt/f).]"
                : string.Empty;

        /// <summary>
        /// A pointer to a script file when an inline "python -c" / "node -e" command failed. The
        /// owner's logs (2026-11-23) show a run spending 40 steps on python -c one-liners whose
        /// nested quotes PowerShell mangled, and never writing the deliverable.
        /// </summary>
        internal static string InlineScriptHint(string command) =>
            InlineScriptRegex.IsMatch(command ?? string.Empty)
                ? "\n[Hint: inline scripts (python -c / node -e) break easily on PowerShell quoting. Write the script to a file with write_file (for example check.py) and run that file instead; the user can then reuse it.]"
                : string.Empty;

        private static readonly Regex InlineScriptRegex = new(@"(?i)\b(?:python3?|py|node)(?:\.exe)?\s+(?:-\S+\s+)*-(?:c|e)\s", RegexOptions.Compiled);

        /// <summary>
        /// Hands the command to PowerShell byte-for-byte as -EncodedCommand (UTF-16LE Base64).
        /// </summary>
        /// <remarks>
        /// The old form wrapped it as -Command "..." with inner quotes escaped as `" - but Windows
        /// splits the process command line before PowerShell sees it, and a backtick is not an
        /// escape there. Any command containing a double quote (every quoted path) arrived
        /// mangled: Start-Process "C:\site\index.html" became Start-Process `C:\... and failed with
        /// "cannot find the file specified", which sent agents into rewrite-and-retry loops.
        /// Progress output is silenced so PowerShell does not emit CLIXML progress records.
        /// </remarks>
        internal static string EncodeForPowerShell(string command) =>
            Convert.ToBase64String(Encoding.Unicode.GetBytes("$ProgressPreference = 'SilentlyContinue'\n" + (command ?? string.Empty)));

        private static readonly Regex ClixmlStringRegex = new(@"<S S=""(?:Error|Warning)"">(?<text>.*?)</S>", RegexOptions.Singleline | RegexOptions.Compiled);

        /// <summary>
        /// PowerShell started with -EncodedCommand may write its error stream as CLIXML
        /// ("#&lt; CLIXML" plus XML). Turns that back into the plain text a model can read.
        /// </summary>
        internal static string CleanPowerShellErrorStream(string stderr)
        {
            if (string.IsNullOrEmpty(stderr) || !stderr.Contains("#< CLIXML", StringComparison.Ordinal))
                return stderr ?? string.Empty;

            var builder = new StringBuilder();
            foreach (Match match in ClixmlStringRegex.Matches(stderr))
            {
                string text = System.Net.WebUtility.HtmlDecode(match.Groups["text"].Value)
                    .Replace("_x000D__x000A_", "\n", StringComparison.Ordinal)
                    .Replace("_x000A_", "\n", StringComparison.Ordinal);
                builder.Append(text);
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// Strips redundant outer powershell / powershell.exe / cmd /c invocations that models sometimes generate,
        /// avoiding nested quote escaping and backtick mangling.
        /// </summary>
        internal static string NormalizeCommand(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;

            string trimmed = command.Trim();

            // Match outer shell wrappers like:
            // powershell -Command "..."
            // powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "..."
            // powershell -c "..."
            // cmd /c "..."
            var match = Regex.Match(
                trimmed,
                @"^(?:powershell(?:\.exe)?(?:\s+-(?:NoProfile|NonInteractive|ExecutionPolicy\s+\w+))*\s+-(?:Command|c)\s+|cmd(?:\.exe)?\s+/c\s+)(.*)$",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (match.Success)
            {
                string remainder = match.Groups[1].Value.Trim();
                if ((remainder.StartsWith("\"") && remainder.EndsWith("\"")) || (remainder.StartsWith("'") && remainder.EndsWith("'")))
                {
                    if (remainder.Length >= 2)
                        remainder = remainder.Substring(1, remainder.Length - 2).Trim();
                }
                trimmed = remainder;
            }

            return trimmed;
        }

        internal static bool IsDirectoryAlreadyExistsScenario(string command, string stdout, string stderr)
        {
            string cmd = command.ToLowerInvariant();
            if (cmd.Contains("new-item") || cmd.Contains("mkdir") || cmd.Contains("md "))
            {
                string err = (stderr + " " + stdout).ToLowerInvariant();
                if (err.Contains("already exists") || err.Contains("directoryexist") || err.Contains("resourceexists"))
                {
                    return true;
                }
            }
            return false;
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The process already exited, or we lost the race with it. Nothing to recover.
            }
        }
    }
}
