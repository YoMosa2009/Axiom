using System;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private int _subscriptionRefreshVersion;
        private readonly Uri? _axiomAccountEndpoint = AxiomAccountClient.ConfiguredEndpoint();
        private readonly OpenRouterChatService _axiomBillingTransport = new();
        private AxiomDesktopSession? _axiomAccountSession;
        private AxiomSubscriptionStatus? _lastAxiomSubscriptionStatus;
        private bool _useAxiomAccountEndpoint;
        private bool _recoveringAxiomAccount;
        private string? _pendingAxiomRecoveryKey;
        private bool _cloudSuggestionDismissedThisSession;

        private bool HasActivePaidAxiomSubscription =>
            _lastAxiomSubscriptionStatus is { } status
            && status.Balance.AllowanceMicros > 0
            && !string.Equals(status.BillingStatus, "revoked", StringComparison.OrdinalIgnoreCase);

        private OpenRouterChatService SubscriptionTransport
            => _axiomAccountSession is not null ? _axiomBillingTransport : _openRouterChatService;

        private void RestoreAxiomAccount()
        {
            if (_axiomAccountEndpoint is null || _database is null)
                return;
            _axiomAccountSession = _database.LoadAxiomAccount(_axiomAccountEndpoint);
            if (_axiomAccountSession is { } session)
            {
                _axiomBillingTransport.SetManagedEndpoint(_axiomAccountEndpoint.AbsoluteUri, session.Token);
                if (_database.GetSetting(DatabaseService.UseAxiomAccountSettingKey) == "1")
                    ApplyAxiomAccountEndpoint(true);
                _ = RefreshAxiomSubscriptionAsync();
            }
            else
            {
                RenderAxiomUsageUi(null);
            }
            RenderAxiomAccount();
        }

        private void RenderAxiomAccount()
        {
            if (AxiomAccountPanel is null)
                return;

            bool hasEndpoint = _axiomAccountEndpoint is not null;
            if (AxiomAccountUnconfiguredNotice is not null)
                AxiomAccountUnconfiguredNotice.Visibility = hasEndpoint ? Visibility.Collapsed : Visibility.Visible;
            if (!hasEndpoint)
            {
                AxiomAccountPanel.Visibility = Visibility.Collapsed;
                return;
            }

            AxiomAccountPanel.Visibility = Visibility.Visible;
            bool signedIn = _axiomAccountSession is not null;
            AxiomAccountSignInForm.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
            AxiomAccountActiveActions.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
            AxiomAccountMessage.Text = _axiomAccountSession is { } session
                ? $"Signed in as {session.Email}"
                : "Sign in with your email to subscribe or restore an existing Axiom Cloud plan.";
            AxiomAccountEndpointToggle.Content = _useAxiomAccountEndpoint
                ? "Stop using Axiom subscription"
                : "Use Axiom subscription (DeepSeek 4.1F-1017)";
            AxiomRecoveryKeyEntry.Visibility = _recoveringAxiomAccount ? Visibility.Visible : Visibility.Collapsed;
            AxiomConfirmAccountButton.Content = _recoveringAxiomAccount ? "Recover account" : "Create account or sign in";
            AxiomRecoveryToggle.Content = _recoveringAxiomAccount ? "Back to sign-in" : "Recover with key";
            CopyAxiomRecoveryKeyButton.Content = _pendingAxiomRecoveryKey is null
                ? "Copy new recovery key" : "Retry copying recovery key";
        }

        private bool CanChangeAxiomAccount()
        {
            if (!_isProcessing && WorkplaceViewControl?.HasActiveWork != true)
                return true;
            AxiomAccountMessage.Text = "Wait for the current reply or Workplace run to finish before switching accounts.";
            return false;
        }

        private void ApplyAxiomAccountEndpoint(bool enabled)
        {
            _useAxiomAccountEndpoint = enabled;
            if (enabled && _axiomAccountEndpoint is not null && _axiomAccountSession is { } session)
            {
                _openRouterChatService.SetManagedEndpoint(_axiomAccountEndpoint.AbsoluteUri, session.Token);
                _cloudModeActive = true;
                SelectCloudModel(OpenRouterChatService.AxiomManagedModelId);
            }
            else
            {
                _openRouterChatService.SetManagedEndpoint(string.Empty, string.Empty);
                if (IsManagedEndpointModelSelected())
                {
                    if (_openRouterChatService.HasValidKey)
                    {
                        SelectCloudModel(OpenRouterChatService.DefaultModelId);
                    }
                    else if (_openRouterChatService.HasValidCustomEndpoint)
                    {
                        SelectCloudModel(OpenRouterChatService.CustomEndpointModelId);
                    }
                    else
                    {
                        _cloudModeActive = false;
                        _selectedOpenRouterModelId = OpenRouterChatService.DefaultModelId;
                        _database?.SaveSetting(OpenRouterModelSettingKey, _selectedOpenRouterModelId);
                    }
                }
            }
            RefreshCloudModeToggleUi();
            UpdateHeaderDisplay();
        }

        private void ToggleAxiomAccountEndpoint_Click(object sender, RoutedEventArgs e)
        {
            if (!CanChangeAxiomAccount() || _axiomAccountSession is null)
                return;
            bool nextEnabled = !_useAxiomAccountEndpoint;
            _database?.SaveSetting(DatabaseService.UseAxiomAccountSettingKey, nextEnabled ? "1" : "0");
            ApplyAxiomAccountEndpoint(nextEnabled);
            WorkplaceViewControl?.RefreshCustomEndpointConfiguration();
            RenderAxiomAccount();
        }

        private async void SendAxiomAccountCode_Click(object sender, RoutedEventArgs e)
        {
            if (_axiomAccountEndpoint is null)
                return;
            string address = AxiomAccountEmailBox.Text.Trim();
            await RunAxiomAccountActionAsync(async client =>
            {
                var result = await client.RequestCodeAsync(address);
                AxiomAccountMessage.Text = result switch
                {
                    HttpStatusCode.Accepted => "Check your inbox for the sign-in code. It expires in 15 minutes.",
                    HttpStatusCode.BadRequest => "Enter a valid email address.",
                    HttpStatusCode.TooManyRequests => "Too many sign-in requests. Try again in a minute.",
                    _ => "Email delivery is unavailable. Try again shortly."
                };
            });
        }

        private async void ConfirmAxiomAccountCode_Click(object sender, RoutedEventArgs e)
        {
            if (!CanChangeAxiomAccount() || _axiomAccountEndpoint is null)
                return;
            string address = AxiomAccountEmailBox.Text.Trim();
            string code = AxiomAccountCodeBox.Password.Trim();
            bool recovery = _recoveringAxiomAccount;
            string recoveryKey = recovery ? AxiomRecoveryKeyBox.Password.Trim() : string.Empty;
            AxiomAccountCodeBox.Clear();
            await RunAxiomAccountActionAsync(async client =>
            {
                var session = recovery
                    ? await client.RecoverAsync(address, code, recoveryKey)
                    : await client.SignInAsync(address, code);
                if (session is null)
                {
                    AxiomAccountMessage.Text = recovery
                        ? "Recovery failed. Use your saved key and verify a new email that isn’t in use by another Axiom account."
                        : "That code is invalid or expired. Request a new code.";
                    return;
                }
                _recoveringAxiomAccount = false;
                AxiomRecoveryKeyBox.Clear();
                _axiomAccountSession = session;
                _database?.SaveAxiomAccount(_axiomAccountEndpoint, session);
                _axiomBillingTransport.SetManagedEndpoint(_axiomAccountEndpoint.AbsoluteUri, session.Token);
                if (_useAxiomAccountEndpoint)
                {
                    _openRouterChatService.SetManagedEndpoint(_axiomAccountEndpoint.AbsoluteUri, session.Token);
                    WorkplaceViewControl?.RefreshCustomEndpointConfiguration();
                }
                RenderAxiomAccount();
                if (recovery)
                    AxiomAccountMessage.Text = $"Account recovered as {session.Email}. Copy a new recovery key and save it privately.";
                await RefreshAxiomSubscriptionAsync();
            });
        }

        private void ToggleAxiomRecovery_Click(object sender, RoutedEventArgs e)
        {
            _recoveringAxiomAccount = !_recoveringAxiomAccount;
            AxiomAccountCodeBox.Clear();
            AxiomRecoveryKeyBox.Clear();
            RenderAxiomAccount();
            if (_recoveringAxiomAccount)
                AxiomAccountMessage.Text = "Enter your new email and request a code, then enter that code and your saved recovery key.";
        }

        private async void CopyAxiomRecoveryKey_Click(object sender, RoutedEventArgs e)
        {
            if (_axiomAccountSession is not { } session)
                return;
            if (_pendingAxiomRecoveryKey is null
                && MessageBox.Show(this, "A new recovery key replaces any previous one. Save it privately in a password manager.",
                    "New Axiom recovery key", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return;

            await RunAxiomAccountActionAsync(async client =>
            {
                _pendingAxiomRecoveryKey ??= await client.IssueRecoveryKeyAsync(session.Token);
                if (_pendingAxiomRecoveryKey is null)
                {
                    AxiomAccountMessage.Text = "Sign out and sign in again to create a recovery key.";
                    return;
                }

                try
                {
                    Clipboard.SetText(_pendingAxiomRecoveryKey);
                    _pendingAxiomRecoveryKey = null;
                    AxiomAccountMessage.Text = "Recovery key copied. Save it privately. It can be used once.";
                    CopyAxiomRecoveryKeyButton.Content = "Copy new recovery key";
                }
                catch (ExternalException)
                {
                    AxiomAccountMessage.Text = "The clipboard is busy. Retry copying this recovery key.";
                    CopyAxiomRecoveryKeyButton.Content = "Retry copying recovery key";
                }
            });
        }

        private void ClearLocalAxiomSession()
        {
            _axiomAccountSession = null;
            _lastAxiomSubscriptionStatus = null;
            _pendingAxiomRecoveryKey = null;
            _axiomBillingTransport.SetManagedEndpoint(string.Empty, string.Empty);
            if (_axiomAccountEndpoint is not null)
                _database?.SaveAxiomAccount(_axiomAccountEndpoint, null);
            _database?.SaveSetting(DatabaseService.UseAxiomAccountSettingKey, "");
            if (_useAxiomAccountEndpoint)
                ApplyAxiomAccountEndpoint(false);
            WorkplaceViewControl?.RefreshCustomEndpointConfiguration();
            RenderAxiomAccount();
            RenderAxiomUsageUi(null);
        }

        private async void SignOutAxiomAccount_Click(object sender, RoutedEventArgs e)
        {
            if (!CanChangeAxiomAccount() || _axiomAccountEndpoint is null || _axiomAccountSession is not { } session)
                return;
            ClearLocalAxiomSession();
            _ = RefreshAxiomSubscriptionAsync();
            await RunAxiomAccountActionAsync(client => client.SignOutAsync(session.Token));
        }

        private async Task RunAxiomAccountActionAsync(Func<AxiomAccountClient, Task> action)
        {
            if (_axiomAccountEndpoint is null)
                return;
            AxiomAccountPanel.IsEnabled = false;
            try
            {
                await action(new AxiomAccountClient(_axiomAccountEndpoint));
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException)
            {
                AxiomAccountMessage.Text = "Axiom couldn’t complete the account request. Try again shortly.";
            }
            finally
            {
                AxiomAccountPanel.IsEnabled = true;
            }
        }

        private async Task<AxiomSubscriptionStatus?> RefreshAxiomSubscriptionAsync()
        {
            int version = ++_subscriptionRefreshVersion;
            var read = await SubscriptionTransport.ReadAxiomSubscriptionDetailedAsync();
            if (version != _subscriptionRefreshVersion || AxiomSubscriptionPanel == null)
                return null;

            if (read.Unauthorized)
            {
                if (_axiomAccountSession is not null || _useAxiomAccountEndpoint)
                {
                    ClearLocalAxiomSession();
                    AxiomAccountMessage.Text = "Your Axiom session expired or was signed out elsewhere. Sign in again.";
                }
                AxiomSubscriptionPanel.Visibility = Visibility.Collapsed;
                RenderAxiomUsageUi(null);
                return null;
            }

            var subscription = read.Status;
            _lastAxiomSubscriptionStatus = subscription;
            AxiomSubscriptionPanel.Visibility = subscription is null ? Visibility.Collapsed : Visibility.Visible;
            if (subscription is null)
            {
                RenderAxiomUsageUi(null);
                return null;
            }

            bool isRevoked = string.Equals(subscription.BillingStatus, "revoked", StringComparison.OrdinalIgnoreCase);
            bool isPaymentFailed = string.Equals(subscription.BillingStatus, "payment_failed", StringComparison.OrdinalIgnoreCase);
            bool isCanceled = string.Equals(subscription.BillingStatus, "canceled", StringComparison.OrdinalIgnoreCase);
            bool paid = subscription.Balance.AllowanceMicros > 0 && !isRevoked;
            if (paid
                && _axiomAccountSession is not null
                && !_useAxiomAccountEndpoint
                && _database?.GetSetting(DatabaseService.UseAxiomAccountSettingKey) != "0"
                && !_openRouterChatService.HasValidKey)
            {
                _database?.SaveSetting(DatabaseService.UseAxiomAccountSettingKey, "1");
                ApplyAxiomAccountEndpoint(true);
                WorkplaceViewControl?.RefreshCustomEndpointConfiguration();
                RenderAxiomAccount();
            }

            if (paid && AxiomCloudSuggestionBanner != null)
                AxiomCloudSuggestionBanner.Visibility = Visibility.Collapsed;

            if (AxiomCloudPlanTitleText != null)
                AxiomCloudPlanTitleText.Text = subscription.Sandbox ? "Axiom subscription sandbox" : "Axiom Cloud subscription";

            AxiomSubscribeButton.Content = subscription.Sandbox ? "Test checkout" : "Subscribe — $9/mo";
            AxiomManageSubscriptionButton.Content = subscription.Sandbox ? "Manage test subscription" : "Manage billing";
            AxiomSubscribeButton.Visibility = paid ? Visibility.Collapsed : Visibility.Visible;
            AxiomManageSubscriptionButton.Visibility = paid || isPaymentFailed ? Visibility.Visible : Visibility.Collapsed;

            string balanceText = FormattableString.Invariant($"${subscription.Balance.AvailableMicros / 1_000_000m:0.00} of inference available");
            string periodSuffix = subscription.RenewsAt is DateTimeOffset renewal
                ? (isCanceled
                    ? $" · Cancels {renewal.ToLocalTime():MMM d, yyyy}"
                    : $" · Period ends {renewal.ToLocalTime():MMM d, yyyy}")
                : string.Empty;

            AxiomSubscriptionStatusText.Text = isRevoked
                ? "Subscription access revoked due to refund or dispute."
                : isPaymentFailed && paid
                    ? $"Payment failed — update your card in Manage billing · {balanceText}{periodSuffix}"
                    : isPaymentFailed
                        ? "Payment failed — update your card in Manage billing or start checkout again."
                        : paid
                            ? balanceText + periodSuffix
                            : (subscription.Sandbox
                                ? "Test plan · $9/month · $5 included inference"
                                : "$9/month · $5 included DeepSeek 4.1F-1017 inference · Cancel anytime");
            RenderAxiomUsageUi(subscription);
            return subscription;
        }

        private void RenderAxiomUsageUi(AxiomSubscriptionStatus? status)
        {
            if (AxiomSessionUsageResetText == null
                || AxiomSessionUsagePercentText == null
                || AxiomWeeklyUsageResetText == null
                || AxiomWeeklyUsagePercentText == null)
            {
                return;
            }

            if (_axiomAccountSession is null)
            {
                if (AxiomUsageNoticeBorder != null && AxiomUsageNoticeText != null)
                {
                    AxiomUsageNoticeText.Text = "Sign in to your Axiom account in Settings → Cloud AI to view DeepSeek 4.1F-1017 usage.";
                    AxiomUsageNoticeBorder.Visibility = Visibility.Visible;
                }
                AxiomSessionUsageResetText.Text = "Starts with your first message";
                AxiomSessionUsagePercentText.Text = "0% used";
                AxiomWeeklyUsageResetText.Text = "Resets every 7 days";
                AxiomWeeklyUsagePercentText.Text = "0% used";
                if (AxiomUsageFooterText != null)
                    AxiomUsageFooterText.Text = "3-hour session and weekly limits help stretch your monthly DeepSeek 4.1F-1017 subscription.";
                UpdateAxiomUsageBarWidths(null);
                return;
            }

            if (status is null)
            {
                if (AxiomUsageNoticeBorder != null && AxiomUsageNoticeText != null)
                {
                    AxiomUsageNoticeText.Text = "Could not load subscription usage right now. Try refreshing shortly.";
                    AxiomUsageNoticeBorder.Visibility = Visibility.Visible;
                }
                UpdateAxiomUsageBarWidths(null);
                return;
            }

            bool isRevoked = string.Equals(status.BillingStatus, "revoked", StringComparison.OrdinalIgnoreCase);
            bool paid = status.Balance.AllowanceMicros > 0 && !isRevoked;
            if (!paid)
            {
                if (AxiomUsageNoticeBorder != null && AxiomUsageNoticeText != null)
                {
                    AxiomUsageNoticeText.Text = isRevoked
                        ? "Subscription allowance was revoked following a refund or dispute."
                        : status.Sandbox
                            ? "Start the $9/month test plan in Settings → Cloud AI to activate DeepSeek 4.1F-1017 usage."
                            : "Subscribe for $9/month in Settings → Cloud AI to activate DeepSeek 4.1F-1017 usage.";
                    AxiomUsageNoticeBorder.Visibility = Visibility.Visible;
                }
                AxiomSessionUsageResetText.Text = "Starts with your first message";
                AxiomSessionUsagePercentText.Text = "0% used";
                AxiomWeeklyUsageResetText.Text = "Resets every 7 days";
                AxiomWeeklyUsagePercentText.Text = "0% used";
                if (AxiomUsageFooterText != null)
                    AxiomUsageFooterText.Text = "3-hour session and weekly limits help stretch your monthly DeepSeek 4.1F-1017 subscription.";
                UpdateAxiomUsageBarWidths(null);
                return;
            }

            if (AxiomUsageNoticeBorder != null)
                AxiomUsageNoticeBorder.Visibility = Visibility.Collapsed;

            var session = status.Usage?.Session ?? new AxiomSubscriptionUsageWindow(0, 0, 0, false, null, 3);
            var week = status.Usage?.Week ?? new AxiomSubscriptionUsageWindow(0, 0, 0, false, null);

            AxiomSessionUsageResetText.Text = session.Active && session.ResetsAt is DateTimeOffset sessionReset
                ? FormatSessionResetLabel(sessionReset)
                : "Starts with your first message";
            AxiomSessionUsagePercentText.Text = FormatUsagePercentLabel(session);

            AxiomWeeklyUsageResetText.Text = week.ResetsAt is DateTimeOffset weekReset
                ? $"Resets {weekReset.ToLocalTime():dddd h:mm tt}"
                : "Resets every 7 days";
            AxiomWeeklyUsagePercentText.Text = FormatUsagePercentLabel(week);

            if (AxiomUsageFooterText != null)
            {
                string balanceNote = FormattableString.Invariant($"${status.Balance.AvailableMicros / 1_000_000m:0.00} remaining this period");
                string renewNote = status.RenewsAt is DateTimeOffset renewal
                    ? $" · Renews {renewal.ToLocalTime():MMM d, yyyy}"
                    : string.Empty;
                AxiomUsageFooterText.Text = $"DeepSeek 4.1F-1017 pacing ({balanceNote}{renewNote}). Current session resets after 3 hours; weekly usage resets to 0% at the end of each week.";
            }

            UpdateAxiomUsageBarWidths(status.Usage);
        }

        private void UpdateAxiomUsageBarWidths(AxiomSubscriptionUsage? usage)
        {
            ApplyUsageBarFill(AxiomSessionUsageTrack, AxiomSessionUsageFill, usage?.Session);
            ApplyUsageBarFill(AxiomWeeklyUsageTrack, AxiomWeeklyUsageFill, usage?.Week);
        }

        private static void ApplyUsageBarFill(Border? track, Border? fill, AxiomSubscriptionUsageWindow? window)
        {
            if (track == null || fill == null)
                return;

            double trackWidth = track.ActualWidth;
            if (trackWidth <= 0 || window is null || window.UsedMicros <= 0)
            {
                fill.Width = 0;
                return;
            }

            double clamped = Math.Clamp(window.UsedPercent, 0, 100);
            double width = trackWidth * (clamped / 100.0);
            if (width < 4)
                width = 4;
            fill.Width = Math.Min(trackWidth, width);

            string colorHex = clamped >= 100 ? "#EF4444" : clamped >= 85 ? "#E0A030" : "#3B82F6";
            fill.Background = AppBrushCache.Get(colorHex);
        }

        private static string FormatSessionResetLabel(DateTimeOffset resetAt)
        {
            TimeSpan remaining = resetAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return "Starts with your first message";

            int totalMinutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return hours > 0
                ? (minutes > 0 ? $"Resets in {hours} hr {minutes} min" : $"Resets in {hours} hr")
                : $"Resets in {minutes} min";
        }

        private static string FormatUsagePercentLabel(AxiomSubscriptionUsageWindow window)
        {
            if (window.UsedMicros <= 0)
                return "0% used";
            if (window.UsedPercent < 1.0)
                return "<1% used";
            int rounded = (int)Math.Clamp(Math.Round(window.UsedPercent), 1, 100);
            return $"{rounded}% used";
        }

        private void AxiomUsageTrack_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateAxiomUsageBarWidths(_lastAxiomSubscriptionStatus?.Usage);
        }

        private async void RefreshAxiomUsageButton_Click(object sender, RoutedEventArgs e)
        {
            if (RefreshAxiomUsageButton != null)
                RefreshAxiomUsageButton.IsEnabled = false;
            try
            {
                await RefreshAxiomSubscriptionAsync();
            }
            finally
            {
                if (RefreshAxiomUsageButton != null)
                    RefreshAxiomUsageButton.IsEnabled = true;
            }
        }

        private void OpenCloudAiFromUsage_Click(object sender, RoutedEventArgs e)
            => ShowSettingsPage("Cloud");

        private void OpenUsageFromCloudAi_Click(object sender, RoutedEventArgs e)
            => ShowSettingsPage("Usage");

        private void OpenTermsOfService_Click(object sender, RoutedEventArgs e)
            => OpenExternalUrl("https://github.com/YoMosa2009/Axiom/blob/main/TERMS.md", "Terms of Service");

        private void OpenPrivacyPolicy_Click(object sender, RoutedEventArgs e)
            => OpenExternalUrl("https://github.com/YoMosa2009/Axiom/blob/main/PRIVACY.md", "Privacy Policy");

        private void OpenExternalUrl(string url, string label)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                AxiomAccountMessage.Text = $"Open {label} at {url}";
            }
        }

        private void ShowAxiomCloudSuggestion(CloudUpgradeSuggestion? suggestion)
        {
            if (suggestion is null || _cloudSuggestionDismissedThisSession
                || HasActivePaidAxiomSubscription
                || AxiomCloudSuggestionBanner == null
                || AxiomCloudSuggestionHeadline == null
                || AxiomCloudSuggestionDetail == null
                || AxiomCloudSuggestionActionButton == null)
            {
                return;
            }

            AxiomCloudSuggestionHeadline.Text = suggestion.Headline;
            AxiomCloudSuggestionDetail.Text = suggestion.Detail;
            AxiomCloudSuggestionActionButton.Content = suggestion.ActionLabel;
            AxiomCloudSuggestionBanner.Visibility = Visibility.Visible;
        }

        private void AxiomCloudSuggestionAction_Click(object sender, RoutedEventArgs e)
        {
            if (AxiomCloudSuggestionBanner != null)
                AxiomCloudSuggestionBanner.Visibility = Visibility.Collapsed;
            OpenSettingsAt("Cloud");
        }

        private void DismissAxiomCloudSuggestion_Click(object sender, RoutedEventArgs e)
        {
            _cloudSuggestionDismissedThisSession = true;
            if (AxiomCloudSuggestionBanner != null)
                AxiomCloudSuggestionBanner.Visibility = Visibility.Collapsed;
        }

        private async void AxiomSubscribeButton_Click(object sender, RoutedEventArgs e)
            => await OpenAxiomSubscriptionLinkAsync(portal: false);

        private async void AxiomManageSubscriptionButton_Click(object sender, RoutedEventArgs e)
            => await OpenAxiomSubscriptionLinkAsync(portal: true);

        private async void AxiomRefreshSubscriptionButton_Click(object sender, RoutedEventArgs e)
        {
            AxiomSubscriptionActions.IsEnabled = false;
            try
            {
                if (await SubscriptionTransport.SynchronizeAxiomSubscriptionAsync())
                    await RefreshAxiomSubscriptionAsync();
                else
                    AxiomSubscriptionStatusText.Text = "The gateway couldn’t verify payment. Try again shortly.";
            }
            finally
            {
                AxiomSubscriptionActions.IsEnabled = true;
            }
        }

        private async Task OpenAxiomSubscriptionLinkAsync(bool portal)
        {
            AxiomSubscriptionActions.IsEnabled = false;
            try
            {
                bool sandbox = _lastAxiomSubscriptionStatus?.Sandbox ?? true;
                Uri? link = await SubscriptionTransport.CreateAxiomSubscriptionLinkAsync(portal);
                if (link is null)
                {
                    AxiomSubscriptionStatusText.Text = sandbox
                        ? "Stripe couldn’t open this sandbox session."
                        : "Stripe couldn’t open Checkout right now. Try again shortly.";
                    return;
                }

                Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                if (!portal)
                    AxiomSubscriptionStatusText.Text = sandbox
                        ? "Complete the test checkout in your browser, then refresh billing here."
                        : "Complete Checkout in your browser, then click Refresh billing here.";
            }
            catch (System.ComponentModel.Win32Exception)
            {
                AxiomSubscriptionStatusText.Text = "Axiom couldn’t open the browser.";
            }
            finally
            {
                AxiomSubscriptionActions.IsEnabled = true;
            }
        }
    }
}
