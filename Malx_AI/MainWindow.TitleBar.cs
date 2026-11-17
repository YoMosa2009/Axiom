using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private void InitializeUnifiedTitleBar()
        {
            // Keep the OS-owned caption, resizing, system menu and caption buttons.
            // The app toolbar lives entirely in the client area and measures its own height.
            if (SidebarNavigationHeader.Parent is Panel sidebar)
                sidebar.Children.Remove(SidebarNavigationHeader);
            SidebarNavigationHeader.Margin = new Thickness(0);
            TitleBarNavigationHost.Content = SidebarNavigationHeader;
            WorkplaceTitleBarHost.Content = WorkplaceViewControl.DetachHeaderForTitleBar();
        }
    }
}
