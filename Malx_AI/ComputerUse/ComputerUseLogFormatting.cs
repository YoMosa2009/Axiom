using System;

namespace Malx_AI.ComputerUse
{
    /// <summary>What the reader needs to do about a line, which is what its colour encodes.</summary>
    public enum ComputerUseLogSeverity
    {
        /// <summary>Model narration and action labels: no badge at all.</summary>
        Plain,
        /// <summary>Something is established. Nothing to do.</summary>
        Verified,
        /// <summary>Waiting on evidence that has not arrived yet.</summary>
        Waiting,
        /// <summary>Refused or failed; the run needs a different approach.</summary>
        Blocked,
        /// <summary>Worth a look, but the run continues.</summary>
        Attention,
        /// <summary>An observation the controller recorded.</summary>
        Observed,
        /// <summary>Bookkeeping the reader rarely needs.</summary>
        Quiet
    }

    /// <summary>
    /// Reads the controller's tagged observations for display.
    /// </summary>
    /// <remarks>
    /// The controller writes to the model as "[OUTCOME VERIFIED] …", "[ACTION NOT SENT] …", and
    /// those exact strings are part of the model's contract — they are never rewritten. This turns
    /// one into a short status word plus its sentence, so the panel can show "Verified" in green
    /// rather than a column of shouted brackets. Deliberately free of any UI type so it can be
    /// tested without a display.
    /// </remarks>
    public static class ComputerUseLogFormatting
    {
        public static (string Badge, ComputerUseLogSeverity Severity, string Body) Classify(string? line)
        {
            string text = (line ?? string.Empty).Trim();
            if (text.Length == 0)
                return ("", ComputerUseLogSeverity.Plain, "");

            // Untagged lines are the model's own words. They already read as prose, and badging
            // everything just rebuilds the same wall of noise in a different font.
            int close = text.IndexOf(']');
            if (!text.StartsWith('[') || close < 0)
                return ("", ComputerUseLogSeverity.Plain, text);

            string tag = text[1..close].Trim();
            string body = text[(close + 1)..].Trim();
            if (body.Length == 0)
                body = tag;

            bool Has(string word) => tag.Contains(word, StringComparison.OrdinalIgnoreCase);

            // Ordered by urgency: a failure mentioning "verification" is still a failure.
            if (Has("NOT SENT") || Has("FAILED") || Has("BLOCK") || Has("ERROR") || Has("DENIED"))
                return ("Blocked", ComputerUseLogSeverity.Blocked, body);
            if (Has("NOT VERIFIED") || Has("PENDING") || Has("UNAVAILABLE") || Has("INCONCLUSIVE") || Has("NOT OBSERVED"))
                return ("Waiting", ComputerUseLogSeverity.Waiting, body);
            if (Has("VERIFIED") || Has("COMPLETE"))
                return ("Verified", ComputerUseLogSeverity.Verified, body);
            if (Has("REACHED"))
                return ("Reached", ComputerUseLogSeverity.Verified, body);
            if (Has("STALLED") || Has("WARNING") || Has("INTERRUPTED") || Has("REQUIRED"))
                return ("Attention", ComputerUseLogSeverity.Attention, body);
            if (Has("ASSESSMENT") || Has("SCREENSHOT") || Has("OBSERVATION") || Has("EVIDENCE"))
                return ("Observed", ComputerUseLogSeverity.Observed, body);
            if (Has("LEDGER") || Has("PROGRESS") || Has("PLAN") || Has("STATE"))
                return ("Progress", ComputerUseLogSeverity.Quiet, body);

            return (Title(tag), ComputerUseLogSeverity.Quiet, body);
        }

        // "TAB VERIFICATION" reads better as "Tab verification" than as shouting.
        private static string Title(string tag)
        {
            string trimmed = tag.Trim();
            if (trimmed.Length == 0)
                return "Note";
            if (trimmed.Length > 20)
                trimmed = trimmed[..20].TrimEnd() + "…";
            return char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant();
        }
    }
}
