using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI.ComputerUse
{
    /// <summary>
    /// Reads the visible name of a browser control the Workplace agent wants to click or fill.
    /// Money and account-closing controls are never pressed by the agent; other final-looking
    /// controls (delete, send, submit) still go to the user, with a plain warning in the prompt.
    /// </summary>
    internal static class BrowserActionRisk
    {
        internal enum Level { Normal, Irreversible, Blocked }

        private static readonly Regex Blocked = new(
            @"\b(delete|close|deactivate|terminate|cancel)\s+(my\s+|your\s+|the\s+)?account\b"
            + @"|\b(pay|buy|purchase|order|checkout|check\s+out|subscribe|donate|tip)\s+now\b"
            + @"|\bplace\s+(my\s+|your\s+|the\s+)?order\b|\bconfirm\s+(and\s+pay|payment|purchase|order)\b|\bcomplete\s+(purchase|payment|order)\b"
            + @"|\b(send|transfer|withdraw)\s+(money|funds|payment|crypto)\b|\bproceed\s+to\s+(payment|checkout)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex Final = new(
            @"\b(delete|remove|erase|discard|send|submit|publish|post|unsubscribe|deactivate|cancel\s+subscription|confirm|transfer|empty\s+trash|sign\s+out|log\s+out)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static Level Classify(string? controlName)
        {
            string name = Regex.Replace(controlName ?? string.Empty, @"\s+", " ").Trim();
            if (name.Length == 0) return Level.Normal;
            if (Blocked.IsMatch(name)) return Level.Blocked;
            return Final.IsMatch(name) ? Level.Irreversible : Level.Normal;
        }

        public const string BlockedMessage =
            "Axiom does not press payment or account-closing controls for you. Tell the user which control it is so they can press it themselves.";

        public const string IrreversibleWarning =
            "This control may do something that cannot be undone. Allow it only if you meant it.";
    }
}
