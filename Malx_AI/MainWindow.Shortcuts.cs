using System.Windows;
using System.Windows.Input;

namespace Malx_AI
{
    // Global keyboard shortcuts and the Ctrl+/ cheatsheet. The chord table lives in
    // KeyboardShortcutCatalog (pure, tested); this file only performs the actions.
    public partial class MainWindow
    {
        private ShortcutsWindow? _shortcutsWindow;

        /// <summary>Returns true when the key press was a shortcut and has been handled.</summary>
        private bool TryHandleGlobalShortcut(KeyEventArgs e)
        {
            ModifierKeys mods = Keyboard.Modifiers;
            AppShortcutAction action = KeyboardShortcutCatalog.Resolve(
                e.Key.ToString(),
                (mods & ModifierKeys.Control) != 0,
                (mods & ModifierKeys.Shift) != 0,
                (mods & ModifierKeys.Alt) != 0);

            switch (action)
            {
                case AppShortcutAction.NewChat:
                    if (_isWorkplaceTabActive || !NewChatButton.IsVisible || !NewChatButton.IsEnabled)
                        return false;
                    NewChat_Click(NewChatButton, new RoutedEventArgs());
                    return true;

                case AppShortcutAction.FocusSearch:
                    if (ChatHistorySearchBox.IsVisible)
                    {
                        ChatHistorySearchBox.Focus();
                        ChatHistorySearchBox.SelectAll();
                        return true;
                    }
                    if (WorkplaceSearchBox.IsVisible)
                    {
                        WorkplaceSearchBox.Focus();
                        WorkplaceSearchBox.SelectAll();
                        return true;
                    }
                    return false;

                case AppShortcutAction.FocusComposer:
                    if (!InputBox.IsVisible || !InputBox.IsEnabled)
                        return false;
                    InputBox.Focus();
                    return true;

                case AppShortcutAction.ShowShortcuts:
                    ShowShortcutsWindow();
                    return true;

                default:
                    // ToggleSidebar is handled by OnPreviewKeyDown (Workplace only).
                    return false;
            }
        }

        private void ShowShortcutsWindow()
        {
            if (_shortcutsWindow is { IsLoaded: true })
            {
                _shortcutsWindow.Close();
                return;
            }

            _shortcutsWindow = new ShortcutsWindow { Owner = this };
            _shortcutsWindow.Closed += (_, _) => _shortcutsWindow = null;
            _shortcutsWindow.Show();
        }
    }
}
