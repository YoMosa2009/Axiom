using System;
using System.IO;

namespace Malx_AI
{
    /// <summary>
    /// Central location for downloaded and staged application updates.
    /// Set AXIOM_UPDATE_DIR to keep update packages outside the local app-data profile.
    /// </summary>
    internal static class UpdateStoragePaths
    {
        internal const string EnvironmentVariableName = "AXIOM_UPDATE_DIR";

        /// <summary>
        /// The update folder: AXIOM_UPDATE_DIR when its drive is actually present, otherwise the
        /// profile default. A redirect to a disconnected drive (a removed USB/external disk) used
        /// to fail every update with "Could not find a part of the path".
        /// </summary>
        internal static string Root
        {
            get
            {
                string fallback = Path.GetFullPath(Path.Combine(AppDataPaths.Root, "Updates"));
                string configured = ResolveRoot(Environment.GetEnvironmentVariable(EnvironmentVariableName), fallback);
                return string.Equals(configured, fallback, StringComparison.OrdinalIgnoreCase) || IsDriveAvailable(configured)
                    ? configured
                    : fallback;
            }
        }

        internal static bool IsDriveAvailable(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                return !string.IsNullOrWhiteSpace(root) && new DriveInfo(root).IsReady;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The update root a downloaded package lives under (the parent of its "downloads"
        /// folder), so staging lands on the same drive the download was placed on.
        /// </summary>
        internal static string RootForDownloadedPackage(string packagePath)
        {
            try
            {
                for (DirectoryInfo? dir = new FileInfo(packagePath).Directory; dir != null; dir = dir.Parent)
                {
                    if (string.Equals(dir.Name, "downloads", StringComparison.OrdinalIgnoreCase) && dir.Parent != null)
                        return dir.Parent.FullName;
                }
            }
            catch
            {
            }

            return Root;
        }

        internal static string Downloads => Path.Combine(Root, "downloads");
        internal static string Staging => Path.Combine(Root, "staging");
        internal static string SuccessMarker => Path.Combine(Root, "last-update-success.txt");

        /// <summary>
        /// Applying an update needs room for the ZIP, the folder it stages into, and the backup of
        /// the files being replaced. Four times the download is a conservative allowance for a
        /// self-contained build that unpacks to roughly three times its compressed size.
        /// </summary>
        internal const int StagingSpaceMultiplier = 4;

        internal static long RequiredFreeBytes(long packageBytes) =>
            Math.Max(packageBytes, 0) * StagingSpaceMultiplier + (256L * 1024 * 1024);

        /// <summary>Free bytes on the volume holding <paramref name="path"/>, or null if unknown.</summary>
        internal static long? TryGetFreeBytes(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string? root = Path.GetPathRoot(full);
                if (string.IsNullOrWhiteSpace(root))
                    return null;
                var drive = new DriveInfo(root);
                return drive.IsReady ? drive.AvailableFreeSpace : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Chooses where this update will be downloaded and staged.
        /// </summary>
        /// <remarks>
        /// AXIOM_UPDATE_DIR is a redirect, not a promise: the drive it names can be disconnected,
        /// read-only, or full by the time an update arrives. When that happens the local app-data
        /// profile is used instead, because failing the whole update over a stale environment
        /// variable is worse than quietly using the default location. The reason is reported so it
        /// can be surfaced and logged rather than hidden.
        /// </remarks>
        internal static bool TryResolveUsableRoot(long packageBytes, out string root, out string? notice)
        {
            notice = null;
            long required = RequiredFreeBytes(packageBytes);
            string configured = Root;
            string fallback = Path.GetFullPath(Path.Combine(AppDataPaths.Root, "Updates"));

            if (IsUsable(configured, required, out string? configuredProblem))
            {
                root = configured;
                return true;
            }

            bool redirected = !string.Equals(configured, fallback, StringComparison.OrdinalIgnoreCase);
            if (redirected && IsUsable(fallback, required, out _))
            {
                notice = $"{configured} is unusable ({configuredProblem}); downloading to {fallback} instead.";
                root = fallback;
                return true;
            }

            notice = configuredProblem;
            root = configured;
            return false;
        }

        private static bool IsUsable(string candidate, long requiredBytes, out string? problem)
        {
            problem = null;
            try
            {
                Directory.CreateDirectory(candidate);
            }
            catch (Exception ex)
            {
                problem = $"the folder could not be created ({ex.Message})";
                return false;
            }

            long? free = TryGetFreeBytes(candidate);
            if (free == null)
            {
                problem = "the drive is not available";
                return false;
            }

            if (free.Value < requiredBytes)
            {
                problem = $"only {FormatBytes(free.Value)} free, and about {FormatBytes(requiredBytes)} is needed";
                return false;
            }

            return true;
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
                return (bytes / 1024.0 / 1024 / 1024).ToString("0.#") + " GB";
            if (bytes >= 1024 * 1024)
                return (bytes / 1024.0 / 1024).ToString("0") + " MB";
            return (bytes / 1024.0).ToString("0") + " KB";
        }

        internal static string ResolveRoot(string? configuredPath, string fallbackPath)
        {
            string fallback = Path.GetFullPath(fallbackPath);
            if (string.IsNullOrWhiteSpace(configuredPath))
                return fallback;

            try
            {
                string expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
                return Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
