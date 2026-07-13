using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public enum WorkspaceAgentMode
    {
        Local,
        Cloud
    }

    public enum WorkspaceConnectionKind
    {
        None,
        Folder,
        Files,
        GitRepository
    }

    public sealed class ConnectedWorkspaceState
    {
        public bool CodebaseEditAccessEnabled { get; set; }
        public bool AutoApplyCodebaseChanges { get; set; }
        public string LockedMode { get; set; } = WorkspaceAgentMode.Local.ToString();
        public string ConnectionKind { get; set; } = WorkspaceConnectionKind.None.ToString();
        public string RootPath { get; set; } = "";
        public string RepositoryUrl { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int IndexedFileCount { get; set; }
        public long IndexedByteCount { get; set; }
        public DateTime EnabledAt { get; set; }
        public DateTime IndexedAt { get; set; }
        public string StatusMessage { get; set; } = "Codebase edit access is off.";
        public List<string> ConnectedFiles { get; set; } = new();

        // Relative paths of files changed by applied patches in this chat, most recent first.
        // Follow-up prompts rarely re-name the file they are iterating on ("make the button
        // bigger"), so these files get the same context priority and per-file character budget
        // as prompt-named files — otherwise the Builder sees a truncated view of the code it
        // just wrote and produces blind (often no-op) patches on turn 2+.
        public List<string> RecentlyChangedFiles { get; set; } = new();
    }

    public sealed record WorkspaceFileEntry(
        string RelativePath,
        long Length,
        DateTime LastWriteTime);

    public sealed record WorkspaceIndexResult(
        string RootPath,
        string DisplayName,
        IReadOnlyList<WorkspaceFileEntry> Files,
        long TotalBytes);

    public sealed record WorkspaceContextResult(
        string Packet,
        IReadOnlyList<string> FilesRead);

    public sealed record WorkspaceCloneResult(
        string LocalPath,
        string Output);

    public sealed record WorkspacePatchEditBlock(
        string Search,
        string Replace);

    public sealed record WorkspaceFilePatch(
        string RelativePath,
        string Action,
        string Content,
        IReadOnlyList<WorkspacePatchEditBlock>? EditBlocks = null)
    {
        public IReadOnlyList<WorkspacePatchEditBlock> Blocks => EditBlocks ?? Array.Empty<WorkspacePatchEditBlock>();
    }

    public sealed record WorkspacePatchProposal(
        IReadOnlyList<WorkspaceFilePatch> Files,
        string RawText);

    public sealed record WorkspacePatchApplyResult(
        IReadOnlyList<string> ChangedFiles,
        string Summary);

    public sealed record WorkspaceGitStatus(
        bool IsRepository,
        string Branch,
        int ChangedFileCount,
        string ShortStatus,
        string Error);

    public sealed record WorkspaceGitCheckpointResult(
        bool IsRepository,
        bool Attempted,
        bool Created,
        bool Success,
        string Summary);

    public sealed record WorkspaceReadToolResult(
        bool Success,
        string Output);

    public sealed class WorkspaceAccessService
    {
        private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".vs",
            ".idea",
            ".vscode",
            "bin",
            "obj",
            "node_modules",
            "packages",
            "dist",
            "build",
            "coverage",
            ".next",
            ".nuxt",
            ".turbo"
        };

        private static readonly HashSet<string> IgnoredFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".DS_Store",
            "Thumbs.db"
        };

        private static readonly HashSet<string> IgnoredExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll",
            ".exe",
            ".pdb",
            ".bin",
            ".obj",
            ".cache",
            ".png",
            ".jpg",
            ".jpeg",
            ".gif",
            ".webp",
            ".ico",
            ".pdf",
            ".zip",
            ".7z",
            ".rar",
            ".bak",
            ".backup",
            ".old"
        };

        private const int MaxIndexedFiles = 5000;
        private const int MaxContextFiles = 8;
        private const int MaxContextCharsPerFile = 8000;
        private const int MaxPromptNamedContextCharsPerFile = 24000;
        private const int MaxPatchFileChars = 1_000_000;
        private const int MaxToolReadChars = 24000;
        private const int MaxToolSearchOutputChars = 24000;
        private const int MaxToolListOutputChars = 16000;

        private static readonly Regex PatchEnvelopeRegex = new(
            @"\[\[AXIOM_CODEBASE_PATCH\]\](?<body>[\s\S]*?)\[\[END AXIOM_CODEBASE_PATCH\]\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PatchFileRegex = new(
            @"FILE:\s*(?<path>[^\r\n]+)\s+ACTION:\s*(?<action>[^\r\n]+)\s+(?<fence>`{3,})[^\r\n]*\r?\n(?<content>[\s\S]*?)\r?\n\k<fence>(?:\s*\[\[END FILE\]\])?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PatchFileHeaderRegex = new(
            @"(?im)^\s*FILE:\s*(?<path>[^\r\n]+)\s*\r?\n\s*ACTION:\s*(?<action>[^\r\n]+)\s*\r?\n",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Marker runs are tolerant ({4,9}) instead of demanding exactly 7 characters: models
        // regularly emit 6 or 8 and an off-by-one there must not invalidate a correct edit.
        private static readonly Regex PatchEditBlockRegex = new(
            @"(?ms)^\s*<{4,9}\s*SEARCH\s*\r?\n(?<search>[\s\S]*?)\r?\n^={4,9}\s*\r?\n(?<replace>[\s\S]*?)\r?\n^>{4,9}\s*REPLACE\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public WorkspaceIndexResult IndexWorkspace(string rootPath)
        {
            string normalizedRoot = NormalizeRoot(rootPath);
            var files = new List<WorkspaceFileEntry>();
            long totalBytes = 0;

            foreach (string file in EnumerateCandidateFiles(normalizedRoot))
            {
                if (files.Count >= MaxIndexedFiles)
                    break;

                var info = new FileInfo(file);
                string relativePath = Path.GetRelativePath(normalizedRoot, info.FullName);
                files.Add(new WorkspaceFileEntry(
                    relativePath.Replace('\\', '/'),
                    info.Length,
                    info.LastWriteTime));
                totalBytes += info.Length;
            }

            return new WorkspaceIndexResult(
                normalizedRoot,
                new DirectoryInfo(normalizedRoot).Name,
                files,
                totalBytes);
        }

        public WorkspaceIndexResult IndexFiles(IReadOnlyList<string> filePaths)
        {
            if (filePaths == null || filePaths.Count == 0)
                throw new ArgumentException("No files were selected.", nameof(filePaths));

            var files = new List<WorkspaceFileEntry>();
            long totalBytes = 0;
            string root = Path.GetDirectoryName(Path.GetFullPath(filePaths[0])) ?? Environment.CurrentDirectory;

            foreach (string path in filePaths.Where(File.Exists).Take(MaxIndexedFiles))
            {
                var info = new FileInfo(path);
                string relativePath = Path.GetFileName(info.FullName);
                files.Add(new WorkspaceFileEntry(relativePath, info.Length, info.LastWriteTime));
                totalBytes += info.Length;
            }

            if (files.Count == 0)
                throw new FileNotFoundException("None of the selected files could be read.");

            return new WorkspaceIndexResult(
                root,
                files.Count == 1 ? files[0].RelativePath : $"{files.Count} selected files",
                files,
                totalBytes);
        }

        public WorkspaceContextResult BuildContextPacket(ConnectedWorkspaceState state, string query, int maxChars)
        {
            if (state == null || !state.CodebaseEditAccessEnabled)
                return new WorkspaceContextResult(string.Empty, Array.Empty<string>());

            var files = ResolveCandidateFiles(state).ToList();
            if (files.Count == 0)
                return new WorkspaceContextResult(BuildUnavailablePacket(state), Array.Empty<string>());

            var selected = SelectPromptAwareRelevantFiles(files, state, query, MaxContextFiles).ToList();
            // Prompt-named files and files changed by prior applied patches share priority: both
            // are what the user is iterating on, and both need enough of their CURRENT content
            // visible for SEARCH/REPLACE anchors to be written against real text.
            var promptNamedReferences = ExtractMentionedWorkspaceFileReferences(query)
                .Concat(NormalizeRecentlyChangedReferences(state))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var readFiles = new List<string>();
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("The user enabled Codebase Edit Access for this Workplace chat.");
            sb.AppendLine($"Locked model mode: {state.LockedMode}");
            sb.AppendLine($"Connection: {state.ConnectionKind}");
            sb.AppendLine($"Auto apply: {(state.AutoApplyCodebaseChanges ? "enabled" : "disabled")}");
            if (!string.IsNullOrWhiteSpace(state.RepositoryUrl))
                sb.AppendLine($"Repository URL: {state.RepositoryUrl}");
            if (!string.IsNullOrWhiteSpace(state.RootPath))
                sb.AppendLine($"Local root: {state.RootPath}");
            sb.AppendLine(state.AutoApplyCodebaseChanges
                ? "Current capability: read/search context plus host-side auto-apply after a valid patch. Do not claim files were changed until the app reports the patch was applied."
                : "Current capability: read/search context only. Do not claim files were changed. If code changes are needed, propose them for review.");
            sb.AppendLine("When proposing codebase changes, output this exact structured patch envelope:");
            sb.AppendLine("[[AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("FILE: relative/path/from/workspace.ext");
            sb.AppendLine("ACTION: edit");
            sb.AppendLine("<<<<<<< SEARCH");
            sb.AppendLine("exact existing text to replace");
            sb.AppendLine("=======");
            sb.AppendLine("replacement text");
            sb.AppendLine(">>>>>>> REPLACE");
            sb.AppendLine("[[END FILE]]");
            sb.AppendLine("[[END AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("Prefer ACTION: edit for existing files. Each SEARCH block must match exactly once in the current file.");
            sb.AppendLine("Use ACTION: replace only when a full-file replacement is small and necessary:");
            sb.AppendLine("[[AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("FILE: relative/path/from/workspace.ext");
            sb.AppendLine("ACTION: replace");
            sb.AppendLine("```language");
            sb.AppendLine("complete replacement file content");
            sb.AppendLine("```");
            sb.AppendLine("[[END FILE]]");
            sb.AppendLine("[[END AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("Use ACTION: create only for new files. Do not use delete actions. Do not output partial replacement files.");
            sb.AppendLine("Use the exact connected workspace path for the target file. Do not rename file extensions.");
            sb.AppendLine();
            sb.AppendLine("RELEVANT FILES:");

            int remaining = Math.Max(1200, maxChars);
            // Per-file caps scale with the packet budget. The fixed 8k/24k caps were sized for
            // small local-model windows; a cloud run hands this packet a 60k budget, and follow-up
            // edits fail when the file being iterated on is truncated below its interesting parts.
            int standardFileCap = Math.Clamp(maxChars / 4, MaxContextCharsPerFile, 16000);
            int priorityFileCap = Math.Clamp(maxChars / 2, MaxPromptNamedContextCharsPerFile, 40000);
            foreach (string path in selected)
            {
                if (remaining <= 0)
                    break;

                string content;
                try
                {
                    content = File.ReadAllText(path);
                }
                catch
                {
                    continue;
                }

                string relative = GetDisplayPath(state, path);
                bool promptNamedFile = IsPromptNamedContextFile(relative, path, promptNamedReferences);
                int fileCharLimit = promptNamedFile ? priorityFileCap : standardFileCap;
                string capped = content.Length > fileCharLimit
                    ? content[..fileCharLimit] + "\n[...file truncated for context budget]"
                    : content;

                string block = $"--- {relative} ({content.Length:n0} chars) ---\n{capped}\n";
                if (block.Length > remaining)
                    block = block[..remaining] + "\n[...workspace context budget exhausted]\n";

                sb.AppendLine(block);
                readFiles.Add(relative);
                remaining -= block.Length;
            }

            return new WorkspaceContextResult(sb.ToString().Trim(), readFiles);
        }

        public WorkspaceReadToolResult ReadFileForTool(ConnectedWorkspaceState state, string relativePath, int maxChars = MaxToolReadChars)
        {
            if (!TryResolveReadableToolTarget(state, relativePath, out string target, out string error))
                return new WorkspaceReadToolResult(false, error);

            try
            {
                string content = File.ReadAllText(target);
                string displayPath = GetDisplayPath(state, target);
                int cappedMax = Math.Clamp(maxChars, 1000, MaxToolReadChars);
                string visible = content.Length > cappedMax
                    ? content[..cappedMax] + "\n[...file truncated for tool result]"
                    : content;

                return new WorkspaceReadToolResult(
                    true,
                    $"READ_FILE result: {displayPath} ({content.Length:n0} chars)\n---\n{visible}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new WorkspaceReadToolResult(false, $"Could not read file: {ex.Message}");
            }
        }

        public WorkspaceReadToolResult SearchCodebaseForTool(ConnectedWorkspaceState state, string query, int maxMatches = 40)
        {
            if (!TryGetReadableToolFiles(state, out IReadOnlyList<string> files, out string error))
                return new WorkspaceReadToolResult(false, error);

            string normalizedQuery = (query ?? string.Empty).Trim();
            if (normalizedQuery.Length == 0)
                return new WorkspaceReadToolResult(false, "Search query is empty.");

            var terms = ExtractQueryTerms(normalizedQuery);
            var matches = new List<WorkspaceSearchMatch>();
            foreach (string file in files)
            {
                string display = GetDisplayPath(state, file);
                bool pathMatch = display.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                    || terms.Any(term => display.Contains(term, StringComparison.OrdinalIgnoreCase));
                if (pathMatch)
                {
                    matches.Add(new WorkspaceSearchMatch(display, 0, "[path match]", 20 + ScoreFile(file, terms)));
                }

                string[] lines;
                try
                {
                    lines = File.ReadAllLines(file);
                }
                catch
                {
                    continue;
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i];
                    int termHits = terms.Count(term => line.Contains(term, StringComparison.OrdinalIgnoreCase));
                    bool exactHit = normalizedQuery.Length >= 3 && line.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase);
                    if (!exactHit && termHits == 0)
                        continue;

                    int score = (exactHit ? 30 : 0) + (termHits * 6) + ScoreFile(file, terms);
                    matches.Add(new WorkspaceSearchMatch(display, i + 1, TrimToolLine(line), score));
                }
            }

            var selected = matches
                .OrderByDescending(match => match.Score)
                .ThenBy(match => match.RelativePath)
                .ThenBy(match => match.LineNumber)
                .Take(Math.Clamp(maxMatches, 1, 80))
                .ToList();

            if (selected.Count == 0)
                return new WorkspaceReadToolResult(false, $"No codebase matches found for: {normalizedQuery}");

            var sb = new StringBuilder();
            sb.AppendLine($"SEARCH_CODEBASE results for: {normalizedQuery}");
            foreach (WorkspaceSearchMatch match in selected)
            {
                string location = match.LineNumber > 0
                    ? $"{match.RelativePath}:{match.LineNumber}"
                    : match.RelativePath;
                sb.AppendLine($"- {location}: {match.Snippet}");
                if (sb.Length >= MaxToolSearchOutputChars)
                {
                    sb.AppendLine("[...search result truncated]");
                    break;
                }
            }

            return new WorkspaceReadToolResult(true, sb.ToString().Trim());
        }

        public WorkspaceReadToolResult ListFilesForTool(ConnectedWorkspaceState state, string query, int maxFiles = 200)
        {
            if (!TryGetReadableToolFiles(state, out IReadOnlyList<string> files, out string error))
                return new WorkspaceReadToolResult(false, error);

            string normalizedQuery = (query ?? string.Empty).Trim();
            var terms = ExtractQueryTerms(normalizedQuery);
            IEnumerable<string> filtered = files;
            if (!string.IsNullOrWhiteSpace(normalizedQuery))
            {
                filtered = filtered.Where(path =>
                {
                    string display = GetDisplayPath(state, path);
                    return display.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                        || terms.Any(term => display.Contains(term, StringComparison.OrdinalIgnoreCase));
                });
            }

            var selected = filtered
                .Select(path => new FileInfo(path))
                .OrderBy(info => GetDisplayPath(state, info.FullName))
                .Take(Math.Clamp(maxFiles, 1, 500))
                .ToList();

            if (selected.Count == 0)
                return new WorkspaceReadToolResult(false, string.IsNullOrWhiteSpace(normalizedQuery)
                    ? "No readable files are connected."
                    : $"No readable files matched: {normalizedQuery}");

            var sb = new StringBuilder();
            sb.AppendLine(string.IsNullOrWhiteSpace(normalizedQuery)
                ? "LIST_FILES result:"
                : $"LIST_FILES result for: {normalizedQuery}");
            foreach (FileInfo info in selected)
            {
                sb.AppendLine($"- {GetDisplayPath(state, info.FullName)} ({info.Length:n0} bytes)");
                if (sb.Length >= MaxToolListOutputChars)
                {
                    sb.AppendLine("[...file list truncated]");
                    break;
                }
            }

            return new WorkspaceReadToolResult(true, sb.ToString().Trim());
        }

        public bool TryParsePatchProposal(string text, out WorkspacePatchProposal? proposal, out string error)
        {
            proposal = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            // CLOSED envelopes (both sentinels present) are an explicit output contract even when
            // narration precedes them — cloud models often lead with a sentence despite the
            // "first visible line" rule, and rejecting the whole valid patch for that turns every
            // follow-up edit into a failure. Garbage stays out because the file sections are
            // strictly structured (FILE/ACTION headers, SEARCH/REPLACE blocks with no leftover
            // text) and every patch still passes path/materialize/no-op validation before apply.
            // Envelopes are tried LAST-first: deliberation sometimes quotes the contract's example
            // envelope before emitting the real one, and the deliverable is the final envelope.
            MatchCollection envelopes = PatchEnvelopeRegex.Matches(text);
            if (envelopes.Count > 0)
            {
                string firstError = string.Empty;
                for (int i = envelopes.Count - 1; i >= 0; i--)
                {
                    if (TryParsePatchBodyFiles(envelopes[i].Groups["body"].Value, out IReadOnlyList<WorkspaceFilePatch> files, out string bodyError))
                    {
                        proposal = new WorkspacePatchProposal(files, text);
                        return true;
                    }

                    if (string.IsNullOrWhiteSpace(firstError))
                        firstError = bodyError;
                }

                return RejectPatch(string.IsNullOrWhiteSpace(firstError)
                    ? "Patch envelope did not contain any valid file blocks."
                    : firstError, out error);
            }

            // No closing marker anywhere: without the end sentinel this "envelope" is only
            // trustworthy when the model actually led its response with the marker. A marker
            // buried mid-prose with no end marker is deliberation quoting its instructions and
            // must never be mined for file content (fail closed).
            int marker = text.IndexOf("[[AXIOM_CODEBASE_PATCH]]", StringComparison.OrdinalIgnoreCase);
            if (marker < 0 || !IsPatchEnvelopeMarkerLeading(text))
                return false;

            string body = text[(marker + "[[AXIOM_CODEBASE_PATCH]]".Length)..];
            if (string.IsNullOrWhiteSpace(body))
                return false;

            if (!TryParsePatchBodyFiles(body, out IReadOnlyList<WorkspaceFilePatch> openFiles, out string openError))
                return RejectPatch(string.IsNullOrWhiteSpace(openError)
                    ? "Patch envelope did not contain any valid file blocks."
                    : openError, out error);

            proposal = new WorkspacePatchProposal(openFiles, text);
            return true;
        }

        private static bool TryParsePatchBodyFiles(string body, out IReadOnlyList<WorkspaceFilePatch> files, out string error)
        {
            files = Array.Empty<WorkspaceFilePatch>();
            error = string.Empty;

            var parsed = new List<WorkspaceFilePatch>();
            foreach (Match match in PatchFileRegex.Matches(body))
            {
                string relativePath = NormalizeRelativePatchPath(match.Groups["path"].Value.Trim());
                string action = match.Groups["action"].Value.Trim().ToLowerInvariant();
                string content = match.Groups["content"].Value.Replace("\r\n", "\n", StringComparison.Ordinal);

                if (relativePath.Length == 0)
                {
                    error = "Patch contains an empty file path.";
                    return false;
                }
                if (!TryBuildWorkspaceFilePatch(relativePath, action, content, out WorkspaceFilePatch? patch, out string fileError)
                    || patch == null)
                {
                    error = fileError;
                    return false;
                }

                parsed.Add(patch);
            }

            // The loose (unfenced) parser grabs everything between FILE/ACTION headers as content.
            // It is the only way ACTION: edit sections (whose SEARCH/REPLACE blocks are not fenced)
            // get parsed at all.
            if (parsed.Count == 0)
            {
                if (!TryParseLoosePatchFiles(body, out IReadOnlyList<WorkspaceFilePatch> looseFiles, out error))
                    return false;
                parsed.AddRange(looseFiles);
            }

            if (parsed.Count == 0)
            {
                error = "Patch envelope did not contain any valid file blocks.";
                return false;
            }

            files = parsed;
            return true;
        }

        // True when [[AXIOM_CODEBASE_PATCH]] actually LEADS the response as the output contract
        // demands: first line, or preceded by at most one short announcement line ("Here is the
        // patch:"). Multiple lines of prose before the marker means the model was deliberating and
        // merely quoted its instructions — that text must never be mined for file content.
        private static bool IsPatchEnvelopeMarkerLeading(string text)
        {
            string value = text ?? string.Empty;
            int marker = value.IndexOf("[[AXIOM_CODEBASE_PATCH]]", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;

            string preamble = value[..marker].Trim();
            if (preamble.Length == 0)
                return true;

            string[] preambleLines = preamble.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return preambleLines.Length <= 1 && preamble.Length <= 100;
        }

        private static bool TryParseLoosePatchFiles(string body, out IReadOnlyList<WorkspaceFilePatch> files, out string error)
        {
            files = Array.Empty<WorkspaceFilePatch>();
            error = string.Empty;
            string source = body ?? string.Empty;
            MatchCollection headers = PatchFileHeaderRegex.Matches(source);
            if (headers.Count == 0)
            {
                error = "Patch envelope had no FILE/ACTION headers.";
                return false;
            }

            var parsed = new List<WorkspaceFilePatch>();
            for (int i = 0; i < headers.Count; i++)
            {
                if (headers[i] is not Match header)
                    continue;

                string relativePath = NormalizeRelativePatchPath(header.Groups["path"].Value.Trim());
                string action = header.Groups["action"].Value.Trim().ToLowerInvariant();
                int contentStart = header.Index + header.Length;
                int contentEnd = source.Length;
                if (i + 1 < headers.Count && headers[i + 1] is Match nextHeader)
                    contentEnd = nextHeader.Index;
                string content = source[contentStart..contentEnd];
                content = TrimLoosePatchContent(relativePath, action, content);

                if (relativePath.Length == 0)
                {
                    error = "Patch contains an empty file path.";
                    return false;
                }
                if (!TryBuildWorkspaceFilePatch(relativePath, action, content, out WorkspaceFilePatch? patch, out string patchError)
                    || patch == null)
                {
                    error = patchError;
                    return false;
                }

                parsed.Add(patch);
            }

            files = parsed;
            return parsed.Count > 0;
        }

        private static bool TryBuildWorkspaceFilePatch(
            string relativePath,
            string action,
            string content,
            out WorkspaceFilePatch? patch,
            out string error)
        {
            patch = null;
            error = string.Empty;

            if (relativePath.Length == 0)
            {
                error = "Patch contains an empty file path.";
                return false;
            }

            // The output contract's example envelopes use these placeholder paths. A "patch"
            // targeting one of them is the model echoing its instructions (deliberation), not a
            // real proposal — reject it so the retry/rescue pipeline asks for a real patch.
            if (IsContractPlaceholderPath(relativePath))
            {
                error = $"Patch targets the contract example placeholder path '{relativePath}' instead of a real workspace file.";
                return false;
            }

            if (action is not ("replace" or "create" or "edit"))
            {
                error = $"Unsupported patch action '{action}' for {relativePath}.";
                return false;
            }

            string normalizedContent = NormalizePatchFragment(content);
            if (normalizedContent.Length > MaxPatchFileChars)
            {
                error = $"Patch content for {relativePath} is too large.";
                return false;
            }

            if (action == "edit")
            {
                if (!TryParseEditBlocks(relativePath, normalizedContent, out IReadOnlyList<WorkspacePatchEditBlock> blocks, out error))
                    return false;

                patch = new WorkspaceFilePatch(relativePath, action, normalizedContent, blocks);
                return true;
            }

            if (string.IsNullOrWhiteSpace(normalizedContent))
            {
                error = $"Patch content for {relativePath} is empty.";
                return false;
            }

            patch = new WorkspaceFilePatch(relativePath, action, normalizedContent);
            return true;
        }

        private static bool TryParseEditBlocks(
            string relativePath,
            string content,
            out IReadOnlyList<WorkspacePatchEditBlock> blocks,
            out string error)
        {
            blocks = Array.Empty<WorkspacePatchEditBlock>();
            error = string.Empty;

            var parsed = new List<WorkspacePatchEditBlock>();
            foreach (Match match in PatchEditBlockRegex.Matches(content ?? string.Empty))
            {
                string search = NormalizePatchFragment(match.Groups["search"].Value);
                string replace = NormalizePatchFragment(match.Groups["replace"].Value);
                if (string.IsNullOrEmpty(search))
                {
                    error = $"{relativePath}: edit SEARCH block is empty.";
                    return false;
                }

                parsed.Add(new WorkspacePatchEditBlock(search, replace));
            }

            if (parsed.Count == 0)
            {
                error = $"{relativePath}: ACTION edit requires at least one <<<<<<< SEARCH / ======= / >>>>>>> REPLACE block.";
                return false;
            }

            string leftover = PatchEditBlockRegex.Replace(content ?? string.Empty, string.Empty);
            if (!string.IsNullOrWhiteSpace(leftover))
            {
                error = $"{relativePath}: ACTION edit contains text outside SEARCH/REPLACE blocks.";
                return false;
            }

            blocks = parsed;
            return true;
        }

        private static string TrimLoosePatchContent(string relativePath, string action, string content)
        {
            string trimmed = (content ?? string.Empty).Trim();
            trimmed = Regex.Replace(trimmed, @"(?im)^\s*\[\[END FILE\]\]\s*$[\s\S]*$", string.Empty).Trim();
            trimmed = Regex.Replace(trimmed, @"(?im)^\s*\[\[END AXIOM_CODEBASE_PATCH\]\]\s*$[\s\S]*$", string.Empty).Trim();

            Match fenced = Regex.Match(trimmed, @"\A`{3,}[^\r\n]*\r?\n(?<content>[\s\S]*?)\r?\n`{3,}\s*\z");
            if (fenced.Success)
                trimmed = fenced.Groups["content"].Value.TrimEnd();

            // Only full-file content may be truncated at the document's closing tag. ACTION: edit
            // content is SEARCH/REPLACE markers: a REPLACE block that legitimately ends with
            // </html> (an edit touching the end of the file) would otherwise lose its trailing
            // >>>>>>> REPLACE marker and fail to parse.
            string extension = Path.GetExtension(relativePath).ToLowerInvariant();
            if (extension is ".html" or ".htm"
                && !string.Equals(action, "edit", StringComparison.OrdinalIgnoreCase))
            {
                int closingHtml = trimmed.LastIndexOf("</html>", StringComparison.OrdinalIgnoreCase);
                if (closingHtml >= 0)
                    trimmed = trimmed[..(closingHtml + "</html>".Length)];
            }

            return trimmed.TrimEnd();
        }

        public WorkspacePatchApplyResult ApplyPatchProposal(ConnectedWorkspaceState state, WorkspacePatchProposal proposal)
        {
            if (state == null || !state.CodebaseEditAccessEnabled)
                throw new InvalidOperationException("Codebase Edit Access is not enabled.");
            if (proposal == null || proposal.Files.Count == 0)
                throw new InvalidOperationException("There are no proposed codebase changes to apply.");

            var resolved = new List<(WorkspaceFilePatch Patch, string TargetPath)>();
            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                string target = ResolvePatchTargetPath(state, patch);
                if ((patch.Action == "replace" || patch.Action == "edit") && !File.Exists(target))
                    throw new FileNotFoundException($"Cannot replace a file that does not exist: {patch.RelativePath}");
                if (patch.Action == "create" && File.Exists(target))
                    throw new IOException($"Cannot create a file that already exists: {patch.RelativePath}");

                resolved.Add((patch, target));
            }

            var changed = new List<string>();
            foreach ((WorkspaceFilePatch patch, string targetPath) in resolved)
            {
                string? directory = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);

                string materializedContent = MaterializePatchContent(state, patch);
                AtomicFileWriter.WriteAllText(targetPath, NormalizeFileContentForWrite(materializedContent), keepBackup: false);
                // Earlier versions left a ".bak" sidecar beside every patched file; clean any
                // leftover so it stops polluting the user's git status.
                try
                {
                    string staleBackupPath = targetPath + ".bak";
                    if (File.Exists(staleBackupPath))
                        File.Delete(staleBackupPath);
                }
                catch
                {
                    // Cleanup is cosmetic — never fail an apply over it.
                }

                changed.Add(patch.RelativePath);
            }

            string summary = changed.Count == 1
                ? $"Applied 1 codebase change: {changed[0]}"
                : $"Applied {changed.Count} codebase changes:\n- " + string.Join("\n- ", changed);
            return new WorkspacePatchApplyResult(changed, summary);
        }

        // True when applying the proposal would leave every targeted file byte-identical (after
        // newline normalization) — e.g. an edit whose SEARCH and REPLACE blocks are the same, or a
        // replace that regurgitates the current file. Such a patch "succeeds" while changing
        // nothing; the caller should reject it and ask the Builder for a real change instead of
        // presenting the user an empty diff. Any materialization error means this is NOT a no-op —
        // those failures are reported by the normal validation path.
        public bool IsNoOpPatchProposal(ConnectedWorkspaceState state, WorkspacePatchProposal proposal, out string detail)
        {
            detail = string.Empty;
            if (state == null || proposal == null || proposal.Files.Count == 0)
                return false;

            var unchanged = new List<string>();
            foreach (WorkspaceFilePatch patch in proposal.Files)
            {
                try
                {
                    string target = ResolvePatchTargetPath(state, patch);
                    if (!File.Exists(target))
                        return false; // creating a new file is always a real change

                    string current = NormalizePatchFragment(File.ReadAllText(target));
                    string materialized = NormalizePatchFragment(MaterializePatchContent(state, patch));
                    if (!string.Equals(materialized, current, StringComparison.Ordinal))
                        return false;

                    unchanged.Add(patch.RelativePath);
                }
                catch
                {
                    return false;
                }
            }

            detail = string.Join(", ", unchanged);
            return unchanged.Count > 0;
        }

        public string MaterializePatchContent(ConnectedWorkspaceState state, WorkspaceFilePatch patch)
        {
            return MaterializePatchContent(state, patch, null);
        }

        public string MaterializePatchContent(ConnectedWorkspaceState state, WorkspaceFilePatch patch, string? baseContentOverride)
        {
            if (patch.Action != "edit")
                return patch.Content ?? string.Empty;

            string current;
            if (baseContentOverride != null)
            {
                current = NormalizePatchFragment(baseContentOverride);
            }
            else
            {
                string target = ResolvePatchTargetPath(state, patch);
                if (!File.Exists(target))
                    throw new FileNotFoundException($"Cannot edit a file that does not exist: {patch.RelativePath}");

                current = NormalizePatchFragment(File.ReadAllText(target));
            }

            return ApplySearchReplaceBlocks(patch.RelativePath, current, patch.Blocks);
        }

        private static string ApplySearchReplaceBlocks(
            string relativePath,
            string currentContent,
            IReadOnlyList<WorkspacePatchEditBlock> blocks)
        {
            if (blocks.Count == 0)
                throw new InvalidOperationException($"{relativePath}: ACTION edit has no SEARCH/REPLACE blocks.");

            string result = currentContent ?? string.Empty;
            for (int i = 0; i < blocks.Count; i++)
            {
                WorkspacePatchEditBlock block = blocks[i];
                string search = block.Search ?? string.Empty;
                string replace = block.Replace ?? string.Empty;

                // Tier 1: exact byte match — the fast, unambiguous path.
                int occurrences = CountOccurrences(result, search);
                if (occurrences == 1)
                {
                    result = result.Replace(search, replace, StringComparison.Ordinal);
                    continue;
                }
                if (occurrences > 1)
                    throw new InvalidOperationException($"{relativePath}: edit block {i + 1:n0} SEARCH text matched {occurrences:n0} times; make the anchor unique.");

                // Tier 2+: models routinely reproduce a region with slightly wrong indentation or
                // trailing whitespace. Rather than fail the whole patch, locate the SEARCH span by
                // progressively looser line-based comparison and splice the (re-indented) REPLACE
                // over the ACTUAL file span. Each looser tier still requires a UNIQUE match so a
                // fuzzy anchor can never overwrite the wrong location.
                if (TryLocateFuzzySearchSpan(result, search, out int spanStart, out int spanLength, out string spanReplace, replace))
                {
                    result = result[..spanStart] + spanReplace + result[(spanStart + spanLength)..];
                    continue;
                }

                throw new InvalidOperationException($"{relativePath}: edit block {i + 1:n0} SEARCH text was not found.");
            }

            return result;
        }

        // Locate a SEARCH block in the file by line-sequence comparison at increasing whitespace
        // tolerance. Returns the exact character span in the file plus a REPLACE re-indented to the
        // file's actual indentation, so a slightly-misindented anchor applies cleanly and correctly.
        private static bool TryLocateFuzzySearchSpan(
            string file,
            string search,
            out int spanStart,
            out int spanLength,
            out string adjustedReplace,
            string replace)
        {
            spanStart = 0;
            spanLength = 0;
            adjustedReplace = replace;

            string[] searchLines = search.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            // Drop leading/trailing blank anchor lines: they carry no positional information and are
            // the single most common source of a spurious extra/missing line in a model's anchor.
            int sFirst = 0, sLast = searchLines.Length - 1;
            while (sFirst <= sLast && searchLines[sFirst].Trim().Length == 0) sFirst++;
            while (sLast >= sFirst && searchLines[sLast].Trim().Length == 0) sLast--;
            if (sFirst > sLast)
                return false;

            var wantedRaw = new List<string>();
            for (int i = sFirst; i <= sLast; i++)
                wantedRaw.Add(searchLines[i]);

            var fileLineSpans = SplitLinesWithSpans(file);

            // comparer 0: trailing-whitespace-insensitive (exact indentation).
            // comparer 1: fully trimmed (indentation-insensitive).
            for (int tier = 0; tier <= 1; tier++)
            {
                Func<string, string> norm = tier == 0
                    ? (s => s.TrimEnd())
                    : (s => s.Trim());

                var match = FindUniqueLineBlock(fileLineSpans, wantedRaw, norm);
                if (match == null)
                    continue;

                (int startLine, int endLine) = match.Value;
                spanStart = fileLineSpans[startLine].Start;
                int spanEnd = fileLineSpans[endLine].Start + fileLineSpans[endLine].Length;
                spanLength = spanEnd - spanStart;

                // Re-indent REPLACE by the difference between the file's actual indentation and the
                // anchor's indentation on the first meaningful line, so tier-1 (trim) matches keep
                // the file's real indentation instead of the model's off-by-N version.
                string fileIndent = LeadingWhitespace(fileLineSpans[startLine].Text);
                string anchorIndent = LeadingWhitespace(wantedRaw[0]);
                adjustedReplace = tier == 1 && !string.Equals(fileIndent, anchorIndent, StringComparison.Ordinal)
                    ? ReindentBlock(replace, anchorIndent, fileIndent)
                    : replace;
                return true;
            }

            return false;
        }

        private static (int Start, int Length, string Text)[] SplitLinesWithSpans(string text)
        {
            var spans = new List<(int Start, int Length, string Text)>();
            int lineStart = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    spans.Add((lineStart, i - lineStart, text[lineStart..i]));
                    lineStart = i + 1;
                }
            }
            spans.Add((lineStart, text.Length - lineStart, text[lineStart..]));
            return spans.ToArray();
        }

        private static (int StartLine, int EndLine)? FindUniqueLineBlock(
            (int Start, int Length, string Text)[] fileLines,
            List<string> wanted,
            Func<string, string> norm)
        {
            var normWanted = wanted.Select(norm).ToArray();
            int found = -1;
            int matchCount = 0;

            for (int start = 0; start + normWanted.Length <= fileLines.Length; start++)
            {
                bool ok = true;
                for (int k = 0; k < normWanted.Length; k++)
                {
                    if (!string.Equals(norm(fileLines[start + k].Text), normWanted[k], StringComparison.Ordinal))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    matchCount++;
                    if (matchCount > 1)
                        return null; // ambiguous — refuse rather than risk the wrong location
                    found = start;
                }
            }

            return found >= 0 ? (found, found + normWanted.Length - 1) : null;
        }

        private static string LeadingWhitespace(string line)
        {
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t'))
                i++;
            return line[..i];
        }

        private static string ReindentBlock(string block, string fromIndent, string toIndent)
        {
            string[] lines = block.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0)
                    continue;
                lines[i] = fromIndent.Length > 0 && lines[i].StartsWith(fromIndent, StringComparison.Ordinal)
                    ? toIndent + lines[i][fromIndent.Length..]
                    : toIndent + lines[i].TrimStart(' ', '\t');
            }
            return string.Join("\n", lines);
        }

        public WorkspaceGitStatus GetGitStatus(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return new WorkspaceGitStatus(false, string.Empty, 0, string.Empty, string.Empty);

            try
            {
                string root = NormalizeRoot(rootPath);
                var inside = RunGit(root, "rev-parse", "--is-inside-work-tree");
                if (!inside.Success || !inside.Output.Contains("true", StringComparison.OrdinalIgnoreCase))
                    return new WorkspaceGitStatus(false, string.Empty, 0, string.Empty, string.Empty);

                var branch = RunGit(root, "branch", "--show-current");
                var status = RunGit(root, "status", "--short");
                string shortStatus = status.Output.Trim();
                int changed = shortStatus.Length == 0
                    ? 0
                    : shortStatus.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

                return new WorkspaceGitStatus(
                    true,
                    branch.Output.Trim(),
                    changed,
                    shortStatus,
                    status.Success ? string.Empty : status.Output.Trim());
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new WorkspaceGitStatus(false, string.Empty, 0, string.Empty, "Git was not found.");
            }
            catch (Exception ex)
            {
                return new WorkspaceGitStatus(false, string.Empty, 0, string.Empty, ex.Message);
            }
        }

        public WorkspaceGitCheckpointResult CreateGitCheckpointCommit(string rootPath, string message)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
                return new WorkspaceGitCheckpointResult(false, false, false, true, "No local Git workspace is connected.");

            WorkspaceGitStatus status = GetGitStatus(rootPath);
            if (!status.IsRepository)
                return new WorkspaceGitCheckpointResult(false, false, false, true, "Connected workspace is not a Git repository.");

            if (status.ChangedFileCount == 0)
                return new WorkspaceGitCheckpointResult(true, false, false, true, "Git worktree is already clean; current HEAD is the checkpoint.");

            string root = NormalizeRoot(rootPath);
            var add = RunGit(root, "add", "-A");
            if (!add.Success)
                return new WorkspaceGitCheckpointResult(true, true, false, false, "Could not stage checkpoint files: " + add.Output.Trim());

            var commit = RunGit(root, "commit", "-m", string.IsNullOrWhiteSpace(message) ? "Axiom auto-apply checkpoint" : message.Trim());
            if (!commit.Success)
                return new WorkspaceGitCheckpointResult(true, true, false, false, "Could not create checkpoint commit: " + commit.Output.Trim());

            return new WorkspaceGitCheckpointResult(true, true, true, true, "Created Git checkpoint commit before auto-apply.");
        }

        public bool LooksLikeRepositoryUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            return Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
                && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "git", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase));
        }

        public async Task<WorkspaceCloneResult> CloneRepositoryAsync(
            string repositoryUrl,
            string parentFolder,
            string? preferredFolderName,
            IProgress<string>? progress,
            CancellationToken token,
            string? authenticatedCloneUrl = null)
        {
            if (!LooksLikeRepositoryUrl(repositoryUrl) && string.IsNullOrWhiteSpace(authenticatedCloneUrl))
                throw new ArgumentException("Repository URL is not valid.", nameof(repositoryUrl));
            if (string.IsNullOrWhiteSpace(parentFolder))
                throw new ArgumentException("Clone parent folder is empty.", nameof(parentFolder));

            string parent = Path.GetFullPath(parentFolder);
            Directory.CreateDirectory(parent);

            string folderName = MakeSafeFolderName(string.IsNullOrWhiteSpace(preferredFolderName)
                ? GuessRepositoryName(repositoryUrl)
                : preferredFolderName);
            string target = GetAvailableClonePath(parent, folderName);

            // Prefer tokenized URL for private GitHub repos when Cloud Connectors GitHub is linked.
            string cloneSource = !string.IsNullOrWhiteSpace(authenticatedCloneUrl)
                ? authenticatedCloneUrl.Trim()
                : repositoryUrl.Trim();

            var output = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("clone");
            psi.ArgumentList.Add("--progress");
            psi.ArgumentList.Add(cloneSource);
            psi.ArgumentList.Add(target);

            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) => AppendProcessLine(e.Data, output, progress);
            process.ErrorDataReceived += (_, e) => AppendProcessLine(e.Data, output, progress);

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Git clone could not be started.");

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                await process.WaitForExitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKillProcessTree(process);
                throw;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException("Git was not found. Install Git for Windows or connect an existing local clone folder.", ex);
            }

            string log = output.ToString().Trim();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
                    ? $"git clone failed with exit code {process.ExitCode}."
                    : log);

            return new WorkspaceCloneResult(target, log);
        }

        public string NormalizeRoot(string rootPath)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Workspace path is empty.", nameof(rootPath));

            string fullPath = Path.GetFullPath(rootPath);
            if (!Directory.Exists(fullPath))
                throw new DirectoryNotFoundException($"Workspace folder was not found: {fullPath}");

            return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public bool IsPathInsideWorkspace(string rootPath, string candidatePath)
        {
            string root = NormalizeRoot(rootPath) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(candidatePath);
            return candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        }

        public string ResolvePatchTargetPath(ConnectedWorkspaceState state, WorkspaceFilePatch patch)
        {
            string relativePath = NormalizeRelativePatchPath(patch.RelativePath);
            if (relativePath.Length == 0)
                throw new InvalidOperationException("Patch path is empty.");

            if (!string.IsNullOrWhiteSpace(state.RootPath) && Directory.Exists(state.RootPath))
            {
                string root = NormalizeRoot(state.RootPath);
                string target = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsPathInsideWorkspace(root, target))
                    throw new InvalidOperationException($"Patch path escapes the connected workspace: {patch.RelativePath}");
                return target;
            }

            if (state.ConnectedFiles.Count > 0)
            {
                var matches = state.ConnectedFiles
                    .Where(File.Exists)
                    .Where(path =>
                    {
                        string fileName = Path.GetFileName(path);
                        string normalized = path.Replace('\\', '/');
                        return string.Equals(fileName, relativePath, StringComparison.OrdinalIgnoreCase)
                            || normalized.EndsWith("/" + relativePath, StringComparison.OrdinalIgnoreCase);
                    })
                    .ToList();

                if (matches.Count == 1)
                    return Path.GetFullPath(matches[0]);
                if (matches.Count > 1)
                    throw new InvalidOperationException($"Patch path is ambiguous across connected files: {patch.RelativePath}");
            }

            throw new InvalidOperationException($"Patch path is not inside a connected local workspace: {patch.RelativePath}");
        }

        private bool TryGetReadableToolFiles(ConnectedWorkspaceState state, out IReadOnlyList<string> files, out string error)
        {
            files = Array.Empty<string>();
            error = string.Empty;

            if (state == null || !state.CodebaseEditAccessEnabled)
            {
                error = "Codebase access is not enabled.";
                return false;
            }

            files = ResolveCandidateFiles(state).Take(MaxIndexedFiles).ToList();
            if (files.Count == 0)
            {
                error = "No readable local code files are connected.";
                return false;
            }

            return true;
        }

        private bool TryResolveReadableToolTarget(ConnectedWorkspaceState state, string relativePath, out string target, out string error)
        {
            target = string.Empty;
            error = string.Empty;

            if (state == null || !state.CodebaseEditAccessEnabled)
            {
                error = "Codebase access is not enabled.";
                return false;
            }

            string normalized = NormalizeRelativePatchPath(relativePath);
            if (normalized.Length == 0)
            {
                error = "File path is empty or escapes the connected workspace.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(state.RootPath) && Directory.Exists(state.RootPath))
            {
                string root = NormalizeRoot(state.RootPath);
                string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
                if (!IsPathInsideWorkspace(root, candidate))
                {
                    error = $"File path escapes the connected workspace: {relativePath}";
                    return false;
                }

                if (!IsReadableSourceFile(candidate))
                {
                    error = $"File is not a readable connected source file: {normalized}";
                    return false;
                }

                target = candidate;
                return true;
            }

            var matches = state.ConnectedFiles
                .Where(File.Exists)
                .Where(IsReadableSourceFile)
                .Where(path =>
                {
                    string fileName = Path.GetFileName(path);
                    string display = path.Replace('\\', '/');
                    return string.Equals(fileName, normalized, StringComparison.OrdinalIgnoreCase)
                        || display.EndsWith("/" + normalized, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();

            if (matches.Count == 1)
            {
                target = Path.GetFullPath(matches[0]);
                return true;
            }

            error = matches.Count == 0
                ? $"File is not connected or readable: {normalized}"
                : $"File path is ambiguous across connected files: {normalized}";
            return false;
        }

        private static IEnumerable<string> EnumerateCandidateFiles(string rootPath)
        {
            var pending = new Stack<string>();
            pending.Push(rootPath);

            while (pending.Count > 0)
            {
                string current = pending.Pop();
                IEnumerable<string> directories;
                IEnumerable<string> files;

                try
                {
                    directories = Directory.EnumerateDirectories(current);
                    files = Directory.EnumerateFiles(current);
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }
                catch (IOException)
                {
                    continue;
                }

                foreach (string directory in directories)
                {
                    string name = Path.GetFileName(directory);
                    if (!IgnoredDirectoryNames.Contains(name))
                        pending.Push(directory);
                }

                foreach (string file in files)
                {
                    string name = Path.GetFileName(file);
                    string extension = Path.GetExtension(file);
                    if (!IgnoredFileNames.Contains(name) && !IgnoredExtensions.Contains(extension))
                        yield return file;
                }
            }
        }

        private IEnumerable<string> ResolveCandidateFiles(ConnectedWorkspaceState state)
        {
            if (state.ConnectedFiles.Count > 0)
            {
                foreach (string path in state.ConnectedFiles)
                {
                    if (File.Exists(path) && IsReadableSourceFile(path))
                        yield return Path.GetFullPath(path);
                }
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(state.RootPath) && Directory.Exists(state.RootPath))
            {
                foreach (string file in EnumerateCandidateFiles(state.RootPath))
                    yield return file;
            }
        }

        private static IEnumerable<string> SelectPromptAwareRelevantFiles(IReadOnlyList<string> files, ConnectedWorkspaceState state, string query, int maxFiles)
        {
            var forced = ResolvePromptNamedFiles(files, state, query)
                .Take(Math.Max(0, maxFiles))
                .ToList();
            if (forced.Count >= maxFiles)
                return forced;

            return forced
                .Concat(SelectRelevantFiles(files, query, maxFiles)
                    .Where(path => !forced.Contains(path, StringComparer.OrdinalIgnoreCase)))
                .Take(maxFiles);
        }

        // Relative paths recorded when patches were applied in this chat, normalized for matching.
        private static IReadOnlyList<string> NormalizeRecentlyChangedReferences(ConnectedWorkspaceState state)
        {
            return (state?.RecentlyChangedFiles ?? new List<string>())
                .Select(path => NormalizeRelativePatchPath(path ?? string.Empty))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        private static IEnumerable<string> ResolvePromptNamedFiles(IReadOnlyList<string> files, ConnectedWorkspaceState state, string query)
        {
            if (files.Count == 0)
                return Array.Empty<string>();

            var mentions = ExtractMentionedWorkspaceFileReferences(query ?? string.Empty)
                .Concat(NormalizeRecentlyChangedReferences(state))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var selected = new List<string>();
            foreach (string mention in mentions)
            {
                string normalizedMention = mention.Replace('\\', '/');
                var matches = files
                    .Where(path =>
                    {
                        string display = GetDisplayPath(state, path).Replace('\\', '/');
                        string fileName = Path.GetFileName(path);
                        return string.Equals(display, normalizedMention, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(fileName, normalizedMention, StringComparison.OrdinalIgnoreCase)
                            || display.EndsWith("/" + normalizedMention, StringComparison.OrdinalIgnoreCase);
                    })
                    .OrderBy(path => GetDisplayPath(state, path).Length)
                    .ToList();

                if (matches.Count == 1 && !selected.Contains(matches[0], StringComparer.OrdinalIgnoreCase))
                    selected.Add(matches[0]);
            }

            return selected;
        }

        private static IReadOnlyList<string> ExtractMentionedWorkspaceFileReferences(string query)
        {
            return Regex.Matches(query ?? string.Empty, @"(?<![\w.-])(?<path>[\w./\\-]+\.(?:cs|xaml|csproj|sln|slnx|py|js|mjs|ts|tsx|jsx|json|jsonc|css|scss|html|htm|md|txt|xml|yaml|yml|toml))(?![\w.-])", RegexOptions.IgnoreCase)
                .Select(match => NormalizeRelativePatchPath(match.Groups["path"].Value.Trim()))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool IsPromptNamedContextFile(string relativePath, string fullPath, IReadOnlyList<string> promptNamedReferences)
        {
            if (promptNamedReferences.Count == 0)
                return false;

            string normalizedRelative = (relativePath ?? string.Empty).Replace('\\', '/');
            string fileName = Path.GetFileName(fullPath);
            return promptNamedReferences.Any(reference =>
                string.Equals(normalizedRelative, reference, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fileName, reference, StringComparison.OrdinalIgnoreCase)
                || normalizedRelative.EndsWith("/" + reference, StringComparison.OrdinalIgnoreCase));
        }

        // How many lexically top-ranked files get a semantic re-rank pass. Embedding every
        // candidate (a folder connection can index 5,000 files) is minutes of native
        // inference on a cold cache; re-ranking a small lexical shortlist keeps the
        // semantic benefit at a bounded cost.
        private const int MaxSemanticRerankCandidates = 48;

        private static IEnumerable<string> SelectRelevantFiles(IReadOnlyList<string> files, string query, int maxFiles)
        {
            var terms = ExtractQueryTerms(query);
            var lexicalRanked = files
                .Select(path => new
                {
                    Path = path,
                    Score = ScoreFile(path, terms)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .ToList();

            if (!LocalSemanticEmbeddingService.Shared.IsAvailable)
                return lexicalRanked.Take(maxFiles).Select(item => item.Path);

            int rerankCount = Math.Clamp(maxFiles * 6, maxFiles, MaxSemanticRerankCandidates);
            return lexicalRanked
                .Take(rerankCount)
                .Select(item => new
                {
                    item.Path,
                    Score = item.Score + ComputeSemanticFileBoost(query, item.Path)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .Take(maxFiles)
                .Select(item => item.Path);
        }

        private static int ComputeSemanticFileBoost(string query, string path)
        {
            return LocalSemanticEmbeddingService.Shared.TryGetSimilarity(query, BuildFileSemanticDescriptor(path), out double semanticScore)
                ? (int)Math.Round(Math.Max(0, semanticScore - 0.20) * 60)
                : 0;
        }

        private static int ScoreFile(string path, IReadOnlyCollection<string> terms)
        {
            string name = Path.GetFileName(path).ToLowerInvariant();
            string relative = path.Replace('\\', '/').ToLowerInvariant();
            int score = 0;

            foreach (string term in terms)
            {
                if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score += 12;
                if (relative.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score += 4;
            }

            string extension = Path.GetExtension(path);
            if (extension is ".cs" or ".xaml" or ".csproj" or ".sln" or ".slnx")
                score += 3;
            if (name is "readme.md" or "package.json" or "project.json")
                score += 2;

            return score;
        }

        private static string BuildFileSemanticDescriptor(string path)
        {
            string relative = path.Replace('\\', '/');
            string extension = Path.GetExtension(path).ToLowerInvariant();
            string kind = extension switch
            {
                ".cs" => "C# source code class service view model logic",
                ".xaml" => "WPF XAML user interface layout controls styling",
                ".csproj" or ".sln" or ".slnx" => "dotnet project solution build dependencies",
                ".json" or ".jsonc" => "JSON configuration settings data",
                ".md" => "markdown documentation readme notes",
                ".html" or ".css" or ".js" or ".ts" or ".tsx" or ".jsx" => "web frontend source",
                _ => "source file"
            };

            string spaced = Regex.Replace(relative, @"[_\-/\\.]+", " ");
            spaced = Regex.Replace(spaced, "([a-z])([A-Z])", "$1 $2");
            return $"{relative}\n{spaced}\n{kind}";
        }

        private static IReadOnlyList<string> ExtractQueryTerms(string query)
        {
            return System.Text.RegularExpressions.Regex.Matches((query ?? string.Empty).ToLowerInvariant(), @"\b[a-z][a-z0-9_]{2,}\b")
                .Select(match => match.Value)
                .Where(term => term is not ("the" or "and" or "for" or "this" or "that" or "with" or "from" or "into" or "codebase" or "file" or "files"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
        }

        private sealed record WorkspaceSearchMatch(string RelativePath, int LineNumber, string Snippet, int Score);

        private static string TrimToolLine(string line)
        {
            string trimmed = (line ?? string.Empty).Trim();
            return trimmed.Length <= 220 ? trimmed : trimmed[..220] + "...";
        }

        private static bool IsReadableSourceFile(string path)
        {
            string name = Path.GetFileName(path);
            string extension = Path.GetExtension(path);
            return File.Exists(path)
                && !IgnoredFileNames.Contains(name)
                && !IgnoredExtensions.Contains(extension);
        }

        private static string GetDisplayPath(ConnectedWorkspaceState state, string path)
        {
            if (!string.IsNullOrWhiteSpace(state.RootPath) && Directory.Exists(state.RootPath))
            {
                try
                {
                    return Path.GetRelativePath(state.RootPath, path).Replace('\\', '/');
                }
                catch
                {
                    return Path.GetFileName(path);
                }
            }

            return Path.GetFileName(path);
        }

        private static string BuildUnavailablePacket(ConnectedWorkspaceState state)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("The user enabled Codebase Edit Access, but no readable local code files are connected yet.");
            sb.AppendLine($"Locked model mode: {state.LockedMode}");
            sb.AppendLine($"Connection: {state.ConnectionKind}");
            if (!string.IsNullOrWhiteSpace(state.RepositoryUrl))
            {
                sb.AppendLine($"Repository URL: {state.RepositoryUrl}");
                if (string.IsNullOrWhiteSpace(state.RootPath))
                    sb.AppendLine("A repository URL is recorded, but the repo must be cloned locally or connected through a provider integration before file reads/edits can run.");
            }
            sb.AppendLine("Ask the user to connect a local folder or files before making codebase-specific claims.");
            return sb.ToString().Trim();
        }

        private static bool RejectPatch(string message, out string error)
        {
            error = message;
            return false;
        }

        // Placeholder paths used by the patch-format examples in the Builder's instructions.
        private static bool IsContractPlaceholderPath(string relativePath)
        {
            return relativePath.Equals("relative/path/from/workspace.ext", StringComparison.OrdinalIgnoreCase)
                || relativePath.Equals("path/from/workspace.ext", StringComparison.OrdinalIgnoreCase)
                || relativePath.Equals("relative/path/of/target/file", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeRelativePatchPath(string path)
        {
            string normalized = (path ?? string.Empty).Trim().Replace('\\', '/');
            normalized = normalized.TrimStart('/');
            if (normalized.Contains("../", StringComparison.Ordinal)
                || normalized.Equals("..", StringComparison.Ordinal)
                || Path.IsPathRooted(normalized))
                return string.Empty;

            while (normalized.Contains("//", StringComparison.Ordinal))
                normalized = normalized.Replace("//", "/", StringComparison.Ordinal);
            return normalized;
        }

        // NUL and other C0 control characters (degenerate provider output) must never be persisted
        // into a workspace file — they render as empty boxes and can corrupt tooling. Tab/CR/LF kept.
        private static readonly Regex FileWriteControlCharacterRegex = new(
            @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F\uFFFE\uFFFF]",
            RegexOptions.Compiled);

        private static string NormalizeFileContentForWrite(string content)
        {
            string sanitized = FileWriteControlCharacterRegex.Replace(content ?? string.Empty, string.Empty);
            string normalized = sanitized.Replace("\r\n", "\n", StringComparison.Ordinal);
            return normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
        }

        private static string NormalizePatchFragment(string content)
        {
            string normalized = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
            return normalized.Replace("\r", "\n", StringComparison.Ordinal);
        }

        private static int CountOccurrences(string value, string needle)
        {
            if (string.IsNullOrEmpty(value) || string.IsNullOrEmpty(needle))
                return 0;

            int count = 0;
            int index = 0;
            while ((index = value.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }

        private static void AppendProcessLine(string? line, StringBuilder output, IProgress<string>? progress)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            output.AppendLine(line);
            progress?.Report(line.Trim());
        }

        private static void TryKillProcessTree(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only.
            }
        }

        private static (bool Success, string Output) RunGit(string root, params string[] args)
        {
            var output = new StringBuilder();
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(root);
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);

            using var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, e) => AppendProcessLine(e.Data, output, null);
            process.ErrorDataReceived += (_, e) => AppendProcessLine(e.Data, output, null);
            if (!process.Start())
                return (false, "Git command could not be started.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit(6000);
            if (!process.HasExited)
            {
                TryKillProcessTree(process);
                return (false, "Git status timed out.");
            }

            return (process.ExitCode == 0, output.ToString());
        }

        private static string GetAvailableClonePath(string parent, string folderName)
        {
            string target = Path.Combine(parent, folderName);
            if (!Directory.Exists(target) && !File.Exists(target))
                return target;

            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(parent, $"{folderName}-{i}");
                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(parent, folderName + "-" + DateTime.Now.ToString("yyyyMMddHHmmss"));
        }

        private static string GuessRepositoryName(string repositoryUrl)
        {
            try
            {
                var uri = new Uri(repositoryUrl);
                string name = uri.Segments.LastOrDefault()?.Trim('/') ?? "repository";
                return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            }
            catch
            {
                string trimmed = repositoryUrl.Trim().TrimEnd('/');
                int slash = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf(':'));
                string name = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
                return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            }
        }

        private static string MakeSafeFolderName(string value)
        {
            string name = string.IsNullOrWhiteSpace(value) ? "repository" : value.Trim();
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');

            name = name.Trim('.', ' ', '-');
            return string.IsNullOrWhiteSpace(name) ? "repository" : name;
        }
    }
}
