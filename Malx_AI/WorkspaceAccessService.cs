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

    public sealed record WorkspaceFilePatch(
        string RelativePath,
        string Action,
        string Content);

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
            ".rar"
        };

        private const int MaxIndexedFiles = 5000;
        private const int MaxContextFiles = 8;
        private const int MaxContextCharsPerFile = 8000;
        private const int MaxPatchFileChars = 1_000_000;

        private static readonly Regex PatchEnvelopeRegex = new(
            @"\[\[AXIOM_CODEBASE_PATCH\]\](?<body>[\s\S]*?)\[\[END AXIOM_CODEBASE_PATCH\]\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex PatchFileRegex = new(
            @"FILE:\s*(?<path>[^\r\n]+)\s+ACTION:\s*(?<action>[^\r\n]+)\s+(?<fence>`{3,})[^\r\n]*\r?\n(?<content>[\s\S]*?)\r?\n\k<fence>(?:\s*\[\[END FILE\]\])?",
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

            var selected = SelectRelevantFiles(files, query, MaxContextFiles).ToList();
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
            sb.AppendLine("When proposing codebase changes, output this exact full-file patch envelope:");
            sb.AppendLine("[[AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("FILE: relative/path/from/workspace.ext");
            sb.AppendLine("ACTION: replace");
            sb.AppendLine("```language");
            sb.AppendLine("complete replacement file content");
            sb.AppendLine("```");
            sb.AppendLine("[[END FILE]]");
            sb.AppendLine("[[END AXIOM_CODEBASE_PATCH]]");
            sb.AppendLine("Use ACTION: create only for new files. Do not use delete actions. Do not output partial files.");
            sb.AppendLine("Use the exact connected workspace path for the target file. Do not rename file extensions.");
            sb.AppendLine();
            sb.AppendLine("RELEVANT FILES:");

            int remaining = Math.Max(1200, maxChars);
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
                string capped = content.Length > MaxContextCharsPerFile
                    ? content[..MaxContextCharsPerFile] + "\n[...file truncated for context budget]"
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

        public bool TryParsePatchProposal(string text, out WorkspacePatchProposal? proposal, out string error)
        {
            proposal = null;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                return false;

            Match envelope = PatchEnvelopeRegex.Match(text);
            if (!envelope.Success)
                return false;

            string body = envelope.Groups["body"].Value;
            var files = new List<WorkspaceFilePatch>();
            foreach (Match match in PatchFileRegex.Matches(body))
            {
                string relativePath = NormalizeRelativePatchPath(match.Groups["path"].Value.Trim());
                string action = match.Groups["action"].Value.Trim().ToLowerInvariant();
                string content = match.Groups["content"].Value.Replace("\r\n", "\n", StringComparison.Ordinal);

                if (relativePath.Length == 0)
                    return RejectPatch("Patch contains an empty file path.", out error);
                if (action is not ("replace" or "create"))
                    return RejectPatch($"Unsupported patch action '{action}' for {relativePath}.", out error);
                if (content.Length > MaxPatchFileChars)
                    return RejectPatch($"Patch content for {relativePath} is too large.", out error);

                files.Add(new WorkspaceFilePatch(relativePath, action, content));
            }

            if (files.Count == 0)
                return RejectPatch("Patch envelope did not contain any valid file blocks.", out error);

            proposal = new WorkspacePatchProposal(files, text);
            return true;
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
                if (patch.Action == "replace" && !File.Exists(target))
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

                AtomicFileWriter.WriteAllText(targetPath, NormalizeFileContentForWrite(patch.Content));
                changed.Add(patch.RelativePath);
            }

            string summary = changed.Count == 1
                ? $"Applied 1 codebase change: {changed[0]}"
                : $"Applied {changed.Count} codebase changes:\n- " + string.Join("\n- ", changed);
            return new WorkspacePatchApplyResult(changed, summary);
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
            CancellationToken token)
        {
            if (!LooksLikeRepositoryUrl(repositoryUrl))
                throw new ArgumentException("Repository URL is not valid.", nameof(repositoryUrl));
            if (string.IsNullOrWhiteSpace(parentFolder))
                throw new ArgumentException("Clone parent folder is empty.", nameof(parentFolder));

            string parent = Path.GetFullPath(parentFolder);
            Directory.CreateDirectory(parent);

            string folderName = MakeSafeFolderName(string.IsNullOrWhiteSpace(preferredFolderName)
                ? GuessRepositoryName(repositoryUrl)
                : preferredFolderName);
            string target = GetAvailableClonePath(parent, folderName);

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
            psi.ArgumentList.Add(repositoryUrl);
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

        private static IEnumerable<string> SelectRelevantFiles(IReadOnlyList<string> files, string query, int maxFiles)
        {
            var terms = ExtractQueryTerms(query);
            return files
                .Select(path => new
                {
                    Path = path,
                    Score = ScoreFile(path, terms)
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Path.Length)
                .Take(maxFiles)
                .Select(item => item.Path);
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

        private static IReadOnlyList<string> ExtractQueryTerms(string query)
        {
            return System.Text.RegularExpressions.Regex.Matches((query ?? string.Empty).ToLowerInvariant(), @"\b[a-z][a-z0-9_]{2,}\b")
                .Select(match => match.Value)
                .Where(term => term is not ("the" or "and" or "for" or "this" or "that" or "with" or "from" or "into" or "codebase" or "file" or "files"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(24)
                .ToList();
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

        private static string NormalizeFileContentForWrite(string content)
        {
            string normalized = (content ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
            return normalized.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
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
