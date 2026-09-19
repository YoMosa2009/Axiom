using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Malx_AI
{
    /// <summary>
    /// Owns the durable copies behind a Workplace project's knowledge base.  A knowledge-base
    /// attachment must never depend on the original drag/drop path still existing after the user
    /// closes the app, moves a file, or removes an external drive.
    /// </summary>
    public static class ProjectKnowledgeStorage
    {
        private const string StorageFolderName = "WorkplaceKnowledgeBases";

        public static string GetProjectRoot(string projectId)
        {
            string safeProjectId = string.IsNullOrWhiteSpace(projectId)
                ? "default"
                : string.Concat(projectId.Where(char.IsLetterOrDigit));
            if (safeProjectId.Length == 0)
                safeProjectId = "default";

            return Path.Combine(AppDataPaths.Root, StorageFolderName, safeProjectId);
        }

        public static async Task<string> CopyIntoProjectAsync(string projectId, string sourcePath, string relativePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("A source file path is required.", nameof(sourcePath));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Knowledge-base source file was not found.", sourcePath);

            string projectRoot = GetProjectRoot(projectId);
            string filesRoot = Path.Combine(projectRoot, "files");
            Directory.CreateDirectory(filesRoot);

            string safeRelativePath = MakeSafeRelativePath(relativePath, Path.GetFileName(sourcePath));
            string destination = Path.Combine(filesRoot, safeRelativePath);
            string destinationDirectory = Path.GetDirectoryName(destination) ?? filesRoot;
            Directory.CreateDirectory(destinationDirectory);

            // Preserve a folder hierarchy where possible, but never overwrite a same-named file
            // from a second source.  The suffix is stable for the bytes, so re-attaching an
            // unchanged file does not create an unbounded sequence of duplicates.
            if (File.Exists(destination) && !FilesAreEquivalent(sourcePath, destination))
            {
                string extension = Path.GetExtension(destination);
                string stem = Path.GetFileNameWithoutExtension(destination);
                string digest = await ComputeShortSha256Async(sourcePath).ConfigureAwait(false);
                destination = Path.Combine(destinationDirectory, $"{stem}-{digest}{extension}");
            }

            if (!FilesAreEquivalent(sourcePath, destination))
            {
                await using FileStream source = new(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                await using FileStream target = new(destination, FileMode.Create, FileAccess.Write, FileShare.None);
                await source.CopyToAsync(target).ConfigureAwait(false);
            }

            return destination;
        }

        public static IReadOnlyList<ProjectKnowledgeSourceFile> ExpandDroppedPaths(IEnumerable<string>? paths)
        {
            var results = new List<ProjectKnowledgeSourceFile>();
            if (paths == null)
                return results;

            foreach (string rawPath in paths.Where(path => !string.IsNullOrWhiteSpace(path)))
            {
                try
                {
                    if (File.Exists(rawPath))
                    {
                        results.Add(new ProjectKnowledgeSourceFile(rawPath, Path.GetFileName(rawPath)));
                        continue;
                    }

                    if (!Directory.Exists(rawPath))
                        continue;

                    string rootName = Path.GetFileName(rawPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    foreach (string file in Directory.EnumerateFiles(rawPath, "*", SearchOption.AllDirectories))
                    {
                        string relative = Path.Combine(rootName, Path.GetRelativePath(rawPath, file));
                        results.Add(new ProjectKnowledgeSourceFile(file, relative));
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    // Keep the rest of a folder import usable when one protected child cannot be read.
                }
                catch (IOException)
                {
                    // A concurrently changing project folder should not abort the full import.
                }
            }

            return results
                .GroupBy(file => Path.GetFullPath(file.SourcePath), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        public static void RemoveStoredFile(string projectId, string? storedPath)
        {
            if (string.IsNullOrWhiteSpace(storedPath))
                return;

            try
            {
                string projectRoot = Path.GetFullPath(GetProjectRoot(projectId));
                string candidate = Path.GetFullPath(storedPath);
                if (!candidate.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return;

                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
            catch (IOException)
            {
                // The metadata is still removed; a locked orphan is harmless and can be reclaimed later.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IO case above.
            }
        }

        public static bool IsStoredWithinProject(string projectId, string? candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath))
                return false;

            try
            {
                string projectRoot = Path.GetFullPath(GetProjectRoot(projectId));
                string candidate = Path.GetFullPath(candidatePath);
                return candidate.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch (IOException)
            {
                return false;
            }
        }

        public static void RemoveProject(string? projectId)
        {
            if (string.IsNullOrWhiteSpace(projectId))
                return;

            try
            {
                string root = GetProjectRoot(projectId);
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A failed cleanup must not prevent resetting the visible Workplace session.
            }
            catch (UnauthorizedAccessException)
            {
                // See the IO case above.
            }
        }

        private static string MakeSafeRelativePath(string? relativePath, string fallbackFileName)
        {
            string candidate = string.IsNullOrWhiteSpace(relativePath) ? fallbackFileName : relativePath;
            string[] segments = candidate
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment is not "." and not "..")
                .Select(segment => string.Concat(segment.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)))
                .Where(segment => !string.IsNullOrWhiteSpace(segment))
                .ToArray();

            return segments.Length == 0 ? fallbackFileName : Path.Combine(segments);
        }

        private static bool FilesAreEquivalent(string source, string destination)
        {
            if (!File.Exists(destination))
                return false;

            if (new FileInfo(source).Length != new FileInfo(destination).Length)
                return false;

            // Same full path is necessarily the staged copy already used by this project.
            return string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)
                || CryptographicOperations.FixedTimeEquals(ComputeSha256(source), ComputeSha256(destination));
        }

        private static byte[] ComputeSha256(string filePath)
        {
            using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return SHA256.HashData(stream);
        }

        private static async Task<string> ComputeShortSha256Async(string filePath)
        {
            await using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            return Convert.ToHexString(hash)[..12].ToLowerInvariant();
        }
    }

    public sealed record ProjectKnowledgeSourceFile(string SourcePath, string RelativePath);
}
