using Xunit;

namespace Malx_AI.Tests;

public sealed class AxiomCapabilityRegistryTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), "AxiomCapabilityTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_ExposeExactlyFiveBuiltInSkillsAndFiveNativePlugins()
    {
        AxiomCapabilityRegistry registry = CreateRegistry();

        Assert.Equal(5, registry.Skills.Count(skill => skill.IsBuiltIn));
        Assert.Equal(5, registry.Plugins.Count);
        Assert.Contains(registry.Skills, skill => skill.Id == "pdf-studio");
        Assert.Contains(registry.Skills, skill => skill.Id == "slide-deck-studio");
        Assert.Contains(registry.Plugins, plugin => plugin.Id == AxiomCapabilityRegistry.ConnectedAppsPluginId);
    }

    [Fact]
    public void AttachmentsAndCustomSkills_PersistGlobally()
    {
        string statePath = Path.Combine(_testDirectory, "capabilities.json");
        var first = new AxiomCapabilityRegistry(statePath);
        first.EnsureLoaded();
        first.SetSkillAttached("document-summarizer", true);
        first.SetPluginAttached(AxiomCapabilityRegistry.FileIntelligencePluginId, true);
        AxiomSkillDefinition custom = first.AddCustomSkill(
            "Incident brief",
            "Writes operational incident briefs.",
            "State impact, timeline, root cause, remediation, and owner.",
            "incident,postmortem");

        var second = new AxiomCapabilityRegistry(statePath);
        second.EnsureLoaded();

        Assert.True(second.Skills.Single(skill => skill.Id == "document-summarizer").IsAttached);
        Assert.True(second.Plugins.Single(plugin => plugin.Id == AxiomCapabilityRegistry.FileIntelligencePluginId).IsAttached);
        Assert.True(second.Skills.Single(skill => skill.Id == custom.Id).IsAttached);
    }

    [Fact]
    public void SystemInstruction_LoadsFullSkillOnlyWhenRelevant()
    {
        AxiomCapabilityRegistry registry = CreateRegistry();
        registry.SetSkillAttached("pdf-studio", true);

        string unrelated = registry.BuildSystemInstruction("Hello there", "Normal Chat / Local");
        string relevant = registry.BuildSystemInstruction("Create a PDF report from this attachment", "Normal Chat / Cloud");

        Assert.Contains("PDF Studio: Design polished", unrelated);
        Assert.DoesNotContain("<skill name=\"PDF Studio\">", unrelated);
        Assert.Contains("<skill name=\"PDF Studio\">", relevant);
        Assert.Contains("Never claim", relevant, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WebResearchPlugin_ActivatesOnlyForRelevantRequests()
    {
        AxiomCapabilityRegistry registry = CreateRegistry();
        registry.SetPluginAttached(AxiomCapabilityRegistry.WebResearchPluginId, true);

        Assert.False(registry.ShouldUseWebResearch("Write a short poem"));
        Assert.True(registry.ShouldUseWebResearch("Verify the latest release documentation online"));
    }

    private AxiomCapabilityRegistry CreateRegistry()
    {
        var registry = new AxiomCapabilityRegistry(Path.Combine(_testDirectory, "capabilities.json"));
        registry.EnsureLoaded();
        return registry;
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDirectory))
            Directory.Delete(_testDirectory, true);
    }
}
