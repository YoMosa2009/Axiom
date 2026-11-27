using System.Windows;
using System.Windows.Input;

namespace Malx_AI
{
    /// <summary>
    /// The context line under the composer and the run failure card above it (the OpenCode /
    /// Kimi pattern: how full the context is, always in view, and a short card with buttons when a
    /// model request fails).
    /// </summary>
    public partial class WorkplaceView
    {
        private bool _runFailureCardWired;

        /// <summary>Called by RefreshContextPanel with the same numbers the Context panel shows.</summary>
        private void UpdateComposerContextLine(double percent, int used, int capacity, bool measured)
        {
            if (ComposerContextText == null)
                return;
            if (capacity <= 0 || used <= 0)
            {
                ComposerContextText.Text = string.Empty;
                return;
            }
            ComposerContextText.Text = $"{percent:0}% context";
            ComposerContextText.ToolTip = $"{FormatCompactTokenCount(used)} of {FormatCompactTokenCount(capacity)} tokens "
                + (measured ? "(reported by the model provider)" : "(estimated)") + ". Click for the breakdown.";
        }

        private void ComposerContextText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            => WorkplaceContextSidebarButton_Click(sender, new RoutedEventArgs());

        /// <summary>
        /// Shows the failure card for a provider error the user can act on. Returns false for an
        /// error the card has nothing useful to say about (the caller keeps its own message).
        /// </summary>
        private bool ShowRunFailureCard(RunFailureAdvice advice)
        {
            if (WorkplaceRunFailureCard == null
                || advice.Kind is RunFailureKind.Unknown or RunFailureKind.Cancelled)
                return false;

            if (!_runFailureCardWired)
            {
                _runFailureCardWired = true;
                WorkplaceRunFailureCard.ActionRequested += OnRunFailureAction;
            }
            Dispatcher.Invoke(() =>
            {
                WorkplaceRunFailureCard.Show(advice);
                WorkplaceRunFailureCard.Visibility = Visibility.Visible;
            });
            return true;
        }

        private void HideRunFailureCard()
        {
            if (WorkplaceRunFailureCard != null && WorkplaceRunFailureCard.Visibility == Visibility.Visible)
                WorkplaceRunFailureCard.Visibility = Visibility.Collapsed;
        }

        private void OnRunFailureAction(RunFailureAction action)
        {
            HideRunFailureCard();
            switch (action)
            {
                case RunFailureAction.Retry:
                case RunFailureAction.CompactAndRetry:
                    // Completed steps are kept by the run, so "continue" picks up where it stopped.
                    if (_isProcessing)
                        return;
                    QueryInput.Text = "continue";
                    SendMessage_Click(this, new RoutedEventArgs());
                    break;
                case RunFailureAction.SwitchModel:
                case RunFailureAction.OpenSettings:
                    if (WorkplaceCloudModelCombo != null)
                    {
                        WorkplaceCloudModelCombo.Focus();
                        WorkplaceCloudModelCombo.IsDropDownOpen = true;
                    }
                    break;
            }
        }
    }
}
