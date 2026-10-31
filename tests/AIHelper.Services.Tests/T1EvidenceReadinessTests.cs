using System.IO;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

[Collection("T1ArtifactWrites")]
public sealed class T1EvidenceReadinessTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 10, 30, 6, 45, 0, TimeSpan.Zero);
    private static readonly DateOnly Day = new(2026, 10, 30), Monday = new(2026, 11, 2);
    private static readonly MarketDataMetadata Meta = new("TEST_FIXTURE", false, false, false, DataSourceKind.EastMoney);

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "AIHelper-T1Evidence", Guid.NewGuid().ToString("N"));
        public IntradaySnapshot Snapshot { get; }
        public string SnapshotPath { get; }
        public T1RankingResult Ranking { get; }
        public T1RankingArtifactPaths Package { get; }
        public Fixture()
        {
            var symbols = Enumerable.Range(1, 5).Select(i =>
            {
                string code = $"60000{i}";
                var rows = i == 5 ? Array.Empty<MinuteObservation>() : Enumerable.Range(0, 16).Select(n =>
                {
                    var t = Cutoff.AddMinutes(n - 15);
                    return new MinuteObservation(code, t, t, t, n == 15 ? 10.2m : 10m,
                        1, 10, "shares", "CNY", Meta, "one minute ending at source timestamp", Cutoff, "TEST_FIXTURE", true);
                }).ToArray();
                return new SnapshotSymbol(code, code, i, 50 - i, "fixture", null, null, Cutoff, Meta,
                    IntradayQuality.MissingEventTime, new(rows, IntradayQuality.Partial, "fixture", Meta), IntradayQuality.Partial);
            }).ToArray();
            Snapshot = new(1, new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "V2", "v2", "frozen-fixture-parameters", 5,
                Cutoff.AddMinutes(-20), Cutoff.AddMinutes(-1)), Day, Cutoff.AddMinutes(-1), Cutoff, Cutoff,
                SessionEvidence.TradingDay, Meta, symbols, false);
            SnapshotPath = new IntradaySnapshotStore(Root).Save(Snapshot);
            Ranking = T1PriceOnlyRanking.Rank(Snapshot, IntradayFeatureAnalysis.Analyze(Snapshot,
                IntradaySnapshotStore.Fingerprint(Snapshot), Cutoff), 2);
            Package = T1RankingArtifact.Write(Ranking, Path.Combine(Root, "packages"), CancellationToken.None);
        }
        public void ReplaceSnapshot(IntradaySnapshot snapshot) => File.WriteAllBytes(SnapshotPath,
            JsonSerializer.SerializeToUtf8Bytes(new SnapshotEnvelope(1, IntradaySnapshotStore.Fingerprint(snapshot), snapshot)));
        public T1EvidenceRequest Request(T1RankingResult? ranking = null) => new(SnapshotPath,
            ranking is null ? Package.PackageDirectory : T1RankingArtifact.Write(ranking, Path.Combine(Root, "packages"), CancellationToken.None).PackageDirectory,
            Origin: T1EvidenceOrigin.TEST_FIXTURE);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    private static T1CalendarEvidence Calendar(DateOnly? end = null)
    {
        var target = end ?? Monday;
        var rows = Enumerable.Range(0, target.DayNumber - Day.DayNumber + 1)
            .Select(i => new HistoricalCalendarDay(Day.AddDays(i), i == 0 || Day.AddDays(i) == target,
                i == 0 ? Day.AddDays(-1) : Day, "TEST_FIXTURE")).ToArray();
        var coverage = new HistoricalCoverageEvidence("trade_cal", "TEST_FIXTURE", new(Day, target, Exchange: "SSE"),
            null, false, null, "fixture date range", 1, new[] { new HistoricalCoverageChunk(Day, target, rows.Length) },
            rows.Length, false, 0, 0, 0, 0, 0, rows.Length, rows.Length, 0, HistoricalCoverageAcquisitionStatus.Full, "exact natural date set");
        return new("SSE", new(rows, new[] { coverage }));
    }
    private static T1PriceBasis Basis(HistoricalPriceAdjustmentMode mode = HistoricalPriceAdjustmentMode.Raw) =>
        new(mode, "600001.SH", "CNY", "per-share", "TEST_FIXTURE/basis");
    private static T1OutcomeEnvelope Outcome() => new("600001", "SSE", "600001.SH", Day, Monday, "TEST_FIXTURE",
        new DateTimeOffset(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), T1OutcomeSourceState.AVAILABLE,
        HistoricalPriceAdjustmentMode.Raw, T1OutcomeSession.OPEN,
        new("600001", "600001.SH", Monday, 10, 11, 9, 10.5, 10, .5, 5, 100, 1000, "TEST_FIXTURE", HistoricalPriceAdjustmentMode.Raw),
        Basis: Basis(), CorporateAction: new(Day, Monday, "TEST_FIXTURE/action", new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero), true),
        SourceReference: "TEST_FIXTURE/daily");

    [Fact]
    public void T01_T12_T13_T16_T17_T20_T21_T39_ValidTruncatedPairPreservesCompletePoolAndIsReadOnly()
    {
        using var f = new Fixture();
        byte[] source = File.ReadAllBytes(f.SnapshotPath), csv = File.ReadAllBytes(f.Package.CsvPath), json = File.ReadAllBytes(f.Package.SidecarPath);
        var verified = T1EvidenceReadiness.VerifySnapshot(f.SnapshotPath);
        Assert.Equal(T1ReadinessStatus.PASS, verified.Structure);
        Assert.Equal(IntradaySnapshotStore.Fingerprint(f.Snapshot), verified.Fingerprint);
        Assert.Equal(f.Snapshot.Symbols[0].Minutes.Rows[0], verified.Snapshot!.Symbols[0].Minutes.Rows[0]);
        var sample = T1EvidenceReadiness.Evaluate(f.Request());
        Assert.Equal(T1ReadinessStatus.PASS, sample.RankingIntegrity);
        Assert.Equal(T1ReadinessStatus.PASS, sample.Binding);
        Assert.Equal(T1ReadinessStatus.PASS, sample.CapturedPool);
        Assert.Equal(new T1RankingCoverage(5, 4, 1, 2, 2), sample.Coverage);
        Assert.Equal(5, sample.Captured.Count);
        Assert.Equal(f.Snapshot.Symbols.Select(s => s.Score), sample.Captured.Select(s => s.OriginalScore));
        Assert.Single(f.Ranking.Excluded); // two omitted eligible symbols are not invented exclusions
        Assert.Equal(T1ReadinessStatus.NOT_VERIFIED, sample.FullEligibleOrdering);
        Assert.Equal(T1EvidenceClass.RETROSPECTIVE_DIAGNOSTIC, sample.EvidenceClass);
        Assert.Equal(T1ReadinessStatus.BLOCKED, sample.Overall);
        var claimed = f.Snapshot with { Certified = true };
        f.ReplaceSnapshot(claimed);
        Assert.NotEqual(T1EvidenceClass.LIVE_RECORDED_PIT, T1EvidenceReadiness.Evaluate(f.Request()).EvidenceClass);
        File.WriteAllBytes(f.SnapshotPath, source);
        var report = T1EvidenceReadiness.EvaluateCoverage(new[] { f.Request() });
        Assert.Equal(0, report.AuthenticatedProductionSamples); Assert.Equal(1, report.FixtureSamples);
        Assert.Equal(2, report.TruncatedEligibleCandidates);
        Assert.Equal(source, File.ReadAllBytes(f.SnapshotPath)); Assert.Equal(csv, File.ReadAllBytes(f.Package.CsvPath));
        Assert.Equal(json, File.ReadAllBytes(f.Package.SidecarPath));
    }

    [Theory]
    [InlineData("corrupt")] [InlineData("schema")] [InlineData("duplicate")] [InlineData("rank")]
    [InlineData("date")] [InlineData("time")] [InlineData("oversized")] [InlineData("null-minutes")]
    [InlineData("fingerprint")] [InlineData("provenance")] [InlineData("full-market")]
    public void T02_T03_T04_InvalidSnapshotFailsClosed(string kind)
    {
        using var f = new Fixture();
        var s = f.Snapshot;
        switch (kind)
        {
            case "corrupt": File.WriteAllText(f.SnapshotPath, "{broken"); break;
            case "schema": f.ReplaceSnapshot(s with { SchemaVersion = 9 }); break;
            case "duplicate": f.ReplaceSnapshot(s with { Symbols = new[] { s.Symbols[0], s.Symbols[0] } }); break;
            case "rank": f.ReplaceSnapshot(s with { Symbols = new[] { s.Symbols[0] with { Rank = 0 } } }); break;
            case "date": f.ReplaceSnapshot(s with { TradingDate = Day.AddDays(-1) }); break;
            case "time": f.ReplaceSnapshot(s with { CapturedAtUtc = Cutoff.AddDays(-1) }); break;
            case "oversized": using (var stream = File.OpenWrite(f.SnapshotPath)) stream.SetLength(IntradaySnapshotStore.MaximumBytes + 1); break;
            case "null-minutes": f.ReplaceSnapshot(s with { Symbols = new[] { s.Symbols[0] with { Minutes = null! } } }); break;
            case "fingerprint": File.WriteAllBytes(f.SnapshotPath, JsonSerializer.SerializeToUtf8Bytes(new SnapshotEnvelope(1, new string('0', 64), s))); break;
            case "provenance": f.ReplaceSnapshot(s with { CalendarProvenance = null! }); break;
            case "full-market": f.ReplaceSnapshot(s with { Coverage = "full market" }); break;
        }
        Assert.Equal(T1ReadinessStatus.FAIL, T1EvidenceReadiness.VerifySnapshot(f.SnapshotPath).Structure);
        Assert.Equal(T1ReadinessStatus.FAIL, T1EvidenceReadiness.Evaluate(f.Request()).Overall);
    }

    [Theory]
    [InlineData("run", "BINDING_RUN_ID_MISMATCH")]
    [InlineData("fingerprint", "BINDING_SNAPSHOT_FINGERPRINT_MISMATCH")]
    [InlineData("cutoff", "BINDING_CUTOFF_MISMATCH")]
    [InlineData("strategy", "BINDING_STRATEGY_MISMATCH")]
    [InlineData("version", "BINDING_STRATEGY_MISMATCH")]
    [InlineData("score", "BINDING_ORIGINAL_ATTRIBUTION_MISMATCH")]
    [InlineData("rank", "BINDING_ORIGINAL_ATTRIBUTION_MISMATCH")]
    [InlineData("ranked", "BINDING_RANKED_OUTSIDE_POOL")]
    [InlineData("excluded", "BINDING_EXCLUDED_OUTSIDE_POOL")]
    public void T05_T06_T07_T08_T09_T14_T15_IndependentPackageCannotBindToForeignEvidence(string kind, string expected)
    {
        using var f = new Fixture();
        var r = f.Ranking;
        var first = r.Shortlist[0];
        r = kind switch
        {
            "run" => r with { Request = r.Request with { RunId = Guid.Parse("22222222-2222-2222-2222-222222222222") } },
            "fingerprint" => r with { Request = r.Request with { SnapshotFingerprint = new string('F', 64) } },
            "cutoff" => r with { Request = r.Request with { Cutoff = Cutoff.AddSeconds(-1) } },
            "excluded" => r with { Excluded = new[] { r.Excluded[0] with { Symbol = "600999" } } },
            _ => r with { Shortlist = new[] { first with
                {
                    Symbol = kind == "ranked" ? "600999" : first.Symbol,
                    Explanation = first.Explanation with
                    {
                        Strategy = kind == "strategy" ? "V3" : first.Explanation.Strategy,
                        StrategyVersion = kind == "version" ? "v3" : first.Explanation.StrategyVersion,
                        OriginalScore = kind == "score" ? 999 : first.Explanation.OriginalScore,
                        OriginalRank = kind == "rank" ? 99 : first.Explanation.OriginalRank
                    }
                }, r.Shortlist[1] } }
        };
        var input = f.Request(r);
        Assert.True(T1RankingArtifact.Verify(input.PackageDirectory!).IsValid); // integrity alone is insufficient
        var result = T1EvidenceReadiness.Evaluate(input);
        Assert.Equal(T1ReadinessStatus.FAIL, result.Binding); Assert.Contains(expected, result.Reasons);
    }

    [Theory]
    [InlineData("csv")] [InlineData("json")] [InlineData("legacy")]
    public void T10_T11_PackageIntegrityCannotBeBypassed(string kind)
    {
        using var f = new Fixture();
        if (kind == "csv") File.AppendAllText(f.Package.CsvPath, "tamper");
        if (kind == "json") File.WriteAllText(f.Package.SidecarPath, "{}");
        var input = kind == "legacy" ? f.Request() with { PackageDirectory = f.Root } : f.Request();
        Assert.Equal(T1ReadinessStatus.FAIL, T1EvidenceReadiness.Evaluate(input).RankingIntegrity);
    }

    [Fact]
    public void T16_T35_T36_T38_NoSourceOrConfigurationIsBlockedWithoutInventedMetrics()
    {
        using var f = new Fixture();
        var absent = f.Request() with { SnapshotPath = Path.Combine(f.Root, "missing", "source.json") };
        var sample = T1EvidenceReadiness.Evaluate(absent);
        Assert.Equal(T1ReadinessStatus.PASS, sample.RankingIntegrity);
        Assert.Equal(T1ReadinessStatus.BLOCKED, sample.CapturedPool);
        Assert.Empty(sample.Captured); Assert.Null(sample.Coverage);
        var report = T1EvidenceReadiness.EvaluateCoverage(new[] { new T1EvidenceRequest(null, null) });
        Assert.Equal("NO_SAMPLE", report.SampleState); Assert.Equal("NOT_APPLICABLE", report.FailureRate);
        Assert.Equal(T1ReadinessStatus.BLOCKED, report.Overall); Assert.Equal(0, report.VerifiedSnapshots);
        Assert.Contains("NO_AUTHORITATIVE_SAMPLE", report.Samples[0].Reasons);
        using var document = JsonDocument.Parse(T1EvidenceReadiness.CanonicalReport(report));
        Assert.DoesNotContain(document.RootElement.EnumerateObject(), p => p.Name.Contains("Return") || p.Name.Contains("Profit") || p.Name.Contains("WinRate"));
        Assert.Equal(T1OutcomeSourceState.CREDENTIAL_REQUIRED, T1EvidenceReadiness.DescribeSource(false, true, null));
        Assert.Equal(T1OutcomeSourceState.SOURCE_NOT_CONFIGURED, T1EvidenceReadiness.DescribeSource(false, false, null));
        Assert.Equal(T1OutcomeSourceState.NOT_VERIFIED, T1EvidenceReadiness.DescribeSource(true, true, null));
    }

    [Theory]
    [InlineData("availability", "OBSERVATION_TIME_INVALID")]
    [InlineData("interval", "INTERVAL_CLOSURE_UNVERIFIED")]
    public void T18_T19_TemporalEvidenceDoesNotGetPromoted(string kind, string reason)
    {
        using var f = new Fixture();
        var symbol = f.Snapshot.Symbols[0]; var row = symbol.Minutes.Rows[^1];
        var replacement = kind == "availability" ? row with { AvailableAtUtc = Cutoff.AddSeconds(1) }
            : row with { EventTimeSemanticsVerified = false, Interval = "source-labelled minute; interval closure unverified" };
        var changed = f.Snapshot with { Symbols = new[] { symbol with { Minutes = symbol.Minutes with { Rows = symbol.Minutes.Rows.SkipLast(1).Append(replacement).ToArray() } } }
            .Concat(f.Snapshot.Symbols.Skip(1)).ToArray() };
        f.ReplaceSnapshot(changed);
        var ranking = T1PriceOnlyRanking.Rank(changed, IntradayFeatureAnalysis.Analyze(changed,
            IntradaySnapshotStore.Fingerprint(changed), Cutoff), 2);
        var sample = T1EvidenceReadiness.Evaluate(f.Request(ranking));
        Assert.Equal(T1ReadinessStatus.PASS, sample.Binding);
        Assert.Contains(reason, sample.Reasons); Assert.Equal(T1ReadinessStatus.NOT_VERIFIED, sample.PitAvailability);
        Assert.NotEqual(T1EvidenceClass.LIVE_RECORDED_PIT, sample.EvidenceClass);
    }

    [Theory]
    [InlineData(3)] [InlineData(7)]
    public void T22_T23_VerifiedCalendarMapsWeekendAndHolidayByEvidence(int gap)
    {
        var target = Day.AddDays(gap);
        var map = T1EvidenceReadiness.MapNextTradingDay(Day, Calendar(target));
        Assert.Equal(T1ReadinessStatus.PASS, map.Status); Assert.Equal(target, map.NextTradingDate);
    }

    [Theory]
    [InlineData("missing")] [InlineData("partial")] [InlineData("duplicate")] [InlineData("contradictory")]
    [InlineData("exchange")] [InlineData("chain")] [InlineData("source")] [InlineData("coverage")]
    public void T24_T25_IncompleteOrContradictoryCalendarFailsClosed(string kind)
    {
        var c = Calendar(); var rows = c.Acquisition.Data;
        c = kind switch
        {
            "missing" => c with { Acquisition = c.Acquisition with { Data = rows.Where(r => r.TradingDate != Day.AddDays(1)).ToArray() } },
            "partial" => c with { Acquisition = c.Acquisition with { CoverageEvidence = new[] { c.Acquisition.CoverageEvidence[0] with { CoverageStatus = HistoricalCoverageAcquisitionStatus.Partial } } } },
            "duplicate" => c with { Acquisition = c.Acquisition with { Data = rows.Append(rows[0]).ToArray() } },
            "contradictory" => c with { Acquisition = c.Acquisition with { Data = rows.Append(rows[0] with { IsOpen = false }).ToArray() } },
            "exchange" => c with { Exchange = "UNKNOWN" },
            "chain" => c with { Acquisition = c.Acquisition with { Data = rows.Select(r => r.TradingDate == Monday ? r with { PreviousOpenDate = Day.AddDays(-1) } : r).ToArray() } },
            "source" => c with { Acquisition = c.Acquisition with { Data = rows.Select(r => r.TradingDate == Monday ? r with { Source = "foreign" } : r).ToArray() } },
            _ => c with { Acquisition = new(rows) }
        };
        var map = T1EvidenceReadiness.MapNextTradingDay(Day, c);
        Assert.NotEqual(T1ReadinessStatus.PASS, map.Status); Assert.Null(map.NextTradingDate);
        Assert.Contains("CALENDAR_NOT_VERIFIED", map.Reasons);
    }

    [Fact]
    public void T26_T27_T32_OutcomeDataDoesNotChangeDecisionInputsOrMoveTargetDate()
    {
        using var f = new Fixture();
        var input = f.Request() with { Calendar = Calendar(), DecisionBasis = Basis(), Outcomes = new[] { Outcome() with { Daily = null } } };
        var before = T1EvidenceReadiness.Evaluate(input);
        Assert.Equal(Monday, before.Calendar.NextTradingDate);
        Assert.Contains("MISSING_TARGET_DAILY_BAR", before.Outcomes[0].Reasons);
        Assert.DoesNotContain("SUSPENDED", before.Outcomes[0].Reasons);
        var foreign = Outcome() with { Daily = Outcome().Daily! with { TradingDate = Monday.AddDays(1), Close = 999 } };
        var after = T1EvidenceReadiness.Evaluate(input with { Outcomes = new[] { foreign } });
        Assert.Equal(Monday, after.Calendar.NextTradingDate);
        Assert.Contains("OUTCOME_TARGET_DATE_MISMATCH", after.Outcomes[0].Reasons);
        Assert.Equal(before.SnapshotFingerprint, after.SnapshotFingerprint); Assert.Equal(before.PackageFingerprint, after.PackageFingerprint);
        Assert.Equal(before.Captured, after.Captured); Assert.Equal(before.Binding, after.Binding);
        Assert.Equal(before.EvidenceClass, after.EvidenceClass);
    }

    [Theory]
    [InlineData("raw", T1PriceBasisState.ALIGNED_RAW)]
    [InlineData("mismatch", T1PriceBasisState.INCOMPATIBLE_BASIS)]
    [InlineData("unknown", T1PriceBasisState.UNKNOWN_BASIS)]
    [InlineData("action", T1PriceBasisState.CORPORATE_ACTION_UNVERIFIED)]
    public void T28_T29_T30_T31_PriceBasisIsExplicit(string kind, T1PriceBasisState expected)
    {
        using var f = new Fixture();
        var o = Outcome();
        var sample = T1EvidenceReadiness.Evaluate(f.Request() with
        {
            Calendar = Calendar(), DecisionBasis = kind == "unknown" ? null : Basis(),
            Outcomes = new[] { o with { Basis = kind == "mismatch" ? Basis(HistoricalPriceAdjustmentMode.ForwardAdjusted) : o.Basis,
                CorporateAction = kind == "action" ? null : o.CorporateAction } }
        });
        Assert.Equal(expected, sample.Outcomes[0].PriceBasis);
        Assert.Equal(T1ReadinessStatus.BLOCKED, sample.Outcomes[0].Status);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void T33_SuspensionRequiresSupportingExactDateAndInstrumentEvidence(bool supported)
    {
        using var f = new Fixture();
        var o = Outcome() with { Daily = null, Session = T1OutcomeSession.SUSPENDED,
            Suspension = supported ? new("600001", "600001.SH", Monday, "S", null, "fixture-source") : null };
        var sample = T1EvidenceReadiness.Evaluate(f.Request() with { Calendar = Calendar(), Outcomes = new[] { o } });
        Assert.Equal(supported, sample.Outcomes[0].Reasons.Contains("SUSPENDED"));
        if (!supported) Assert.Contains("SESSION_STATUS_UNVERIFIED", sample.Outcomes[0].Reasons);
    }

    [Theory]
    [InlineData(HistoricalSourceCapabilityStatus.Unavailable, T1OutcomeSourceState.SOURCE_ERROR)]
    [InlineData(HistoricalSourceCapabilityStatus.PermissionDenied, T1OutcomeSourceState.PERMISSION_DENIED)]
    [InlineData(HistoricalSourceCapabilityStatus.RateLimited, T1OutcomeSourceState.RATE_LIMITED)]
    public void T34_SourceStatesKeepActualFailureMeaning(HistoricalSourceCapabilityStatus observed, T1OutcomeSourceState expected) =>
        Assert.Equal(expected, T1EvidenceReadiness.DescribeSource(true, true, observed));

    [Fact]
    public void T37_ReportIsDeterministicAndKeepsAllMissingCandidates()
    {
        using var f = new Fixture();
        var input = f.Request() with { Calendar = Calendar(), DecisionBasis = Basis(), Outcomes = new[] { Outcome() } };
        var first = T1EvidenceReadiness.EvaluateCoverage(new[] { input });
        var second = T1EvidenceReadiness.EvaluateCoverage(new[] { input });
        Assert.Equal(T1EvidenceReadiness.CanonicalReport(first), T1EvidenceReadiness.CanonicalReport(second));
        Assert.Equal(5, first.CapturedCandidates); Assert.Equal(5, first.Samples[0].Outcomes.Count);
        Assert.Equal(1, first.AvailableOutcomeEnvelopes); Assert.Equal(4, first.MissingOutcomeReasons["SOURCE_UNAVAILABLE"]);
        Assert.Equal(5, first.MissingOutcomeReasons["NO_EXECUTION_EVIDENCE"]);
        Assert.Equal(T1EvidenceReadiness.PolicyVersion, first.PolicyVersion);
        Assert.DoesNotContain(f.Root, T1EvidenceReadiness.CanonicalReport(first));
        Assert.Throws<ArgumentException>(() => T1EvidenceReadiness.EvaluateCoverage(new[] { input, input }));
    }

    [Theory]
    [InlineData("identity")] [InlineData("exchange")] [InlineData("unit")] [InlineData("publication")]
    [InlineData("ohlc")] [InlineData("nan")] [InlineData("volume")] [InlineData("provenance")]
    public void OutcomeEnvelopeRejectsInvalidFieldsWithoutChangingRanking(string kind)
    {
        using var f = new Fixture();
        var o = Outcome();
        o = kind switch
        {
            "identity" => o with { SourceInstrumentId = "600001.SZ" },
            "exchange" => o with { Exchange = "SZSE" },
            "unit" => o with { Basis = o.Basis! with { PriceUnit = "milli-CNY" } },
            "publication" => o with { RetrievedAtUtc = Cutoff },
            "ohlc" => o with { Daily = o.Daily! with { High = 8 } },
            "nan" => o with { Daily = o.Daily! with { Open = double.NaN } },
            "volume" => o with { Daily = o.Daily! with { Volume = -1 } },
            _ => o with { SourceReference = "" }
        };
        var sample = T1EvidenceReadiness.Evaluate(f.Request() with { Calendar = Calendar(), DecisionBasis = Basis(), Outcomes = new[] { o } });
        Assert.Equal(T1ReadinessStatus.PASS, sample.Binding);
        if (kind == "unit") Assert.Equal(T1PriceBasisState.INCOMPATIBLE_BASIS, sample.Outcomes[0].PriceBasis);
        else Assert.Equal(T1ReadinessStatus.FAIL, sample.Outcomes[0].FieldValidity);
        Assert.NotEqual(T1ReadinessStatus.PASS, sample.Overall);
        Assert.NotEmpty(sample.Outcomes[0].EvidenceFingerprint);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AdjustedBasisRequiresSameContemporaneousFactor(bool future)
    {
        var basis = Basis(HistoricalPriceAdjustmentMode.ForwardAdjusted) with
        {
            FactorFingerprint = new string('A', 64), FactorAsOf = Day,
            FactorAvailableAtUtc = future ? Cutoff.AddDays(1) : Cutoff.AddMinutes(-1)
        };
        var action = Outcome().CorporateAction! with { NoAction = false, FactorFingerprint = basis.FactorFingerprint };
        Assert.Equal(future ? T1PriceBasisState.CORPORATE_ACTION_UNVERIFIED : T1PriceBasisState.ALIGNED_ADJUSTED_WITH_PROVENANCE,
            T1EvidenceReadiness.CheckPriceBasis(basis, basis, action, Day, Monday, Cutoff));
    }

    [Fact]
    public void MissingCalendarDoesNotBecomeAFalseOutcomeDateMismatch()
    {
        using var f = new Fixture();
        var sample = T1EvidenceReadiness.Evaluate(f.Request() with { Outcomes = new[] { Outcome() } });
        Assert.Equal(T1ReadinessStatus.BLOCKED, sample.Overall);
        Assert.Contains("CALENDAR_NOT_VERIFIED", sample.Outcomes[0].Reasons);
        Assert.DoesNotContain("OUTCOME_TARGET_DATE_MISMATCH", sample.Outcomes[0].Reasons);
    }

    [Fact]
    public void DailyOnlyAndEmptyInputsDoNotCreateAnIntradaySample()
    {
        var sample = T1EvidenceReadiness.Evaluate(new(null, null, Outcomes: new[] { Outcome() }));
        Assert.Equal(T1EvidenceClass.DAILY_ONLY, sample.EvidenceClass);
        Assert.Equal(T1ReadinessStatus.BLOCKED, sample.Overall); Assert.Empty(sample.Captured);
        Assert.Equal("NO_SAMPLE", T1EvidenceReadiness.EvaluateCoverage(Array.Empty<T1EvidenceRequest>()).SampleState);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DelistingNeedsEffectiveLifecycleEvidenceRatherThanBackfilledStockBasic(bool supported)
    {
        using var f = new Fixture();
        var o = Outcome() with
        {
            Session = T1OutcomeSession.DELISTED, Daily = null,
            Security = new("600001", "600001.SH", "fixture", null, "SSE", "D", Day.AddYears(-1), Monday, "fixture"),
            Lifecycle = supported ? new("600001", "fixture", HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai,
                Day.AddYears(-1), Monday, HistoricalLifecycleQuality.Complete) : null,
            LifecycleSourceReference = supported ? "fixture/exclusive-effective-date" : ""
        };
        var sample = T1EvidenceReadiness.Evaluate(f.Request() with { Calendar = Calendar(), Outcomes = new[] { o } });
        Assert.Equal(supported, sample.Outcomes[0].Reasons.Contains("DELISTED"));
        Assert.Equal(T1ReadinessStatus.BLOCKED, sample.Overall);
    }

    [Fact]
    public void EnvelopeDuplicatesAndOutsideSymbolsDoNotInflateCoverage()
    {
        using var f = new Fixture();
        var sample = T1EvidenceReadiness.Evaluate(f.Request() with { Calendar = Calendar(), Outcomes = new[] { Outcome(), Outcome() } });
        Assert.Equal(T1ReadinessStatus.FAIL, sample.Overall);
        Assert.Contains("DUPLICATE_OUTCOME_ENVELOPE", sample.Outcomes[0].Reasons);
        sample = T1EvidenceReadiness.Evaluate(f.Request() with { Outcomes = new[] { Outcome() with { Symbol = "600999" } } });
        Assert.Contains("OUTCOME_OUTSIDE_CAPTURED_POOL", sample.Reasons);
        Assert.Equal(T1ReadinessStatus.FAIL, sample.Overall);
    }

    [Theory]
    [InlineData(T1OutcomeSourceState.AVAILABLE)] [InlineData(T1OutcomeSourceState.SOURCE_NOT_CONFIGURED)]
    [InlineData(T1OutcomeSourceState.CREDENTIAL_REQUIRED)] [InlineData(T1OutcomeSourceState.PERMISSION_DENIED)]
    [InlineData(T1OutcomeSourceState.RATE_LIMITED)] [InlineData(T1OutcomeSourceState.DATA_MISSING)]
    [InlineData(T1OutcomeSourceState.PARTIAL_COVERAGE)] [InlineData(T1OutcomeSourceState.SOURCE_ERROR)]
    [InlineData(T1OutcomeSourceState.NOT_VERIFIED)]
    public void OutcomeStatesArePreservedWithoutPromotingDeclaredData(T1OutcomeSourceState state)
    {
        using var f = new Fixture();
        var result = T1EvidenceReadiness.Evaluate(f.Request() with
        {
            Calendar = Calendar(), DecisionBasis = Basis(), Outcomes = new[] { Outcome() with { State = state } }
        });
        Assert.Equal(state, result.Outcomes[0].SourceState);
        Assert.Equal(state == T1OutcomeSourceState.AVAILABLE, result.Outcomes[0].SourceAvailability == T1ReadinessStatus.PASS);
        Assert.Equal(T1ReadinessStatus.BLOCKED, result.Overall);
        if (state == T1OutcomeSourceState.PARTIAL_COVERAGE) Assert.Contains("SOURCE_PARTIAL", result.Outcomes[0].Reasons);
    }
}
