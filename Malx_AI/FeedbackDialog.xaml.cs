using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    /// <summary>The facts about this install that a feedback report can carry.</summary>
    internal sealed record FeedbackContext(
        string AppVersion,
        string WindowsVersion,
        string HardwareSummary,
        string InferenceMode,
        string RecentErrorLog);

    /// <summary>
    /// "Send feedback / report a problem" from Settings › General. Sends directly when
    /// <see cref="FeedbackDelivery"/> is set up, and otherwise (or on failure) opens a ready email.
    /// </summary>
    public partial class FeedbackDialog : Window
    {
        private readonly FeedbackContext _context;
        private CancellationTokenSource? _sending;

        internal FeedbackDialog(FeedbackContext context)
        {
            _context = context;
            InitializeComponent();
            WindowTitleBarTheme.Apply(this);

            SendButton.Content = FeedbackDelivery.CanSendDirectly ? "Send" : "Continue in email";
            DeliveryHintText.Text = FeedbackDelivery.CanSendDirectly
                ? string.Empty
                : $"Opens your email app with the report filled in, addressed to {FeedbackDelivery.FeedbackEmail}.";
            Loaded += (_, _) => MessageInput.Focus();
        }

        private FeedbackKind SelectedKind
            => KindIdea.IsChecked == true ? FeedbackKind.Idea
                : KindOther.IsChecked == true ? FeedbackKind.Other
                : FeedbackKind.Problem;

        private FeedbackReport BuildReport()
            => FeedbackReportBuilder.Build(new FeedbackSubmission(
                SelectedKind,
                MessageInput.Text,
                ReplyEmailBox.Text,
                IncludeDiagnosticsBox.IsChecked == true,
                _context.AppVersion,
                _context.WindowsVersion,
                _context.HardwareSummary,
                _context.InferenceMode,
                _context.RecentErrorLog));

        private void Kind_Checked(object sender, RoutedEventArgs e)
        {
            if (MessageLabel == null)
                return;

            (MessageLabel.Text, MessagePlaceholder.Text) = SelectedKind switch
            {
                FeedbackKind.Idea => ("What would make Axiom better?", "Describe the feature or change, and what it would help you do."),
                FeedbackKind.Other => ("Your message", "Questions, praise, anything else."),
                _ => ("What happened?", "Describe what you were doing and what went wrong. Steps to repeat it help a lot.")
            };
            RefreshPreview();
        }

        private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            bool hasText = !string.IsNullOrWhiteSpace(MessageInput.Text);
            MessagePlaceholder.Visibility = MessageInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            SendButton.IsEnabled = hasText && _sending == null;
            if (GitHubIssueLink != null)
                GitHubIssueLink.IsEnabled = hasText;
            RefreshPreview();
        }

        private void IncludeDiagnostics_Changed(object sender, RoutedEventArgs e) => RefreshPreview();

        private void PreviewLink_Click(object sender, RoutedEventArgs e)
        {
            PreviewBox.Visibility = PreviewBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            RefreshPreview();
        }

        private void RefreshPreview()
        {
            if (PreviewBox == null || PreviewBox.Visibility != Visibility.Visible)
                return;

            FeedbackReport report = BuildReport();
            PreviewBox.Text = "Subject: " + report.Subject + "\n\n" + report.Body;
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_sending != null || string.IsNullOrWhiteSpace(MessageInput.Text))
                return;

            FeedbackReport report = BuildReport();
            if (!FeedbackDelivery.CanSendDirectly)
            {
                OpenEmailFallback(report, reason: null);
                return;
            }

            _sending = new CancellationTokenSource();
            SendButton.IsEnabled = false;
            SendButton.Content = "Sending…";
            ShowStatus("Sending…", "MutedTextBrush");

            string? failure;
            try
            {
                failure = await FeedbackDelivery.TrySendAsync(report, ReplyEmailBox.Text, _sending.Token);
            }
            finally
            {
                _sending.Dispose();
                _sending = null;
            }

            if (failure == null)
            {
                ShowStatus("Sent. Thank you!", "SuccessBrush");
                SendButton.Content = "Sent";
                CloseButton.Content = "Close";
                await Task.Delay(1200);
                if (IsLoaded)
                    DialogResult = true;
                return;
            }

            SendButton.Content = "Send";
            SendButton.IsEnabled = true;
            OpenEmailFallback(report, failure);
        }

        private void OpenEmailFallback(FeedbackReport report, string? reason)
        {
            // The mailto link has to be trimmed, so the full report also goes on the clipboard.
            bool copied = TryCopy("Subject: " + report.Subject + "\n\n" + report.Body);
            bool opened = FeedbackDelivery.TryOpenEmailDraft(report);

            string lead = reason == null ? string.Empty : reason + " ";
            if (opened)
            {
                ShowStatus(lead + "Your email app should now be open with the report filled in. Press send there."
                    + (copied ? " The full report is also on your clipboard if anything was cut off." : string.Empty), "DarkSecondaryBrush");
                CloseButton.Content = "Close";
            }
            else
            {
                ShowStatus(lead + $"No email app opened. {(copied ? "The report is on your clipboard; " : string.Empty)}please email it to {FeedbackDelivery.FeedbackEmail}.",
                    "WarningBrush");
            }
        }

        // GitHub prefills issue forms from the URL; a few thousand characters per field keeps the
        // whole link well under browsers' URL limits.
        private const int GitHubFieldLimit = 2500;

        private void GitHubIssueLink_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MessageInput.Text))
                return;

            var submission = new FeedbackSubmission(
                SelectedKind,
                MessageInput.Text,
                ReplyEmailBox.Text,
                IncludeDiagnosticsBox.IsChecked == true,
                _context.AppVersion,
                _context.WindowsVersion,
                _context.HardwareSummary,
                _context.InferenceMode,
                _context.RecentErrorLog);
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                    FeedbackReportBuilder.BuildGitHubIssueUri(submission, GitHubFieldLimit)) { UseShellExecute = true });
                ShowStatus("GitHub's issue form should now be open in your browser with your report filled in. Sign in and press Submit there.", "DarkSecondaryBrush");
                CloseButton.Content = "Close";
            }
            catch (Exception ex)
            {
                ShowStatus("Couldn't open the browser: " + ex.Message, "WarningBrush");
            }
        }

        private static bool TryCopy(string text)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ShowStatus(string text, string brushKey)
        {
            StatusText.Text = text;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            StatusText.Visibility = Visibility.Visible;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            _sending?.Cancel();
            DialogResult = false;
        }
    }
}
