using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace Malx_AI
{
    /// <summary>
    /// Central user-data locations under %LOCALAPPDATA%\Axiom. Persistence previously wrote
    /// working-directory-relative paths ("ChatHistory", "axiom_data.db"), which for an installed
    /// build resolve inside the install folder — an installer cleaning that folder wiped chats,
    /// settings, and the API key, and Program Files is typically not writable at runtime.
    /// The first reference to this class migrates any legacy working-directory data once.
    /// </summary>
    public static class AppDataPaths
    {
        public static string Root { get; }
        public static string ChatHistory { get; }
        public static string Logs { get; }
        public static string DatabaseFile { get; }
        public static string WebView2UserData { get; }

        static AppDataPaths()
        {
            Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Axiom");
            ChatHistory = Path.Combine(Root, "ChatHistory");
            Logs = Path.Combine(Root, "logs");
            DatabaseFile = Path.Combine(Root, "axiom_data.db");
            // WebView2's default user-data folder sits next to the exe — not writable under
            // Program Files, which silently breaks every WebView2 pane in an installed build.
            WebView2UserData = Path.Combine(Root, "WebView2");

            try
            {
                Directory.CreateDirectory(Root);
                Directory.CreateDirectory(ChatHistory);
                Directory.CreateDirectory(Logs);
                Directory.CreateDirectory(WebView2UserData);
                MigrateLegacyWorkingDirectoryData();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AppDataPaths init error: {ex.Message}");
            }
        }

        /// <summary>
        /// Copies legacy data from the process working directory into the app-data root.
        /// Existing target files are never overwritten, so the migration is idempotent and a
        /// stale legacy copy can't clobber data the app has already written to the new location.
        /// </summary>
        private static void MigrateLegacyWorkingDirectoryData()
        {
            // The exe directory comes first: relative paths historically resolved against the
            // working directory, which for normal launches (double-click, VS debug) IS the exe
            // directory — that's where the authoritative data accumulated. The current working
            // directory is only a fallback for launches that overrode it, and because existing
            // target files are never overwritten, the exe-directory copy always wins conflicts.
            string[] legacyRoots = { AppContext.BaseDirectory, Directory.GetCurrentDirectory() };

            foreach (string legacyRoot in legacyRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(legacyRoot) || PathsEqual(legacyRoot, Root))
                    continue;

                try
                {
                    // SQLite database plus WAL/SHM sidecars — the family must come whole from ONE
                    // root: pairing a .db from one location with a -wal from another corrupts it.
                    // So the whole family is skipped once a database exists at the target.
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
    }
}
