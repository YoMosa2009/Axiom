using System.Collections.Generic;
using System.Linq;
using Malx_AI.Agent;
using Xunit;

namespace Malx_AI.Tests;

public sealed class AgentContextCompactionTests
{
    [Fact]
    public void SuccessfulReadFileWithExceptionInContentIsSummarisedAsRead()
    {
        // A C# file containing "Exception" in its code should not be treated as an error.
        var call = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\proj\\Foo.cs"
        });
        var observation = "1\tusing System;\n2\t\n3\tpublic class Foo\n4\t{\n5\t\tpublic void Bar()\n6\t\t{\n7\t\t\tthrow new Exception(\"something went wrong\");\n8\t\t}\n9\t}";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("[Read C:\\proj\\Foo.cs]", result[0].Observation);
    }

    [Fact]
    public void SuccessfulReadFileWithExceptionInFirstLineIsSummarisedAsRead()
    {
        // Even if the first line contains "Exception", a successful read is a read.
        var call = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\proj\\ErrorHelper.cs"
        });
        var observation = "1\t// Handles Exception parsing\n2\tpublic static void Parse() { }";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("[Read C:\\proj\\ErrorHelper.cs]", result[0].Observation);
    }

    [Fact]
    public void RealFailureIsSummarisedAsError()
    {
        // A genuine failure starts with "ERROR:" and should be summarised as the first line.
        var call = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\missing.cs"
        });
        var observation = "ERROR: No such file: C:\\missing.cs";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("ERROR: No such file: C:\\missing.cs", result[0].Observation);
    }

    [Fact]
    public void RealFailureMentionsExceptionAndIsSummarisedAsError()
    {
        // A real failure that also contains "Exception" in the error message.
        var call = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\missing.cs"
        });
        var observation = "ERROR: System.IO.FileNotFoundException: Could not find file 'C:\\missing.cs'.";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("ERROR: System.IO.FileNotFoundException: Could not find file 'C:\\missing.cs'.", result[0].Observation);
    }

    [Fact]
    public void SuccessfulRunCommandOutputMentioningExceptionIsSummarisedAsOk()
    {
        // A successful command whose output mentions "Exception" should not be treated as an error.
        // The observation is longer than maxChars (200) so it gets summarised.
        var call = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string>
        {
            ["command"] = "dotnet build"
        });
        var observation = "[Exit code: 0 | Duration: 3200ms]\nBuild completed.\nNote: Some code paths throw Exception, but the build succeeded.\n"
            + "This is extra padding to push the observation over 200 characters so it gets summarised.";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("[Ran dotnet build: ok]", result[0].Observation);
    }

    [Fact]
    public void FailedRunCommandIsSummarisedAsError()
    {
        // A failed command starts with "ERROR:" and should be summarised as the first line.
        var call = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string>
        {
            ["command"] = "dotnet build"
        });
        var observation = "ERROR: [Exit code: 1 | Duration: 2100ms]\nC:\\proj\\Foo.cs(5,1): error CS0103: The name 'Exception' does not exist in the current context";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("ERROR: [Exit code: 1 | Duration: 2100ms]", result[0].Observation);
    }

    [Fact]
    public void RecentStepsAreKeptFull()
    {
        // The most recent steps should not be compacted.
        var call = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\proj\\Foo.cs"
        });
        var observation = "1\tusing System;\n2\t\n3\tpublic class Foo\n4\t{\n5\t\tpublic void Bar()\n6\t\t{\n7\t\t\tthrow new Exception(\"something went wrong\");\n8\t\t}\n9\t}";

        var history = new List<AgentExchange>
        {
            new AgentExchange(call, observation),
            new AgentExchange(call, observation),
            new AgentExchange(call, observation),
            new AgentExchange(call, observation),
            new AgentExchange(call, observation),
            new AgentExchange(call, observation),
        };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 5);

        Assert.Equal(6, result.Count);
        // The last 5 should be kept full
        Assert.Equal(observation, result.Skip(1).First().Observation);
        // Only the first one should be compacted
        Assert.Equal("[Read C:\\proj\\Foo.cs]", result.First().Observation);
    }

    [Fact]
    public void WriteFileSummaryIsCorrect()
    {
        var call = new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\proj\\index.html",
            ["content"] = "<html></html>"
        });
        var observation = "Successfully wrote 15 bytes (1 lines) to C:\\proj\\index.html.";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("[Wrote C:\\proj\\index.html]", result[0].Observation);
    }

    [Fact]
    public void EmptyObservationBecomesNoOutput()
    {
        var call = new AgentToolCall(AgentToolNames.ListDirectory, new Dictionary<string, string>
        {
            ["path"] = "C:\\empty"
        });
        var observation = string.Empty;

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.Equal("(no output)", result[0].Observation);
    }

    [Fact]
    public void ARefusedWriteIsNotSummarisedAsWritten()
    {
        var call = new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\Windows\\x.txt"
        });
        var observation = "Refused: Blocked: writing inside protected Windows system folders is refused in both modes.";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.StartsWith("Refused:", result[0].Observation);
        Assert.DoesNotContain("[Wrote", result[0].Observation);
    }

    [Fact]
    public void ABlockedRepeatIsNotSummarisedAsRun()
    {
        var call = new AgentToolCall(AgentToolNames.RunCommand, new Dictionary<string, string>
        {
            ["command"] = "npm test"
        });
        var observation = "Blocked: this exact run_command call has already been made 3 times in this task, and repeating it will not change the result.\nUse the earlier results.";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.StartsWith("Blocked:", result[0].Observation);
        Assert.DoesNotContain("Use the earlier results", result[0].Observation);
    }

    [Fact]
    public void ADeclinedStepIsNotSummarisedAsRead()
    {
        var call = new AgentToolCall(AgentToolNames.ReadFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\secret.txt"
        });
        var observation = "The user declined this step. Do not retry it; work around it or explain what you need.";

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0);

        Assert.Single(result);
        Assert.StartsWith("The user declined", result[0].Observation);
        Assert.DoesNotContain("[Read", result[0].Observation);
    }

    [Fact]
    public void ALongRefusalIsTrimmedToTheLimit()
    {
        var call = new AgentToolCall(AgentToolNames.WriteFile, new Dictionary<string, string>
        {
            ["path"] = "C:\\a.txt"
        });
        var observation = "Refused: " + new string('x', 500);

        var history = new List<AgentExchange> { new AgentExchange(call, observation) };

        var result = AgentContextManager.CompactExchanges(history, recentKeepCount: 0, olderMaxChars: 50);

        Assert.Single(result);
        Assert.Equal(51, result[0].Observation.Length);
        Assert.EndsWith("…", result[0].Observation);
    }
}
