using AIHelper.Core.Sparrow;
using static AIHelper.Core.Sparrow.ResearchEvidenceBatchAuditConstants;
using static AIHelper.Core.Sparrow.ResearchEvidenceBatchAuditReasonCodes;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleCheckStatus;
using static AIHelper.Core.Sparrow.ResearchEvidenceBundleStageStatus;

namespace AIHelper.Services.StockData.Sparrow;

public interface IResearchEvidenceBatchAuditor
{
    Task<ResearchEvidenceBatchAuditReport> AuditAsync(string directoryPath, bool reexecute = false, CancellationToken cancellationToken = default);
}

/// <summary>
/// Audits every evidence bundle in one directory using the authoritative Phase 3.15 single-bundle verifier.
/// Discovery is deterministic (ordinal file-name order, non-recursive), verification is sequential and every
/// bundle is isolated so one failure cannot hide the outcome of the remaining bundles.
/// </summary>
public sealed class ResearchEvidenceBatchAuditor : IResearchEvidenceBatchAuditor
{
    private readonly IResearchEvidenceBundleVerifier _verifier;

    public ResearchEvidenceBatchAuditor(IResearchEvidenceBundleVerifier? verifier = null)
        => _verifier = verifier ?? new ResearchEvidenceBundleVerifier();

    /// <summary>
    /// Selects accepted bundle file names, rejects ambiguous case-colliding names and returns them in ordinal
    /// order. Pure and deterministic: the result never depends on enumeration order.
    /// </summary>
    public static IReadOnlyList<string> DiscoverBundleFileNames(IEnumerable<string> fileNames)
    {
        ArgumentNullException.ThrowIfNull(fileNames);

        string[] accepted = fileNames
            .Where(name => string.Equals(Path.GetExtension(name), BundleFileExtension, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        List<string> collisions = accepted
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .SelectMany(group => group.OrderBy(name => name, StringComparer.Ordinal))
            .ToList();

        if (collisions.Count > 0)
        {
            throw new ResearchEvidenceBatchAuditException(DirectoryInvalid,
                $"Ambiguous case-colliding bundle file names are rejected: {string.Join(", ", collisions)}.");
        }

        return Array.AsReadOnly(accepted.OrderBy(name => name, StringComparer.Ordinal).ToArray());
    }

    public async Task<ResearchEvidenceBatchAuditReport> AuditAsync(string directoryPath, bool reexecute = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        cancellationToken.ThrowIfCancellationRequested();

        string directory = Path.GetFullPath(directoryPath);
        if (!Directory.Exists(directory))
            throw new ResearchEvidenceBatchAuditException(DirectoryInvalid, "The batch audit directory does not exist or is not a directory.");

        ResearchEvidenceBatchAuditMode mode = reexecute
            ? ResearchEvidenceBatchAuditMode.StructuralAndReexecution
            : ResearchEvidenceBatchAuditMode.StructuralOnly;

        IReadOnlyList<DiscoveredBundle> discovered;
        try
        {
            discovered = Discover(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ResearchEvidenceBatchAuditException(DirectoryInvalid, "The batch audit directory could not be enumerated.", ex);
        }

        if (discovered.Count > MaxBundlesPerInvocation)
        {
            throw new ResearchEvidenceBatchAuditException(BundleLimitExceeded,
                $"The directory contains {discovered.Count} bundles, which exceeds the maximum of {MaxBundlesPerInvocation} bundles per invocation.");
        }

        List<ResearchEvidenceBatchAuditBundleResult> results = new(discovered.Count);

        // Sequential by design: no concurrent decompression or concurrent re-execution.
        foreach (DiscoveredBundle bundle in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await AuditBundleAsync(bundle, reexecute, cancellationToken).ConfigureAwait(false));
        }

        return new ResearchEvidenceBatchAuditReport(mode, results);
    }

    private async Task<ResearchEvidenceBatchAuditBundleResult> AuditBundleAsync(DiscoveredBundle bundle, bool reexecute, CancellationToken cancellationToken)
    {
        if (bundle.IsReparsePoint)
        {
            return FailedBundle(bundle.FileName, BundleIoFailed,
                "The entry is a symbolic link or reparse point and is not followed by the batch audit.");
        }

        ResearchEvidenceBundleVerificationResult verification;
        try
        {
            verification = await _verifier.VerifyAsync(bundle.FullPath, reexecute, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is never downgraded to a per-bundle outcome.
            throw;
        }
        catch (Exception ex)
        {
            // No structured result exists; never fabricate structural success.
            string reasonCode = IsIoFailure(ex) ? BundleIoFailed : BundleVerificationException;
            return FailedBundle(bundle.FileName, reasonCode, $"Verification did not produce a structured result ({ex.GetType().Name}).");
        }

        ResearchEvidenceBatchAuditCheck[] failedChecks = verification.Checks
            .Where(check => !string.Equals(check.Status, Pass, StringComparison.Ordinal))
            .Select(check => new ResearchEvidenceBatchAuditCheck(check.CheckCode, check.Status, Sanitize(check.Message, bundle)))
            .ToArray();

        return new ResearchEvidenceBatchAuditBundleResult(
            bundle.FileName,
            verification.ExperimentId,
            verification.BundleVersion,
            verification.BundleManifestFingerprint,
            verification.Status,
            verification.ReexecutionStatus,
            verification.FullVerificationSucceeded,
            verification.EntryIntegrityStatus,
            verification.ExperimentRecordStatus,
            verification.ArtifactLineageStatus,
            verification.DatasetStatus,
            verification.ParameterStatus,
            verification.FailedCheckCount,
            verification.ReasonCodes,
            failedChecks);
    }

    private static ResearchEvidenceBatchAuditBundleResult FailedBundle(string fileName, string reasonCode, string message) =>
        new(fileName, null, null, null,
            ResearchEvidenceBundleVerificationStatus.Failed,
            ResearchEvidenceBundleReexecutionStatus.NotRequested,
            fullVerificationSucceeded: false,
            NotChecked, NotChecked, NotChecked, NotChecked, NotChecked,
            failedCheckCount: 1,
            new[] { reasonCode },
            new[] { new ResearchEvidenceBatchAuditCheck("BATCH_BUNDLE", Fail, message) });

    private static bool IsIoFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or NotSupportedException;

    /// <summary>Keeps absolute machine paths out of the report's semantic content.</summary>
    private static string Sanitize(string message, DiscoveredBundle bundle) =>
        message.Replace(bundle.FullPath, bundle.FileName, StringComparison.OrdinalIgnoreCase)
               .Replace(bundle.FullPath.Replace('\\', '/'), bundle.FileName, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DiscoveredBundle> Discover(string directory)
    {
        Dictionary<string, string> fullPaths = new(StringComparer.Ordinal);

        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(Path.GetExtension(name), BundleFileExtension, StringComparison.OrdinalIgnoreCase))
                fullPaths[name] = file;
        }

        List<DiscoveredBundle> bundles = new(fullPaths.Count);
        foreach (string name in DiscoverBundleFileNames(fullPaths.Keys))
        {
            string fullPath = fullPaths[name];
            bool isReparsePoint;
            try
            {
                isReparsePoint = (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                isReparsePoint = false;
            }

            bundles.Add(new DiscoveredBundle(name, fullPath, isReparsePoint));
        }

        return bundles;
    }

    private sealed record DiscoveredBundle(string FileName, string FullPath, bool IsReparsePoint);
}
