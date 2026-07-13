using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Data;
using System.Globalization;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using LLama;
using LLama.Common;
using LLama.Sampling;
using LLama.Transformers;
using Malx_AI.Mcp;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private sealed class CloudToolExecutionResult
        {
            public string Name { get; init; } = string.Empty;
            public string Result { get; init; } = string.Empty;
        }

        private sealed class CloudToolCallLoopResult
        {
            public string ResponseText { get; init; } = string.Empty;
            public string ReasoningText { get; init; } = string.Empty;
            public int ToolCallCount { get; init; }
            public OpenRouterTokenUsage? Usage { get; init; }
        }

        private sealed class CloudChatRequestContext
        {
            public string SystemPrompt { get; init; } = string.Empty;
            public string FinalUserMessage { get; init; } = string.Empty;
            public List<ChatMessage> SelectedHistoryMessages { get; init; } = new();
            public List<OpenRouterMessage> ConversationHistory { get; init; } = new();
            public bool ThinkingEnabled { get; init; }
            public List<string> ImageDataUrls { get; init; } = new();
        }

        private const int MaxCloudVisionImagesPerTurn = 4;

        private List<string> BuildCloudImageDataUrls(IReadOnlyList<ChatDocumentAttachment> chatDocuments)
        {
            if (!_openRouterChatService.SupportsImageInput(_selectedOpenRouterModelId))
                return new List<string>();

            return (chatDocuments ?? [])
                .Where(doc => doc.IsImage && !string.IsNullOrWhiteSpace(doc.Base64Data) && !string.IsNullOrWhiteSpace(doc.MimeType))
                .OrderByDescending(doc => doc.ImportedAt)
                .Take(MaxCloudVisionImagesPerTurn)
                .Select(doc => $"data:{doc.MimeType};base64,{doc.Base64Data}")
                .ToList();
        }

        private bool HasVisionAttachmentForCloudTurn()
        {
            return _cloudModeActive && _chatDocuments.Any(doc => doc.IsImage && !string.IsNullOrWhiteSpace(doc.Base64Data));
        }

        private async Task<string> GenerateSingleTurnCloudResponseAsync(string systemPrompt, string userPrompt, CancellationToken token)
        {
            if (!_openRouterChatService.HasValidKey)
                return string.Empty;

            string effectiveSystemPrompt = string.IsNullOrWhiteSpace(systemPrompt)
                ? BuildDefaultAssistantSystemPrompt()
                : systemPrompt.Trim();

            OpenRouterChatResponse response = await _openRouterChatService.SendConversationAsync(
                new List<OpenRouterMessage> { new("user", userPrompt) },
                effectiveSystemPrompt,
                false,
                _selectedOpenRouterModelId,
                null,
                token);

            return response.Text;
        }

        private void LoadOpenRouterSettings()
        {
            string selectedModel = _database?.GetSetting(OpenRouterModelSettingKey) ?? string.Empty;
            _selectedOpenRouterModelId = _openRouterChatService.NormalizeSelectableModelId(selectedModel);

            UpdateOpenRouterDetectedModelUi();
            ResetOpenRouterKeyStatusIndicator();
        }

        private void LoadStoredOpenRouterApiKey()
        {
            try
            {
                string? storedKey = _database?.LoadOpenRouterApiKey();
                if (string.IsNullOrWhiteSpace(storedKey))
                {
                    _openRouterChatService.SetApiKey(string.Empty);
                    _cloudModeActive = false;
                    UpdateOpenRouterDetectedModelUi();
                    return;
                }

                _openRouterChatService.SetApiKey(storedKey);
                _cloudModeActive = _openRouterChatService.HasValidKey;
                if (OpenRouterApiKeyPasswordBox != null)
                    OpenRouterApiKeyPasswordBox.Password = storedKey;

                UpdateOpenRouterDetectedModelUi();
            }
            catch (Exception ex)
            {
                _openRouterChatService.SetApiKey(string.Empty);
                _cloudModeActive = false;
                UpdateOpenRouterDetectedModelUi();
                _ = BackendLogService.LogErrorAsync("MainWindow.LoadStoredOpenRouterApiKey", ex);
            }
        }

        private void LocalModeButton_Click(object sender, RoutedEventArgs e)
        {
            _cloudModeActive = false;
            RefreshCloudModeToggleUi();
            RefreshInferenceSettingsUi();
            UpdateHeaderDisplay();
        }

        private void CloudModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_openRouterChatService.HasValidKey)
            {
                _cloudModeActive = false;
                RefreshCloudModeToggleUi();
                RefreshInferenceSettingsUi();
                UpdateHeaderDisplay();
                return;
            }

            _cloudModeActive = true;
            RefreshCloudModeToggleUi();
            RefreshInferenceSettingsUi();
            UpdateHeaderDisplay();
        }

        private void EidosModelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectCloudModel(OpenRouterChatService.Eidos1ModelId);
        }

        private void HephaModelButton_Click(object sender, RoutedEventArgs e)
        {
            SelectCloudModel(OpenRouterChatService.Hepha1ModelId);
        }

        private void SelectCloudModel(string modelId)
        {
            _selectedOpenRouterModelId = _openRouterChatService.NormalizeSelectableModelId(modelId);
            _database?.SaveSetting(OpenRouterModelSettingKey, _selectedOpenRouterModelId);
            UpdateOpenRouterDetectedModelUi();
            RefreshCloudModeToggleUi();
            RefreshInferenceSettingsUi();
            UpdateHeaderDisplay();

            string label = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
            ShowTransientStatus($"Cloud model set to {label}.");
        }

        private void OpenRouterApiKeyPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (!_isTestingOpenRouterKey)
                ResetOpenRouterKeyStatusIndicator();
        }

        private void OpenRouterUsageTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateOpenRouterUsageProgressBar();
        }

        private async void SaveOpenRouterKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (SaveOpenRouterKeyButton == null)
                return;

            string apiKey = OpenRouterApiKeyPasswordBox?.Password ?? string.Empty;
            _database?.SaveOpenRouterApiKey(apiKey);
            _openRouterChatService.SetApiKey(apiKey);
            RefreshCloudModeToggleUi();
            UpdateOpenRouterDetectedModelUi();

            _isTestingOpenRouterKey = true;
            SaveOpenRouterKeyButton.IsEnabled = false;
            if (OpenRouterKeyTestingText != null)
                OpenRouterKeyTestingText.Visibility = Visibility.Visible;
            if (OpenRouterKeyStatusText != null)
                OpenRouterKeyStatusText.Visibility = Visibility.Collapsed;

            bool isValid = false;
            try
            {
                // TestConnectionAsync validates the key and fetches available models — no extra probe needed.
                // ValidateModelAvailabilityAsync was removed: it sent a full chat completion request that
                // could take 30-60 seconds and caused the wrong model to be stored as the detected model.
                isValid = await _openRouterChatService.TestConnectionAsync();
            }
            finally
            {
                _isTestingOpenRouterKey = false;
                SaveOpenRouterKeyButton.IsEnabled = true;
                if (OpenRouterKeyTestingText != null)
                    OpenRouterKeyTestingText.Visibility = Visibility.Collapsed;
            }

            UpdateOpenRouterDetectedModelUi();
            SetOpenRouterKeyValidationStatus(isValid);
            if (!isValid)
                _cloudModeActive = false;
            else
            {
                _cloudModeActive = true;
                _selectedOpenRouterModelId = _openRouterChatService.NormalizeSelectableModelId(_selectedOpenRouterModelId);
                _database?.SaveSetting(OpenRouterModelSettingKey, _selectedOpenRouterModelId);
            }

            RefreshCloudModeToggleUi();
            UpdateHeaderDisplay();

            if (isValid)
                await LoadOpenRouterUsageAsync();
            else
                ResetOpenRouterUsageUi();
        }

        private void ClearOpenRouterKeyButton_Click(object sender, RoutedEventArgs e)
        {
            if (OpenRouterApiKeyPasswordBox != null)
                OpenRouterApiKeyPasswordBox.Password = string.Empty;

            _database?.SaveOpenRouterApiKey(string.Empty);
            _openRouterChatService.SetApiKey(string.Empty);
            _cloudModeActive = false;
            _selectedOpenRouterModelId = OpenRouterChatService.DefaultModelId;
            _database?.SaveSetting(OpenRouterModelSettingKey, _selectedOpenRouterModelId);
            ResetOpenRouterKeyStatusIndicator();
            ResetOpenRouterUsageUi();
            UpdateOpenRouterDetectedModelUi();
            RefreshCloudModeToggleUi();
            UpdateHeaderDisplay();
        }

        private async void RefreshOpenRouterUsageButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadOpenRouterUsageAsync();
        }

        private void RefreshCloudModeToggleUi()
        {
            _localModeButton ??= FindName("LocalModeButton") as Button;
            _cloudModeButton ??= FindName("CloudModeButton") as Button;
            _eidosModelButton ??= FindName("EidosModelButton") as Button;
            _hephaModelButton ??= FindName("HephaModelButton") as Button;

            bool hasValidKey = _openRouterChatService.HasValidKey;
            if (!hasValidKey)
                _cloudModeActive = false;

            // Show cloud model selector (Eidos / Hepha) only when in cloud mode
            if (FindName("CloudModelSelectorBorder") is Border cloudSelectorBorder)
                cloudSelectorBorder.Visibility = _cloudModeActive ? Visibility.Visible : Visibility.Collapsed;

            if (_localModeButton != null)
            {
                bool isSelected = !_cloudModeActive;
                _localModeButton.Background = AppBrushCache.Get(isSelected ? "#B8924A" : "Transparent");
                _localModeButton.Foreground = AppBrushCache.Get(isSelected ? "#EDE8E3" : "#8A8279");
                _localModeButton.BorderBrush = Brushes.Transparent;
                _localModeButton.BorderThickness = new Thickness(0);
            }

            if (_cloudModeButton != null)
            {
                _cloudModeButton.IsEnabled = hasValidKey;
                _cloudModeButton.ToolTip = hasValidKey
                    ? "Use OpenRouter cloud inference"
                    : "Add an OpenRouter API key in Settings to enable cloud mode";
                _cloudModeButton.Background = AppBrushCache.Get(_cloudModeActive ? "#B8924A" : "Transparent");
                _cloudModeButton.Foreground = AppBrushCache.Get(_cloudModeActive ? "#EDE8E3" : "#8A8279");
                _cloudModeButton.BorderBrush = Brushes.Transparent;
                _cloudModeButton.BorderThickness = new Thickness(0);
            }

            RefreshCloudModelSelectionUi(_eidosModelButton, OpenRouterChatService.Eidos1ModelId, hasValidKey);
            RefreshCloudModelSelectionUi(_hephaModelButton, OpenRouterChatService.Hepha1ModelId, hasValidKey);
            RefreshInferenceSettingsUi();

            UpdateHeaderDisplay();
            UpdateUIState(!_isProcessing);
            UpdateTokenUsageIndicator();
            UpdateOpenRouterUsageVisibility();
            RefreshMcpConnectorsUi();
            UpdateInputMcpMentionHighlight();
            if (!_cloudModeActive)
                CloseMcpMentionPopup();
        }

        private void RefreshCloudModelSelectionUi(Button? button, string modelId, bool hasValidKey)
        {
            if (button == null)
                return;

            bool isSelected = string.Equals(_selectedOpenRouterModelId, modelId, StringComparison.OrdinalIgnoreCase);
            bool isAvailable = hasValidKey && _openRouterChatService.IsSelectableModelAvailable(modelId);
            button.IsEnabled = hasValidKey;
            button.Background = AppBrushCache.Get(isSelected ? "#B8924A" : "Transparent");
            button.Foreground = AppBrushCache.Get(isSelected ? "#EDE8E3" : "#8A8279");
            button.BorderBrush = Brushes.Transparent;
            button.BorderThickness = new Thickness(0);
            button.Opacity = isAvailable || !hasValidKey ? 1.0 : 0.45;
            button.ToolTip = !hasValidKey
                ? "Add an OpenRouter API key in Settings to enable cloud models"
                : isAvailable
                    ? _openRouterChatService.DescribeModelSelection(modelId)
                    : $"{_openRouterChatService.ResolveModelLabel(modelId)} is not available on this OpenRouter key.";
        }

        private void ResetOpenRouterKeyStatusIndicator()
        {
            if (OpenRouterKeyTestingText != null)
                OpenRouterKeyTestingText.Visibility = Visibility.Collapsed;

            if (OpenRouterKeyStatusText != null)
            {
                OpenRouterKeyStatusText.Text = string.Empty;
                OpenRouterKeyStatusText.Visibility = Visibility.Collapsed;
                OpenRouterKeyStatusText.Foreground = AppBrushCache.Get("#8A8279");
            }
        }

        private async Task EnsureOpenRouterUsageLoadedAsync()
        {
            TextBlock? usageTextBlock = FindName("OpenRouterUsageText") as TextBlock;

            if (!_openRouterChatService.HasValidKey)
            {
                ResetOpenRouterUsageUi();
                return;
            }

            if (_isFetchingOpenRouterUsage)
                return;

            string currentText = usageTextBlock?.Text ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(currentText)
                && !string.Equals(currentText, "No usage data yet", StringComparison.Ordinal)
                && !string.Equals(currentText, "Unable to fetch usage. Check the key.", StringComparison.Ordinal))
            {
                UpdateOpenRouterUsageVisibility();
                return;
            }

            await LoadOpenRouterUsageAsync();
        }

        private async Task LoadOpenRouterUsageAsync()
        {
            TextBlock? usageTextBlock = FindName("OpenRouterUsageText") as TextBlock;
            Button? refreshButton = FindName("RefreshOpenRouterUsageButton") as Button;
            Border? usageFillBar = FindName("OpenRouterUsageFillBar") as Border;

            if (!_openRouterChatService.HasValidKey)
            {
                ResetOpenRouterUsageUi();
                return;
            }

            if (_isFetchingOpenRouterUsage)
                return;

            _isFetchingOpenRouterUsage = true;
            UpdateOpenRouterUsageVisibility();

            if (usageTextBlock != null)
                usageTextBlock.Text = "Fetching usage";

            if (refreshButton != null)
                refreshButton.IsEnabled = false;

            try
            {
                OpenRouterKeyInfo keyInfo = await _openRouterChatService.FetchKeyInfoAsync();
                if (!keyInfo.FetchSucceeded)
                {
                    SetOpenRouterUsageFailureState();
                    return;
                }

                if (keyInfo.IsUnlimited)
                {
                    _openRouterUsagePercent = 0;
                    if (usageTextBlock != null)
                        usageTextBlock.Text = "Free tier — no request cap on this key";
                    if (usageFillBar != null)
                        usageFillBar.Background = GetOpenRouterUsageBrush(0);
                    SetOpenRouterUsageHint(string.Empty);
                    UpdateOpenRouterUsageProgressBar();
                    return;
                }

                int used = Math.Max(0, keyInfo.RequestsUsed);
                int limit = Math.Max(1, keyInfo.RequestsLimit);
                int remaining = Math.Max(0, limit - used);
                _openRouterUsagePercent = Math.Clamp(used * 100d / limit, 0d, 100d);

                if (usageTextBlock != null)
                    usageTextBlock.Text = $"{used} / {limit} used  ·  {remaining} left";

                if (usageFillBar != null)
                    usageFillBar.Background = GetOpenRouterUsageBrush(_openRouterUsagePercent);

                SetOpenRouterUsageHint(remaining == 0
                    ? "⚠ Daily quota exhausted — cloud requests will fail until it resets (midnight UTC)."
                    : _openRouterUsagePercent >= 85d
                        ? "Running low on daily requests."
                        : string.Empty);

                UpdateOpenRouterUsageProgressBar();
            }
            finally
            {
                _isFetchingOpenRouterUsage = false;
                if (refreshButton != null)
                    refreshButton.IsEnabled = true;
                UpdateOpenRouterUsageVisibility();
            }
        }

        private void SetOpenRouterUsageHint(string hint)
        {
            if (FindName("OpenRouterUsageHintText") is not TextBlock hintText)
                return;

            hintText.Text = hint ?? string.Empty;
            hintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
            hintText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(
                hint != null && hint.StartsWith("⚠", StringComparison.Ordinal) ? "#FF6B6B" : "#B0A89F"));
        }

        private void OpenRouterChatService_TokenUsageRecorded(OpenRouterTokenUsage usage)
        {
            if (!_openRouterUsageRefreshThrottle.TryBegin(DateTime.UtcNow, _openRouterChatService.HasValidKey))
                return;

            _ = RefreshOpenRouterUsageAfterResponseAsync();
        }

        private async Task RefreshOpenRouterUsageAfterResponseAsync()
        {
            try
            {
                if (!_openRouterChatService.HasValidKey)
                    return;

                if (Dispatcher.CheckAccess())
                {
                    await LoadOpenRouterUsageAsync();
                }
                else
                {
                    await Dispatcher.InvokeAsync(LoadOpenRouterUsageAsync).Task.Unwrap();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OpenRouter usage auto-refresh error: {ex.Message}");
            }
            finally
            {
                _openRouterUsageRefreshThrottle.Complete();
            }
        }

        private void ResetOpenRouterUsageUi()
        {
            TextBlock? usageTextBlock = FindName("OpenRouterUsageText") as TextBlock;
            Border? usageFillBar = FindName("OpenRouterUsageFillBar") as Border;
            Button? refreshButton = FindName("RefreshOpenRouterUsageButton") as Button;

            _isFetchingOpenRouterUsage = false;
            _openRouterUsagePercent = 0;

            if (usageTextBlock != null)
                usageTextBlock.Text = "No usage data yet";

            if (usageFillBar != null)
                usageFillBar.Background = GetOpenRouterUsageBrush(0);

            if (refreshButton != null)
                refreshButton.IsEnabled = true;

            SetOpenRouterUsageHint(string.Empty);
            UpdateOpenRouterUsageProgressBar();
            UpdateOpenRouterUsageVisibility();
        }

        private void SetOpenRouterUsageFailureState()
        {
            TextBlock? usageTextBlock = FindName("OpenRouterUsageText") as TextBlock;
            Border? usageFillBar = FindName("OpenRouterUsageFillBar") as Border;

            _openRouterUsagePercent = 0;

            if (usageTextBlock != null)
                usageTextBlock.Text = "Unable to fetch usage. Check the key.";

            if (usageFillBar != null)
                usageFillBar.Background = GetOpenRouterUsageBrush(0);

            UpdateOpenRouterUsageProgressBar();
        }

        private void UpdateOpenRouterUsageVisibility()
        {
            StackPanel? usagePanel = FindName("OpenRouterUsagePanel") as StackPanel;
            if (usagePanel == null)
                return;

            usagePanel.Visibility = _openRouterChatService.HasValidKey
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void UpdateOpenRouterUsageProgressBar()
        {
            Border? usageFillBar = FindName("OpenRouterUsageFillBar") as Border;
            Border? usageTrack = FindName("OpenRouterUsageTrack") as Border;
            if (usageFillBar == null || usageTrack == null)
                return;

            // The fill sits inside the pill's 1px border with a 1px margin on each side.
            double trackWidth = Math.Max(0, usageTrack.ActualWidth - 4);
            usageFillBar.Width = trackWidth <= 0
                ? 0
                : trackWidth * (_openRouterUsagePercent / 100d);
        }

        private static Brush GetOpenRouterUsageBrush(double percent)
        {
            string color = percent switch
            {
                >= 85d => "#FF3B3B",
                >= 60d => "#F97316",
                _ => "#22C55E"
            };

            return AppBrushCache.Get(color);
        }

        private void SetOpenRouterKeyValidationStatus(bool isValid)
        {
            if (OpenRouterKeyStatusText == null)
                return;

            OpenRouterKeyStatusText.Text = isValid
                ? "Valid"
                : _openRouterChatService.LastTestFailureReason switch
                {
                    OpenRouterConnectionTestFailureReason.InvalidKey => "Invalid key",
                    OpenRouterConnectionTestFailureReason.RequestFormatError => "Request format error, check logs",
                    OpenRouterConnectionTestFailureReason.RateLimited => "Rate limited, try again shortly",
                    OpenRouterConnectionTestFailureReason.ProviderUnavailable => "Provider/model temporarily unavailable, retrying or switch models",
                    OpenRouterConnectionTestFailureReason.NetworkError => "Connection failed, check your network",
                    _ => "Test failed, check logs"
                };
            OpenRouterKeyStatusText.Foreground = AppBrushCache.Get(isValid ? "#22C55E" : "#FF3B3B");
            OpenRouterKeyStatusText.Visibility = Visibility.Visible;
        }

        private void UpdateOpenRouterDetectedModelUi()
        {
            string selectedLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
            if (OpenRouterDetectedModelText != null)
                OpenRouterDetectedModelText.Text = selectedLabel;

            if (OpenRouterDetectedModelIdText != null)
                OpenRouterDetectedModelIdText.Text = _openRouterChatService.DescribeModelSelection(_selectedOpenRouterModelId);
        }

        private string BuildCloudIdentitySystemInstruction()
        {
            string selectedLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
            return $"[AXIOM IDENTITY] You are the {selectedLabel} cloud AI profile inside the Axiom app. If the user asks what AI you are, say you are {selectedLabel} in Axiom. If the user asks who made you or what app this is, say you are being provided through the Axiom app. Keep identity answers short, factual, and consistent with the selected Axiom profile name.";
        }

        private List<OpenRouterMessage> BuildOpenRouterConversationHistory(List<ChatMessage> selectedHistoryMessages)
        {
            var history = new List<OpenRouterMessage>();
            int tokenBudget = CloudHistoryTokenBudget;

            foreach (ChatMessage message in selectedHistoryMessages ?? [])
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Content))
                    continue;

                int messageTokens = _openRouterChatService.EstimateTokenCountForBudget(message.Content);
                if (history.Count > 0 && tokenBudget - messageTokens < 0)
                    continue;

                if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                {
                    history.Add(new OpenRouterMessage("user", message.Content));
                    tokenBudget -= messageTokens;
                }
                else if (string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                {
                    string cleanedAssistant = CleanAssistantContentForOpenRouterHistory(message.Content);
                    if (!string.IsNullOrWhiteSpace(cleanedAssistant))
                    {
                        history.Add(new OpenRouterMessage("assistant", cleanedAssistant));
                        tokenBudget -= _openRouterChatService.EstimateTokenCountForBudget(cleanedAssistant);
                    }
                }
            }

            return history;
        }

        private static string CleanCloudReasoningForDisplay(string reasoning)
        {
            if (string.IsNullOrWhiteSpace(reasoning))
                return string.Empty;

            string cleaned = CleanOutputTokens(reasoning);
            cleaned = StripThinkBlocksAndLeadingBlankLines(cleaned);
            cleaned = Regex.Replace(cleaned, @"(?:\r?\n){3,}", Environment.NewLine + Environment.NewLine);
            return cleaned.Trim();
        }

        private static string CleanAssistantContentForOpenRouterHistory(string content)
        {
            string cleaned = SanitizeAssistantContentForInference(content);
            if (string.IsNullOrWhiteSpace(cleaned))
                return string.Empty;

            cleaned = CloudHistoryBracketedMetadataBlockRegex.Replace(cleaned, string.Empty);
            cleaned = CloudHistoryPlainMetadataBlockRegex.Replace(cleaned, string.Empty);
            cleaned = Regex.Replace(cleaned, @"(?:\r?\n){3,}", Environment.NewLine + Environment.NewLine);
            return cleaned.Trim();
        }

        private string BuildCloudModelSystemInstruction(string userMsg)
        {
            string selectedLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);

            if (string.Equals(_selectedOpenRouterModelId, OpenRouterChatService.Hepha1ModelId, StringComparison.OrdinalIgnoreCase))
            {
                return "[MODEL PROFILE: HEPHA 1] You are operating in Axiom's Hepha 1 cloud profile. Prioritize code correctness, deterministic tool usage, compact structured outputs, and environment-aware solutions that work cleanly with the app's Python sandbox, web retrieval, imported documents, and persisted chat state. When tool-produced context is present, treat it as authoritative and integrate it directly without re-describing internal plumbing. Use the larger cloud context window to connect the latest user message with relevant prior turns before choosing tools or answering.";
            }

            string taskBias = IsCodingRequest(userMsg)
                ? "When coding is requested, stay implementation-first and keep code directly usable."
                : "Favor strong reasoning, concise execution-ready answers, and efficient use of supplied context blocks.";

            return $"[MODEL PROFILE: {selectedLabel.ToUpperInvariant()}] You are operating in Axiom's {selectedLabel} cloud profile. Optimize for the app's backend pipeline: use tool outputs, imported document context, web-grounding blocks, persona memory, and calculator/python results as high-priority signals. Use the larger cloud context window to connect the latest user message with relevant prior turns before choosing tools or answering. {taskBias} Do not expose internal chain-of-thought unless explicitly requested by the product behavior.";
        }

        private string BuildCloudWebSearchSystemInstruction(string userMsg, bool proactiveWebContextAttached)
        {
            string webAvailability = proactiveWebContextAttached
                ? "A proactive web evidence block is already attached for this turn."
                : _normalWebSearchEnabled || ContainsExplicitWebSearchRequest(userMsg)
                    ? "Web Search is enabled or explicitly requested for this turn."
                    : "The web_search tool is available for cases where current or source-backed information is needed.";

            return "[CLOUD WEB SEARCH BEHAVIOR]\n" +
                webAvailability + "\n" +
                "Before using web_search, resolve the user's latest message against the conversation history so the query is standalone. Replace references like 'the movie', 'that model', 'this article', pronouns, or character names with the actual title, product, person, organization, document, or topic from prior turns when the conversation provides it.\n" +
                "Use web_search for current, obscure, source-backed, documentation, policy, release, pricing, legal, medical, financial, or specific factual claims that may not be reliable from model memory. Stable background context may come from general knowledge when it is not contradicted by source evidence.\n" +
                "For multi-part or ambiguous requests, prefer one focused query that preserves the relation the user asked about. If the first result is off-topic, misses the named entity, or answers only part of the relation, call one narrower follow-up query before final synthesis.\n" +
                "After web_search, synthesize the evidence into a direct answer instead of behaving like a search-results page. Cite source titles, hosts, URLs, or dates naturally for current/source-backed claims, and explicitly separate confirmed facts from gaps the sources did not cover.";
        }

        private static string TrimForInlineError(string message, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "Cloud request failed.";

            string normalized = message.Trim();
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength];
        }

        private IReadOnlyList<OpenRouterToolDefinition> BuildCloudToolDefinitions(IReadOnlyCollection<string>? mentionedMcpHandles = null)
        {
            var tools = new List<OpenRouterToolDefinition>
            {
                new OpenRouterToolDefinition(
                    "web_search",
                    "Search the web for relevant evidence, definitions, explanations, comparisons, documentation, or current information, then return grounded snippets. Use this for source-backed facts and when conversation context is needed to answer correctly. Include relevant conversation context in the query: resolve pronouns and phrases like 'the movie', 'that model', or 'this article' to the actual title, product, person, or topic from prior turns.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["query"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "A concise standalone search query focused on the user's actual question, preserving the relation being asked about and including any prior-turn entity needed to disambiguate it."
                            }
                        },
                        ["required"] = new JsonArray("query"),
                        ["additionalProperties"] = false
                    }),
                new OpenRouterToolDefinition(
                    "run_python",
                    "Execute Python code in the existing sandbox and return stdout/stderr.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["code"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The Python code to execute."
                            }
                        },
                        ["required"] = new JsonArray("code"),
                        ["additionalProperties"] = false
                    }),
                new OpenRouterToolDefinition(
                    "calculate",
                    "Evaluate a math or unit conversion expression with the existing calculator.",
                    new JsonObject
                    {
                        ["type"] = "object",
                        ["properties"] = new JsonObject
                        {
                            ["expression"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["description"] = "The expression or conversion to evaluate."
                            }
                        },
                        ["required"] = new JsonArray("expression"),
                        ["additionalProperties"] = false
                    })
            };

            // MCP / Cloud Connectors: only when Cloud Mode is active (this method is cloud-only)
            // and at least one connector is connected. @mentions focus those connectors in the system
            // prompt; tools for all connected connectors remain available for seamless use.
            if (_mcpConnectorService != null && _cloudModeActive)
            {
                foreach (McpToolDefinition mcpTool in _mcpConnectorService.GetActiveTools(mentionedHandles: null))
                {
                    tools.Add(new OpenRouterToolDefinition(
                        mcpTool.Name,
                        mcpTool.Description,
                        mcpTool.ParametersSchema));
                }

                // If the user @mentioned disconnected connectors only, still no MCP tools — fine.
                _ = mentionedMcpHandles;
            }

            return tools;
        }

        private async Task<CloudToolCallLoopResult> RunCloudToolCallingLoopAsync(
            string userMsg,
            string systemPrompt,
            List<OpenRouterMessage> conversationHistory,
            bool thinkingEnabled,
            Action<string>? onToken,
            CancellationToken token,
            IReadOnlyList<string>? imageDataUrls = null)
        {
            List<OpenRouterMessage> messages = new(conversationHistory ?? []);
            // PreserveFullText: keeps the active user message (which may carry attached document
            // context) intact through tool-loop iterations, where it stops being the final message.
            // ImageDataUrls: attached images ride the same payload message as multipart content.
            messages.Add(new OpenRouterMessage("user", userMsg, PreserveFullText: true, ImageDataUrls: imageDataUrls));

            IReadOnlyList<string> mentionedMcpHandles = ResolveMentionedMcpHandles(userMsg);
            IReadOnlyList<OpenRouterToolDefinition> tools = BuildCloudToolDefinitions(mentionedMcpHandles);
            var reasoningParts = new List<string>();
            int toolCallCount = 0;
            bool pythonSessionStarted = false;

            try
            {
                for (int iteration = 0; iteration < CloudToolLoopIterationLimit; iteration++)
                {
                    OpenRouterChatResponse response = await _openRouterChatService.SendConversationStreamAsync(
                        messages,
                        systemPrompt,
                        thinkingEnabled,
                        _selectedOpenRouterModelId,
                        tools,
                        onToken,
                        token);

                    if (!string.IsNullOrWhiteSpace(response.Reasoning))
                        reasoningParts.Add(response.Reasoning.Trim());

                    if (response.ToolCalls == null || response.ToolCalls.Count == 0)
                    {
                        return new CloudToolCallLoopResult
                        {
                            ResponseText = response.Text,
                            ReasoningText = string.Join("\n\n", reasoningParts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal)),
                            ToolCallCount = toolCallCount,
                            Usage = response.Usage
                        };
                    }

                    toolCallCount += response.ToolCalls.Count;
                    messages.Add(new OpenRouterMessage("assistant", response.Text ?? string.Empty, null, response.ToolCalls));

                    foreach (OpenRouterToolCall toolCall in response.ToolCalls)
                    {
                        if (string.Equals(toolCall?.Name, "run_python", StringComparison.OrdinalIgnoreCase) && !pythonSessionStarted)
                        {
                            await _pythonExecutionService.StartPersistentSessionAsync(token);
                            pythonSessionStarted = true;
                        }

                        CloudToolExecutionResult executionResult = await ExecuteCloudToolCallAsync(toolCall, userMsg, messages, token);
                        string boundedToolResult = BuildCloudToolResultMessage(executionResult.Result, toolCall.Name);
                        messages.Add(new OpenRouterMessage("tool", boundedToolResult, toolCall.Id));
                    }
                }

                // Iteration budget exhausted. Instead of discarding the turn (and every tool result
                // gathered so far), run one final pass with tools disabled so the model must
                // synthesize an answer from the evidence already in the conversation.
                await BackendLogService.LogEventAsync(
                    "CloudToolLoopBudgetExhausted",
                    $"ToolCalls:{toolCallCount}\nIterationLimit:{CloudToolLoopIterationLimit}\nForcing final no-tools synthesis pass.");

                OpenRouterChatResponse finalResponse = await _openRouterChatService.SendConversationStreamAsync(
                    messages,
                    systemPrompt,
                    thinkingEnabled,
                    _selectedOpenRouterModelId,
                    null,
                    onToken,
                    token);

                if (!string.IsNullOrWhiteSpace(finalResponse.Reasoning))
                    reasoningParts.Add(finalResponse.Reasoning.Trim());

                return new CloudToolCallLoopResult
                {
                    ResponseText = finalResponse.Text,
                    ReasoningText = string.Join("\n\n", reasoningParts.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal)),
                    ToolCallCount = toolCallCount,
                    Usage = finalResponse.Usage
                };
            }
            finally
            {
                if (pythonSessionStarted)
                    await _pythonExecutionService.EndPersistentSessionAsync(CancellationToken.None);
            }
        }

        private static bool HasCloudWebSearchEvidence(string text)
        {
            return !string.IsNullOrWhiteSpace(text)
                && text.Contains("[[WEB SEARCH DATA]]", StringComparison.OrdinalIgnoreCase)
                && text.Contains("[[END WEB SEARCH DATA]]", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<ConversationSearchTurn> BuildCloudToolSearchTurns(IReadOnlyList<OpenRouterMessage>? messages)
        {
            if (messages == null || messages.Count == 0)
                return [];

            return messages
                .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                .Where(m => !string.IsNullOrWhiteSpace(m.Text))
                .TakeLast(14)
                .Select(m => new ConversationSearchTurn(m.Role, m.Text))
                .ToList();
        }

        private static string BuildCloudToolResultMessage(string toolResult, string toolName)
        {
            string normalized = (toolResult ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(normalized))
                return "Tool returned no output. If needed, summarize that no evidence or result was produced.";

            bool isWebSearch = string.Equals(toolName?.Trim(), "web_search", StringComparison.OrdinalIgnoreCase);
            if (isWebSearch && HasCloudWebSearchEvidence(normalized))
            {
                normalized =
                    "[WEB TOOL RESULT RULE]\n" +
                    "Use this web_search result as source evidence only for claims it directly supports. Verify that the evidence covers the user's named entities and the relation they asked about. If it is off-topic or incomplete and another tool call is still available, call one narrower standalone web_search query; otherwise answer the supported portion and state the exact evidence gap. Synthesize the sources into a direct answer instead of listing raw snippets.\n\n" +
                    normalized;
            }

            if (normalized.Length <= CloudToolResultCharacterLimit)
                return normalized;

            string displayName = string.IsNullOrWhiteSpace(toolName) ? "tool" : toolName.Trim();
            string truncated = normalized[..CloudToolResultCharacterLimit].TrimEnd();
            return truncated + $"\n\n[TOOL RESULT TRUNCATED] The {displayName} output exceeded {CloudToolResultCharacterLimit} characters. Use the retained portion above as the primary evidence and synthesize from it instead of asking the tool to repeat the same large output unless a narrower follow-up query is required.";
        }

        private async Task<CloudToolExecutionResult> ExecuteCloudToolCallAsync(OpenRouterToolCall toolCall, string originalUserMessage, IReadOnlyList<OpenRouterMessage> conversationMessages, CancellationToken token)
        {
            if (toolCall == null || string.IsNullOrWhiteSpace(toolCall.Name))
                return new CloudToolExecutionResult { Result = "Tool call was empty." };

            string normalizedName = toolCall.Name.Trim();

            try
            {
                using JsonDocument argsDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(toolCall.ArgumentsJson) ? "{}" : toolCall.ArgumentsJson);
                JsonElement root = argsDocument.RootElement;

                if (string.Equals(normalizedName, "web_search", StringComparison.OrdinalIgnoreCase))
                {
                    StartToolActivityIndicator("Searching the web");
                    string query = root.TryGetProperty("query", out JsonElement queryElement) ? queryElement.GetString() ?? string.Empty : string.Empty;
                    string contextualQuery = ConversationSearchContext.BuildContextualSearchPrompt(
                        query,
                        BuildCloudToolSearchTurns(conversationMessages));
                    if (string.IsNullOrWhiteSpace(contextualQuery))
                    {
                        contextualQuery = ConversationSearchContext.BuildContextualSearchPrompt(
                            query,
                            new[] { new ConversationSearchTurn("user", originalUserMessage) });
                    }

                    string finalQuery = string.IsNullOrWhiteSpace(contextualQuery) ? query : contextualQuery;
                    await BackendLogService.LogEventAsync("MainWindow.CloudWebSearch", $"ToolQuery:{query}\nContextualQuery:{finalQuery}");
                    string data = await _webSearchService.SearchTopSnippetsForNormalChatAsync(
                        finalQuery,
                        token);
                    return new CloudToolExecutionResult
                    {
                        Name = normalizedName,
                        Result = string.IsNullOrWhiteSpace(data) ? "No web results were found." : data
                    };
                }

                if (string.Equals(normalizedName, "run_python", StringComparison.OrdinalIgnoreCase))
                {
                    StartToolActivityIndicator("Running Python");
                    string code = root.TryGetProperty("code", out JsonElement codeElement) ? codeElement.GetString() ?? string.Empty : string.Empty;
                    PythonCodeExecutionOutcome outcome = await ExecuteAndRepairPythonCodeAsync(code, string.Empty, originalUserMessage, token, true);
                    return new CloudToolExecutionResult
                    {
                        Name = normalizedName,
                        Result = string.IsNullOrWhiteSpace(outcome.ExecutionResult) ? "Python completed with no output." : outcome.ExecutionResult
                    };
                }

                if (string.Equals(normalizedName, "calculate", StringComparison.OrdinalIgnoreCase))
                {
                    StartToolActivityIndicator("Calculating");
                    string expression = root.TryGetProperty("expression", out JsonElement expressionElement) ? expressionElement.GetString() ?? string.Empty : string.Empty;
                    if (!CalculatorToolAgent.TryEvaluateExpression(expression, out string resultText))
                        resultText = "Calculator could not evaluate the requested expression.";

                    return new CloudToolExecutionResult
                    {
                        Name = normalizedName,
                        Result = resultText
                    };
                }

                if (_mcpConnectorService != null
                    && _cloudModeActive
                    && _mcpConnectorService.TryResolveTool(normalizedName, out McpConnectorInfo? mcpConnector)
                    && mcpConnector != null)
                {
                    string activity = string.Equals(normalizedName, "gmail_send", StringComparison.OrdinalIgnoreCase)
                        ? "Sending email"
                        : string.Equals(normalizedName, "gmail_create_draft", StringComparison.OrdinalIgnoreCase)
                            ? "Creating Gmail draft"
                            : normalizedName.StartsWith("github_", StringComparison.OrdinalIgnoreCase)
                                ? "GitHub"
                                : normalizedName.StartsWith("todoist_", StringComparison.OrdinalIgnoreCase)
                                    ? "Todoist"
                                    : mcpConnector.DisplayName;
                    StartToolActivityIndicator(activity);
                    McpToolExecutionResult mcpResult = await _mcpConnectorService
                        .ExecuteToolAsync(normalizedName, toolCall.ArgumentsJson ?? "{}", token)
                        .ConfigureAwait(false);
                    return new CloudToolExecutionResult
                    {
                        Name = normalizedName,
                        Result = mcpResult.Result
                    };
                }

                return new CloudToolExecutionResult
                {
                    Name = normalizedName,
                    Result = $"Unsupported tool: {normalizedName}"
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new CloudToolExecutionResult
                {
                    Name = normalizedName,
                    Result = $"Tool execution failed: {ex.Message}"
                };
            }
            finally
            {
                StopToolActivityIndicator();
            }
        }

        private async Task HandleCloudChatRequestAsync(string userMsg, CancellationToken token)
        {
            if (!_openRouterChatService.HasValidKey)
                throw new InvalidOperationException("Add a valid OpenRouter API key in Settings to enable cloud mode.");

            CloudChatRequestContext requestContext = await PrepareCloudChatRequestContextAsync(userMsg, token).ConfigureAwait(false);
            var streamedResponseBuilder = new StringBuilder();
            bool firstTokenReceived = false;
            var streamUiThrottle = Stopwatch.StartNew();
            long lastStreamUiUpdateMs = long.MinValue;
            const int streamUiUpdateIntervalMs = 80;

            await Dispatcher.InvokeAsync(() =>
            {
                _currentStreamingMessage = new ChatMessage("assistant", string.Empty)
                {
                    ModelLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId),
                    PreferPlainTextRendering = false
                };
                _currentStreamingMessage.IsThinkingInProgress = true;
                _currentStreamingMessage.IsStreaming = true;
                _chatMessages.Add(_currentStreamingMessage);
            }, System.Windows.Threading.DispatcherPriority.Background);

            try
            {
                DateTime requestStartUtc = DateTime.UtcNow;

                CloudToolCallLoopResult toolLoopResult = await RunCloudToolCallingLoopAsync(
                    requestContext.FinalUserMessage,
                    requestContext.SystemPrompt,
                    requestContext.ConversationHistory,
                    requestContext.ThinkingEnabled,
                    imageDataUrls: requestContext.ImageDataUrls,
                    onToken: tokenChunk =>
                    {
                        if (string.IsNullOrEmpty(tokenChunk))
                            return;

                        lock (streamedResponseBuilder)
                        {
                            streamedResponseBuilder.Append(tokenChunk);
                        }

                        // Throttle UI refreshes: re-rendering the full accumulated text on every
                        // delta is quadratic on long answers and floods the dispatcher queue. The
                        // first token always renders immediately (it clears the thinking state),
                        // and the finalize step renders the complete text at the end.
                        long elapsedMs = streamUiThrottle.ElapsedMilliseconds;
                        if (firstTokenReceived && elapsedMs - lastStreamUiUpdateMs < streamUiUpdateIntervalMs)
                            return;
                        lastStreamUiUpdateMs = elapsedMs;

                        Dispatcher.InvokeAsync(() =>
                        {
                            if (_currentStreamingMessage == null)
                                return;

                            if (!firstTokenReceived)
                            {
                                firstTokenReceived = true;
                                _currentStreamingMessage.IsThinkingInProgress = false;
                                _currentStreamingMessage.PreferPlainTextRendering = true;
                            }

                            string snapshot;
                            lock (streamedResponseBuilder)
                            {
                                snapshot = streamedResponseBuilder.ToString();
                            }

                            _currentStreamingMessage.SetStreamingContent(snapshot);
                            ScrollChatToEnd();
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    },
                    token: token);

                await BackendLogService.LogEventAsync(
                    "OpenRouterLatency",
                    $"Model:{_openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId)}\nThinking:{requestContext.ThinkingEnabled}\nHistoryMessages:{requestContext.ConversationHistory.Count}\nPromptChars:{requestContext.FinalUserMessage.Length}\nToolCalls:{toolLoopResult.ToolCallCount}\nElapsedMs:{(DateTime.UtcNow - requestStartUtc).TotalMilliseconds:F0}");

                _tokenCount = toolLoopResult.Usage?.CompletionTokens > 0
                    ? toolLoopResult.Usage.CompletionTokens
                    : Math.Max(1, (toolLoopResult.ResponseText?.Length ?? 0) / 4);

                await FinalizeCloudStreamingMessageAsync(
                    toolLoopResult.ResponseText,
                    toolLoopResult.ReasoningText,
                    generationStopped: false,
                    toolLoopResult.Usage);

                ShowTransientStatus($"Tokens: {_tokenCount}  •  Mode: Cloud ({_openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId)})");
                _currentStreamingMessage = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // User pressed the Stop button — handle gracefully.
                string partialResponse;
                lock (streamedResponseBuilder)
                {
                    partialResponse = streamedResponseBuilder.ToString();
                }

                if (string.IsNullOrWhiteSpace(partialResponse))
                {
                    if (_currentStreamingMessage != null)
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            _currentStreamingMessage.IsThinkingInProgress = false;
                            _currentStreamingMessage.IsStreaming = false;
                            _chatMessages.Remove(_currentStreamingMessage);
                        }, System.Windows.Threading.DispatcherPriority.Background);
                        _currentStreamingMessage = null;
                    }

                    return;
                }

                _tokenCount = Math.Max(1, partialResponse.Length / 4);
                await FinalizeCloudStreamingMessageAsync(partialResponse, string.Empty, generationStopped: true);
                _currentStreamingMessage = null;
            }
            catch (OperationCanceledException)
            {
                // HttpClient.Timeout fired (not user stop) — show a visible error instead of
                // silently removing the message. The model took too long to start responding.
                if (_currentStreamingMessage != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _currentStreamingMessage.IsThinkingInProgress = false;
                        _currentStreamingMessage.IsStreaming = false;
                        _chatMessages.Remove(_currentStreamingMessage);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                    _currentStreamingMessage = null;
                }

                string selectedLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
                throw new InvalidOperationException($"{selectedLabel} timed out waiting for a response. The model may be overloaded — wait a moment and try again.");
            }
            catch
            {
                if (_currentStreamingMessage != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        _currentStreamingMessage.IsThinkingInProgress = false;
                        _currentStreamingMessage.IsStreaming = false;
                        _chatMessages.Remove(_currentStreamingMessage);
                    }, System.Windows.Threading.DispatcherPriority.Background);
                    _currentStreamingMessage = null;
                }

                throw;
            }
        }

        private async Task FinalizeCloudStreamingMessageAsync(
            string responseText,
            string reasoningText,
            bool generationStopped,
            OpenRouterTokenUsage? usage = null)
        {
            await Dispatcher.InvokeAsync(() =>
            {
                if (_currentStreamingMessage == null)
                    return;

                string cleanedResponse = string.IsNullOrWhiteSpace(responseText)
                    ? string.Empty
                    : StripThinkBlocksAndLeadingBlankLines(CleanOutputTokens(responseText));

                if (generationStopped && !string.IsNullOrWhiteSpace(cleanedResponse))
                    cleanedResponse = AppendGenerationStoppedLabel(cleanedResponse);

                _currentStreamingMessage.FinalizeStreamingContent(string.IsNullOrWhiteSpace(cleanedResponse)
                    ? EmptyStrippedResponseInlineHtml
                    : cleanedResponse);
                _currentStreamingMessage.ThinkingContent = CleanCloudReasoningForDisplay(reasoningText);
                _currentStreamingMessage.ThinkingHeaderText = !string.IsNullOrWhiteSpace(reasoningText) ? "View reasoning" : "Thinking";
                _currentStreamingMessage.IsThinkingInProgress = false;
                _currentStreamingMessage.IsThinkingExpanded = false;
                _currentStreamingMessage.IsStreaming = false;
                _currentStreamingMessage.PreferPlainTextRendering = false;
                _currentStreamingMessage.ModelLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
                _currentStreamingMessage.CloudPromptTokens = usage?.PromptTokens ?? 0;
                _currentStreamingMessage.CloudCompletionTokens = usage?.CompletionTokens ?? 0;
                _currentStreamingMessage.CloudTotalTokens = usage?.TotalTokens ?? 0;

                var activeBranch = _branches.FirstOrDefault(b => b.Id == _activeBranchId);
                if (activeBranch != null)
                {
                    activeBranch.Messages.Add(new ChatMessageState
                    {
                        Id = _currentStreamingMessage.Id,
                        Role = _currentStreamingMessage.Role,
                        Content = _currentStreamingMessage.Content,
                        ThinkingContent = _currentStreamingMessage.ThinkingContent,
                        ThinkingHeaderText = _currentStreamingMessage.ThinkingHeaderText,
                        ModelLabel = _currentStreamingMessage.ModelLabel,
                        CloudPromptTokens = _currentStreamingMessage.CloudPromptTokens,
                        CloudCompletionTokens = _currentStreamingMessage.CloudCompletionTokens,
                        CloudTotalTokens = _currentStreamingMessage.CloudTotalTokens,
                        Timestamp = _currentStreamingMessage.Timestamp
                    });
                }

                ScrollChatToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        private static string AppendGenerationStoppedLabel(string content)
        {
            string normalized = (content ?? string.Empty).TrimEnd();
            if (string.IsNullOrWhiteSpace(normalized))
                return string.Empty;

            return normalized + "\n\n<sub>Generation stopped.</sub>";
        }

        private async Task<CloudChatRequestContext> PrepareCloudChatRequestContextAsync(string userMsg, CancellationToken token)
        {
            NormalChatUiSnapshot uiSnapshot = await CaptureNormalChatUiSnapshotAsync(userMsg, isCloudMode: true);

            // Proactive web search — mirrors local-mode behavior so the web toggle and [web] marker
            // work identically in cloud mode. Cloud also has a web_search tool for reactive searches,
            // but proactive injection is needed when the user explicitly enables the toggle.
            string webContext = await TryBuildWebContextAsync(userMsg, false, token, uiSnapshot.ChatMessages);
            string personaContext = await _personaMemoryService.GetRelevantContextAsync(userMsg, 150, 350, token);

            return await Task.Run(() =>
            {
                bool thinkingEnabled = _normalThinkingModeEnabled;
                string systemPrompt = string.IsNullOrWhiteSpace(uiSnapshot.SystemPromptText) ? BuildDefaultAssistantSystemPrompt() : uiSnapshot.SystemPromptText.Trim();
                string cloudModelInstruction = BuildCloudModelSystemInstruction(userMsg);
                if (!string.IsNullOrWhiteSpace(cloudModelInstruction))
                    systemPrompt += "\n\n" + cloudModelInstruction;

                string cloudWebSearchInstruction = BuildCloudWebSearchSystemInstruction(userMsg, !string.IsNullOrWhiteSpace(webContext));
                if (!string.IsNullOrWhiteSpace(cloudWebSearchInstruction))
                    systemPrompt += "\n\n" + cloudWebSearchInstruction;

                string cloudIdentityInstruction = BuildCloudIdentitySystemInstruction();
                if (!string.IsNullOrWhiteSpace(cloudIdentityInstruction))
                    systemPrompt += "\n\n" + cloudIdentityInstruction;

                IReadOnlyList<string> mentionedMcp = ResolveMentionedMcpHandles(userMsg);
                string mcpInstruction = _mcpConnectorService?.BuildSystemInstruction(mentionedMcp, cloudModeActive: true) ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(mcpInstruction))
                    systemPrompt += "\n\n" + mcpInstruction;

                // Inject hippocampus context (prior research sessions) into the cloud system prompt
                if (!string.IsNullOrWhiteSpace(uiSnapshot.HippocampusContext))
                    systemPrompt += "\n\n[FROM PRIOR RESEARCH SESSIONS]\n" + uiSnapshot.HippocampusContext + "\n[/FROM PRIOR RESEARCH SESSIONS]";

                if (!string.IsNullOrWhiteSpace(personaContext))
                    systemPrompt += "\n\n[USER CONTEXT]\n" + personaContext + "\n[/USER CONTEXT]";

                if (!string.IsNullOrWhiteSpace(uiSnapshot.AttachedDocumentMemory))
                    systemPrompt += "\n\n" + uiSnapshot.AttachedDocumentMemory;

                string codingInstruction = BuildCloudCodingSystemInstruction(userMsg);
                if (!string.IsNullOrWhiteSpace(codingInstruction))
                    systemPrompt += "\n\n" + codingInstruction;

                systemPrompt = AppendSystemInstruction(systemPrompt, LocalMathLatexInstruction);

                // Pass webContext so proactive search results are grounded into this turn's system prompt
                systemPrompt = AppendSingleTurnSystemTail(systemPrompt, webContext, thinkingEnabled);

                systemPrompt = _openRouterChatService.BuildSystemPromptForModel(_selectedOpenRouterModelId, systemPrompt);
                systemPrompt = AppendAttachedDocumentContextToSystemPrompt(systemPrompt, uiSnapshot.DocumentContext);

                List<ChatMessage> historyMessages = uiSnapshot.CurrentStreamingMessageId.HasValue
                    ? uiSnapshot.ChatMessages.Where(msg => msg.Id != uiSnapshot.CurrentStreamingMessageId.Value).ToList()
                    : uiSnapshot.ChatMessages;
                List<ChatMessage> selectedHistoryMessages = SelectRelevantChatHistory(userMsg, historyMessages, uiSnapshot.ContextSize, uiSnapshot.ChatDocuments);
                List<OpenRouterMessage> conversationHistory = BuildOpenRouterConversationHistory(selectedHistoryMessages);

                return new CloudChatRequestContext
                {
                    SystemPrompt = systemPrompt,
                    FinalUserMessage = userMsg,
                    SelectedHistoryMessages = selectedHistoryMessages,
                    ConversationHistory = conversationHistory,
                    ThinkingEnabled = thinkingEnabled,
                    ImageDataUrls = BuildCloudImageDataUrls(uiSnapshot.ChatDocuments)
                };
            }, token).ConfigureAwait(false);
        }

        private void UpdateCloudContextNotice(int usedTokens)
        {
            Border? cloudContextNoticeBorder = FindName("CloudContextNoticeBorder") as Border;
            TextBlock? cloudContextNoticeText = FindName("CloudContextNoticeText") as TextBlock;
            if (cloudContextNoticeBorder == null || cloudContextNoticeText == null)
                return;

            int contextWindow = _openRouterChatService.GetApproximateContextWindowTokens(_selectedOpenRouterModelId);
            int percentUsed = contextWindow <= 0
                ? 0
                : (int)Math.Clamp(Math.Round(usedTokens * 100d / contextWindow), 0, 100);

            if (percentUsed < CloudContextNoticeThresholdPercent)
            {
                cloudContextNoticeBorder.Visibility = Visibility.Collapsed;
                cloudContextNoticeText.Text = string.Empty;
                return;
            }

            string modelLabel = _openRouterChatService.ResolveModelLabel(_selectedOpenRouterModelId);
            string levelText;
            string foregroundColor;
            string borderColor;

            if (percentUsed >= CloudContextCriticalThresholdPercent)
            {
                levelText = "Cloud context nearly full";
                foregroundColor = "#FFB4B4";
                borderColor = "#FF3B3B";
            }
            else
            {
                levelText = "Cloud context getting full";
                foregroundColor = "#EDE8E3";
                borderColor = "#B8924A";
            }

            cloudContextNoticeText.Text = $"{levelText} · {modelLabel} is using an estimated {usedTokens:N0} of {contextWindow:N0} tokens in this chat ({percentUsed}%).";
            cloudContextNoticeText.Foreground = AppBrushCache.Get(foregroundColor);
            cloudContextNoticeBorder.BorderBrush = AppBrushCache.Get(borderColor);
            cloudContextNoticeBorder.Visibility = Visibility.Visible;
        }

        private static bool IsPythonCodingRequest(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return PythonIntentPhrases.Any(signal => message.Contains(signal, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildCloudCodingSystemInstruction(string userMessage)
        {
            if (!IsCodingRequest(userMessage))
                return string.Empty;

            var builder = new StringBuilder();
            builder.AppendLine("[CODE QUALITY RULES] For coding tasks, return syntactically valid, directly usable code.");
            builder.AppendLine("Do not assume hidden setup, predeclared variables, or prior execution state.");
            builder.AppendLine("Never reference a variable, function, class, module, placeholder, or identifier unless it is declared, assigned, or imported in the code you output.");
            builder.AppendLine("If the user asks for code, prefer complete runnable code over pseudocode unless they explicitly ask for a partial snippet.");

            if (IsPythonCodingRequest(userMessage))
            {
                builder.AppendLine("[PYTHON RUNTIME RULES] When generating Python code, produce one self-contained Python 3 script unless the user explicitly asks for a different format.");
                builder.AppendLine("Declare every variable before first use.");
                builder.AppendLine("Include every required import.");
                builder.AppendLine("If user input is needed, read it with input() and assign it before using it.");
                builder.AppendLine("Do not use placeholder identifiers like name, x, y, data, value, args, or result unless you define them first.");
                builder.AppendLine("The script must run as-is when pasted into a standard online Python compiler or interpreter.");
                builder.AppendLine("Before finishing, verify that every identifier referenced in the script is either imported, assigned, defined as a function/class, or provided by Python builtins.");
                builder.AppendLine("Do not output example placeholders such as print(name), hello(name), user_name = name, or f'Hello {name}' unless name is explicitly assigned earlier in the script or read from input().");
                builder.AppendLine("For simple script requests, return a single complete code block and no extra prose unless the user explicitly asks for explanation.");
            }

            return builder.ToString().Trim();
        }

        private void RefreshNormalWebToggleUi()
        {
            _normalWebSearchToggleButton ??= FindName("NormalWebSearchToggleButton") as Button;

            if (_normalWebSearchToggleButton == null)
                return;

            _normalWebSearchToggleButton.Opacity = _normalWebSearchEnabled ? 1.0 : 0.45;
            _normalWebSearchToggleButton.ToolTip = _normalWebSearchEnabled
                ? "Normal chat web search enabled"
                : "Normal chat web search disabled";
        }
    }
}
