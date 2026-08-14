using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private readonly AxiomCapabilityRegistry _capabilityRegistry = AxiomCapabilityRegistry.Shared;

        private void InitializeCapabilities()
        {
            try
            {
                _capabilityRegistry.EnsureLoaded();
                RefreshCapabilityUi();
                if (!string.IsNullOrWhiteSpace(_capabilityRegistry.LastLoadStatusMessage))
                    ShowTransientStatus(_capabilityRegistry.LastLoadStatusMessage);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Capability registry initialization failed: {ex.Message}");
                ShowTransientStatus("Skills and Plugins could not be loaded.");
            }
        }

        private string BuildAttachedCapabilityInstruction(string userMessage, string surfaceName)
            => _capabilityRegistry.BuildSystemInstruction(userMessage, surfaceName);

        private void OpenSkills_Click(object sender, RoutedEventArgs e)
        {
            PluginsPopup.IsOpen = false;
            RefreshSkillsPanel();
            SkillsPopup.IsOpen = true;
        }

        private void OpenPlugins_Click(object sender, RoutedEventArgs e)
        {
            SkillsPopup.IsOpen = false;
            RefreshPluginsPanel();
            PluginsPopup.IsOpen = true;
        }

        private void CloseSkills_Click(object sender, RoutedEventArgs e) => SkillsPopup.IsOpen = false;
        private void ClosePlugins_Click(object sender, RoutedEventArgs e) => PluginsPopup.IsOpen = false;

        private void SkillsPopup_Opened(object? sender, EventArgs e) => AnimateCapabilityFlyout(SkillsFlyoutCard);
        private void PluginsPopup_Opened(object? sender, EventArgs e) => AnimateCapabilityFlyout(PluginsFlyoutCard);

        private static void AnimateCapabilityFlyout(Border card)
        {
            if (card.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                card.RenderTransform = translate;
            }

            card.Opacity = 0;
            translate.Y = 14;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }

        private void RefreshCapabilityUi()
        {
            _capabilityRegistry.EnsureLoaded();
            int attachedSkills = _capabilityRegistry.Skills.Count(skill => skill.IsAttached);
            int attachedPlugins = _capabilityRegistry.Plugins.Count(plugin => plugin.IsAttached);

            if (SkillsButtonCountText != null)
                SkillsButtonCountText.Text = attachedSkills.ToString();
            if (SkillsButtonBadge != null)
                SkillsButtonBadge.Visibility = attachedSkills > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (PluginsButtonCountText != null)
                PluginsButtonCountText.Text = attachedPlugins.ToString();
            if (PluginsButtonBadge != null)
                PluginsButtonBadge.Visibility = attachedPlugins > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (SkillsFlyoutSummaryText != null)
                SkillsFlyoutSummaryText.Text = attachedSkills == 0 ? "No Skills attached" : $"{attachedSkills} attached globally";
            if (PluginsFlyoutSummaryText != null)
                PluginsFlyoutSummaryText.Text = attachedPlugins == 0 ? "No Plugins attached" : $"{attachedPlugins} attached globally";
        }

        private void RefreshSkillsPanel()
        {
            if (SkillsListPanel == null)
                return;

            _capabilityRegistry.EnsureLoaded();
            SkillsListPanel.Children.Clear();
            foreach (AxiomSkillDefinition skill in _capabilityRegistry.Skills
                .OrderByDescending(item => item.IsAttached)
                .ThenByDescending(item => item.IsBuiltIn)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                SkillsListPanel.Children.Add(BuildSkillCard(skill));
            }
            RefreshCapabilityUi();
        }

        private void RefreshPluginsPanel()
        {
            if (PluginsListPanel == null)
                return;

            _capabilityRegistry.EnsureLoaded();
            PluginsListPanel.Children.Clear();
            foreach (AxiomPluginDefinition plugin in _capabilityRegistry.Plugins
                .OrderByDescending(item => item.IsAttached)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                PluginsListPanel.Children.Add(BuildPluginCard(plugin));
            }
            RefreshCapabilityUi();
        }

        private Border BuildSkillCard(AxiomSkillDefinition skill)
        {
            var actionButton = BuildAttachButton(skill.IsAttached);
            actionButton.Click += (_, _) =>
            {
                try
                {
                    _capabilityRegistry.SetSkillAttached(skill.Id, !skill.IsAttached);
                    RefreshSkillsPanel();
                    ShowTransientStatus($"{skill.Name} {(skill.IsAttached ? "attached" : "detached")} for every model and mode.");
                }
                catch (Exception ex)
                {
                    ShowTransientStatus($"Could not update {skill.Name}: {ex.Message}");
                }
            };

            Button? removeButton = null;
            if (!skill.IsBuiltIn)
            {
                removeButton = new Button
                {
                    Content = "Remove",
                    Height = 28,
                    Margin = new Thickness(0, 7, 0, 0),
                    Padding = new Thickness(8, 0, 8, 0),
                    Background = Brushes.Transparent,
                    Foreground = AppBrushCache.Get("#A69D92"),
                    BorderBrush = AppBrushCache.Get("#4B4239"),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    FontSize = 10
                };
                removeButton.Click += (_, _) =>
                {
                    if (_capabilityRegistry.RemoveCustomSkill(skill.Id))
                    {
                        RefreshSkillsPanel();
                        ShowTransientStatus($"Removed custom Skill: {skill.Name}.");
                    }
                };
            }

            return BuildCapabilityCard(
                skill.IconGlyph,
                skill.Name,
                skill.Description,
                skill.IsBuiltIn ? "Built in • activates when relevant" : "Custom • instruction-based",
                skill.IsAttached,
                actionButton,
                removeButton);
        }

        private Border BuildPluginCard(AxiomPluginDefinition plugin)
        {
            var actionButton = BuildAttachButton(plugin.IsAttached);
            actionButton.Click += (_, _) =>
            {
                try
                {
                    _capabilityRegistry.SetPluginAttached(plugin.Id, !plugin.IsAttached);
                    RefreshPluginsPanel();
                    ShowTransientStatus($"{plugin.Name} {(plugin.IsAttached ? "attached" : "detached")} for every compatible mode.");
                }
                catch (Exception ex)
                {
                    ShowTransientStatus($"Could not update {plugin.Name}: {ex.Message}");
                }
            };

            string availability = string.Equals(plugin.Id, AxiomCapabilityRegistry.ConnectedAppsPluginId, StringComparison.OrdinalIgnoreCase)
                ? "Cloud/Hybrid • configured connectors only"
                : "Axiom native • no separate install";
            return BuildCapabilityCard(
                plugin.IconGlyph,
                plugin.Name,
                plugin.Description,
                plugin.CapabilityLabel + " • " + availability,
                plugin.IsAttached,
                actionButton,
                null);
        }

        private static Border BuildCapabilityCard(
            string glyph,
            string title,
            string description,
            string metadata,
            bool attached,
            Button actionButton,
            Button? secondaryButton)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new Border
            {
                Width = 36,
                Height = 36,
                CornerRadius = new CornerRadius(10),
                Background = AppBrushCache.Get(attached ? "#3A3226" : "#171615"),
                BorderBrush = AppBrushCache.Get(attached ? "#B8924A" : "#302D2A"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = glyph.Length > 2 ? 9 : 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = AppBrushCache.Get(attached ? "#D8B56B" : "#BFB6AA"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            Grid.SetColumn(icon, 0);
            grid.Children.Add(icon);

            var copy = new StackPanel { Margin = new Thickness(2, 0, 12, 0) };
            copy.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = AppBrushCache.Get("#EDE8E3"),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            copy.Children.Add(new TextBlock
            {
                Text = description,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = AppBrushCache.Get("#A69D92"),
                FontSize = 11,
                LineHeight = 15,
                TextWrapping = TextWrapping.Wrap
            });
            copy.Children.Add(new TextBlock
            {
                Text = metadata,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = AppBrushCache.Get(attached ? "#D8B56B" : "#70685F"),
                FontSize = 9,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(copy, 1);
            grid.Children.Add(copy);

            var actions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(actionButton);
            if (secondaryButton != null)
                actions.Children.Add(secondaryButton);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            return new Border
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(12),
                Background = AppBrushCache.Get(attached ? "#28231E" : "#1B1917"),
                BorderBrush = AppBrushCache.Get(attached ? "#4B4239" : "#302D2A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Child = grid
            };
        }

        private static Button BuildAttachButton(bool attached) => new()
        {
            Content = attached ? "Attached ✓" : "Attach",
            MinWidth = 74,
            Height = 30,
            Padding = new Thickness(10, 0, 10, 0),
            Background = AppBrushCache.Get(attached ? "#B8924A" : "#24211F"),
            Foreground = AppBrushCache.Get(attached ? "#171615" : "#EDE8E3"),
            BorderBrush = AppBrushCache.Get(attached ? "#B8924A" : "#4B4239"),
            BorderThickness = new Thickness(1),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };

        private void CreateCustomSkill_Click(object sender, RoutedEventArgs e)
        {
            SkillsPopup.IsOpen = false;
            CustomSkillDraft? draft = ShowCustomSkillDialog();
            if (draft == null)
                return;

            try
            {
                AxiomSkillDefinition created = _capabilityRegistry.AddCustomSkill(
                    draft.Name,
                    draft.Description,
                    draft.Instructions,
                    draft.ActivationTerms);
                RefreshCapabilityUi();
                ShowTransientStatus($"Created and attached custom Skill: {created.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Create Skill", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private CustomSkillDraft? ShowCustomSkillDialog()
        {
            var dialog = new Window
            {
                Owner = this,
                Title = "Create custom Skill",
                Width = 560,
                Height = 650,
                MinWidth = 480,
                MinHeight = 560,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                Background = AppBrushCache.Get("#171615"),
                Foreground = AppBrushCache.Get("#EDE8E3")
            };

            var root = new Grid { Margin = new Thickness(24) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            heading.Children.Add(new TextBlock { Text = "Create custom Skill", FontSize = 22, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock
            {
                Text = "Instruction-based Skills are stored locally and become available to every model and mode. Custom executable scripts are intentionally not accepted here.",
                Margin = new Thickness(0, 7, 0, 0),
                Foreground = AppBrushCache.Get("#A69D92"),
                FontSize = 11,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            TextBox nameBox = AddLabeledInput(root, 1, "Name", "Example: Product brief writer", 40, false);
            TextBox descriptionBox = AddLabeledInput(root, 2, "Description", "What this Skill helps the model do", 64, false);
            TextBox instructionsBox = AddLabeledInput(root, 3, "Instructions", "Write the repeatable procedure the model should follow...", 160, true);
            TextBox termsBox = AddLabeledInput(root, 4, "Activation terms", "Comma-separated words or phrases, e.g. product brief, PRD, requirements", 58, false);

            var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cancel = new Button
            {
                Content = "Cancel",
                Width = 88,
                Height = 36,
                Margin = new Thickness(0, 0, 8, 0),
                Background = AppBrushCache.Get("#24211F"),
                Foreground = AppBrushCache.Get("#EDE8E3"),
                BorderBrush = AppBrushCache.Get("#4B4239"),
                BorderThickness = new Thickness(1),
                IsCancel = true
            };
            var create = new Button
            {
                Content = "Create & attach",
                Width = 126,
                Height = 36,
                Background = AppBrushCache.Get("#B8924A"),
                Foreground = AppBrushCache.Get("#171615"),
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
                IsDefault = true
            };
            Grid.SetColumn(cancel, 1);
            Grid.SetColumn(create, 2);
            footer.Children.Add(cancel);
            footer.Children.Add(create);
            Grid.SetRow(footer, 5);
            root.Children.Add(footer);

            CustomSkillDraft? result = null;
            create.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(nameBox.Text) || string.IsNullOrWhiteSpace(instructionsBox.Text))
                {
                    MessageBox.Show(dialog, "Enter a name and instructions for the Skill.", "Create Skill", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                result = new CustomSkillDraft(
                    nameBox.Text.Trim(),
                    descriptionBox.Text.Trim(),
                    instructionsBox.Text.Trim(),
                    termsBox.Text.Trim());
                dialog.DialogResult = true;
            };

            dialog.Content = root;
            _ = dialog.ShowDialog();
            return result;
        }

        private static TextBox AddLabeledInput(Grid root, int row, string label, string hint, double minHeight, bool multiline)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppBrushCache.Get("#EDE8E3")
            });
            var box = new TextBox
            {
                MinHeight = minHeight,
                Padding = new Thickness(10, 8, 10, 8),
                Background = AppBrushCache.Get("#211F1D"),
                Foreground = AppBrushCache.Get("#EDE8E3"),
                BorderBrush = AppBrushCache.Get("#4B4239"),
                BorderThickness = new Thickness(1),
                FontSize = 12,
                AcceptsReturn = multiline,
                TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                ToolTip = hint
            };
            panel.Children.Add(box);
            Grid.SetRow(panel, row);
            root.Children.Add(panel);
            return box;
        }

        private sealed record CustomSkillDraft(string Name, string Description, string Instructions, string ActivationTerms);
    }
}
