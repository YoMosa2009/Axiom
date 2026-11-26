using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Malx_AI;

/// <summary>
/// Which UI font Axiom uses. Releases bundle Geist (SIL OFL 1.1). Anthropic Sans is preferred
/// when it is installed on the PC, but it is never bundled: its files carry an Anthropic
/// copyright and no licence that allows redistribution in a public repo or release.
/// Dependency-free so the tests can link it.
/// </summary>
internal static class AppFontPolicy
{
    public const string PreferredFamily = "Anthropic Sans Text";
    public const string BundledFamily = "Geist";
    public const string SystemFallback = "Segoe UI";
    public const string ResourceKey = "AppUiFont";

    /// <summary>True for an installed Anthropic Sans Text file (any weight, OTF or TTF).</summary>
    public static bool IsPreferredFontFile(string? path)
    {
        string text = path ?? string.Empty;
        string name = text[(text.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        return name.StartsWith("AnthropicSans-Text-", StringComparison.OrdinalIgnoreCase)
            && (name.EndsWith(".otf", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The installed folder that holds Anthropic Sans Text, or null.</summary>
    public static string? FindPreferredFontFolder(IEnumerable<string> candidateFolders)
    {
        foreach (string folder in candidateFolders)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder)
                    && Directory.EnumerateFiles(folder).Any(IsPreferredFontFile))
                    return folder;
            }
            catch (Exception)
            {
                // An unreadable fonts folder only means the bundled font is used.
            }
        }
        return null;
    }
}
