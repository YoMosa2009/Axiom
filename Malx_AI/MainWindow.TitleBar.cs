using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shell;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private void InitializeUnifiedTitleBar()
        {
            if (SidebarNavigationHeader.Parent is Panel sidebar)
                sidebar.Children.Remove(SidebarNavigationHeader);
            SidebarNavigationHeader.Margin = new Thickness(0);
            TitleBarNavigationHost.Content = SidebarNavigationHeader;
            WorkplaceTitleBarHost.Content = WorkplaceViewControl.DetachHeaderForTitleBar();
            MarkCaptionControls(UnifiedTitleBar);
            Loaded += (_, _) => MarkCaptionControls(UnifiedTitleBar);
        }

        private static void MarkCaptionControls(DependencyObject root)
        {
            if (root is ButtonBase or ComboBox or TextBox)
                WindowChrome.SetIsHitTestVisibleInChrome((IInputElement)root, true);
            // Logical children exist even while Workplace is collapsed and its control
            // templates have not been measured yet. Caption controls must be interactive
            // on the first switch to Workplace, not only after a later layout pass.
            foreach (object child in LogicalTreeHelper.GetChildren(root))
                if (child is DependencyObject dependency) MarkCaptionControls(dependency);
        }

        private void CaptionMinimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
        private void CaptionMaximize_Click(object sender, RoutedEventArgs e)
        {
            if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
            else SystemCommands.MaximizeWindow(this);
        }
        private void CaptionClose_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
    }
}
