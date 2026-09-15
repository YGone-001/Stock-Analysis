using System.Text;
using System.Text.Json;
using AIHelper;
using AIHelper.Helpers;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class ReleaseContractTests
{
    [Theory]
    [InlineData("2.0.0")]
    [InlineData("0.0.0")]
    [InlineData("12.34.56")]
    public void StableReleaseVersion_AcceptsOnlyThreeNumericParts(string version) => Assert.True(ReleaseContract.IsStableVersion(version));

    [Theory]
    [InlineData("")]
    [InlineData("2")]
    [InlineData("2.0")]
    [InlineData("v2.0.0")]
    [InlineData("2.0.0-beta")]
    [InlineData("release-2.0.0")]
    public void StableReleaseVersion_RejectsTagPrefixesAndPrereleases(string version) => Assert.False(ReleaseContract.IsStableVersion(version));

    [Fact]
    public void DefaultApplicationVersion_NormalizesDevelopmentMetadataToStableVersion() =>
        Assert.Equal("2.0.0", ApplicationVersionProvider.GetInformationalVersion(typeof(App).Assembly));

    [Fact]
    public void ArtifactName_IsDeterministicAndVersioned() =>
        Assert.Equal("AIHelper-2.0.0-win-x64.zip", ReleaseContract.CreateArtifactName("2.0.0"));

    [Fact]
    public void Sha256SumsLine_IsLowercaseAndVerifiable()
    {
        byte[] artifact = Encoding.UTF8.GetBytes("release artifact contents");
        string name = ReleaseContract.CreateArtifactName("2.0.0");
        string sums = ReleaseContract.CreateSha256SumsLine(name, artifact);

        Assert.Matches("^[0-9a-f]{64}  AIHelper-2\\.0\\.0-win-x64\\.zip$", sums);
        Assert.True(ReleaseContract.IsValidSha256SumsLine(sums, name, artifact));
        Assert.False(ReleaseContract.IsValidSha256SumsLine(sums, name, Encoding.UTF8.GetBytes("changed")));
    }

    [Fact]
    public void Provenance_SerializesOnlyConsistentReleaseIdentity()
    {
        ReleaseProvenance provenance = new(
            "2.0.0",
            "0123456789abcdef0123456789abcdef01234567",
            "v2.0.0",
            DateTimeOffset.UtcNow,
            "win-x64",
            "net10.0-windows",
            "10.0.401",
            "AIHelper-2.0.0-win-x64.zip",
            new string('a', 64));

        using JsonDocument document = JsonDocument.Parse(ReleaseContract.SerializeProvenance(provenance));
        JsonElement root = document.RootElement;
        Assert.Equal("2.0.0", root.GetProperty("Version").GetString());
        Assert.Equal("v2.0.0", root.GetProperty("GitTag").GetString());
        Assert.Equal("AIHelper-2.0.0-win-x64.zip", root.GetProperty("Artifact").GetString());

        ReleaseProvenance inconsistent = provenance with { Artifact = "AIHelper-2.0.1-win-x64.zip" };
        Assert.Throws<ArgumentException>(() => ReleaseContract.SerializeProvenance(inconsistent));
    }
}
