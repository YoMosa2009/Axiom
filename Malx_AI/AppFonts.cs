using System;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace Malx_AI
{
    /// <summary>
    /// Resolves the app-wide UI font once at startup and publishes it as the
    /// <see cref="AppFontPolicy.ResourceKey"/> resource that every view binds to.
    /// </summary>
    internal static class AppFonts
    {
        private static FontFamily? _ui;

        /// <summary>The UI font for code-built elements (XAML uses the DynamicResource).</summary>
        public static FontFamily Ui => _ui ??= Resolve();

        public static void Apply(ResourceDictionary resources)
        {
            try
            {
                resources[AppFontPolicy.ResourceKey] = Ui;
            }
            catch (Exception)
            {
                // Keep the Segoe UI default from App.xaml.
            }
        }

        private static FontFamily Resolve()
        {
            try
            {
                // Fonts installed "for this user only" live outside C:\Windows\Fonts, which WPF's
                // system list does not always include.
                string userFonts = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Fonts");
                // A per-user font can also be registered from any folder (the owner keeps theirs on
                // F:); its registry entry holds the full path.
                string? folder = AppFontPolicy.FindPreferredFontFolder(new[] { userFonts })
                    ?? AppFontPolicy.FolderFromRegisteredFontPaths(ReadRegisteredUserFontPaths());
                if (folder != null)
                {
                    var baseUri = new Uri(folder.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
                    return new FontFamily(baseUri, $"./#{AppFontPolicy.PreferredFamily}, {AppFontPolicy.SystemFallback}");
                }

                // WPF tries each family in turn: Anthropic Sans when installed system-wide, then the
                // bundled Geist, then Segoe UI for any glyph neither has.
                return new FontFamily(new Uri("pack://application:,,,/"),
                    $"{AppFontPolicy.PreferredFamily}, ./Assets/Fonts/#{AppFontPolicy.BundledFamily}, {AppFontPolicy.SystemFallback}");
            }
            catch (Exception)
            {
                return new FontFamily(AppFontPolicy.SystemFallback);
            }
        }

        private static string[] ReadRegisteredUserFontPaths()
        {
            try
            {
                using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key == null)
                    return [];
                var paths = new System.Collections.Generic.List<string>();
                foreach (string name in key.GetValueNames())
                    if (key.GetValue(name) is string path)
                        paths.Add(path);
                return paths.ToArray();
            }
            catch (Exception)
            {
                return [];
            }
        }
    }
}
