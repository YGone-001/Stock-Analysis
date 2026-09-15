using System.Security.Cryptography;
using System.IO;

namespace AIHelper.Helpers;

/// <summary>Versioned, integrity-bearing update metadata. Unknown JSON fields are ignored deliberately.</summary>
public sealed record class UpdateManifest
{
    public int SchemaVersion { get; set; }
    public string? Version { get; set; }
    public string? DownloadUrl { get; set; }
    public string? Sha256 { get; set; }
    public string? Description { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? ReleaseNotesUrl { get; set; }
}

public enum UpdateOperationStatus
{
    NoUpdate,
    UpdateReady,
    InvalidManifest,
    UnsafeUrl,
    UnsupportedArtifact,
    DownloadFailed,
    HashMismatch,
    UnexpectedError
}

public sealed record UpdateOperationResult(UpdateOperationStatus Status, string Message, UpdateManifest? Manifest = null, string? VerifiedArtifactPath = null)
{
    public bool IsReady => Status == UpdateOperationStatus.UpdateReady && !string.IsNullOrWhiteSpace(VerifiedArtifactPath);
}

/// <summary>Centralized policy for all update URLs. URL text is never trusted through prefix matching.</summary>
public static class UpdateSecurityPolicy
{
    public const int SupportedSchemaVersion = 1;
    private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase) { "ooppp.com", "www.ooppp.com" };
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".exe", ".zip" };

    public static bool IsAllowedManifestUri(Uri uri) => IsAllowedHttpsUri(uri);
    public static bool IsAllowedDownloadUri(Uri uri) => IsAllowedHttpsUri(uri) && IsAllowedArtifactExtension(uri);
    public static bool IsAllowedReleaseNotesUri(Uri uri) => IsAllowedHttpsUri(uri);
    public static bool IsAllowedArtifactExtension(Uri uri) => AllowedExtensions.Contains(Path.GetExtension(uri.AbsolutePath));

    private static bool IsAllowedHttpsUri(Uri uri) => uri.IsAbsoluteUri
        && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
        && AllowedHosts.Contains(uri.Host);
}

public static class ApplicationVersionProvider
{
    public static string Current => GetInformationalVersion(System.Reflection.Assembly.GetEntryAssembly() ?? typeof(ApplicationVersionProvider).Assembly) ?? "0.0.0";

    public static string? GetInformationalVersion(System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string? raw = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .SingleOrDefault()?.InformationalVersion;
        return TryNormalizeStable(raw, out string? normalized) ? normalized : null;
    }

    public static bool TryNormalizeStable(string? value, out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string candidate = value.Trim();
        if (candidate.StartsWith("v", StringComparison.OrdinalIgnoreCase)) candidate = candidate[1..];
        int metadata = candidate.IndexOf('+');
        if (metadata >= 0) candidate = candidate[..metadata];
        if (candidate.Contains('-', StringComparison.Ordinal)) return false;
        string[] parts = candidate.Split('.');
        if (parts.Length != 3 || parts.Any(part => !int.TryParse(part, out int n) || n < 0)) return false;
        normalized = string.Join('.', parts.Select(part => int.Parse(part).ToString()));
        return true;
    }

    public static bool IsRemoteNewer(string remoteVersion, string currentVersion)
    {
        if (!TryNormalizeStable(remoteVersion, out string? remote) || !TryNormalizeStable(currentVersion, out string? current)) return false;
        return Version.Parse(remote!) > Version.Parse(current!);
    }
}

public static class UpdateManifestValidator
{
    public static UpdateOperationResult Validate(UpdateManifest? manifest)
    {
        if (manifest is null) return new(UpdateOperationStatus.InvalidManifest, "Update manifest is missing.");
        if (manifest.SchemaVersion != UpdateSecurityPolicy.SupportedSchemaVersion)
            return new(UpdateOperationStatus.InvalidManifest, "Unsupported update manifest schema version.");
        if (!ApplicationVersionProvider.TryNormalizeStable(manifest.Version, out _))
            return new(UpdateOperationStatus.InvalidManifest, "Update manifest has an invalid stable version.");
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out Uri? download))
            return new(UpdateOperationStatus.InvalidManifest, "Update manifest download URL is missing or invalid.");
        if (!UpdateSecurityPolicy.IsAllowedDownloadUri(download))
            return new(Path.GetExtension(download.AbsolutePath).Length == 0 || !UpdateSecurityPolicy.IsAllowedArtifactExtension(download)
                ? UpdateOperationStatus.UnsupportedArtifact : UpdateOperationStatus.UnsafeUrl,
                "Update manifest download URL is not an approved HTTPS artifact URL.");
        if (string.IsNullOrWhiteSpace(manifest.Sha256) || manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit))
            return new(UpdateOperationStatus.InvalidManifest, "Update manifest requires a 64-character SHA-256 value.");
        if (!string.IsNullOrWhiteSpace(manifest.ReleaseNotesUrl)
            && (!Uri.TryCreate(manifest.ReleaseNotesUrl, UriKind.Absolute, out Uri? notes) || !UpdateSecurityPolicy.IsAllowedReleaseNotesUri(notes)))
            return new(UpdateOperationStatus.UnsafeUrl, "Update manifest release notes URL is not approved HTTPS.");
        return new(UpdateOperationStatus.UpdateReady, "Update manifest is valid.", manifest);
    }

    public static bool HashMatches(string expectedHex, ReadOnlySpan<byte> actualHash)
    {
        if (expectedHex.Length != 64 || !expectedHex.All(Uri.IsHexDigit)) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expectedHex), actualHash);
    }
}
