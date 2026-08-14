using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class JavaExecutionServiceTests
{
    [Fact]
    public void TryExtractExplicitCode_ExtractsJavaFence()
    {
        bool found = JavaExecutionService.TryExtractExplicitCode(
            "Run this:\n```java\nclass Main { public static void main(String[] args) {} }\n```",
            out string code);

        Assert.True(found);
        Assert.StartsWith("class Main", code, StringComparison.Ordinal);
    }

    [Fact]
    public void TryExtractExplicitCode_DoesNotTreatJavaScriptAsJava()
    {
        bool found = JavaExecutionService.TryExtractExplicitCode(
            "```javascript\nconsole.log('hello');\n```",
            out string code);

        Assert.False(found);
        Assert.Empty(code);
    }
}
