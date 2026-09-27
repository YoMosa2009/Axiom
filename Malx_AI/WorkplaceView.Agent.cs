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
using Malx_AI.ComputerUse;
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
        private AgentActiveTaskState? _activeAgentTaskState;

        /// <summary>Non-null while an agent run is going and can take a message from the user.</summary>
        private AgentUserMessageInbox? _agentInbox;

        /// <summary>True while the agent loop itself (not the Architect/Critic passes) is running.</summary>
        private bool _agentSessionRunning;

        /// <summary>
        /// Ticks (UTC) of a Stop that interrupted a Hybrid Local model turn, or 0. The server keeps
        /// generating the abandoned reply, so the next request waits behind it; knowing that lets
        /// the run card say so instead of sitting on "Starting...".
        /// </summary>
        private long _agentStoppedMidTurnTicks;

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

        private string _agentActivityLabel = string.Empty;
        private string _agentActivityDetail = string.Empty;
        private DateTime _agentActivityStartedUtc;
        private System.Windows.Threading.DispatcherTimer? _agentActivityTimer;

        /// <summary>
        /// The quiet one-line "what is happening right now" indicator. Null clears it, which is
        /// what every finished step and every completed run does. While a label is showing it
        /// carries a live elapsed time (and, while a file is being written, its size so far), so
        /// a long model turn reads as work in progress rather than a frozen "Thinking".
        /// </summary>
        private void ReportAgentActivity(string? label)
        {
            if (AgentActivityLine == null)
                return;

            void Apply()
            {
                if (string.IsNullOrWhiteSpace(label))
                {
                    _agentActivityTimer?.Stop();
                    _agentActivityLabel = string.Empty;
                    _agentActivityDetail = string.Empty;
                    AgentActivityLine.Visibility = Visibility.Collapsed;
                    AgentActivityText.Text = string.Empty;
                    return;
                }

                if (!string.Equals(_agentActivityLabel, label, StringComparison.Ordinal))
                {
                    _agentActivityLabel = label;
                    _agentActivityDetail = string.Empty;
                    _agentActivityStartedUtc = DateTime.UtcNow;
                }

                if (_agentActivityTimer == null)
                {
                    _agentActivityTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                    _agentActivityTimer.Tick += (_, _) => RenderAgentActivity();
                }

                _agentActivityTimer.Start();
                AgentActivityLine.Visibility = Visibility.Visible;
                RenderAgentActivity();
            }

            if (Dispatcher.CheckAccess())
                Apply();
            else
                Dispatcher.Invoke(Apply);
        }

        /// <summary>Extra detail for the current activity (e.g. "Writing index.html · 3.2 KB").</summary>
        private void ReportAgentActivityDetail(string detail)
        {
            NoteAgentModelResponding();
            Dispatcher.BeginInvoke(() =>
            {
                if (string.IsNullOrEmpty(_agentActivityLabel))
                    return;
                _agentActivityDetail = detail ?? string.Empty;
                RenderAgentActivity();
            });
        }

        private void RenderAgentActivity()
        {
            if (AgentActivityText == null || string.IsNullOrEmpty(_agentActivityLabel))
                return;

            int seconds = (int)(DateTime.UtcNow - _agentActivityStartedUtc).TotalSeconds;
            string main = string.IsNullOrWhiteSpace(_agentActivityDetail) ? _agentActivityLabel : _agentActivityDetail;
            if (string.IsNullOrWhiteSpace(_agentActivityDetail)
                && string.Equals(_agentActivityLabel, AgentThinkingLabel, StringComparison.Ordinal))
            {
                main = DescribeModelWait(seconds) ?? main;
            }

            string text = seconds >= 2 ? $"{main} · {seconds}s" : main;
            AgentActivityText.Text = text;
            SetActiveRunCardStatus(text);
        }

        private const string AgentThinkingLabel = "Thinking";

        /// <summary>
        /// What a long wait for the model's first output means, so a quiet turn never reads as a
        /// frozen app. Null keeps the plain "Thinking".
        /// </summary>
        private string? DescribeModelWait(int seconds)
        {
            bool hybridLocal = _isCloudModeEnabled && _isHybridLocalCouncilSelected;
            if (!hybridLocal)
                return seconds >= 45 ? "Waiting for the model to respond" : null;

            long stoppedTicks = Interlocked.Read(ref _agentStoppedMidTurnTicks);
            if (stoppedTicks != 0
                && DateTime.UtcNow - new DateTime(stoppedTicks, DateTimeKind.Utc) < TimeSpan.FromMinutes(15)
                && seconds >= 4)
            {
                return "Waiting for your Hybrid Local server: it is still finishing the reply that was stopped";
            }

            // Self-hosted servers usually send a tool call only once it is complete, so a model
            // writing a whole file shows no progress until it finishes.
            if (seconds >= 180)
                return "Still waiting on your Hybrid Local server (it may also be busy with another request)";
            return seconds >= 20 ? "Your Hybrid Local model is working on this step" : null;
        }

        /// <summary>The model is producing output again, so any earlier stopped turn is done.</summary>
        private void NoteAgentModelResponding() => Interlocked.Exchange(ref _agentStoppedMidTurnTicks, 0);

        /// <summary>Called by Stop: remembers that a Hybrid Local turn was abandoned mid-generation.</summary>
        private void NoteAgentStopRequested()
        {
            if (_agentInbox != null
                && _isCloudModeEnabled
                && _isHybridLocalCouncilSelected
                && string.Equals(_agentActivityLabel, AgentThinkingLabel, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _agentStoppedMidTurnTicks, DateTime.UtcNow.Ticks);
            }
        }

        private static string DescribeToolCallProgress(string toolName, int argumentChars)
        {
            string size = argumentChars >= 1024 ? $"{argumentChars / 1024.0:0.0} KB" : $"{argumentChars} chars";
            return toolName switch
            {
                AgentToolNames.WriteFile or AgentToolNames.AppendFile => $"Writing a file · {size}",
                AgentToolNames.EditFile => $"Preparing an edit · {size}",
                AgentToolNames.RunCommand => "Preparing a command",
                _ => string.IsNullOrWhiteSpace(toolName) ? "Preparing the next step" : $"Preparing {toolName}"
            };
        }

        // ───────────────────────────────── live run card

        private sealed class AgentRunCard
        {
            public required WorkplaceChatMessage Card { get; set; }
            public required string Goal { get; init; }
            public required string Title { get; init; }
            public List<string> StepLines { get; } = new();

            /// <summary>What is happening right now, shown in italics under the steps.</summary>
            public string? Status { get; set; }

            public string Render()
            {
                var builder = new StringBuilder();
                builder.Append("**").Append(Title).Append("**\n\n");
                if (StepLines.Count > 0)
                    builder.Append(string.Join("\n", StepLines.Select(l => "- " + l)));
                if (!string.IsNullOrWhiteSpace(Status))
                {
                    if (StepLines.Count > 0)
                        builder.Append("\n\n");
                    builder.Append('_').Append(Status).Append('_');
                }
                return builder.ToString().TrimEnd();
            }
        }

        private AgentRunCard? _activeAgentRunCard;
        private const string StartingStatus = "Starting...";

        /// <summary>
        /// One chat card per run that grows as the agent works: each step appears the moment it
        /// finishes (✓ / ✗), the way a terminal agent prints its actions, with a live line saying
        /// what is happening now; then the card becomes the run's final answer.
        /// </summary>
        private AgentRunCard StartAgentRunCard(string goal, string title)
        {
            var run = new AgentRunCard
            {
                Card = new WorkplaceChatMessage { Role = "agent" },
                Goal = goal,
                Title = title,
                Status = StartingStatus
            };
            run.Card.Content = run.Render();
            Dispatcher.Invoke(() =>
            {
                _chatCards.Add(run.Card);
                _activeAgentRunCard = run;
                ChatScrollViewer?.ScrollToEnd();
            });
            return run;
        }

        private void AppendAgentRunCardLine(AgentRunCard run, string line)
        {
            Dispatcher.BeginInvoke(() =>
            {
                run.StepLines.Add(line);
                if (string.Equals(run.Status, StartingStatus, StringComparison.Ordinal))
                    run.Status = null;
                run.Card.Content = run.Render();
                ChatScrollViewer?.ScrollToEnd();
            });
        }

        private void SetActiveRunCardStatus(string? status)
        {
            AgentRunCard? run = _activeAgentRunCard;
            if (run == null || string.Equals(run.Status, status, StringComparison.Ordinal))
                return;
            run.Status = status;
            run.Card.Content = run.Render();
        }

        /// <summary>
        /// The user wrote while the agent was working. Their message goes into the chat where they
        /// wrote it, and the run carries on in a fresh card underneath it, so the conversation
        /// reads in order: steps before the message, the message, steps after it.
        /// </summary>
        private bool TryPostMessageToRunningAgent()
        {
            if (_agentInbox == null)
                return false;

            string text = (QueryInput.Text ?? string.Empty).Trim();
            if (text.Length == 0)
                return true;

            if (ComputerUseMention.IsInvoked(text))
            {
                AppendVisibleNotice("The agent is still working. @ComputerUse can run once it finishes, or after you press Stop.");
                return true;
            }

            QueryInput.Text = string.Empty;
            AppendChat("user", text);
            _chatHistory.Add(("user", text));
            _agentInbox.Post(text);
            LogActivity("Agent Access: message queued for the running agent.");

            AgentRunCard? run = _activeAgentRunCard;
            if (run != null && _agentSessionRunning)
            {
                run.Status = "Continuing below with your message";
                run.Card.Content = run.Render();

                run.Card = new WorkplaceChatMessage { Role = "agent" };
                run.StepLines.Clear();
                run.Status = "Got it. Passing your message to the agent at its next step";
                run.Card.Content = run.Render();
                _chatCards.Add(run.Card);
            }

            ChatScrollViewer?.ScrollToEnd();
            return true;
        }

        private static string DescribeAgentStepLine(AgentStep step)
        {
            string action = step.Call.DescribeShort();
            if (step.Result == null)
                return $"⊘ {action} — {step.Note}";
            if (step.Result.Succeeded)
                return $"✓ {action}";

            string reason = (step.Result.Error ?? "failed").Split('\n')[0].Trim();
            if (reason.Length > 90)
                reason = reason[..89].TrimEnd() + "…";
            return $"✗ {action} — {reason}";
        }

        private void CompleteAgentRunCard(AgentRunCard run, string finalMessage)
        {
            Dispatcher.Invoke(() =>
            {
                string steps = run.StepLines.Count == 0
                    ? string.Empty
                    : $"\n\n**Steps ({run.StepLines.Count})**\n" + string.Join("\n", run.StepLines.Select(l => "- " + l));
                run.Status = null;
                run.Card.Content = (finalMessage ?? string.Empty).Trim() + steps;
                if (ReferenceEquals(_activeAgentRunCard, run))
                    _activeAgentRunCard = null;
                ChatScrollViewer?.ScrollToEnd();
            });
            RequestWorkspaceStateSave();
        }

        /// <summary>A problem the user must see: shown in the chat, not only in the bell.</summary>
        private void AppendVisibleNotice(string message)
        {
            AppendChat("notice", message);
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
        /// Runs an agent turn, then keeps going with anything the user sent that the run did not
        /// get to: a message typed as the run was finishing, or together with a Stop ("stop and
        /// do this instead"). Nothing the user types while the agent works is dropped.
        /// </summary>
        private async Task RunComputerAgentTurnAsync(string userQuery)
        {
            string? next = userQuery;
            while (next != null)
            {
                _agentInbox = new AgentUserMessageInbox();
                IReadOnlyList<string> pending;
                try
                {
                    await RunComputerAgentTurnCoreAsync(next);
                }
                finally
                {
                    pending = _agentInbox?.TakeAll() ?? [];
                    _agentInbox = null;
                    _agentSessionRunning = false;
                }

                next = null;
                if (pending.Count > 0)
                {
                    // Those messages are already in the chat; move them after the run's answer in
                    // the history so the follow-up run sees them as the latest turn.
                    foreach (string message in pending)
                    {
                        int index = _chatHistory.FindLastIndex(turn => turn.Role == "user" && turn.Content == message);
                        if (index >= 0)
                            _chatHistory.RemoveAt(index);
                    }

                    next = string.Join("\n\n", pending);
                    _chatHistory.Add(("user", next));
                    _isProcessing = true;
                    StopButton.IsEnabled = true;
                    LogActivity("Agent Access: continuing with the message sent during the last run.");
                }
            }
        }

        /// <summary>
        /// Runs one agent turn. Local, Hybrid Local, and Cloud all arrive here: the model call goes
        /// through the same role executor the rest of the Workplace uses, so whichever backend is
        /// selected is the one that drives the agent.
        /// </summary>
        private async Task RunComputerAgentTurnCoreAsync(string userQuery)
        {
            if (!_agentScopeEntireComputer && string.IsNullOrWhiteSpace(_agentScopeFolder))
            {
                AppendVisibleNotice("Agent Access needs a place to work: choose a folder in the Agent panel, or switch its scope to the entire computer.");
                FinishAgentRunUi();
                return;
            }

            AgentScope scope = BuildAgentScope();
            LocalModelCapabilityProfile? capability = _isCloudModeEnabled
                ? null
                : LocalModelCapabilityProfile.FromModel(GetEffectiveRoleConfig(CouncilRole.Builder).ModelPath);
            AgentTier tier = AgentPromptBuilder.TierFor(capability, _isCloudModeEnabled);
            string systemPrompt = AgentPromptBuilder.Build(tier, scope, _agentApprovalMode, _isCloudModeEnabled);

            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            CancellationToken token = _cancellationTokenSource.Token;

            // Context preservation: detect if we are continuing an ongoing or step-limited task
            bool isContinuation = false;
            string effectiveGoal = userQuery;
            IReadOnlyList<AgentExchange>? initialHistory = null;
            List<string>? initialAllowList = null;

            if (_activeAgentTaskState != null && !AgentContextManager.IsNewTaskPhrase(userQuery))
            {
                isContinuation = true;
                _activeAgentTaskState.FileBackup ??= CreateAgentFileBackup();
                initialAllowList = _activeAgentTaskState.SessionAllowList;
                effectiveGoal = AgentContextManager.BuildContinuationGoal(
                    _activeAgentTaskState.OriginalGoal,
                    userQuery,
                    _activeAgentTaskState.TouchedFiles,
                    _activeAgentTaskState.LastStatusMessage,
                    _activeAgentTaskState.VerifiedDependencies,
                    _activeAgentTaskState.FileBackup.Saved.Count > 0 ? _activeAgentTaskState.FileBackup.Root : null);
                initialHistory = AgentContextManager.CompactExchanges(_activeAgentTaskState.AccumulatedExchanges, recentKeepCount: 8);
                _activeAgentTaskState.LastTurnGoal = userQuery;
            }
            else
            {
                _activeAgentTaskState = new AgentActiveTaskState
                {
                    OriginalGoal = userQuery,
                    LastTurnGoal = userQuery,
                    FileBackup = CreateAgentFileBackup()
                };
            }

            AgentFileBackup fileBackup = _activeAgentTaskState.FileBackup!;
            int backupsBeforeRun = fileBackup.Saved.Count;

            // If documents or images are attached, inform the agent of their paths
            if (_documents.Count > 0)
            {
                var fileDetails = new StringBuilder();
                fileDetails.AppendLine("[ATTACHED FILES ON DISK]");
                foreach (DocumentInfo doc in _documents)
                {
                    string kind = doc.IsImage ? "Image" : "File";
                    fileDetails.AppendLine($"- {doc.Name} ({kind}): Path = \"{doc.FilePath}\"");
                }

                string attachmentManifest = BuildWorkplaceAttachmentIndexBlock(userQuery);
                if (!string.IsNullOrWhiteSpace(attachmentManifest))
                {
                    fileDetails.AppendLine();
                    fileDetails.AppendLine(attachmentManifest);
                }

                effectiveGoal = $"{effectiveGoal}\n\n{fileDetails.ToString().Trim()}";
            }

            const string runTitle = "Agent Access";
            AgentRunCard runCard = StartAgentRunCard(userQuery, runTitle);

            // Typing while the agent works is how you steer it, so Send stays available: the
            // message reaches the agent at its next step (see TryPostMessageToRunningAgent).
            SendButton.IsEnabled = true;
            SendButton.Content = "Send";
            SendButton.ToolTip = "Send a message to the running agent. It reads it at its next step.";

            // Council synergy: when Council Mode is active, Architect plans and identifies dependencies
            bool enableCouncilSynergy = !_isSingleModelMode
                && (_isCloudModeEnabled || HasEffectiveLocalRoleModel(CouncilRole.Architect) || HasEffectiveLocalRoleModel(CouncilRole.Critic));
            string? architectPlan = null;

            if (enableCouncilSynergy && (_isCloudModeEnabled || HasEffectiveLocalRoleModel(CouncilRole.Architect)))
            {
                UpdateStageIndicator(CouncilRole.Architect, false, false, false);
                RelayStatusBlock.Text = "Relay: Architect is planning...";
                LogActivity("Architect planning agent mission...");

                string architectSystem = "You are the Council Architect. The Council Builder will execute the user request using Machine Agent Tools (commands, file operations).\n"
                    + "Analyze the goal and provide a concise, grounded architectural blueprint (3-5 bullet points):\n"
                    + "1. Components/files to create or modify. Something new goes in a new, descriptively named folder (never inside an unrelated existing project); only when the user asked to change an existing project, modify it in place.\n"
                    + "2. Identify required dependencies/software and instruct Builder to pre-check if they are already installed (e.g. python -c \"import <pkg>\" or pip show) BEFORE attempting installation.\n"
                    + "3. Outline verification steps (e.g. run test or launch with Start-Process).\n"
                    + "Output ONLY the concise blueprint.";

                ReportAgentActivity("Architect is planning");
                try
                {
                    ReasoningParser.ParsedResponse architectResult = await ExecuteCouncilRoleAsync(
                        CouncilRole.Architect,
                        architectSystem,
                        $"Scope: {scope.Describe()}\nUser Request: {effectiveGoal}",
                        token,
                        temperatureOverride: 0.2f,
                        showLiveCard: false);

                    if (!string.IsNullOrWhiteSpace(architectResult.Answer))
                    {
                        architectPlan = architectResult.Answer.Trim();
                        effectiveGoal = $"{effectiveGoal}\n\n[ARCHITECT BLUEPRINT]\n{architectPlan}";
                        LogActivity("Architect blueprint generated.");
                        AppendAgentRunCardLine(runCard, "✓ Architect planned the approach");
                    }
                }
                catch (Exception archEx)
                {
                    LogActivity($"Architect planning pass skipped: {archEx.Message}");
                }
                finally
                {
                    ReportAgentActivity(null);
                }
            }

            if (enableCouncilSynergy)
            {
                UpdateStageIndicator(CouncilRole.Builder, architectPlan != null, false, false);
            }
            else
            {
                UpdateStageIndicator(CouncilRole.Builder, false, false, false);
            }

            IAgentModel model = BuildAgentModel(tier, systemPrompt, _chatHistory);
            if (!string.IsNullOrWhiteSpace(model.Unavailable))
            {
                CompleteAgentRunCard(runCard, model.Unavailable!);
                FinishAgentRunUi();
                return;
            }

            int maxSteps = AgentPromptBuilder.MaxStepsFor(tier, EffortPolicy.Current, capability, _isCloudModeEnabled);
            LogActivity($"Computer Agent: {tier} tier, {EffortPolicy.Current} effort ({maxSteps} steps), {_agentApprovalMode} approval, scope {scope.Describe()}.");
            RelayStatusBlock.Text = enableCouncilSynergy ? "Relay: Builder executing agent mission..." : "Relay: Agent running";

            var session = new AgentSession(scope, maxSteps, new AgentToolExecutor(scope, fileBackup));
            AgentRunResult result;
            _agentSessionRunning = true;
            try
            {
                result = await session.RunAsync(
                    effectiveGoal,
                    _agentApprovalMode,
                    model,
                    RequestAgentApprovalAsync,
                    ReportAgentActivity,
                    token,
                    initialHistory,
                    initialAllowList,
                    onStep: step =>
                    {
                        NoteAgentModelResponding();
                        AppendAgentRunCardLine(runCard, DescribeAgentStepLine(step));
                    },
                    inbox: _agentInbox);
            }
            catch (Exception ex)
            {
                await BackendLogService.LogErrorAsync("ComputerAgent", ex);
                CompleteAgentRunCard(runCard, $"The agent stopped: {ex.Message}");
                FinishAgentRunUi();
                return;
            }
            finally
            {
                _agentSessionRunning = false;
            }

            if (_activeAgentTaskState != null)
            {
                _activeAgentTaskState.StoppedOnStepLimit = result.StoppedOnStepLimit;
                _activeAgentTaskState.LastStatusMessage = result.FinalMessage;
                if (result.Exchanges != null && result.Exchanges.Count > 0)
                {
                    _activeAgentTaskState.AccumulatedExchanges.Clear();
                    _activeAgentTaskState.AccumulatedExchanges.AddRange(result.Exchanges);
                    foreach (string file in AgentContextManager.ExtractTouchedFiles(result.Exchanges))
                        _activeAgentTaskState.TouchedFiles.Add(file);
                    foreach (string dep in AgentContextManager.ExtractVerifiedDependencies(result.Exchanges))
                        _activeAgentTaskState.VerifiedDependencies.Add(dep);
                }
                foreach (string allowed in session.SessionAllowList)
                {
                    if (!_activeAgentTaskState.SessionAllowList.Contains(allowed))
                        _activeAgentTaskState.SessionAllowList.Add(allowed);
                }
                if (!result.StoppedOnStepLimit && !result.Cancelled)
                {
                    _activeAgentTaskState.StoppedOnStepLimit = false;
                }
            }

            foreach (AgentStep step in result.Steps)
            {
                string outcome = step.Result == null
                    ? step.Note
                    : step.Result.Succeeded ? "ok" : "failed";
                LogActivity($"Agent · {step.Call.DescribeShort()} · {outcome}");
            }

            _ = BackendLogService.LogEventAsync(
                "ComputerAgent",
                $"tier:{tier} effort:{EffortPolicy.Current} mode:{_agentApprovalMode} steps:{result.Steps.Count}/{maxSteps} "
                + $"limit:{result.StoppedOnStepLimit} cancelled:{result.Cancelled}");

            string finalChatMessage = result.FinalMessage;

            // Council synergy: Critic review phase
            if (enableCouncilSynergy && (_isCloudModeEnabled || HasEffectiveLocalRoleModel(CouncilRole.Critic)))
            {
                UpdateStageIndicator(CouncilRole.Critic, architectPlan != null, true, false);
                RelayStatusBlock.Text = "Relay: Critic is reviewing...";
                LogActivity("Critic auditing agent execution...");

                string touchedFilesList = _activeAgentTaskState?.TouchedFiles.Count > 0
                    ? string.Join(", ", _activeAgentTaskState.TouchedFiles.Select(Path.GetFileName))
                    : "none";

                string criticSystem = "You are the Council Critic. The Council Builder just completed executing an agent mission on the machine.\n"
                    + "Review the Builder's work against the goal and provide a concise verification audit (2-3 bullet points):\n"
                    + "- Did the Builder meet the user's requirements without unnecessary re-downloading or re-installing?\n"
                    + "- Are the files, code, and executed commands verified and intact?\n"
                    + "- Final verdict: [VERIFIED] or [NEEDS REVISION].\n"
                    + "Output ONLY the concise audit.";

                string criticPayload = $"User Request: {userQuery}\n"
                    + (architectPlan != null ? $"Architect Blueprint: {architectPlan}\n" : "")
                    + $"Files Touched: {touchedFilesList}\n"
                    + $"Builder Execution Report:\n{result.FinalMessage}";

                ReportAgentActivity("Critic is reviewing");
                try
                {
                    ReasoningParser.ParsedResponse criticResult = await ExecuteCouncilRoleAsync(
                        CouncilRole.Critic,
                        criticSystem,
                        criticPayload,
                        token,
                        temperatureOverride: 0.2f,
                        showLiveCard: false);

                    if (!string.IsNullOrWhiteSpace(criticResult.Answer))
                    {
                        string criticAudit = criticResult.Answer.Trim();
                        finalChatMessage = $"{result.FinalMessage}\n\n**Council Critic Verification**\n{criticAudit}";
                        UpdateStageIndicator(null, architectPlan != null, true, true);
                    }
                }
                catch (Exception criticEx)
                {
                    LogActivity($"Critic review pass skipped: {criticEx.Message}");
                    UpdateStageIndicator(null, architectPlan != null, true, false);
                }
                finally
                {
                    ReportAgentActivity(null);
                }
            }
            else if (enableCouncilSynergy)
            {
                UpdateStageIndicator(null, architectPlan != null, true, false);
            }
            else
            {
                UpdateStageIndicator(null, false, true, false);
            }

            string backupNote = DescribeBackups(fileBackup, backupsBeforeRun);
            if (backupNote.Length > 0)
                finalChatMessage = $"{finalChatMessage}\n\n{backupNote}";

            CompleteAgentRunCard(runCard, finalChatMessage);
            _chatHistory.Add(("assistant", finalChatMessage));
            UpdateWorkplaceTokenUsageIndicator();
            FinishAgentRunUi();
        }

        /// <summary>
        /// Picks how the agent talks to the model.
        /// </summary>
        /// <remarks>
        /// Cloud and Hybrid Local use the provider's own function calling with the agent's tools
        /// and nothing else. Routing them through the council executor was the original bug: that
        /// path rewrites the system prompt with council role identity and advertises web_search /
        /// run_python instead, so the model concluded it had no machine access and refused.
        /// </remarks>
        private IAgentModel BuildAgentModel(AgentTier tier, string systemPrompt, IReadOnlyList<(string Role, string Content)>? chatHistory = null)
        {
            if (_isCloudModeEnabled)
            {
                List<string>? imageDataUrls = null;
                var images = _documents
                    .Where(doc => doc.IsImage && !string.IsNullOrWhiteSpace(doc.MimeType) && !string.IsNullOrWhiteSpace(doc.Base64Data))
                    .Take(MaxCouncilVisionImagesPerTurn)
                    .ToList();
                if (images.Count > 0 && _openRouterChatService.SupportsImageInput(GetEffectiveCouncilModelId()))
                {
                    imageDataUrls = images
                        .Select(image => LocalVisionSupport.BuildImageDataUrl(image.MimeType, image.Base64Data))
                        .ToList();
                    LogActivity($"Agent Access: supplied {imageDataUrls.Count} image(s) to cloud model for vision.");
                }

                var cloudModel = new CloudAgentModel(
                    _openRouterChatService,
                    _isHybridLocalCouncilSelected
                        ? (_openRouterChatService.CustomEndpointConfiguredModelId ?? GetEffectiveCouncilModelId())
                        : GetEffectiveCouncilModelId(),
                    systemPrompt,
                    chatHistory,
                    imageDataUrls);
                cloudModel.OnToolCallProgress = (toolName, chars) =>
                    ReportAgentActivityDetail(DescribeToolCallProgress(toolName, chars));
                cloudModel.OnWaiting = message => ReportAgentActivityDetail(message);
                cloudModel.OnText = _ => NoteAgentModelResponding();
                cloudModel.OnTokenUsageRecorded = (promptTokens, completionTokens) =>
                {
                    _lastRolePromptTokenEstimates[CouncilRole.Builder] = promptTokens;
                    _lastRoleGeneratedTokenCounts[CouncilRole.Builder] = completionTokens;
                    UpdateWorkplaceTokenUsageIndicator();
                };
                return cloudModel;
            }

            // A local GGUF has no tool-calling channel, so it is asked for the flat text protocol
            // and parsed back. internalInferenceStep keeps the council's own prompt scaffolding
            // out of the way.
            return new TextProtocolAgentModel(
                async (prompt, transcript, cancellation) =>
                {
                    ReasoningParser.ParsedResponse response = await ExecuteCouncilRoleAsync(
                        CouncilRole.Builder,
                        prompt,
                        transcript,
                        cancellation,
                        temperatureOverride: 0.1f,
                        baseStateVault: null,
                        loadBaseState: false,
                        allowBatchRecovery: true,
                        showLiveCard: false,
                        maxGenerationTokensOverride: 2048,
                        contextSizeOverride: null,
                        useBuilderToolDecision: false,
                        outputGrammar: null,
                        allowAgenticPauses: false,
                        internalInferenceStep: true);
                    return response.Answer ?? string.Empty;
                },
                systemPrompt,
                ObservationBudgetFor(tier),
                chatHistory);
        }

        private static int ObservationBudgetFor(AgentTier tier) => tier switch
        {
            AgentTier.Micro => 1200,
            AgentTier.Compact => 3000,
            _ => 6000
        };

        private void FinishAgentRunUi()
        {
            ReportAgentActivity(null);
            HideAgentApprovalBar();
            _isProcessing = false;
            SendButton.IsEnabled = true;
            SendButton.Content = _isSingleModelMode ? "Run Agent" : "Run Council";
            SendButton.ToolTip = null;
            StopButton.IsEnabled = false;
            RelayStatusBlock.Text = "Relay: Idle";
        }

        /// <summary>A fresh copy folder for one task, after clearing out ones older than two weeks.</summary>
        private static AgentFileBackup CreateAgentFileBackup()
        {
            string folder = Path.Combine(AppDataPaths.Root, "AgentBackups");
            AgentFileBackup.PruneOld(folder, TimeSpan.FromDays(14));
            return new AgentFileBackup(Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd_HHmmss")));
        }

        /// <summary>Tells the user which of their existing files were changed and where the originals are.</summary>
        private static string DescribeBackups(AgentFileBackup backup, int savedBefore)
        {
            List<AgentBackedUpFile> saved = backup.Saved.Skip(savedBefore).ToList();
            if (saved.Count == 0)
                return string.Empty;

            string names = string.Join(", ", saved.Take(6).Select(file => Path.GetFileName(file.OriginalPath)));
            if (saved.Count > 6)
                names += $" and {saved.Count - 6} more";
            string noun = saved.Count == 1 ? "file that already existed" : "files that already existed";
            return $"Before changing {saved.Count} {noun} ({names}), Axiom saved the originals in `{backup.Root}`. Ask to undo it if you want them back.";
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
