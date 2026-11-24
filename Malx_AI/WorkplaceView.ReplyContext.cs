using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    /// <summary>
    /// Workplace: right-click a reply card to copy it or attach the selected part as context
    /// for the next message, shown as a quote bar above the input box.
    /// </summary>
    public partial class WorkplaceView
    {
        private string? _replyContextText;

        internal void AttachReplyContext(string text)
        {
            if (!ReplyContextAttachment.IsUsable(text))
                return;
            _replyContextText = text.Trim();
            WorkplaceReplyContextHost.Child = ReplyContextUi.CreateQuoteBar(_replyContextText, ClearReplyContext);
            WorkplaceReplyContextHost.Visibility = Visibility.Visible;
            QueryInput.Focus();
            QueryInput.CaretOffset = QueryInput.Document.TextLength;
        }

        private void ClearReplyContext()
        {
            _replyContextText = null;
            WorkplaceReplyContextHost.Child = null;
            WorkplaceReplyContextHost.Visibility = Visibility.Collapsed;
        }

        /// <summary>Folds an attached quote into the query just before it is sent.</summary>
        private void ApplyReplyContextToQuery()
        {
            if (_replyContextText == null || string.IsNullOrWhiteSpace(QueryInput.Text))
                return;
            QueryInput.Text = ReplyContextAttachment.Compose(_replyContextText, QueryInput.Text);
            ClearReplyContext();
        }

        private void WorkplaceReplyText_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement element)
                return;
            e.Handled = true;
            ContextMenu menu = ReplyContextUi.CreateMenu(
                (_, _) => ReplyContextUi.Copy(ReplyContextUi.SelectedOrWholeText(element)),
                (_, _) =>
                {
                    string? text = ReplyContextUi.SelectedOrWholeText(element);
                    if (text != null)
                        AttachReplyContext(text);
                });
            menu.PlacementTarget = element;
            menu.IsOpen = true;
        }
    }
}
