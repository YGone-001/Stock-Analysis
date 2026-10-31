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
        var sourceRequests = items.Select(i => i.Evidence with { Outcomes = null }).ToArray();
        var sourceCoverage = EvaluateCoverage(sourceRequests);
        var records = items.Select(BuildAssociation).OrderBy(r => r.Evidence.SnapshotFingerprint, StringComparer.Ordinal)
            .ThenBy(r => r.Evidence.PackageFingerprint, StringComparer.Ordinal).ThenBy(r => DeclarationFingerprint(r), StringComparer.Ordinal).ToArray();
        foreach (var record in records)
        {
            if (!sourceCoverage.Samples.Any(s => s.SnapshotFingerprint == record.Evidence.SnapshotFingerprint
                && s.PackageFingerprint == record.Evidence.PackageFingerprint && s.Binding == record.Evidence.Binding))
                throw new InvalidDataException("SOURCE_CHANGED_DURING_ASSOCIATION");
        }
        var candidates = records.SelectMany(r => r.Candidates).ToArray();
        sourceCoverage = sourceCoverage with
        {
            Samples = records.Select(r => r.Evidence).ToArray(),
            Overall = records.Any(r => r.Evidence.Overall == T1ReadinessStatus.FAIL) ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED,
            AvailableOutcomeEnvelopes = candidates.Count(c => c.Outcome.SourceAvailability == T1ReadinessStatus.PASS && c.Outcome.FieldValidity == T1ReadinessStatus.PASS),
            PriceBasisMismatches = candidates.Count(c => c.Outcome.Reasons.Contains("PRICE_BASIS_MISMATCH")),
            MissingOutcomeReasons = new SortedDictionary<string, int>(candidates.SelectMany(c => c.Outcome.Reasons)
                .GroupBy(r => r, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal)
        };
        int usable = candidates.Count(c => c.AssociationUsableDeclared);
        var breakdown = records.GroupBy(r => (Date: r.Evidence.DecisionDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "NOT_APPLICABLE", r.Evidence.Strategy))
            .OrderBy(g => g.Key.Date, StringComparer.Ordinal).ThenBy(g => g.Key.Strategy, StringComparer.Ordinal)
            .Select(g => new T1AssociationBreakdown(g.Key.Date, g.Key.Strategy, g.Sum(r => r.Candidates.Count),
                g.Sum(r => r.Candidates.Count(c => c.AssociationUsableDeclared)), g.Count(r => r.Evidence.Origin == T1EvidenceOrigin.TEST_FIXTURE))).ToArray();
        var coverage = new T1AssociationCoverage(sourceCoverage, candidates.Length, candidates.Length, candidates.Count(c => c.OutcomeAvailableDeclared),
            candidates.Count(c => c.Outcome.FieldValidity == T1ReadinessStatus.PASS), candidates.Count(c => Aligned(c.Outcome.PriceBasis)),
            candidates.Count(c => !Aligned(c.Outcome.PriceBasis)), candidates.Count(c => c.Outcome.Reasons.Contains("MISSING_TARGET_DAILY_BAR")),
            candidates.Count(c => c.Outcome.SourceState != T1OutcomeSourceState.AVAILABLE),
            candidates.Count(c => c.Outcome.Reasons.Contains("SUSPENDED")),
            candidates.Count(c => c.Outcome.Reasons.Any(r => r is "LIFECYCLE_EFFECTIVE_DATE_UNVERIFIED" or "SESSION_STATUS_UNVERIFIED")),
            candidates.Count(c => c.Outcome.Reasons.Contains("NO_EXECUTION_EVIDENCE")), usable,
            candidates.Length == 0 ? "NOT_APPLICABLE" : $"{usable}/{candidates.Length}", breakdown);
        var content = new T1AssociationContent(1, PolicyVersion,
            "Declared association only; not authenticated PIT, executable fills or performance evaluation.", records, coverage);
        return new(1, DeclarationFingerprint(content), content);
    }

    private static bool Aligned(T1PriceBasisState state) => state is T1PriceBasisState.ALIGNED_RAW or T1PriceBasisState.ALIGNED_ADJUSTED_WITH_PROVENANCE;

    private static T1AssociationRecord BuildAssociation(T1AssociationInput input)
    {
        var request = input.Evidence;
        var sample = Evaluate(request with { Outcomes = null });
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
        var records = new List<T1CandidateAssociation>();
        foreach (var original in sample.Captured)
        {
            bool linked = sample.Binding == T1ReadinessStatus.PASS;
            var ranked = linked ? package!.Ranked.SingleOrDefault(r => r.Symbol == original.Symbol) : null;
            var excluded = linked ? package!.Excluded.SingleOrDefault(r => r.Symbol == original.Symbol) : null;
            var category = !linked ? sample.Binding == T1ReadinessStatus.FAIL || sample.RankingIntegrity == T1ReadinessStatus.FAIL
                ? T1CandidateAssociationCategory.SOURCE_OR_RANKING_INVALID : T1CandidateAssociationCategory.ELIGIBLE_STATUS_NOT_VERIFIED
                : ranked is not null ? T1CandidateAssociationCategory.SHORTLISTED : excluded is not null ? T1CandidateAssociationCategory.EXCLUDED
                : T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED;
            var basis = input.DecisionBases?.GetValueOrDefault(original.Symbol) ?? request.DecisionBasis;
            var envelope = envelopes.SingleOrDefault(e => e.Symbol == original.Symbol);
            var check = CheckOutcome(original.Symbol, sample.DecisionDate!.Value, source.Snapshot!.RequestedAsOf,
                sample.Calendar, envelope, basis);
            // B's absent-envelope status is preserved; C also explicitly accounts for its absent target bar.
            if (envelope?.Daily is null && !check.Reasons.Contains("MISSING_TARGET_DAILY_BAR"))
                check = check with { Reasons = Ordered(check.Reasons.Append("MISSING_TARGET_DAILY_BAR")) };
            bool available = envelope?.Daily is not null && envelope.State == T1OutcomeSourceState.AVAILABLE;
            bool usable = linked && sample.Calendar.Status == T1ReadinessStatus.PASS && available
                && check.SourceAvailability == T1ReadinessStatus.PASS && check.FieldValidity == T1ReadinessStatus.PASS
                && Aligned(check.PriceBasis) && envelope!.Session == T1OutcomeSession.OPEN;
            var reasons = new List<string>(check.Reasons);
            reasons.Add(usable ? "OUTCOME_AVAILABLE_DECLARED" : "OUTCOME_SOURCE_NOT_VERIFIED");
            if (sample.FullEligibleOrdering != T1ReadinessStatus.PASS) reasons.Add("FULL_ORDERING_NOT_VERIFIED");
            if (category == T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED) reasons.Add("ELIGIBLE_STATUS_NOT_INDEPENDENTLY_RECONSTRUCTED");
            records.Add(new(original, category, ranked?.T1Rank, excluded?.Code, excluded?.Reason,
                T1ReadinessStatus.NOT_VERIFIED, check, basis is null ? "" : DeclarationFingerprint(basis), available, usable, Ordered(reasons)));
        }
        sample = sample with { Outcomes = records.Select(c => c.Outcome).ToArray(),
            Overall = sample.Overall == T1ReadinessStatus.FAIL || records.Any(c => c.Outcome.Status == T1ReadinessStatus.FAIL)
                ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED };
        return new(sample, source.Structure == T1ReadinessStatus.PASS ? source.Snapshot!.Scan.StrategyVersion : "",
            source.Structure == T1ReadinessStatus.PASS ? source.Snapshot!.Scan.ParameterFingerprint : "",
            source.Structure == T1ReadinessStatus.PASS ? source.Snapshot!.RequestedAsOf : null, records);
    }

    public static string CanonicalAssociation(T1AssociationArtifact artifact) => JsonSerializer.Serialize(artifact, Json);

    public static bool VerifyAssociation(T1AssociationArtifact artifact)
    {
        try
        {
            if (artifact.SchemaVersion != 1 || artifact.Content.SchemaVersion != 1 || artifact.Content.PolicyVersion != PolicyVersion
                || artifact.Fingerprint != DeclarationFingerprint(artifact.Content)) return false;
            var records = artifact.Content.Records;
            if (records.Count > 100 || records.Any(r => r.Candidates.Count > 20
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
