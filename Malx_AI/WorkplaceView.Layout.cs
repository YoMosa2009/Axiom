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
            WorkplaceHeaderBorder.Padding = new Thickness(12, 2, 8, 2);
            WorkplaceHeaderBorder.BorderThickness = new Thickness(0);
            return WorkplaceHeaderBorder;
        }
    }
}
