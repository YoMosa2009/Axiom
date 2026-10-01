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
    public void NormalOutlineStillWorks()
    {
        string normal = "TITLE: Energy\nSLIDE: Solar\n- Panels\nSLIDE: Wind\n- Turbines";
        bool ok = SkillArtifactComposer.TryCompose(SkillSmallModelFormats.Outline, normal, out string html2);
        Assert.True(ok);
        Assert.Contains("Wind", html2);
    }
}
