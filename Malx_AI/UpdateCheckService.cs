using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    /// <summary>
    /// Checks Axiom's latest stable GitHub release and downloads its published Windows package.
    /// User data remains under AppDataPaths.Root and is never part of the install-folder swap.
    /// </summary>
    public static class UpdateCheckService
    {
        private const string LatestReleaseApiUrl = "https://api.github.com/repos/YoMosa2009/Axiom/releases/latest";
        private const long MaximumPackageBytes = 4L * 1024 * 1024 * 1024;
        private static readonly HttpClient Http = CreateClient();

        public static bool CanApplyUpdates
        {
            get
            {
#if DEBUG
                return false;
#else
                return true;
#endif
            }
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Axiom-Updater/1.7");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            return client;
        }

        public static Version GetCurrentVersion()
            => UpdateReleaseParser.NormalizeVersion(
                Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0));

        public static string GetCurrentVersionText()
            => UpdateReleaseParser.FormatVersion(GetCurrentVersion());

        public static async Task<UpdateCheckResult?> CheckForUpdateAsync(CancellationToken token = default)
        {
            try
            {
                using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                checkCts.CancelAfter(TimeSpan.FromSeconds(15));
                using HttpResponseMessage response = await Http.GetAsync(LatestReleaseApiUrl, checkCts.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    await BackendLogService.LogEventAsync(
                        "UpdateCheck",
                        $"Latest-release request returned {(int)response.StatusCode} ({response.StatusCode}).").ConfigureAwait(false);
                    return null;
                }

                string json = await response.Content.ReadAsStringAsync(checkCts.Token).ConfigureAwait(false);
                return UpdateReleaseParser.Parse(json, GetCurrentVersion());
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await BackendLogService.LogEventAsync("UpdateCheck", "GitHub release check timed out after 15 seconds.").ConfigureAwait(false);
                return null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await BackendLogService.LogErrorAsync("UpdateCheck", ex).ConfigureAwait(false);
                return null;
            }
        }

        public static async Task<string> DownloadPackageAsync(
            UpdateCheckResult update,
            IProgress<double>? progress,
            CancellationToken token)
        {
            ArgumentNullException.ThrowIfNull(update);
            if (!update.HasPackageAsset)
                throw new InvalidOperationException("The GitHub release does not contain a supported Axiom update package.");
            if (update.PackageSizeBytes > MaximumPackageBytes)
                throw new InvalidOperationException("The update package is larger than Axiom's 4 GB safety limit.");

            string safeName = Path.GetFileName(update.PackageFileName.Trim());
            if (string.IsNullOrWhiteSpace(safeName))
                safeName = update.PackageKind == UpdatePackageKind.Zip ? "AxiomUpdate.zip" : "AxiomUpdate.exe";

            string versionFolder = UpdateReleaseParser.FormatVersion(update.LatestVersion);
            string downloadDirectory = Path.Combine(AppDataPaths.Root, "Updates", "downloads", versionFolder);
            Directory.CreateDirectory(downloadDirectory);

            string targetPath = Path.Combine(downloadDirectory, safeName);
            if (File.Exists(targetPath) && await IsDownloadedPackageValidAsync(targetPath, update, token).ConfigureAwait(false))
            {
                progress?.Report(100);
                return targetPath;
            }

            string partialPath = targetPath + ".partial";
            TryDeleteFile(partialPath);

            try
            {
                using HttpResponseMessage response = await Http.GetAsync(
                    update.PackageDownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                long contentLength = response.Content.Headers.ContentLength ?? -1;
                if (contentLength > MaximumPackageBytes)
                    throw new InvalidOperationException("The downloaded update exceeds Axiom's 4 GB safety limit.");

                await using Stream source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                await using var target = new FileStream(
                    partialPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    useAsync: true);

                var buffer = new byte[1024 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
                {
                    received += read;
                    if (received > MaximumPackageBytes)
                        throw new InvalidOperationException("The downloaded update exceeds Axiom's 4 GB safety limit.");

                    await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    long expected = update.PackageSizeBytes > 0 ? update.PackageSizeBytes : contentLength;
                    if (expected > 0)
                        progress?.Report(Math.Clamp(received * 100.0 / expected, 0, 100));
                }

                await target.FlushAsync(token).ConfigureAwait(false);

                if (update.PackageSizeBytes > 0 && received != update.PackageSizeBytes)
                    throw new InvalidDataException($"The update download is incomplete ({received:N0} of {update.PackageSizeBytes:N0} bytes).");

                if (!await IsDownloadedPackageValidAsync(partialPath, update, token).ConfigureAwait(false))
                    throw new InvalidDataException("The update package checksum does not match GitHub's release asset digest.");

                File.Move(partialPath, targetPath, overwrite: true);
                progress?.Report(100);
                return targetPath;
            }
            catch
            {
                TryDeleteFile(partialPath);
                throw;
            }
        }

        public static Task<string> DownloadInstallerAsync(
            string downloadUrl,
            string fileName,
            IProgress<double>? progress,
            CancellationToken token)
        {
            var installer = new UpdateCheckResult
            {
                PackageDownloadUrl = downloadUrl,
                PackageFileName = fileName,
                PackageKind = UpdatePackageKind.Installer
            };
            return DownloadPackageAsync(installer, progress, token);
        }

        private static async Task<bool> IsDownloadedPackageValidAsync(
            string path,
            UpdateCheckResult update,
            CancellationToken token)
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0)
                return false;
            if (update.PackageSizeBytes > 0 && info.Length != update.PackageSizeBytes)
                return false;
            if (string.IsNullOrWhiteSpace(update.PackageSha256))
                return true;

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, useAsync: true);
            byte[] digest = await SHA256.HashDataAsync(stream, token).ConfigureAwait(false);
            string actual = Convert.ToHexString(digest);
            return string.Equals(actual, update.PackageSha256, StringComparison.OrdinalIgnoreCase);
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // A later download will report the real access error when it opens the path.
            }
        }
    }
}
