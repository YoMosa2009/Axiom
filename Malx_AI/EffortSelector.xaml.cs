using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Malx_AI
{
    public partial class EffortSelector : UserControl
    {
        private const double ThumbDiameter = 30;
        private bool _loaded;
        private bool _isDragging;
        private Window? _hostWindow;

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
            _isDragging = false;
            if (SliderHitTarget.IsMouseCaptured)
                SliderHitTarget.ReleaseMouseCapture();
            if (_hostWindow != null)
                _hostWindow.Deactivated -= HostWindow_Deactivated;
            _hostWindow = null;
            EffortPreferenceStore.Changed -= OnEffortChanged;
        }

        private void HostWindow_Deactivated(object? sender, EventArgs e)
        {
            // A WPF Popup owns a separate top-level window. Dismissing it on deactivation keeps
            // the control from floating above the app the user just switched to.
            EffortPopup.IsOpen = false;
        }

        private void ToggleButton_Click(object sender, RoutedEventArgs e)
            => EffortPopup.IsOpen = true;

        private void CloseButton_Click(object sender, RoutedEventArgs e)
            => EffortPopup.IsOpen = false;

        private void SliderHitTarget_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            SliderHitTarget.CaptureMouse();
            SetEffortFromPointer(e.GetPosition(SliderHitTarget));
            e.Handled = true;
        }

        private void SliderHitTarget_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging)
                return;

            if (e.LeftButton != MouseButtonState.Pressed)
            {
                StopDragging();
                return;
            }

            SetEffortFromPointer(e.GetPosition(SliderHitTarget));
        }

        private void SliderHitTarget_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isDragging)
                SetEffortFromPointer(e.GetPosition(SliderHitTarget));
            StopDragging();
            e.Handled = true;
        }

        private void SliderHitTarget_LostMouseCapture(object sender, MouseEventArgs e)
            => _isDragging = false;

        private void SliderSurface_SizeChanged(object sender, SizeChangedEventArgs e)
            => Refresh(EffortPreferenceStore.Current);

        private void StopDragging()
        {
            _isDragging = false;
            if (SliderHitTarget.IsMouseCaptured)
                SliderHitTarget.ReleaseMouseCapture();
        }

        private void SetEffortFromPointer(Point point)
        {
            double usableWidth = Math.Max(1, SliderHitTarget.ActualWidth - ThumbDiameter);
            double normalized = Math.Clamp((point.X - (ThumbDiameter / 2d)) / usableWidth, 0d, 1d);
            EffortLevel level = (EffortLevel)(int)Math.Round(normalized * 4d, MidpointRounding.AwayFromZero);
            EffortPreferenceStore.Set(level);
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
            ToggleButton.ToolTip = $"Effort: {name}. Controls reasoning, generation, and tool budgets.";
            LevelText.Text = name;
            LevelDescription.Text = level switch
            {
                EffortLevel.Light => "Fast and direct for simple requests.",
                EffortLevel.Medium => "Balanced depth for everyday tasks.",
                EffortLevel.High => "Plans and verifies involved work.",
                EffortLevel.ExtraHigh => "More planning, tools, and checks.",
                EffortLevel.Ultra => "Maximum planning, checks, and recovery.",
                _ => string.Empty
            };

            double trackWidth = Math.Max(ThumbDiameter, SliderHitTarget.ActualWidth);
            double travel = Math.Max(0, trackWidth - ThumbDiameter);
            double position = (int)level * travel / 4d;
            Thumb.Margin = new Thickness(position, 0, 0, 0);
            FillTrack.Width = Math.Clamp(position + (ThumbDiameter / 2d), ThumbDiameter / 2d, trackWidth);

            bool ultra = level == EffortLevel.Ultra;
            Color accent = ultra ? Color.FromRgb(188, 95, 22) : Color.FromRgb(189, 122, 33);
            Color textAccent = ultra ? Color.FromRgb(250, 185, 69) : Color.FromRgb(226, 165, 67);
            FillTrack.Background = new SolidColorBrush(accent);
            LevelAccent.Background = new SolidColorBrush(textAccent);
            LevelText.Foreground = new SolidColorBrush(textAccent);
            PopupBorder.BorderBrush = new SolidColorBrush(ultra ? Color.FromRgb(137, 75, 27) : Color.FromRgb(98, 87, 74));

            if (ultra)
            {
                UltraTexture.BeginAnimation(OpacityProperty, new DoubleAnimation(0.2, 0.9, TimeSpan.FromMilliseconds(560))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever
                });
            }
            else
            {
                UltraTexture.BeginAnimation(OpacityProperty, null);
                UltraTexture.Opacity = 0;
            }
        }
    }
}
