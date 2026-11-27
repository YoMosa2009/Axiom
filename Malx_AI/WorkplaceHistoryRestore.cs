using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>
    /// Rebuilds the model's conversation for a Workplace chat from its saved cards.
    /// </summary>
    /// <remarks>
    /// Only the cards were saved, never the conversation the model is sent, so after a restart
    /// (or reopening a chat from the sidebar) the model started from nothing: "continue" or "any
    /// updates?" reached a model that had never seen the task (owner report 2026-11-26, "should
    /// automatically fetch context of whatever chat"). The cards carry the same words, so the
    /// conversation is rebuilt from them: the user's messages and the agent's answers, without the
    /// run-card bookkeeping (plan checklist, step list, live status lines).
    /// </remarks>
    public static class WorkplaceHistoryRestore
    {
        /// <summary>Turns kept; older ones are summarised by the cards themselves if needed.</summary>
        public const int MaxTurns = 40;

        /// <summary>Longest single turn carried over, in characters.</summary>
        public const int MaxTurnChars = 6000;

        private static readonly Regex StepsBlock = new(@"(?ms)^###[ \t]+Steps Executed[ \t]+\(\d+\)[ \t]*\r?\n.*?(?=^#{1,3}[ \t]|\z)", RegexOptions.Compiled);
        private static readonly Regex PlanBlock = new(@"(?m)^\*\*Plan\*\*[ \t]*\(\d+/\d+\)[ \t]*\r?\n(?:^[ \t]*-[ \t].*(?:\r?\n|$))*", RegexOptions.Compiled);
        private static readonly Regex StatusLine = new(@"(?m)^_[^_\n]+_[ \t]*$", RegexOptions.Compiled);
        private static readonly Regex NarrationLine = new(@"(?m)^›[ \t].*$", RegexOptions.Compiled);
        private static readonly Regex BlankRuns = new(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>
        /// The conversation (role "user" / "assistant") for the given cards, oldest first.
        /// Cards of other roles (notices, system, errors) are not part of it.
        /// </summary>
        public static List<(string Role, string Content)> FromCards(IEnumerable<(string Role, string Content)> cards)
        {
            var turns = new List<(string Role, string Content)>();
            foreach ((string role, string content) in cards ?? [])
            {
                string? mapped = (role ?? string.Empty).ToLowerInvariant() switch
                {
                    "user" => "user",
                    "agent" or "assistant" or "builder" => "assistant",
                    _ => null
                };
                if (mapped == null)
                    continue;

                string text = mapped == "assistant" ? CleanAgentCard(content) : (content ?? string.Empty).Trim();
                if (text.Length == 0)
                    continue;
                if (text.Length > MaxTurnChars)
                    text = text[..MaxTurnChars].TrimEnd() + "\n[…earlier text shortened]";

                // A run split into several cards reads as one answer.
                if (turns.Count > 0 && turns[^1].Role == mapped && mapped == "assistant")
                    turns[^1] = (mapped, turns[^1].Content + "\n\n" + text);
                else
                    turns.Add((mapped, text));
            }

            return turns.Count <= MaxTurns ? turns : turns.Skip(turns.Count - MaxTurns).ToList();
        }

        /// <summary>The agent's words on a card, without the run's step list, plan and status lines.</summary>
        public static string CleanAgentCard(string? content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return string.Empty;
            string text = content.Replace("\r\n", "\n");
            text = StepsBlock.Replace(text, string.Empty);
            text = PlanBlock.Replace(text, string.Empty);
            text = StatusLine.Replace(text, string.Empty);
            text = NarrationLine.Replace(text, string.Empty);
            // Live step rows ("- ✓ Ran …", "- ⋯ 4 earlier actions") are bookkeeping too.
            text = string.Join("\n", text.Split('\n').Where(line =>
            {
                string t = line.TrimStart();
                return !(t.StartsWith("- ✓ ", StringComparison.Ordinal) || t.StartsWith("- ✗ ", StringComparison.Ordinal)
                    || t.StartsWith("- ⊘ ", StringComparison.Ordinal) || t.StartsWith("- ⋯ ", StringComparison.Ordinal));
            }));
            text = BlankRuns.Replace(text, "\n\n").Trim();
            return string.Equals(text, "Stopped.", StringComparison.Ordinal) ? string.Empty : text;
        }
    }
}
