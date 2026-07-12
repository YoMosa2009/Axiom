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
}
