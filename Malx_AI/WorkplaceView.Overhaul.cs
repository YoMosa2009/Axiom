using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Malx_AI.Agent;
using Microsoft.Win32;

namespace Malx_AI
{
    public enum WorkplaceRightSidebarTab
    {
        Diff,
        Context,
        Canvas
    }

    public partial class WorkplaceView
    {
        private sealed record TrackedFileDiffRecord(
            string FilePath,
            string BeforeText,
            string AfterText,
            int Additions,
            int Removals);

        public event Action<string, string>? SessionMetadataChanged;

        private readonly AgentBackgroundTaskManager _backgroundTaskManager = new();
        private readonly BrowserControlService _browserControl = new();
        private readonly DispatcherTimer _overhaulLiveTimer = new();
        private readonly List<string> _recentProjectFolders = new();
        private readonly List<string> _sessionTouchedFiles = new();
        private readonly Dictionary<string, string> _fileBaselineCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TrackedFileDiffRecord> _trackedFileDiffs = new(StringComparer.OrdinalIgnoreCase);

        private string _sessionTitle = "New session";
        private string _activeTrackerFilePath = string.Empty;
        private CommandReviewMode _commandReviewMode = CommandReviewMode.AutoReview;
        private WorkplaceRightSidebarTab _rightSidebarTab = WorkplaceRightSidebarTab.Canvas;
        private bool _isFinishedTasksExpanded;
        private string? _cachedBrowserPromptSummary;

        public string SessionTitle => _sessionTitle;

        public string SelectedProjectFolder => (_agentScopeFolder ?? string.Empty).Trim();

        public CommandReviewMode CurrentCommandReviewMode => _commandReviewMode;

        internal AgentBackgroundTaskManager BackgroundTaskManager => _backgroundTaskManager;

        internal BrowserControlService BrowserControl => _browserControl;

        private void InitializeOverhaulFeatures()
        {
            _overhaulLiveTimer.Interval = TimeSpan.FromSeconds(1);
            _overhaulLiveTimer.Tick += OverhaulLiveTimer_Tick;

            _backgroundTaskManager.Changed += () =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    RefreshRightSidebarContextAndTasks();
                    UpdateOverhaulLiveTimerState();
                }, DispatcherPriority.Background);
            };

            UpdateProjectScopeBadgesUi();
            UpdateCommandReviewSelectorUi();
            UpdateSessionTitleHeaderUi();
            HighlightRightSidebarButtons();
        }

        private void OverhaulLiveTimer_Tick(object? sender, EventArgs e)
        {
            if (_rightSidebarTab == WorkplaceRightSidebarTab.Context
                && _backgroundTaskManager.ListTasks().Any(t => t.IsRunning))
            {
                RefreshRightSidebarBackgroundTasksOnly();
            }

            UpdateOverhaulLiveTimerState();
        }

        private void UpdateOverhaulLiveTimerState()
        {
            bool anyRunningBg = _backgroundTaskManager.ListTasks().Any(t => t.IsRunning);
            if (anyRunningBg)
            {
                if (!_overhaulLiveTimer.IsEnabled)
                    _overhaulLiveTimer.Start();
            }
            else if (_overhaulLiveTimer.IsEnabled)
            {
                _overhaulLiveTimer.Stop();
            }
        }

        public void RenameSessionTitle(string? newTitle)
        {
            string clean = string.IsNullOrWhiteSpace(newTitle) ? "New session" : newTitle.Trim();
            _sessionTitle = clean;
            UpdateSessionTitleHeaderUi();
            SessionMetadataChanged?.Invoke(_sessionTitle, SelectedProjectFolder);
        }

        public void SetProjectFolderScope(string? folderPath)
        {
            string clean = folderPath?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(clean) && Directory.Exists(clean))
            {
                _agentScopeFolder = clean;
                _agentScopeEntireComputer = false;
                _agentEnabled = true;
                AgentSettings.Write(AgentScopeFolderKey, clean);
                AgentSettings.Write(AgentScopeAllKey, "0");
                AgentSettings.Write(AgentEnabledKey, "1");
                RememberRecentProjectFolder(clean);
            }
            else if (string.IsNullOrWhiteSpace(clean))
            {
                _agentScopeFolder = string.Empty;
                _agentScopeEntireComputer = true;
                AgentSettings.Write(AgentScopeFolderKey, string.Empty);
                AgentSettings.Write(AgentScopeAllKey, "1");
            }

            RefreshAgentUi();
            UpdateProjectScopeBadgesUi();
            RefreshRightSidebarContextAndTasks();
            SessionMetadataChanged?.Invoke(_sessionTitle, SelectedProjectFolder);
        }

        public void ResetWorkspaceSession(string? projectFolderPath)
        {
            ResetWorkspaceSession();
            _sessionTitle = "New session";
            _activeTrackerFilePath = string.Empty;
            _sessionTouchedFiles.Clear();
            _fileBaselineCache.Clear();
            _trackedFileDiffs.Clear();
            SetProjectFolderScope(projectFolderPath);
            UpdateSessionTitleHeaderUi();
            RefreshRightSidebarDiffView();
            RefreshRightSidebarContextAndTasks();
        }

        private void RestoreOverhaulSnapshotFields(WorkplaceSessionSnapshot snapshot)
        {
            string rawTitle = snapshot.SessionTitle;
            if (WorkplaceSessionTitleGenerator.IsPlaceholderName(rawTitle))
            {
                string? firstPrompt = snapshot.ChatCards?
                    .FirstOrDefault(c => string.Equals(c.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content
                    ?? snapshot.ObjectiveText;
                rawTitle = WorkplaceSessionTitleGenerator.GenerateTitle(firstPrompt, snapshot.ProjectFolderPath);
            }
            _sessionTitle = string.IsNullOrWhiteSpace(rawTitle) ? "New session" : rawTitle.Trim();

            if (Enum.TryParse<CommandReviewMode>(snapshot.CommandReviewMode, true, out var reviewMode))
                _commandReviewMode = reviewMode;
            else
                _commandReviewMode = CommandReviewMode.AutoReview;

            _agentApprovalMode = _commandReviewMode == CommandReviewMode.ManualReview
                ? AgentApprovalMode.Manual
                : AgentApprovalMode.Auto;
            AgentSettings.Write(AgentApprovalModeKey, _agentApprovalMode == AgentApprovalMode.Auto ? "auto" : "manual");

            _agentScopeFolder = snapshot.ProjectFolderPath ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_agentScopeFolder) && Directory.Exists(_agentScopeFolder))
            {
                _agentScopeEntireComputer = false;
                _agentEnabled = true;
                RememberRecentProjectFolder(_agentScopeFolder);
            }

            _sessionTouchedFiles.Clear();
            _trackedFileDiffs.Clear();
            _activeTrackerFilePath = string.Empty;

            UpdateSessionTitleHeaderUi();
            UpdateProjectScopeBadgesUi();
            UpdateCommandReviewSelectorUi();
            RefreshAgentUi();
            RefreshRightSidebarContextAndTasks();
            RefreshRightSidebarDiffView();
        }

        internal void EnsureAutoSessionTitle(string? prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return;

            if (!WorkplaceSessionTitleGenerator.IsPlaceholderName(_sessionTitle))
                return;

            _sessionTitle = WorkplaceSessionTitleGenerator.GenerateTitle(prompt, _agentScopeFolder);
            UpdateSessionTitleHeaderUi();
            SessionMetadataChanged?.Invoke(_sessionTitle, SelectedProjectFolder);
        }

        internal string GetOrResolveTrackerFilePath()
        {
            if (!string.IsNullOrWhiteSpace(_activeTrackerFilePath))
                return _activeTrackerFilePath;

            int stableId = Math.Abs((_projectKnowledgeBaseId ?? "default").GetHashCode());
            string folder = !string.IsNullOrWhiteSpace(_agentScopeFolder) && Directory.Exists(_agentScopeFolder)
                ? _agentScopeFolder
                : null!;
            _activeTrackerFilePath = WorkplaceTaskTrackerFileService.ResolveTrackerPath(folder, stableId);
            return _activeTrackerFilePath;
        }

        internal void EnsureSessionTrackerUpdated(
            string userGoal,
            string? latestSummary = null,
            IEnumerable<string>? completedSteps = null,
            IEnumerable<string>? touchedFiles = null)
        {
            try
            {
                if (touchedFiles != null)
                {
                    foreach (string f in touchedFiles)
                    {
                        if (!string.IsNullOrWhiteSpace(f)
                            && !_sessionTouchedFiles.Any(existing => string.Equals(existing, f, StringComparison.OrdinalIgnoreCase)))
                        {
                            _sessionTouchedFiles.Add(f);
                        }
                    }
                }

                string path = GetOrResolveTrackerFilePath();
                WorkplaceTaskTrackerFileService.InitializeOrUpdateTracker(
                    path,
                    _sessionTitle,
                    userGoal,
                    completedSteps?.ToList(),
                    _sessionTouchedFiles,
                    _backgroundTaskManager.ListTasks(),
                    latestSummary,
                    _activeAgentTaskState?.OriginalGoal,
                    _activeAgentTaskState?.Turns);

                RefreshRightSidebarContextAndTasks();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Tracker update warning: {ex.Message}");
            }
        }

        internal string GetTrackerContextAfterCompaction()
        {
            if (string.IsNullOrWhiteSpace(_activeTrackerFilePath))
                GetOrResolveTrackerFilePath();
            return WorkplaceTaskTrackerFileService.ReadTrackerForCompactionContext(_activeTrackerFilePath, 3500);
        }

        internal string GetDetectedBrowserSummaryForPrompt()
        {
            if (!string.IsNullOrWhiteSpace(_cachedBrowserPromptSummary))
                return _cachedBrowserPromptSummary;

            DetectedBrowserInfo info = BrowserControlService.DetectSingleBrowser();
            _cachedBrowserPromptSummary = $"{info.DisplayName} (search: {info.DefaultSearchEngineName})";
            return _cachedBrowserPromptSummary;
        }

        internal void UpdateWorkplaceStatusLine(string? status)
            => ReportAgentActivity(status);

        internal void RecordTrackedAgentFileStep(AgentStep step, string workingDirectory)
        {
            if (step.Result == null || !step.Result.Succeeded)
                return;

            string tool = step.Call.Tool.ToLowerInvariant();
            string rawPath = step.Call.Arg("path");
            if (string.IsNullOrWhiteSpace(rawPath))
                return;

            string resolved;
            try
            {
                resolved = Path.IsPathRooted(rawPath)
                    ? Path.GetFullPath(rawPath)
                    : Path.GetFullPath(Path.Combine(workingDirectory, rawPath));
            }
            catch
            {
                return;
            }

            if (tool == AgentToolNames.ReadFile)
            {
                if (!_fileBaselineCache.ContainsKey(resolved) && File.Exists(resolved))
                {
                    try
                    {
                        if (new FileInfo(resolved).Length <= 512 * 1024)
                            _fileBaselineCache[resolved] = File.ReadAllText(resolved);
                    }
                    catch { }
                }
                return;
            }

            if (tool is AgentToolNames.WriteFile or AgentToolNames.AppendFile or AgentToolNames.EditFile)
            {
                if (!_sessionTouchedFiles.Any(p => string.Equals(p, resolved, StringComparison.OrdinalIgnoreCase)))
                    _sessionTouchedFiles.Add(resolved);

                string before = _fileBaselineCache.TryGetValue(resolved, out string? cached) ? cached : string.Empty;
                string after = string.Empty;
                if (File.Exists(resolved))
                {
                    try
                    {
                        if (new FileInfo(resolved).Length <= 512 * 1024)
                            after = File.ReadAllText(resolved);
                    }
                    catch { }
                }

                IReadOnlyList<LineDiffEntry> diff = LineDiff.Build(before, after);
                int additions = diff.Count(d => d.Kind == LineDiffKind.Added);
                int removals = diff.Count(d => d.Kind == LineDiffKind.Removed);
                _trackedFileDiffs[resolved] = new TrackedFileDiffRecord(resolved, before, after, additions, removals);

                Dispatcher.InvokeAsync(() =>
                {
                    RefreshRightSidebarDiffView();
                    RefreshRightSidebarContextAndTasks();
                }, DispatcherPriority.Background);
            }
        }

        private void UpdateSessionTitleHeaderUi()
        {
            if (WorkplaceSessionTitleText != null)
                WorkplaceSessionTitleText.Text = string.IsNullOrWhiteSpace(_sessionTitle) ? "New session" : _sessionTitle;
        }

        private void UpdateProjectScopeBadgesUi()
        {
            bool scoped = !_agentScopeEntireComputer && !string.IsNullOrWhiteSpace(_agentScopeFolder) && Directory.Exists(_agentScopeFolder);
            if (WorkplaceProjectBadgeText != null)
            {
                WorkplaceProjectBadgeText.Text = scoped
                    ? WorkplaceSessionTitleGenerator.ShortProjectLabel(_agentScopeFolder)
                    : "This computer";
            }

            if (WorkplaceProjectBadgeButton != null)
            {
                WorkplaceProjectBadgeButton.ToolTip = scoped
                    ? $"Strictly confined to: {_agentScopeFolder}"
                    : "Unscoped (click Select Project to confine the model to a folder)";
            }

            if (SelectProjectCurrentPathText != null)
            {
                SelectProjectCurrentPathText.Text = scoped
                    ? _agentScopeFolder
                    : "Not restricted yet (This computer)";
            }
        }

        private void UpdateCommandReviewSelectorUi()
        {
            if (CommandReviewSelectorText == null)
                return;

            CommandReviewSelectorText.Text = _commandReviewMode switch
            {
                CommandReviewMode.AutoReview => "Auto-Review",
                CommandReviewMode.ManualReview => "Review Commands",
                CommandReviewMode.AutoApprove => "Auto-Run",
                _ => "Auto-Review"
            };
        }

        private void RememberRecentProjectFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return;
            _recentProjectFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
            _recentProjectFolders.Insert(0, folder);
            while (_recentProjectFolders.Count > 5)
                _recentProjectFolders.RemoveAt(_recentProjectFolders.Count - 1);
        }

        private void PopulateRecentProjectListUi()
        {
            if (SelectProjectRecentList == null || SelectProjectRecentHeader == null)
                return;

            SelectProjectRecentList.Children.Clear();
            var valid = _recentProjectFolders.Where(Directory.Exists).ToList();
            SelectProjectRecentHeader.Visibility = valid.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (string path in valid)
            {
                var btn = new Button
                {
                    Content = WorkplaceSessionTitleGenerator.ShortProjectLabel(path) + "  ·  " + path,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 1, 0, 1),
                    FontSize = 10.5,
                    Style = (Style)FindResource("SubtleButtonStyle"),
                    ToolTip = path
                };
                btn.Click += (_, _) =>
                {
                    SetProjectFolderScope(path);
                    SelectProjectPopup.IsOpen = false;
                };
                SelectProjectRecentList.Children.Add(btn);
            }
        }

        public void CloseAllPopups()
        {
            if (WorkplaceConfigPopup != null)
                WorkplaceConfigPopup.IsOpen = false;
            if (NotificationDropdownPopup != null)
                NotificationDropdownPopup.IsOpen = false;
            _isNotificationPanelOpen = false;
            SetSidebarButtonActive(NotificationToggleButton, false);
            if (WorkplacePlusPopup != null)
                WorkplacePlusPopup.IsOpen = false;
            if (SelectProjectPopup != null)
                SelectProjectPopup.IsOpen = false;
            if (CommandReviewPopup != null)
                CommandReviewPopup.IsOpen = false;
            if (WorkplaceSkillsPopup != null)
                WorkplaceSkillsPopup.IsOpen = false;
            if (ProjectKnowledgeBasePopup != null)
                ProjectKnowledgeBasePopup.IsOpen = false;
            if (WorkplaceMentionPopup != null)
                WorkplaceMentionPopup.IsOpen = false;
        }

        private void WorkplaceConfigButton_Click(object sender, RoutedEventArgs e)
        {
            if (WorkplaceConfigPopup != null)
                WorkplaceConfigPopup.IsOpen = !WorkplaceConfigPopup.IsOpen;
        }

        private void WorkplaceSessionTitleButton_Click(object sender, RoutedEventArgs e)
        {
            Window? owner = Window.GetWindow(this);
            var dialog = new ChatRenameDialog(_sessionTitle);
            if (owner != null)
                dialog.Owner = owner;
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.ChatName))
            {
                RenameSessionTitle(dialog.ChatName);
                RequestWorkspaceStateSave();
            }
        }

        private void WorkplacePlusButton_Click(object sender, RoutedEventArgs e)
        {
            if (WorkplacePlusPopup != null)
                WorkplacePlusPopup.IsOpen = true;
        }

        private void WorkplaceSelectProjectMenu_Click(object sender, RoutedEventArgs e)
        {
            if (WorkplacePlusPopup != null)
                WorkplacePlusPopup.IsOpen = false;
            OpenSelectProjectPopup_Click(sender, e);
        }

        private void WorkplaceAttachFilesMenu_Click(object sender, RoutedEventArgs e)
        {
            if (WorkplacePlusPopup != null)
                WorkplacePlusPopup.IsOpen = false;
            WorkplaceAttachFileButton_Click(sender, e);
        }

        private void OpenSelectProjectPopup_Click(object sender, RoutedEventArgs e)
        {
            UpdateProjectScopeBadgesUi();
            PopulateRecentProjectListUi();
            if (SelectProjectPopup != null)
                SelectProjectPopup.IsOpen = true;
        }

        private void CloseSelectProjectPopup_Click(object sender, RoutedEventArgs e)
        {
            if (SelectProjectPopup != null)
                SelectProjectPopup.IsOpen = false;
        }

        private void BrowseStrictProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "Select project folder (model strictly confined to this folder)"
            };
            Window? owner = Window.GetWindow(this);
            bool? ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
            if (ok == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SetProjectFolderScope(dialog.FolderName);
                if (SelectProjectPopup != null)
                    SelectProjectPopup.IsOpen = false;
                RequestWorkspaceStateSave();
            }
        }

        private void ClearStrictProjectFolder_Click(object sender, RoutedEventArgs e)
        {
            SetProjectFolderScope(string.Empty);
            if (SelectProjectPopup != null)
                SelectProjectPopup.IsOpen = false;
            RequestWorkspaceStateSave();
        }

        private void CommandReviewSelectorButton_Click(object sender, RoutedEventArgs e)
        {
            WorkplacePlusPopup.IsOpen = false;
            if (CommandReviewPopup != null)
                CommandReviewPopup.IsOpen = true;
        }

        private void SelectCommandReviewOption_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tag && Enum.TryParse<CommandReviewMode>(tag, true, out var mode))
            {
                _commandReviewMode = mode;
                _agentApprovalMode = mode == CommandReviewMode.ManualReview
                    ? AgentApprovalMode.Manual
                    : AgentApprovalMode.Auto;
                AgentSettings.Write(AgentApprovalModeKey, _agentApprovalMode == AgentApprovalMode.Auto ? "auto" : "manual");
                UpdateCommandReviewSelectorUi();
                RefreshAgentUi();
                RequestWorkspaceStateSave();
            }

            if (CommandReviewPopup != null)
                CommandReviewPopup.IsOpen = false;
        }

        private void WorkplaceDiffSidebarButton_Click(object sender, RoutedEventArgs e)
            => ShowRightSidebarTab(WorkplaceRightSidebarTab.Diff, toggleOffIfSame: true);

        private void WorkplaceContextSidebarButton_Click(object sender, RoutedEventArgs e)
            => ShowRightSidebarTab(WorkplaceRightSidebarTab.Context, toggleOffIfSame: true);

        private void WorkplaceCanvasSidebarButton_Click(object sender, RoutedEventArgs e)
            => ShowRightSidebarTab(WorkplaceRightSidebarTab.Canvas, toggleOffIfSame: true);

        private void RightTabCanvasButton_Click(object sender, RoutedEventArgs e)
            => ShowRightSidebarTab(WorkplaceRightSidebarTab.Canvas, toggleOffIfSame: false);

        private void ShowRightSidebarTab(WorkplaceRightSidebarTab tab, bool toggleOffIfSame)
        {
            if (_rightSidebarTab == tab && _isProjectCanvasShown && toggleOffIfSame)
            {
                ToggleProjectCanvas();
                HighlightRightSidebarButtons();
                return;
            }

            _rightSidebarTab = tab;
            if (!_isProjectCanvasShown)
                ToggleProjectCanvas();

            if (tab == WorkplaceRightSidebarTab.Diff)
                RefreshRightSidebarDiffView();
            else if (tab == WorkplaceRightSidebarTab.Context)
                RefreshRightSidebarContextAndTasks();

            ApplyRightSidebarTabVisibility();
            HighlightRightSidebarButtons();
        }

        private void ApplyRightSidebarTabVisibility()
        {
            bool isContext = _rightSidebarTab == WorkplaceRightSidebarTab.Context;
            RightSidebarHeaderBorder.Visibility = isContext ? Visibility.Collapsed : Visibility.Visible;
            ContextEnvironmentPanel.Visibility = isContext ? Visibility.Visible : Visibility.Collapsed;
            CodeOutputPanel.Visibility = isContext ? Visibility.Collapsed : Visibility.Visible;
            RefreshCanvasArtifactUi();
        }

        private void HighlightRightSidebarButtons()
        {
            bool panelOpen = _isProjectCanvasShown;
            SetSidebarButtonActive(RightTabDiffButton, panelOpen && _rightSidebarTab == WorkplaceRightSidebarTab.Diff);
            SetSidebarButtonActive(RightTabContextButton, panelOpen && _rightSidebarTab == WorkplaceRightSidebarTab.Context);
            SetSidebarButtonActive(RightTabCanvasButton, panelOpen && _rightSidebarTab == WorkplaceRightSidebarTab.Canvas);
            SetSidebarButtonActive(WorkplaceDiffSidebarButton, panelOpen && _rightSidebarTab == WorkplaceRightSidebarTab.Diff);
            SetSidebarButtonActive(WorkplaceContextSidebarButton, panelOpen && _rightSidebarTab == WorkplaceRightSidebarTab.Context);
            SetSidebarButtonActive(WorkplaceCanvasSidebarButton, panelOpen && _rightSidebarTab == WorkplaceRightSidebarTab.Canvas);
        }

        private void SetSidebarButtonActive(Button? btn, bool active)
        {
            if (btn == null)
                return;
            btn.Background = active ? AppTheme.Brush("#3A3226") : Brushes.Transparent;
            btn.BorderBrush = active ? AppTheme.Brush("#B8924A") : AppTheme.Brush("#302D2A");
        }

        private void RefreshRightSidebarDiffView()
        {
            if (_rightSidebarTab != WorkplaceRightSidebarTab.Diff)
                return;

            if (SessionDiffLines == null)
                return;

            var cards = new List<UIElement>();
            int totalAdd = 0;
            int totalDel = 0;

            foreach (TrackedFileDiffRecord rec in _trackedFileDiffs.Values)
            {
                totalAdd += rec.Additions;
                totalDel += rec.Removals;
                cards.Add(BuildDiffCardElement(
                    Path.GetFileName(rec.FilePath),
                    rec.FilePath,
                    rec.BeforeText,
                    rec.AfterText,
                    rec.Additions,
                    rec.Removals));
            }

            if (!string.IsNullOrWhiteSpace(_canvasDiffCurrentSource)
                && !string.Equals(_canvasDiffBaseSource, _canvasDiffCurrentSource, StringComparison.Ordinal))
            {
                IReadOnlyList<LineDiffEntry> canvasDiff = LineDiff.Build(_canvasDiffBaseSource, _canvasDiffCurrentSource);
                int cAdd = canvasDiff.Count(d => d.Kind == LineDiffKind.Added);
                int cDel = canvasDiff.Count(d => d.Kind == LineDiffKind.Removed);
                if (cAdd > 0 || cDel > 0)
                {
                    totalAdd += cAdd;
                    totalDel += cDel;
                    cards.Add(BuildDiffCardElement(
                        "Project Canvas Artifact",
                        "Live canvas deliverable",
                        _canvasDiffBaseSource,
                        _canvasDiffCurrentSource,
                        cAdd,
                        cDel));
                }
            }

            SessionDiffLines.Items.Clear();
            foreach (UIElement card in cards)
                SessionDiffLines.Items.Add(card);
            if (DiffViewerEmptyText != null)
                DiffViewerEmptyText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _sessionDiffSummary = cards.Count > 0
                ? $"+{totalAdd} additions / −{totalDel} removals"
                : string.Empty;
            RefreshCanvasPresentation();
        }

        private UIElement BuildDiffCardElement(
            string title,
            string subtitle,
            string before,
            string after,
            int additions,
            int removals)
        {
            var outer = new Border
            {
                Background = AppTheme.Brush("#211F1D"),
                BorderBrush = AppTheme.Brush("#302D2A"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var stack = new StackPanel();
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var counts = new TextBlock
            {
                Text = $"+{additions} / -{removals}",
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = AppTheme.Brush("#5FAF7D"),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(counts, Dock.Right);
            header.Children.Add(counts);

            var nameText = new TextBlock
            {
                Text = title,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                ToolTip = subtitle,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            header.Children.Add(nameText);
            stack.Children.Add(header);

            IReadOnlyList<LineDiffEntry> entries = LineDiff.Build(before, after);
            int shown = 0;
            foreach (LineDiffEntry entry in entries)
            {
                if (entry.Kind == LineDiffKind.Unchanged)
                    continue;
                if (++shown > 120)
                {
                    stack.Children.Add(new TextBlock
                    {
                        Text = $"… {entries.Count - shown} more diff lines truncated",
                        FontSize = 10,
                        Foreground = AppTheme.Brush("#8A8279")
                    });
                    break;
                }

                bool added = entry.Kind == LineDiffKind.Added;
                var lineBorder = new Border
                {
                    Background = new SolidColorBrush(added ? Color.FromArgb(0x22, 0x3F, 0xB9, 0x50) : Color.FromArgb(0x22, 0xF8, 0x51, 0x49)),
                    Padding = new Thickness(4, 1, 4, 1),
                    Margin = new Thickness(0, 0.5, 0, 0.5),
                    CornerRadius = new CornerRadius(2)
                };
                lineBorder.Child = new TextBlock
                {
                    Text = (added ? "+ " : "- ") + entry.Text,
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 10.5,
                    LineHeight = 14,
                    Foreground = AppTheme.Brush(added ? "#5FAF7D" : "#C96A5B"),
                    TextWrapping = TextWrapping.NoWrap
                };
                stack.Children.Add(lineBorder);
            }

            outer.Child = stack;
            return outer;
        }

        private void RefreshRightSidebarContextAndTasks()
        {
            if (ContextBreakdownRowsPanel == null)
                return;

            string folder = SelectedProjectFolder;
            if (EnvProjectPathText != null)
            {
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    string shortName = WorkplaceSessionTitleGenerator.ShortProjectLabel(folder);
                    string compactPath = folder.Length > 26 ? folder[..11] + "…" + folder[^11..] : folder;
                    EnvProjectPathText.Text = $"{shortName} · {compactPath}";
                    EnvProjectPathText.ToolTip = folder;
                }
                else
                {
                    EnvProjectPathText.Text = "No project selected";
                    EnvProjectPathText.ToolTip = "Use + -> Select Project to scope this session";
                }
            }

            if (EnvGitBranchText != null)
                EnvGitBranchText.Text = ReadGitBranchFast(folder) ?? "No git branch";

            int totalCap = Math.Max(4096, _isCloudModeEnabled
                ? _openRouterChatService.GetApproximateContextWindowTokens(GetEffectiveCouncilModelId())
                : (_isSingleModelMode ? (int)_singleModelContextSize : (int)_builderContextSize));

            int messagesTokens = Math.Max(0, EstimateWorkplaceContextTokens());
            int systemToolsTokens = _agentEnabled ? AgentToolSchemas.All().Count * 165 : 0;
            int mcpCount = _mcpConnectorService?.IsAnyConnected() == true ? _mcpConnectorService.GetActiveTools(null).Count : 0;
            int mcpToolsTokens = mcpCount * 220;
            int skillsCount = int.TryParse(WorkplaceSkillsCountText?.Text, out int sc) ? sc : 0;
            int skillsTokens = skillsCount * 280;
            int systemPromptTokens = 1100;
            int mcpInstructionsTokens = mcpCount > 0 ? 420 : 0;
            string trackerText = GetTrackerContextAfterCompaction();
            int memoryFilesTokens = string.IsNullOrWhiteSpace(trackerText) ? 0 : Math.Max(80, EstimateTokenCount(trackerText));
            int sinceLastUpdateTokens = EstimateTokenCount(QueryInput?.Text ?? string.Empty);
            int autocompactBuffer = Math.Max(512, (int)Math.Round(totalCap * 0.08));

            int usedTotal = messagesTokens + systemToolsTokens + mcpToolsTokens + skillsTokens
                + systemPromptTokens + mcpInstructionsTokens + memoryFilesTokens + sinceLastUpdateTokens;
            int freeTokens = Math.Max(0, totalCap - usedTotal - autocompactBuffer);
            double usedPercent = Math.Clamp((usedTotal / (double)totalCap) * 100.0, 0, 100);

            if (ContextUsedPercentText != null)
                ContextUsedPercentText.Text = $"{usedPercent:0}% used";
            if (ContextUsedFractionText != null)
                ContextUsedFractionText.Text = $"{FormatCompactTokenCount(usedTotal)} / {FormatCompactTokenCount(totalCap)}";

            if (CtxBarMessagesCol != null)
            {
                CtxBarMessagesCol.Width = new GridLength(Math.Max(0.1, messagesTokens), GridUnitType.Star);
                CtxBarSystemToolsCol.Width = new GridLength(Math.Max(0.1, systemToolsTokens), GridUnitType.Star);
                CtxBarMcpToolsCol.Width = new GridLength(Math.Max(0.1, mcpToolsTokens), GridUnitType.Star);
                CtxBarOverheadCol.Width = new GridLength(Math.Max(0.1, skillsTokens + systemPromptTokens + mcpInstructionsTokens + memoryFilesTokens + sinceLastUpdateTokens), GridUnitType.Star);
                CtxBarFreeCol.Width = new GridLength(Math.Max(0.1, freeTokens + autocompactBuffer), GridUnitType.Star);
            }

            ContextBreakdownRowsPanel.Children.Clear();
            void AddRow(string colorHex, string label, int tok)
            {
                double pct = (tok / (double)totalCap) * 100.0;
                var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });

                var dot = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                var nameTb = new TextBlock { Text = label, FontSize = 13, LineHeight = 19, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
                var tokTb = new TextBlock
                {
                    Text = FormatCompactTokenCount(tok),
                    FontSize = 13,
                    LineHeight = 19,
                    Foreground = AppTheme.Brush("#B0A89F"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 0, 0)
                };
                var pctTb = new TextBlock
                {
                    Text = $"{pct:0.0}%",
                    FontSize = 13,
                    LineHeight = 19,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Foreground = AppTheme.Brush("#B0A89F"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                row.Children.Add(dot);
                Grid.SetColumn(nameTb, 1); row.Children.Add(nameTb);
                Grid.SetColumn(tokTb, 2); row.Children.Add(tokTb);
                Grid.SetColumn(pctTb, 3); row.Children.Add(pctTb);
                ContextBreakdownRowsPanel.Children.Add(row);
            }

            AddRow("#3B82F6", "Messages", messagesTokens);
            AddRow("#F97316", "System tools", systemToolsTokens);
            AddRow("#10B981", "MCP tools", mcpToolsTokens);
            AddRow("#9CA3AF", "Skills", skillsTokens);
            AddRow("#9CA3AF", "System prompt", systemPromptTokens);
            AddRow("#9CA3AF", "MCP server instructions", mcpInstructionsTokens);
            AddRow("#9CA3AF", "Memory files (.md tracker)", memoryFilesTokens);
            AddRow("#9CA3AF", "Since last update", sinceLastUpdateTokens);
            AddRow("#6B7280", "Autocompact buffer", autocompactBuffer);
            AddRow("#374151", "Free space", freeTokens);

            if (CtxExpandMcpText != null)
                CtxExpandMcpText.Text = $"> MCP tools   {mcpCount}";
            if (CtxExpandMemoryText != null)
                CtxExpandMemoryText.Text = $"> Memory files   {(string.IsNullOrWhiteSpace(_activeTrackerFilePath) ? 0 : 1)} (.md)";
            if (CtxUntilCompactText != null)
            {
                int until = Math.Max(0, (int)(totalCap * 0.80) - usedTotal);
                CtxUntilCompactText.Text = $"{FormatCompactTokenCount(until)} until auto-compact";
            }

            if (RightSidebarOutputsPanel != null)
            {
                RightSidebarOutputsPanel.Children.Clear();
                if (_sessionTouchedFiles.Count == 0 && string.IsNullOrWhiteSpace(_activeTrackerFilePath))
                {
                    RightSidebarOutputsPanel.Children.Add(new TextBlock
                    {
                        Text = "No files written yet.",
                        FontSize = 13,
                        LineHeight = 19,
                        Foreground = AppTheme.Brush("#8A8279")
                    });
                }
                else
                {
                    var items = _sessionTouchedFiles.TakeLast(6).ToList();
                    if (!string.IsNullOrWhiteSpace(_activeTrackerFilePath) && File.Exists(_activeTrackerFilePath)
                        && !items.Any(p => string.Equals(p, _activeTrackerFilePath, StringComparison.OrdinalIgnoreCase)))
                    {
                        items.Insert(0, _activeTrackerFilePath);
                    }

                    foreach (string file in items)
                    {
                        string captureFile = file;
                        var fileRow = new Grid { ToolTip = captureFile };
                        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        fileRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        fileRow.Children.Add(new TextBlock
                        {
                            Text = "\uE8A5",
                            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                            FontSize = 13,
                            LineHeight = double.NaN,
                            Foreground = AppTheme.Brush("#8A8279"),
                            Margin = new Thickness(0, 0, 7, 0),
                            VerticalAlignment = VerticalAlignment.Center
                        });
                        var labelStack = new StackPanel();
                        labelStack.Children.Add(new TextBlock
                        {
                            Text = Path.GetFileName(captureFile),
                            FontSize = 13,
                            LineHeight = 18,
                            Foreground = AppTheme.Brush("#EDE8E3"),
                            TextTrimming = TextTrimming.CharacterEllipsis
                        });
                        labelStack.Children.Add(new TextBlock
                        {
                            Text = "Open in Project Canvas",
                            FontSize = 11,
                            LineHeight = 15,
                            Foreground = AppTheme.Brush("#F5D591"),
                            TextDecorations = TextDecorations.Underline
                        });
                        Grid.SetColumn(labelStack, 1);
                        fileRow.Children.Add(labelStack);

                        var fileButton = new Button
                        {
                            Content = fileRow,
                            HorizontalContentAlignment = HorizontalAlignment.Stretch,
                            Background = Brushes.Transparent,
                            BorderThickness = new Thickness(0),
                            Padding = new Thickness(2, 4, 2, 4),
                            Margin = new Thickness(0, 2, 0, 2),
                            Cursor = System.Windows.Input.Cursors.Hand,
                            ToolTip = captureFile
                        };
                        fileButton.Click += (_, _) => OpenFileInProjectCanvas(captureFile, showSidebar: true);
                        RightSidebarOutputsPanel.Children.Add(fileButton);
                    }
                }
            }

            RefreshRightSidebarBackgroundTasksOnly();
        }

        private void RefreshRightSidebarBackgroundTasksOnly()
        {
            if (RightSidebarBackgroundTasksPanel == null || RightSidebarFinishedTasksPanel == null)
                return;

            IReadOnlyList<AgentBackgroundTaskSnapshot> all = _backgroundTaskManager.ListTasks();
            var running = all.Where(t => t.IsRunning).ToList();
            var finished = all.Where(t => !t.IsRunning).ToList();

            RightSidebarBackgroundTasksPanel.Children.Clear();
            if (running.Count == 0 && finished.Count == 0)
            {
                RightSidebarBackgroundTasksPanel.Children.Add(new TextBlock
                {
                    Text = "No background tasks running.",
                    FontSize = 13,
                    LineHeight = 19,
                    Foreground = AppTheme.Brush("#8A8279")
                });
            }

            foreach (AgentBackgroundTaskSnapshot task in running)
            {
                var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = task.Command };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var termIcon = new TextBlock
                {
                    Text = ">_",
                    FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                    FontSize = 13,
                    LineHeight = 19,
                    Foreground = AppTheme.Brush("#B0A89F"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 7, 0)
                };
                var titleTb = new TextBlock
                {
                    Text = task.Title,
                    FontSize = 13,
                    LineHeight = 19,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var elapsedTb = new TextBlock
                {
                    Text = $"{Math.Max(0, (int)Math.Floor(task.Elapsed.TotalSeconds))}s",
                    FontSize = 13,
                    LineHeight = 19,
                    Foreground = AppTheme.Brush("#8A8279"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(6, 0, 6, 0)
                };
                var stopBtn = new Button
                {
                    Content = new Border
                    {
                        Width = 8,
                        Height = 8,
                        CornerRadius = new CornerRadius(1.5),
                        Background = AppTheme.Brush("#EDE8E3")
                    },
                    Width = 28,
                    Height = 28,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    ToolTip = "Stop background task",
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                string capturedId = task.Id;
                stopBtn.Click += (_, _) => _backgroundTaskManager.StopTask(capturedId);

                row.Children.Add(termIcon);
                Grid.SetColumn(titleTb, 1); row.Children.Add(titleTb);
                Grid.SetColumn(elapsedTb, 2); row.Children.Add(elapsedTb);
                Grid.SetColumn(stopBtn, 3); row.Children.Add(stopBtn);
                RightSidebarBackgroundTasksPanel.Children.Add(row);
            }

            if (BackgroundFinishedToggleButton != null && BackgroundFinishedCountText != null)
            {
                BackgroundFinishedToggleButton.Visibility = finished.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                BackgroundFinishedCountText.Text = $"{finished.Count} finished";
            }

            RightSidebarFinishedTasksPanel.Visibility = _isFinishedTasksExpanded && finished.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            RightSidebarFinishedTasksPanel.Children.Clear();
            foreach (AgentBackgroundTaskSnapshot done in finished.Take(12))
            {
                string statusText = done.WasStoppedByUser
                    ? "stopped"
                    : $"exit {done.ExitCode?.ToString() ?? "0"}";
                RightSidebarFinishedTasksPanel.Children.Add(new TextBlock
                {
                    Text = $"• {done.Title} ({statusText}, {Math.Max(0, (int)Math.Round(done.Elapsed.TotalSeconds))}s)",
                    FontSize = 13,
                    LineHeight = 19,
                    Foreground = AppTheme.Brush("#8A8279"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 1, 0, 1),
                    ToolTip = string.IsNullOrWhiteSpace(done.OutputTail) ? done.Command : done.OutputTail
                });
            }
        }

        private void BackgroundFinishedToggle_Click(object sender, RoutedEventArgs e)
        {
            _isFinishedTasksExpanded = !_isFinishedTasksExpanded;
            RefreshRightSidebarBackgroundTasksOnly();
        }

        private async void CompactSessionNow_Click(object sender, RoutedEventArgs e)
        {
            if (_isProcessing)
            {
                AppendVisibleNotice("Wait for the current task to finish before compacting context.");
                return;
            }
            EnsureSessionTrackerUpdated(_sessionTitle, "Manual context compaction requested.");
            await CompressChatHistoryIfNeededAsync(CancellationToken.None, forceCompact: true);
            RefreshRightSidebarContextAndTasks();
        }

        private void ExportContextEnvironment_Click(object sender, RoutedEventArgs e)
        {
            EnsureSessionTrackerUpdated(_sessionTitle, "Session snapshot saved.");
            RequestWorkspaceStateSave();
            AppendVisibleNotice($"Saved session tracker to {GetOrResolveTrackerFilePath()}");
        }

        private static string? ReadGitBranchFast(string? folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                    return null;

                string? dir = folderPath;
                for (int i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
                {
                    string headFile = Path.Combine(dir, ".git", "HEAD");
                    if (File.Exists(headFile))
                    {
                        string content = File.ReadAllText(headFile).Trim();
                        const string prefix = "ref: refs/heads/";
                        if (content.StartsWith(prefix, StringComparison.Ordinal))
                            return content[prefix.Length..].Trim();
                        return content.Length >= 8 ? content[..8] : content;
                    }
                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            catch { }
            return null;
        }
    }
}
