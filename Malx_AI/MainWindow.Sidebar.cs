using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Malx_AI
{
    /// <summary>
    /// The left sidebar's list items for chats and workplaces: one look for both, an accent rail
    /// on the active item, and a ⋯ button (also the right-click menu) for rename, pin and delete.
    /// </summary>
    public partial class MainWindow
    {
        private const string SidebarIconFontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

        private sealed record SidebarMenuAction(string Header, string Glyph, RoutedEventHandler Click, object Tag, bool IsDestructive = false);

        private Button CreateSidebarItem(string title, object tag, bool pinned, IReadOnlyList<SidebarMenuAction> actions, string? toolTip = null)
        {
            var titleText = new TextBlock
            {
                Text = title,
                Style = (Style)FindResource("SidebarRowLabelStyle"),
                ToolTip = title
            };

            var pinGlyph = new TextBlock
            {
                Text = "",
                FontFamily = new FontFamily(SidebarIconFontFamily),
                FontSize = 11,
                LineHeight = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 2, 0),
                Visibility = pinned ? Visibility.Visible : Visibility.Collapsed,
                ToolTip = "Pinned"
            };
            pinGlyph.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            ContextMenu menu = BuildSidebarItemMenu(actions);
            var moreButton = new Button
            {
                Style = (Style)FindResource("SidebarItemMoreButtonStyle"),
                Margin = new Thickness(4, 0, 0, 0),
                ContextMenu = menu
            };
            moreButton.Click += (_, e) =>
            {
                menu.PlacementTarget = moreButton;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                e.Handled = true;
            };

            var content = new Grid { Height = 36 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(titleText);
            Grid.SetColumn(pinGlyph, 1);
            content.Children.Add(pinGlyph);
            Grid.SetColumn(moreButton, 2);
            content.Children.Add(moreButton);

            var item = new Button
            {
                Content = content,
                Style = (Style)FindResource("SidebarListItemButtonStyle"),
                Tag = tag,
                ContextMenu = menu,
                ToolTip = toolTip
            };
            System.Windows.Automation.AutomationProperties.SetName(item, title);
            return item;
        }

        private ContextMenu BuildSidebarItemMenu(IReadOnlyList<SidebarMenuAction> actions)
        {
            var menu = new ContextMenu();
            menu.SetResourceReference(BackgroundProperty, "NotebookSurfaceBrush");
            menu.SetResourceReference(ForegroundProperty, "NotebookTextBrush");
            menu.SetResourceReference(BorderBrushProperty, "NotebookBorderBrush");

            foreach (SidebarMenuAction action in actions)
            {
                var icon = new TextBlock
                {
                    Text = action.Glyph,
                    FontFamily = new FontFamily(SidebarIconFontFamily),
                    FontSize = 12,
                    LineHeight = double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var menuItem = new MenuItem { Header = action.Header, Tag = action.Tag, Icon = icon };
                if (action.IsDestructive)
                {
                    menuItem.SetResourceReference(ForegroundProperty, "DangerBrush");
                    icon.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                }
                else
                {
                    icon.SetResourceReference(TextBlock.ForegroundProperty, "DarkSecondaryBrush");
                }

                menuItem.Click += action.Click;
                menu.Items.Add(menuItem);
            }

            return menu;
        }

        private static TextBlock? FindSidebarItemTitle(Button item)
            => item.Content is Grid grid ? grid.Children.OfType<TextBlock>().FirstOrDefault() : null;

        private static void SetSidebarItemTitle(Button item, string title)
        {
            if (FindSidebarItemTitle(item) is TextBlock titleText)
            {
                titleText.Text = title;
                titleText.ToolTip = title;
            }
            else
            {
                item.Content = title;
            }

            System.Windows.Automation.AutomationProperties.SetName(item, title);
        }

        /// <summary>
        /// Moves the highlight. The colours are resource references, so a theme change repaints
        /// the active item without any code here running again.
        /// </summary>
        private static void SetSidebarItemActive(Button? previous, Button next)
        {
            if (previous != null && !ReferenceEquals(previous, next))
            {
                previous.ClearValue(BackgroundProperty);
                previous.ClearValue(BorderBrushProperty);
                previous.ClearValue(ForegroundProperty);
            }

            next.SetResourceReference(BackgroundProperty, "AccentSoftBrush");
            next.SetResourceReference(BorderBrushProperty, "PrimaryAccentBrush");
            next.SetResourceReference(ForegroundProperty, "NotebookTextBrush");
        }

        private TextBlock CreateSidebarGroupHeader(string label)
            => new() { Text = label, Style = (Style)FindResource("SidebarGroupHeaderStyle") };

        /// <summary>Chat list grouped as Pinned, Today, Yesterday, Previous 7 days and so on.</summary>
        private void RenderChatHistoryList(IEnumerable<ChatIndexEntry> entries)
        {
            ChatHistoryStack.Children.Clear();
            _activeChatButton = null;

            DateTime now = DateTime.Now;
            string? currentGroup = null;
            foreach (ChatIndexEntry entry in entries
                .OrderByDescending(item => item.IsPinned)
                .ThenByDescending(item => item.UpdatedAt))
            {
                string group = SidebarHistoryGroups.LabelFor(entry.UpdatedAt, now, entry.IsPinned);
                if (!string.Equals(group, currentGroup, StringComparison.Ordinal))
                {
                    ChatHistoryStack.Children.Add(CreateSidebarGroupHeader(group));
                    currentGroup = group;
                }

                string name = string.IsNullOrWhiteSpace(entry.Name) ? $"Chat{entry.Id}" : entry.Name;
                AddChatToHistory(name, entry.Id);
            }
        }

        private async void DeleteChatMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: int chatId })
                return;

            string name = _chatNames.TryGetValue(chatId, out string? savedName) ? savedName : "this chat";
            if (!ConfirmSidebarDelete($"Delete “{name}”?", "The chat and its messages are removed from this computer. This can't be undone."))
                return;

            await DeleteChatAsync(chatId);
        }

        private void RenameWorkplaceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: int workplaceId })
                return;

            string currentName = _workplaceChatNames.TryGetValue(workplaceId, out string? savedName) ? savedName : $"Workplace {workplaceId}";
            var dialog = new ChatRenameDialog(currentName) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ChatName))
                return;

            string newName = dialog.ChatName.Trim();
            _workplaceChatNames[workplaceId] = newName;
            Button? item = WorkplaceHistoryStack.Children.OfType<Button>().FirstOrDefault(b => b.Tag is int id && id == workplaceId);
            if (item != null)
                SetSidebarItemTitle(item, newName);

            _ = QueueCoordinatedChatPersistenceAsync(false, false, true, false);
        }

        private async void DeleteWorkplaceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: int workplaceId })
                return;

            if (_workplaceChats.Count <= 1)
            {
                ShowTransientStatus("Axiom keeps at least one workplace. Create another before deleting this one.");
                return;
            }

            string name = _workplaceChatNames.TryGetValue(workplaceId, out string? savedName) ? savedName : "this workplace";
            if (!ConfirmSidebarDelete($"Delete “{name}”?", "Its conversation and run history are removed from this computer. Files on disk are not touched. This can't be undone."))
                return;

            await DeleteWorkplaceAsync(workplaceId);
        }

        private bool ConfirmSidebarDelete(string title, string detail)
            => MessageBox.Show(this, detail, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;

        private void WorkplaceSearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => ApplyWorkplaceSearchFilter();

        private void ApplyWorkplaceSearchFilter()
        {
            if (WorkplaceSearchBox is null || WorkplaceHistoryStack is null || WorkplaceListHeaderText is null)
                return;

            string query = WorkplaceSearchBox.Text?.Trim() ?? string.Empty;
            int shown = 0;
            foreach (Button item in WorkplaceHistoryStack.Children.OfType<Button>())
            {
                string title = FindSidebarItemTitle(item)?.Text ?? string.Empty;
                bool match = query.Length == 0 || title.Contains(query, StringComparison.OrdinalIgnoreCase);
                item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match)
                    shown++;
            }

            WorkplaceListHeaderText.Text = query.Length == 0
                ? "Workplaces"
                : shown == 0 ? "No matching workplaces" : $"{shown} matching";
        }

        /// <summary>Shows the Chat or the Workplace set of sidebar controls.</summary>
        private void ShowSidebarSection(bool workplace)
        {
            ChatHistorySectionContainer.Visibility = workplace ? Visibility.Collapsed : Visibility.Visible;
            WorkplaceHistorySectionContainer.Visibility = workplace ? Visibility.Visible : Visibility.Collapsed;
            NewWorkplaceButton.Visibility = workplace ? Visibility.Visible : Visibility.Collapsed;
            WorkplaceSearchPanel.Visibility = workplace ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
