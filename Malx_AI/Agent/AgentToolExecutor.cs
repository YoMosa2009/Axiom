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
        private const int MaxReadChars = 60000;
        private const int DefaultTimeoutSeconds = 120;

        private readonly AgentScope _scope;
        private readonly AgentFileBackup? _backup;

        public AgentToolExecutor(AgentScope scope, AgentFileBackup? backup = null)
        {
            _scope = scope ?? AgentScope.WholeComputer();
            _backup = backup;
        }

        /// <summary>Copies a file that already existed before its first change; says where, if it did.</summary>
        private string BackupNote(string path)
        {
            string? copy = _backup?.BeforeChange(path);
            return copy == null ? string.Empty : $" The previous version was saved to {copy}.";
        }

        public async Task<AgentToolResult> ExecuteAsync(AgentToolCall call, CancellationToken token)
        {
            try
            {
                return call.Tool.ToLowerInvariant() switch
                {
                    AgentToolNames.RunCommand => await RunCommandAsync(call, token),
                    AgentToolNames.ReadFile => ReadFile(call),
                    AgentToolNames.WriteFile => WriteFile(call),
                    AgentToolNames.AppendFile => AppendFile(call),
                    AgentToolNames.EditFile => EditFile(call),
                    AgentToolNames.ListDirectory => ListDirectory(call),
                    AgentToolNames.FindFiles => FindFiles(call),
                    AgentToolNames.SearchText => SearchText(call),
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

            string workingDirectory = ResolveWorkingDirectory(call.Arg("cwd"));
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

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) => { if (e.Data != null) stdout.AppendLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) stderr.AppendLine(e.Data); };

            var stopwatch = Stopwatch.StartNew();
            if (!process.Start())
                return AgentToolResult.Fail("The command could not be started.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            try
            {
                await process.WaitForExitAsync(timeout.Token);
                process.WaitForExit();
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                TryKill(process);
                stopwatch.Stop();
                return AgentToolResult.Fail(
                    $"The command timed out after {timeoutSeconds}s ({stopwatch.ElapsedMilliseconds}ms) and was stopped.\n"
                    + Combine(stdout, stderr));
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            stopwatch.Stop();
            long elapsedMs = stopwatch.ElapsedMilliseconds;

            string cleanedError = CleanPowerShellErrorStream(stderr.ToString());
            stderr.Clear().Append(cleanedError);
            string combined = Combine(stdout, stderr);
            string exitPrefix = $"[Exit code: {process.ExitCode} | Duration: {elapsedMs}ms]";

            if (process.ExitCode == 0)
            {
                string body = string.IsNullOrWhiteSpace(combined)
                    ? "(Command completed successfully with no standard output)"
                    : combined;
                return AgentToolResult.Ok($"{exitPrefix}\n{body}");
            }
            else if (IsDirectoryAlreadyExistsScenario(command, stdout.ToString(), stderr.ToString()))
            {
                string body = $"Directory already exists and is ready for use.\n{combined}".Trim();
                return AgentToolResult.Ok($"[Exit code: 0 (Directory already exists) | Duration: {elapsedMs}ms]\n{body}");
            }
            else
            {
                string body = string.IsNullOrWhiteSpace(combined)
                    ? "(Command failed with no output)"
                    : combined;
                return AgentToolResult.Fail($"{exitPrefix}\n{body}{ShellMismatchHint(command)}");
            }
        }

        private AgentToolResult ReadFile(AgentToolCall call)
        {
            if (!TryResolve(call.Arg("path"), out string path, out string error))
                return AgentToolResult.Fail(error);
            if (!File.Exists(path))
                return AgentToolResult.Fail($"No such file: {path}");

            string[] lines = File.ReadAllLines(path);
            int start = int.TryParse(call.Arg("start_line"), out int s) && s > 0 ? s : 1;
            int count = int.TryParse(call.Arg("line_count"), out int c) && c > 0 ? c : lines.Length;

            IEnumerable<string> slice = lines.Skip(start - 1).Take(count);
            var builder = new StringBuilder();
            int number = start;
            foreach (string line in slice)
            {
                builder.Append(number++).Append('\t').AppendLine(line);
                if (builder.Length > MaxReadChars)
                {
                    builder.AppendLine($"… truncated at {MaxReadChars} characters. Read a line range to see more.");
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
            int lineCount = content.Split('\n').Length;
            return AgentToolResult.Ok($"Successfully wrote {fileInfo.Length:N0} bytes ({lineCount} lines) to {path}. Verified on disk.{backupNote}");
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
            int lineCount = File.ReadAllText(path).Split('\n').Length;
            return AgentToolResult.Ok($"Appended {content.Length:N0} characters. {path} is now {fileInfo.Length:N0} bytes ({lineCount} lines).{backupNote}");
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
            int occurrences = CountOccurrences(original, oldString);
            if (occurrences == 0)
            {
                // Try normalizing line endings to match the file's conventions
                string normalizedOldCrlf = oldString.Replace("\r\n", "\n").Replace("\n", "\r\n");
                if (CountOccurrences(original, normalizedOldCrlf) == 1)
                {
                    oldString = normalizedOldCrlf;
                    if (original.Contains("\r\n"))
                        newString = newString.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    occurrences = 1;
                }
                else
                {
                    string normalizedOldLf = oldString.Replace("\r\n", "\n");
                    if (CountOccurrences(original, normalizedOldLf) == 1)
                    {
                        oldString = normalizedOldLf;
                        if (!original.Contains("\r\n"))
                            newString = newString.Replace("\r\n", "\n");
                        occurrences = 1;
                    }
                }
            }

            if (occurrences == 0)
                return AgentToolResult.Fail("old_string was not found in the file. Read the file and copy the exact text.");
            if (occurrences > 1)
                return AgentToolResult.Fail($"old_string appears {occurrences} times. Include more surrounding lines so it is unique.");

            string replaced = original.Replace(oldString, newString, StringComparison.Ordinal);
            string backupNote = BackupNote(path);
            File.WriteAllText(path, replaced, new UTF8Encoding(false));
            var fileInfo = new FileInfo(path);
            int lineCount = replaced.Split('\n').Length;
            return AgentToolResult.Ok($"Successfully edited {path} ({fileInfo.Length:N0} bytes, {lineCount} lines). Verified on disk.{backupNote}");
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
            if (!Directory.Exists(root))
                return AgentToolResult.Fail($"No such directory: {root}");

            string glob = call.Arg("file_pattern", "*");
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MaxRecursionDepth = 12
            };

            var builder = new StringBuilder();
            int hits = 0;
            foreach (string file in Directory.EnumerateFiles(root, glob, options))
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

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }

        private static string Combine(StringBuilder stdout, StringBuilder stderr)
        {
            var builder = new StringBuilder();
            builder.Append(stdout);
            if (stderr.Length > 0)
                builder.AppendLine().Append("[stderr]").AppendLine().Append(stderr);
            string text = builder.ToString().TrimEnd();
            return text.Length <= MaxOutputChars
                ? text
                : text[..MaxOutputChars] + $"\n… output truncated at {MaxOutputChars} characters.";
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
