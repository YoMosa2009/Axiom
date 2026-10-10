using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Malx_AI
{
    internal enum FirstRunGuideOutcome
    {
        Skipped,
        ModelDownloaded,
        ImportExistingModel,
        SetUpCloud,
        SetUpServer
    }

    /// <summary>
    /// First-run guide: choose Local, Cloud or your own server; for Local, see this computer's
    /// hardware, which models fit it, and download the recommended one. The sidebar's
    /// "Download a model" opens it straight on the model page.
    /// </summary>
    public partial class FirstRunGuideWindow : Window
    {
        // Plain-language blurbs, keyed by GGUF file name so a reordered list keeps them right.
        private static readonly Dictionary<string, string> ModelBlurbs = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Qwen3-0.6B-Q4_0.gguf"] = "Tiny and quick. Fine for short questions; struggles with longer tasks.",
            ["Qwen3-1.7B-Q4_K_M.gguf"] = "Small. Good for everyday chat on modest hardware.",
            ["Qwen3-4B-Q4_K_M.gguf"] = "Balanced. Good answers, coding help and tool use.",
            ["Qwen3-8B-Q4_K_M.gguf"] = "The strongest of these. Best answers; wants a graphics card or plenty of memory."
        };

        private readonly IReadOnlyList<ModelDownloadRecommendation> _models = DefaultModelDownloadService.AvailableModels;
        private readonly List<ModelCard> _cards = [];
        private ModelDownloadRecommendation? _selected;
        private CancellationTokenSource? _downloadCancellation;
        private bool _isDownloading;
        private bool _closeAfterCancellation;
        private bool _hardwareRequested;

        internal FirstRunGuideOutcome Outcome { get; private set; } = FirstRunGuideOutcome.Skipped;
        public string DownloadedModelPath { get; private set; } = string.Empty;

        private sealed record ModelCard(Button Button, Ellipse Dot, ModelDownloadRecommendation Model);

        public FirstRunGuideWindow(bool startOnModelPage)
        {
            InitializeComponent();
            WindowTitleBarTheme.Apply(this);

            if (startOnModelPage)
            {
                Title = "Download a model";
                BackButton.Visibility = Visibility.Collapsed;
                ShowLocalPage();
            }
        }

        private void ChooseLocal_Click(object sender, RoutedEventArgs e) => ShowLocalPage();

        private void ChooseCloud_Click(object sender, RoutedEventArgs e) => Finish(FirstRunGuideOutcome.SetUpCloud);

        private void ChooseServer_Click(object sender, RoutedEventArgs e) => Finish(FirstRunGuideOutcome.SetUpServer);

        private void Skip_Click(object sender, RoutedEventArgs e)
        {
            Outcome = FirstRunGuideOutcome.Skipped;
            DialogResult = false;
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading)
                return;

            LocalPage.Visibility = Visibility.Collapsed;
            WelcomePage.Visibility = Visibility.Visible;
        }

        private void ImportExisting_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDownloading)
                Finish(FirstRunGuideOutcome.ImportExistingModel);
        }

        private void Finish(FirstRunGuideOutcome outcome)
        {
            Outcome = outcome;
            DialogResult = true;
        }

        private void ShowLocalPage()
        {
            WelcomePage.Visibility = Visibility.Collapsed;
            LocalPage.Visibility = Visibility.Visible;
            if (_hardwareRequested)
                return;

            _hardwareRequested = true;
            _ = LoadHardwareAndModelsAsync();
        }

        private async Task LoadHardwareAndModelsAsync()
        {
            HardwareProfile? hardware = null;
            try
            {
                // Runs PowerShell and nvidia-smi, so keep it off the UI thread.
                hardware = await Task.Run(HardwareProfiler.Capture);
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogErrorAsync("FirstRunGuide", ex);
            }

            if (hardware == null)
            {
                CpuSummaryText.Text = "Unknown";
                RamSummaryText.Text = "Unknown";
                GpuSummaryText.Text = "Couldn't check";
                BuildModelCards(null);
                return;
            }

            CpuSummaryText.Text = $"{hardware.LogicalProcessorCount} threads";
            RamSummaryText.Text = $"{Math.Round(hardware.AvailableRamGb):0} GB";
            double vramGb = hardware.TotalVramGb > 0 ? hardware.TotalVramGb : hardware.AvailableVramGb;
            (GpuSummaryText.Text, GpuSummaryText.ToolTip) = hardware switch
            {
                { HasNvidiaGpu: true } when vramGb > 0 => ($"{hardware.PrimaryGpuName} · {Math.Round(vramGb):0} GB", "Models that fit in this card's memory run fastest."),
                { HasNvidiaGpu: true } => (hardware.PrimaryGpuName, "Axiom couldn't read this card's memory size."),
                _ when IsKnownGpuName(hardware.PrimaryGpuName) => (
                    $"{CleanGpuName(hardware.PrimaryGpuName)} (not used)",
                    "Axiom speeds up NVIDIA graphics cards only, so models run on the processor."),
                _ => ("None that Axiom can use", "Models will run on the processor.")
            };

            BuildModelCards(DefaultModelDownloadService.ToFitHardware(hardware));
        }

        private static bool IsKnownGpuName(string? name)
            => !string.IsNullOrWhiteSpace(name) && !string.Equals(name, "Unknown", StringComparison.OrdinalIgnoreCase);

        /// <summary>"Intel(R) Iris(R) Plus Graphics" reads as "Intel Iris Plus Graphics".</summary>
        private static string CleanGpuName(string name)
            => name.Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("  ", " ")
                .Trim();

        private void BuildModelCards(LocalModelFitHardware? hardware)
        {
            ModelListPanel.Children.Clear();
            _cards.Clear();

            LocalModelFitCandidate[] candidates = _models.Select(DefaultModelDownloadService.ToFitCandidate).ToArray();
            int recommended = hardware == null ? -1 : LocalModelFitAdvisor.ChooseRecommendedIndex(candidates, hardware);

            for (int i = 0; i < _models.Count; i++)
            {
                ModelDownloadRecommendation model = _models[i];
                LocalModelFitResult? fit = hardware == null ? null : LocalModelFitAdvisor.Assess(candidates[i], hardware);
                ModelCard card = CreateModelCard(model, candidates[i].FileSizeGb, fit, i == recommended);
                _cards.Add(card);
                ModelListPanel.Children.Add(card.Button);
            }

            if (recommended >= 0)
                Select(_models[recommended]);
            else if (_models.Count > 0)
                Select(_models[Math.Min(1, _models.Count - 1)]);
        }

        private ModelCard CreateModelCard(ModelDownloadRecommendation model, double sizeGb, LocalModelFitResult? fit, bool isRecommended)
        {
            var dot = new Ellipse { Width = 14, Height = 14, StrokeThickness = 1.5, VerticalAlignment = VerticalAlignment.Center };
            dot.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");

            var name = new TextBlock { Text = model.DisplayName.Replace("Axiom ", string.Empty), FontSize = 14, FontWeight = FontWeights.SemiBold };
            string fileName = System.IO.Path.GetFileName(model.Manifest.DestinationPath);
            var blurb = new TextBlock
            {
                Text = ModelBlurbs.TryGetValue(fileName, out string? blurbText) ? blurbText : fileName,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            blurb.SetResourceReference(TextBlock.ForegroundProperty, "DarkSecondaryBrush");

            var fitLine = new TextBlock
            {
                Text = $"{sizeGb:0.0} GB download" + (fit == null ? string.Empty : " · " + fit.Summary),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            };
            fitLine.SetResourceReference(TextBlock.ForegroundProperty, fit?.Fit switch
            {
                LocalModelFit.RunsOnGpu => "SuccessBrush",
                LocalModelFit.Tight => "WarningBrush",
                LocalModelFit.TooLarge => "DangerBrush",
                _ => "MutedTextBrush"
            });

            var text = new StackPanel();
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
            titleRow.Children.Add(name);
            if (isRecommended)
            {
                var badgeText = new TextBlock { Text = "Recommended", FontSize = 10, FontWeight = FontWeights.SemiBold };
                badgeText.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrightBrush");
                var badge = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(7, 1, 7, 2),
                    Margin = new Thickness(10, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = badgeText
                };
                badge.SetResourceReference(Border.BackgroundProperty, "AccentSoftBrush");
                titleRow.Children.Add(badge);
            }

            text.Children.Add(titleRow);
            text.Children.Add(blurb);
            text.Children.Add(fitLine);

            var layout = new Grid();
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            layout.Children.Add(dot);
            Grid.SetColumn(text, 1);
            layout.Children.Add(text);

            var button = new Button
            {
                Content = layout,
                Style = (Style)FindResource("GuideCardButtonStyle"),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8),
                IsEnabled = fit?.Fit != LocalModelFit.TooLarge
            };
            System.Windows.Automation.AutomationProperties.SetName(button, model.DisplayName);
            button.Click += (_, _) => Select(model);
            return new ModelCard(button, dot, model);
        }

        private void Select(ModelDownloadRecommendation model)
        {
            if (_isDownloading)
                return;

            _selected = model;
            foreach (ModelCard card in _cards)
            {
                bool selected = ReferenceEquals(card.Model, model);
                if (selected)
                {
                    card.Button.SetResourceReference(BorderBrushProperty, "PrimaryAccentBrush");
                    card.Dot.SetResourceReference(Shape.FillProperty, "PrimaryAccentBrush");
                    card.Dot.SetResourceReference(Shape.StrokeProperty, "PrimaryAccentBrush");
                }
                else
                {
                    card.Button.ClearValue(BorderBrushProperty);
                    card.Dot.Fill = Brushes.Transparent;
                    card.Dot.SetResourceReference(Shape.StrokeProperty, "BorderStrongBrush");
                }
            }

            double sizeGb = model.Manifest.ExpectedSizeBytes / 1024d / 1024d / 1024d;
            DownloadButton.Content = $"Download {model.DisplayName.Replace("Axiom ", string.Empty)} ({sizeGb:0.0} GB)";
            DownloadButton.IsEnabled = true;
        }

        private async void Download_Click(object sender, RoutedEventArgs e)
        {
            if (_isDownloading || _selected == null)
                return;

            _isDownloading = true;
            _downloadCancellation = new CancellationTokenSource();
            DownloadButton.IsEnabled = false;
            ImportExistingButton.IsEnabled = false;
            BackButton.IsEnabled = false;
            CancelButton.Content = "Cancel download";
            DownloadErrorText.Visibility = Visibility.Collapsed;
            DownloadStatusPanel.Visibility = Visibility.Visible;
            DownloadProgressText.Text = "Starting the download…";
            foreach (ModelCard card in _cards)
                card.Button.IsHitTestVisible = false;

            var service = new DefaultModelDownloadService(_selected);
            var progress = new Progress<ModelDownloadProgress>(UpdateProgress);
            try
            {
                DownloadedModelPath = await service.DownloadAsync(progress, _downloadCancellation.Token);
                _isDownloading = false;
                Outcome = FirstRunGuideOutcome.ModelDownloaded;
                DialogResult = true;
            }
            catch (OperationCanceledException)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadProgressText.Text = "Download cancelled. The partial file was removed.";
                _isDownloading = false;
                if (_closeAfterCancellation)
                    Close();
                else
                    ResetAfterDownloadStopped();
            }
            catch (Exception ex)
            {
                DownloadProgressBar.IsIndeterminate = false;
                DownloadStatusPanel.Visibility = Visibility.Collapsed;
                DownloadErrorText.Text = ex switch
                {
                    HttpRequestException => "Network error. Check your internet connection and try again. " + ex.Message,
                    _ => ex.Message
                };
                DownloadErrorText.Visibility = Visibility.Visible;
                _isDownloading = false;
                ResetAfterDownloadStopped();
                DownloadButton.Content = "Try again";
            }
            finally
            {
                _isDownloading = false;
                _downloadCancellation?.Dispose();
                _downloadCancellation = null;
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            if (!_isDownloading)
            {
                DialogResult = false;
                return;
            }

            CancelButton.IsEnabled = false;
            DownloadProgressText.Text = "Cancelling and removing the partial file…";
            _downloadCancellation?.Cancel();
        }

        private void ResetAfterDownloadStopped()
        {
            DownloadButton.IsEnabled = _selected != null;
            ImportExistingButton.IsEnabled = true;
            BackButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            CancelButton.Content = "Not now";
            foreach (ModelCard card in _cards)
                card.Button.IsHitTestVisible = true;
        }

        private void UpdateProgress(ModelDownloadProgress progress)
        {
            double downloadedMb = progress.BytesDownloaded / 1024d / 1024d;
            double totalMb = progress.TotalBytes / 1024d / 1024d;
            switch (progress.Stage)
            {
                case ModelDownloadStage.CheckingExistingFile:
                    DownloadProgressBar.IsIndeterminate = true;
                    DownloadProgressText.Text = "Found an earlier download. Checking it…";
                    break;
                case ModelDownloadStage.Verifying:
                    DownloadProgressBar.IsIndeterminate = true;
                    DownloadProgressText.Text = "Checking the file is complete and untouched…";
                    break;
                case ModelDownloadStage.Complete:
                    DownloadProgressBar.IsIndeterminate = false;
                    DownloadProgressBar.Value = 100;
                    DownloadProgressText.Text = "Done. Loading the model…";
                    break;
                default:
                    DownloadProgressBar.IsIndeterminate = false;
                    double percent = progress.TotalBytes <= 0
                        ? 0
                        : Math.Clamp(progress.BytesDownloaded * 100d / progress.TotalBytes, 0, 100);
                    DownloadProgressBar.Value = percent;
                    DownloadProgressText.Text = $"{percent:F0}%  ·  {downloadedMb:N0} of {totalMb:N0} MB";
                    break;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (_isDownloading)
            {
                e.Cancel = true;
                _closeAfterCancellation = true;
                CancelButton.IsEnabled = false;
                DownloadProgressText.Text = "Cancelling and removing the partial file…";
                _downloadCancellation?.Cancel();
                return;
            }

            base.OnClosing(e);
        }
    }
}
