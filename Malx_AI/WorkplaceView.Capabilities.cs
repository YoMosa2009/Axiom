using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private readonly AxiomCapabilityRegistry _capabilityRegistry = AxiomCapabilityRegistry.Shared;

        /// <summary>
        /// Attachments are global, so the Workplace shows the same registry Normal Chat does and
        /// a Skill attached in either place is attached in both.
        /// </summary>
        private void InitializeWorkplaceCapabilities()
        {
            try
            {
                _capabilityRegistry.EnsureLoaded();
                RefreshWorkplaceCapabilityUi();
            }
            catch (Exception ex)
            {
                LogActivity($"Skills and Plugins could not be loaded: {ex.Message}");
            }
        }

        /// <summary>Re-reads the shared registry after a change made on another surface.</summary>
        internal void RefreshCapabilityCounts() => RefreshWorkplaceCapabilityUi();

        private void RefreshWorkplaceCapabilityUi()
        {
            _capabilityRegistry.EnsureLoaded();
            int attachedSkills = _capabilityRegistry.Skills.Count(skill => skill.IsAttached);

            if (WorkplaceSkillsCountText != null)
                WorkplaceSkillsCountText.Text = attachedSkills.ToString();
            if (WorkplaceSkillsBadge != null)
                WorkplaceSkillsBadge.Visibility = attachedSkills > 0 ? Visibility.Visible : Visibility.Collapsed;

            if (WorkplaceSkillsSummaryText != null)
                WorkplaceSkillsSummaryText.Text = attachedSkills == 0 ? "No Skills attached" : $"{attachedSkills} attached globally";
        }

        private void WorkplaceOpenSkills_Click(object sender, RoutedEventArgs e)
        {
            RefreshWorkplaceSkillsPanel();
            WorkplaceSkillsPopup.IsOpen = true;
        }

        private void WorkplaceCloseSkills_Click(object sender, RoutedEventArgs e) => WorkplaceSkillsPopup.IsOpen = false;

        private void WorkplaceSkillsPopup_Opened(object? sender, EventArgs e) => AnimateWorkplaceFlyout(WorkplaceSkillsFlyoutCard);

        private static void AnimateWorkplaceFlyout(Border card)
        {
            if (card.RenderTransform is not TranslateTransform translate)
            {
                translate = new TranslateTransform();
                card.RenderTransform = translate;
            }

            card.Opacity = 0;
            translate.Y = 14;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        }

        private void RefreshWorkplaceSkillsPanel()
        {
            if (WorkplaceSkillsListPanel == null)
                return;

            CapabilityPanelBuilder.PopulateSkills(
                WorkplaceSkillsListPanel,
                _capabilityRegistry,
                Window.GetWindow(this) ?? Application.Current.MainWindow,
                LogActivity,
                RefreshWorkplaceSkillsPanel);
            RefreshWorkplaceCapabilityUi();
        }

        private void WorkplaceCreateCustomSkill_Click(object sender, RoutedEventArgs e)
        {
            WorkplaceSkillsPopup.IsOpen = false;
            Window owner = Window.GetWindow(this) ?? Application.Current.MainWindow;
            CustomSkillDraft? draft = CapabilityPanelBuilder.ShowCustomSkillDialog(owner);
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
                RefreshWorkplaceCapabilityUi();
                LogActivity(created.RendersToCanvas
                    ? $"Created and attached custom Skill: {created.Name}. It renders in Project Canvas."
                    : $"Created and attached custom Skill: {created.Name}.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, ex.Message, "Create Skill", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// The Skill, if any, that owns the deliverable for the run currently on screen. Reads the
        /// active run first so a live run wins over the one before it.
        /// </summary>
        private SkillCanvasDirective? ResolveWorkplaceCanvasDirective()
        {
            string prompt = _activeCouncilRunContext?.UserPrompt ?? _lastRunContext?.UserPrompt ?? string.Empty;
            return string.IsNullOrWhiteSpace(prompt) ? null : _capabilityRegistry.ResolveCanvasDirective(prompt);
        }

        /// <summary>
        /// How much artifact authoring the Builder can be trusted with. Cloud Builders always get
        /// the full contract; a local one is measured from its own weights file.
        /// </summary>
        private SkillCanvasTier ResolveBuilderCanvasTier()
        {
            if (_isCloudModeEnabled)
                return SkillCanvasTier.Full;

            string? builderPath = _council.TryGetValue(CouncilRole.Builder, out CouncilModelConfig? config)
                ? config?.ModelPath
                : null;
            return string.IsNullOrWhiteSpace(builderPath)
                ? SkillCanvasTier.Full
                : LocalModelCapabilityProfile.ResolveCanvasTier(LocalModelCapabilityProfile.FromModel(builderPath));
        }
    }
}
