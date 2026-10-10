using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Malx_AI
{
    /// <summary>
    /// Shows a <see cref="GenerationMonitor"/> as "31 tok/s · 412 tokens" in a view's header, the
    /// same way for Local, Cloud and Hybrid Local. Refreshes a few times a second while a reply
    /// streams and stays on the last reply's numbers afterwards.
    /// </summary>
    internal sealed class GenerationReadout
    {
        private readonly GenerationMonitor _monitor;
        private readonly FrameworkElement _container;
        private readonly TextBlock _title;
        private readonly TextBlock _value;
        private readonly DispatcherTimer _timer;

        public GenerationReadout(GenerationMonitor monitor, FrameworkElement container, TextBlock title, TextBlock value)
        {
            _monitor = monitor;
            _container = container;
            _title = title;
            _value = value;
            _timer = new DispatcherTimer(DispatcherPriority.Background, container.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _timer.Tick += (_, _) => Refresh();
            _monitor.LiveChanged += () => _container.Dispatcher.BeginInvoke(OnLiveChanged, DispatcherPriority.Background);
            Refresh();
        }

        private void OnLiveChanged()
        {
            (_, _, bool live) = _monitor.Read();
            if (live)
                _timer.Start();
            else
                _timer.Stop();
            Refresh();
        }

        public void Refresh()
        {
            (GenerationStatsSnapshot? stats, string mode, bool live) = _monitor.Read();
            if (stats == null)
            {
                if (live)
                {
                    _title.Text = "Speed";
                    _value.Text = "waiting…";
                    _container.ToolTip = $"Waiting for the first text from {DescribeMode(mode)}.";
                    _container.Visibility = Visibility.Visible;
                }
                else if (_value.Text.Length == 0)
                {
                    _container.Visibility = Visibility.Collapsed;
                }
                return;
            }

            _title.Text = live ? "Speed" : "Last reply";
            _value.Text = GenerationStatsFormatter.FormatSummary(stats);
            _container.ToolTip = live
                ? $"Replying now ({mode}).\n" + GenerationStatsFormatter.FormatTooltip(stats, mode).Replace("Last reply", "So far", StringComparison.Ordinal)
                : GenerationStatsFormatter.FormatTooltip(stats, mode);
            _container.Visibility = Visibility.Visible;
        }

        private static string DescribeMode(string mode) => mode switch
        {
            "Local" => "the local model",
            "Hybrid Local" => "your Hybrid Local server",
            _ => "the cloud model"
        };
    }
}
