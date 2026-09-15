using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Malx_AI.Mcp;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseMention
    {
        public const string PrimaryHandle = "ComputerUse";
        public const string DisplayName = "Computer Use";
        public const string Description = "Let a vision-capable model see the screen and use the mouse and keyboard.";

        public static readonly string[] Handles =
        [
            PrimaryHandle,
            "Computer",
            "Computer_Use"
        ];

        private static readonly HashSet<string> HandleSet = new(Handles, StringComparer.OrdinalIgnoreCase);
        private static readonly Regex MentionStripRegex = new(
            @"(?<![A-Za-z0-9_])@(?:ComputerUse|Computer_Use|Computer)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static IReadOnlyCollection<string> KnownHandles => Handles;

        public static bool IsKnownHandle(string? handle)
            => !string.IsNullOrWhiteSpace(handle) && HandleSet.Contains(handle.Trim());

        public static bool IsInvoked(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return McpMentionHelper.FindMentions(text, Handles).Any(span => span.IsComplete);
        }

        public static string StripMentions(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            string stripped = MentionStripRegex.Replace(text, " ");
            return Regex.Replace(stripped, @"[ \t]{2,}", " ").Trim();
        }

        public static bool MatchesQuery(string? query)
        {
            string normalized = (query ?? string.Empty).Trim();
            if (normalized.Length == 0)
                return true;

            return Handles.Any(handle =>
                handle.StartsWith(normalized, StringComparison.OrdinalIgnoreCase)
                || handle.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                || DisplayName.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static McpConnectorInfo CreatePickerOption()
        {
            return new McpConnectorInfo
            {
                Id = "axiom-computer-use",
                Handle = PrimaryHandle,
                DisplayName = DisplayName,
                Description = Description,
                Kind = McpConnectorKind.GitHub,
                LogoGlyph = "🖥",
                IsConnected = true,
                AccountLabel = "Workplace"
            };
        }
    }
}
