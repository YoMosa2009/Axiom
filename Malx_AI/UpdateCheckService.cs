using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    public sealed class UpdateCheckResult
    {
        public string LatestVersionTag { get; init; } = string.Empty;
        public Version LatestVersion { get; init; } = new(0, 0, 0, 0);
        public Version CurrentVersion { get; init; } = new(0, 0, 0, 0);
        public string ReleasePageUrl { get; init; } = string.Empty;
        public string InstallerDownloadUrl { get; init; } = string.Empty;
        public string InstallerFileName { get; init; } = string.Empty;
        public bool IsNewerVersionAvailable { get; init; }

        public bool HasInstallerAsset => !string.IsNullOrWhiteSpace(InstallerDownloadUrl);
    }

    /// <summary>
    /// Startup update check against the GitHub releases API. User data (chat history, settings,
    /// workplace/council state) lives in local app data, so running a newer installer over the
    /// program files does not touch it.
    /// </summary>
    public static class UpdateCheckService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/YoMosa2009/Axiom/releases/latest";
        private static readonly Regex VersionInTagRegex = new(@"(?<version>\d+(?:\.\d+){1,3})", RegexOptions.Compiled);
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            // GitHub's API rejects requests without a User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-Update-Check");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        public static Version GetCurrentVersion()
            => NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

        public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken token = default)
        {
            try
            {
                using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApiUrl, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    // 404 also covers "repository has no releases yet" — not an error worth surfacing.
                    await BackendLogService.LogEventAsync("UpdateCheck", $"Latest-release request returned {(int)response.StatusCode} ({response.StatusCode}).").ConfigureAwait(false);
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                string tag = root.TryGetProperty("tag_name", out JsonElement tagElement) ? tagElement.GetString() ?? string.Empty : string.Empty;
                if (string.IsNullOrWhiteSpace(tag) || !TryParseVersionTag(tag, out Version latestVersion))
                    return null;

                string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty;
                (string downloadUrl, string fileName) = FindInstallerAsset(root);
                Version currentVersion = GetCurrentVersion();

                return new UpdateCheckResult
                {
                    LatestVersionTag = tag.Trim(),
                    LatestVersion = latestVersion,
                    CurrentVersion = currentVersion,
                    ReleasePageUrl = releaseUrl,
                    InstallerDownloadUrl = downloadUrl,
                    InstallerFileName = fileName,
                    IsNewerVersionAvailable = latestVersion > currentVersion
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await BackendLogService.LogErrorAsync("UpdateCheck", ex).ConfigureAwait(false);
                return null;
            }
        }

        public static async Task<string> DownloadInstallerAsync(string downloadUrl, string fileName, IProgress<double>? progress, CancellationToken token)
        {
            string safeName = string.IsNullOrWhiteSpace(fileName) ? "AxiomUpdateInstaller.exe" : Path.GetFileName(fileName.Trim());
            string targetPath = Path.Combine(Path.GetTempPath(), safeName);

            using HttpResponseMessage response = await Http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                received += read;
                if (totalBytes > 0)
                    progress?.Report(received * 100.0 / totalBytes);
            }

            return targetPath;
        }

        private static bool TryParseVersionTag(string tag, out Version version)
        {
            version = new Version(0, 0, 0, 0);
            Match match = VersionInTagRegex.Match(tag ?? string.Empty);
            if (!match.Success || !Version.TryParse(match.Groups["version"].Value, out Version? parsed))
                return false;

            version = NormalizeVersion(parsed);
            return true;
        }

        // Version treats missing components as -1, so "1.3" would compare *below* "1.3.0.0".
        // Pad every version to four components before comparing.
        private static Version NormalizeVersion(Version version)
            => new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build), Math.Max(0, version.Revision));

        private static (string Url, string Name) FindInstallerAsset(JsonElement root)
        {
            if (!root.TryGetProperty("assets", out JsonElement assets) || assets.ValueKind != JsonValueKind.Array)
                return (string.Empty, string.Empty);

            var candidates = assets.EnumerateArray()
                .Select(asset => (
                    Name: asset.TryGetProperty("name", out JsonElement nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty,
                    Url: asset.TryGetProperty("browser_download_url", out JsonElement urlElement) ? urlElement.GetString() ?? string.Empty : string.Empty))
                .Where(asset => !string.IsNullOrWhiteSpace(asset.Url) && !string.IsNullOrWhiteSpace(asset.Name))
                .ToList();

            (string Name, string Url) best = candidates.FirstOrDefault(a =>
                a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && (a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) || a.Name.Contains("install", StringComparison.OrdinalIgnoreCase)));

            if (string.IsNullOrWhiteSpace(best.Url))
                best = candidates.FirstOrDefault(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(best.Url))
                best = candidates.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

            return (best.Url ?? string.Empty, best.Name ?? string.Empty);
        }
    }
}
