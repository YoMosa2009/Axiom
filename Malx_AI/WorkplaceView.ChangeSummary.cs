using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Malx_AI;

public partial class WorkplaceView
{
    private int _changeSummaryStartIndex;
    private string _changeSummaryCanvasBefore = "";
    private readonly List<WorkplaceFileChange> _runRecordedChanges = new();

    private void BeginRunChangeSummary()
    {
        _changeSummaryStartIndex = _chatCards.Count;
        _changeSummaryCanvasBefore = ProjectCanvasEditor?.Text ?? "";
        _runRecordedChanges.Clear();
    }

    private async void CompleteRunChangeSummary()
    {
        WorkplaceChatMessage? message = _chatCards.Skip(_changeSummaryStartIndex)
            .LastOrDefault(c => c.Role is "agent" or "builder" or "critic" or "critic-final");
        if (message == null) return;
        // Capture this run before yielding; a follow-up can start while diff work is in flight.
        var recorded = message.FileChanges.Concat(_runRecordedChanges).ToArray();
        string before = _changeSummaryCanvasBefore;
        string after = ProjectCanvasEditor?.Text ?? "";
        try
        {
            var changes = await Task.Run(() =>
            {
                var canvas = WorkplaceFileChange.FromText("axiom:canvas", before, after, canvas: true, displayPath: "Project Canvas");
                return WorkplaceFileChange.Merge(canvas == null ? recorded : recorded.Append(canvas));
            });
            message.FileChanges = changes;
            RequestWorkspaceStateSave();
            ChatScrollViewer?.ScrollToEnd();
        }
        catch (Exception ex) { await BackendLogService.LogErrorAsync("Workplace.ChangeSummary", ex); }
    }

    private void RecordAppliedPatchChanges(CodebaseUndoSnapshot before, WorkspacePatchApplyResult result)
    {
        var changed = new HashSet<string>(result.ChangedFiles, StringComparer.OrdinalIgnoreCase);
        var changes = new List<WorkplaceFileChange>();
        foreach (var file in before.Files.Where(f => changed.Contains(f.RelativePath)))
        {
            // The undo snapshot is the actual before-state; the file is read only after apply.
            try
            {
                bool exists = System.IO.File.Exists(file.TargetPath);
                WorkplaceFileChange? change;
                if (exists && new System.IO.FileInfo(file.TargetPath).Length > 512 * 1024)
                    change = new WorkplaceFileChange { Path = file.TargetPath, DisplayPath = file.RelativePath,
                        UnavailableReason = "Text diff unavailable for files larger than 512 KB." };
                else
                    change = WorkplaceFileChange.FromText(file.TargetPath, file.PreviousContent,
                        exists ? System.IO.File.ReadAllText(file.TargetPath) : "", file.Existed, exists, file.RelativePath);
                if (change != null) changes.Add(change);
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                changes.Add(new WorkplaceFileChange { Path = file.TargetPath, DisplayPath = file.RelativePath, UnavailableReason = ex.Message });
            }
        }
        if (_isProcessing) _runRecordedChanges.AddRange(changes);
        else
        {
            var message = _chatCards.LastOrDefault(c => c.Role is "agent" or "builder");
            if (message != null) message.FileChanges = WorkplaceFileChange.Merge(message.FileChanges.Concat(changes));
        }
    }

    private void CompletedChanges_Review(object? sender, WorkplaceChatMessage message) => ShowCapturedChanges(message.FileChanges);
    private void CompletedChanges_ReviewFile(object? sender, WorkplaceFileChange change) => ShowCapturedChanges(new[] { change });
    private void CompletedChanges_Undo(object? sender, WorkplaceChatMessage message)
    {
        if (ReferenceEquals(message, _agentUndoMessage) && message.CanUndoFileChanges)
            AgentUndo_Click(sender ?? this, new RoutedEventArgs());
    }

    private void ShowCapturedChanges(IReadOnlyList<WorkplaceFileChange> changes)
    {
        if (changes.Count == 0) return;
        var window = new Window { Title = "Recorded changes", Width = 980, Height = 650, MinWidth = 620, MinHeight = 380,
            Owner = Window.GetWindow(this), WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = new FontFamily("Segoe UI") };
        window.SetResourceReference(BackgroundProperty, "NotebookBackgroundBrush");
        window.SetResourceReference(ForegroundProperty, "NotebookTextBrush");
        var grid = new Grid { Margin = new Thickness(16) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var note = new TextBlock { Text = "Captured at the end of this run · Later file edits are not shown here.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14), FontSize = 12 };
        note.SetResourceReference(TextBlock.ForegroundProperty, "DarkSecondaryBrush");
        Grid.SetColumnSpan(note, 2); grid.Children.Add(note);
        var list = new ListBox { ItemsSource = changes, DisplayMemberPath = nameof(WorkplaceFileChange.DisplayPath),
            Margin = new Thickness(0, 0, 12, 0), BorderThickness = new Thickness(0), FontSize = 13 };
        list.SetResourceReference(Control.BackgroundProperty, "NotebookSurfaceBrush");
        list.SetResourceReference(Control.ForegroundProperty, "NotebookTextBrush");
        Grid.SetRow(list, 1); grid.Children.Add(list);
        var text = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12, Padding = new Thickness(12), TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        text.SetResourceReference(Control.BackgroundProperty, "NotebookSurfaceBrush");
        text.SetResourceReference(Control.ForegroundProperty, "NotebookTextBrush");
        text.SetResourceReference(Control.BorderBrushProperty, "NotebookBorderBrush");
        Grid.SetRow(text, 1); Grid.SetColumn(text, 1); grid.Children.Add(text);
        int selection = 0;
        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not WorkplaceFileChange change) return;
            int current = ++selection;
            text.Text = "Preparing captured diff…";
            string review = await Task.Run(change.BuildReviewText);
            if (selection == current) { text.Text = review; text.ScrollToHome(); }
        };
        window.Content = grid;
        list.SelectedIndex = 0;
        window.Show();
    }
}
