using Malx_AI;
using Xunit;

namespace Malx_AI.Tests;

public sealed class UpdateStoragePathsTests
{
    [Fact]
    public void ResolveRoot_UsesConfiguredAbsolutePath()
    {
        string fallback = Path.Combine(Path.GetTempPath(), "AxiomFallback");
        string configured = Path.Combine(Path.GetPathRoot(fallback)!, "Axiom-Updates");

        Assert.Equal(Path.GetFullPath(configured), UpdateStoragePaths.ResolveRoot(configured, fallback));
    }

    [Fact]
    public void ResolveRoot_RejectsRelativeConfiguredPath()
    {
        string fallback = Path.Combine(Path.GetTempPath(), "AxiomFallback");

        Assert.Equal(Path.GetFullPath(fallback), UpdateStoragePaths.ResolveRoot("relative\\updates", fallback));
    }
}
