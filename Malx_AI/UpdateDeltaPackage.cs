using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace Malx_AI
{
    /// <summary>One delta package on a release: the files that changed since one earlier version.</summary>
    public sealed record UpdateDeltaAsset(Version BaseVersion, string Name, string DownloadUrl, string Sha256, long SizeBytes);

    /// <summary>
    /// Delta (differential) OTA updates: download only the files that changed since the
    /// installed version, and rebuild the full new package locally from the installed copy.
    /// </summary>
    /// <remarks>
    /// Owner request (2026-11-26). Every update downloaded the whole ~380 MB clean ZIP, while the
    /// updater's own log shows only 4-5 files actually change per release ("5 file(s) replaced,
    /// 875 already up to date"): the app's own assemblies, executables and manifest, a few MB.
    ///
    /// A release may carry "Axiom-v{new}-delta-from-v{old}.axdelta": a ZIP holding the changed
    /// files plus AXIOM_DELTA.json, which lists the SHA-256 of EVERY file in the new package.
    /// The ".axdelta" extension keeps it invisible to older updaters (they only take .zip/.exe).
    /// The client copies each listed file from the delta or, when unchanged, from the installed
    /// folder, checks every hash, and hands the rebuilt folder to the same validation and
    /// transaction as a full ZIP. Anything that does not match exactly (an edited or missing
    /// installed file, a different base version) fails closed and the full ZIP is used instead.
    /// New installs always use the full installer.
    /// </remarks>
    public static class UpdateDeltaPackage
    {
        public const string IndexFileName = "AXIOM_DELTA.json";
        public const string Extension = ".axdelta";

        private static readonly Regex AssetNameRegex = new(
            @"^Axiom-v?(?<to>\d+(?:\.\d+){1,3})-delta-from-v?(?<from>\d+(?:\.\d+){1,3})\.axdelta$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static string AssetName(Version target, Version baseVersion) =>
            $"Axiom-v{UpdateReleaseParser.FormatVersion(target)}-delta-from-v{UpdateReleaseParser.FormatVersion(baseVersion)}{Extension}";

        public static bool TryParseAssetName(string? name, out Version target, out Version baseVersion)
        {
            target = new Version(0, 0);
            baseVersion = new Version(0, 0);
            Match match = AssetNameRegex.Match(name ?? string.Empty);
            if (!match.Success
                || !Version.TryParse(match.Groups["to"].Value, out Version? to)
                || !Version.TryParse(match.Groups["from"].Value, out Version? from))
                return false;
            target = UpdateReleaseParser.NormalizeVersion(to);
            baseVersion = UpdateReleaseParser.NormalizeVersion(from);
            return true;
        }

        /// <summary>The delta that starts from the installed version, if the release has one.</summary>
        public static UpdateDeltaAsset? FindFor(UpdateCheckResult update)
        {
            Version current = UpdateReleaseParser.NormalizeVersion(update.CurrentVersion);
            return update.DeltaPackages.FirstOrDefault(d => UpdateReleaseParser.NormalizeVersion(d.BaseVersion) == current);
        }

        /// <summary>The delta index: every file of the new package with its SHA-256 (upper-case hex).</summary>
        public sealed record Index(Version BaseVersion, Version TargetVersion, IReadOnlyDictionary<string, string> Files);

        public static Index ParseIndex(string json)
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (!Version.TryParse(root.GetProperty("base").GetString(), out Version? baseVersion)
                || !Version.TryParse(root.GetProperty("target").GetString(), out Version? target))
                throw new InvalidDataException("The delta index has no valid base or target version.");
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JsonProperty file in root.GetProperty("files").EnumerateObject())
            {
                string hash = (file.Value.GetString() ?? string.Empty).Trim().ToUpperInvariant();
                if (!Regex.IsMatch(hash, "^[0-9A-F]{64}$"))
                    throw new InvalidDataException($"The delta index has an invalid hash for {file.Name}.");
                files[file.Name.Replace('\\', '/')] = hash;
            }
            if (files.Count == 0)
                throw new InvalidDataException("The delta index lists no files.");
            return new Index(UpdateReleaseParser.NormalizeVersion(baseVersion), UpdateReleaseParser.NormalizeVersion(target), files);
        }

        public static string WriteIndex(Version baseVersion, Version targetVersion, IReadOnlyDictionary<string, string> files) =>
            JsonSerializer.Serialize(new
            {
                @base = UpdateReleaseParser.FormatVersion(baseVersion),
                target = UpdateReleaseParser.FormatVersion(targetVersion),
                files = files.OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(f => f.Key, f => f.Value)
            }, new JsonSerializerOptions { WriteIndented = true });

        public static string HashFile(string path)
        {
            using FileStream stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }

        /// <summary>
        /// Rebuilds the complete new package in <paramref name="payloadRoot"/> from the extracted
        /// delta and the installed folder, checking every file's hash. Throws on any mismatch.
        /// </summary>
        public static void BuildPayload(string deltaRoot, string installRoot, string payloadRoot,
            Version expectedBase, Version expectedTarget, CancellationToken token)
        {
            string indexPath = Path.Combine(deltaRoot, IndexFileName);
            if (!File.Exists(indexPath))
                throw new InvalidDataException("The delta package has no index.");
            Index index = ParseIndex(File.ReadAllText(indexPath));
            if (index.BaseVersion != UpdateReleaseParser.NormalizeVersion(expectedBase)
                || index.TargetVersion != UpdateReleaseParser.NormalizeVersion(expectedTarget))
                throw new InvalidDataException("The delta package is for a different pair of versions.");

            // Every file in the delta must be one the index lists; nothing extra is accepted.
            foreach (string deltaFile in Directory.EnumerateFiles(deltaRoot, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(deltaRoot, deltaFile).Replace('\\', '/');
                if (!string.Equals(relative, IndexFileName, StringComparison.OrdinalIgnoreCase) && !index.Files.ContainsKey(relative))
                    throw new InvalidDataException($"The delta package contains an unlisted file: {relative}");
            }

            Directory.CreateDirectory(payloadRoot);
            foreach ((string entry, string expectedHash) in index.Files)
            {
                token.ThrowIfCancellationRequested();
                if (!UpdatePackageSafety.TryResolvePathUnderRoot(payloadRoot, entry, out string normalized, out string target)
                    || UpdatePackageSafety.IsProtectedUserDataPath(normalized)
                    || !UpdatePackageSafety.TryResolvePathUnderRoot(deltaRoot, normalized, out _, out string fromDelta)
                    || !UpdatePackageSafety.TryResolvePathUnderRoot(installRoot, normalized, out _, out string fromInstall))
                    throw new InvalidDataException($"Unsafe delta index entry: {entry}");

                string source = File.Exists(fromDelta) ? fromDelta : fromInstall;
                if (!File.Exists(source))
                    throw new InvalidDataException($"The delta update needs {normalized}, which is not in the installed copy.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target, overwrite: true);
                string actual = HashFile(target);
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(source == fromDelta
                        ? $"The delta file {normalized} does not match its checksum."
                        : $"The installed file {normalized} differs from version {UpdateReleaseParser.FormatVersion(expectedBase)}; using the full update.");
            }
        }
    }
}
