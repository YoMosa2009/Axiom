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

        internal static string Root => ResolveRoot(
            Environment.GetEnvironmentVariable(EnvironmentVariableName),
            Path.Combine(AppDataPaths.Root, "Updates"));

        internal static string Downloads => Path.Combine(Root, "downloads");
        internal static string Staging => Path.Combine(Root, "staging");
        internal static string SuccessMarker => Path.Combine(Root, "last-update-success.txt");

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
