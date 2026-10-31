using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData;

public static partial class T1EvidenceReadiness
{
    private static bool Digest(string value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static void ValidateCalendarMap(IReadOnlyDictionary<string, T1CalendarEvidence> calendars)
    {
        if (calendars.Count > 3 || calendars.Any(p => p.Key is not ("SSE" or "SZSE" or "BSE") || p.Value is null || p.Value.Exchange != p.Key))
            throw new ArgumentException("CALENDAR_EXCHANGE_IDENTITY_INVALID");
    }

    private static T1AssociationRecord DeriveAssociation(T1AssociationDerivation proof)
    {
        var sample = proof.SourceFacts;
        ValidateCalendarMap(proof.Calendars);
        if (sample.Captured.Count > 20 || proof.Outcomes.Count > 100 || proof.DecisionBases.Count > 20
            || proof.Ranked.Count > 20 || proof.Excluded.Count > 20
            || proof.Outcomes.Any(e => e is null) || proof.Outcomes.Select(e => e.Symbol).Distinct(StringComparer.Ordinal).Count() != proof.Outcomes.Count
            || sample.Captured.Select(c => c.Symbol).Distinct(StringComparer.Ordinal).Count() != sample.Captured.Count
            || sample.Captured.Any(c => ExchangeOf(c.Symbol) is null || c.OriginalRank < 1 || !double.IsFinite(c.OriginalScore))
            || sample.Captured.Select(c => c.OriginalRank).Distinct().Count() != sample.Captured.Count
            || sample.PolicyVersion != PolicyVersion || !Enum.IsDefined(sample.Origin)
            || sample.PitAvailability != T1ReadinessStatus.NOT_VERIFIED || sample.ExecutionEvidence != T1ReadinessStatus.NOT_VERIFIED
            || sample.EvidenceClass is not (T1EvidenceClass.UNVERIFIED or T1EvidenceClass.RETROSPECTIVE_DIAGNOSTIC)
            || sample.Overall == T1ReadinessStatus.PASS) throw new InvalidDataException("SOURCE_FACTS_INVALID");
        if (proof.SnapshotFilePresent && !Digest(proof.SourceFileKey)) throw new InvalidDataException("SOURCE_FILE_IDENTITY_INVALID");
        bool validSource = sample.SnapshotStructure == T1ReadinessStatus.PASS;
        if (sample.SnapshotStructure is not (T1ReadinessStatus.PASS or T1ReadinessStatus.FAIL or T1ReadinessStatus.BLOCKED)
            || sample.RankingIntegrity is not (T1ReadinessStatus.PASS or T1ReadinessStatus.FAIL or T1ReadinessStatus.BLOCKED)
            || sample.Binding is not (T1ReadinessStatus.PASS or T1ReadinessStatus.FAIL or T1ReadinessStatus.BLOCKED)
            || sample.Overall != (sample.SnapshotStructure == T1ReadinessStatus.FAIL || sample.RankingIntegrity == T1ReadinessStatus.FAIL
                || sample.Binding == T1ReadinessStatus.FAIL ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED)
            || sample.Outcomes.Count != 0
            || DeclarationFingerprint(sample.Calendar) != DeclarationFingerprint(CalendarMissing())
            || sample.DecisionBasisFingerprint != (proof.FallbackBasis is null ? "" : DeclarationFingerprint(proof.FallbackBasis))
            || sample.RankingIntegrity == T1ReadinessStatus.PASS != (proof.RankingRequest is not null))
            throw new InvalidDataException("SOURCE_GATE_CLAIM_INVALID");
        if (validSource && (sample.RunId is null || sample.RunId == Guid.Empty || sample.DecisionDate is null
            || !Digest(sample.SnapshotFingerprint) || !proof.SnapshotFilePresent || proof.Cutoff is null
            || ChinaExchangeTime.Date(proof.Cutoff.Value) != sample.DecisionDate || Missing(proof.SourceParameterFingerprint)
            || proof.StrategyVersion != (sample.Strategy switch { "Classic" => SparrowStrategyVersions.Classic,
                "V2" => SparrowStrategyVersions.V2, "V3" => SparrowStrategyVersions.V3, "V4" => SparrowStrategyVersions.V4,
                "V5" => SparrowStrategyVersions.V5, "V6" => SparrowStrategyVersions.V6, _ => "UNSUPPORTED" })))
            throw new InvalidDataException("SOURCE_IDENTITY_INVALID");
        if (!validSource && (sample.Captured.Count != 0 || proof.Cutoff is not null || proof.StrategyVersion != "" || proof.SourceParameterFingerprint != ""))
            throw new InvalidDataException("UNVERIFIED_SOURCE_CANNOT_SUPPLY_POOL");
        if (validSource && (proof.Outcomes.Any(e => !sample.Captured.Any(c => c.Symbol == e.Symbol))
            || proof.DecisionBases.Keys.Any(s => !sample.Captured.Any(c => c.Symbol == s)))) throw new InvalidDataException("OUTSIDE_CAPTURED_POOL");
        if (sample.RankingIntegrity == T1ReadinessStatus.PASS && !Digest(sample.PackageFingerprint)) throw new InvalidDataException("PACKAGE_IDENTITY_INVALID");
        bool linked = sample.Binding == T1ReadinessStatus.PASS;
        if (!linked && sample.Coverage is not null || (!validSource || sample.RankingIntegrity != T1ReadinessStatus.PASS)
            && sample.Binding != T1ReadinessStatus.BLOCKED) throw new InvalidDataException("SOURCE_BINDING_INVALID");
        if (linked)
        {
            var cov = sample.Coverage;
            var request = proof.RankingRequest!;
            if (!validSource || sample.RankingIntegrity != T1ReadinessStatus.PASS || cov is null || cov.CapturedCandidates != sample.Captured.Count
                || request.RunId != sample.RunId || request.TradingDate != sample.DecisionDate || request.Cutoff != proof.Cutoff
                || request.SnapshotFingerprint != sample.SnapshotFingerprint || request.CapturedCandidateCount != sample.Captured.Count
                || request.ShortlistTopN != cov.RequestedShortlistSize
                || cov.EligibleCandidates < 0 || cov.ExcludedCandidates < 0 || cov.CapturedCandidates != cov.EligibleCandidates + cov.ExcludedCandidates
                || cov.RequestedShortlistSize is < 1 or > 20 || cov.ActualShortlistSize != Math.Min(cov.EligibleCandidates, cov.RequestedShortlistSize)
                || proof.Ranked.Count != cov.ActualShortlistSize || proof.Excluded.Count != cov.ExcludedCandidates
                || proof.Ranked.Select(r => r.Symbol).Concat(proof.Excluded.Select(r => r.Symbol)).Distinct(StringComparer.Ordinal).Count() != proof.Ranked.Count + proof.Excluded.Count
                || !proof.Ranked.Select(r => r.T1Rank).SequenceEqual(Enumerable.Range(1, cov.ActualShortlistSize))) throw new InvalidDataException("PACKAGE_PROJECTION_INVALID");
            foreach (var row in proof.Ranked)
            {
                var c = sample.Captured.SingleOrDefault(c => c.Symbol == row.Symbol); var e = row.Explanation;
                if (c is null || e is null || c.Name != row.Name || c.OriginalRank != e.OriginalRank || c.OriginalScore != e.OriginalScore
                    || e.Strategy != sample.Strategy || e.StrategyVersion != proof.StrategyVersion || e.SnapshotFingerprint != sample.SnapshotFingerprint)
                    throw new InvalidDataException("RANK_ATTRIBUTION_INVALID");
            }
            foreach (var row in proof.Excluded)
            {
                var c = sample.Captured.SingleOrDefault(c => c.Symbol == row.Symbol);
                if (c is null || c.Name != row.Name || c.OriginalRank != row.OriginalRank || row.Strategy != sample.Strategy
                    || row.StrategyVersion != proof.StrategyVersion || !Enum.IsDefined(row.Code) || row.Code == T1RankingExclusionCode.None || Missing(row.Reason))
                    throw new InvalidDataException("EXCLUSION_ATTRIBUTION_INVALID");
            }
        }
        if (sample.CapturedPool != (linked ? T1ReadinessStatus.PASS : T1ReadinessStatus.BLOCKED)
            || sample.FullEligibleOrdering != (linked && sample.Coverage!.EligibleCandidates == sample.Coverage.ActualShortlistSize ? T1ReadinessStatus.PASS : T1ReadinessStatus.NOT_VERIFIED))
            throw new InvalidDataException("SOURCE_POOL_CLAIM_INVALID");
        var candidates = new List<T1CandidateAssociation>();
        foreach (var original in sample.Captured)
        {
            string exchange = ExchangeOf(original.Symbol)!;
            var calendar = MapNextTradingDay(sample.DecisionDate!.Value, proof.Calendars.GetValueOrDefault(exchange));
            var ranked = linked ? proof.Ranked.SingleOrDefault(r => r.Symbol == original.Symbol) : null;
            var excluded = linked ? proof.Excluded.SingleOrDefault(r => r.Symbol == original.Symbol) : null;
            var category = !linked ? sample.Binding == T1ReadinessStatus.FAIL || sample.RankingIntegrity == T1ReadinessStatus.FAIL
                ? T1CandidateAssociationCategory.SOURCE_OR_RANKING_INVALID : T1CandidateAssociationCategory.ELIGIBLE_STATUS_NOT_VERIFIED
                : ranked is not null ? T1CandidateAssociationCategory.SHORTLISTED : excluded is not null ? T1CandidateAssociationCategory.EXCLUDED
                : T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED;
            var basis = proof.DecisionBases.GetValueOrDefault(original.Symbol) ?? proof.FallbackBasis;
            var envelope = proof.Outcomes.SingleOrDefault(e => e.Symbol == original.Symbol);
            var check = CheckOutcome(original.Symbol, sample.DecisionDate.Value, proof.Cutoff!.Value, calendar, envelope, basis);
            if (envelope?.Daily is null && !check.Reasons.Contains("MISSING_TARGET_DAILY_BAR"))
                check = check with { Reasons = Ordered(check.Reasons.Append("MISSING_TARGET_DAILY_BAR")) };
            bool available = envelope?.Daily is not null && envelope.State == T1OutcomeSourceState.AVAILABLE;
            bool usable = linked && calendar.Status == T1ReadinessStatus.PASS && available && check.SourceAvailability == T1ReadinessStatus.PASS
                && check.FieldValidity == T1ReadinessStatus.PASS && Aligned(check.PriceBasis) && envelope!.Session == T1OutcomeSession.OPEN;
            var reasons = new List<string>(check.Reasons) { usable ? "OUTCOME_AVAILABLE_DECLARED" : "OUTCOME_SOURCE_NOT_VERIFIED" };
            if (sample.FullEligibleOrdering != T1ReadinessStatus.PASS) reasons.Add("FULL_ORDERING_NOT_VERIFIED");
            if (category == T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED) reasons.Add("ELIGIBLE_STATUS_NOT_INDEPENDENTLY_RECONSTRUCTED");
            candidates.Add(new(original, category, ranked?.T1Rank, excluded?.Code, excluded?.Reason, T1ReadinessStatus.NOT_VERIFIED,
                check, basis is null ? "" : DeclarationFingerprint(basis), available, usable, Ordered(reasons), exchange, calendar));
        }
        var maps = candidates.Select(c => c.Calendar!).ToArray();
        var summary = maps.Length > 0 && candidates.Select(c => c.Exchange).Distinct(StringComparer.Ordinal).Count() == 1
            ? maps[0] : maps.Length == 0 ? CalendarMissing() : new T1CalendarMapping(T1ReadinessStatus.PARTIAL, null, "MULTIPLE", "",
                Ordered(maps.SelectMany(m => m.Reasons).Append("CANDIDATE_SPECIFIC_CALENDARS")));
        sample = sample with { Calendar = summary, Outcomes = candidates.Select(c => c.Outcome).ToArray(),
            Reasons = Ordered(sample.Reasons.Where(r => r is not ("CALENDAR_NOT_VERIFIED" or "MISSING_NEXT_TRADING_DATE")).Concat(summary.Reasons)),
            Overall = sample.Overall == T1ReadinessStatus.FAIL || maps.Any(m => m.Status == T1ReadinessStatus.FAIL)
                || candidates.Any(c => c.Outcome.Status == T1ReadinessStatus.FAIL) ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED };
        return new(sample, proof.StrategyVersion, proof.SourceParameterFingerprint, proof.Cutoff, candidates, proof);
    }

    private static T1AssociationCoverage RecomputeAssociationCoverage(IReadOnlyList<T1AssociationRecord> records)
    {
        var all = records.SelectMany(r => r.Candidates).ToArray(); var samples = records.Select(r => r.Evidence).ToArray();
        var valid = samples.Where(s => s.SnapshotStructure == T1ReadinessStatus.PASS).ToArray();
        var linked = samples.Where(s => s.Binding == T1ReadinessStatus.PASS).ToArray();
        static IReadOnlyDictionary<string, int> Counts(IEnumerable<string> values) => new SortedDictionary<string, int>(values.GroupBy(v => v, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count()), StringComparer.Ordinal);
        int files = records.Where(r => r.Derivation!.SnapshotFilePresent).Select(r => r.Derivation!.SourceFileKey).Distinct(StringComparer.Ordinal).Count();
        int usable = all.Count(c => c.AssociationUsableDeclared);
        var source = new T1EvidenceCoverageReport(PolicyVersion, files == 0 && samples.All(s => s.RankingIntegrity != T1ReadinessStatus.PASS) ? "NO_SAMPLE" : "OBSERVED_UNAUTHENTICATED_EVIDENCE",
            samples.Any(s => s.Overall == T1ReadinessStatus.FAIL) ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED,
            files, valid.Select(s => s.SnapshotFingerprint).Distinct(StringComparer.Ordinal).Count(),
            samples.Where(s => s.RankingIntegrity == T1ReadinessStatus.PASS).Select(s => s.PackageFingerprint).Distinct(StringComparer.Ordinal).Count(),
            linked.Length, samples.Count(s => s.Binding == T1ReadinessStatus.FAIL), valid.Select(s => s.DecisionDate).Distinct().Count(), valid.Select(s => s.RunId).Distinct().Count(),
            linked.Sum(s => s.Coverage!.CapturedCandidates), linked.Sum(s => s.Coverage!.EligibleCandidates), linked.Sum(s => s.Coverage!.ExcludedCandidates),
            linked.Sum(s => s.Coverage!.ActualShortlistSize), linked.Sum(s => s.Coverage!.EligibleCandidates - s.Coverage.ActualShortlistSize),
            all.Count(c => c.Calendar!.Status == T1ReadinessStatus.PASS), all.Count(c => c.Calendar!.Status != T1ReadinessStatus.PASS) + records.Count(r => r.Candidates.Count == 0),
            all.Count(c => c.Outcome.SourceAvailability == T1ReadinessStatus.PASS && c.Outcome.FieldValidity == T1ReadinessStatus.PASS),
            all.Count(c => c.Outcome.Reasons.Contains("PRICE_BASIS_MISMATCH")), samples.Count(s => s.ExecutionEvidence != T1ReadinessStatus.PASS),
            samples.Count(s => s.Origin == T1EvidenceOrigin.TEST_FIXTURE), 0, "NOT_APPLICABLE", Counts(valid.Select(s => s.Strategy)),
            Counts(samples.Select(s => s.EvidenceClass.ToString())), Counts(all.SelectMany(c => c.Outcome.Reasons)), samples);
        var breakdown = records.GroupBy(r => (Date: r.Evidence.DecisionDate?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "NOT_APPLICABLE", r.Evidence.Strategy))
            .OrderBy(g => g.Key.Date, StringComparer.Ordinal).ThenBy(g => g.Key.Strategy, StringComparer.Ordinal)
            .Select(g => new T1AssociationBreakdown(g.Key.Date, g.Key.Strategy, g.Sum(r => r.Candidates.Count), g.Sum(r => r.Candidates.Count(c => c.AssociationUsableDeclared)),
                g.Count(r => r.Evidence.Origin == T1EvidenceOrigin.TEST_FIXTURE))).ToArray();
        return new(source, all.Length, all.Length, all.Count(c => c.OutcomeAvailableDeclared), all.Count(c => c.Outcome.FieldValidity == T1ReadinessStatus.PASS),
            all.Count(c => Aligned(c.Outcome.PriceBasis)), all.Count(c => !Aligned(c.Outcome.PriceBasis)), all.Count(c => c.Outcome.Reasons.Contains("MISSING_TARGET_DAILY_BAR")),
            all.Count(c => c.Outcome.SourceState != T1OutcomeSourceState.AVAILABLE), all.Count(c => c.Outcome.Reasons.Contains("SUSPENDED")),
            all.Count(c => c.Outcome.Reasons.Any(r => r is "LIFECYCLE_EFFECTIVE_DATE_UNVERIFIED" or "SESSION_STATUS_UNVERIFIED")),
            all.Count(c => c.Outcome.Reasons.Contains("NO_EXECUTION_EVIDENCE")), usable, all.Length == 0 ? "NOT_APPLICABLE" : $"{usable}/{all.Length}", breakdown);
    }

    /// <summary>Schema 1 can only be inspected with the explicitly weaker legacy API; no automatic migration.</summary>
    public static bool VerifyLegacyAssociationIntegrity(T1AssociationArtifact artifact) => VerifyAssociationV1Structure(artifact);

    public static bool VerifyAssociation(T1AssociationArtifact artifact)
    {
        try
        {
            if (artifact.SchemaVersion != 2 || artifact.Content.SchemaVersion != 2 || artifact.Content.PolicyVersion != PolicyVersion
                || artifact.Fingerprint != DeclarationFingerprint(artifact.Content) || artifact.Content.Records.Count > 100) return false;
            var records = artifact.Content.Records;
            if (records.Any(r => r.Derivation is null || DeclarationFingerprint(DeriveAssociation(r.Derivation)) != DeclarationFingerprint(r))) return false;
            if (records.Where(r => r.Evidence.SnapshotFingerprint.Length > 0 && r.Evidence.PackageFingerprint.Length > 0)
                .GroupBy(r => (r.Evidence.SnapshotFingerprint, r.Evidence.PackageFingerprint)).Any(g => g.Count() > 1)) return false;
            return DeclarationFingerprint(RecomputeAssociationCoverage(records)) == DeclarationFingerprint(artifact.Content.Coverage)
                && artifact.Content.AuthenticationLimitation == "Declared association only; not authenticated PIT, executable fills or performance evaluation.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or NullReferenceException or InvalidOperationException or OverflowException or KeyNotFoundException) { return false; }
    }
}
