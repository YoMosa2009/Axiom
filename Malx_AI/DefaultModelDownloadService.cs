using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Malx_AI
{
    internal enum ModelDownloadStage
    {
        CheckingExistingFile,
        Downloading,
        Verifying,
        Complete
    }

    internal sealed record ModelDownloadProgress(
        ModelDownloadStage Stage,
        long BytesDownloaded,
        long TotalBytes);

    internal sealed record ModelDownloadManifest(
        string DownloadUrl,
        string DestinationPath,
        long ExpectedSizeBytes,
        string Sha256);

    internal sealed class DefaultModelDownloadService
    {
        private const long DiskSafetyMarginBytes = 256L * 1024 * 1024;
        private static readonly HttpClient Http = CreateHttpClient();
        private readonly HttpClient _httpClient;
        private readonly ModelDownloadManifest _manifest;

        public string DestinationPath => _manifest.DestinationPath;

        public DefaultModelDownloadService()
            : this(
                Http,
                new ModelDownloadManifest(
                    ModelInferenceProfiles.DefaultQwen3DownloadUrl,
                    Path.Combine(AppDataPaths.LocalModels, ModelInferenceProfiles.DefaultQwen3FileName),
                    ModelInferenceProfiles.DefaultQwen3FileSizeBytes,
                    ModelInferenceProfiles.DefaultQwen3Sha256))
        {
        }

        internal DefaultModelDownloadService(HttpClient httpClient, ModelDownloadManifest manifest)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
        }

        public async Task<string> DownloadAsync(
            IProgress<ModelDownloadProgress>? progress,
            CancellationToken cancellationToken)
        {
            string? destinationDirectory = Path.GetDirectoryName(DestinationPath);
            if (string.IsNullOrWhiteSpace(destinationDirectory))
                throw new IOException("Unable to determine the model download directory.");
            Directory.CreateDirectory(destinationDirectory);
            string destinationPath = DestinationPath;
            string partialPath = destinationPath + ".partial";
            DeleteIfPresent(partialPath);

            try
            {
                if (File.Exists(destinationPath))
                {
                    progress?.Report(new ModelDownloadProgress(
                        ModelDownloadStage.CheckingExistingFile,
                        new FileInfo(destinationPath).Length,
                        _manifest.ExpectedSizeBytes));
                    if (await VerifyFileAsync(destinationPath, _manifest.ExpectedSizeBytes, _manifest.Sha256, cancellationToken))
                        return destinationPath;
                }

                EnsureDiskSpace(destinationPath, _manifest.ExpectedSizeBytes + DiskSafetyMarginBytes);
                using var request = new HttpRequestMessage(HttpMethod.Get, _manifest.DownloadUrl);
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                response.EnsureSuccessStatusCode();

                long totalBytes = response.Content.Headers.ContentLength is > 0
                    ? response.Content.Headers.ContentLength.Value
                    : _manifest.ExpectedSizeBytes;
                long downloadedBytes = 0;
                var reportStopwatch = Stopwatch.StartNew();
                byte[] buffer = new byte[1024 * 1024];

                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using (var destination = new FileStream(
                    partialPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    while (true)
                    {
                        int read = await source.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                            break;

                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        downloadedBytes += read;
                        if (reportStopwatch.ElapsedMilliseconds >= 100)
                        {
                            progress?.Report(new ModelDownloadProgress(ModelDownloadStage.Downloading, downloadedBytes, totalBytes));
                            reportStopwatch.Restart();
                        }
                    }

                    await destination.FlushAsync(cancellationToken);
                }

                progress?.Report(new ModelDownloadProgress(ModelDownloadStage.Downloading, downloadedBytes, totalBytes));
                if (downloadedBytes != _manifest.ExpectedSizeBytes)
                {
                    throw new InvalidDataException(
                        $"The download ended at {FormatMegabytes(downloadedBytes)} MB; expected {FormatMegabytes(_manifest.ExpectedSizeBytes)} MB.");
                }

                progress?.Report(new ModelDownloadProgress(ModelDownloadStage.Verifying, downloadedBytes, totalBytes));
                if (!await VerifyFileAsync(partialPath, _manifest.ExpectedSizeBytes, _manifest.Sha256, cancellationToken))
                    throw new InvalidDataException("The downloaded model failed SHA-256 verification. The partial file was removed; retry the download.");

                File.Move(partialPath, destinationPath, overwrite: true);
                progress?.Report(new ModelDownloadProgress(ModelDownloadStage.Complete, downloadedBytes, totalBytes));
                return destinationPath;
            }
            catch (Exception downloadException)
            {
                try
                {
                    DeleteIfPresent(partialPath);
                }
                catch (Exception cleanupException)
                {
                    throw new IOException(
                        $"The download failed and Axiom could not remove the partial file at {partialPath}.",
                        new AggregateException(downloadException, cleanupException));
                }
                throw;
            }
        }

        internal static async Task<bool> VerifyFileAsync(
            string path,
            long expectedSizeBytes,
            string expectedSha256,
            CancellationToken cancellationToken)
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length != expectedSizeBytes)
                return false;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
            return string.Equals(
                Convert.ToHexString(hash),
                expectedSha256,
                StringComparison.OrdinalIgnoreCase);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient
            {
                Timeout = Timeout.InfiniteTimeSpan
            };
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Axiom", "1.0"));
            return client;
        }

        private static void EnsureDiskSpace(string path, long requiredBytes)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath)
                ?? throw new IOException("Unable to determine the destination drive.");
            var drive = new DriveInfo(root);
            if (drive.AvailableFreeSpace >= requiredBytes)
                return;

            throw new IOException(
                $"Not enough disk space on {drive.Name}. " +
                $"The download needs about {FormatMegabytes(requiredBytes)} MB free, but only {FormatMegabytes(drive.AvailableFreeSpace)} MB is available.");
        }

        private static long FormatMegabytes(long bytes)
        {
            return Math.Max(0, bytes) / (1024 * 1024);
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
