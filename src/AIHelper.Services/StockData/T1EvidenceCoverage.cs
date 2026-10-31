using System.Text.Json;

namespace AIHelper.Services.StockData;

public static partial class T1EvidenceReadiness
{
    /// <summary>
    /// Explicit inputs only, no directory discovery. Pair totals are per distinct supplied source/package pair;
    /// date/RunId totals remain distinct. Fixture counts are separate; authenticated production count is zero
    /// until an independently reviewed authentication adapter exists. No rates without observations.
    /// </summary>
    public static T1EvidenceCoverageReport EvaluateCoverage(IEnumerable<T1EvidenceRequest> requests)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var inputs = requests.Take(1001).ToArray();
        if (inputs.Length > 1000 || inputs.Any(i => i is null)) throw new ArgumentException("Evidence batch exceeds bound or contains null", nameof(requests));
        string PathIdentity(string? path) => Missing(path) ? "" : Path.GetFullPath(path!);
        if (inputs.GroupBy(i => (PathIdentity(i.SnapshotPath), PathIdentity(i.PackageDirectory))).Any(g => g.Count() > 1))
            throw new ArgumentException("Duplicate source/package pair", nameof(requests));
        var samples = inputs.Select(Evaluate).OrderBy(s => s.SnapshotFingerprint, StringComparer.Ordinal)
            .ThenBy(s => s.PackageFingerprint, StringComparer.Ordinal).ThenBy(s => s.Origin)
            .ThenBy(s => JsonSerializer.Serialize(s, Json), StringComparer.Ordinal).ToArray();
        if (samples.Where(s => s.SnapshotFingerprint.Length > 0 && s.PackageFingerprint.Length > 0)
            .GroupBy(s => (s.SnapshotFingerprint, s.PackageFingerprint)).Any(g => g.Count() > 1))
            throw new ArgumentException("Duplicate content-addressed pair", nameof(requests));
        var files = inputs.Where(i => !Missing(i.SnapshotPath) && File.Exists(i.SnapshotPath)).Select(i => PathIdentity(i.SnapshotPath)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var valid = samples.Where(s => s.SnapshotStructure == T1ReadinessStatus.PASS).ToArray();
        var linked = samples.Where(s => s.Binding == T1ReadinessStatus.PASS).ToArray();
        var outcomes = samples.SelectMany(s => s.Outcomes).ToArray();
        static IReadOnlyDictionary<string, int> Counts(IEnumerable<string> values) => new SortedDictionary<string, int>(
            values.GroupBy(v => v, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal), StringComparer.Ordinal);
        bool noSample = files == 0 && samples.All(s => s.RankingIntegrity != T1ReadinessStatus.PASS);
        return new(PolicyVersion, noSample ? "NO_SAMPLE" : "OBSERVED_UNAUTHENTICATED_EVIDENCE",
            samples.Any(s => s.Overall == T1ReadinessStatus.FAIL) ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED,
            files, valid.Select(s => s.SnapshotFingerprint).Distinct(StringComparer.Ordinal).Count(),
            samples.Where(s => s.RankingIntegrity == T1ReadinessStatus.PASS).Select(s => s.PackageFingerprint).Distinct(StringComparer.Ordinal).Count(),
            linked.Length, samples.Count(s => s.Binding == T1ReadinessStatus.FAIL),
            valid.Select(s => s.DecisionDate).Distinct().Count(), valid.Select(s => s.RunId).Distinct().Count(),
            linked.Sum(s => s.Coverage!.CapturedCandidates), linked.Sum(s => s.Coverage!.EligibleCandidates),
            linked.Sum(s => s.Coverage!.ExcludedCandidates), linked.Sum(s => s.Coverage!.ActualShortlistSize),
            linked.Sum(s => s.Coverage!.EligibleCandidates - s.Coverage.ActualShortlistSize),
            samples.Count(s => s.Calendar.Status == T1ReadinessStatus.PASS), samples.Count(s => s.Calendar.Status != T1ReadinessStatus.PASS),
            outcomes.Count(o => o.SourceAvailability == T1ReadinessStatus.PASS && o.FieldValidity == T1ReadinessStatus.PASS),
            outcomes.Count(o => o.Reasons.Contains("PRICE_BASIS_MISMATCH")), samples.Count(s => s.ExecutionEvidence != T1ReadinessStatus.PASS),
            samples.Count(s => s.Origin == T1EvidenceOrigin.TEST_FIXTURE), 0, "NOT_APPLICABLE",
            Counts(valid.Select(s => s.Strategy)), Counts(samples.Select(s => s.EvidenceClass.ToString())),
            Counts(outcomes.SelectMany(o => o.Reasons)), samples);
    }

    /// <summary>Canonical deterministic machine content; no current timestamps, filesystem paths or performance statistics.</summary>
    public static string CanonicalReport(T1EvidenceCoverageReport report) => JsonSerializer.Serialize(report, Json);

    /// <summary>Explanatory presentation stays outside canonical machine data. This API never persists reports.</summary>
    public static string Explain(T1EvidenceCoverageReport report) =>
        $"{report.SampleState}: {report.Overall}; linked packages {report.LinkedPairs}; "
        + $"authenticated production samples {report.AuthenticatedProductionSamples}. "
        + "Integrity does not establish historical availability, market authenticity or executable fills. No returns calculated.";
}
