using System.Linq;
using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public class SkillArtifactComposerLooseOutlineTests
{
    const string LooseText = """
Here's a concise, evidence-based slide deck about renewable energy:

**Title:** *Renewable Energy Overview: A Path to Sustainable Future*

**Subtitle:** *Sources, technologies, and impact across the globe*

- **Definition:** Renewable energy refers to energy obtained from natural sources like sunlight, wind, water, geothermal, and biomass that are replenished over time.
- **Key sources:** Solar PV (photovoltaic), wind turbines, hydroelectric power, geothermal heat pumps, and biomass energy.
- **Technology overview:**
  - **Solar:** 20% of global electricity comes from solar power. Photovoltaic panels convert sunlight to electricity.
  - **Wind:** 100+ gigawatts of wind power is now being harnessed globally through offshore wind farms and onshore wind.
  - **Hydro:** Waterfalls and dams are the largest renewable energy sources, contributing over 40% of global electricity generation.
- **Global production:** Solar PV accounts for about 26% of global electricity generation in 2023 (IEA), while solar thermal systems supply roughly 15% of total power.
- **Environmental benefits:** Renewables produce little greenhouse gas emissions compared to fossil fuels, and their use supports climate goals.

Let me build the deck.
""";

    [Fact]
    public void ParseLooseOutlineOnLooseTextGivesExpectedDeck()
    {
        var outline = SkillArtifactComposer.ParseLooseOutline(LooseText);

        Assert.Equal("Renewable Energy Overview: A Path to Sustainable Future", outline.Title);
        Assert.Equal("Sources, technologies, and impact across the globe", outline.Subtitle);
        Assert.Equal(5, outline.Slides.Count);

        var titles = outline.Slides.Select(s => s.Title).ToArray();
        Assert.Equal(new[] { "Definition", "Key sources", "Technology overview", "Global production", "Environmental benefits" }, titles);

        var techSlide = outline.Slides.Single(s => s.Title == "Technology overview");
        Assert.Equal(3, techSlide.Bullets.Count);
    }

    [Fact]
    public void TryComposeWithLooseTextReturnsTrueAndContainsKeySources()
    {
        bool ok = SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline, LooseText, out string html);
        Assert.True(ok);
        Assert.Contains("Key sources", html);
    }

    [Fact]
    public void TryComposeWithProseOnlyReturnsFalse()
    {
        bool ok = SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline,
            "Renewable energy is good for the planet and the economy.", out _);
        Assert.False(ok);
    }

    [Fact]
    public void ARepeatedTitleLineStartsANewSlide()
    {
        // Live 0.6B reply shape: every slide headed with "TITLE:" instead of "SLIDE:".
        const string reply = "TITLE: Renewable Energy for the Future\nSLIDE: What we already know\n- Solar is growing fast\n- Wind is significant\nTITLE: The science behind renewables\n- Panels convert sunlight\n- Turbines capture wind\nTITLE: Making renewables accessible\n- Investment is rising";

        ComposedOutline outline = SkillArtifactComposer.ParseOutline(reply);

        Assert.Equal("Renewable Energy for the Future", outline.Title);
        Assert.Equal(new[] { "What we already know", "The science behind renewables", "Making renewables accessible" }, outline.Slides.Select(s => s.Title).ToArray());
        Assert.Equal(2, outline.Slides[1].Bullets.Count);
    }

    [Fact]
    public void ATitleThenSlideLinePairIsOneSlide()
    {
        const string reply = "TITLE: Renewable Energy\nTITLE: Cutting emissions\nSLIDE: Renewables cut warming\n- Solar reduces fossil use\nTITLE: Adopting renewables\n- Use solar at home";

        ComposedOutline outline = SkillArtifactComposer.ParseOutline(reply);

        Assert.Equal(new[] { "Cutting emissions", "Adopting renewables" }, outline.Slides.Select(s => s.Title).ToArray());
        Assert.Equal(new[] { "Renewables cut warming", "Solar reduces fossil use" }, outline.Slides[0].Bullets.ToArray());
    }

    [Fact]
    public void BoldLeadInsWithoutColonsBecomeSlides()
    {
        // Live 0.6B reply shape: "- **What is renewable energy?** text" and "- **Solar power** converts...".
        const string reply = "TITLE: The Power of Nature\nSUBTITLE: A simple guide\n- **What is renewable energy?** Energy that is replenished naturally.\n- **Solar power** converts sunlight into electricity.\n- **Wind power** turns air movement into electricity.";

        Assert.True(SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline, reply, out string html));
        ComposedOutline outline = SkillArtifactComposer.ParseLooseOutline(reply);
        Assert.Equal(new[] { "What is renewable energy?", "Solar power", "Wind power" }, outline.Slides.Select(s => s.Title).ToArray());
        Assert.Contains("converts sunlight into electricity.", outline.Slides[1].Bullets[0]);
        Assert.Contains("Solar power", html);
    }

    [Fact]
    public void ChartRowsCountEvenWithoutAChartLine()
    {
        // Live 0.6B reply shape: the CHART: line was dropped in every sample.
        const string reply = "TITLE: Sales by month\nJanuary | 120\nFebruary | 95\nMarch | 140";

        Assert.True(SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Chart, reply, out string html));
        Assert.Contains("February", html);
    }

    [Fact]
    public void ADeckWithColonFactsIsNotMistakenForAChart()
    {
        const string reply = "TITLE: Energy\nSLIDE: Solar share\n- Share: 26%\nSLIDE: Wind\n- Turbines";

        ComposedOutline outline = SkillArtifactComposer.ParseOutline(reply);

        Assert.Empty(outline.Chart);
        Assert.Equal(2, outline.Slides.Count);
    }

    [Fact]
    public void PlainBulletsWithNoHeadingsAreSplitIntoSlides()
    {
        const string reply = "TITLE: Energy\n- one\n- two\n- three\n- four\n- five";

        Assert.True(SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline, reply, out _));
        Assert.Equal(2, SkillArtifactComposer.ParseLooseOutline(reply).Slides.Count);
    }

    [Fact]
    public void NormalOutlineStillWorks()
    {
        string normal = "TITLE: Energy\nSLIDE: Solar\n- Panels\nSLIDE: Wind\n- Turbines";
        bool ok = SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline, normal, out string html2);
        Assert.True(ok);
        Assert.Contains("Wind", html2);
    }
}
