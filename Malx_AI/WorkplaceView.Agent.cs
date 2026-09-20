using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Malx_AI.Agent;
using Microsoft.Win32;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        private bool _agentEnabled;
        private bool _agentScopeEntireComputer;
        private string _agentScopeFolder = string.Empty;
        private AgentApprovalMode _agentApprovalMode = AgentApprovalMode.Manual;
        private TaskCompletionSource<AgentApprovalOutcome>? _agentPendingApproval;

        private const string AgentEnabledKey = "workplace_agent_enabled";
        private const string AgentScopeAllKey = "workplace_agent_scope_all";
        private const string AgentScopeFolderKey = "workplace_agent_scope_folder";
        private const string AgentApprovalModeKey = "workplace_agent_approval_mode";

        // ───────────────────────────────── UI state

        private void InitializeAgentUi()
        {
            try
            {
                _agentEnabled = ReadAgentFlag(AgentEnabledKey);
                _agentScopeEntireComputer = ReadAgentFlag(AgentScopeAllKey);
                _agentScopeFolder = AgentSettings.Read(AgentScopeFolderKey) ?? string.Empty;
                _agentApprovalMode = string.Equals(AgentSettings.Read(AgentApprovalModeKey), "auto", StringComparison.OrdinalIgnoreCase)
                    ? AgentApprovalMode.Auto
                    : AgentApprovalMode.Manual;
            }
            catch (Exception ex)
            {
                LogActivity($"Agent settings could not be read: {ex.Message}");
            }

            RefreshAgentUi();
        }

        private static bool ReadAgentFlag(string key) =>
            string.Equals(AgentSettings.Read(key), "1", StringComparison.Ordinal);

        private void RefreshAgentUi()
        {
            if (AgentEnableButton == null)
                return;

            AgentEnableButton.Content = _agentEnabled ? "Disable Computer Agent" : "Enable Computer Agent";
            AgentStateText.Text = _agentEnabled
                ? (_agentApprovalMode == AgentApprovalMode.Auto ? "On · Auto" : "On · Manual")
                : "Off";
            AgentStateBadge.Background = _agentEnabled
                ? AppTheme.Brush(p => p.AccentSoft)
                : AppTheme.Brush(p => p.Surface);
            AgentStateBadge.BorderBrush = _agentEnabled
                ? AppTheme.Brush(p => p.Accent)
                : AppTheme.Brush(p => p.Border);
            AgentStateText.Foreground = _agentEnabled
                ? AppTheme.Brush(p => p.AccentMuted)
                : AppTheme.Brush(p => p.TextSecondary);

            AgentScopeCombo.IsEnabled = _agentEnabled;
            AgentChooseFolderButton.IsEnabled = _agentEnabled && !_agentScopeEntireComputer;
            AgentManualModeButton.IsEnabled = _agentEnabled;
            AgentAutoModeButton.IsEnabled = _agentEnabled;
            AgentScopeCombo.SelectedIndex = _agentScopeEntireComputer ? 1 : 0;

            AgentScopeDetailText.Text = _agentScopeEntireComputer
                ? "Every folder on this computer is reachable."
                : string.IsNullOrWhiteSpace(_agentScopeFolder)
                    ? "No folder chosen yet."
                    : _agentScopeFolder;

            bool auto = _agentApprovalMode == AgentApprovalMode.Auto;
            AgentAutoModeButton.Background = auto ? AppTheme.Brush(p => p.Accent) : AppTheme.Brush(p => p.Surface);
            AgentAutoModeButton.Foreground = auto ? AppTheme.Brush(p => p.Background) : AppTheme.Brush(p => p.Text);
            AgentManualModeButton.Background = auto ? AppTheme.Brush(p => p.Surface) : AppTheme.Brush(p => p.Accent);
            AgentManualModeButton.Foreground = auto ? AppTheme.Brush(p => p.Text) : AppTheme.Brush(p => p.Background);
            AgentApprovalHintText.Text = auto
                ? "Auto: the agent runs commands and edits files without stopping."
                : "Manual: you approve each command before it runs.";
        }

        private void AgentEnableButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_agentEnabled)
            {
                var confirm = MessageBox.Show(
                    Window.GetWindow(this),
                    "The Computer Agent lets the model run commands and change files on this computer.\n\n"
                    + "Manual mode asks you before every command. Auto mode does not.\n\n"
                    + "Enable it?",
                    "Enable Computer Agent",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            _agentEnabled = !_agentEnabled;
            AgentSettings.Write(AgentEnabledKey, _agentEnabled ? "1" : "0");
            RefreshAgentUi();
            AppendChat("system", _agentEnabled
                ? $"Computer Agent enabled ({_agentApprovalMode}). Scope: {BuildAgentScope().Describe()}."
                : "Computer Agent disabled.");
        }

        private void AgentScopeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AgentScopeCombo?.SelectedItem is not ComboBoxItem item)
                return;

            bool entire = string.Equals(item.Tag as string, "all", StringComparison.Ordinal);
            if (entire == _agentScopeEntireComputer)
                return;

            if (entire)
            {
                var confirm = MessageBox.Show(
                    Window.GetWindow(this),
                    "Whole-computer scope removes the folder boundary: the agent can read and change files anywhere your Windows account can.\n\nContinue?",
                    "Entire computer",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    AgentScopeCombo.SelectedIndex = 0;
                    return;
                }
            }

            _agentScopeEntireComputer = entire;
            AgentSettings.Write(AgentScopeAllKey, entire ? "1" : "0");
            RefreshAgentUi();
        }

        private void AgentChooseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "Choose the folder the agent may work in" };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true)
                return;

            _agentScopeFolder = dialog.FolderName;
            AgentSettings.Write(AgentScopeFolderKey, _agentScopeFolder);
            RefreshAgentUi();
            AppendChat("system", $"Agent scope set to {_agentScopeFolder}.");
        }

        private void AgentApprovalMode_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button)
                return;

            bool auto = string.Equals(button.Tag as string, "auto", StringComparison.Ordinal);
            if (auto && _agentApprovalMode != AgentApprovalMode.Auto)
            {
                var confirm = MessageBox.Show(
                    Window.GetWindow(this),
                    "Auto mode runs every command and file change the model asks for, without asking you first.\n\n"
                    + "Axiom still refuses a short list of whole-machine operations (formatting a drive, deleting a drive root, rewriting the boot configuration).\n\n"
                    + "Turn Auto on?",
                    "Auto approval",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                    return;
            }

            _agentApprovalMode = auto ? AgentApprovalMode.Auto : AgentApprovalMode.Manual;
            AgentSettings.Write(AgentApprovalModeKey, auto ? "auto" : "manual");
            RefreshAgentUi();
        }

        // ───────────────────────────────── activity line

        /// <summary>
        /// The quiet one-line "what is happening right now" indicator. Null clears it, which is
        /// what every finished step and every completed run does.
        /// </summary>
        private void ReportAgentActivity(string? label)
        {
            if (AgentActivityLine == null)
                return;

            void Apply()
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    AgentActivityLine.Visibility = Visibility.Collapsed;
                    AgentActivityText.Text = string.Empty;
                }
                else
                {
                    AgentActivityText.Text = label;
                    AgentActivityLine.Visibility = Visibility.Visible;
                }
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        // ───────────────────────────────── approval gate

        private Task<AgentApprovalOutcome> RequestAgentApprovalAsync(AgentToolCall call, string reason, CancellationToken token)
        {
            var completion = new TaskCompletionSource<AgentApprovalOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            _agentPendingApproval = completion;

            Dispatcher.Invoke(() =>
            {
                AgentApprovalTitleText.Text = string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                    ? "Run this command?"
                    : $"Allow this change? ({call.Tool})";
                AgentApprovalDetailText.Text = DescribeCallForApproval(call);
                AgentApproveAlwaysButton.Visibility =
                    string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                AgentApprovalBar.Visibility = Visibility.Visible;
            });

            // A cancelled run must not leave the loop parked on a prompt nobody will answer.
            CancellationTokenRegistration registration = token.Register(() =>
            {
                completion.TrySetResult(AgentApprovalOutcome.Deny);
                Dispatcher.Invoke(HideAgentApprovalBar);
            });

            return completion.Task.ContinueWith(task =>
            {
                registration.Dispose();
                return task.Result;
            }, TaskScheduler.Default);
        }

        private static string DescribeCallForApproval(AgentToolCall call)
        {
            if (string.Equals(call.Tool, AgentToolNames.RunCommand, StringComparison.OrdinalIgnoreCase))
            {
                string cwd = call.Arg("cwd");
                return string.IsNullOrWhiteSpace(cwd) ? call.Arg("command") : $"{call.Arg("command")}\n(in {cwd})";
            }

            var builder = new StringBuilder();
            builder.AppendLine(call.Arg("path"));
            if (string.Equals(call.Tool, AgentToolNames.EditFile, StringComparison.OrdinalIgnoreCase))
            {
                builder.AppendLine();
                builder.AppendLine("- " + Clip(call.Arg("old_string"), 200));
                builder.Append("+ " + Clip(call.Arg("new_string"), 200));
            }
            else
            {
                builder.Append(Clip(call.Arg("content"), 400));
            }

            return builder.ToString();
        }

        private static string Clip(string value, int max)
        {
            string text = value ?? string.Empty;
            return text.Length <= max ? text : text[..max] + "…";
        }

        private void AgentApprove_Click(object sender, RoutedEventArgs e) => ResolveAgentApproval(AgentApprovalOutcome.Approve);
        private void AgentApproveAlways_Click(object sender, RoutedEventArgs e) => ResolveAgentApproval(AgentApprovalOutcome.ApproveAlways);
        private void AgentDeny_Click(object sender, RoutedEventArgs e) => ResolveAgentApproval(AgentApprovalOutcome.Deny);

        private void ResolveAgentApproval(AgentApprovalOutcome outcome)
        {
            HideAgentApprovalBar();
            _agentPendingApproval?.TrySetResult(outcome);
            _agentPendingApproval = null;
        }

        private void HideAgentApprovalBar()
        {
            if (AgentApprovalBar != null)
                AgentApprovalBar.Visibility = Visibility.Collapsed;
        }

        // ───────────────────────────────── the run

        private AgentScope BuildAgentScope() =>
            _agentScopeEntireComputer || string.IsNullOrWhiteSpace(_agentScopeFolder)
                ? (_agentScopeEntireComputer ? AgentScope.WholeComputer() : AgentScope.Folder(Directory.GetCurrentDirectory()))
                : AgentScope.Folder(_agentScopeFolder);

        /// <summary>
        /// Runs one agent turn. Local, Hybrid Local, and Cloud all arrive here: the model call goes
        /// through the same role executor the rest of the Workplace uses, so whichever backend is
        /// selected is the one that drives the agent.
        /// </summary>
        private async Task RunComputerAgentTurnAsync(string userQuery)
        {
            if (!_agentScopeEntireComputer && string.IsNullOrWhiteSpace(_agentScopeFolder))
            {
                AppendChat("error", "Choose a folder for the agent, or switch its scope to the entire computer.");
                FinishAgentRunUi();
                return;
            }

            AgentScope scope = BuildAgentScope();
            LocalModelCapabilityProfile? capability = _isCloudModeEnabled
                ? null
                : LocalModelCapabilityProfile.FromModel(GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath);
            AgentTier tier = AgentPromptBuilder.TierFor(capability, _isCloudModeEnabled);

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            LogActivity($"Computer Agent: {tier} tier, {_agentApprovalMode} approval, scope {scope.Describe()}.");

            var session = new AgentSession(scope, tier);
            AgentRunResult result;
            try
            {
                result = await session.RunAsync(
                    userQuery,
                    _agentApprovalMode,
                    InvokeAgentModelAsync,
                    RequestAgentApprovalAsync,
                    ReportAgentActivity,
                    token);
            }
            catch (Exception ex)
            {
                AppendChat("error", $"The agent stopped: {ex.Message}");
                FinishAgentRunUi();
                return;
            }

            foreach (AgentStep step in result.Steps)
            {
                string outcome = step.Result == null
                    ? step.Note
                    : step.Result.Succeeded ? "ok" : "failed";
                LogActivity($"Agent · {step.Call.DescribeShort()} · {outcome}");
            }

            AppendChat("assistant", result.FinalMessage);
            _chatHistory.Add(("assistant", result.FinalMessage));
            FinishAgentRunUi();
        }

        private async Task<string> InvokeAgentModelAsync(string systemPrompt, string transcript, CancellationToken token)
        {
            ReasoningParser.ParsedResponse response = await ExecuteCouncilRoleAsync(
                CouncilRole.Builder,
                systemPrompt,
                transcript,
                token,
                temperatureOverride: 0.1f,
                baseStateVault: null,
                loadBaseState: false,
                allowBatchRecovery: true,
                showLiveCard: false,
                maxGenerationTokensOverride: 1400,
                contextSizeOverride: null,
                useBuilderToolDecision: false,
                outputGrammar: null,
                allowAgenticPauses: false,
                internalInferenceStep: true);

            return response.Answer ?? string.Empty;
        }

        private void FinishAgentRunUi()
        {
            ReportAgentActivity(null);
            HideAgentApprovalBar();
            _isProcessing = false;
            SendButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            RelayStatusBlock.Text = "Relay: Idle";
        }
    }

    /// <summary>Small key/value store for the agent's own switches, kept out of session state.</summary>
    internal static class AgentSettings
    {
        private static readonly string SettingsPath =
            Path.Combine(AppDataPaths.ChatHistory, "agent_settings.json");

        private static Dictionary<string, string> Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsPath))
                       ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        public static string? Read(string key) => Load().TryGetValue(key, out string? value) ? value : null;

        public static void Write(string key, string value)
        {
            try
            {
                Dictionary<string, string> all = Load();
                all[key] = value;
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath) ?? AppDataPaths.ChatHistory);
                AtomicFileWriter.WriteAllText(SettingsPath, System.Text.Json.JsonSerializer.Serialize(all));
            }
            catch
            {
                // A settings write failure must never take down a run in progress.
            }
        }
    }
}
