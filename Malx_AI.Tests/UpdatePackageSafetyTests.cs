using Xunit;

namespace Malx_AI.Tests;

public sealed class UpdatePackageSafetyTests
{
    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("folder/../../outside.dll")]
    [InlineData("C:/Windows/system32/file.dll")]
    public void TryResolvePathUnderRoot_RejectsEscapes(string entry)
    {
        string root = Path.Combine(Path.GetTempPath(), "AxiomUpdateSafety", "root");

        bool safe = UpdatePackageSafety.TryResolvePathUnderRoot(root, entry, out _, out _);

        Assert.False(safe);
    }

    [Fact]
    public void TryResolvePathUnderRoot_AllowsNormalNestedFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "AxiomUpdateSafety", "root");

        bool safe = UpdatePackageSafety.TryResolvePathUnderRoot(root, "runtimes/win-x64/native.dll", out string relative, out string fullPath);

        Assert.True(safe);
        Assert.Equal("runtimes/win-x64/native.dll", relative);
        Assert.StartsWith(Path.GetFullPath(root), fullPath, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("ChatHistory/chat_1.json")]
    [InlineData("Models/user-model.gguf")]
    [InlineData("axiom_data.db-wal")]
    [InlineData("settings_client_secret.txt")]
    [InlineData("token.dpapi")]
    public void IsProtectedUserDataPath_ProtectsUserState(string path)
        => Assert.True(UpdatePackageSafety.IsProtectedUserDataPath(path));

    [Theory]
    [InlineData("Malx_AI.exe")]
    [InlineData("EmbeddingModels/model.gguf")]
    [InlineData("runtimes/win-x64/native.dll")]
    public void IsProtectedUserDataPath_AllowsPackagedFiles(string path)
        => Assert.False(UpdatePackageSafety.IsProtectedUserDataPath(path));
}
