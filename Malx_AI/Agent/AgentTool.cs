using System;
using System.Collections.Generic;
using System.Linq;

namespace Malx_AI.Agent
{
    /// <summary>
    /// The tools the computer agent exposes. Deliberately the same small set the established
    /// coding agents settled on (shell, read, write, edit, list, glob, grep): a model that has
    /// seen any of them in training recognises this shape, and a small set is far easier for a
    /// compact local model to route than a large one.
    /// </summary>
    public static class AgentToolNames
    {
        public const string RunCommand = "run_command";
        public const string ReadFile = "read_file";
        public const string WriteFile = "write_file";
        public const string AppendFile = "append_file";
        public const string EditFile = "edit_file";
        public const string ListDirectory = "list_directory";
        public const string FindFiles = "find_files";
        public const string SearchText = "search_text";
        public const string StartBackgroundTask = "start_background_task";
        public const string CheckBackgroundTask = "check_background_task";
        public const string StopBackgroundTask = "stop_background_task";
        public const string BrowserControl = "browser_control";
        public const string TakeScreenshot = "take_screenshot";
        public const string Finish = "finish";

        public static readonly IReadOnlyList<string> All =
        [
            RunCommand,
            ReadFile,
            WriteFile,
            AppendFile,
            EditFile,
            ListDirectory,
            FindFiles,
            SearchText,
            StartBackgroundTask,
            CheckBackgroundTask,
            StopBackgroundTask,
            BrowserControl,
            TakeScreenshot,
            Finish
        ];

        /// <summary>Tools that only observe. Everything else can change the machine.</summary>
        public static readonly IReadOnlySet<string> ReadOnly =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ReadFile, ListDirectory, FindFiles, SearchText, CheckBackgroundTask, TakeScreenshot, Finish
            };

        public static bool IsReadOnly(string? tool) => tool != null && ReadOnly.Contains(tool);

        public static bool IsKnown(string? tool) =>
            tool != null && All.Any(name => string.Equals(name, tool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One tool invocation requested by the model.</summary>
    public sealed record AgentToolCall(string Tool, IReadOnlyDictionary<string, string> Arguments)
    {
        public string Arg(string key)
        {
            if (Arguments.TryGetValue(key, out string? exact) && !string.IsNullOrWhiteSpace(exact))
                return exact;

            // Common aliases emitted across various model sizes and providers
            string[]? aliases = key.ToLowerInvariant() switch
            {
                "path" => ["file", "filepath", "file_path", "filename", "file_name", "target", "directory", "dir"],
                "command" => ["cmd", "run", "script", "shell_command"],
                "pattern" => ["query", "search", "text", "term", "filter"],
                "old_string" => ["old_text", "oldtext", "old", "search", "target"],
                "new_string" => ["new_text", "newtext", "new", "replace", "replacement"],
                "content" => ["text", "body", "code", "file_content"],
                "start_line" => ["startline", "start", "line_start"],
                "line_count" => ["linecount", "lines", "count"],
                "file_pattern" => ["filepattern", "glob", "filter"],
                "summary" => ["result", "message", "final_text"],
                "wake_seconds" => ["wakeseconds", "wake_after_seconds", "wait_seconds", "duration", "sleep_seconds"],
                "wait" => ["sleep", "pause", "wait_for_wake"],
                "task_id" => ["taskid", "id"],
                "action" => ["operation", "mode", "subcommand"],
                "url" => ["target", "query", "address", "uri", "package"],
                "destination" => ["dest", "output_path", "save_path"],
                "save_path" => ["path", "destination", "dest", "output_path"],
                "delay_ms" => ["delay", "wait_ms"],
                "window" => ["window_title", "app", "application", "process"],
                "background" => ["bg", "async"],
                "title" => ["label", "name"],
                "cwd" => ["working_directory", "workdir", "dir"],
                _ => null
            };

            if (aliases != null)
            {
                foreach (string alias in aliases)
                {
                    if (Arguments.TryGetValue(alias, out string? value) && !string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return Arguments.TryGetValue(key, out string? fallbackVal) ? (fallbackVal ?? string.Empty) : string.Empty;
        }

        public string Arg(string key, string fallback)
        {
            string value = Arg(key);
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        /// <summary>
        /// The short label the running-activity line shows, e.g. "Ran npm test" or "Read App.xaml".
        /// Kept to a few words: it appears as one quiet line under the message, not a log.
        /// </summary>
        public string DescribeShort()
        {
            switch (Tool.ToLowerInvariant())
            {
                case AgentToolNames.RunCommand:
                    return "Ran " + Summarize(Arg("command"), 40);
                case AgentToolNames.ReadFile:
                    return "Read " + FileLabel(Arg("path"));
                case AgentToolNames.WriteFile:
                    return "Wrote " + FileLabel(Arg("path"));
                case AgentToolNames.AppendFile:
                    return "Added to " + FileLabel(Arg("path"));
                case AgentToolNames.EditFile:
                    return "Edited " + FileLabel(Arg("path"));
                case AgentToolNames.ListDirectory:
                    return "Listed " + FileLabel(Arg("path", "."));
                case AgentToolNames.FindFiles:
                    return "Searched for " + Summarize(Arg("pattern"), 28);
                case AgentToolNames.SearchText:
                    return "Searched " + Summarize(Arg("pattern"), 28);
                case AgentToolNames.StartBackgroundTask:
                    return "Running background task: " + Summarize(Arg("title", Arg("command")), 34);
                case AgentToolNames.CheckBackgroundTask:
                    return string.IsNullOrWhiteSpace(Arg("task_id"))
                        ? "Checking background tasks"
                        : "Waiting on background task " + Summarize(Arg("task_id"), 18);
                case AgentToolNames.StopBackgroundTask:
                    return "Stopping background task " + Summarize(Arg("task_id"), 18);
                case AgentToolNames.BrowserControl:
                    return "Browser (" + Arg("action", "open") + "): " + Summarize(Arg("url"), 26);
                case AgentToolNames.TakeScreenshot:
                    return "Took a screenshot";
                case AgentToolNames.Finish:
                    return "Finished";
                default:
                    return Summarize(Tool, 28);
            }
        }

        /// <summary>
        /// The call as a model should read it back in a text transcript: the tool name and its
        /// arguments, with full paths and commands, but without file bodies.
        /// </summary>
        /// <remarks>
        /// <see cref="DescribeShort"/> is a UI label: 40 characters and a bare file name. Read back
        /// by a local model it showed "Ran Set-Content -Path hello.py -Value 'for…", so the model
        /// could not tell what it had already run or where it had written.
        /// </remarks>
        public string DescribeForModel(int maxValueChars = 300)
        {
            var parts = new List<string>();
            foreach ((string key, string value) in Arguments)
            {
                if (string.IsNullOrWhiteSpace(value))
                    continue;
                if (key is "content" or "old_string" or "new_string")
                {
                    int lines = value.Split('\n').Length;
                    parts.Add($"{key}: ({lines} line{(lines == 1 ? "" : "s")}, {value.Length} chars)");
                    continue;
                }

                // Only line breaks are flattened: the spacing inside a command is part of it.
                string flat = string.Join(" ", value.Trim().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
                parts.Add($"{key}: {(flat.Length <= maxValueChars ? flat : flat[..maxValueChars] + "…")}");
            }

            return parts.Count == 0 ? Tool : Tool + " (" + string.Join("; ", parts) + ")";
        }

        private static string FileLabel(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return "a file";
            string trimmed = path.Replace('\\', '/').TrimEnd('/');
            int slash = trimmed.LastIndexOf('/');
            string name = slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : trimmed;
            return Summarize(name, 32);
        }

        private static string Summarize(string value, int max)
        {
            string flat = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
            while (flat.Contains("  ", StringComparison.Ordinal))
                flat = flat.Replace("  ", " ", StringComparison.Ordinal);
            if (flat.Length == 0)
                return "a command";
            return flat.Length <= max ? flat : flat[..(max - 1)].TrimEnd() + "…";
        }
    }

    /// <summary>
    /// One screenshot taken for the agent, with a plain statement of what it shows.
    /// </summary>
    /// <param name="Description">What was captured, e.g. "the window 'Rocket - Edge' (msedge)".</param>
    /// <param name="OpenWindows">Other windows that could be asked for, as "process: title".</param>
    public sealed record AgentScreenshot(
        byte[] PngBytes,
        int Width,
        int Height,
        string Description,
        IReadOnlyList<string> OpenWindows)
    {
        public static string DescribeOpenWindows(IReadOnlyList<string>? windows)
        {
            if (windows == null || windows.Count == 0)
                return string.Empty;
            return "Open windows: " + string.Join("; ", windows.Take(8).Select(window => window.Length <= 70 ? window : window[..70] + "…")) + ".";
        }
    }

    /// <summary>The outcome of running one tool, as fed back to the model.</summary>
    public sealed record AgentToolResult(bool Succeeded, string Output, string? Error = null, string? ImageDataUrl = null)
    {
        public static AgentToolResult Ok(string output, string? imageDataUrl = null) => new(true, output ?? string.Empty, null, imageDataUrl);
        public static AgentToolResult Fail(string error) => new(false, string.Empty, error);

        /// <summary>Renders the result as the observation text appended to the transcript.</summary>
        public string ToObservation(int maxChars = 6000)
        {
            string body = Succeeded ? Output : "ERROR: " + (Error ?? "the tool failed.");
            if (string.IsNullOrWhiteSpace(body))
                body = Succeeded ? "(no output)" : "ERROR: the tool failed.";
            return body.Length <= maxChars
                ? body
                : body[..maxChars] + $"\n… output truncated at {maxChars} characters.";
        }
    }
}
