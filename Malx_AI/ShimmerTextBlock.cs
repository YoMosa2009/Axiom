using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Malx_AI
{
    public sealed class ShimmerTextBlock : TextBlock
    {
        public static readonly DependencyProperty BaseColorProperty =
            DependencyProperty.Register(
                nameof(BaseColor),
                typeof(Color),
                typeof(ShimmerTextBlock),
                new PropertyMetadata(Color.FromRgb(0x71, 0x71, 0x7A), OnShimmerPropertyChanged));

        public static readonly DependencyProperty HighlightColorProperty =
            DependencyProperty.Register(
                nameof(HighlightColor),
                typeof(Color),
                typeof(ShimmerTextBlock),
                new PropertyMetadata(Colors.White, OnShimmerPropertyChanged));

        public static readonly DependencyProperty ShimmerDurationSecondsProperty =
            DependencyProperty.Register(
                nameof(ShimmerDurationSeconds),
                typeof(double),
                typeof(ShimmerTextBlock),
                new PropertyMetadata(2.0, OnShimmerPropertyChanged));

        private readonly TranslateTransform _shimmerTransform = new(-1.15, 0);

        public Color BaseColor
        {
            get => (Color)GetValue(BaseColorProperty);
            set => SetValue(BaseColorProperty, value);
        }

        public Color HighlightColor
        {
            get => (Color)GetValue(HighlightColorProperty);
            set => SetValue(HighlightColorProperty, value);
        }

        public double ShimmerDurationSeconds
        {
            get => (double)GetValue(ShimmerDurationSecondsProperty);
            set => SetValue(ShimmerDurationSecondsProperty, value);
        }

        public ShimmerTextBlock()
        {
            Loaded += (_, _) => StartShimmer();
            Unloaded += (_, _) => StopShimmer();
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible)
                    StartShimmer();
                else
                    StopShimmer();
            };
        }

        private static void OnShimmerPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ShimmerTextBlock textBlock)
                textBlock.StartShimmer();
        }

        private void StartShimmer()
        {
            if (!IsLoaded || !IsVisible)
                return;

            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
                SpreadMethod = GradientSpreadMethod.Pad,
                RelativeTransform = _shimmerTransform
            };

            brush.GradientStops.Add(new GradientStop(BaseColor, 0.0));
            brush.GradientStops.Add(new GradientStop(BaseColor, 0.38));
            brush.GradientStops.Add(new GradientStop(HighlightColor, 0.50));
            brush.GradientStops.Add(new GradientStop(BaseColor, 0.62));
            brush.GradientStops.Add(new GradientStop(BaseColor, 1.0));
            Foreground = brush;

            _shimmerTransform.X = -1.15;
            _shimmerTransform.BeginAnimation(
                TranslateTransform.XProperty,
                new DoubleAnimation
                {
                    From = -1.15,
                    To = 1.15,
                    Duration = TimeSpan.FromSeconds(Math.Clamp(ShimmerDurationSeconds, 0.8, 6.0)),
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }

        private void StopShimmer()
        {
            _shimmerTransform.BeginAnimation(TranslateTransform.XProperty, null);
            Foreground = new SolidColorBrush(BaseColor);
        }
    }
}
