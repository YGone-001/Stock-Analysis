using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using static AIHelper.Core.Sparrow.ResearchEvidenceBatchAuditReasonCodes;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>
/// Renders and publishes deterministic batch audit reports (JSON or Markdown) using create-new atomic
/// promotion: a unique temporary file is flushed to disk and then moved into place without overwriting.
/// </summary>
public sealed class ResearchEvidenceBatchReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly Func<string, FileStream> _createTemporaryFile;

    public ResearchEvidenceBatchReportExporter(Func<string, FileStream>? createTemporaryFile = null)
        => _createTemporaryFile = createTemporaryFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough));

    public string SerializeJson(ResearchEvidenceBatchAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return JsonSerializer.Serialize(report, JsonOptions);
    }

    public string Render(ResearchEvidenceBatchAuditReport report, ResearchEvidenceBatchAuditReportFormat format)
    {
        ArgumentNullException.ThrowIfNull(report);
        return format == ResearchEvidenceBatchAuditReportFormat.Json ? SerializeJson(report) : RenderMarkdown(report);
    }

    public string RenderMarkdown(ResearchEvidenceBatchAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        ResearchEvidenceBatchAuditSummary summary = report.Summary;
        StringBuilder text = new();
        text.AppendLine("# Research Evidence Batch Audit");
        text.AppendLine();
        text.AppendLine("| Field | Value |");
        text.AppendLine("| --- | --- |");
        text.AppendLine($"| Report version | `{report.ReportVersion}` |");
        text.AppendLine($"| Audit mode | `{report.AuditMode}` |");
        text.AppendLine($"| Batch status | `{report.BatchStatus}` |");
        text.AppendLine();

        text.AppendLine("## Batch summary");
        text.AppendLine();
        text.AppendLine("| Metric | Count |");
        text.AppendLine("| --- | --- |");
        AppendSummaryRow(text, "Total bundles", summary.TotalBundles);
        AppendSummaryRow(text, "Structurally verified", summary.StructurallyVerified);
        AppendSummaryRow(text, "Structurally failed", summary.StructurallyFailed);
        AppendSummaryRow(text, "Structurally unsupported", summary.StructurallyUnsupported);
        AppendSummaryRow(text, "Re-execution not requested", summary.ReexecutionNotRequested);
        AppendSummaryRow(text, "Re-execution equivalent", summary.ReexecutionEquivalent);
        AppendSummaryRow(text, "Re-execution diverged", summary.ReexecutionDiverged);
        AppendSummaryRow(text, "Re-execution unsupported", summary.ReexecutionUnsupported);
        AppendSummaryRow(text, "Re-execution failed", summary.ReexecutionFailed);
        AppendSummaryRow(text, "Fully successful", summary.FullySuccessful);
        AppendSummaryRow(text, "Not fully successful", summary.NotFullySuccessful);
        text.AppendLine();

        text.AppendLine("## Bundle results");
        text.AppendLine();
        if (summary.TotalBundles == 0)
        {
            text.AppendLine("_No evidence bundles were discovered in the audited directory._");
            text.AppendLine();
            return text.ToString();
        }

        text.AppendLine("| Bundle | Experiment | Bundle version | Manifest fingerprint | Structural | Re-execution | Fully successful | Failed checks |");
        text.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (ResearchEvidenceBatchAuditBundleResult bundle in report.Bundles)
        {
            string[] cells =
            {
                Cell(bundle.BundleFileName),
                Cell(bundle.ExperimentId),
                Cell(bundle.BundleVersion),
                Cell(bundle.BundleManifestFingerprint),
                Cell(bundle.StructuralStatus.ToString()),
                Cell(bundle.ReexecutionStatus.ToString()),
                bundle.FullVerificationSucceeded ? "yes" : "no",
                bundle.FailedCheckCount.ToString(CultureInfo.InvariantCulture)
            };
            text.AppendLine($"| {string.Join(" | ", cells)} |");
        }
        text.AppendLine();

        text.AppendLine("## Failure details");
        text.AppendLine();
        ResearchEvidenceBatchAuditBundleResult[] failing = report.Bundles
            .Where(bundle => !bundle.FullVerificationSucceeded)
            .ToArray();

        if (failing.Length == 0)
        {
            text.AppendLine("_No failed or unsupported checks were recorded._");
            text.AppendLine();
            return text.ToString();
        }

        foreach (ResearchEvidenceBatchAuditBundleResult bundle in failing)
        {
            text.AppendLine($"### `{bundle.BundleFileName}`");
            text.AppendLine();
            text.AppendLine($"- Experiment: `{bundle.ExperimentId ?? "<unknown>"}`");
            text.AppendLine($"- Structural status: `{bundle.StructuralStatus}`");
            text.AppendLine($"- Re-execution status: `{bundle.ReexecutionStatus}`");
            text.AppendLine($"- Manifest fingerprint: `{bundle.BundleManifestFingerprint ?? "<unavailable>"}`");
            text.AppendLine($"- Stage statuses: entry integrity `{bundle.EntryIntegrityStatus}`, experiment record `{bundle.ExperimentRecordStatus}`, artifact lineage `{bundle.ArtifactLineageStatus}`, dataset `{bundle.DatasetStatus}`, parameters `{bundle.ParameterStatus}`");
            text.AppendLine($"- Reasons: {(bundle.ReasonCodes.Count == 0 ? "<none>" : string.Join(", ", bundle.ReasonCodes.Select(code => $"`{code}`")))}");
            if (bundle.Checks.Count > 0)
            {
                text.AppendLine("- Checks:");
                foreach (ResearchEvidenceBatchAuditCheck check in bundle.Checks)
                    text.AppendLine($"  - `{check.Code}` ({check.Status}): {Line(check.Message)}");
            }
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Writes the report atomically and returns the published full path.</summary>
    public async Task<string> WriteAsync(
        ResearchEvidenceBatchAuditReport report,
        ResearchEvidenceBatchAuditReportFormat format,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        string path = Path.GetFullPath(outputPath);
        if (File.Exists(path))
            throw new ResearchEvidenceBatchAuditException(OutputExists, "The report output already exists and will not be overwritten.");

        string? directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("The report output directory is invalid.", nameof(outputPath));

        string content = Render(report, format);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            Directory.CreateDirectory(directory);
            await using (FileStream stream = _createTemporaryFile(temporaryPath))
            await using (StreamWriter writer = new(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 8192, leaveOpen: true))
            {
                await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); }
                catch { /* Best-effort cleanup of this attempt's temporary file only. */ }
            }
        }

        return path;
    }

    private static void AppendSummaryRow(StringBuilder text, string metric, int count) =>
        text.AppendLine($"| {metric} | {count.ToString(CultureInfo.InvariantCulture)} |");

    private static string Cell(string? value) => Line(value ?? "<none>").Replace("|", "\\|", StringComparison.Ordinal);

    private static string Line(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
