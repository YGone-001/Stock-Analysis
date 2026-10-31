namespace AIHelper.Services.StockData;

public static class T1ResearchReport
{
    public static void Write(string sampleDirectory, T1PilotEvidence? evidence, TextWriter output)
    {
        var m = T1ResearchSamples.Verify(sampleDirectory); var s = m.Content;
        var a = evidence is null ? T1EvidenceReadiness.Associate(new[] { new T1AssociationInput(new(T1ResearchSamples.SnapshotPath(sampleDirectory),
            T1ResearchSamples.RankingPath(sampleDirectory, m), Origin: s.Origin)) }) : T1OutcomePilot.Associate(sampleDirectory, evidence);
        if (!T1EvidenceReadiness.VerifyAssociation(a)) throw new InvalidDataException("REPORT_ASSOCIATION_INVALID");
        var c = a.Content.Coverage;
        output.WriteLine("ENGINEERING_READY; PIT_AUTHENTICATION_UNVERIFIED; PRODUCTION_EVALUATION_BLOCKED");
        output.WriteLine(s.Origin == T1EvidenceOrigin.TEST_FIXTURE ? "SAMPLE=TEST_FIXTURE; REAL_SAMPLE_NOT_YET_CAPTURED" : "SAMPLE=REAL_SAMPLE_RETAINED (operator-declared origin; not independently authenticated)");
        output.WriteLine($"RUN={s.RunId}; STRATEGY={s.Strategy}/{s.StrategyVersion}; DATE={s.TradingDate:yyyy-MM-dd}");
        output.WriteLine($"CUTOFF={s.RequestedCutoff:O}; CAPTURED={s.CapturedAtUtc:O}; SCAN_TO_CAPTURE_MS={s.ScanToCaptureMilliseconds}; OUTSIDE_STUDY_WINDOW={s.OutsideStudyWindow}");
        output.WriteLine($"ARCHIVE=VERIFIED; SNAPSHOT=VERIFIED; RANKING_PACKAGE=VERIFIED; MANIFEST={m.Fingerprint}; CLASS={s.EvidenceClass}");
        output.WriteLine($"CAPTURED={s.Coverage.CapturedCandidates}; ELIGIBLE={s.Coverage.EligibleCandidates}; SHORTLISTED={s.Coverage.ActualShortlistSize}; EXCLUDED={s.Coverage.ExcludedCandidates}; TRUNCATED={s.Coverage.EligibleCandidates - s.Coverage.ActualShortlistSize}");
        output.WriteLine($"OBSERVED_DAILY_RECORDS={c.DeclaredAvailableBars}; MISSING_OR_UNVERIFIED_DAILY={c.MissingBars}; UNKNOWN_OR_BLOCKED_BASIS={c.BasisBlocked}");
        output.WriteLine($"OUTCOME_SOURCE_AVAILABLE={(c.DeclaredAvailableBars > 0 ? "DECLARED" : "NOT_VERIFIED")}");
        int established = evidence is null ? 0 : T1OutcomePilot.EstablishedCoverage(evidence, a);
        bool complete = s.Candidates.Count > 0 && established == s.Candidates.Count && c.ValidatedFields == s.Candidates.Count;
        output.WriteLine($"OUTCOME_COVERAGE_ESTABLISHED={(complete ? "SOURCE_DECLARED" : established > 0 ? "PARTIAL" : "NOT_VERIFIED")}; DAILY_SOURCE_COVERAGE={(evidence is null || !evidence.Content.Daily.Any(d => d.Acquisition is not null) ? "NOT_APPLICABLE" : $"{established}/{s.Candidates.Count}")}");
        output.WriteLine($"ASSOCIATION=SCHEMA_2_VERIFIED_IN_MEMORY; FINGERPRINT={a.Fingerprint}; USABLE_DECLARED={c.AssociationCoverage}; EXECUTION=NOT_VERIFIED; AUTHENTICATED_PRODUCTION_SAMPLES=0");
        output.WriteLine("EVIDENCE_READINESS=" + c.SourceCoverage.Overall);
        output.WriteLine("SOURCE_REASONS=" + (evidence is null ? "NOT_VERIFIED (no retained outcome input)" : string.Join(',', evidence.Content.Reasons)));
        foreach (var candidate in a.Content.Records[0].Candidates)
        {
            var quality = s.Candidates.Single(x => x.Original.Symbol == candidate.Original.Symbol);
            output.WriteLine($"CANDIDATE={candidate.Original.Symbol}; EXCHANGE={candidate.Exchange}; ORIGINAL_RANK={candidate.Original.OriginalRank}; ORIGINAL_SCORE={candidate.Original.OriginalScore.ToString(System.Globalization.CultureInfo.InvariantCulture)}; CATEGORY={candidate.Category}; TARGET={candidate.Calendar!.NextTradingDate?.ToString("yyyy-MM-dd") ?? "NOT_VERIFIED"}; CALENDAR={candidate.Calendar.Status}; MINUTES={quality.MinuteObservations}; LATEST_USABLE={quality.LatestUsableObservation?.ToString("O") ?? "NOT_VERIFIED"}; FRESHNESS={quality.Freshness.LatestQuality}; BASIS={quality.PriceBasis}; REASONS={string.Join(',', candidate.Reasons)}");
        }
    }
    public static void Inventory(string archiveRoot, int limit, TextWriter output, string? outcomeRoot = null)
    {
        if (limit is < 1 or > 100) throw new ArgumentException("INVENTORY_BOUND");
        string root = T1ResearchStorage.Root(archiveRoot);
        if (!Directory.Exists(root)) { output.WriteLine("REAL_SAMPLE_NOT_YET_CAPTURED; SAMPLES=0; COVERAGE=NOT_APPLICABLE"); return; }
        var directories = Directory.EnumerateDirectories(root).Where(d => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(d), "^[0-9a-f]{32}-[0-9A-F]{64}$"))
            .Order(StringComparer.Ordinal).Take(limit + 1).ToArray();
        var samples = new List<T1ResearchManifest>();
        var coverage = new Dictionary<string, (int Available, int Missing, string Reasons)>();
        var sources = new List<T1PilotEvidence>();
        if (outcomeRoot is not null)
        {
            string sourceRoot = T1ResearchStorage.Root(outcomeRoot);
            if (Directory.Exists(sourceRoot))
            {
                var retained = Directory.EnumerateDirectories(sourceRoot, "t1-outcomes-*").Take(101).ToArray();
                foreach (string path in retained.Take(100))
                {
                    try { sources.Add(T1OutcomePilot.Load(Path.Combine(path, "source.json"))); }
                    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidDataException) { output.WriteLine("INVALID_OUTCOME_INPUT=" + Path.GetFileName(path)); }
                }
                output.WriteLine("OUTCOME_PACKAGES_MORE_NOT_INSPECTED=" + (retained.Length > 100));
            }
        }
        foreach (var directory in directories.Take(limit))
        {
            try
            {
                var m = T1ResearchSamples.Verify(directory); samples.Add(m);
                var source = sources.Where(e => e.Content.SampleFingerprint == m.Fingerprint).OrderByDescending(e => e.Content.Daily.Select(d => d.RetrievedAtUtc).DefaultIfEmpty().Max()).ThenBy(e => e.Fingerprint, StringComparer.Ordinal).FirstOrDefault();
                if (source is not null)
                {
                    try
                    {
                        var result = T1OutcomePilot.Associate(directory, source);
                        coverage[m.Fingerprint] = (result.Content.Coverage.DeclaredAvailableBars, result.Content.Coverage.MissingBars,
                            string.Join(',', result.Content.Records.SelectMany(r => r.Candidates).SelectMany(c => c.Reasons).Distinct().Order(StringComparer.Ordinal)));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or ArgumentException) { output.WriteLine("INVALID_OUTCOME_EVIDENCE=" + source.Fingerprint); source = null; }
                }
                output.WriteLine($"SAMPLE={Path.GetFileName(directory)}; DATE={m.Content.TradingDate:yyyy-MM-dd}; STRATEGY={m.Content.Strategy}; ORIGIN={m.Content.Origin}; SNAPSHOT=VERIFIED; RANKING=VERIFIED; CLASS={m.Content.EvidenceClass}; OUTCOME={(source is null ? "NOT_VERIFIED" : "SCHEMA_2_VERIFIED_DECLARATIONS")}");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or ArgumentException)
            { output.WriteLine("INVALID_ARCHIVE=" + Path.GetFileName(directory)); }
        }
        if (samples.Count == 0 || samples.All(s => s.Content.Origin == T1EvidenceOrigin.TEST_FIXTURE)) output.WriteLine("REAL_SAMPLE_NOT_YET_CAPTURED");
        foreach (var group in samples.GroupBy(s => (s.Content.TradingDate, s.Content.Strategy)).OrderBy(g => g.Key.TradingDate).ThenBy(g => g.Key.Strategy, StringComparer.Ordinal))
        {
            var observed = group.Where(s => coverage.ContainsKey(s.Fingerprint)).ToArray();
            string ratio = observed.Length == 0 ? "NOT_APPLICABLE" : $"{observed.Sum(s => coverage[s.Fingerprint].Available)}/{observed.Sum(s => s.Content.Candidates.Count)}";
            output.WriteLine($"DATE={group.Key.TradingDate:yyyy-MM-dd}; STRATEGY={group.Key.Strategy}; SAMPLES={group.Count()}; CANDIDATES={group.Sum(s => s.Content.Candidates.Count)}; SHORTLISTED={group.Sum(s => s.Content.Coverage.ActualShortlistSize)}; EXCLUDED={group.Sum(s => s.Content.Coverage.ExcludedCandidates)}; FIXTURES={group.Count(s => s.Content.Origin == T1EvidenceOrigin.TEST_FIXTURE)}; OBSERVED_NEXT_DAY_BARS={ratio}; SAMPLES_WITHOUT_OUTCOME_INPUT={group.Count() - observed.Length}; CLASSES={string.Join(',', group.Select(s => s.Content.EvidenceClass).Distinct())}; SOURCE_REASONS={string.Join(',', observed.Select(s => coverage[s.Fingerprint].Reasons))}; PIT_AUTHENTICATION_UNVERIFIED");
        }
        output.WriteLine($"INSPECTED={Math.Min(directories.Length, limit)}; MORE_NOT_INSPECTED={directories.Length > limit}; PRODUCTION_EVALUATION_BLOCKED");
    }
}
