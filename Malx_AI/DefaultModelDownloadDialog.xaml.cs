using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Windows;

namespace Malx_AI
{
    public partial class DefaultModelDownloadDialog : Window
    {
        private readonly DefaultModelDownloadService _downloadService = new();
        private CancellationTokenSource? _downloadCancellation;
        private bool _isDownloading;
        private bool _closeAfterCancellation;

        public string DownloadedModelPath { get; private set; } = string.Empty;

        public DefaultModelDownloadDialog()
        {
            InitializeComponent();
        }

        private async void StartDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading)
                return;

            _isDownloading = true;
            _downloadCancellation = new CancellationTokenSource();
            StartDownloadButton.IsEnabled = false;
            CancelDownloadButton.Content = "Cancel download";
            DownloadErrorText.Visibility = Visibility.Collapsed;
            DownloadErrorText.Text = string.Empty;

            var progress = new Progress<ModelDownloadProgress>(UpdateProgress);
            try
            {
                DownloadedModelPath = await _downloadService.DownloadAsync(progress, _downloadCancellation.Token);
                _isDownloading = false;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressText.Text = "Download cancelled. The partial file was removed.";
                if (_closeAfterCancellation)
                {
                    _isDownloading = false;
                    Close();
                }
                else
                    ResetForRetry();
            }
            catch (Exception ex)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressText.Text = "Download failed.";
                DownloadErrorText.Text = BuildFailureMessage(ex);
                DownloadErrorText.Visibility = Visibility.Visible;
                ResetForRetry();
            }
            finally
            {
                _isDownloading = false;
                _downloadCancellation?.Dispose();
                _downloadCancellation = null;
            }
        }

        private void CancelDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDownloading)
            {
                DialogResult = false;
                return;
            }

            CancelDownloadButton.IsEnabled = false;
            DownloadProgressText.Text = "Cancelling and removing the partial file...";
            _downloadCancellation?.Cancel();
        }

        private void UpdateProgress(ModelDownloadProgress progress)
        {
            double downloadedMb = progress.BytesDownloaded / 1024d / 1024d;
            double totalMb = progress.TotalBytes / 1024d / 1024d;
            switch (progress.Stage)
            {
                case ModelDownloadStage.CheckingExistingFile:
                    DownloadProgressBar.IsIndeterminate = true;
                    DownloadProgressText.Text = "Checking the existing model checksum...";
                    break;
                case ModelDownloadStage.Verifying:
                    DownloadProgressBar.IsIndeterminate = true;
                    DownloadProgressText.Text = $"Verifying SHA-256 checksum - {downloadedMb:N0} MB";
                    break;
                case ModelDownloadStage.Complete:
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = 100;
                    DownloadProgressText.Text = $"100% - {downloadedMb:N0} MB - Verified";
                    break;
                default:
                    DownloadProgressBar.IsIndeterminate = false;
                    double percent = progress.TotalBytes <= 0
                        ? 0
                        : Math.Clamp(progress.BytesDownloaded * 100d / progress.TotalBytes, 0, 100);
                    DownloadProgressBar.Value = percent;
                    DownloadProgressText.Text = $"{percent:F0}% - {downloadedMb:N0} / {totalMb:N0} MB";
                    break;
            }
        }

        private void ResetForRetry()
        {
            StartDownloadButton.IsEnabled = true;
            StartDownloadButton.Content = "Retry download";
            CancelDownloadButton.IsEnabled = true;
            CancelDownloadButton.Content = "Close";
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isDownloading)
            {
                e.Cancel = true;
                _closeAfterCancellation = true;
                CancelDownloadButton.IsEnabled = false;
                DownloadProgressText.Text = "Cancelling and removing the partial file...";
                _downloadCancellation?.Cancel();
                return;
            }

            base.OnClosing(e);
        }

        private static string BuildFailureMessage(Exception ex)
        {
            if (ex is IOException)
                return ex.Message;
            if (ex is HttpRequestException)
                return "Network error. Check your internet connection and try again. " + ex.Message;

            return ex.Message;
        }
    }
}
