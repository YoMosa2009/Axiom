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
        public const string EditFile = "edit_file";
        public const string ListDirectory = "list_directory";
        public const string FindFiles = "find_files";
        public const string SearchText = "search_text";
        public const string Finish = "finish";

        public static readonly IReadOnlyList<string> All =
        [
            RunCommand, ReadFile, WriteFile, EditFile, ListDirectory, FindFiles, SearchText, Finish
        ];

        /// <summary>Tools that only observe. Everything else can change the machine.</summary>
        public static readonly IReadOnlySet<string> ReadOnly =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ReadFile, ListDirectory, FindFiles, SearchText, Finish
            };

        public static bool IsReadOnly(string? tool) => tool != null && ReadOnly.Contains(tool);

        public static bool IsKnown(string? tool) =>
            tool != null && All.Any(name => string.Equals(name, tool, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>One tool invocation requested by the model.</summary>
    public sealed record AgentToolCall(string Tool, IReadOnlyDictionary<string, string> Arguments)
    {
        public string Arg(string key) =>
            Arguments.TryGetValue(key, out string? value) ? value ?? string.Empty : string.Empty;

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
                case AgentToolNames.EditFile:
                    return "Edited " + FileLabel(Arg("path"));
                case AgentToolNames.ListDirectory:
                    return "Listed " + FileLabel(Arg("path", "."));
                case AgentToolNames.FindFiles:
                    return "Searched for " + Summarize(Arg("pattern"), 28);
                case AgentToolNames.SearchText:
                    return "Searched " + Summarize(Arg("pattern"), 28);
                case AgentToolNames.Finish:
                    return "Finished";
                default:
                    return Summarize(Tool, 28);
            }
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

    /// <summary>The outcome of running one tool, as fed back to the model.</summary>
    public sealed record AgentToolResult(bool Succeeded, string Output, string? Error = null)
    {
        public static AgentToolResult Ok(string output) => new(true, output ?? string.Empty);
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
