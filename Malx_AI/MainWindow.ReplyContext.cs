using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Malx_AI
{
    /// <summary>
    /// Chat: right-click an AI reply to copy it or attach the selected part as context for the
    /// next message, shown as a quote bar above the input box. Ctrl+Shift+L attaches the current
    /// selection in Chat or Workplace.
    /// </summary>
    public partial class MainWindow
    {
        private string? _replyContextText;

        private void InitializeReplyContext()
        {
            PreviewKeyDown += ReplyContext_PreviewKeyDown;
        }

        internal void AttachReplyContext(string text)
        {
            if (!ReplyContextAttachment.IsUsable(text))
                return;
            _replyContextText = text.Trim();
            ReplyContextHost.Child = ReplyContextUi.CreateQuoteBar(_replyContextText, ClearReplyContext);
            ReplyContextHost.Visibility = Visibility.Visible;
            InputBox.Focus();
            InputBox.CaretIndex = InputBox.Text?.Length ?? 0;
        }

        private void ClearReplyContext()
        {
            _replyContextText = null;
            ReplyContextHost.Child = null;
            ReplyContextHost.Visibility = Visibility.Collapsed;
        }

        private void ReplyText_ContextMenuOpening(object sender, ContextMenuEventArgs e)
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

        private void ReplyContext_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!ReplyContextUi.AttachGesture.Matches(this, e))
                return;
            if (Keyboard.FocusedElement is not DependencyObject focused)
                return;

            string? selection = focused switch
            {
                TextBox { IsReadOnly: true, SelectionLength: > 0 } box => box.SelectedText,
                FlowDocumentScrollViewer viewer when !string.IsNullOrWhiteSpace(viewer.Selection?.Text) => viewer.Selection!.Text,
                _ => null,
            };
            if (selection == null)
                return;

            e.Handled = true;
            for (DependencyObject? node = focused; node != null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is WorkplaceView workplace)
                {
                    workplace.AttachReplyContext(selection);
                    return;
                }
            }
            AttachReplyContext(selection);
        }
    }
}
