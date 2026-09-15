using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    internal static class ComputerUseSafety
    {
        private static readonly string[] HighRiskPhrases =
        [
            "format c:", "del /s", "rm -rf", "shutdown", "restart /r", "reg delete",
            "net user", "passwd", "password", "credit card", "ssn", "social security",
            "wire transfer", "send all funds", "crypto seed", "private key",
            "disable antivirus", "bcdedit", "diskpart", "cipher /w", "takeown",
            "sudo rm", "mkfs", "drop table", "truncate table"
        ];

        private static readonly string[] ElevatedPhrases =
        [
            "powershell -enc", "cmd.exe", "run as administrator", "uac",
            "gpupdate", "schtasks", "taskkill", "sc delete", "netsh",
            "install", "uninstall", "registry", "regedit"
        ];

        private static readonly Regex DangerousHotkeyRegex = new(
            @"ctrl\s*\+\s*alt\s*\+\s*del|ctrl\s*\+\s*shift\s*\+\s*esc|alt\s*\+\s*f4|win\s*\+\s*r|win\s*\+\s*x|win\s*\+\s*r\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static ComputerUseSafetyVerdict Evaluate(ComputerUseAction action, ComputerUseSafetyVerdict? modelVerdict)
        {
            string haystack = $"{action.Text} {action.Keys} {action.Summary} {action.Explanation}".ToLowerInvariant();
            bool highRiskText = HighRiskPhrases.Any(phrase => haystack.Contains(phrase, StringComparison.Ordinal));
            bool elevatedText = ElevatedPhrases.Any(phrase => haystack.Contains(phrase, StringComparison.Ordinal));
            bool dangerousKeys = DangerousHotkeyRegex.IsMatch(action.Keys ?? string.Empty)
                || DangerousHotkeyRegex.IsMatch(haystack);

            bool dangerous = highRiskText || dangerousKeys;
            bool requiresAsk = dangerous || elevatedText;
            string risk = dangerous ? "high" : elevatedText ? "medium" : "low";
            string reason = dangerous
                ? "Local safety gate blocked a destructive, credential, or system-control action."
                : elevatedText
                    ? "This action can change system state; Autopilot must re-check and Ask mode needs approval."
                    : "Action looks like ordinary pointing, typing, or scrolling.";

            ComputerUseSafetyVerdict model = modelVerdict ?? new ComputerUseSafetyVerdict();
            bool modelDangerous = model.Dangerous || string.Equals(model.Risk, "high", StringComparison.OrdinalIgnoreCase);
            bool missingPass = !model.Ok && string.IsNullOrWhiteSpace(model.Causes) && string.IsNullOrWhiteSpace(model.Effects);
            bool combinedDangerous = dangerous || modelDangerous;
            bool combinedOk = !combinedDangerous && model.Ok && !missingPass;

            return new ComputerUseSafetyVerdict
            {
                Ok = combinedOk,
                Dangerous = combinedDangerous,
                RequiresAsk = requiresAsk || missingPass || !model.Ok || combinedDangerous,
                Risk = combinedDangerous ? "high" : string.IsNullOrWhiteSpace(model.Risk) ? risk : model.Risk,
                Causes = FirstNonEmpty(model.Causes, dangerous ? "Matches a destructive or credential-related pattern." : "Pointer/keyboard interaction with the current screen."),
                Effects = FirstNonEmpty(model.Effects, dangerous ? "Could delete data, leak secrets, or take the machine offline." : "Updates the focused UI according to the requested input."),
                Reason = combinedDangerous
                    ? FirstNonEmpty(model.Reason, reason)
                    : missingPass
                        ? "Autopilot requires an explicit safety pass covering causes, effects, and danger."
                        : FirstNonEmpty(model.Reason, reason)
            };
        }

        public static string BuildRefusalObservation(ComputerUseAction action, ComputerUseSafetyVerdict verdict)
        {
            return $"[COMPUTER USE SAFETY BLOCK]\nAction: {action.ShortLabel}\nRisk: {verdict.Risk}\nCauses: {verdict.Causes}\nEffects: {verdict.Effects}\nReason: {verdict.Reason}\nDo not retry the same action. Choose a safer alternative or finish with type=done.";
        }

        private static string FirstNonEmpty(params string[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
    }
}
