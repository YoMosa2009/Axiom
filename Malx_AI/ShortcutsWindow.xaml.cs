using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Malx_AI
{
    /// <summary>The Ctrl+/ cheatsheet, built from <see cref="KeyboardShortcutCatalog"/>.</summary>
    public partial class ShortcutsWindow : Window
    {
        public ShortcutsWindow()
        {
            InitializeComponent();
            WindowTitleBarTheme.Apply(this);
            Build();
        }

        private void Build()
        {
            foreach (var group in KeyboardShortcutCatalog.All.GroupBy(s => s.Group))
            {
                ShortcutRows.Children.Add(new TextBlock
                {
                    Text = group.Key.ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, 12, 0, 6),
                }.WithBrush(TextBlock.ForegroundProperty, "MutedTextBrush"));

                foreach (var item in group)
                {
                    var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                    row.ColumnDefinitions.Add(new ColumnDefinition());
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    row.Children.Add(new TextBlock
                    {
                        Text = item.Description,
                        FontSize = 12.5,
                        VerticalAlignment = VerticalAlignment.Center,
                    });

                    var chip = new Border
                    {
                        CornerRadius = new CornerRadius(5),
                        BorderThickness = new Thickness(1),
                        Padding = new Thickness(8, 2, 8, 2),
                        Child = new TextBlock { Text = item.Keys, FontSize = 11.5, FontWeight = FontWeights.Medium },
                    };
                    chip.SetResourceReference(Border.BackgroundProperty, "SurfaceRaisedBrush");
                    chip.SetResourceReference(Border.BorderBrushProperty, "NotebookBorderBrush");
                    Grid.SetColumn(chip, 1);
                    row.Children.Add(chip);

                    ShortcutRows.Children.Add(row);
                }
            }
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
                DragMove();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void Window_Deactivated(object? sender, System.EventArgs e)
        {
            if (IsLoaded)
                Close();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }
    }

    internal static class ShortcutsWindowExtensions
    {
        public static T WithBrush<T>(this T element, DependencyProperty property, string resourceKey)
            where T : FrameworkElement
        {
            element.SetResourceReference(property, resourceKey);
            return element;
        }
    }
}
