using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Malx_AI
{
    internal static class UpdatePackageSafety
    {
        private static readonly string[] ProtectedDirectoryNames =
        [
            "ChatHistory", "WebView2", "logs", "Models", "KvStates",
            "CouncilKvStates", "WorkplaceExports", "Updates"
        ];

        internal static bool TryResolvePathUnderRoot(
            string root,
            string relativePath,
            out string normalizedRelativePath,
            out string fullPath)
        {
            normalizedRelativePath = string.Empty;
            fullPath = string.Empty;

            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(relativePath))
                return false;

            string candidate = relativePath.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(candidate) || candidate.IndexOf(':') >= 0)
                return false;

            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string resolved = Path.GetFullPath(Path.Combine(fullRoot, candidate));
            string rootPrefix = fullRoot + Path.DirectorySeparatorChar;
            if (!resolved.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
                return false;

            normalizedRelativePath = Path.GetRelativePath(fullRoot, resolved).Replace(Path.DirectorySeparatorChar, '/');
            if (normalizedRelativePath is "." or ".." || normalizedRelativePath.StartsWith("../", StringComparison.Ordinal))
                return false;

            fullPath = resolved;
            return true;
        }

        internal static bool IsProtectedUserDataPath(string relativePath)
        {
            string normalized = (relativePath ?? string.Empty).Replace('\\', '/').Trim('/');
            if (string.IsNullOrWhiteSpace(normalized))
                return true;

            string firstSegment = normalized.Split('/', 2)[0];
            if (ProtectedDirectoryNames.Any(name => string.Equals(name, firstSegment, StringComparison.OrdinalIgnoreCase)))
                return true;

            string fileName = Path.GetFileName(normalized);
            return fileName.StartsWith("axiom_data.db", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("mcp_connector_state", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("workplace_session.json", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("chats_index.json", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("chat_workspace_state.json", StringComparison.OrdinalIgnoreCase)
                || fileName.StartsWith("smart_compaction_settings.json", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith("_advanced_state.json", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith("_client_secret.txt", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith("_client_id.txt", StringComparison.OrdinalIgnoreCase)
                || fileName.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase);
        }

        internal static IReadOnlySet<string> ParseManifest(string manifestText)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in (manifestText ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim().Replace('\\', '/').TrimStart('/');
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
                    continue;
                paths.Add(line);
            }

            return paths;
        }
    }
}
