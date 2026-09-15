using Malx_AI.ComputerUse;
using System.Text.Json;
using Xunit;

namespace Malx_AI.Tests;

public class ComputerUsePlanningTests
{
    private const string Request = "Open Notepad and then open Calculator.";
    // Typical usable output: linking words are not repeated, and irrelevant desktop
    // fields plus the optional top-level error field are omitted.
    private const string UsablePlan = """
        {"outcomes":[
         {"request_text":"Open Notepad","description":"Open Notepad","kind":"Desktop","application":"Notepad"},
         {"request_text":"open Calculator","description":"Open Calculator","kind":"desktop","application":"Calculator"}
        ]}
        """;

    [Fact]
    public void UsablePlanDoesNotRequireVerbatimConnectivesOrIrrelevantFields()
    {
        Assert.True(ComputerUseTaskContract.TryParse(UsablePlan, Request, out var contract, out string error), error);
        Assert.Equal("Open Notepad", contract!.Progress([]).Current);
        Assert.Equal("Open Calculator", contract.Progress([]).Next);
    }

    [Fact]
    public async Task MalformedPlanIsCorrectedWithoutEndingTheSession()
    {
        int calls = 0;
        var rejected = new List<string>();
        var result = await ComputerUsePlanning.CreateAsync(Request, "", (system, payload, token) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult("```json");
            using var json = JsonDocument.Parse(payload);
            Assert.Contains("outcomes", json.RootElement.GetProperty("validation_error").GetString());
            Assert.Equal("```json", json.RootElement.GetProperty("previous_plan").GetString());
            return Task.FromResult(UsablePlan);
        }, CancellationToken.None, (attempt, error, raw) => { rejected.Add(error); return Task.CompletedTask; });
        Assert.NotNull(result.Contract);
        Assert.Equal(2, result.Attempts);
        Assert.Single(rejected);
    }

    [Fact]
    public async Task MissingLaterOutcomeGetsSpecificRepairFeedback()
    {
        int calls = 0;
        var result = await ComputerUsePlanning.CreateAsync(Request, "", (system, payload, token) =>
        {
            calls++;
            if (calls == 1) return Task.FromResult("""{"outcomes":[{"request_text":"Open Notepad","description":"Open Notepad","kind":"desktop"}]}""");
            using var json = JsonDocument.Parse(payload);
            Assert.Contains("calculator", json.RootElement.GetProperty("validation_error").GetString());
            return Task.FromResult(UsablePlan);
        }, CancellationToken.None);
        Assert.NotNull(result.Contract);
        Assert.Equal(2, result.Attempts);
    }

    [Fact]
    public async Task RepeatedFailureIsBoundedAndPreservesTheReason()
    {
        int calls = 0;
        var result = await ComputerUsePlanning.CreateAsync(Request, "", (_, _, _) =>
        {
            calls++;
            return Task.FromResult("""{"error":"The requested destination could not be resolved."}""");
        }, CancellationToken.None);
        Assert.Null(result.Contract);
        Assert.Equal(3, calls);
        Assert.Contains("destination could not be resolved", result.Error);
    }

    [Fact]
    public async Task CancellationPreventsAnotherPlanningAttempt()
    {
        using var cts = new CancellationTokenSource();
        int calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ComputerUsePlanning.CreateAsync(Request, "", (_, _, _) =>
        {
            calls++;
            cts.Cancel();
            return Task.FromResult("{}");
        }, cts.Token));
        Assert.Equal(1, calls);
    }
}
