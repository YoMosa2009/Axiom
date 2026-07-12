using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Malx_AI
{
    internal static class WindowsToastNotificationService
    {
        private static bool _registered;

        public static void Initialize()
        {
            if (_registered)
                return;

            try
            {
                AppNotificationManager.Default.NotificationInvoked += NotificationInvoked;
                AppNotificationManager.Default.Register();
                _registered = true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Windows notification registration failed: {ex.Message}");
                _ = BackendLogService.LogErrorAsync("WindowsToast.Register", ex);
            }
        }

        public static void Shutdown()
        {
            if (!_registered)
                return;

            try
            {
                AppNotificationManager.Default.NotificationInvoked -= NotificationInvoked;
                AppNotificationManager.Default.Unregister();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Windows notification shutdown failed: {ex.Message}");
            }
            finally
            {
                _registered = false;
            }
        }

        public static void ShowCouncilCompletionIfInactive(string message)
        {
            if (!_registered || string.IsNullOrWhiteSpace(message))
                return;

            Application.Current.Dispatcher.InvokeAsync(() =>
            {
                if (Application.Current.MainWindow is not Window window || IsForegroundWindow(window))
                    return;

                try
                {
                    AppNotification notification = new AppNotificationBuilder()
                        .AddArgument("action", "focus-workplace")
                        .AddText("Axiom Workplace")
                        .AddText(message)
                        .BuildNotification();
                    AppNotificationManager.Default.Show(notification);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Windows notification failed: {ex.Message}");
                    _ = BackendLogService.LogErrorAsync("WindowsToast.Show", ex);
                }
            });
        }

        internal static bool IsForegroundWindow(Window window)
        {
            if (window.WindowState == WindowState.Minimized || !window.IsVisible || !window.IsActive)
                return false;

            nint handle = new WindowInteropHelper(window).Handle;
            return handle != 0 && GetForegroundWindow() == handle;
        }

        private static void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
        {
            Application.Current?.Dispatcher.InvokeAsync(FocusMainWindow);
        }

        private static void FocusMainWindow()
        {
            if (Application.Current?.MainWindow is not Window window)
                return;

            if (!window.IsVisible)
                window.Show();
            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Activate();
            nint handle = new WindowInteropHelper(window).Handle;
            if (handle != 0)
                SetForegroundWindow(handle);
        }

        [DllImport("user32.dll")]
        private static extern nint GetForegroundWindow();

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint hWnd);
    }
}
