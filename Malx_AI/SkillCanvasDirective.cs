using System;
using System.Text;

namespace Malx_AI
{
    /// <summary>Artifact formats a Skill can deliver into the Project Canvas.</summary>
    public static class SkillDeliverableFormats
    {
        public const string None = "";
        public const string Html = "html";
        public const string Svg = "svg";
        public const string Markdown = "markdown";

        public static bool IsRenderable(string? format) =>
            string.Equals(format, Html, StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, Svg, StringComparison.OrdinalIgnoreCase)
            || string.Equals(format, Markdown, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What a model too small to author HTML is asked for instead. Axiom composes the artifact
    /// from the result, so the deliverable is the same at every model size.
    /// </summary>
    public static class SkillSmallModelFormats
    {
        public const string Outline = "outline";
        public const string Chart = "chart";
        public const string Markdown = "markdown";
    }

    /// <summary>
    /// How much artifact authoring the answering model can be trusted with. Mirrors
    /// <see cref="LocalModelSizeClass"/>, with cloud and unmeasured models treated as Full.
    /// </summary>
    public enum SkillCanvasTier
    {
        /// <summary>Under 1B. Cannot author HTML; emits a short structured outline Axiom renders.</summary>
        Micro,

        /// <summary>1B-4B. Writes usable structure but not a correct self-contained document.</summary>
        Compact,

        /// <summary>4B and up, and every cloud model. Authors the artifact directly.</summary>
        Full
    }

    /// <summary>
    /// A resolved decision that one attached Skill delivers a rendered artifact this turn instead
    /// of a prose answer. Carrying it as a value keeps Normal Chat and the Workplace Builder
    /// rendering the same thing for the same request.
    /// </summary>
    public sealed record SkillCanvasDirective(
        string SkillId,
        string SkillName,
        string Format,
        string Contract,
        string SmallModelFormat = SkillSmallModelFormats.Markdown)
    {
        private (string Fence, string Guidance) FormatRules() => Format.ToLowerInvariant() switch
        {
            SkillDeliverableFormats.Svg => ("svg",
                "One standalone <svg> element with a viewBox and no fixed width or height, so it scales to the pane. "
                + "Text must use a generic family (sans-serif / serif / monospace) because no webfont can load."),
            SkillDeliverableFormats.Markdown => ("markdown",
                "One Markdown document using headings, tables, and lists. No HTML tags and no front matter."),
            _ => ("html",
                "One complete self-contained HTML document: <!DOCTYPE html>, <html>, <head> with an inline <style>, "
                + "and <body>. Any behaviour goes in an inline <script>. Everything the artifact needs lives in this "
                + "one document.")
        };

        /// <summary>
        /// The prompt block that turns a Skill into a rendered deliverable, sized to what the
        /// answering model can actually produce.
        /// </summary>
        /// <remarks>
        /// The failure this fixes at the top tier is a model that describes the deck it would
        /// build and returns prose the canvas has nothing to render. At the lower tiers the
        /// failure is different: a 0.5B model asked for a self-contained HTML document returns
        /// broken markup, so it is asked for a few structured lines and Axiom builds the document.
        /// </remarks>
        public string BuildSystemInstruction(string surfaceDescription, SkillCanvasTier tier) =>
            tier == SkillCanvasTier.Full
                ? BuildAuthoringInstruction(surfaceDescription)
                : BuildStructuredInstruction(tier);

        private string BuildAuthoringInstruction(string surfaceDescription)
        {
            (string fence, string guidance) = FormatRules();
            var builder = new StringBuilder();
            builder.AppendLine("[SKILL CANVAS DELIVERY]");
            builder.AppendLine($"Active Skill: {SkillName}. Delivery target: Project Canvas ({surfaceDescription}).");
            builder.AppendLine("Axiom renders the artifact source in your final answer inside the Project Canvas pane. Your answer must therefore BE the artifact, not a description, outline, or plan for one.");
            builder.AppendLine();
            builder.AppendLine("Output shape:");
            builder.AppendLine($"- Return exactly one ```{fence} fenced block containing the complete artifact.");
            builder.AppendLine($"- {guidance}");
            builder.AppendLine("- At most one short sentence before the block. Nothing after it. Never split the artifact across several blocks.");
            builder.AppendLine();
            builder.AppendLine("Environment (non-negotiable):");
            builder.AppendLine("- Fully offline. No external URLs, CDNs, webfonts, stylesheets, scripts, images, or libraries; each one fails silently and leaves a broken artifact.");
            builder.AppendLine("- Build visuals from CSS, inline SVG, or the canvas API. Never leave a placeholder box, an image reference, or text such as \"[chart goes here]\".");
            builder.AppendLine("- The pane is a narrow, user-resizable column (roughly 300-750px). Use relative units and let the layout reflow; never assume a desktop viewport width.");
            builder.AppendLine("- Do not claim a file was written, exported, downloaded, or saved. Producing the artifact is the whole deliverable.");
            builder.AppendLine("- Skip hidden deliberation: do not draft or rewrite the artifact in a reasoning pass first. Write the finished artifact directly as your visible answer.");

            if (!string.IsNullOrWhiteSpace(Contract))
            {
                builder.AppendLine();
                builder.AppendLine($"{SkillName} requirements:");
                builder.AppendLine(Contract.Trim());
            }

            builder.Append("[/SKILL CANVAS DELIVERY]");
            return builder.ToString();
        }

        /// <summary>
        /// The small-model path: a handful of imperative lines and one worked example. Long
        /// rule lists are what make compact models drift, so the whole block stays short.
        /// </summary>
        private string BuildStructuredInstruction(SkillCanvasTier tier)
        {
            int minimum = tier == SkillCanvasTier.Micro ? 4 : 6;
            var builder = new StringBuilder();
            builder.AppendLine("[SKILL CANVAS DELIVERY]");
            builder.AppendLine($"Active Skill: {SkillName}. Axiom builds the visual design; you supply the content in the exact format below.");
            builder.AppendLine();

            switch (SmallModelFormat)
            {
                case SkillSmallModelFormats.Outline:
                    builder.AppendLine($"Write at least {minimum} SLIDE sections. Use this format and nothing else:");
                    builder.AppendLine(SkillArtifactComposer.OutlineFormatDescription);
                    builder.AppendLine();
                    builder.AppendLine("Rules: one idea per SLIDE. Keep each line under 15 words. Two to four lines per slide. No HTML, no CSS, no code.");
                    break;

                case SkillSmallModelFormats.Chart:
                    builder.AppendLine("Use this format and nothing else:");
                    builder.AppendLine(SkillArtifactComposer.ChartFormatDescription);
                    builder.AppendLine();
                    builder.AppendLine("Rules: one category per line, a plain number after the bar. Use only numbers the user supplied. No HTML, no CSS, no code.");
                    break;

                default:
                    builder.AppendLine("Write the document in plain Markdown: a '# ' title, '## ' section headings, short paragraphs, '- ' lists, and Markdown tables where figures belong.");
                    builder.AppendLine();
                    builder.AppendLine("Rules: no HTML, no CSS, no code fences around the document. Headings and tables only.");
                    break;
            }

            builder.Append("[/SKILL CANVAS DELIVERY]");
            return builder.ToString();
        }
    }
}
