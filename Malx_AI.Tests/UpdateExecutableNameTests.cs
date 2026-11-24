using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

/// <summary>
/// V1.12 renamed the app to "Axiom Harness.exe" but keeps Malx_AI.exe, because V1.11 copies update
/// and restart through that name. The updater must work whichever name the install or the running
/// copy uses.
/// </summary>
public sealed class UpdateExecutableNameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"AxiomExeNameTest-{Guid.NewGuid():N}");

    public UpdateExecutableNameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void AnInstallWithBothNamesStartsAxiomHarness()
    {
        File.WriteAllText(Path.Combine(_root, "Axiom Harness.exe"), "");
        File.WriteAllText(Path.Combine(_root, "Malx_AI.exe"), "");
        Assert.Equal(Path.Combine(_root, "Axiom Harness.exe"), UpdateApplyService.InstalledExecutablePath(_root));
    }

    [Fact]
    public void AnOlderInstallStartsMalxAi()
    {
        File.WriteAllText(Path.Combine(_root, "Malx_AI.exe"), "");
        Assert.Equal(Path.Combine(_root, "Malx_AI.exe"), UpdateApplyService.InstalledExecutablePath(_root));
    }

    [Theory]
    [InlineData("Malx_AI", "Malx_AI.exe")]
    [InlineData("malx_ai", "Malx_AI.exe")]
    [InlineData("Axiom Harness", "Axiom Harness.exe")]
    public void TheRunningCopyIsMatchedByItsOwnName(string processName, string expected)
        => Assert.Equal(expected, UpdateApplyService.ExecutableFileNameForProcess(processName));
}
