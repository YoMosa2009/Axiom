using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.Agent
{
    /// <summary>
    /// Checks a finished run against the disk before its summary is accepted.
    /// </summary>
    /// <remarks>
    /// Models routinely end with a confident summary that lists a file they never wrote (the live
    /// "make me a website" run reported a script.js that did not exist, and its index.html linked
    /// to it). Terminal agents avoid this by verifying before they declare done; this is that
    /// check made deterministic: every problem it reports is a real, observable gap.
    /// </remarks>
    public static class AgentWorkVerifier
    {
        private static readonly Regex MentionedFileRegex = new(
            @"(?<![\w./\\-])(?<name>[A-Za-z0-9_][\w.-]*\.(?:html?|css|js|jsx|ts|tsx|json|py|cs|java|md|txt|xml|svg|sh|ps1|bat|yml|yaml))\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex HtmlLocalReferenceRegex = new(
            @"<(?:script|link|img|source|a)\b[^>]*?\b(?:src|href)\s*=\s*[""'](?<ref>[^""'#?]+)[""']",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Problems worth one more pass before finishing; empty when the work checks out.
        /// </summary>
        public static IReadOnlyList<string> FindProblems(
            string finalText,
            IEnumerable<AgentStep> steps,
            string workingDirectory)
        {
            var problems = new List<string>();
            var written = new List<string>();
            foreach (AgentStep step in steps)
            {
                if (step.Result?.Succeeded != true)
                    continue;

                string tool = step.Call.Tool.ToLowerInvariant();
                if (tool is not (AgentToolNames.WriteFile or AgentToolNames.AppendFile or AgentToolNames.EditFile))
                    continue;

                string? full = Resolve(step.Call.Arg("path"), workingDirectory);
                if (full != null && !written.Contains(full, StringComparer.OrdinalIgnoreCase))
                    written.Add(full);
            }

            if (written.Count == 0)
                return problems;

            // 1. Local files a written HTML page points at must exist.
            foreach (string page in written.Where(p => p.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".htm", StringComparison.OrdinalIgnoreCase)))
            {
                if (!File.Exists(page))
                    continue;

                string html = SafeRead(page);
                string pageDirectory = Path.GetDirectoryName(page) ?? workingDirectory;
                foreach (Match match in HtmlLocalReferenceRegex.Matches(html))
                {
                    string reference = match.Groups["ref"].Value.Trim();
                    if (!IsLocalFileReference(reference))
                        continue;

                    string? target = Resolve(reference, pageDirectory);
                    if (target != null && !File.Exists(target))
                        problems.Add($"{Path.GetFileName(page)} links to \"{reference}\", but that file does not exist.");
                }
            }

            // 2. Files the summary says were created must exist.
            var knownNames = new HashSet<string>(written.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
            foreach (Match match in MentionedFileRegex.Matches(finalText ?? string.Empty))
            {
                string name = match.Groups["name"].Value;
                if (knownNames.Contains(name))
                    continue;

                bool existsNearby = written
                    .Select(Path.GetDirectoryName)
                    .Append(workingDirectory)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Any(dir => !string.IsNullOrEmpty(dir) && File.Exists(Path.Combine(dir!, name)));
                if (!existsNearby && !problems.Any(p => p.Contains($"\"{name}\"", StringComparison.OrdinalIgnoreCase)))
                    problems.Add($"Your summary mentions {name}, but no such file was written.");
            }

            return problems.Distinct(StringComparer.Ordinal).Take(6).ToList();
        }

        private static bool IsLocalFileReference(string reference)
        {
            if (string.IsNullOrWhiteSpace(reference))
                return false;
            if (reference.Contains("://", StringComparison.Ordinal) || reference.StartsWith("//", StringComparison.Ordinal))
                return false;
            if (reference.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)
                || reference.StartsWith("tel:", StringComparison.OrdinalIgnoreCase)
                || reference.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || reference.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                return false;

            // Only references that name a file; "about/" style routes are left alone.
            return Path.HasExtension(reference);
        }

        private static string? Resolve(string path, string baseDirectory)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                string trimmed = path.Trim().Trim('"').TrimStart('/');
                return Path.IsPathRooted(path.Trim().Trim('"'))
                    ? Path.GetFullPath(path.Trim().Trim('"'))
                    : Path.GetFullPath(Path.Combine(baseDirectory, trimmed));
            }
            catch
            {
                return null;
            }
        }

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); }
            catch { return string.Empty; }
        }
    }
}
