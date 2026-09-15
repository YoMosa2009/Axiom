using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Threading;
using System.Windows;
using System.Windows.Input;

namespace Malx_AI.ComputerUse
{
    public partial class ComputerUseSessionWindow : Window
    {
        private TaskCompletionSource<bool>? _permissionSource;
        private readonly ObservableCollection<ComputerUseLogEntry> _logEntries = new();
        // A model turn is the only part of a session with no visible progress of its own. Without
        // a ticking counter a slow local server and a hung one look identical -- a motionless
        // window with a single unchanging line -- and the run reads as broken either way.
        private readonly System.Windows.Threading.DispatcherTimer _waitTimer =
            new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _waitStartedUtc;
        private string _waitLabel = "";
        private bool _waitHintShown;

        public ComputerUseSessionWindow()
        {
            InitializeComponent();
            LogEntries.ItemsSource = _logEntries;
            Loaded += ComputerUseSessionWindow_Loaded;
            _waitTimer.Tick += WaitTimer_Tick;
            Closed += (_, _) => _waitTimer.Stop();
        }

        /// <summary>Shows <paramref name="label"/> with a live elapsed time until <see cref="EndWait"/>.</summary>
        public void BeginWait(string label)
        {
            _waitLabel = string.IsNullOrWhiteSpace(label) ? "Working" : label.Trim();
            _waitStartedUtc = DateTime.UtcNow;
            _waitHintShown = false;
            SetStatus(_waitLabel);
            _waitTimer.Start();
        }

        public void EndWait() => _waitTimer.Stop();

        private void WaitTimer_Tick(object? sender, EventArgs e)
        {
            int seconds = (int)(DateTime.UtcNow - _waitStartedUtc).TotalSeconds;
            StatusText.Text = $"{_waitLabel} · {seconds}s";
            // Said once, not every tick. A local server's first request can genuinely take minutes
            // while it loads the model, and the user needs to know waiting is expected.
            if (!_waitHintShown && seconds >= 45)
            {
                _waitHintShown = true;
                AppendLog($"Still waiting for the model to respond ({seconds}s). A local server can take a "
                    + "while on its first request; press Stop to cancel.");
            }
        }

        public ComputerUseMode Mode => AutopilotModeRadio.IsChecked == true
            ? ComputerUseMode.Autopilot
            : ComputerUseMode.Ask;

        public event Action? StopRequested;
        public event Action<ComputerUseMode>? ModeChanged;

        public void SetStatus(string status)
        {
            StatusText.Text = string.IsNullOrWhiteSpace(status) ? "Idle" : status.Trim();
        }

        // Bounded so a long session cannot grow the panel without limit; the oldest entries fall
        // off the top exactly as the old character cap did.
        private const int MaxVisibleLogEntries = 60;

        public void AppendLog(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            // A controller observation can carry several tagged lines at once. Each becomes its own
            // entry so every status word gets its own badge, rather than one badge for a paragraph.
            foreach (string part in line.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string text = part.Trim();
                if (text.Length > 0)
                    _logEntries.Add(ComputerUseLogEntry.Create(text));
            }

            while (_logEntries.Count > MaxVisibleLogEntries)
                _logEntries.RemoveAt(0);

            EmptyLogHint.Visibility = _logEntries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            LogScroll.ScrollToEnd();
        }

        public void ClearPermission()
        {
            PermissionPanel.Visibility = Visibility.Collapsed;
            PendingActionText.Text = string.Empty;
            _permissionSource?.TrySetResult(false);
            _permissionSource = null;
        }

        public Task<bool> RequestPermissionAsync(string explanation)
        {
            _permissionSource?.TrySetResult(false);
            _permissionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            PendingActionText.Text = explanation;
            PermissionPanel.Visibility = Visibility.Visible;
            Activate();
            return _permissionSource.Task;
        }

        public async Task<bool> RequestRecoveryAsync(string reason, CancellationToken token)
        {
            SetStatus("Paused — model unavailable; progress retained");
            Task<bool> choice = RequestPermissionAsync(reason + "\nNo action will be repeated. Retry resumes from a fresh screenshot.");
            PermissionHeading.Text = "Model response interrupted";
            AllowActionButton.Content = "Retry";
            DenyActionButton.Content = "Stop";
            try { return await choice.WaitAsync(token); }
            finally
            {
                ClearPermission();
                PermissionHeading.Text = "Permission needed";
                AllowActionButton.Content = "Allow";
                DenyActionButton.Content = "Deny";
            }
        }

        private void ComputerUseSessionWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Rect area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left + 16, area.Right - Width - 20);
            Top = Math.Max(area.Top + 16, area.Top + 28);
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed)
                return;
            try { DragMove(); } catch { }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _permissionSource?.TrySetResult(false);
            StopRequested?.Invoke();
        }

        private void AllowButton_Click(object sender, RoutedEventArgs e)
        {
            PermissionPanel.Visibility = Visibility.Collapsed;
            _permissionSource?.TrySetResult(true);
            _permissionSource = null;
        }

        private void DenyButton_Click(object sender, RoutedEventArgs e)
        {
            PermissionPanel.Visibility = Visibility.Collapsed;
            _permissionSource?.TrySetResult(false);
            _permissionSource = null;
        }

        private void ModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            ModeChanged?.Invoke(Mode);
        }
    }
}
