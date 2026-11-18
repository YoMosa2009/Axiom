using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Malx_AI;

public partial class MainWindow
{
    private readonly DispatcherTimer _openRouterUsageRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private DateTimeOffset? _openRouterUsageFetchedAt;
    private CancellationTokenSource? _openRouterUsageCancellation;
    private int _openRouterUsageGeneration;

    private void InitializeOpenRouterUsageRefresh()
    {
        SettingsPageCloud.IsVisibleChanged += (_, _) => UpdateOpenRouterUsagePolling();
        _openRouterUsageRefreshTimer.Tick += (_, _) => _ = EnsureOpenRouterUsageLoadedAsync();
    }

    private void UpdateOpenRouterUsagePolling()
    {
        if (SettingsPageCloud.IsVisible && _openRouterChatService.HasValidKey)
        {
            _openRouterUsageRefreshTimer.Start();
            _ = EnsureOpenRouterUsageLoadedAsync();
        }
        else _openRouterUsageRefreshTimer.Stop();
    }

    private void StopOpenRouterUsageRefresh()
    {
        _openRouterUsageRefreshTimer.Stop();
        _openRouterUsageGeneration++;
        _openRouterUsageCancellation?.Cancel();
    }

    private async Task EnsureOpenRouterUsageLoadedAsync()
    {
        if (!_openRouterChatService.HasValidKey) { ResetOpenRouterUsageUi(); return; }
        if (!_isFetchingOpenRouterUsage && !OpenRouterKeyUsage.IsFresh(_openRouterUsageFetchedAt, DateTimeOffset.UtcNow))
            await LoadOpenRouterUsageAsync();
    }

    private async Task LoadOpenRouterUsageAsync()
    {
        if (!_openRouterChatService.HasValidKey) { ResetOpenRouterUsageUi(); return; }
        if (_isFetchingOpenRouterUsage) return;
        int generation = ++_openRouterUsageGeneration;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _openRouterUsageCancellation = cancellation;
        _isFetchingOpenRouterUsage = true;
        RefreshOpenRouterUsageButton.IsEnabled = false;
        OpenRouterUsageText.Text = "Checking usage…";
        _openRouterUsageRemainingPercent = 0;
        SetOpenRouterUsageHint(string.Empty);
        UpdateOpenRouterUsageProgressBar();
        UpdateOpenRouterUsageVisibility();
        try
        {
            var info = await _openRouterChatService.FetchKeyInfoAsync(cancellation.Token);
            // Clearing/replacing a key invalidates an older in-flight response.
            if (generation == _openRouterUsageGeneration && _openRouterChatService.HasValidKey)
                ApplyOpenRouterUsage(info);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"OpenRouter usage refresh error: {ex.GetType().Name}");
            if (generation == _openRouterUsageGeneration) ApplyOpenRouterUsage(new(0, 0, false));
        }
        finally
        {
            if (generation == _openRouterUsageGeneration)
            {
                _openRouterUsageCancellation = null;
                _isFetchingOpenRouterUsage = false;
                RefreshOpenRouterUsageButton.IsEnabled = true;
                UpdateOpenRouterUsageVisibility();
            }
        }
    }

    private void ApplyOpenRouterUsage(OpenRouterKeyInfo info)
    {
        _openRouterUsageRemainingPercent = OpenRouterKeyUsage.RemainingPercent(info);
        OpenRouterUsageText.Text = OpenRouterKeyUsage.Label(info);
        _openRouterUsageFetchedAt = info.FetchSucceeded && info.HasDailyRequestUsage ? DateTimeOffset.UtcNow : null;
        string hint = !info.FetchSucceeded ? "Unable to refresh usage from OpenRouter. Try refreshing again." :
            !info.HasDailyRequestUsage ? "OpenRouter did not report a daily request counter. Remaining usage cannot be calculated." :
            $"{info.RequestsUsed:N0} / {info.RequestsLimit:N0} used today · resets at 00:00 UTC. Updated {_openRouterUsageFetchedAt:HH:mm:ss} UTC.";
        if (info.FetchSucceeded && info.HasDailyRequestUsage && info.RequestsRemaining == 0)
            hint = "Daily free-model quota exhausted. Resets at 00:00 UTC. " + hint;
        SetOpenRouterUsageHint(hint, info.HasDailyRequestUsage && info.RequestsRemaining == 0);
        System.Windows.Automation.AutomationProperties.SetHelpText(OpenRouterUsageTrack, OpenRouterUsageText.Text + ". " + hint);
        UpdateOpenRouterUsageProgressBar();
    }

    private void SetOpenRouterUsageHint(string hint, bool warning = false)
    {
        OpenRouterUsageHintText.Text = hint;
        OpenRouterUsageHintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        OpenRouterUsageHintText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "DarkSecondaryBrush");
    }

    private void ResetOpenRouterUsageUi()
    {
        _openRouterUsageGeneration++;
        _openRouterUsageCancellation?.Cancel();
        _openRouterUsageCancellation = null;
        _isFetchingOpenRouterUsage = false;
        _openRouterUsageFetchedAt = null;
        _openRouterUsageRemainingPercent = 0;
        OpenRouterUsageText.Text = "No usage data yet";
        RefreshOpenRouterUsageButton.IsEnabled = true;
        SetOpenRouterUsageHint(string.Empty);
        System.Windows.Automation.AutomationProperties.SetHelpText(OpenRouterUsageTrack, "Usage has not been fetched.");
        UpdateOpenRouterUsageProgressBar();
        UpdateOpenRouterUsageVisibility();
    }

    private void UpdateOpenRouterUsageVisibility()
    {
        OpenRouterUsagePanel.Visibility = _openRouterChatService.HasValidKey ? Visibility.Visible : Visibility.Collapsed;
        if (!_openRouterChatService.HasValidKey) _openRouterUsageRefreshTimer.Stop();
        else if (SettingsPageCloud.IsVisible) _openRouterUsageRefreshTimer.Start();
    }

    private void UpdateOpenRouterUsageProgressBar()
    {
        if (OpenRouterUsageFillBar == null || OpenRouterUsageTrack == null) return;
        double width = Math.Max(0, OpenRouterUsageTrack.ActualWidth - 4);
        OpenRouterUsageFillBar.Width = width * (Math.Clamp(_openRouterUsageRemainingPercent, 0, 100) / 100);
    }

    private void OpenRouterChatService_TokenUsageRecorded(OpenRouterTokenUsage usage)
    {
        if (_openRouterUsageRefreshThrottle.TryBegin(DateTime.UtcNow, _openRouterChatService.HasValidKey))
            _ = RefreshOpenRouterUsageAfterResponseAsync();
    }

    private async Task RefreshOpenRouterUsageAfterResponseAsync()
    {
        try
        {
            if (Dispatcher.CheckAccess()) await LoadOpenRouterUsageAsync();
            else await Dispatcher.InvokeAsync(LoadOpenRouterUsageAsync).Task.Unwrap();
        }
        catch (Exception ex) { Debug.WriteLine($"OpenRouter usage observer error: {ex.GetType().Name}"); }
        finally { _openRouterUsageRefreshThrottle.Complete(); }
    }
}
