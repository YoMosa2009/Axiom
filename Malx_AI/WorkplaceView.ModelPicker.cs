using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    /// <summary>
    /// One model picker for Workplace: Cloud models, the Hybrid Local server and a local GGUF in a
    /// single list in the header.
    /// </summary>
    /// <remarks>
    /// Owner report (2026-11-26): choosing a model meant a "Cloud Off/On" toggle, a separate cloud
    /// model list, a "Use Hybrid Local" button inside Setup and a "Load Single Model" button in
    /// another card, which was "a whole mess to go through". Every choice is now one item in the
    /// list, and picking it switches the mode as well. Each chat remembers its model
    /// (RestoreSnapshot), so reopening a chat brings its model back with its conversation.
    /// </remarks>
    public partial class WorkplaceView
    {
        private const string PickerHybridTag = "__hybrid__";
        private const string PickerLocalTag = "__local__";
        private const string PickerChooseLocalTag = "__local_pick__";

        private static bool IsPickerSpecialTag(string? tag)
            => tag is PickerHybridTag or PickerLocalTag or PickerChooseLocalTag;

        /// <summary>Adds, updates or removes the Hybrid Local and Local entries of the picker.</summary>
        private void EnsureModelPickerExtraItems(ComboBox combo)
        {
            ComboBoxItem? Find(string tag)
                => combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag as string == tag);

            void Upsert(string tag, bool show, string content, string tooltip)
            {
                ComboBoxItem? item = Find(tag);
                if (!show)
                {
                    if (item != null)
                        combo.Items.Remove(item);
                    return;
                }
                if (item == null)
                {
                    item = new ComboBoxItem { Tag = tag };
                    combo.Items.Add(item);
                }
                item.Content = content;
                item.ToolTip = tooltip;
            }

            bool hasHybrid = _openRouterChatService.HasValidCustomEndpoint || _isHybridLocalCouncilSelected;
            string hybridName = AgentCardLabels.HybridModelDisplayName(_openRouterChatService.CustomEndpointConfiguredModelId);
            Upsert(PickerHybridTag, hasHybrid,
                string.IsNullOrWhiteSpace(hybridName) ? "Hybrid Local" : $"Hybrid Local · {hybridName}",
                "Your own server (set in Settings)");

            bool hasLocal = !string.IsNullOrWhiteSpace(_singleModel.ModelPath);
            Upsert(PickerLocalTag, hasLocal,
                $"Local · {_singleModel.DisplayName}",
                _singleModel.ModelPath ?? "On-device model");

            Upsert(PickerChooseLocalTag, true,
                hasLocal ? "Choose another local model…" : "Choose a local model…",
                "Pick a .gguf file to run on this computer");
        }

        /// <summary>The picker item that matches the current mode, or null for a cloud model.</summary>
        private string? CurrentPickerSpecialTag()
        {
            if (_isCloudModeEnabled && _isHybridLocalCouncilSelected)
                return PickerHybridTag;
            if (!_isCloudModeEnabled)
                return string.IsNullOrWhiteSpace(_singleModel.ModelPath) ? null : PickerLocalTag;
            return null;
        }

        /// <summary>
        /// Handles a Hybrid / Local / Choose entry. Returns true when the selection was one of them.
        /// </summary>
        private bool TryHandleModelPickerSpecialSelection(ComboBox combo, string tag)
        {
            if (!IsPickerSpecialTag(tag))
                return false;

            if (_connectedWorkspace.CodebaseEditAccessEnabled)
            {
                AppendChat("system", "The model is locked while Codebase Edit Access is on in this Workplace chat. Start a new Workplace chat to change it.");
                SyncWorkplaceCloudModelComboSelection(combo);
                return true;
            }

            switch (tag)
            {
                case PickerHybridTag:
                    SelectHybridLocalModel();
                    break;
                case PickerLocalTag:
                    SelectLocalModel();
                    break;
                case PickerChooseLocalTag:
                    if (ChooseLocalModelFile())
                        SelectLocalModel();
                    break;
            }
            SyncWorkplaceCloudModelComboSelection(combo);
            return true;
        }

        private void SelectHybridLocalModel()
        {
            LoadCustomEndpointForWorkplace();
            if (!_openRouterChatService.HasValidCustomEndpoint)
            {
                AppendChat("error", "Hybrid Local needs your server's address in Settings.");
                return;
            }
            if (_isCloudModeEnabled && _isHybridLocalCouncilSelected)
                return;

            _isHybridLocalCouncilSelected = true;
            _isCloudModeEnabled = true;
            _ = RefreshHybridLocalMetadataAsync();
            AfterModelChoiceChanged($"Workplace now uses your Hybrid Local server ({AgentCardLabels.HybridModelDisplayName(_openRouterChatService.CustomEndpointConfiguredModelId)}).");
        }

        private void SelectLocalModel()
        {
            if (string.IsNullOrWhiteSpace(_singleModel.ModelPath))
                return;
            if (!_isCloudModeEnabled)
                return;

            _isCloudModeEnabled = false;
            _isHybridLocalCouncilSelected = false;
            AfterModelChoiceChanged($"Workplace now uses {_singleModel.DisplayName} on this computer.");
        }

        /// <summary>Selects a cloud model from the picker (also turning cloud mode on).</summary>
        private bool SelectCloudModelFromPicker(string normalizedModelId)
        {
            LoadOpenRouterKeyForWorkplace();
            LoadManagedEndpointForWorkplace();
            if (!_openRouterChatService.HasCredentialForModel(normalizedModelId))
            {
                AppendChat("error", _openRouterChatService.IsManagedEndpointModel(normalizedModelId)
                    ? "That model needs an Axiom subscription. Sign in under Settings → Account."
                    : "That model needs an OpenRouter API key in Settings.");
                return false;
            }

            bool modeChanged = !_isCloudModeEnabled || _isHybridLocalCouncilSelected;
            bool modelChanged = !string.Equals(_selectedWorkplaceCloudModelId, normalizedModelId, StringComparison.OrdinalIgnoreCase);
            if (!modeChanged && !modelChanged)
                return true;

            _selectedWorkplaceCloudModelId = normalizedModelId;
            _isCloudModeEnabled = true;
            _isHybridLocalCouncilSelected = false;
            try
            {
                using var database = new DatabaseService();
                database.SaveSetting("openrouter_model_id", _selectedWorkplaceCloudModelId);
            }
            catch
            {
                // The per-chat choice is still saved with the session.
            }
            AfterModelChoiceChanged($"Workplace now uses {_openRouterChatService.ResolveModelLabel(_selectedWorkplaceCloudModelId)}.");
            return true;
        }

        private void AfterModelChoiceChanged(string notice)
        {
            RefreshWorkplaceCloudModeUi();
            UpdateCouncilBlocks();
            UpdateContextInfo();
            RefreshSingleModelModeUi();
            ResetWorkplaceTokenUsageIndicator();
            SavePersistedSession();
            AppendChat("system", notice);
            if (RelayStatusBlock != null)
                RelayStatusBlock.Text = "Ready";
        }

        /// <summary>Asks for a .gguf file and makes it the local model. False when cancelled or invalid.</summary>
        private bool ChooseLocalModelFile()
        {
            var dialog = new OpenFileDialog { Filter = "GGUF models (*.gguf)|*.gguf", Title = "Choose a local model" };
            if (dialog.ShowDialog() != true)
                return false;

            if (IsMmprojFile(dialog.FileName))
            {
                AppendChat("error", "That file is a vision projector (mmproj), not a model. Choose the main .gguf model file.");
                return false;
            }

            string? previousPath = _singleModel.ModelPath;
            _singleModel.ModelPath = dialog.FileName;
            _singleModel.DisplayName = IsQwen3Model(dialog.FileName)
                ? ModelInferenceProfiles.DefaultQwen3DisplayName
                : Path.GetFileNameWithoutExtension(dialog.FileName);
            _singleModel.Format = DetectPromptFormatFromModel(dialog.FileName);

            if (!string.IsNullOrWhiteSpace(previousPath)
                && !string.Equals(previousPath, dialog.FileName, StringComparison.OrdinalIgnoreCase)
                && !_council.Values.Any(config => string.Equals(config.ModelPath, previousPath, StringComparison.OrdinalIgnoreCase))
                && _modelCache.TryGetValue(previousPath, out var cached))
            {
                cached.Dispose();
                _modelCache.Remove(previousPath);
            }

            LogActivity($"Local model chosen: {_singleModel.DisplayName}");
            if (!_isCloudModeEnabled)
            {
                // Already local: the choice itself is the change.
                AfterModelChoiceChanged($"Workplace now uses {_singleModel.DisplayName} on this computer.");
                return false;
            }
            return true;
        }
    }
}
