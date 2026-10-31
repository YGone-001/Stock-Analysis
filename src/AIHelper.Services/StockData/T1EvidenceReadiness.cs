using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData;

/// <summary>
/// Offline structural evidence foundation. No network, ranking generation, fills or returns.
/// A PASS checks the stated local contract, never external authenticity. Current schema-1 inputs
/// cannot establish authenticated live/archive PIT evidence; this version deliberately fails closed.
/// </summary>
public static partial class T1EvidenceReadiness
{
    public const string PolicyVersion = "t1-evidence-readiness-v1";
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static readonly JsonSerializerOptions DeclarationJson = new(Json)
        { NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals };
    private static bool Missing(string? value) => string.IsNullOrWhiteSpace(value);
    private static string[] Ordered(IEnumerable<string> reasons) => reasons.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    // New declarations have no established artifact hash. Hash only their canonical typed content;
    // existing snapshot/package hashes continue to use their established implementations unchanged.
    private static string DeclarationFingerprint<T>(T value) => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, DeclarationJson)));
    internal static string? ExchangeOf(string symbol) => symbol.Length == 6 && symbol.All(char.IsAsciiDigit)
        ? symbol[0] switch { '6' => "SSE", '0' or '3' => "SZSE", '4' or '8' or '9' => "BSE", _ => null } : null;

    private static FileStream OpenBounded(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= IntradaySnapshotStore.MaximumBytes) return stream;
        stream.Dispose();
        throw new InvalidDataException("COMPONENT_TOO_LARGE");
    }

    private static byte[] ReadBounded(FileStream stream)
    {
        long length = stream.Length;
        if (length > IntradaySnapshotStore.MaximumBytes) throw new InvalidDataException("COMPONENT_TOO_LARGE");
        byte[] bytes = new byte[(int)length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw new InvalidDataException("COMPONENT_CHANGED");
        return bytes;
    }

    public static T1SnapshotVerification VerifySnapshot(string? path)
    {
        if (Missing(path)) return new(T1ReadinessStatus.BLOCKED, "", null, new[] { "NO_AUTHORITATIVE_SAMPLE" });
        try
        {
            // Bounded read uses the existing envelope and canonical snapshot hash, without altering schema 1.
            using var lease = OpenBounded(path!);
            var envelope = JsonSerializer.Deserialize<SnapshotEnvelope>(ReadBounded(lease));
            if (envelope?.Snapshot is null || envelope.SchemaVersion != 1 || envelope.Snapshot.SchemaVersion != 1)
                throw new InvalidDataException("SNAPSHOT_SCHEMA_UNSUPPORTED");
            var snapshot = envelope.Snapshot;
            if (envelope.Fingerprint != IntradaySnapshotStore.Fingerprint(snapshot))
                throw new InvalidDataException("SNAPSHOT_FINGERPRINT_MISMATCH");
            var reasons = new List<string>();
            var scan = snapshot.Scan;
            if (scan is null || scan.RunId == Guid.Empty || Missing(scan.ParameterFingerprint)) reasons.Add("SNAPSHOT_SCAN_IDENTITY_INVALID");
            var versions = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Classic"] = SparrowStrategyVersions.Classic, ["V2"] = SparrowStrategyVersions.V2,
                ["V3"] = SparrowStrategyVersions.V3, ["V4"] = SparrowStrategyVersions.V4,
                ["V5"] = SparrowStrategyVersions.V5, ["V6"] = SparrowStrategyVersions.V6
            };
            if (scan is null || !versions.TryGetValue(scan.Strategy ?? "", out var version) || version != scan.StrategyVersion)
                reasons.Add("SNAPSHOT_STRATEGY_UNSUPPORTED");
            if (scan is null || scan.TopN is < 1 or > 20 || snapshot.Symbols is null || snapshot.Symbols.Count > scan.TopN)
                reasons.Add("SNAPSHOT_CAPTURED_COUNT_INVALID");
            if (scan is null || scan.ScanStartedAtUtc == default || scan.ScanCompletedAtUtc < scan.ScanStartedAtUtc
                || snapshot.AcquisitionStartedAtUtc < scan.ScanCompletedAtUtc || snapshot.CapturedAtUtc < snapshot.AcquisitionStartedAtUtc)
                reasons.Add("SNAPSHOT_TIME_ORDER_INVALID");
            if (snapshot.TradingDate == default || snapshot.RequestedAsOf == default || snapshot.CapturedAtUtc == default
                || ChinaExchangeTime.Date(snapshot.RequestedAsOf) != snapshot.TradingDate
                || ChinaExchangeTime.Date(snapshot.CapturedAtUtc) != snapshot.TradingDate
                || scan is not null && (ChinaExchangeTime.Date(scan.ScanStartedAtUtc) != snapshot.TradingDate
                    || ChinaExchangeTime.Date(snapshot.AcquisitionStartedAtUtc) != snapshot.TradingDate)
                || snapshot.Timezone != "Asia/Shanghai (UTC+08:00)") reasons.Add("SNAPSHOT_DATE_INVALID");
            if (snapshot.CalendarProvenance is null || Missing(snapshot.CalendarProvenance.Source)) reasons.Add("SNAPSHOT_PROVENANCE_MISSING");
            if (snapshot.Coverage != "selected TopN only; not an all-market or unbiased training dataset")
                reasons.Add("SNAPSHOT_COVERAGE_UNSUPPORTED");
            var symbols = new HashSet<string>(StringComparer.Ordinal);
            var ranks = new HashSet<int>();
            foreach (var symbol in snapshot.Symbols ?? Array.Empty<SnapshotSymbol>())
            {
                if (symbol is null || Missing(symbol.Symbol) || ExchangeOf(symbol.Symbol) is null || !symbols.Add(symbol.Symbol))
                { reasons.Add("SNAPSHOT_SYMBOL_INVALID"); continue; }
                if (Missing(symbol.Name) || Missing(symbol.RankingProfile) || symbol.Rank < 1 || !ranks.Add(symbol.Rank) || !double.IsFinite(symbol.Score))
                    reasons.Add("SNAPSHOT_ORIGINAL_ATTRIBUTION_INVALID");
                if (symbol.Minutes?.Rows is null || symbol.Minutes.Rows.Count > 500
                    || symbol.Minutes.Rows.Any(r => r is null || r.Provenance is null || r.Symbol != symbol.Symbol))
                    reasons.Add("SNAPSHOT_MINUTE_STRUCTURE_INVALID");
                if (symbol.Minutes?.Provenance is null || Missing(symbol.Minutes.Provenance.Source)) reasons.Add("SNAPSHOT_PROVENANCE_MISSING");
            }
            return new(reasons.Count == 0 ? T1ReadinessStatus.PASS : T1ReadinessStatus.FAIL,
                IntradaySnapshotStore.Fingerprint(snapshot), snapshot, Ordered(reasons));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        { return new(T1ReadinessStatus.BLOCKED, "", null, new[] { "NO_AUTHORITATIVE_SAMPLE" }); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        {
            string code = ex is InvalidDataException ? ex.Message switch
            {
                "COMPONENT_TOO_LARGE" => "SNAPSHOT_TOO_LARGE",
                "SNAPSHOT_SCHEMA_UNSUPPORTED" => "SNAPSHOT_SCHEMA_UNSUPPORTED",
                "SNAPSHOT_FINGERPRINT_MISMATCH" => "SNAPSHOT_FINGERPRINT_MISMATCH",
                _ => "SNAPSHOT_UNREADABLE_OR_INTEGRITY_INVALID"
            } : ex is UnauthorizedAccessException ? "SNAPSHOT_ACCESS_DENIED" : "SNAPSHOT_UNREADABLE_OR_INTEGRITY_INVALID";
            return new(T1ReadinessStatus.FAIL, "", null, new[] { code });
        }
    }

    private sealed record Package(string Fingerprint, T1RankingRequest Request, T1RankingCoverage Coverage,
        T1RankingCandidate[] Ranked, T1RankingExclusion[] Excluded);

    private static Package? ReadPackage(string? directory, List<string> reasons)
    {
        if (Missing(directory) || !Directory.Exists(directory)) { reasons.Add("RANKING_PACKAGE_MISSING"); return null; }
        try
        {
            using var jsonLease = OpenBounded(Path.Combine(directory!, T1RankingArtifact.SidecarFileName));
            using var csvLease = OpenBounded(Path.Combine(directory!, T1RankingArtifact.CsvFileName));
            var verdict = T1RankingArtifact.Verify(directory!);
            if (!verdict.IsValid) { reasons.Add("RANKING_PACKAGE_INTEGRITY_INVALID"); return null; }
            using var document = JsonDocument.Parse(ReadBounded(jsonLease));
            var content = document.RootElement.GetProperty("Content");
            return new(verdict.ContentFingerprint, content.GetProperty("Request").Deserialize<T1RankingRequest>(Json)!,
                content.GetProperty("Coverage").Deserialize<T1RankingCoverage>(Json)!,
                content.GetProperty("Shortlist").Deserialize<T1RankingCandidate[]>(Json)!,
                content.GetProperty("Excluded").Deserialize<T1RankingExclusion[]>(Json)!);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException)
        { reasons.Add("RANKING_PACKAGE_UNREADABLE"); return null; }
    }

    private static string[] Bind(IntradaySnapshot source, string fingerprint, Package package)
    {
        var reasons = new List<string>();
        void Check(bool valid, string code) { if (!valid) reasons.Add("BINDING_" + code); }
        var request = package.Request;
        Check(request.RunId == source.Scan.RunId, "RUN_ID_MISMATCH");
        Check(request.SnapshotFingerprint == fingerprint, "SNAPSHOT_FINGERPRINT_MISMATCH");
        Check(request.TradingDate == source.TradingDate, "DATE_MISMATCH");
        Check(request.Cutoff == source.RequestedAsOf, "CUTOFF_MISMATCH");
        Check(request.CapturedCandidateCount == source.Symbols.Count && package.Coverage.CapturedCandidates == source.Symbols.Count, "CAPTURED_COUNT_MISMATCH");
        // Source parameter identity is transitively bound by the exact snapshot hash; it is not the T1 model's parameter hash.
        var captured = source.Symbols.ToDictionary(s => s.Symbol, StringComparer.Ordinal);
        foreach (var row in package.Ranked)
        {
            if (!captured.TryGetValue(row.Symbol, out var original)) { reasons.Add("BINDING_RANKED_OUTSIDE_POOL"); continue; }
            var e = row.Explanation;
            Check(e is not null && e.Strategy == source.Scan.Strategy && e.StrategyVersion == source.Scan.StrategyVersion, "STRATEGY_MISMATCH");
            Check(e is not null && row.Name == original.Name && e.OriginalRank == original.Rank && e.OriginalScore == original.Score, "ORIGINAL_ATTRIBUTION_MISMATCH");
            Check(e is not null && e.SnapshotFingerprint == fingerprint, "EXPLANATION_FINGERPRINT_MISMATCH");
        }
        foreach (var row in package.Excluded)
        {
            if (!captured.TryGetValue(row.Symbol, out var original)) { reasons.Add("BINDING_EXCLUDED_OUTSIDE_POOL"); continue; }
            Check(row.Strategy == source.Scan.Strategy && row.StrategyVersion == source.Scan.StrategyVersion, "STRATEGY_MISMATCH");
            Check(row.Name == original.Name && row.OriginalRank == original.Rank, "ORIGINAL_ATTRIBUTION_MISMATCH");
            // Exclusion rows have no original score field. Preserve its source value in the complete captured pool.
        }
        return Ordered(reasons);
    }

    public static T1EvidenceSample Evaluate(T1EvidenceRequest input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!Enum.IsDefined(input.Origin)) throw new ArgumentException("Unsupported evidence origin", nameof(input));
        var verified = VerifySnapshot(input.SnapshotPath);
        var reasons = new List<string>(verified.Reasons);
        var package = ReadPackage(input.PackageDirectory, reasons);
        bool validSource = verified.Structure == T1ReadinessStatus.PASS;
        var source = validSource ? verified.Snapshot : null;
        var binding = T1ReadinessStatus.BLOCKED;
        if (source is not null && package is not null)
        {
            var failures = Bind(source, verified.Fingerprint, package);
            reasons.AddRange(failures);
            binding = failures.Length == 0 ? T1ReadinessStatus.PASS : T1ReadinessStatus.FAIL;
        }
        var classification = source is null ? T1EvidenceClass.UNVERIFIED : T1EvidenceClass.RETROSPECTIVE_DIAGNOSTIC;
        if (verified.Structure == T1ReadinessStatus.BLOCKED && input.Outcomes?.Any(e => e?.Daily is not null) == true)
            classification = T1EvidenceClass.DAILY_ONLY;
        if (source is not null)
        {
            if (source.Scan.ScanCompletedAtUtc > source.RequestedAsOf || source.CapturedAtUtc > source.RequestedAsOf)
                reasons.Add("ACQUISITION_OR_SCAN_AFTER_CUTOFF");
            foreach (var symbol in source.Symbols)
            foreach (var row in symbol.Minutes.Rows)
            {
                var quality = IntradayCutoff.Validate(row, source.TradingDate, source.RequestedAsOf, new(TimeSpan.FromDays(1)));
                if (quality is IntradayQuality.FutureData or IntradayQuality.InconsistentEvidence or IntradayQuality.WrongTradingDate)
                { reasons.Add("OBSERVATION_TIME_INVALID"); classification = T1EvidenceClass.UNVERIFIED; }
                if (row.AvailableAtUtc is null || row.TransportReceivedAtUtc is null || Missing(row.Provider) || row.Provenance is null)
                    reasons.Add("ORIGINAL_RECEIPT_OR_AVAILABILITY_MISSING");
                if (row.ExchangeEventTime is null || row.AvailableAtUtc is null || row.ObservedAtUtc == default)
                { reasons.Add("REQUIRED_OBSERVATION_TIMESTAMPS_MISSING"); classification = T1EvidenceClass.UNVERIFIED; }
                if (row.TransportReceivedAtUtc > source.RequestedAsOf) reasons.Add("TRANSPORT_RECEIVED_AFTER_CUTOFF");
                if (!row.EventTimeSemanticsVerified || Missing(row.Interval) || row.Interval.Contains("unverified", StringComparison.OrdinalIgnoreCase))
                    reasons.Add("INTERVAL_CLOSURE_UNVERIFIED");
            }
            var features = IntradayFeatureAnalysis.Analyze(source, verified.Fingerprint, source.RequestedAsOf);
            if (features.Any(f => f.Return5m.Value is null || f.Return15m.Value is null || f.PricePersistence.Value is null || f.PullbackFromObservedHigh.Value is null))
                reasons.Add("REQUIRED_FEATURES_MISSING");
            if (source.Calendar != SessionEvidence.TradingDay) reasons.Add("DECISION_SESSION_UNVERIFIED");
        }
        if (binding == T1ReadinessStatus.FAIL) classification = T1EvidenceClass.UNVERIFIED;
        if (input.Origin == T1EvidenceOrigin.TEST_FIXTURE) reasons.Add("TEST_FIXTURE_NOT_PRODUCTION");
        reasons.AddRange(new[] { "PIT_EVIDENCE_UNVERIFIED", "INDEPENDENT_SOURCE_AUTHENTICATION_MISSING",
            "RANKING_COMPLETION_TIME_NOT_RETAINED", "NO_EXECUTION_EVIDENCE", "LIMIT_RESTRICTION_UNVERIFIED" });
        var calendar = source is null ? CalendarMissing() : MapNextTradingDay(source.TradingDate, input.Calendar);
        reasons.AddRange(calendar.Reasons);
        var captured = source?.Symbols.OrderBy(s => s.Rank).ThenBy(s => s.Symbol, StringComparer.Ordinal)
            .Select(s => new T1CapturedIdentity(s.Symbol, s.Name, s.Rank, s.Score)).ToArray() ?? Array.Empty<T1CapturedIdentity>();
        var outcomes = new List<T1OutcomeCheck>();
        var envelopes = input.Outcomes ?? Array.Empty<T1OutcomeEnvelope>();
        if (envelopes.Count > 100 || envelopes.Any(e => e is null)) throw new ArgumentException("Invalid bounded outcome input", nameof(input));
        if (validSource && envelopes.Any(e => !captured.Any(c => c.Symbol == e.Symbol))) reasons.Add("OUTCOME_OUTSIDE_CAPTURED_POOL");
        foreach (var candidate in captured)
        {
            var matches = envelopes.Where(e => e.Symbol == candidate.Symbol).ToArray();
            outcomes.Add(matches.Length > 1
                ? new(candidate.Symbol, T1OutcomeSourceState.NOT_VERIFIED, T1ReadinessStatus.FAIL, T1ReadinessStatus.FAIL,
                    T1ReadinessStatus.FAIL, T1PriceBasisState.UNKNOWN_BASIS, new[] { "DUPLICATE_OUTCOME_ENVELOPE" })
                : CheckOutcome(candidate.Symbol, source!.TradingDate, source.RequestedAsOf, calendar, matches.SingleOrDefault(), input.DecisionBasis));
        }
        bool failed = verified.Structure == T1ReadinessStatus.FAIL || binding == T1ReadinessStatus.FAIL
            || calendar.Status == T1ReadinessStatus.FAIL || outcomes.Any(o => o.Status == T1ReadinessStatus.FAIL)
            || reasons.Contains("OUTCOME_OUTSIDE_CAPTURED_POOL")
            || (package is null && reasons.Any(r => r is "RANKING_PACKAGE_INTEGRITY_INVALID" or "RANKING_PACKAGE_UNREADABLE"));
        bool packageFailed = package is null && reasons.Any(r => r is "RANKING_PACKAGE_INTEGRITY_INVALID" or "RANKING_PACKAGE_UNREADABLE");
        return new(PolicyVersion, source?.Scan.RunId, source?.TradingDate, source?.Scan.Strategy ?? "",
            verified.Fingerprint, package?.Fingerprint ?? "", input.Origin, verified.Structure,
            package is not null ? T1ReadinessStatus.PASS : packageFailed ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED,
            binding, validSource && binding == T1ReadinessStatus.PASS ? T1ReadinessStatus.PASS : T1ReadinessStatus.BLOCKED,
            binding == T1ReadinessStatus.PASS && package!.Coverage.EligibleCandidates == package.Coverage.ActualShortlistSize
                ? T1ReadinessStatus.PASS : T1ReadinessStatus.NOT_VERIFIED,
            classification, T1ReadinessStatus.NOT_VERIFIED, calendar, T1ReadinessStatus.NOT_VERIFIED,
            failed ? T1ReadinessStatus.FAIL : T1ReadinessStatus.BLOCKED, captured,
            binding == T1ReadinessStatus.PASS ? package!.Coverage : null, outcomes, Ordered(reasons),
            input.DecisionBasis is null ? "" : DeclarationFingerprint(input.DecisionBasis));
    }
}
