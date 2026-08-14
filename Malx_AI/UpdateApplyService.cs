using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal sealed record PreparedUpdate(
        string PayloadDirectory,
        string StagedExecutablePath,
        string TargetDirectory,
        Version Version,
        string VersionTag);

    internal sealed record UpdateApplyResult(bool Succeeded, string ErrorMessage = "");

    /// <summary>
    /// Stages and applies folder-based Axiom ZIP releases. The staged copy of the new Axiom
    /// executable runs in updater mode, waits for the current process to exit, atomically swaps
    /// package-managed files, then starts the installed copy. User profile paths are never touched.
    /// </summary>
    internal static class UpdateApplyService
    {
        internal const string ApplyUpdateArgument = "--axiom-apply-update";
        internal const string UpdateManifestFileName = "AXIOM_UPDATE_MANIFEST.txt";
        private const string MainExecutableFileName = "Malx_AI.exe";
        private const int MaximumArchiveEntries = 75000;
        private const long MaximumExpandedBytes = 12L * 1024 * 1024 * 1024;
        private static readonly TimeSpan ProcessExitTimeout = TimeSpan.FromMinutes(2);

        internal static bool IsUpdaterInvocation(IEnumerable<string>? arguments)
            => arguments?.Any(argument => string.Equals(argument, ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase)) == true;

        internal static async Task<PreparedUpdate> PrepareZipUpdateAsync(
            UpdateCheckResult update,
            string zipPath,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(update);
            if (update.PackageKind != UpdatePackageKind.Zip)
                throw new InvalidOperationException("The selected GitHub release asset is not a ZIP package.");
            if (!File.Exists(zipPath))
                throw new FileNotFoundException("The downloaded update package was not found.", zipPath);

            string version = UpdateReleaseParser.FormatVersion(update.LatestVersion);
            string stagingRoot = Path.Combine(
                AppDataPaths.Root,
                "Updates",
                "staging",
                $"{version}-{Guid.NewGuid():N}");
            string extractRoot = Path.Combine(stagingRoot, "package");
            Directory.CreateDirectory(extractRoot);

            try
            {
                await Task.Run(() => ExtractArchiveSafely(zipPath, extractRoot, token), token).ConfigureAwait(false);
                string payloadDirectory = LocateAndValidatePayload(extractRoot, update.LatestVersion);
                string executable = Path.Combine(payloadDirectory, MainExecutableFileName);

                return new PreparedUpdate(
                    payloadDirectory,
                    executable,
                    Path.GetFullPath(AppContext.BaseDirectory),
                    update.LatestVersion,
                    update.LatestVersionTag);
            }
            catch
            {
                TryDeleteDirectory(stagingRoot);
                throw;
            }
        }

        internal static Process LaunchPreparedUpdate(PreparedUpdate update, int currentProcessId)
        {
            ArgumentNullException.ThrowIfNull(update);
            ValidateInstallRoot(update.TargetDirectory);

            var startInfo = new ProcessStartInfo
            {
                FileName = update.StagedExecutablePath,
                WorkingDirectory = update.PayloadDirectory,
                UseShellExecute = true
            };
            startInfo.ArgumentList.Add(ApplyUpdateArgument);
            startInfo.ArgumentList.Add("--wait-pid");
            startInfo.ArgumentList.Add(currentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--source");
            startInfo.ArgumentList.Add(update.PayloadDirectory);
            startInfo.ArgumentList.Add("--target");
            startInfo.ArgumentList.Add(update.TargetDirectory);
            startInfo.ArgumentList.Add("--version");
            startInfo.ArgumentList.Add(UpdateReleaseParser.FormatVersion(update.Version));

            if (!CanWriteToDirectory(update.TargetDirectory))
                startInfo.Verb = "runas";

            return Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows could not start the Axiom update helper.");
        }

        internal static UpdateApplyResult ApplyUpdateAndRestart(string[] arguments)
        {
            try
            {
                Dictionary<string, string> values = ParseArguments(arguments);
                if (!values.TryGetValue("--wait-pid", out string? pidText)
                    || !int.TryParse(pidText, out int waitPid)
                    || waitPid <= 0)
                {
                    throw new InvalidOperationException("The update helper did not receive a valid Axiom process id.");
                }

                string source = RequireArgument(values, "--source");
                string target = RequireArgument(values, "--target");
                string version = RequireArgument(values, "--version");
                source = Path.GetFullPath(source);
                target = Path.GetFullPath(target);
                ValidateInstallRoot(target);
                ValidateSourceRoot(source);
                if (!UpdateReleaseParser.TryParseVersionTag(version, out Version expectedVersion)
                    || !string.Equals(
                        Path.GetFullPath(LocateAndValidatePayload(source, expectedVersion)).TrimEnd(Path.DirectorySeparatorChar),
                        source.TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("The staged update payload could not be revalidated.");
                }

                WaitForProcessExit(waitPid);
                ApplyPackageTransaction(source, target);
                WriteSuccessMarker(version);

                string installedExecutable = Path.Combine(target, MainExecutableFileName);
                Process.Start(new ProcessStartInfo
                {
                    FileName = installedExecutable,
                    WorkingDirectory = target,
                    UseShellExecute = true
                });

                return new UpdateApplyResult(true);
            }
            catch (Exception ex)
            {
                WriteUpdateLog("Update apply failed: " + ex);
                TryRestartInstalledCopy(arguments);
                return new UpdateApplyResult(false, ex.Message);
            }
        }

        internal static string? ConsumeSuccessMarker()
        {
            string marker = GetSuccessMarkerPath();
            try
            {
                if (!File.Exists(marker))
                    return null;

                string version = File.ReadAllText(marker, Encoding.UTF8).Trim();
                File.Delete(marker);
                return string.IsNullOrWhiteSpace(version) ? null : version;
            }
            catch
            {
                return null;
            }
        }

        internal static void CleanupOldStagingDirectories()
        {
            string stagingRoot = Path.Combine(AppDataPaths.Root, "Updates", "staging");
            if (!Directory.Exists(stagingRoot))
                return;

            foreach (string directory in Directory.EnumerateDirectories(stagingRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < DateTime.UtcNow.AddDays(-2))
                        Directory.Delete(directory, recursive: true);
                }
                catch
                {
                    // A running updater may still own this directory; leave it for a later launch.
                }
            }
        }

        private static void ExtractArchiveSafely(string zipPath, string extractRoot, CancellationToken token)
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            if (archive.Entries.Count == 0 || archive.Entries.Count > MaximumArchiveEntries)
                throw new InvalidDataException("The update archive has an invalid number of entries.");

            long expandedBytes = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                token.ThrowIfCancellationRequested();

                int unixFileType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixFileType == 0xA000)
                    throw new InvalidDataException($"The update archive contains an unsupported symbolic link: {entry.FullName}");

                expandedBytes = checked(expandedBytes + Math.Max(0, entry.Length));
                if (expandedBytes > MaximumExpandedBytes)
                    throw new InvalidDataException("The expanded update exceeds Axiom's 12 GB safety limit.");

                if (!UpdatePackageSafety.TryResolvePathUnderRoot(
                        extractRoot,
                        entry.FullName,
                        out _,
                        out string destination))
                {
                    throw new InvalidDataException($"The update archive contains an unsafe path: {entry.FullName}");
                }

                bool isDirectory = string.IsNullOrEmpty(entry.Name)
                    || entry.FullName.EndsWith('/')
                    || entry.FullName.EndsWith('\\');
                if (isDirectory)
                {
                    Directory.CreateDirectory(destination);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: false);
            }
        }

        private static string LocateAndValidatePayload(string extractRoot, Version expectedVersion)
        {
            string[] executables = Directory.GetFiles(extractRoot, MainExecutableFileName, SearchOption.AllDirectories);
            if (executables.Length != 1)
                throw new InvalidDataException("The update ZIP must contain exactly one Malx_AI.exe.");

            string payloadRoot = Path.GetDirectoryName(executables[0])!;
            ValidateSourceRoot(payloadRoot);

            string manifestPath = Path.Combine(payloadRoot, UpdateManifestFileName);
            IReadOnlySet<string> manifest = UpdatePackageSafety.ParseManifest(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (!manifest.Contains(MainExecutableFileName) || !manifest.Contains(UpdateManifestFileName))
                throw new InvalidDataException("The update manifest does not identify the Axiom executable and manifest.");

            var actualFiles = Directory
                .EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(payloadRoot, path).Replace('\\', '/'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (string manifestEntry in manifest)
            {
                if (!UpdatePackageSafety.TryResolvePathUnderRoot(payloadRoot, manifestEntry, out string normalized, out string fullPath)
                    || UpdatePackageSafety.IsProtectedUserDataPath(normalized)
                    || !File.Exists(fullPath))
                {
                    throw new InvalidDataException($"The update manifest contains an invalid or missing file: {manifestEntry}");
                }
            }

            if (!actualFiles.SetEquals(manifest))
                throw new InvalidDataException("The update ZIP contents do not match AXIOM_UPDATE_MANIFEST.txt.");

            FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(executables[0]);
            string packagedVersion = versionInfo.ProductVersion ?? versionInfo.FileVersion ?? string.Empty;
            if (!UpdateReleaseParser.TryParseVersionTag(packagedVersion, out Version parsedVersion)
                || parsedVersion != UpdateReleaseParser.NormalizeVersion(expectedVersion))
            {
                throw new InvalidDataException(
                    $"The packaged executable version ({packagedVersion}) does not match the GitHub release tag ({UpdateReleaseParser.FormatVersion(expectedVersion)}).");
            }

            return payloadRoot;
        }

        private static void ApplyPackageTransaction(string sourceRoot, string targetRoot)
        {
            string newManifestPath = Path.Combine(sourceRoot, UpdateManifestFileName);
            IReadOnlySet<string> newManifest = UpdatePackageSafety.ParseManifest(File.ReadAllText(newManifestPath, Encoding.UTF8));
            IReadOnlySet<string> oldManifest = ReadInstalledManifest(targetRoot);
            var transitions = new List<FileTransition>();

            try
            {
                // Copy every source file to a temporary sibling first. No installed file changes
                // until the complete new package is readable and fits on disk.
                foreach (string entry in newManifest.OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (!UpdatePackageSafety.TryResolvePathUnderRoot(sourceRoot, entry, out string normalized, out string source)
                        || !UpdatePackageSafety.TryResolvePathUnderRoot(targetRoot, normalized, out _, out string target)
                        || UpdatePackageSafety.IsProtectedUserDataPath(normalized)
                        || !File.Exists(source))
                    {
                        throw new InvalidDataException($"Unsafe update manifest entry: {entry}");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    string pending = target + ".axiom-update-new";
                    string backup = target + ".axiom-update-old";
                    TryDeleteFile(pending);
                    File.Copy(source, pending, overwrite: true);
                    transitions.Add(new FileTransition(target, pending, backup, File.Exists(target)));
                }

                foreach (FileTransition transition in transitions)
                {
                    TryDeleteFile(transition.BackupPath);
                    if (transition.HadOriginal)
                    {
                        File.Move(transition.TargetPath, transition.BackupPath, overwrite: true);
                        transition.OriginalMoved = true;
                    }
                    File.Move(transition.PendingPath, transition.TargetPath, overwrite: true);
                    transition.Swapped = true;
                }

                foreach (string staleEntry in oldManifest.Except(newManifest, StringComparer.OrdinalIgnoreCase))
                {
                    if (UpdatePackageSafety.TryResolvePathUnderRoot(targetRoot, staleEntry, out string normalized, out string stalePath)
                        && !UpdatePackageSafety.IsProtectedUserDataPath(normalized))
                    {
                        TryDeleteFile(stalePath);
                    }
                }

                foreach (FileTransition transition in transitions)
                    TryDeleteFile(transition.BackupPath);

                WriteUpdateLog($"Updated Axiom to {FileVersionInfo.GetVersionInfo(Path.Combine(targetRoot, MainExecutableFileName)).ProductVersion}.");
            }
            catch
            {
                foreach (FileTransition transition in transitions.AsEnumerable().Reverse())
                {
                    TryDeleteFile(transition.PendingPath);
                    if (transition.Swapped || transition.OriginalMoved)
                        TryDeleteFile(transition.TargetPath);
                    if (transition.OriginalMoved && File.Exists(transition.BackupPath))
                        File.Move(transition.BackupPath, transition.TargetPath, overwrite: true);
                }

                throw;
            }
        }

        private static IReadOnlySet<string> ReadInstalledManifest(string targetRoot)
        {
            string path = Path.Combine(targetRoot, UpdateManifestFileName);
            try
            {
                return File.Exists(path)
                    ? UpdatePackageSafety.ParseManifest(File.ReadAllText(path, Encoding.UTF8))
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void ValidateSourceRoot(string sourceRoot)
        {
            string fullRoot = Path.GetFullPath(sourceRoot);
            if (!Directory.Exists(fullRoot)
                || !File.Exists(Path.Combine(fullRoot, MainExecutableFileName))
                || !File.Exists(Path.Combine(fullRoot, UpdateManifestFileName)))
            {
                throw new InvalidDataException("The staged Axiom package is incomplete.");
            }
        }

        private static void ValidateInstallRoot(string targetRoot)
        {
            string fullRoot = Path.GetFullPath(targetRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string? driveRoot = Path.GetPathRoot(fullRoot)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(fullRoot)
                || string.Equals(fullRoot, driveRoot, StringComparison.OrdinalIgnoreCase)
                || !Directory.Exists(fullRoot)
                || !File.Exists(Path.Combine(fullRoot, MainExecutableFileName)))
            {
                throw new InvalidOperationException("Axiom could not verify its installation folder, so the update was not applied.");
            }
        }

        private static void WaitForProcessExit(int processId)
        {
            try
            {
                using Process process = Process.GetProcessById(processId);
                if (!process.WaitForExit((int)ProcessExitTimeout.TotalMilliseconds))
                    throw new TimeoutException("Axiom did not close within two minutes. The update was cancelled.");
            }
            catch (ArgumentException)
            {
                // The process already exited between launch and lookup.
            }
        }

        private static bool CanWriteToDirectory(string directory)
        {
            string probe = Path.Combine(directory, $".axiom-update-write-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
                return true;
            }
            catch
            {
                TryDeleteFile(probe);
                return false;
            }
        }

        private static Dictionary<string, string> ParseArguments(string[] arguments)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (arguments[i].StartsWith("--", StringComparison.Ordinal)
                    && !string.Equals(arguments[i], ApplyUpdateArgument, StringComparison.OrdinalIgnoreCase))
                {
                    values[arguments[i]] = arguments[++i];
                }
            }

            return values;
        }

        private static string RequireArgument(IReadOnlyDictionary<string, string> values, string name)
            => values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : throw new InvalidOperationException($"The update helper is missing {name}.");

        private static void TryRestartInstalledCopy(string[] arguments)
        {
            try
            {
                Dictionary<string, string> values = ParseArguments(arguments);
                string target = RequireArgument(values, "--target");
                string executable = Path.Combine(Path.GetFullPath(target), MainExecutableFileName);
                if (File.Exists(executable))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        WorkingDirectory = Path.GetDirectoryName(executable)!,
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
                // The error dialog tells the user where the install folder is if restart is impossible.
            }
        }

        private static void WriteSuccessMarker(string version)
        {
            string marker = GetSuccessMarkerPath();
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, version.Trim(), Encoding.UTF8);
        }

        private static string GetSuccessMarkerPath()
            => Path.Combine(AppDataPaths.Root, "Updates", "last-update-success.txt");

        private static void WriteUpdateLog(string message)
        {
            try
            {
                Directory.CreateDirectory(AppDataPaths.Logs);
                File.AppendAllText(
                    Path.Combine(AppDataPaths.Logs, "update.log"),
                    $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
            catch
            {
                // Update logging must never turn a successful file swap into a failed update.
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                }
            }
            catch
            {
                // Cleanup is best effort; transactional rollback handles required files explicitly.
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch
            {
                // A later cleanup pass removes abandoned staging directories.
            }
        }

        private sealed class FileTransition(
            string targetPath,
            string pendingPath,
            string backupPath,
            bool hadOriginal)
        {
            public string TargetPath { get; } = targetPath;
            public string PendingPath { get; } = pendingPath;
            public string BackupPath { get; } = backupPath;
            public bool HadOriginal { get; } = hadOriginal;
            public bool OriginalMoved { get; set; }
            public bool Swapped { get; set; }
        }
    }
}
