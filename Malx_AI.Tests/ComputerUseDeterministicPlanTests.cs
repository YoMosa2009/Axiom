using Malx_AI.ComputerUse;
using Xunit;

namespace Malx_AI.Tests;

public class ComputerUseDeterministicPlanTests
{
    [Fact]
    public void MultiStepBrowserAndAppRequestBuildsValidContract()
    {
        const string request = "open Microsoft Edge, then take me to my github repo 'Axiom', then open a new tab and go to YouTube";
        string navContext = ComputerUseTrustedNavigation.CreateContext("https://github.com/YoMosa2009/Axiom");

        string planJson = ComputerUseDeterministicPlanBuilder.BuildFallbackPlanJson(request, navContext);

        Assert.True(ComputerUseTaskContract.TryParse(planJson, request, out ComputerUseTaskContract? contract, out string error, out _, strictCitations: false), error);
        Assert.NotNull(contract);
        ComputerUseTaskProgress p1 = contract!.Progress([]);
        Assert.Contains("Microsoft Edge", p1.Current);
        Assert.Contains("https://github.com/YoMosa2009/Axiom", p1.Next);
    }

    [Fact]
    public void NotepadWithQuotedTextBuildsDesktopOutcomeWithRequiredText()
    {
        const string request = "open Notepad and type \"Hello Axiom\"";
        string planJson = ComputerUseDeterministicPlanBuilder.BuildFallbackPlanJson(request);

        Assert.True(ComputerUseTaskContract.TryParse(planJson, request, out ComputerUseTaskContract? contract, out string error, out _, strictCitations: false), error);
        Assert.NotNull(contract);
        string desc = contract!.Describe();
        Assert.Contains("Required application: Notepad", desc);
        Assert.Contains("Hello Axiom", desc);
    }

    [Fact]
    public void StandaloneNewTabClauseAppliesSeparateTabToNextBrowserOutcome()
    {
        const string request = "go to github.com, open a new tab, then go to youtube";
        string planJson = ComputerUseDeterministicPlanBuilder.BuildFallbackPlanJson(request);

        Assert.True(ComputerUseTaskContract.TryParse(planJson, request, out ComputerUseTaskContract? contract, out string error, out _, strictCitations: false), error);
        Assert.NotNull(contract);
        // Satisfy first destination
        var capture1 = new ComputerUseCapture
        {
            JpegBytes = [1],
            TargetWindowHandle = new IntPtr(10),
            ForegroundProcessName = "msedge",
            BrowserState = new ComputerUseBrowserState
            {
                DocumentAddress = "https://github.com/",
                Address = "https://github.com/",
                ActiveTabId = "t1",
                TabIds = ["t1"],
                HasTabTelemetry = true
            }
        };
        string obs = contract!.ObserveCurrentOutcome(capture1);
        Assert.Contains("[OUTCOME VERIFIED]", obs);
        Assert.Contains("separate tab: True", contract.Describe());
    }

    [Fact]
    public void SalvageModeAllowsNonLiteralRequiredTextInsteadOfAborting()
    {
        const string request = "open notepad and write a quick greeting";
        const string planWithParaphrasedText = """
            {"error":"","outcomes":[
              {"request_text":"open notepad and write a quick greeting","description":"Open Notepad and type greeting","kind":"desktop","destination":"","match":"exact","separate_tab":false,"application":"Notepad","required_text":["Hello there!"]}
            ]}
            """;

        // Strict mode still catches non-literal required_text
        Assert.False(ComputerUseTaskContract.TryParse(planWithParaphrasedText, request, out _, out _, out _, strictCitations: true));
        // Salvage mode permits it with advisory
        Assert.True(ComputerUseTaskContract.TryParse(planWithParaphrasedText, request, out var contract, out _, out string advisory, strictCitations: false));
        Assert.NotNull(contract);
        Assert.Contains("required_text", advisory);
    }
}
