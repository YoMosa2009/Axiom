using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    public partial class WorkplaceView
    {
        internal FrameworkElement DetachHeaderForTitleBar()
        {
            if (WorkplaceHeaderBorder.Parent is Panel panel)
                panel.Children.Remove(WorkplaceHeaderBorder);
            WorkplaceHeaderBorder.Padding = new Thickness(8, 2, 8, 2);
            WorkplaceHeaderBorder.BorderThickness = new Thickness(0);
            WorkplaceHeaderActions.SizeChanged += (_, e) =>
            {
                if (e.WidthChanged) UpdateHeaderActionLayout();
            };
            return WorkplaceHeaderBorder;
        }

        private void UpdateHeaderActionLayout()
        {
            if (WorkplaceHeaderActions == null || WorkplaceHeaderBorder.ActualWidth <= 0) return;
            WorkplaceHeaderActions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double actionsWidth = WorkplaceHeaderActions.DesiredSize.Width - WorkplaceHeaderActions.Margin.Left - WorkplaceHeaderActions.Margin.Right;
            bool stacked = WorkplaceHeaderBorder.ActualWidth < actionsWidth + 240;
            Grid.SetRow(WorkplaceHeaderActions, stacked ? 1 : 0);
            Grid.SetColumn(WorkplaceHeaderActions, stacked ? 0 : 1);
            Grid.SetColumnSpan(WorkplaceHeaderActions, stacked ? 2 : 1);
            WorkplaceHeaderActions.Margin = stacked ? new Thickness(0, 6, 0, 0) : new Thickness(12, 0, 0, 0);
            WorkplaceHeaderActions.HorizontalAlignment = HorizontalAlignment.Right;
        }
    }
}
