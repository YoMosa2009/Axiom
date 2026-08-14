using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    public enum UpdatePackageKind
    {
        None,
        Zip,
        Installer
    }

    public sealed class UpdateCheckResult
    {
        public string LatestVersionTag { get; init; } = string.Empty;
        public Version LatestVersion { get; init; } = new(0, 0, 0, 0);
        public Version CurrentVersion { get; init; } = new(0, 0, 0, 0);
        public string ReleasePageUrl { get; init; } = string.Empty;
        public string ReleaseNotes { get; init; } = string.Empty;
        public DateTimeOffset? PublishedAt { get; init; }
        public string PackageDownloadUrl { get; init; } = string.Empty;
        public string PackageFileName { get; init; } = string.Empty;
        public string PackageSha256 { get; init; } = string.Empty;
        public long PackageSizeBytes { get; init; }
        public UpdatePackageKind PackageKind { get; init; }
        public bool IsNewerVersionAvailable { get; init; }

        public bool HasPackageAsset => PackageKind != UpdatePackageKind.None
            && !string.IsNullOrWhiteSpace(PackageDownloadUrl);

        // Compatibility aliases for installer assets while the ZIP updater is the preferred path.
        public string InstallerDownloadUrl => PackageDownloadUrl;
        public string InstallerFileName => PackageFileName;
        public bool HasInstallerAsset => HasPackageAsset;
    }

    /// <summary>
    /// Pure GitHub release JSON parsing kept separate from networking so release selection and
    /// version comparisons can be covered by the cross-platform unit-test project.
    /// </summary>
    internal static class UpdateReleaseParser
    {
        private static readonly Regex VersionInTagRegex = new(
            @"(?<version>\d+(?:\.\d+){1,3})",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        internal static UpdateCheckResult? Parse(string json, Version currentVersion)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (ReadBoolean(root, "draft") || ReadBoolean(root, "prerelease"))
                return null;

            string tag = ReadString(root, "tag_name");
            if (!TryParseVersionTag(tag, out Version latestVersion))
                return null;

            Version normalizedCurrent = NormalizeVersion(currentVersion);
            ReleaseAsset? package = FindBestPackageAsset(root);
            DateTimeOffset? publishedAt = DateTimeOffset.TryParse(ReadString(root, "published_at"), out DateTimeOffset parsedPublishedAt)
                ? parsedPublishedAt
                : null;

            return new UpdateCheckResult
            {
                LatestVersionTag = tag.Trim(),
                LatestVersion = latestVersion,
                CurrentVersion = normalizedCurrent,
                ReleasePageUrl = ReadString(root, "html_url"),
                ReleaseNotes = ReadString(root, "body"),
                PublishedAt = publishedAt,
                PackageDownloadUrl = package?.DownloadUrl ?? string.Empty,
                PackageFileName = package?.Name ?? string.Empty,
                PackageSha256 = package?.Sha256 ?? string.Empty,
                PackageSizeBytes = package?.SizeBytes ?? 0,
                PackageKind = package?.Kind ?? UpdatePackageKind.None,
                IsNewerVersionAvailable = latestVersion > normalizedCurrent
            };
        }

        internal static bool TryParseVersionTag(string? tag, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            Match match = VersionInTagRegex.Match(tag ?? string.Empty);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out Version? parsed))
                return false;

            version = NormalizeVersion(parsed);
            return true;
        }

        internal static Version NormalizeVersion(Version? version)
        {
            version ??= new Version(0, 0, 0, 0);
            return new Version(
                Math.Max(0, version.Major),
                Math.Max(0, version.Minor),
                Math.Max(0, version.Build),
                Math.Max(0, version.Revision));
        }

        internal static string FormatVersion(Version? version)
        {
            Version normalized = NormalizeVersion(version);
            return $"{normalized.Major}.{normalized.Minor}.{normalized.Build}";
        }

        private static ReleaseAsset? FindBestPackageAsset(JsonElement root)
        {
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return null;

            var candidates = new List<ReleaseAsset>();
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = ReadString(asset, "name");
                string url = ReadString(asset, "browser_download_url");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(url))
                    continue;

                string lowerName = name.ToLowerInvariant();
                if (lowerName.Contains("source") || lowerName.Contains("symbols") || lowerName.EndsWith(".pdb.zip"))
                    continue;

                UpdatePackageKind kind = lowerName.EndsWith(".zip")
                    ? UpdatePackageKind.Zip
                    : lowerName.EndsWith(".exe") || lowerName.EndsWith(".msi")
                        ? UpdatePackageKind.Installer
                        : UpdatePackageKind.None;
                if (kind == UpdatePackageKind.None)
                    continue;

                int score = kind == UpdatePackageKind.Zip ? 100 : 70;
                if (lowerName.Contains("axiom")) score += 40;
                if (lowerName.Contains("win-x64") || lowerName.Contains("windows-x64")) score += 30;
                if (lowerName.Contains("clean")) score += 10;
                if (lowerName.Contains("setup") || lowerName.Contains("install")) score += kind == UpdatePackageKind.Installer ? 20 : 0;

                string digest = ReadString(asset, "digest");
                string sha256 = digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest[7..].Trim()
                    : string.Empty;
                long size = asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsedSize)
                    ? parsedSize
                    : 0;

                candidates.Add(new ReleaseAsset(name, url, sha256, size, kind, score));
            }

            return candidates
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        private static string ReadString(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;

        private static bool ReadBoolean(JsonElement element, string propertyName)
            => element.TryGetProperty(propertyName, out JsonElement value)
                && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                && value.GetBoolean();

        private sealed record ReleaseAsset(
            string Name,
            string DownloadUrl,
            string Sha256,
            long SizeBytes,
            UpdatePackageKind Kind,
            int Score);
    }
}
