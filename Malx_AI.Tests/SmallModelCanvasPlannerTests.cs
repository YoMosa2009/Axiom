using System.Collections.Generic;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    /// <summary>
    /// The "Hello" chat: a 0.6B local model with Slide Deck Studio on was asked for a slide deck,
    /// then told "@ProjectCanvas", then "it must be an actual visual (SVG/HTML)". It never
    /// produced a deck. These pin how each of those turns is now planned.
    /// </summary>
    public class SmallModelCanvasPlannerTests
    {
        private const string DeckRequest = "Please make me a slide-deck/presentation about renewable energy.";
        private const string FirstNudge = "you were supposed to make it in the projectcanvas @ProjectCanvas";
        private const string SecondNudge = "it must be an actual visual (SVG/HTML) @ProjectCanvas";

        private static readonly SkillCanvasDirective SlideDeckStudio =
            new("slide-deck-studio", "Slide Deck Studio", SkillDeliverableFormats.Html, "contract", SkillSmallModelFormats.Outline);

        private static SkillCanvasDirective? NoSkill(string message) => null;

        private static SkillCanvasDirective? SlideSkill(string message) =>
            message.Contains("slide", System.StringComparison.OrdinalIgnoreCase) ? SlideDeckStudio : null;

        [Fact]
        public void AnAttachedSkillIsUsedForTheRequestThatMatchesIt()
        {
            SmallModelCanvasPlan? plan = SmallModelCanvasPlanner.Resolve(DeckRequest, ["Hello"], SlideSkill, false, SkillCanvasTier.Micro);

            Assert.NotNull(plan);
            Assert.Same(SlideDeckStudio, plan!.Directive);
            Assert.Equal(DeckRequest, plan.Task);
            Assert.False(plan.IsFollowUp);
        }

        [Fact]
        public void ADeckRequestIsACanvasTurnEvenWithoutASkill()
        {
            SmallModelCanvasPlan? plan = SmallModelCanvasPlanner.Resolve(DeckRequest, [], NoSkill, false, SkillCanvasTier.Micro);

            Assert.NotNull(plan);
            Assert.Equal(SkillSmallModelFormats.Outline, plan!.Directive.SmallModelFormat);
        }

        [Fact]
        public void AProjectCanvasNudgeInheritsTheEarlierDeckRequest()
        {
            SmallModelCanvasPlan? plan = SmallModelCanvasPlanner.Resolve(FirstNudge, ["Hello", DeckRequest], SlideSkill, true, SkillCanvasTier.Micro);

            Assert.NotNull(plan);
            Assert.True(plan!.IsFollowUp);
            Assert.Equal(DeckRequest, plan.Task);
            Assert.Same(SlideDeckStudio, plan.Directive);
        }

        [Fact]
        public void ASecondNudgeStillFindsTheDeckRequest()
        {
            SmallModelCanvasPlan? plan = SmallModelCanvasPlanner.Resolve(
                SecondNudge, ["Hello", DeckRequest, FirstNudge], NoSkill, true, SkillCanvasTier.Micro);

            Assert.NotNull(plan);
            Assert.Equal(DeckRequest, plan!.Task);
            Assert.Equal(SkillSmallModelFormats.Outline, plan.Directive.SmallModelFormat);
        }

        [Fact]
        public void ProjectCanvasWithNothingToInheritBecomesADocument()
        {
            SmallModelCanvasPlan? plan = SmallModelCanvasPlanner.Resolve(
                "@ProjectCanvas write a short poem about the sea", ["Hello"], NoSkill, true, SkillCanvasTier.Micro);

            Assert.NotNull(plan);
            Assert.Equal(SkillSmallModelFormats.Markdown, plan!.Directive.SmallModelFormat);
            Assert.Equal("write a short poem about the sea", plan.Task);
        }

        [Fact]
        public void OrdinaryChatIsNotACanvasTurn()
        {
            Assert.Null(SmallModelCanvasPlanner.Resolve("Hello", [], NoSkill, false, SkillCanvasTier.Micro));
            Assert.Null(SmallModelCanvasPlanner.Resolve("What causes wind?", ["Hello"], NoSkill, false, SkillCanvasTier.Compact));
        }

        [Fact]
        public void ModelsThatCanAuthorArtifactsAreLeftAlone()
        {
            Assert.Null(SmallModelCanvasPlanner.Resolve(DeckRequest, [], SlideSkill, true, SkillCanvasTier.Full));
        }

        [Fact]
        public void TheUserTurnCarriesTheTaskAndTheExactFormat()
        {
            var plan = new SmallModelCanvasPlan(SlideDeckStudio, DeckRequest, IsFollowUp: true);
            string turn = SmallModelCanvasPlanner.BuildUserTurn(plan);

            Assert.StartsWith("TASK: " + DeckRequest, turn);
            Assert.Contains("SLIDE: Why sleep matters", turn);
            Assert.Contains("(for a different topic)", turn);
            Assert.EndsWith("Begin your answer with \"TITLE:\".", turn);
            Assert.DoesNotContain("Python", turn);
            Assert.DoesNotContain("<!DOCTYPE", turn);
        }
    }
}
