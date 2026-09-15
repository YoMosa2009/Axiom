using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Malx_AI
{
    /// <summary>
    /// The colours one theme supplies, named by role rather than by hue so a palette swap keeps
    /// every surface, border and text level in the same relationship to each other.
    /// </summary>
    public sealed class AppThemePalette
    {
        public required string Id { get; init; }
        public required string DisplayName { get; init; }

        /// <summary>Window and page background — the darkest surface.</summary>
        public required string Background { get; init; }
        /// <summary>Cards, bubbles and the composer.</summary>
        public required string Surface { get; init; }
        /// <summary>A card sitting on another card (chips, inline tiles).</summary>
        public required string SurfaceRaised { get; init; }
        /// <summary>Hover fill for quiet controls.</summary>
        public required string SurfaceHover { get; init; }
        /// <summary>Wells and code panes that sit below the page surface.</summary>
        public required string SurfaceSunken { get; init; }

        public required string Border { get; init; }
        /// <summary>Border for a focused or emphasised control.</summary>
        public required string BorderStrong { get; init; }

        public required string Text { get; init; }
        public required string TextSecondary { get; init; }
        public required string TextMuted { get; init; }
        public required string TextDisabled { get; init; }

        public required string Accent { get; init; }
        /// <summary>Accent at full brightness: active labels, hovered accent glyphs.</summary>
        public required string AccentBright { get; init; }
        /// <summary>Accent dimmed for secondary emphasis.</summary>
        public required string AccentMuted { get; init; }
        /// <summary>Accent as a background tint behind accent text.</summary>
        public required string AccentSoft { get; init; }
        /// <summary>Text and glyphs drawn on top of a solid accent fill.</summary>
        public required string OnAccent { get; init; }

        public required string Success { get; init; }
        public required string Warning { get; init; }
        public required string Danger { get; init; }
    }

    /// <summary>
    /// Applies a palette across the running app.
    /// </summary>
    /// <remarks>
    /// Applying a palette replaces the brush entries in <c>Application.Resources</c>. WPF freezes
    /// the Freezables that dictionary holds, so recolouring a brush in place is impossible; every
    /// palette reference in XAML is therefore a <c>DynamicResource</c>, which re-resolves when the
    /// entry is replaced. That is what lets a theme change apply without a restart.
    /// </remarks>
    public static class AppTheme
    {
        public const string DefaultThemeId = "axiom-dark";
        public const string ThemeSettingKey = "app_theme_id";

        // Assigned lazily, not in a field initializer: static fields initialise in declaration
        // order, so reading Palettes from here would read it before it exists.
        private static AppThemePalette? _current;

        /// <summary>Raised after a palette is applied, for surfaces painted from code.</summary>
        public static event EventHandler? Changed;

        public static AppThemePalette Current => _current ??= Palettes[0];

        public static IReadOnlyList<AppThemePalette> Palettes { get; } =
        [
            new AppThemePalette
            {
                Id = DefaultThemeId,
                DisplayName = "Axiom Dark (default)",
                Background = "#171615",
                Surface = "#211F1D",
                SurfaceRaised = "#24211F",
                SurfaceHover = "#2A2826",
                SurfaceSunken = "#1C1A18",
                Border = "#302D2A",
                BorderStrong = "#4B4239",
                Text = "#EDE8E3",
                TextSecondary = "#B0A89F",
                TextMuted = "#8A8279",
                TextDisabled = "#70685F",
                Accent = "#B8924A",
                AccentBright = "#F5D591",
                AccentMuted = "#D8B56B",
                AccentSoft = "#3A3226",
                OnAccent = "#171615",
                Success = "#5FAF7D",
                Warning = "#E0A030",
                Danger = "#C96A5B"
            },
            new AppThemePalette
            {
                Id = "gruvbox-dark",
                DisplayName = "Gruvbox Dark",
                // Background, foreground and accent are the values from the Gruvbox palette; the
                // rest of the ramp follows Gruvbox's own greys so contrast stays even.
                Background = "#282828",
                Surface = "#32302F",
                SurfaceRaised = "#3C3836",
                SurfaceHover = "#45403D",
                SurfaceSunken = "#1D2021",
                Border = "#504945",
                BorderStrong = "#665C54",
                Text = "#EBDBB2",
                TextSecondary = "#D5C4A1",
                TextMuted = "#A89984",
                TextDisabled = "#7C6F64",
                Accent = "#458588",
                AccentBright = "#8EC07C",
                AccentMuted = "#83A598",
                AccentSoft = "#2F3B3C",
                OnAccent = "#1D2021",
                Success = "#B8BB26",
                Warning = "#FABD2F",
                Danger = "#FB4934"
            }
        ];

        public static AppThemePalette Resolve(string? themeId)
            => Palettes.FirstOrDefault(p => string.Equals(p.Id, themeId, StringComparison.OrdinalIgnoreCase))
                ?? Palettes[0];

        /// <summary>Colour for one palette slot in the active theme.</summary>
        public static Color Color(Func<AppThemePalette, string> slot)
            => ParseColor(slot(Current));

        /// <summary>Cached brush for one palette slot in the active theme.</summary>
        public static SolidColorBrush Brush(Func<AppThemePalette, string> slot)
            => AppBrushCache.Get(Color(slot));

        // The default palette doubles as the token set for code-painted colours: a hex that
        // matches a slot in Axiom Dark resolves to that slot in whatever theme is active, and
        // anything else (status hues, one-off colours) is used as written.
        private static readonly Dictionary<string, Func<AppThemePalette, string>> DefaultHexSlots =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["#171615"] = p => p.Background,
                ["#211F1D"] = p => p.Surface,
                ["#24211F"] = p => p.SurfaceRaised,
                ["#2A2826"] = p => p.SurfaceHover,
                ["#2D2926"] = p => p.SurfaceHover,
                ["#1C1A18"] = p => p.SurfaceSunken,
                ["#302D2A"] = p => p.Border,
                ["#36312D"] = p => p.Border,
                ["#3A3631"] = p => p.Border,
                ["#4B4239"] = p => p.BorderStrong,
                ["#4A443D"] = p => p.BorderStrong,
                ["#4A4035"] = p => p.BorderStrong,
                ["#EDE8E3"] = p => p.Text,
                ["#DCD5CB"] = p => p.Text,
                ["#B0A89F"] = p => p.TextSecondary,
                ["#BFB6AA"] = p => p.TextSecondary,
                ["#A69D92"] = p => p.TextSecondary,
                ["#8A8279"] = p => p.TextMuted,
                ["#70685F"] = p => p.TextDisabled,
                ["#665F58"] = p => p.TextDisabled,
                ["#B8924A"] = p => p.Accent,
                ["#F5D591"] = p => p.AccentBright,
                ["#D8B56B"] = p => p.AccentMuted,
                ["#3A3226"] = p => p.AccentSoft,
                ["#2A241B"] = p => p.AccentSoft,
                ["#5FAF7D"] = p => p.Success,
                ["#22C55E"] = p => p.Success,
                ["#E0A030"] = p => p.Warning,
                ["#C96A5B"] = p => p.Danger,
                ["#FF3B3B"] = p => p.Danger,
            };

        /// <summary>Brush for a colour written as a default-palette hex, mapped to the active theme.</summary>
        public static SolidColorBrush Brush(string colorText)
        {
            if (!string.IsNullOrWhiteSpace(colorText)
                && DefaultHexSlots.TryGetValue(colorText.Trim(), out Func<AppThemePalette, string>? slot))
            {
                return AppBrushCache.Get(ParseColor(slot(Current)));
            }

            return AppBrushCache.Get(colorText);
        }

        public static void Apply(string? themeId) => Apply(Resolve(themeId));

        public static void Apply(AppThemePalette palette)
        {
            ArgumentNullException.ThrowIfNull(palette);
            _current = palette;

            ResourceDictionary? resources = Application.Current?.Resources;
            if (resources == null)
                return;

            SetBrush(resources, "NotebookBackgroundBrush", palette.Background);
            SetBrush(resources, "DarkSidebarBrush", palette.Background);
            SetBrush(resources, "NotebookSurfaceBrush", palette.Surface);
            SetBrush(resources, "AiMessageBrush", palette.Surface);
            SetBrush(resources, "SurfaceRaisedBrush", palette.SurfaceRaised);
            SetBrush(resources, "SurfaceHoverBrush", palette.SurfaceHover);
            SetBrush(resources, "SurfaceSunkenBrush", palette.SurfaceSunken);
            SetBrush(resources, "NotebookBorderBrush", palette.Border);
            SetBrush(resources, "BorderStrongBrush", palette.BorderStrong);
            SetBrush(resources, "NotebookTextBrush", palette.Text);
            SetBrush(resources, "DarkSecondaryBrush", palette.TextSecondary);
            SetBrush(resources, "MutedTextBrush", palette.TextMuted);
            SetBrush(resources, "DisabledTextBrush", palette.TextDisabled);
            SetBrush(resources, "PrimaryAccentBrush", palette.Accent);
            SetBrush(resources, "AccentBrightBrush", palette.AccentBright);
            SetBrush(resources, "AccentMutedBrush", palette.AccentMuted);
            SetBrush(resources, "AccentSoftBrush", palette.AccentSoft);
            SetBrush(resources, "OnAccentBrush", palette.OnAccent);
            SetBrush(resources, "SuccessBrush", palette.Success);
            SetBrush(resources, "WarningBrush", palette.Warning);
            SetBrush(resources, "DangerBrush", palette.Danger);

            SetColor(resources, "DarkBackground", palette.Background);
            SetColor(resources, "DarkText", palette.Text);
            SetColor(resources, "DarkSecondary", palette.TextSecondary);
            SetColor(resources, "DarkSidebar", palette.Background);
            SetColor(resources, "PrimaryAccent", palette.Accent);

            Changed?.Invoke(null, EventArgs.Empty);
        }

        private static void SetBrush(ResourceDictionary resources, string key, string colorText)
        {
            Color color = ParseColor(colorText);
            if (resources[key] is SolidColorBrush existing && existing.Color == color)
                return;

            // The entry is replaced rather than recoloured: WPF freezes the Freezables held in
            // Application.Resources, so the brush instance itself can never be edited. Every
            // palette reference in XAML is a DynamicResource, which re-resolves on replacement.
            resources[key] = new SolidColorBrush(color);
        }

        private static void SetColor(ResourceDictionary resources, string key, string colorText)
        {
            if (resources.Contains(key))
                resources[key] = ParseColor(colorText);
        }

        private static Color ParseColor(string colorText)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(colorText);
            }
            catch (Exception)
            {
                return Colors.Magenta; // Loud on purpose: a bad palette entry should be obvious.
            }
        }
    }
}
