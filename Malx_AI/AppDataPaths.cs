using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Malx_AI
{
    /// <summary>
    /// Central user-data locations under %LOCALAPPDATA%.
    /// <para>
    /// <b>Debug / VS runs</b> use <c>Axiom-Dev</c> so day-to-day development never pollutes the
    /// release profile that end users (and your published-zip smoke tests) get.
    /// <b>Release / published builds</b> use <c>Axiom</c>.
    /// </para>
    /// Override with environment variable <c>AXIOM_DATA_DIR</c> for a fully isolated folder
    /// (useful when smoke-testing a release zip without wiping your real release profile).
    /// </summary>
    public static class AppDataPaths
    {
        /// <summary>Env var: absolute path for all app data (takes precedence over profile roots).</summary>
        public const string DataDirEnvironmentVariable = "AXIOM_DATA_DIR";

        public static string Root { get; }
        public static string ChatHistory { get; }
        public static string Logs { get; }
        public static string DatabaseFile { get; }
        public static string WebView2UserData { get; }
        public static string EmbeddingModels { get; }
        public static string LocalModels { get; }

        /// <summary>Human-readable profile name for diagnostics (dev / release / custom).</summary>
        public static string ProfileLabel { get; }

        static AppDataPaths()
        {
            Root = ResolveRoot(out string profileLabel);
            ProfileLabel = profileLabel;
            ChatHistory = Path.Combine(Root, "ChatHistory");
            Logs = Path.Combine(Root, "logs");
            DatabaseFile = Path.Combine(Root, "axiom_data.db");
            EmbeddingModels = Path.Combine(Root, "EmbeddingModels");
            LocalModels = Path.Combine(Root, "Models");
            // WebView2's default user-data folder sits next to the exe — not writable under
            // Program Files, which silently breaks every WebView2 pane in an installed build.
            WebView2UserData = Path.Combine(Root, "WebView2");

            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(ChatHistory);
                Directory.CreateDirectory(Logs);
                Directory.CreateDirectory(EmbeddingModels);
                Directory.CreateDirectory(LocalModels);
                Directory.CreateDirectory(WebView2UserData);

#if DEBUG
                // Bring existing developer data from the old shared %LocalAppData%\Axiom folder
                // into Axiom-Dev once so switching profiles doesn't look like "data disappeared".
                TryMigrateSharedAxiomIntoDevProfile();
                // Old relative paths under bin/Debug — only for development.
                MigrateLegacyWorkingDirectoryData();
#else
                // Release/published builds: do NOT scrape ChatHistory / DB out of the install
                // folder. That path is how a poorly packed zip could ship the author's personal
                // chats into every first launch on a machine. End-user data lives only in Root.
#endif
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppDataPaths init error: {ex.Message}");
            }
        }

        private static string ResolveRoot(out string profileLabel)
        {
            string? overrideDir = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
            if (!string.IsNullOrWhiteSpace(overrideDir))
            {
                profileLabel = "custom";
                return Path.GetFullPath(overrideDir.Trim());
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

#if DEBUG
            // Isolated from the release/end-user profile.
            profileLabel = "dev";
            return Path.Combine(localAppData, "Axiom-Dev");
#else
            profileLabel = "release";
            return Path.Combine(localAppData, "Axiom");
#endif
        }

#if DEBUG
        private static void TryMigrateSharedAxiomIntoDevProfile()
        {
            try
            {
                string legacyShared = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Axiom");

                if (!Directory.Exists(legacyShared) || PathsEqual(legacyShared, Root))
                    return;

                // Only migrate when the dev profile looks empty (first switch to Axiom-Dev).
                bool devHasDb = File.Exists(DatabaseFile);
                bool devHasChats = Directory.Exists(ChatHistory)
                    && Directory.EnumerateFileSystemEntries(ChatHistory).Any();
                if (devHasDb || devHasChats)
                    return;

                string legacyDb = Path.Combine(legacyShared, "axiom_data.db");
                if (File.Exists(legacyDb) && !File.Exists(DatabaseFile))
                {
                    foreach (string legacyDbFile in Directory.GetFiles(legacyShared, "axiom_data.db*"))
                        File.Copy(legacyDbFile, Path.Combine(Root, Path.GetFileName(legacyDbFile)), overwrite: false);
                }

                string legacyChat = Path.Combine(legacyShared, "ChatHistory");
                if (Directory.Exists(legacyChat))
                    CopyDirectoryIfMissing(legacyChat, ChatHistory);

                // MCP tokens / connector state side-car
                string legacyMcp = Path.Combine(legacyShared, "mcp_connector_state.dpapi");
                string targetMcp = Path.Combine(Root, "mcp_connector_state.dpapi");
                if (File.Exists(legacyMcp) && !File.Exists(targetMcp))
                    File.Copy(legacyMcp, targetMcp, overwrite: false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Dev profile migration error: {ex.Message}");
            }
        }

        /// <summary>
        /// Copies legacy data from the process working directory into the app-data root.
        /// DEBUG only — Release must never pull personal data out of a publish folder.
        /// </summary>
        private static void MigrateLegacyWorkingDirectoryData()
        {
            string[] legacyRoots = { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

            foreach (string legacyRoot in legacyRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(legacyRoot) || PathsEqual(legacyRoot, Root))
                    continue;

                try
                {
                    if (!File.Exists(DatabaseFile))
                    {
                        foreach (string legacyDbFile in Directory.GetFiles(legacyRoot, "axiom_data.db*"))
                            File.Copy(legacyDbFile, Path.Combine(Root, Path.GetFileName(legacyDbFile)), overwrite: true);
                    }

                    string legacyChatHistory = Path.Combine(legacyRoot, "ChatHistory");
                    if (Directory.Exists(legacyChatHistory) && !PathsEqual(legacyChatHistory, ChatHistory))
                        CopyDirectoryIfMissing(legacyChatHistory, ChatHistory);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Legacy data migration error ({legacyRoot}): {ex.Message}");
                }
            }
        }
#endif

        // KV-cache snapshots are per-run caches the app purges and rebuilds anyway — copying
        // them (hundreds of MB) would stall the first launch for nothing.
        private static readonly string[] MigrationExcludedFolderNames = { "KvStates", "CouncilKvStates" };

        private static void CopyDirectoryIfMissing(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);

            foreach (string sourceFile in Directory.GetFiles(sourceDir))
            {
                string targetFile = Path.Combine(targetDir, Path.GetFileName(sourceFile));
                if (File.Exists(targetFile))
                    continue;

                try
                {
                    File.Copy(sourceFile, targetFile);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Legacy file migration skipped ({Path.GetFileName(sourceFile)}): {ex.Message}");
                }
            }

            foreach (string sourceSubDir in Directory.GetDirectories(sourceDir))
            {
                string folderName = Path.GetFileName(sourceSubDir);
                if (MigrationExcludedFolderNames.Any(excluded => string.Equals(excluded, folderName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                CopyDirectoryIfMissing(sourceSubDir, Path.Combine(targetDir, folderName));
            }
        }

        private static bool PathsEqual(string left, string right)
            => string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Deletes the entire current profile root (chats, DB, connectors, WebView2 cache).
        /// Caller must restart the app for a clean session. Returns false on failure.
        /// </summary>
        public static bool TryResetAllLocalData(out string? error)
        {
            error = null;
            try
            {
                if (Directory.Exists(Root))
                {
                    // Best-effort close SQLite handles by renaming, then delete.
                    string trash = Root + ".trash-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                    try
                    {
                        Directory.Move(Root, trash);
                        Directory.Delete(trash, recursive: true);
                    }
                    catch
                    {
                        // Fallback: delete contents in place if rename fails (locked files).
                        DeleteDirectoryContents(Root);
                    }
                }

                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(ChatHistory);
                Directory.CreateDirectory(Logs);
                Directory.CreateDirectory(EmbeddingModels);
                Directory.CreateDirectory(LocalModels);
                Directory.CreateDirectory(WebView2UserData);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void DeleteDirectoryContents(string directory)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch
                {
                    // skip locked
                }
            }

            foreach (string dir in Directory.GetDirectories(directory))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch { /* skip locked */ }
            }
        }
    }
}
