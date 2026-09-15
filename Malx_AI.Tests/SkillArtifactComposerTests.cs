using Malx_AI;
using Xunit;

namespace Malx_AI.Tests
{
    public class SkillArtifactComposerTests
    {
        private const string SmallModelDeck = """
            TITLE: Q3 Retention
            SUBTITLE: What changed and why
            SLIDE: Churn fell for the first time in four quarters
            - Monthly churn dropped to 3.1%
            - Driven by the onboarding rewrite
            NOTE: Lead with the number, not the project.
            SLIDE: Onboarding completion is the lever
            - Completion rose from 54% to 71%
            - Completers churn at a third of the rate
            """;

        [Fact]
        public void ParseOutline_ReadsTitleSubtitleSlidesAndNotes()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(SmallModelDeck);

            Assert.Equal("Q3 Retention", outline.Title);
            Assert.Equal("What changed and why", outline.Subtitle);
            Assert.Equal(2, outline.Slides.Count);
            Assert.Equal("Churn fell for the first time in four quarters", outline.Slides[0].Title);
            Assert.Equal(2, outline.Slides[0].Bullets.Count);
            Assert.Equal("Lead with the number, not the project.", outline.Slides[0].Notes);
            Assert.Equal("Completion rose from 54% to 71%", outline.Slides[1].Bullets[0]);
        }

        [Theory]
        [InlineData("SLIDE 3: Numbered headings are still slides")]
        [InlineData("**SLIDE:** Bolded headings are still slides")]
        [InlineData("SLIDE - Dashed headings are still slides")]
        public void ParseOutline_AcceptsTheWaysModelsActuallyWriteTheKeyword(string line)
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline("TITLE: Deck\n" + line + "\n- point");

            Assert.Single(outline.Slides);
            Assert.EndsWith("are still slides", outline.Slides[0].Title);
        }

        [Fact]
        public void ParseOutline_TreatsMarkdownHeadingsAsSlides()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(
                "# Migration plan\n## Phase one\n- Freeze writes\n## Phase two\n- Backfill");

            Assert.Equal("Migration plan", outline.Title);
            Assert.Equal(2, outline.Slides.Count);
            Assert.Equal("Phase two", outline.Slides[1].Title);
        }

        [Fact]
        public void ParseOutline_KeepsUnprefixedLinesAsBullets()
        {
            // A small model that forgets the "-" should not lose its content.
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(
                "SLIDE: Costs\nCompute is 60% of spend\nStorage is 25%");

            Assert.Equal(2, outline.Slides[0].Bullets.Count);
        }

        [Fact]
        public void ParseOutline_IgnoresCodeFences()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline("```\nSLIDE: Inside a fence\n- point\n```");

            Assert.Single(outline.Slides);
            Assert.Equal("Inside a fence", outline.Slides[0].Title);
        }

        [Fact]
        public void ComposeSlideDeck_ProducesANavigableSelfContainedDocument()
        {
            string html = SkillArtifactComposer.ComposeSlideDeck(SkillArtifactComposer.ParseOutline(SmallModelDeck));

            Assert.StartsWith("<!DOCTYPE html>", html);
            Assert.Contains("Q3 Retention", html);
            // Title slide plus both content slides.
            Assert.Equal(3, CountOccurrences(html, "class=\"slide"));
            Assert.Contains("1 / 3", html);
            Assert.Contains("ArrowRight", html);
            Assert.Contains("Lead with the number", html);
        }

        [Fact]
        public void ComposeSlideDeck_FetchesNothingFromTheNetwork()
        {
            string html = SkillArtifactComposer.ComposeSlideDeck(SkillArtifactComposer.ParseOutline(SmallModelDeck));

            // The canvas WebView2 is offline: any remote reference renders as a broken artifact.
            Assert.DoesNotContain("http://", html);
            Assert.DoesNotContain("https://", html);
            Assert.DoesNotContain("<img", html);
        }

        [Fact]
        public void ComposeSlideDeck_EscapesModelSuppliedText()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(
                "TITLE: A & B\nSLIDE: <script>alert(1)</script>\n- one\nSLIDE: Second\n- two");

            string html = SkillArtifactComposer.ComposeSlideDeck(outline);

            Assert.DoesNotContain("<script>alert(1)</script>", html);
            Assert.Contains("&lt;script&gt;", html);
            Assert.Contains("A &amp; B", html);
        }

        [Fact]
        public void ComposeSlideDeck_RendersBoldBulletsAsEmphasis()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(
                "SLIDE: One\n- **3.1%** monthly churn\nSLIDE: Two\n- flat");

            string html = SkillArtifactComposer.ComposeSlideDeck(outline);

            Assert.Contains("<b>3.1%</b>", html);
        }

        [Fact]
        public void CanComposeDeck_RejectsASingleSlide()
        {
            // One slide is a paragraph, not a deck: better to leave the prose answer alone.
            Assert.False(SkillArtifactComposer.CanComposeDeck(
                SkillArtifactComposer.ParseOutline("TITLE: Only\nSLIDE: One thing\n- a point")));
        }

        [Fact]
        public void ParseChart_ReadsLabelledValues()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(
                "TITLE: Spend\nCHART: USD per month\nCompute | 62000\nStorage | 25,500\nNetwork: 4200");

            Assert.True(SkillArtifactComposer.CanComposeChart(outline));
            Assert.Equal(3, outline.Chart.Count);
            Assert.Equal("Compute", outline.Chart[0].Label);
            Assert.Equal(25500, outline.Chart[1].Value);
            Assert.Equal(4200, outline.Chart[2].Value);
        }

        [Fact]
        public void ComposeChartReport_DrawsBarsAndKeepsTheFiguresCheckable()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline(
                "TITLE: Spend\nCHART: USD per month\nCompute | 62000\nStorage | 25500");

            string html = SkillArtifactComposer.ComposeChartReport(outline);

            Assert.Contains("<svg", html);
            Assert.Equal(2, CountOccurrences(html, "class=\"bar\""));
            Assert.Contains("<table>", html);
            Assert.Contains("62,000", html);
        }

        [Fact]
        public void ComposeBarChartSvg_ScalesTheLongestBarToTheFullWidth()
        {
            ComposedOutline outline = SkillArtifactComposer.ParseOutline("CHART: x\nBig | 100\nSmall | 10");

            string svg = SkillArtifactComposer.ComposeBarChartSvg(outline.Chart, "x");

            // 640 - 150 label - 64 value gutter = 426 available to the largest value.
            Assert.Contains("width=\"426\"", svg);
            Assert.Contains("width=\"42.6\"", svg);
        }

        [Fact]
        public void TryCompose_BuildsADeckFromOutlineOutput()
        {
            Assert.True(SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline, SmallModelDeck, out string html));
            Assert.Contains("1 / 3", html);
        }

        [Fact]
        public void TryCompose_DeclinesWhenTheResponseHasNoStructure()
        {
            Assert.False(SkillArtifactComposer.TryCompose(
                SkillSmallModelFormats.Outline,
                "I can help with that. What audience is the deck for?",
                out _));
        }

        [Fact]
        public void TryCompose_LeavesMarkdownSkillsToTheExistingDocumentRenderer()
        {
            Assert.False(SkillArtifactComposer.TryCompose(
                SkillSmallModelFormats.Markdown,
                "# Report\n## Findings\n- one\n- two",
                out _));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            for (int i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
                 i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal))
            {
                count++;
            }
            return count;
        }
    }
}
