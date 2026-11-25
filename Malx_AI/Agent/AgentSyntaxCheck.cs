using System;
using System.IO;
using System.Text.Json;
using System.Xml;

namespace Malx_AI.Agent
{
    /// <summary>
    /// A cheap, in-process syntax check run right after the agent writes or edits a file whose
    /// format can be parsed without tools (JSON, XML and its project/markup variants). Coding
    /// harnesses feed diagnostics back after each edit (OpenCode via LSP, Claude Code via hooks);
    /// this is the evidence-based part of that which needs no language server. A broken config
    /// file is reported on the step that broke it instead of when the build fails much later.
    /// Dependency-free so the tests can link it.
    /// </summary>
    public static class AgentSyntaxCheck
    {
        private static readonly string[] JsonExtensions = [".json", ".jsonc"];
        private static readonly string[] XmlExtensions = [".xml", ".csproj", ".vbproj", ".fsproj", ".props", ".targets", ".xaml", ".resx", ".config", ".svg", ".manifest", ".nuspec"];

        /// <summary>Returns a note to append to the tool result, or "" when the format is not checked or is valid.</summary>
        public static string Note(string path, string content)
        {
            string? problem = Problem(path, content);
            return problem == null
                ? ""
                : $"\n[Syntax check: {problem} If you are still adding parts with append_file, this can wait until the last part; otherwise fix it now.]";
        }

        internal static string? Problem(string path, string content)
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (Array.IndexOf(JsonExtensions, extension) >= 0)
            {
                // package.json, tsconfig.json and friends allow comments and trailing commas in
                // practice; only flag what no JSON reader would accept.
                try
                {
                    using var _ = JsonDocument.Parse(content, new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                    return null;
                }
                catch (JsonException ex)
                {
                    return $"{Path.GetFileName(path)} is not valid JSON (line {(ex.LineNumber ?? 0) + 1}): {FirstSentence(ex.Message)}";
                }
            }

            if (Array.IndexOf(XmlExtensions, extension) >= 0)
            {
                try
                {
                    var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
                    using var reader = XmlReader.Create(new StringReader(content), settings);
                    while (reader.Read()) { }
                    return null;
                }
                catch (XmlException ex)
                {
                    return $"{Path.GetFileName(path)} is not well-formed XML (line {ex.LineNumber}, column {ex.LinePosition}): {FirstSentence(ex.Message)}";
                }
            }

            return null;
        }

        private static string FirstSentence(string message)
        {
            int cut = message.IndexOf(". ", StringComparison.Ordinal);
            string text = cut > 0 ? message[..(cut + 1)] : message;
            return text.Length > 220 ? text[..220] + "…" : text;
        }
    }
}
