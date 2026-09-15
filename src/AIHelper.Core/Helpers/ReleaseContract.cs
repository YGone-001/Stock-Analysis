using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIHelper.Helpers;

/// <summary>Pure release identity rules shared by release-facing regression tests.</summary>
public static partial class ReleaseContract
{
    public const string DefaultRid = "win-x64";

    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersionPattern();

    [GeneratedRegex("^[0-9a-f]{64}  [^\\r\\n]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256SumsPattern();

    public static bool IsStableVersion(string? version) => !string.IsNullOrWhiteSpace(version) && StableVersionPattern().IsMatch(version);

    public static string CreateArtifactName(string version, string rid = DefaultRid)
    {
        if (!IsStableVersion(version)) throw new ArgumentException("Release version must use X.Y.Z.", nameof(version));
        if (string.IsNullOrWhiteSpace(rid)) throw new ArgumentException("Release RID is required.", nameof(rid));
        return $"AIHelper-{version}-{rid}.zip";
    }

    public static string CreateSha256SumsLine(string artifactName, ReadOnlySpan<byte> artifactBytes) =>
        $"{Convert.ToHexString(SHA256.HashData(artifactBytes)).ToLowerInvariant()}  {artifactName}";

    public static bool IsValidSha256SumsLine(string? value, string artifactName, ReadOnlySpan<byte> artifactBytes)
    {
        if (string.IsNullOrWhiteSpace(value) || !Sha256SumsPattern().IsMatch(value)) return false;
        return string.Equals(value, CreateSha256SumsLine(artifactName, artifactBytes), StringComparison.Ordinal);
    }

    public static string SerializeProvenance(ReleaseProvenance provenance)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        if (!IsStableVersion(provenance.Version)) throw new ArgumentException("Provenance version must use X.Y.Z.", nameof(provenance));
        if (!string.Equals(provenance.Artifact, CreateArtifactName(provenance.Version, provenance.Rid), StringComparison.Ordinal))
            throw new ArgumentException("Provenance artifact does not match its version and RID.", nameof(provenance));
        if (string.IsNullOrWhiteSpace(provenance.GitCommit) || provenance.BuildDateUtc.Offset != TimeSpan.Zero || string.IsNullOrWhiteSpace(provenance.DotnetSdk))
            throw new ArgumentException("Provenance must include actual commit, UTC build date, and SDK.", nameof(provenance));
        return JsonSerializer.Serialize(provenance, new JsonSerializerOptions { WriteIndented = true });
    }
}

public sealed record ReleaseProvenance(
    string Version,
    string GitCommit,
    string GitTag,
    DateTimeOffset BuildDateUtc,
    string Rid,
    string TargetFramework,
    string DotnetSdk,
    string Artifact,
    string Sha256);
