using System.Globalization;
using System.Text.Json;

namespace AIHelper.Services.StockData;

public static partial class T1EvidenceReadiness
{
    /// <summary>Offline association only. Outcomes are validated after source analysis and never supplied to Analyze/Rank.</summary>
    public static T1AssociationArtifact Associate(IEnumerable<T1AssociationInput> inputs)
    {
        var items = inputs.Take(101).ToArray();
        if (items.Length > 100 || items.Any(i => i?.Evidence is null)) throw new ArgumentException("ASSOCIATION_INPUT_BOUND");
        // Reuse B's complete duplicate identity and source coverage rules, without outcome dependencies.
        var sourceRequests = items.Select(i => i.Evidence with { Outcomes = null, Calendar = null }).ToArray();
        var sourceCoverage = EvaluateCoverage(sourceRequests);
        var records = items.Select(BuildAssociation).OrderBy(r => r.Evidence.SnapshotFingerprint, StringComparer.Ordinal)
            .ThenBy(r => r.Evidence.PackageFingerprint, StringComparer.Ordinal).ThenBy(r => DeclarationFingerprint(r), StringComparer.Ordinal).ToArray();
        foreach (var record in records)
        {
            if (!sourceCoverage.Samples.Any(s => s.SnapshotFingerprint == record.Evidence.SnapshotFingerprint
                && s.PackageFingerprint == record.Evidence.PackageFingerprint && s.Binding == record.Evidence.Binding))
                throw new InvalidDataException("SOURCE_CHANGED_DURING_ASSOCIATION");
        }
        var content = new T1AssociationContent(2, PolicyVersion,
            "Declared association only; not authenticated PIT, executable fills or performance evaluation.", records, RecomputeAssociationCoverage(records));
        return new(2, DeclarationFingerprint(content), content);
    }

    private static bool Aligned(T1PriceBasisState state) => state is T1PriceBasisState.ALIGNED_RAW or T1PriceBasisState.ALIGNED_ADJUSTED_WITH_PROVENANCE;

    private static T1AssociationRecord BuildAssociation(T1AssociationInput input)
    {
        var request = input.Evidence;
        var sample = Evaluate(request with { Outcomes = null, Calendar = null });
        var source = VerifySnapshot(request.SnapshotPath);
        var errors = new List<string>();
        var package = ReadPackage(request.PackageDirectory, errors); // existing Verify() is invoked inside this accepted reader
        if (source.Fingerprint != sample.SnapshotFingerprint || (package?.Fingerprint ?? "") != sample.PackageFingerprint)
            throw new InvalidDataException("SOURCE_CHANGED_DURING_ASSOCIATION");
        var envelopes = request.Outcomes ?? Array.Empty<T1OutcomeEnvelope>();
        if (envelopes.Count > 100 || envelopes.Any(e => e is null)) throw new ArgumentException("OUTCOME_INPUT_BOUND");
        if (envelopes.Select(e => e.Symbol).Distinct(StringComparer.Ordinal).Count() != envelopes.Count)
            throw new ArgumentException("DUPLICATE_CANDIDATE_ASSOCIATION");
        if (sample.SnapshotStructure == T1ReadinessStatus.PASS && envelopes.Any(e => !sample.Captured.Any(c => c.Symbol == e.Symbol)))
            throw new ArgumentException("OUTCOME_OUTSIDE_CAPTURED_POOL");
        if (sample.SnapshotStructure == T1ReadinessStatus.PASS && input.DecisionBases?.Any(p => !sample.Captured.Any(c => c.Symbol == p.Key)) == true)
            throw new ArgumentException("BASIS_OUTSIDE_CAPTURED_POOL");
        var calendars = new SortedDictionary<string, T1CalendarEvidence>(StringComparer.Ordinal);
        if (input.Calendars is not null && request.Calendar is not null) throw new ArgumentException("AMBIGUOUS_CALENDARS");
        if (input.Calendars is not null)
            foreach (var pair in input.Calendars) calendars.Add(pair.Key, pair.Value);
        else if (request.Calendar is not null) calendars.Add(request.Calendar.Exchange, request.Calendar);
        ValidateCalendarMap(calendars);
        var bases = new SortedDictionary<string, T1PriceBasis>(StringComparer.Ordinal);
        foreach (var pair in input.DecisionBases ?? new Dictionary<string, T1PriceBasis>()) bases.Add(pair.Key, pair.Value);
        var proof = new T1AssociationDerivation(sample with { Outcomes = Array.Empty<T1OutcomeCheck>() },
            source.Structure == T1ReadinessStatus.PASS ? source.Snapshot!.Scan.StrategyVersion : "",
            source.Structure == T1ReadinessStatus.PASS ? source.Snapshot!.Scan.ParameterFingerprint : "",
            source.Structure == T1ReadinessStatus.PASS ? source.Snapshot!.RequestedAsOf : null,
            !Missing(request.SnapshotPath) && File.Exists(request.SnapshotPath),
            Missing(request.SnapshotPath) ? "" : DeclarationFingerprint(Path.GetFullPath(request.SnapshotPath!)),
            calendars, envelopes.OrderBy(e => e.Symbol, StringComparer.Ordinal).ToArray(), bases, request.DecisionBasis, package?.Request,
            package?.Ranked ?? Array.Empty<T1RankingCandidate>(), package?.Excluded ?? Array.Empty<T1RankingExclusion>());
        return DeriveAssociation(proof);
    }

    public static string CanonicalAssociation(T1AssociationArtifact artifact) => JsonSerializer.Serialize(artifact, Json);

    private static bool VerifyAssociationV1Structure(T1AssociationArtifact artifact)
    {
        try
        {
            if (artifact.SchemaVersion != 1 || artifact.Content.SchemaVersion != 1 || artifact.Content.PolicyVersion != PolicyVersion
                || artifact.Fingerprint != DeclarationFingerprint(artifact.Content)) return false;
            var records = artifact.Content.Records;
            if (records.Count > 100 || records.Any(r => r.Derivation is not null || r.Candidates.Any(c => c.Exchange is not null || c.Calendar is not null)
                || r.Candidates.Count > 20
                || r.Candidates.Select(c => c.Original.Symbol).Distinct(StringComparer.Ordinal).Count() != r.Candidates.Count
                || !r.Candidates.Select(c => c.Original).SequenceEqual(r.Evidence.Captured)
                || r.Evidence.Overall == T1ReadinessStatus.PASS || r.Evidence.EvidenceClass is T1EvidenceClass.LIVE_RECORDED_PIT or T1EvidenceClass.ARCHIVED_PIT_REPLAY)) return false;
            foreach (var r in records)
            {
                if (r.Evidence.Outcomes.Count != r.Candidates.Count || r.Candidates.Any(c => c.Outcome.Symbol != c.Original.Symbol
                    || c.IndependentEligibility != T1ReadinessStatus.NOT_VERIFIED || !Enum.IsDefined(c.Category)
                    || c.Category == T1CandidateAssociationCategory.SHORTLISTED && (c.T1Rank is null or < 1 || c.ExclusionCode is not null)
                    || c.Category == T1CandidateAssociationCategory.EXCLUDED && (c.ExclusionCode is null or T1RankingExclusionCode.None || Missing(c.ExclusionReason) || c.T1Rank is not null)
                    || c.Category is not (T1CandidateAssociationCategory.SHORTLISTED or T1CandidateAssociationCategory.EXCLUDED) && (c.T1Rank is not null || c.ExclusionCode is not null))) return false;
                if (!r.Evidence.Outcomes.Select(DeclarationFingerprint).SequenceEqual(r.Candidates.Select(c => DeclarationFingerprint(c.Outcome)))) return false;
                if (r.Evidence.Binding == T1ReadinessStatus.PASS)
                {
                    var cov = r.Evidence.Coverage;
                    if (cov is null || cov.CapturedCandidates != r.Candidates.Count
                        || cov.ActualShortlistSize != r.Candidates.Count(c => c.Category == T1CandidateAssociationCategory.SHORTLISTED)
                        || cov.ExcludedCandidates != r.Candidates.Count(c => c.Category == T1CandidateAssociationCategory.EXCLUDED)
                        || cov.EligibleCandidates - cov.ActualShortlistSize != r.Candidates.Count(c => c.Category == T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED)) return false;
                    if (!r.Candidates.Where(c => c.T1Rank is not null).Select(c => c.T1Rank!.Value).Order().SequenceEqual(Enumerable.Range(1, cov.ActualShortlistSize))) return false;
                }
            }
            if (records.Where(r => r.Evidence.SnapshotFingerprint.Length > 0 && r.Evidence.PackageFingerprint.Length > 0)
                .GroupBy(r => (r.Evidence.SnapshotFingerprint, r.Evidence.PackageFingerprint)).Any(g => g.Count() > 1)) return false;
            var all = records.SelectMany(r => r.Candidates).ToArray(); var coverage = artifact.Content.Coverage;
            int usable = all.Count(c => c.AssociationUsableDeclared);
            return coverage.TotalCapturedCandidates == all.Length && coverage.CandidateAssociations == all.Length && coverage.UsableDeclaredAssociations == usable
                && coverage.AssociationCoverage == (all.Length == 0 ? "NOT_APPLICABLE" : $"{usable}/{all.Length}")
                && coverage.NoExecutionEvidence == all.Count(c => c.Outcome.Reasons.Contains("NO_EXECUTION_EVIDENCE"))
                && coverage.DeclaredAvailableBars == all.Count(c => c.OutcomeAvailableDeclared)
                && coverage.ValidatedFields == all.Count(c => c.Outcome.FieldValidity == T1ReadinessStatus.PASS)
                && coverage.SourceCoverage.AuthenticatedProductionSamples == 0 && coverage.SourceCoverage.Overall != T1ReadinessStatus.PASS;
        }
        catch (Exception ex) when (ex is ArgumentException or NullReferenceException or InvalidOperationException) { return false; }
    }
}
