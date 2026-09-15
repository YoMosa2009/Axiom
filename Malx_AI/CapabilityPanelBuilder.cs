using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Malx_AI
{
    internal sealed record CustomSkillDraft(
        string Name,
        string Description,
        string Instructions,
        string ActivationTerms,
        string DeliverableFormat);

    /// <summary>
    /// Builds the Skills and Plugins flyout contents. Shared so Normal Chat and the Workplace
    /// show the same cards for the same globally attached capabilities rather than drifting
    /// into two lists that describe the same registry differently.
    /// </summary>
    internal static class CapabilityPanelBuilder
    {
        public static void PopulateSkills(
            Panel target,
            AxiomCapabilityRegistry registry,
            Window owner,
            Action<string> onStatus,
            Action onChanged)
        {
            registry.EnsureLoaded();
            target.Children.Clear();
            foreach (AxiomSkillDefinition skill in registry.Skills
                .OrderByDescending(item => item.IsAttached)
                .ThenByDescending(item => item.IsBuiltIn)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                target.Children.Add(BuildSkillCard(skill, registry, onStatus, onChanged));
            }
        }

        public static void PopulatePlugins(
            Panel target,
            AxiomCapabilityRegistry registry,
            Action<string> onStatus,
            Action onChanged)
        {
            registry.EnsureLoaded();
            target.Children.Clear();
            foreach (AxiomPluginDefinition plugin in registry.Plugins
                .OrderByDescending(item => item.IsAttached)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                target.Children.Add(BuildPluginCard(plugin, registry, onStatus, onChanged));
            }
        }

        private static Border BuildSkillCard(
            AxiomSkillDefinition skill,
            AxiomCapabilityRegistry registry,
            Action<string> onStatus,
            Action onChanged)
        {
            Button actionButton = BuildAttachButton(skill.IsAttached);
            actionButton.Click += (_, _) =>
            {
                try
                {
                    registry.SetSkillAttached(skill.Id, !skill.IsAttached);
                    onChanged();
                    onStatus($"{skill.Name} {(skill.IsAttached ? "attached" : "detached")} for every model and mode.");
                }
                catch (Exception ex)
                {
                    onStatus($"Could not update {skill.Name}: {ex.Message}");
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
                    Foreground = AppTheme.Brush(p => p.TextSecondary),
                    BorderBrush = AppTheme.Brush(p => p.BorderStrong),
                    BorderThickness = new Thickness(1),
                    Cursor = Cursors.Hand,
                    FontSize = 10
                };
                removeButton.Click += (_, _) =>
                {
                    if (registry.RemoveCustomSkill(skill.Id))
                    {
                        onChanged();
                        onStatus($"Removed custom Skill: {skill.Name}.");
                    }
                };
            }

            return BuildCapabilityCard(
                skill.IconGlyph,
                skill.Name,
                skill.Description,
                skill.IsBuiltIn ? "Built in · activates when relevant" : "Custom · instruction-based",
                DescribeDelivery(skill),
                skill.IsAttached,
                actionButton,
                removeButton);
        }

        /// <summary>The one line that tells the user where a Skill's output actually lands.</summary>
        private static string DescribeDelivery(AxiomSkillDefinition skill)
        {
            if (!skill.RendersToCanvas)
                return "Answers in chat";

            string shape = skill.SmallModelFormat switch
            {
                SkillSmallModelFormats.Outline => "slide deck",
                SkillSmallModelFormats.Chart => "charted report",
                _ => "document"
            };
            return $"Renders a {shape} in Project Canvas";
        }

        private static Border BuildPluginCard(
            AxiomPluginDefinition plugin,
            AxiomCapabilityRegistry registry,
            Action<string> onStatus,
            Action onChanged)
        {
            Button actionButton = BuildAttachButton(plugin.IsAttached);
            actionButton.Click += (_, _) =>
            {
                try
                {
                    registry.SetPluginAttached(plugin.Id, !plugin.IsAttached);
                    onChanged();
                    onStatus($"{plugin.Name} {(plugin.IsAttached ? "attached" : "detached")} for every compatible mode.");
                }
                catch (Exception ex)
                {
                    onStatus($"Could not update {plugin.Name}: {ex.Message}");
                }
            };

            string availability = string.Equals(plugin.Id, AxiomCapabilityRegistry.ConnectedAppsPluginId, StringComparison.OrdinalIgnoreCase)
                ? "Cloud/Hybrid · configured connectors only"
                : "Axiom native · no separate install";
            return BuildCapabilityCard(
                plugin.IconGlyph,
                plugin.Name,
                plugin.Description,
                plugin.CapabilityLabel + " · " + availability,
                string.Empty,
                plugin.IsAttached,
                actionButton,
                null);
        }

        private static Border BuildCapabilityCard(
            string glyph,
            string title,
            string description,
            string metadata,
            string deliveryTag,
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
                Background = AppTheme.Brush(attached ? "#3A3226" : "#171615"),
                BorderBrush = AppTheme.Brush(attached ? "#B8924A" : "#302D2A"),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = glyph,
                    FontSize = glyph.Length > 2 ? 9 : 14,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = AppTheme.Brush(attached ? "#D8B56B" : "#BFB6AA"),
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
                Foreground = AppTheme.Brush(p => p.Text),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold
            });
            copy.Children.Add(new TextBlock
            {
                Text = description,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = AppTheme.Brush(p => p.TextSecondary),
                FontSize = 11,
                LineHeight = 15,
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(deliveryTag))
            {
                // The pill answers the question the old card left open: does this Skill hand me a
                // rendered thing, or more text in the transcript?
                bool rendersToCanvas = deliveryTag.Contains("Canvas", StringComparison.Ordinal);
                copy.Children.Add(new Border
                {
                    Margin = new Thickness(0, 7, 0, 0),
                    Padding = new Thickness(7, 2, 7, 3),
                    CornerRadius = new CornerRadius(6),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Background = AppTheme.Brush(rendersToCanvas ? "#3A3226" : "#24211F"),
                    BorderBrush = AppTheme.Brush(rendersToCanvas ? "#4B4239" : "#302D2A"),
                    BorderThickness = new Thickness(1),
                    Child = new TextBlock
                    {
                        Text = deliveryTag,
                        FontSize = 9,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = AppTheme.Brush(rendersToCanvas ? "#D8B56B" : "#8A8279")
                    }
                });
            }

            copy.Children.Add(new TextBlock
            {
                Text = metadata,
                Margin = new Thickness(0, 6, 0, 0),
                Foreground = AppTheme.Brush(attached ? "#D8B56B" : "#70685F"),
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
                Background = AppTheme.Brush(attached ? "#28231E" : "#1B1917"),
                BorderBrush = AppTheme.Brush(attached ? "#4B4239" : "#302D2A"),
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
            Background = AppTheme.Brush(attached ? "#B8924A" : "#24211F"),
            Foreground = AppTheme.Brush(attached ? "#171615" : "#EDE8E3"),
            BorderBrush = AppTheme.Brush(attached ? "#B8924A" : "#4B4239"),
            BorderThickness = new Thickness(1),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };

        public static CustomSkillDraft? ShowCustomSkillDialog(Window owner)
        {
            var dialog = new Window
            {
                Owner = owner,
                Title = "Create custom Skill",
                Width = 560,
                Height = 720,
                MinWidth = 480,
                MinHeight = 600,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.CanResize,
                ShowInTaskbar = false,
                Background = AppTheme.Brush(p => p.Background),
                Foreground = AppTheme.Brush(p => p.Text)
            };

            var root = new Grid { Margin = new Thickness(24) };
            for (int i = 0; i < 6; i++)
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            heading.Children.Add(new TextBlock { Text = "Create custom Skill", FontSize = 22, FontWeight = FontWeights.SemiBold });
            heading.Children.Add(new TextBlock
            {
                Text = "Instruction-based Skills are stored locally and become available to every model and mode. Custom executable scripts are intentionally not accepted here.",
                Margin = new Thickness(0, 7, 0, 0),
                Foreground = AppTheme.Brush(p => p.TextSecondary),
                FontSize = 11,
                LineHeight = 16,
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetRow(heading, 0);
            root.Children.Add(heading);

            TextBox nameBox = AddLabeledInput(root, 1, "Name", "Example: Product brief writer", 40, false);
            TextBox descriptionBox = AddLabeledInput(root, 2, "Description", "What this Skill helps the model do", 64, false);
            ComboBox deliveryBox = AddDeliverySelector(root, 3);
            TextBox instructionsBox = AddLabeledInput(root, 4, "Instructions", "Write the repeatable procedure the model should follow...", 160, true);
            TextBox termsBox = AddLabeledInput(root, 5, "Activation terms", "Comma-separated words or phrases, e.g. product brief, PRD, requirements", 58, false);

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
                Background = AppTheme.Brush(p => p.SurfaceRaised),
                Foreground = AppTheme.Brush(p => p.Text),
                BorderBrush = AppTheme.Brush(p => p.BorderStrong),
                BorderThickness = new Thickness(1),
                IsCancel = true
            };
            var create = new Button
            {
                Content = "Create & attach",
                Width = 126,
                Height = 36,
                Background = AppTheme.Brush(p => p.Accent),
                Foreground = AppTheme.Brush(p => p.Background),
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
                IsDefault = true
            };
            Grid.SetColumn(cancel, 1);
            Grid.SetColumn(create, 2);
            footer.Children.Add(cancel);
            footer.Children.Add(create);
            Grid.SetRow(footer, 6);
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
                    termsBox.Text.Trim(),
                    (deliveryBox.SelectedItem as ComboBoxItem)?.Tag as string ?? SkillDeliverableFormats.None);
                dialog.DialogResult = true;
            };

            dialog.Content = root;
            _ = dialog.ShowDialog();
            return result;
        }

        private static ComboBox AddDeliverySelector(Grid root, int row)
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(new TextBlock
            {
                Text = "Delivers",
                Margin = new Thickness(0, 0, 0, 6),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Brush(p => p.Text)
            });

            var combo = new ComboBox
            {
                Height = 32,
                FontSize = 12,
                ToolTip = "Chat answers stay in the transcript. Canvas Skills return a rendered artifact instead."
            };
            combo.Items.Add(new ComboBoxItem { Content = "A written answer in chat", Tag = SkillDeliverableFormats.None });
            combo.Items.Add(new ComboBoxItem { Content = "A rendered page in Project Canvas (HTML)", Tag = SkillDeliverableFormats.Html });
            combo.Items.Add(new ComboBoxItem { Content = "A vector graphic in Project Canvas (SVG)", Tag = SkillDeliverableFormats.Svg });
            combo.Items.Add(new ComboBoxItem { Content = "A formatted document in Project Canvas (Markdown)", Tag = SkillDeliverableFormats.Markdown });
            combo.SelectedIndex = 0;

            panel.Children.Add(combo);
            Grid.SetRow(panel, row);
            root.Children.Add(panel);
            return combo;
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
                Foreground = AppTheme.Brush(p => p.Text)
            });
            var box = new TextBox
            {
                MinHeight = minHeight,
                Padding = new Thickness(10, 8, 10, 8),
                Background = AppTheme.Brush(p => p.Surface),
                Foreground = AppTheme.Brush(p => p.Text),
                BorderBrush = AppTheme.Brush(p => p.BorderStrong),
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
    }
}
