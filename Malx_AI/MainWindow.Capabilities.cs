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

            CapabilityPanelBuilder.PopulateSkills(
                SkillsListPanel,
                _capabilityRegistry,
                this,
                ShowTransientStatus,
                RefreshSkillsPanel);
            RefreshCapabilityUi();
        }

        private void RefreshPluginsPanel()
        {
            if (PluginsListPanel == null)
                return;

            CapabilityPanelBuilder.PopulatePlugins(
                PluginsListPanel,
                _capabilityRegistry,
                ShowTransientStatus,
                RefreshPluginsPanel);
            RefreshCapabilityUi();
        }

        private void CreateCustomSkill_Click(object sender, RoutedEventArgs e)
        {
            SkillsPopup.IsOpen = false;
            CustomSkillDraft? draft = CapabilityPanelBuilder.ShowCustomSkillDialog(this);
            if (draft == null)
                return;

            try
            {
                AxiomSkillDefinition created = _capabilityRegistry.AddCustomSkill(
                    draft.Name,
                    draft.Description,
                    draft.Instructions,
                    draft.ActivationTerms,
                    draft.DeliverableFormat);
                RefreshCapabilityUi();
                ShowTransientStatus(created.RendersToCanvas
                    ? $"Created and attached custom Skill: {created.Name}. It renders in Project Canvas."
                    : $"Created and attached custom Skill: {created.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Create Skill", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
