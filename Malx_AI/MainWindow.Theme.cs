using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Malx_AI
{
    public partial class MainWindow
    {
        private bool _isPopulatingThemeSelector;

        /// <summary>
        /// Restores the saved theme and fills the selector. Called once the window is up, so the
        /// palette is applied before the user sees anything.
        /// </summary>
        private void InitializeThemeSelector()
        {
            try
            {
                string savedThemeId = _database?.GetSetting(AppTheme.ThemeSettingKey) ?? string.Empty;
                AppThemePalette palette = AppTheme.Resolve(savedThemeId);
                AppTheme.Apply(palette);

                _isPopulatingThemeSelector = true;
                try
                {
                    ThemeSelectorCombo.Items.Clear();
                    foreach (AppThemePalette candidate in AppTheme.Palettes)
                    {
                        ThemeSelectorCombo.Items.Add(new ComboBoxItem
                        {
                            Content = candidate.DisplayName,
                            Tag = candidate.Id
                        });
                    }

                    ThemeSelectorCombo.SelectedIndex = Math.Max(0, AppTheme.Palettes
                        .ToList()
                        .FindIndex(candidate => string.Equals(candidate.Id, palette.Id, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    _isPopulatingThemeSelector = false;
                }

                RefreshThemeUi(palette, saved: false);
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogErrorAsync("MainWindow.InitializeThemeSelector", ex);
            }
        }

        private void ThemeSelectorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isPopulatingThemeSelector)
                return;

            if (ThemeSelectorCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string themeId)
                return;

            try
            {
                AppThemePalette palette = AppTheme.Resolve(themeId);
                AppTheme.Apply(palette);
                _database?.SaveSetting(AppTheme.ThemeSettingKey, palette.Id);
                RefreshThemeUi(palette, saved: true);
            }
            catch (Exception ex)
            {
                _ = BackendLogService.LogErrorAsync("MainWindow.ThemeSelectorCombo_SelectionChanged", ex);
            }
        }

        private void RefreshThemeUi(AppThemePalette palette, bool saved)
        {
            ThemeSwatchAccent.Background = AppBrushCache.Get(palette.Accent);
            ThemeSwatchBackground.Background = AppBrushCache.Get(palette.Background);
            ThemeSwatchForeground.Background = AppBrushCache.Get(palette.Foreground());

            ThemeStatusText.Text = saved
                ? $"{palette.DisplayName} applied."
                : $"{palette.DisplayName} · accent {palette.Accent}, background {palette.Background}, foreground {palette.Text}.";

            // The native caption is painted by DWM, not WPF, so it needs re-applying by hand.
            WindowTitleBarTheme.Apply(this);

            RepaintCodePaintedSelections();
        }

        /// <summary>
        /// Re-applies the highlight colours that are set from code rather than by a style, so a
        /// theme change does not leave the previous accent on the selected chat or workplace.
        /// </summary>
        private void RepaintCodePaintedSelections()
        {
            if (_activeChatButton != null)
            {
                _activeChatButton.Background = AppTheme.Brush(p => p.AccentSoft);
                _activeChatButton.BorderBrush = AppTheme.Brush(p => p.Accent);
            }

            if (_activeWorkplaceButton != null)
            {
                _activeWorkplaceButton.Background = AppTheme.Brush(p => p.AccentSoft);
                _activeWorkplaceButton.BorderBrush = AppTheme.Brush(p => p.Accent);
                _activeWorkplaceButton.Foreground = AppTheme.Brush(p => p.Text);
            }

            RefreshCloudModeToggleUi();
            RefreshNormalWebToggleUi();
        }
    }

    internal static class AppThemePaletteExtensions
    {
        /// <summary>Foreground swatch colour — the text colour, named as the theme editor names it.</summary>
        public static string Foreground(this AppThemePalette palette) => palette.Text;
    }
}
