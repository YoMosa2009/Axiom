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

    internal sealed record ModelDownloadRecommendation(
        ModelDownloadManifest Manifest,
        string DisplayName,
        string SelectionReason);

    internal sealed class DefaultModelDownloadService
    {
        private const long DiskSafetyMarginBytes = 256L * 1024 * 1024;
        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly ModelDownloadRecommendation[] Recommendations =
        [
            CreateRecommendation(
                "Axiom Qwen3-0.6B",
                "Qwen3-0.6B-Q4_0.gguf",
                "https://huggingface.co/ggml-org/Qwen3-0.6B-GGUF/resolve/a41486f827d17edd055fe6b3b0ba3f8d427c0519/Qwen3-0.6B-Q4_0.gguf?download=true",
                428970080,
                "da2572f16c06133561ce56accaa822216f2391ef4d37fba427801cd6736417d4"),
            CreateRecommendation(
                "Axiom Qwen3-1.7B",
                "Qwen3-1.7B-Q4_K_M.gguf",
                "https://huggingface.co/ggml-org/Qwen3-1.7B-GGUF/resolve/daeb8e2d528a760970442092f6bf1e55c3b659eb/Qwen3-1.7B-Q4_K_M.gguf?download=true",
                1282439264,
                "d2387ca2dbfee2ffabce7120d3770dadca0b293052bc2f0e138fdc940d9bc7b5"),
            CreateRecommendation(
                ModelInferenceProfiles.DefaultQwen3DisplayName,
                ModelInferenceProfiles.DefaultQwen3FileName,
                ModelInferenceProfiles.DefaultQwen3DownloadUrl,
                ModelInferenceProfiles.DefaultQwen3FileSizeBytes,
                ModelInferenceProfiles.DefaultQwen3Sha256),
            CreateRecommendation(
                "Axiom Qwen3-8B",
                "Qwen3-8B-Q4_K_M.gguf",
                "https://huggingface.co/ggml-org/Qwen3-8B-GGUF/resolve/2473489dc243ccaffb4ce569c55bf1df66b2088f/Qwen3-8B-Q4_K_M.gguf?download=true",
                5027783872,
                "a67d87633b5f5f191a5bd11e6d37cab18b9ce3d4a6af6861561e8a767352080b")
        ];
        private readonly HttpClient _httpClient;
        private readonly ModelDownloadManifest _manifest;

        public string DestinationPath => _manifest.DestinationPath;
        public ModelDownloadRecommendation Recommendation { get; }

        public DefaultModelDownloadService()
            : this(Http, GetRecommendedModel())
        {
        }

        internal DefaultModelDownloadService(HttpClient httpClient, ModelDownloadManifest manifest)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _manifest = manifest ?? throw new ArgumentNullException(nameof(manifest));
            Recommendation = new ModelDownloadRecommendation(
                _manifest,
                Path.GetFileNameWithoutExtension(_manifest.DestinationPath),
                "Selected model manifest.");
        }

        private DefaultModelDownloadService(HttpClient httpClient, ModelDownloadRecommendation recommendation)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            Recommendation = recommendation ?? throw new ArgumentNullException(nameof(recommendation));
            _manifest = recommendation.Manifest;
        }

        public static ModelDownloadRecommendation GetRecommendedModel()
        {
            HardwareProfile hardware = HardwareProfiler.Capture();
            double ramGb = hardware.AvailableRamGb;
            double vramGb = hardware.AvailableVramGb;

            ModelDownloadRecommendation selected = ramGb >= 24
                && hardware.LogicalProcessorCount >= 8
                && hardware.HasNvidiaGpu
                && vramGb >= 8
                ? Recommendations[3]
                : ramGb >= 10 && hardware.LogicalProcessorCount >= 4
                    ? Recommendations[2]
                    : ramGb >= 6
                        ? Recommendations[1]
                        : Recommendations[0];

            string gpuSummary = hardware.HasNvidiaGpu && vramGb > 0
                ? $"{hardware.PrimaryGpuName} with {vramGb:F1} GB free VRAM"
                : hardware.HasNvidiaGpu
                    ? $"{hardware.PrimaryGpuName} (free VRAM unavailable)"
                    : "no CUDA-compatible GPU detected";
            string reason =
                $"Selected for {ramGb:F1} GB available RAM, {hardware.LogicalProcessorCount} logical CPU cores, and {gpuSummary}. " +
                "Axiom will apply an additional safe memory and GPU-layer plan when it imports the model.";

            return selected with { SelectionReason = reason };
        }

        private static ModelDownloadRecommendation CreateRecommendation(
            string displayName,
            string fileName,
            string downloadUrl,
            long expectedSizeBytes,
            string sha256)
        {
            return new ModelDownloadRecommendation(
                new ModelDownloadManifest(
                    downloadUrl,
                    Path.Combine(AppDataPaths.LocalModels, fileName),
                    expectedSizeBytes,
                    sha256),
                displayName,
                string.Empty);
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
