using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Malx_AI
{
    /// <summary>
    /// A Project Canvas turn for a small local model: what Axiom will compose, and the task
    /// the model is given (the original request when this turn is only a follow-up).
    /// </summary>
    public sealed record SmallModelCanvasPlan(SkillCanvasDirective Directive, string Task, bool IsFollowUp);

    /// <summary>
    /// Decides when a small local model's turn is a Project Canvas deliverable, and what that
    /// model is asked for.
    /// </summary>
    /// <remarks>
    /// The live report: a 0.6B model with Slide Deck Studio on was asked for a slide deck, wrote
    /// Markdown bullets ending "Let me build the deck.", and then, after "@ProjectCanvas" and
    /// "it must be an actual visual (SVG/HTML)", invented Python. Three gaps caused it:
    /// <list type="number">
    /// <item>the slide format sat deep in a long system prompt a 0.6B model does not follow;</item>
    /// <item>the follow-ups named no deliverable, so no Skill matched and the model was handed the
    /// big-model "write a complete HTML document" brief;</item>
    /// <item>its own earlier failed replies stayed in the history and were copied.</item>
    /// </list>
    /// Small models now always get the structured format next to the task (Axiom builds the
    /// visual), and a follow-up inherits the deliverable and topic of the request it follows.
    /// </remarks>
    public static class SmallModelCanvasPlanner
    {
        public const string SmallModelSkillId = "axiom-small-model-canvas";

        private static readonly Regex DeckIntentRegex = new(
            @"\b(?:slides?|slide[\s-]?deck|slidedeck|decks?|presentations?|power\s?point|pptx?|keynote|slide\s?show)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ChartIntentRegex = new(
            @"\b(?:charts?|graphs?|plot|histogram|infographic)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "make it a visual", "it must be SVG/HTML", "put it in the canvas": a nudge about the
        // previous request rather than a new one.
        private static readonly Regex VisualFollowUpRegex = new(
            @"\b(?:visual|svg|html|canvas|render(?:ed)?|actual(?:ly)?)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ProjectCanvasMentionRegex = new(
            @"@ProjectCanvas\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>How many earlier user turns a follow-up may look back through.</summary>
        private const int FollowUpLookback = 4;

        /// <param name="userMessage">This turn's message.</param>
        /// <param name="earlierUserMessages">Earlier user turns, oldest first, excluding this one.</param>
        /// <param name="resolveSkillDirective">The attached rendering Skill for a message, if any.</param>
        /// <param name="projectCanvasInvoked">True when this turn mentions @ProjectCanvas.</param>
        /// <param name="tier">How much the answering model can author.</param>
        /// <returns>Null when this is not a canvas turn, or the model can author the artifact itself.</returns>
        public static SmallModelCanvasPlan? Resolve(
            string userMessage,
            IReadOnlyList<string> earlierUserMessages,
            Func<string, SkillCanvasDirective?> resolveSkillDirective,
            bool projectCanvasInvoked,
            SkillCanvasTier tier)
        {
            if (tier == SkillCanvasTier.Full || string.IsNullOrWhiteSpace(userMessage))
                return null;

            string current = StripMention(userMessage);
            SkillCanvasDirective? own = ResolveDeliverable(userMessage, current, resolveSkillDirective);
            if (own != null)
                return new SmallModelCanvasPlan(own, current, IsFollowUp: false);

            bool followUp = projectCanvasInvoked || VisualFollowUpRegex.IsMatch(current);
            if (!followUp)
                return null;

            // The follow-up itself ("you were supposed to make it in the projectcanvas") holds no
            // topic, so the task given to the model is the request it refers to.
            foreach (string earlier in (earlierUserMessages ?? []).Reverse().Take(FollowUpLookback))
            {
                if (string.IsNullOrWhiteSpace(earlier))
                    continue;

                string earlierTask = StripMention(earlier);
                SkillCanvasDirective? inherited = ResolveDeliverable(earlier, earlierTask, resolveSkillDirective);
                if (inherited != null)
                    return new SmallModelCanvasPlan(inherited, earlierTask, IsFollowUp: true);
            }

            return projectCanvasInvoked
                ? new SmallModelCanvasPlan(BuildDirective(SkillSmallModelFormats.Markdown), current, IsFollowUp: false)
                : null;
        }

        private static SkillCanvasDirective? ResolveDeliverable(
            string rawMessage,
            string strippedMessage,
            Func<string, SkillCanvasDirective?> resolveSkillDirective)
        {
            SkillCanvasDirective? skill = resolveSkillDirective?.Invoke(rawMessage);
            if (skill != null)
                return skill;

            if (DeckIntentRegex.IsMatch(strippedMessage))
                return BuildDirective(SkillSmallModelFormats.Outline);
            if (ChartIntentRegex.IsMatch(strippedMessage))
                return BuildDirective(SkillSmallModelFormats.Chart);
            return null;
        }

        private static SkillCanvasDirective BuildDirective(string smallModelFormat) => smallModelFormat switch
        {
            SkillSmallModelFormats.Outline => new(SmallModelSkillId, "Slide deck", SkillDeliverableFormats.Html, string.Empty, SkillSmallModelFormats.Outline),
            SkillSmallModelFormats.Chart => new(SmallModelSkillId, "Chart", SkillDeliverableFormats.Html, string.Empty, SkillSmallModelFormats.Chart),
            _ => new(SmallModelSkillId, "Document", SkillDeliverableFormats.Markdown, string.Empty, SkillSmallModelFormats.Markdown)
        };

        internal static string StripMention(string message) =>
            Regex.Replace(ProjectCanvasMentionRegex.Replace(message ?? string.Empty, string.Empty), @"\s{2,}", " ").Trim();

        /// <summary>
        /// The system prompt for a sub-1B canvas turn. Deliberately short: everything the model
        /// must follow goes in the user turn, next to the task, where a tiny model follows it.
        /// </summary>
        public const string MicroSystemPrompt =
            "You are Axiom running a very small local model. You write content in a simple plain-text format, "
            + "and Axiom turns it into the finished visual. Never write HTML, CSS, or code, and never explain what you will do.";

        /// <summary>
        /// The user turn for a small-model canvas plan: the task, then the exact format with a
        /// fill-in template, then the instruction to start writing it.
        /// </summary>
        public static string BuildUserTurn(SmallModelCanvasPlan plan)
        {
            ArgumentNullException.ThrowIfNull(plan);
            var builder = new StringBuilder();
            builder.Append("TASK: ").AppendLine(plan.Task.Trim());
            builder.AppendLine();

            switch (plan.Directive.SmallModelFormat)
            {
                case SkillSmallModelFormats.Outline:
                    // A worked example on an unrelated topic: a 0.6B model copies a concrete
                    // example far more faithfully than a <placeholder> template, which it
                    // followed only as far as TITLE/SUBTITLE before dropping the SLIDE lines.
                    builder.AppendLine("Axiom designs the slides. You write only the slide text. Every slide starts with a line beginning \"SLIDE:\".");
                    builder.AppendLine();
                    builder.AppendLine("Example of the format (for a different topic):");
                    builder.AppendLine("TITLE: Healthy Sleep");
                    builder.AppendLine("SUBTITLE: Simple habits for better rest");
                    builder.AppendLine("SLIDE: Why sleep matters");
                    builder.AppendLine("- Sleep restores the body and mind");
                    builder.AppendLine("- Adults need 7 to 9 hours a night");
                    builder.AppendLine("SLIDE: Build a routine");
                    builder.AppendLine("- Go to bed at the same time each night");
                    builder.AppendLine("- Avoid screens in the last hour");
                    builder.AppendLine();
                    builder.AppendLine("Now write the slides for the TASK in the same format: 5 to 7 SLIDE sections, 2 to 4 short points each. No HTML, no code.");
                    builder.Append("Begin your answer with \"TITLE:\".");
                    break;

                case SkillSmallModelFormats.Chart:
                    // Concrete example, not "<category> | <number>": the 0.6B model copied the
                    // placeholder line itself in 2 of 5 live samples.
                    builder.AppendLine("Axiom draws the chart. You write only the data.");
                    builder.AppendLine();
                    builder.AppendLine("Example of the format (for a different topic):");
                    builder.AppendLine("TITLE: Rainfall by season");
                    builder.AppendLine("CHART: Millimetres of rain");
                    builder.AppendLine("Spring | 120");
                    builder.AppendLine("Summer | 60");
                    builder.AppendLine("Autumn | 140");
                    builder.AppendLine();
                    builder.AppendLine("Now write the data for the TASK in the same format: one category and one plain number per line. Use the numbers from the task when it gives any. No HTML, no code.");
                    builder.Append("Begin your answer with \"TITLE:\".");
                    break;

                default:
                    builder.AppendLine("Write it as a short Markdown document: a '# ' title, '## ' section headings, short paragraphs, and '- ' lists.");
                    builder.AppendLine("No HTML, no code.");
                    builder.Append("Begin your answer with \"# \".");
                    break;
            }

            return builder.ToString();
        }
    }
}
