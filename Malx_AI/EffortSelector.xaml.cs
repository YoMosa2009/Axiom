using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Malx_AI
{
    public partial class EffortSelector : UserControl
    {
        private bool _loaded;
        private bool _wasOpenBeforeClick;
        private Window? _hostWindow;

        /// <summary>Hides the "Effort ·" caption so only the level shows, for a narrow composer.</summary>
        public bool IsCompact
        {
            get => EffortCaptionText.Visibility != Visibility.Visible;
            set
            {
                Visibility captionVisibility = value ? Visibility.Collapsed : Visibility.Visible;
                EffortCaptionText.Visibility = captionVisibility;
                EffortCaptionSeparator.Visibility = captionVisibility;
                MinWidth = value ? 0 : 96;
                ToggleContentGrid.MinWidth = value ? 0 : 78;
            }
        }

        public EffortSelector()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            _hostWindow = Window.GetWindow(this);
            if (_hostWindow != null)
                _hostWindow.Deactivated += HostWindow_Deactivated;
            EffortPreferenceStore.Changed += OnEffortChanged;
            Refresh(EffortPreferenceStore.Current);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            if (_hostWindow != null)
                _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow = null;
            EffortPreferenceStore.Changed -= OnEffortChanged;
        }

        private void HostWindow_Deactivated(object? sender, EventArgs e)
            => EffortPopup.IsOpen = false;

        private void ToggleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _wasOpenBeforeClick = EffortPopup.IsOpen;
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            EffortPopup.IsOpen = !_wasOpenBeforeClick;
            _wasOpenBeforeClick = false;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => EffortPopup.IsOpen = false;

        private void OptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tagText && int.TryParse(tagText, out int idx))
            {
                EffortLevel level = (EffortLevel)Math.Clamp(idx, 0, 4);
                EffortPreferenceStore.Set(level);
            }
            EffortPopup.IsOpen = false;
        }

        private void OnEffortChanged(EffortLevel level)
        {
            if (_loaded)
                Dispatcher.InvokeAsync(() => Refresh(level));
        }

        private void Refresh(EffortLevel level)
        {
            string name = EffortPolicy.DisplayName(level);
            ButtonLevelText.Text = name;
            ToggleButton.ToolTip = $"Effort: {name}. Click to switch level.";

            int activeBars = (int)level + 1;
            Brush activeBrush = FindResourceBrush("PrimaryAccentBrush", Colors.Goldenrod);
            Brush inactiveBrush = FindResourceBrush("BorderStrongBrush", Color.FromRgb(75, 66, 57));
            Bar1.Background = activeBars >= 1 ? activeBrush : inactiveBrush;
            Bar2.Background = activeBars >= 2 ? activeBrush : inactiveBrush;
            Bar3.Background = activeBars >= 3 ? activeBrush : inactiveBrush;
            Bar4.Background = activeBars >= 4 ? activeBrush : inactiveBrush;
            Bar5.Background = activeBars >= 5 ? activeBrush : inactiveBrush;

            Brush selectedBg = FindResourceBrush("AccentSoftBrush", Color.FromRgb(58, 50, 38));
            Brush selectedBorder = FindResourceBrush("PrimaryAccentBrush", Colors.Goldenrod);
            Button[] options = [OptionLight, OptionMedium, OptionHigh, OptionExtraHigh, OptionUltra];
            for (int i = 0; i < options.Length; i++)
            {
                bool selected = i == (int)level;
                options[i].Background = selected ? selectedBg : Brushes.Transparent;
                options[i].BorderBrush = selected ? selectedBorder : Brushes.Transparent;
            }
        }

        private Brush FindResourceBrush(string key, Color fallback)
        {
            if (TryFindResource(key) is Brush brush)
                return brush;
            return new SolidColorBrush(fallback);
        }
    }
}
