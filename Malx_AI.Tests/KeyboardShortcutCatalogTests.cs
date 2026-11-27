using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public class KeyboardShortcutCatalogTests
{
    [Theory]
    [InlineData("N", AppShortcutAction.NewChat)]
    [InlineData("K", AppShortcutAction.FocusSearch)]
    [InlineData("L", AppShortcutAction.FocusComposer)]
    [InlineData("B", AppShortcutAction.ToggleSidebar)]
    [InlineData("OemQuestion", AppShortcutAction.ShowShortcuts)]
    [InlineData("Divide", AppShortcutAction.ShowShortcuts)]
    public void CtrlChord_ResolvesToAction(string key, AppShortcutAction expected)
        => Assert.Equal(expected, KeyboardShortcutCatalog.Resolve(key, ctrl: true, shift: false, alt: false));

    [Fact]
    public void WithoutCtrl_OrWithExtraModifiers_ResolvesToNothing()
    {
        Assert.Equal(AppShortcutAction.None, KeyboardShortcutCatalog.Resolve("N", false, false, false));
        // Ctrl+Shift+L attaches a selection as context and must stay free.
        Assert.Equal(AppShortcutAction.None, KeyboardShortcutCatalog.Resolve("L", true, true, false));
        Assert.Equal(AppShortcutAction.None, KeyboardShortcutCatalog.Resolve("N", true, false, true));
        Assert.Equal(AppShortcutAction.None, KeyboardShortcutCatalog.Resolve(null, true, false, false));
        Assert.Equal(AppShortcutAction.None, KeyboardShortcutCatalog.Resolve("Q", true, false, false));
    }

    [Fact]
    public void Catalog_ListsEachActionOnce_WithKeysAndText()
    {
        Assert.Equal(KeyboardShortcutCatalog.All.Count, KeyboardShortcutCatalog.All.Select(s => s.Action).Distinct().Count());
        Assert.All(KeyboardShortcutCatalog.All, s =>
        {
            Assert.NotEqual(AppShortcutAction.None, s.Action);
            Assert.StartsWith("Ctrl+", s.Keys);
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
        });
    }
}
