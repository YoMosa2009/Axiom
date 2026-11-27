using System;
using System.Collections.Generic;

namespace Malx_AI;

/// <summary>What a global keyboard shortcut does. Dependency-free so the tests can link it.</summary>
internal enum AppShortcutAction
{
    None,
    NewChat,
    FocusSearch,
    FocusComposer,
    ToggleSidebar,
    ShowShortcuts,
}

/// <summary>One row of the shortcut cheatsheet.</summary>
internal sealed record AppShortcutInfo(AppShortcutAction Action, string Keys, string Description, string Group);

/// <summary>
/// The single list of Axiom's global shortcuts. The key handler and the Ctrl+/ cheatsheet both
/// read it, so they cannot disagree. Only plain Ctrl+letter chords are used: they do not clash
/// with typing, and they work while the composer has focus.
/// </summary>
internal static class KeyboardShortcutCatalog
{
    public static readonly IReadOnlyList<AppShortcutInfo> All = new[]
    {
        new AppShortcutInfo(AppShortcutAction.NewChat, "Ctrl+N", "New chat", "Navigate"),
        new AppShortcutInfo(AppShortcutAction.FocusSearch, "Ctrl+K", "Search chats", "Navigate"),
        new AppShortcutInfo(AppShortcutAction.FocusComposer, "Ctrl+L", "Focus the message box", "Navigate"),
        new AppShortcutInfo(AppShortcutAction.ToggleSidebar, "Ctrl+B", "Show or hide the sidebar (Workplace)", "View"),
        new AppShortcutInfo(AppShortcutAction.ShowShortcuts, "Ctrl+/", "Show this cheatsheet", "Help"),
    };

    /// <summary>
    /// Resolves a pressed chord. <paramref name="ctrl"/> must be the only modifier held; Shift or
    /// Alt chords belong to other features (for example Ctrl+Shift+L attaches a selection).
    /// <paramref name="keyName"/> is the WPF <c>Key</c> name: "N", "K", "L", "B", "OemQuestion".
    /// </summary>
    public static AppShortcutAction Resolve(string? keyName, bool ctrl, bool shift, bool alt)
    {
        if (!ctrl || shift || alt || string.IsNullOrEmpty(keyName))
            return AppShortcutAction.None;

        return keyName switch
        {
            "N" => AppShortcutAction.NewChat,
            "K" => AppShortcutAction.FocusSearch,
            "L" => AppShortcutAction.FocusComposer,
            "B" => AppShortcutAction.ToggleSidebar,
            // "/" is the unshifted OemQuestion key on US layouts; Divide is the numpad slash.
            "OemQuestion" or "Divide" => AppShortcutAction.ShowShortcuts,
            _ => AppShortcutAction.None,
        };
    }
}
