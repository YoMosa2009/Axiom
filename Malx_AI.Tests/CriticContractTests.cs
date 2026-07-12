using Xunit;

namespace Malx_AI.Tests;

public sealed class CriticContractTests
{
    [Fact]
    public void Parse_AcceptsJsonCleanPass()
    {
        CriticReport report = CriticContractParser.Parse("{\"status\":\"ok\",\"issues\":[]}");

        Assert.False(report.HasIssues);
        Assert.Equal(0, report.FindingsCount);
        Assert.Equal("ok", report.Status);
    }

    [Fact]
    public void Parse_ExtractsStructuredJsonIssue()
    {
        const string json = "{\"status\":\"issues\",\"issues\":[{\"severity\":\"high\",\"summary\":\"Button is inert\",\"evidence\":\"No click handler\",\"suggestedFix\":\"Wire the handler\"}]}";

        CriticReport report = CriticContractParser.Parse(json);

        CriticIssue issue = Assert.Single(report.Issues!);
        Assert.True(report.HasIssues);
        Assert.Equal(1, report.FindingsCount);
        Assert.Equal("high", issue.Severity);
        Assert.Equal("Button is inert", issue.Summary);
    }

    [Fact]
    public void Parse_FallsBackToNumberedFindings()
    {
        CriticReport report = CriticContractParser.Parse("1. Critical: output omits the required footer\n2. Low: spacing is inconsistent");

        Assert.True(report.HasIssues);
        Assert.Equal(2, report.FindingsCount);
    }
}
