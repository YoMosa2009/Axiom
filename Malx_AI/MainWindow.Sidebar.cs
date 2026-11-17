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
            var menu = new ContextMenu { MinWidth = 176 };
            menu.SetResourceReference(BackgroundProperty, "NotebookSurfaceBrush");
            menu.SetResourceReference(ForegroundProperty, "NotebookTextBrush");
            menu.SetResourceReference(BorderBrushProperty, "BorderStrongBrush");

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

            await DeleteChatAsync(chatId);
        }

        private void RenameWorkplaceMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: int workplaceId })
                return;

            string currentName = _workplaceChatNames.TryGetValue(workplaceId, out string? savedName) ? savedName : "New session";
            var dialog = new ChatRenameDialog(currentName) { Owner = this };
            if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.ChatName))
                return;

            string newName = dialog.ChatName.Trim();
            _workplaceChatNames[workplaceId] = newName;
            if (_workplaceChats.TryGetValue(workplaceId, out var snap))
            {
                snap.SessionTitle = newName;
                snap.UpdatedAt = DateTime.Now;
            }
            _workplaceUpdatedAt[workplaceId] = DateTime.Now;
            if (workplaceId == _currentWorkplaceChatId)
                WorkplaceViewControl.RenameSessionTitle(newName);

            RebuildWorkplaceHistoryUi();
            _ = QueueCoordinatedChatPersistenceAsync(false, false, true, false);
        }

        private void AssignWorkplaceProjectMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem { Tag: int workplaceId })
                return;

            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Assign a strict project folder to this session" };
            if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;

            string folder = dialog.FolderName.Trim();
            AddPinnedProjectFolder(folder);
            _workplaceProjectFolders[workplaceId] = folder;
            if (_workplaceChats.TryGetValue(workplaceId, out var snap))
            {
                snap.ProjectFolderPath = folder;
                snap.UpdatedAt = DateTime.Now;
            }
            if (workplaceId == _currentWorkplaceChatId)
                WorkplaceViewControl.SetProjectFolderScope(folder);

            RebuildWorkplaceHistoryUi();
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

            await DeleteWorkplaceAsync(workplaceId);
        }

        private void WorkplaceFilterProjects_Click(object sender, RoutedEventArgs e)
        {
            _hideEmptyProjectFolders = !_hideEmptyProjectFolders;
            if (WorkplaceFilterProjectsButton != null)
                WorkplaceFilterProjectsButton.Opacity = _hideEmptyProjectFolders ? 1.0 : 0.65;
            RebuildWorkplaceHistoryUi();
            ShowTransientStatus(_hideEmptyProjectFolders
                ? "Hiding empty project folders."
                : "Showing all project folders.");
        }

        private void WorkplaceAddProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a project folder to add"
            };
            if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FolderName))
                return;

            string folder = dialog.FolderName.Trim();
            AddPinnedProjectFolder(folder);
            RebuildWorkplaceHistoryUi();
            _ = QueueCoordinatedChatPersistenceAsync(false, false, true, false);
            ShowTransientStatus($"Added project {WorkplaceSessionTitleGenerator.ShortProjectLabel(folder)}");
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
                string title = item.ToolTip as string ?? string.Empty;
                if (item.Content is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock tb)
                    title = tb.Text;

                bool match = query.Length == 0 || title.Contains(query, StringComparison.OrdinalIgnoreCase);
                item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                if (match)
                    shown++;
            }

            if (WorkplaceProjectsStack != null)
            {
                foreach (StackPanel group in WorkplaceProjectsStack.Children.OfType<StackPanel>())
                {
                    int groupMatches = 0;
                    foreach (UIElement child in group.Children)
                    {
                        if (child is Button item && item.Tag is int)
                        {
                            string title = item.ToolTip as string ?? string.Empty;
                            if (item.Content is Grid g && g.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock tb)
                                title = tb.Text;
                            bool match = query.Length == 0 || title.Contains(query, StringComparison.OrdinalIgnoreCase);
                            item.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                            if (match)
                                groupMatches++;
                        }
                    }

                    group.Visibility = query.Length == 0 || groupMatches > 0 ? Visibility.Visible : Visibility.Collapsed;
                    shown += groupMatches;
                }
            }

            WorkplaceListHeaderText.Text = query.Length == 0
                ? "Projects"
                : shown == 0 ? "No matching" : $"Projects ({shown})";
        }

        /// <summary>Builds a project folder header + nested session rows matching Image 5.</summary>
        private UIElement CreateProjectGroupElement(string folderPath, List<int> sessionIds)
        {
            var groupPanel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };

            var headerGrid = new Grid { Height = 26, Margin = new Thickness(4, 1, 2, 1), ToolTip = folderPath };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var folderGlyph = new TextBlock
            {
                Text = "\uE8B7",
                FontFamily = new FontFamily(SidebarIconFontFamily),
                FontSize = 11.5,
                LineHeight = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 7, 0)
            };
            folderGlyph.SetResourceReference(TextBlock.ForegroundProperty, "DarkSecondaryBrush");

            var folderTitle = new TextBlock
            {
                Text = WorkplaceSessionTitleGenerator.ShortProjectLabel(folderPath),
                FontSize = 11.5,
                FontWeight = FontWeights.Medium,
                LineHeight = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            folderTitle.SetResourceReference(TextBlock.ForegroundProperty, "DarkSecondaryBrush");
            Grid.SetColumn(folderTitle, 1);

            var menu = new ContextMenu();
            menu.SetResourceReference(BackgroundProperty, "NotebookSurfaceBrush");
            menu.SetResourceReference(ForegroundProperty, "NotebookTextBrush");
            menu.SetResourceReference(BorderBrushProperty, "NotebookBorderBrush");

            var newInProjectItem = new MenuItem { Header = "New session in project" };
            newInProjectItem.Click += async (_, _) => await CreateWorkplaceInProjectAsync(folderPath);
            menu.Items.Add(newInProjectItem);

            var removeProjectItem = new MenuItem { Header = "Remove project folder" };
            removeProjectItem.Click += (_, _) =>
            {
                _pinnedProjectFolders.RemoveAll(p => string.Equals(p, folderPath, StringComparison.OrdinalIgnoreCase));
                RebuildWorkplaceHistoryUi();
                _ = QueueCoordinatedChatPersistenceAsync(false, false, true, false);
            };
            menu.Items.Add(removeProjectItem);

            var moreBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "\uE712",
                    FontFamily = new FontFamily(SidebarIconFontFamily),
                    FontSize = 10,
                    LineHeight = double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = "Project options",
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(2, 0, 0, 0)
            };
            moreBtn.Click += (_, ev) =>
            {
                menu.PlacementTarget = moreBtn;
                menu.Placement = PlacementMode.Bottom;
                menu.IsOpen = true;
                ev.Handled = true;
            };
            Grid.SetColumn(moreBtn, 2);

            var addSessionBtn = new Button
            {
                Content = new TextBlock
                {
                    Text = "\uE710",
                    FontFamily = new FontFamily(SidebarIconFontFamily),
                    FontSize = 10,
                    LineHeight = double.NaN,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                },
                Width = 20,
                Height = 20,
                Padding = new Thickness(0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                ToolTip = $"New session in {WorkplaceSessionTitleGenerator.ShortProjectLabel(folderPath)}",
                Cursor = System.Windows.Input.Cursors.Hand,
                Margin = new Thickness(2, 0, 0, 0)
            };
            addSessionBtn.Click += async (_, _) => await CreateWorkplaceInProjectAsync(folderPath);
            Grid.SetColumn(addSessionBtn, 3);

            headerGrid.Children.Add(folderGlyph);
            headerGrid.Children.Add(folderTitle);
            headerGrid.Children.Add(moreBtn);
            headerGrid.Children.Add(addSessionBtn);
            groupPanel.Children.Add(headerGrid);

            if (sessionIds.Count == 0)
            {
                var emptyText = new TextBlock
                {
                    Text = "No conversations yet",
                    FontSize = 11,
                    LineHeight = double.NaN,
                    Margin = new Thickness(22, 4, 8, 4)
                };
                emptyText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                groupPanel.Children.Add(emptyText);
            }
            else
            {
                foreach (int id in sessionIds)
                {
                    string title = _workplaceChatNames.TryGetValue(id, out string? n) ? n : "New session";
                    DateTime updated = _workplaceUpdatedAt.TryGetValue(id, out DateTime dt) ? dt : DateTime.Now;
                    groupPanel.Children.Add(CreateWorkplaceTreeRow(id, title, updated, indentLeft: 18));
                }
            }

            return groupPanel;
        }

        private Button CreateWorkplaceTreeRow(int workplaceId, string title, DateTime updatedAt, double indentLeft)
        {
            var menu = BuildSidebarItemMenu(
            [
                new SidebarMenuAction("Rename", "\uE8AC", RenameWorkplaceMenuItem_Click, workplaceId),
                new SidebarMenuAction("Assign project folder…", "\uE8B7", AssignWorkplaceProjectMenuItem_Click, workplaceId),
                new SidebarMenuAction("Delete", "\uE74D", DeleteWorkplaceMenuItem_Click, workplaceId, IsDestructive: true)
            ]);

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 11.5,
                LineHeight = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "NotebookTextBrush");

            var ageBlock = new TextBlock
            {
                Text = SidebarHistoryGroups.FormatRelativeAge(updatedAt, DateTime.Now),
                FontSize = 10,
                LineHeight = double.NaN,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 2, 0)
            };
            ageBlock.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");

            var grid = new Grid { Height = 26, Margin = new Thickness(indentLeft, 0, 4, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(titleBlock);
            Grid.SetColumn(ageBlock, 1);
            grid.Children.Add(ageBlock);

            var button = new Button
            {
                Content = grid,
                Tag = workplaceId,
                ContextMenu = menu,
                ToolTip = title,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 1, 0, 1),
                MinHeight = 26,
                Cursor = System.Windows.Input.Cursors.Hand,
                BorderThickness = new Thickness(1),
                BorderBrush = Brushes.Transparent,
                Background = Brushes.Transparent
            };
            button.Click += (_, _) => LoadWorkplaceChat(
                workplaceId,
                _workplaceChatNames.TryGetValue(workplaceId, out string? currentName) ? currentName : title);

            if (workplaceId == _currentWorkplaceChatId)
            {
                button.SetResourceReference(Button.BackgroundProperty, "SurfaceHoverBrush");
                button.SetResourceReference(Button.BorderBrushProperty, "NotebookBorderBrush");
                _activeWorkplaceButton = button;
            }

            System.Windows.Automation.AutomationProperties.SetName(button, title);
            return button;
        }

        private void AddPinnedProjectFolder(string? folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
                return;
            string clean = folderPath.Trim();
            if (!_pinnedProjectFolders.Any(p => string.Equals(p, clean, StringComparison.OrdinalIgnoreCase)))
                _pinnedProjectFolders.Add(clean);
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
