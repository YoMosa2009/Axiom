using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Malx_AI.Agent
{
    /// <summary>
    /// The agent's tools as provider function-calling definitions.
    /// </summary>
    /// <remarks>
    /// A cloud model given real tool definitions calls them; the same model given tool
    /// instructions buried in prose alongside a different set of native tools ignores the prose
    /// and uses the native ones. That was the original failure: the agent asked for
    /// <c>run_command</c> in text while the transport advertised web_search and run_python, so the
    /// model answered "I have no access to your computer" and never attempted a call.
    /// </remarks>
    public static class AgentToolSchemas
    {
        public static IReadOnlyList<OpenRouterToolDefinition> All() =>
        [
            Define(
                AgentToolNames.RunCommand,
                "Run a shell command on the user's Windows computer through PowerShell and return its output. "
                + "Use this for anything the other tools do not cover: launching programs, git, build tools, package managers, system queries.",
                ("command", "string", "The exact command line to run.", true),
                ("cwd", "string", "Directory to run in. Defaults to the agent's working folder.", false),
                ("timeout_seconds", "integer", "Seconds to allow before the command is stopped. Default 120.", false)),

            Define(
                AgentToolNames.ReadFile,
                "Read a text file from the user's computer. Returns the contents with line numbers.",
                ("path", "string", "Absolute or relative path to the file.", true),
                ("start_line", "integer", "First line to return, 1-based.", false),
                ("line_count", "integer", "How many lines to return.", false)),

            Define(
                AgentToolNames.WriteFile,
                "Create a file, or replace an existing file's entire contents.",
                ("path", "string", "Absolute or relative path to the file.", true),
                ("content", "string", "The complete file contents.", true)),

            Define(
                AgentToolNames.EditFile,
                "Replace one exact piece of text inside an existing file. Read the file first and copy the text precisely. "
                + "old_string must appear exactly once; include surrounding lines to make it unique.",
                ("path", "string", "Absolute or relative path to the file.", true),
                ("old_string", "string", "The exact text to replace.", true),
                ("new_string", "string", "The replacement text.", true)),

            Define(
                AgentToolNames.ListDirectory,
                "List the files and folders in a directory.",
                ("path", "string", "Directory to list. Defaults to the working folder.", false)),

            Define(
                AgentToolNames.FindFiles,
                "Find files by name pattern, searching subfolders.",
                ("pattern", "string", "A filename pattern such as *.cs or appsettings*.json.", true),
                ("path", "string", "Directory to search from. Defaults to the working folder.", false)),

            Define(
                AgentToolNames.SearchText,
                "Search file contents for a piece of text and return the matching lines with their file and line number.",
                ("pattern", "string", "The text to search for.", true),
                ("path", "string", "Directory to search from. Defaults to the working folder.", false),
                ("file_pattern", "string", "Restrict to files matching this name pattern, e.g. *.cs.", false)),
        ];

        private static OpenRouterToolDefinition Define(
            string name,
            string description,
            params (string Name, string Type, string Description, bool Required)[] parameters)
        {
            var properties = new JsonObject();
            var required = new JsonArray();

            foreach ((string parameterName, string type, string parameterDescription, bool isRequired) in parameters)
            {
                properties[parameterName] = new JsonObject
                {
                    ["type"] = type,
                    ["description"] = parameterDescription
                };
                if (isRequired)
                    required.Add(parameterName);
            }

            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = required
            };

            return new OpenRouterToolDefinition(name, description, schema);
        }
    }
}
