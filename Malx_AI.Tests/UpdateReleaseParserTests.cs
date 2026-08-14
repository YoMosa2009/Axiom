using Xunit;

namespace Malx_AI.Tests;

public sealed class UpdateReleaseParserTests
{
    [Fact]
    public void Parse_SelectsAxiomWindowsZipAndReadsDigest()
    {
        const string json = """
        {
          "tag_name": "V1.7.1",
          "html_url": "https://github.com/YoMosa2009/Axiom/releases/tag/V1.7.1",
          "body": "Small fix",
          "draft": false,
          "prerelease": false,
          "assets": [
            { "name": "Source.zip", "browser_download_url": "https://example/source.zip", "size": 5 },
            { "name": "Axiom-v1.7.1-win-x64-clean.zip", "browser_download_url": "https://example/axiom.zip", "size": 123, "digest": "sha256:ABC123" }
          ]
        }
        """;

        UpdateCheckResult? result = UpdateReleaseParser.Parse(json, new Version(1, 7, 0));

        Assert.NotNull(result);
        Assert.True(result.IsNewerVersionAvailable);
        Assert.Equal(UpdatePackageKind.Zip, result.PackageKind);
        Assert.Equal("Axiom-v1.7.1-win-x64-clean.zip", result.PackageFileName);
        Assert.Equal("ABC123", result.PackageSha256);
        Assert.Equal(123, result.PackageSizeBytes);
    }

    [Theory]
    [InlineData("v1.7.0", false)]
    [InlineData("V1.7", false)]
    [InlineData("release-1.7.1", true)]
    [InlineData("v2.0.0", true)]
    public void Parse_UsesNormalizedSemanticVersion(string tag, bool expectedNewer)
    {
        string json = $$"""{ "tag_name": "{{tag}}", "draft": false, "prerelease": false, "assets": [] }""";
        UpdateCheckResult? result = UpdateReleaseParser.Parse(json, new Version(1, 7, 0, 0));

        Assert.NotNull(result);
        Assert.Equal(expectedNewer, result.IsNewerVersionAvailable);
    }

    [Fact]
    public void Parse_IgnoresDraftAndPrerelease()
    {
        const string draft = """{ "tag_name": "v9.0.0", "draft": true, "prerelease": false }""";
        const string prerelease = """{ "tag_name": "v9.0.0-beta", "draft": false, "prerelease": true }""";

        Assert.Null(UpdateReleaseParser.Parse(draft, new Version(1, 7, 0)));
        Assert.Null(UpdateReleaseParser.Parse(prerelease, new Version(1, 7, 0)));
    }
}
