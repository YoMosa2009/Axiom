using System;
using System.IO;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>
    /// Covers the decision a Skill makes before a single token is generated: whether this turn
    /// produces a rendered artifact, and what the model is asked for given its size.
    /// </summary>
    public class SkillCanvasRoutingTests : IDisposable
    {
        private readonly string _statePath = Path.Combine(
            Path.GetTempPath(),
            "AxiomSkillTests",
            Guid.NewGuid().ToString("N"),
            "capabilities.json");

        private AxiomCapabilityRegistry NewRegistry()
        {
            var registry = new AxiomCapabilityRegistry(_statePath);
            registry.EnsureLoaded();
            return registry;
        }

        public void Dispose()
        {
            string? folder = Path.GetDirectoryName(_statePath);
            if (folder != null && Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
            GC.SuppressFinalize(this);
        }

        [Fact]
        public void ResolveCanvasDirective_ReturnsNothingWhileNoSkillIsAttached()
        {
            Assert.Null(NewRegistry().ResolveCanvasDirective("Make me a slide deck about Q3 retention"));
        }

        [Fact]
        public void ResolveCanvasDirective_RoutesASlideRequestToSlideDeckStudio()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);

            SkillCanvasDirective? directive = registry.ResolveCanvasDirective("Make me a slide deck about Q3 retention");

            Assert.NotNull(directive);
            Assert.Equal("slide-deck-studio", directive!.SkillId);
            Assert.Equal(SkillDeliverableFormats.Html, directive.Format);
            Assert.Equal(SkillSmallModelFormats.Outline, directive.SmallModelFormat);
        }

        [Fact]
        public void ResolveCanvasDirective_IgnoresUnrelatedRequestsForAnAttachedSkill()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);

            Assert.Null(registry.ResolveCanvasDirective("What is the capital of Portugal?"));
        }

        [Fact]
        public void ResolveCanvasDirective_LeavesAPlainNumberQuestionInChat()
        {
            // Data Analysis activates on "average" for its procedure, but a one-number answer
            // should not turn into a rendered report.
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("data-analysis", true);

            Assert.Null(registry.ResolveCanvasDirective("What is the average of column B?"));
            Assert.NotNull(registry.ResolveCanvasDirective("Chart the average revenue by region"));
        }

        [Fact]
        public void ResolveCanvasDirective_PicksTheSkillTheRequestMatchesMostStrongly()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);
            registry.SetSkillAttached("data-analysis", true);

            SkillCanvasDirective? directive = registry.ResolveCanvasDirective(
                "Build a slide deck presentation charting our revenue");

            Assert.Equal("slide-deck-studio", directive?.SkillId);
        }

        [Fact]
        public void ResolveCanvasDirective_NeverReturnsAChatOnlySkill()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("document-summarizer", true);
            registry.SetSkillAttached("code-review", true);

            Assert.Null(registry.ResolveCanvasDirective("Summarize this document and review the code"));
        }

        [Fact]
        public void CustomSkill_CanRenderAndIsMatchedOnItsOwnActivationTerms()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.AddCustomSkill(
                "Runbook Studio",
                "Writes runbooks",
                "Write the runbook.",
                "runbook, on-call guide",
                SkillDeliverableFormats.Html);

            SkillCanvasDirective? directive = registry.ResolveCanvasDirective("Write the deploy runbook");

            Assert.Equal("Runbook Studio", directive?.SkillName);
        }

        [Fact]
        public void BuildSystemInstruction_LoadsTheAttachedSkillProcedureAndItsLimits()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);

            string instruction = registry.BuildSystemInstruction("Build a slide deck", "Normal Chat");

            Assert.Contains("Slide Deck Studio", instruction);
            Assert.Contains("Limits:", instruction);
            Assert.Contains("Never return only an outline", instruction);
        }

        [Fact]
        public void FullTier_AsksForTheCompleteArtifactAndTheSkillContract()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);
            SkillCanvasDirective directive = registry.ResolveCanvasDirective("slide deck")!;

            string instruction = directive.BuildSystemInstruction("Normal Chat", SkillCanvasTier.Full);

            Assert.Contains("```html", instruction);
            Assert.Contains("must therefore BE the artifact", instruction);
            Assert.Contains("16:9", instruction);
            Assert.Contains("Fully offline", instruction);
        }

        [Theory]
        [InlineData(SkillCanvasTier.Micro)]
        [InlineData(SkillCanvasTier.Compact)]
        public void SmallTiers_AskForTheOutlineFormatInsteadOfHtml(SkillCanvasTier tier)
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);
            SkillCanvasDirective directive = registry.ResolveCanvasDirective("slide deck")!;

            string instruction = directive.BuildSystemInstruction("Normal Chat", tier);

            Assert.Contains("SLIDE:", instruction);
            Assert.Contains("No HTML", instruction);
            Assert.DoesNotContain("<!DOCTYPE html>", instruction);
            // The whole point of the small tier is brevity.
            Assert.True(instruction.Length < 900, $"small-tier prompt was {instruction.Length} chars");
        }

        [Fact]
        public void MicroTierAsksForFewerSlidesThanCompact()
        {
            var directive = new SkillCanvasDirective("x", "Deck", SkillDeliverableFormats.Html, "", SkillSmallModelFormats.Outline);

            Assert.Contains("at least 4 SLIDE", directive.BuildSystemInstruction("Normal Chat", SkillCanvasTier.Micro));
            Assert.Contains("at least 6 SLIDE", directive.BuildSystemInstruction("Normal Chat", SkillCanvasTier.Compact));
        }

        [Fact]
        public void ChartSkill_AsksSmallModelsForLabelledNumbers()
        {
            var directive = new SkillCanvasDirective("x", "Data", SkillDeliverableFormats.Html, "", SkillSmallModelFormats.Chart);

            string instruction = directive.BuildSystemInstruction("Normal Chat", SkillCanvasTier.Micro);

            Assert.Contains("CHART:", instruction);
            Assert.Contains("| <number>", instruction);
        }

        [Fact]
        public void AttachmentSurvivesAReload()
        {
            AxiomCapabilityRegistry registry = NewRegistry();
            registry.SetSkillAttached("slide-deck-studio", true);

            Assert.NotNull(new AxiomCapabilityRegistry(_statePath).ResolveCanvasDirective("slide deck"));
        }

        [Fact]
        public void UnmeasuredModelsKeepTheFullContract()
        {
            // An unreadable or cloud model is unmeasured, not small.
            Assert.Equal(SkillCanvasTier.Full, LocalModelCapabilityProfile.ResolveCanvasTier(null));
        }
    }
}
