using System.Collections.Generic;
using Xunit;

namespace Malx_AI.Tests;

public sealed class SmartContextCompactionTests
{
    [Theory]
    [InlineData("user", "anything", true, MessageImportance.High)]
    [InlineData("user", "hello", false, MessageImportance.Low)]
    [InlineData("user", "This must preserve backward compatibility.", false, MessageImportance.High)]
    [InlineData("assistant", "```csharp\nthrow new Exception();\n```", false, MessageImportance.High)]
    [InlineData("critic", "Concise finding", false, MessageImportance.High)]
    [InlineData("system", "context pressure: 42%", false, MessageImportance.Low)]
    public void ClassifyImportance_UsesPinnedContentAndRoleSignals(
        string role,
        string content,
        bool pinned,
        MessageImportance expected)
    {
        Assert.Equal(expected, SmartContextCompactionEngine.ClassifyImportance(role, content, pinned));
    }

    [Theory]
    [InlineData("user", "summarize this", false, true, MessageImportance.High)]
    [InlineData("user", "ok", false, false, MessageImportance.Low)]
    [InlineData("assistant", "ok", false, true, MessageImportance.Low)]
    public void ClassifyImportance_ProtectsShortMessagesWithActiveAttachment(
        string role,
        string content,
        bool pinned,
        bool hasActiveAttachment,
        MessageImportance expected)
    {
        Assert.Equal(expected, SmartContextCompactionEngine.ClassifyImportance(role, content, pinned, hasActiveAttachment));
    }

    [Theory]
    [InlineData("custom-endpoint", 70)]
    [InlineData("qwen2.5:7b", 75)]
    [InlineData("nvidia/nemotron-mini", 70)]
    [InlineData("gemma-4-27b", 80)]
    [InlineData("llama3.1:8b", 75)]
    [InlineData("", 75)]
    public void ResolveDefaultCeiling_MatchesKnownModelPatterns(string modelName, int expectedCeiling)
    {
        Assert.Equal(expectedCeiling, SmartContextCompactionEngine.ResolveDefaultCeiling(modelName));
    }

    [Fact]
    public void ExtendRemovalToAvoidOrphanedTurns_PullsInUnprotectedPartner()
    {
        // index: 0=user(removed), 1=assistant(unprotected) -- assistant should be pulled in too,
        // otherwise it would later be silently dropped for lacking a preceding user turn.
        var messages = new List<(string Role, bool IsProtected)>
        {
            ("user", false),
            ("assistant", false),
        };
        var indicesToRemove = new HashSet<int> { 0 };

        var result = SmartContextCompactionEngine.ExtendRemovalToAvoidOrphanedTurns(messages, indicesToRemove);

        Assert.Contains(0, result);
        Assert.Contains(1, result);
    }

    [Fact]
    public void ExtendRemovalToAvoidOrphanedTurns_NeverPullsInAProtectedPartner()
    {
        var messages = new List<(string Role, bool IsProtected)>
        {
            ("user", false),
            ("assistant", true), // e.g. pinned, high-importance, or the currently-streaming reply
        };
        var indicesToRemove = new HashSet<int> { 0 };

        var result = SmartContextCompactionEngine.ExtendRemovalToAvoidOrphanedTurns(messages, indicesToRemove);

        Assert.Contains(0, result);
        Assert.DoesNotContain(1, result);
    }

    [Fact]
    public void ExtendRemovalToAvoidOrphanedTurns_NeverRescuesAMessageOutOfRemoval()
    {
        var messages = new List<(string Role, bool IsProtected)>
        {
            ("user", false),
            ("assistant", false),
            ("user", false),
        };
        var indicesToRemove = new HashSet<int> { 0, 1, 2 };

        var result = SmartContextCompactionEngine.ExtendRemovalToAvoidOrphanedTurns(messages, indicesToRemove);

        Assert.Equal(3, result.Count);
    }
}
