using System;
using System.Windows;
using System.Windows.Controls;

namespace Malx_AI;

public partial class WorkplaceChangeSummary : UserControl
{
    public WorkplaceChangeSummary() => InitializeComponent();
    public event EventHandler<WorkplaceChatMessage>? ReviewRequested;
    public event EventHandler<WorkplaceChatMessage>? UndoRequested;
    public event EventHandler<WorkplaceFileChange>? FileReviewRequested;
    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkplaceChatMessage message) ReviewRequested?.Invoke(this, message);
    }
    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkplaceChatMessage message) UndoRequested?.Invoke(this, message);
    }
    private void File_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: WorkplaceFileChange change }) FileReviewRequested?.Invoke(this, change);
    }
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is WorkplaceChatMessage message) message.FileChangesExpanded = !message.FileChangesExpanded;
    }
    private void Summary_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (SummaryActions == null) return;
        bool narrow = e.NewSize.Width < 440;
        Grid.SetRow(SummaryActions, narrow ? 1 : 0);
        Grid.SetColumn(SummaryActions, narrow ? 0 : 2);
        Grid.SetColumnSpan(SummaryActions, narrow ? 3 : 1);
        Grid.SetColumnSpan(SummaryHeading, narrow ? 2 : 1);
        SummaryActions.Margin = narrow ? new Thickness(0, 9, 0, 0) : new Thickness(0);
    }
}
