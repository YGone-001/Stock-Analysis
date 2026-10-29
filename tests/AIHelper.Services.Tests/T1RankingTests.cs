using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Threading;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>T01–T34 acceptance matrix for the T1-Rank / PriceOnlyV1 research ranking phase (T35–T38 run as existing suites/tools).</summary>
[Collection("T1ArtifactWrites")]
public sealed class T1RankingTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 29, 6, 45, 0, TimeSpan.Zero); // 14:45 China
    private static readonly DateOnly Day = new(2026, 10, 29);
    private static readonly MarketDataMetadata Meta = new("fixture", false, false, false, DataSourceKind.EastMoney);

    private static MinuteObservation Row(DateTimeOffset t, decimal price = 10, string symbol = "600001") =>
        new(symbol, t, t, t, price, 1, price, "shares", "CNY", Meta, "one minute ending at source timestamp", t, "verified-fixture", true);
    private static MinuteObservation[] Rows(decimal last, string symbol = "600001", DateTimeOffset? end = null)
    {
        var e = end ?? T;
        return Enumerable.Range(0, 16).Select(i => Row(e.AddMinutes(i - 15), i == 15 ? last : 10, symbol)).ToArray();
    }
    private static MinuteObservationResult Series(MinuteObservation[] rows) => new(rows, IntradayQuality.Partial, "fixture", Meta);
    private static IntradaySnapshot Snapshot(params (string Code, string Name, decimal Last)[] symbols) => Snapshot(T, symbols);
    private static IntradaySnapshot Snapshot(DateTimeOffset end, params (string Code, string Name, decimal Last)[] symbols) =>
        new(1, new(Guid.NewGuid(), "V2", "v2", "fixture-params", symbols.Length, end.AddMinutes(-20), end.AddMinutes(-1)),
            ChinaExchangeTime.Date(end), end, end, end, SessionEvidence.TradingDay, Meta,
            symbols.Select((s, i) => new SnapshotSymbol(s.Code, s.Name, i + 1, 50 - i, "fixture-profile", null, null, end,
                Meta, IntradayQuality.MissingEventTime, Series(Rows(s.Last, s.Code, end)), IntradayQuality.Partial)).ToArray(), false);
    private static IReadOnlyList<IntradayFeatureResult> Analyze(IntradaySnapshot snapshot, IntradayBenchmarkEvidence? benchmark = null) =>
        IntradayFeatureAnalysis.Analyze(snapshot, IntradaySnapshotStore.Fingerprint(snapshot), snapshot.RequestedAsOf, benchmark);
    private static T1RankingResult Rank(IntradaySnapshot snapshot, IReadOnlyList<IntradayFeatureResult>? features = null, int topN = 5) =>
        T1PriceOnlyRanking.Rank(snapshot, features ?? Analyze(snapshot), topN);
    private static IntradayFeatureResult Single(IntradaySnapshot snapshot, IntradayBenchmarkEvidence? benchmark = null) => Assert.Single(Analyze(snapshot, benchmark));

    [Fact]
    public void T01_ExactFormulaAndRequestIdentity()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var r = Rank(snapshot, topN: 5);
        var c = Assert.Single(r.Shortlist);
        // R5=R15=2% -> M5=100, M15=100*(2+2)/5=80; persistence 1 -> 100; pullback 0 -> 100; chase moderation 100*(3-2)/2=50.
        Assert.Equal(88.5m, c.Explanation.FinalScore);
        Assert.Equal(new T1ComponentScores(100m, 80m, 100m, 100m, 50m), c.Explanation.Components);
        Assert.Equal(new T1ComponentScores(20m, 16m, 25m, 20m, 7.5m), c.Explanation.Contributions);
        // Request identity echo (R1): model/version, fingerprint, RunId, date, cutoff, feature version, TopN, parameter fingerprint, coverage.
        Assert.Equal("T1-Rank", r.Request.RankingModel); Assert.Equal("PriceOnlyV1", r.Request.RankingVersion);
        Assert.Equal(r.Request.SnapshotFingerprint, c.Explanation.SnapshotFingerprint);
        Assert.Equal(snapshot.Scan.RunId, r.Request.RunId); Assert.Equal(Day, r.Request.TradingDate);
        Assert.Equal(snapshot.RequestedAsOf, r.Request.Cutoff);
        Assert.Equal("intraday-features-v1", r.Request.FeatureComputationVersion);
        Assert.Equal(T1PriceOnlyRanking.ParameterFingerprint, r.Request.ParameterFingerprint);
        Assert.Equal(1, r.Request.CapturedCandidateCount);
        Assert.Equal("PriceOnlyV1", r.RankingProfile); Assert.Equal("Diagnostic", r.EvidenceClass);
    }
    [Fact]
    public void T02_WeightsSumExactly100()
    {
        // Fraction weights sum to 1.00, i.e. exactly 100 percent points across the five components.
        Assert.Equal(1.00m, T1PriceOnlyRanking.WeightTable.Sum(w => w.Weight));
        Assert.Equal(100m, T1PriceOnlyRanking.WeightTable.Sum(w => w.Weight) * 100m);
        Assert.All(T1PriceOnlyRanking.WeightTable, w => Assert.True(w.Weight > 0m));
        Assert.Equal(5, T1PriceOnlyRanking.WeightTable.Length);
    }
    [Fact]
    public void T03_ScoreStaysWithinUnitIntervalForExtremeInputs()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var f = Single(snapshot);
        foreach (var (r5, r15, p, pb) in new[] { (20m, 20m, 1m, 0m), (-20m, -20m, 0m, -100m), (20m, -20m, 0m, 0m), (-20m, 20m, 1m, -100m) })
        {
            var patched = f with { Return5m = f.Return5m with { Value = r5 }, Return15m = f.Return15m with { Value = r15 },
                PricePersistence = f.PricePersistence with { Value = p }, PullbackFromObservedHigh = f.PullbackFromObservedHigh with { Value = pb } };
            var c = Assert.Single(Rank(snapshot, new[] { patched }).Shortlist);
            Assert.InRange(c.Explanation.FinalScore, 0m, 100m);
        }
        var outOfRange = f with { Return5m = f.Return5m with { Value = 20.0001m } };
        Assert.Equal(T1RankingExclusionCode.MetricOutOfRange, Assert.Single(Rank(snapshot, new[] { outOfRange }).Excluded).Code);
    }
    [Fact]
    public void T04_PercentAndFractionUnitsAreInterpretedExactly()
    {
        // Return5m = 1 means +1% -> M5 = 100*2/3, never the fraction misreading (+100% -> clamp 100).
        Assert.Equal(100m * 2m / 3m, T1PriceOnlyRanking.M5(1m));
        Assert.NotEqual(100m, T1PriceOnlyRanking.M5(1m));
        // Persistence is a fraction: 0.5 -> 50 points, not 0.5 points.
        Assert.Equal(50m, T1PriceOnlyRanking.Persistence(0.5m));
        Assert.Equal(100m * 1m / 3m, T1PriceOnlyRanking.HighProximity(-2m));
        Assert.Equal(100m, T1PriceOnlyRanking.ChaseModeration(0m));
        Assert.Equal(50m, T1PriceOnlyRanking.ChaseModeration(2m));
    }
    [Fact]
    public void T05_T06_DeterministicTieBreaksOrderBySymbolCodeNotInsertion()
    {
        var snapshot = Snapshot(("600002", "b", 10.2m), ("600001", "a", 10.2m)); // equal evidence; 600002 captured first
        var r = Rank(snapshot);
        Assert.Equal(new[] { "600001", "600002" }, r.Shortlist.Select(c => c.Symbol).ToArray());
        Assert.All(r.Shortlist, c => Assert.Equal(88.5m, c.Explanation.FinalScore));
        var repeat = Rank(snapshot);
        // Warnings arrays compare by reference under record equality; byte identity is the stronger determinism check.
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(r.Shortlist), JsonSerializer.SerializeToUtf8Bytes(repeat.Shortlist));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(r.Excluded), JsonSerializer.SerializeToUtf8Bytes(repeat.Excluded));
        Assert.Equal(r.Coverage, repeat.Coverage); Assert.Equal(r.Request, repeat.Request);
    }
    [Fact]
    public void T05b_PersistenceBreaksScoreTies()
    {
        var snapshot = Snapshot(("600001", "a", 10.2m), ("600002", "b", 10.2m));
        var features = Analyze(snapshot).Select(f => f.Symbol == "600001"
            ? f with { Return5m = f.Return5m with { Value = -2m }, Return15m = f.Return15m with { Value = 3m }, PricePersistence = f.PricePersistence with { Value = 0.4m } }
            : f with { Return5m = f.Return5m with { Value = 3m }, Return15m = f.Return15m with { Value = -2m }, PricePersistence = f.PricePersistence with { Value = 1m } }).ToArray();
        var r = Rank(snapshot, features);
        Assert.All(r.Shortlist, c => Assert.Equal(65m, c.Explanation.FinalScore)); // exact score tie: 0+20+10+20+15 vs 20+0+25+20+0
        Assert.Equal("600002", r.Shortlist[0].Symbol); // persistence 1.0 beats 0.4 although 600002 loses the code tie-break
    }
    [Fact]
    public void T05c_FifteenMinuteReturnBreaksRemainingTies()
    {
        var snapshot = Snapshot(("600001", "a", 10.2m), ("600002", "b", 10.2m));
        var features = Analyze(snapshot).Select(f => f.Symbol == "600001"
            ? f with { Return5m = f.Return5m with { Value = 3m }, Return15m = f.Return15m with { Value = -5m } }
            : f with { Return5m = f.Return5m with { Value = 3m }, Return15m = f.Return15m with { Value = -2m } }).ToArray();
        var r = Rank(snapshot, features);
        Assert.Equal("600002", r.Shortlist[0].Symbol); // identical score/persistence; 15m return -2 > -5 decides over code order
        Assert.All(r.Shortlist, c => Assert.Equal(65m, c.Explanation.FinalScore));
    }
    [Fact]
    public void T07_T08_MissingReturnReferenceExcludesWithExplicitCode()
    {
        var broken5 = Snapshot(("600001", "one", 10.2m)) with { Symbols = new[] { Symbol("600001", "one", 1, 50, Rows(10.2m).Where(r => r.ExchangeEventTime != T.AddMinutes(-5)).ToArray()) } };
        Assert.Equal(T1RankingExclusionCode.Return5mUnavailable, Assert.Single(Rank(broken5).Excluded).Code);
        var broken15 = Snapshot(("600001", "one", 10.2m)) with { Symbols = new[] { Symbol("600001", "one", 1, 50, Rows(10.2m).Where(r => r.ExchangeEventTime != T.AddMinutes(-15)).ToArray()) } };
        Assert.Equal(T1RankingExclusionCode.Return15mUnavailable, Assert.Single(Rank(broken15).Excluded).Code);
    }
    [Fact]
    public void T09_T10_MissingPersistenceOrPullbackExcludes()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var f = Single(snapshot);
        var noPersistence = f with { PricePersistence = f.PricePersistence with { Value = null, EligibleTransitions = 0 } };
        Assert.Equal(T1RankingExclusionCode.PersistenceUnavailable, Assert.Single(Rank(snapshot, new[] { noPersistence }).Excluded).Code);
        var zeroDenominator = f with { PricePersistence = f.PricePersistence with { EligibleTransitions = 0 } };
        Assert.Equal(T1RankingExclusionCode.PersistenceDenominatorNonPositive, Assert.Single(Rank(snapshot, new[] { zeroDenominator }).Excluded).Code);
        var noPullback = f with { PullbackFromObservedHigh = f.PullbackFromObservedHigh with { Value = null } };
        Assert.Equal(T1RankingExclusionCode.PullbackUnavailable, Assert.Single(Rank(snapshot, new[] { noPullback }).Excluded).Code);
    }
    [Fact]
    public void T11_T12_EndpointsBefore1430CannotBeRanked_And1430BoundaryRanks()
    {
        var before = Snapshot(T.AddMinutes(-16), ("600001", "one", 10.2m)); // endpoint 14:29 China
        var exclusion = Assert.Single(Rank(before).Excluded);
        Assert.Equal(T1RankingExclusionCode.EndpointMissing, exclusion.Code);
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var f = Single(snapshot);
        var at1430 = f with { EffectiveObservationTime = T.AddMinutes(-15) }; // 14:30 exactly
        Assert.Single(Rank(snapshot, new[] { at1430 }).Shortlist);
        Assert.Single(Rank(Snapshot(T.AddMinutes(11), ("600001", "one", 10.2m))).Shortlist); // real 14:56 endpoint, before 14:57
        var at1457 = f with { EffectiveObservationTime = T.AddMinutes(12) }; // closing auction start
        Assert.Equal(T1RankingExclusionCode.EndpointOutsideWindow, Assert.Single(Rank(snapshot, new[] { at1457 }).Excluded).Code);
        var afterCutoff = f with { EffectiveObservationTime = T.AddMinutes(1) };
        Assert.Equal(T1RankingExclusionCode.EndpointAfterCutoff, Assert.Single(Rank(snapshot, new[] { afterCutoff }).Excluded).Code);
    }
    [Fact]
    public void T13_ClosingAuctionObservationsCannotEnterRank()
    {
        // A snapshot whose only label is 14:57 (closing auction) yields no eligible afternoon rows at all.
        var auction = Snapshot(T.AddMinutes(12)) with { Symbols = new[] { Symbol("600001", "one", 1, 50, new[] { Row(T.AddMinutes(12)) }) } };
        var exclusion = Assert.Single(Rank(auction).Excluded);
        Assert.Equal(T1RankingExclusionCode.EndpointMissing, exclusion.Code);
    }
    [Fact]
    public void T14_T17_T18_IdentityMismatchesFailClosed()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var f = Single(snapshot);
        Assert.Equal(T1RankingExclusionCode.TradingDateMismatch, Assert.Single(Rank(snapshot, new[] { f with { TradingDate = Day.AddDays(-1) } }).Excluded).Code);
        Assert.Equal(T1RankingExclusionCode.RunIdMismatch,
            Assert.Single(Rank(snapshot with { Scan = snapshot.Scan with { RunId = Guid.NewGuid() } }, new[] { f }).Excluded).Code);
        // Content change without identity-field change: fingerprint fails closed after RunId/date/cutoff agree.
        Assert.Equal(T1RankingExclusionCode.FingerprintMismatch,
            Assert.Single(Rank(snapshot with { Certified = true }, new[] { f }).Excluded).Code);
        Assert.Equal(T1RankingExclusionCode.FeatureVersionUnsupported,
            Assert.Single(Rank(snapshot, new[] { f with { ComputationVersion = "intraday-features-v0" } }).Excluded).Code);
        Assert.Equal(T1RankingExclusionCode.CutoffMismatch,
            Assert.Single(Rank(snapshot, new[] { f with { RequestedAsOf = T.AddMinutes(1) } }).Excluded).Code);
    }
    [Fact]
    public void T15_PostCutoffAcquisitionExcludesEvenWhenMetricsClaimValues()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var f = Single(snapshot);
        var late = f with { SourceProvenance = f.SourceProvenance.Select(r => r with { AvailableAtUtc = T.AddMinutes(1), ObservedAtUtc = T.AddMinutes(1) }).ToArray() };
        Assert.Equal(T1RankingExclusionCode.ObservationAcquiredAfterCutoff, Assert.Single(Rank(snapshot, new[] { late }).Excluded).Code);
        var missing = f with { SourceProvenance = Array.Empty<MinuteObservation>() };
        Assert.Equal(T1RankingExclusionCode.ObservationProvenanceMissing, Assert.Single(Rank(snapshot, new[] { missing }).Excluded).Code);
    }
    [Fact]
    public void T19_MixedSourcePriceBasisIsNotSilentlyAccepted()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var f = Single(snapshot);
        var mixed = f with { SourceProvenance = f.SourceProvenance.Select((r, i) => r with { Provider = i % 2 == 0 ? "verified-fixture" : "other-unverified-basis" }).ToArray() };
        Assert.Equal(T1RankingExclusionCode.MixedSourceBasis, Assert.Single(Rank(snapshot, new[] { mixed }).Excluded).Code);
        // Real pipeline: a mixed-provider minute series already fails closed at feature level.
        var rows = Rows(10.2m); rows[10] = rows[10] with { Provider = "other-unverified-basis" };
        var mixedSnapshot = Snapshot(("600001", "one", 10.2m)) with { Symbols = new[] { Symbol("600001", "one", 1, 50, rows) } };
        Assert.Empty(Rank(mixedSnapshot).Shortlist);
    }
    [Fact]
    public void T20_AvailableRelativeStrengthIsDisclosedButNeverEntersPriceOnlyV1()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var benchmark = new IntradayBenchmarkEvidence("sh000001", Day, Series(Rows(10.1m, "sh000001")));
        var withRsWithout = Rank(snapshot); var withRs = Rank(snapshot, Analyze(snapshot, benchmark));
        Assert.Equal(withRsWithout.Shortlist.Single().Explanation.FinalScore, withRs.Shortlist.Single().Explanation.FinalScore);
        Assert.Equal(1m, Analyze(snapshot, benchmark).Single().RelativeStrength5m.Value); // RS evidence exists in features…
        Assert.Contains("冻结配置不纳入相对强度", withRs.NotIncluded.Single(n => n.Dimension == "RelativeStrength").Reason); // …but is disclosed, not scored
        Assert.Contains("未保存对齐的基准", withRsWithout.NotIncluded.Single(n => n.Dimension == "RelativeStrength").Reason);
    }
    [Fact]
    public void T21_UnknownVolumeUnitsNeverEnterScore()
    {
        var rows = Rows(10.2m).Select(r => r with { EventTimeSemanticsVerified = false, VolumeUnit = "unknown", AmountUnit = "unknown", Interval = "source label; unverified interval" }).ToArray();
        var snapshot = Snapshot(("600001", "one", 10.2m)) with { Symbols = new[] { Symbol("600001", "one", 1, 50, rows) } };
        var features = IntradayFeatureAnalysis.Analyze(snapshot, IntradaySnapshotStore.Fingerprint(snapshot), snapshot.RequestedAsOf,
            quantity: new IntradayQuantityEvidence("verified-fixture", "600001", "shares", "CNY", 1, 1, true, true, true, "unadjusted shares", "fixture", TimeSpan.FromMinutes(1), true));
        var r = Rank(snapshot, features);
        Assert.Null(features.Single().TradeVwap.Value); Assert.Null(features.Single().VolumeAcceleration.Value);
        Assert.Equal(88.5m, r.Shortlist.Single().Explanation.FinalScore);
        Assert.Contains("未经验证", r.NotIncluded.Single(n => n.Dimension == "Volume").Reason);
        Assert.Contains("证据缺失", r.NotIncluded.Single(n => n.Dimension == "Vwap").Reason);
    }
    [Fact]
    public void T22_ZeroEligibleCandidatesIsValidEmptyShortlist()
    {
        var empty = Snapshot(("600001", "one", 10.2m)) with { Symbols = new[] { Symbol("600001", "one", 1, 50, Array.Empty<MinuteObservation>()) } };
        var r = Rank(empty);
        Assert.Empty(r.Shortlist); Assert.Equal(0, r.Coverage.ActualShortlistSize); Assert.Equal(1, r.Coverage.ExcludedCandidates);
        Assert.Empty(Rank(Snapshot()).Shortlist); // zero captured candidates is also valid
        var noRows = Assert.Single(Rank(empty).Excluded);
        Assert.Equal(T1RankingExclusionCode.EndpointMissing, noRows.Code);
    }
    [Fact]
    public void T23_RanksOnlyWithinCapturedCoverage_AndTopNTruncatesWithoutChangingCoverage()
    {
        var snapshot = Snapshot(("600001", "a", 10.2m), ("600002", "b", 10m), ("600003", "c", 9.8m));
        var features = Analyze(snapshot).ToList();
        var extra = features[0] with { Symbol = "600999", Name = "uncaptured" };
        var r = Rank(snapshot, features.Append(extra).ToArray(), topN: 1);
        Assert.Equal(new[] { "600001" }, r.Shortlist.Select(c => c.Symbol).ToArray()); // 88.5 > 74.67 > 45
        Assert.Equal(3, r.Coverage.CapturedCandidates); Assert.Equal(3, r.Coverage.EligibleCandidates);
        Assert.Equal(1, r.Coverage.ActualShortlistSize); Assert.Equal(1, r.Coverage.RequestedShortlistSize);
        Assert.DoesNotContain(r.Shortlist, c => c.Symbol == "600999");
        Assert.Empty(r.Excluded);
        Assert.Throws<ArgumentException>(() => T1PriceOnlyRanking.Rank(snapshot, features, 0));
        Assert.Throws<ArgumentException>(() => T1PriceOnlyRanking.Rank(snapshot, features, 21));
    }
    [Fact]
    public void T24_OriginalStrategyRankScoreAndSnapshotBytesAreUntouched()
    {
        var snapshot = Snapshot(("600001", "one", 10.2m));
        var features = Analyze(snapshot);
        byte[] before = JsonSerializer.SerializeToUtf8Bytes(snapshot);
        byte[] featuresBefore = JsonSerializer.SerializeToUtf8Bytes(features);
        Rank(snapshot, features);
        Assert.Equal(before, JsonSerializer.SerializeToUtf8Bytes(snapshot));
        Assert.Equal(featuresBefore, JsonSerializer.SerializeToUtf8Bytes(features));
        Assert.Equal(1, snapshot.Symbols[0].Rank); Assert.Equal(50, snapshot.Symbols[0].Score);
        Assert.Equal("fixture-profile", snapshot.Symbols[0].RankingProfile);
    }
    [Fact]
    public void T25_ComponentContributionsSumToFinalScore()
    {
        var snapshot = Snapshot(("600001", "a", 10.2m), ("600002", "b", 10m), ("600003", "c", 9.8m));
        foreach (var c in Rank(snapshot).Shortlist)
        {
            var e = c.Explanation;
            Assert.Equal(e.Contributions.M5 + e.Contributions.M15 + e.Contributions.Persistence + e.Contributions.HighProximity + e.Contributions.ChaseModeration, e.FinalScore);
            Assert.Equal(0.20m * e.Components.M5, e.Contributions.M5);
            Assert.Equal(0.25m * e.Components.Persistence, e.Contributions.Persistence);
            Assert.Equal(0.15m * e.Components.ChaseModeration, e.Contributions.ChaseModeration);
        }
    }
    [Fact]
    public void T26_T27_RepeatedRankingIsIdenticalAndExclusionsDeterministic()
    {
        var broken = Snapshot(("600001", "a", 10.2m), ("600002", "b", 10.2m)) with
        {
            Symbols = new[] { Symbol("600001", "a", 1, 50, Rows(10.2m, "600001")), Symbol("600002", "b", 2, 49, Array.Empty<MinuteObservation>()) }
        };
        var first = Rank(broken); var second = Rank(broken);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(first.Shortlist), JsonSerializer.SerializeToUtf8Bytes(second.Shortlist));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(first.Excluded), JsonSerializer.SerializeToUtf8Bytes(second.Excluded));
        Assert.Equal(first.Coverage, second.Coverage); Assert.Equal(first.Request, second.Request);
        var exclusion = Assert.Single(first.Excluded);
        Assert.Equal("600002", exclusion.Symbol); Assert.Equal(T1RankingExclusionCode.EndpointMissing, exclusion.Code);
        Assert.False(string.IsNullOrWhiteSpace(exclusion.Reason));
        Assert.Equal(1, first.Shortlist.Single().T1Rank);
    }
    [Fact]
    public void T30_RealSnapshotFeatureRankWorkflowReachesShortlistAndExport()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIHelper-T1Rank", Guid.NewGuid().ToString("N"));
        string exportDir = Path.Combine(dir, "out");
        try
        {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
            {
                var clock = new T1RankingTests.Clock(); var provider = new T1RankingTests.Provider();
                vm.SnapshotClock = clock;
                vm.SnapshotCapture = new IntradaySnapshotCapture(new MinuteObservationService(provider, clock), new QuoteService(provider),
                    new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[]
                {
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600001", Name = "first", Rank = 3, TotalScore = 77.5, Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } },
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600002", Name = "second", Rank = 4, TotalScore = 60, Features = new SparrowRankingFeatures { Code = "600002", Name = "second" } },
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600003", Name = "third", Rank = 5, TotalScore = 40, Features = new SparrowRankingFeatures { Code = "600003", Name = "third" } }
                });
                T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null));
                Assert.Equal("Completed", vm.ScanOutcome);
                string path = vm.LastSnapshotPath!; byte[] original = File.ReadAllBytes(path);
                T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                Assert.True(vm.RunT1RankingCommand.CanExecute(null));
                T1RankingTests.Pump(vm.RunT1RankingCommand.ExecuteAsync(null));
                Assert.Equal(3, vm.T1RankRows.Count);
                var first = vm.T1RankRows[0]; var second = vm.T1RankRows[1]; var excluded = vm.T1RankRows[2];
                Assert.Equal(1, first.T1Rank); Assert.Equal("600001", first.Code); Assert.Equal("88.5000", first.Score);
                Assert.Equal("入选", first.Status); Assert.Equal(3, first.OriginalRank);
                Assert.Equal(2, second.T1Rank); Assert.Equal("600002", second.Code); Assert.Equal("88.5000", second.Score);
                Assert.Equal("排除", excluded.Status); Assert.NotEmpty(excluded.ExclusionReason); // fixture serves no minute rows for 600003
                Assert.Contains("可评分 2", vm.T1RankStatus); Assert.Contains("排除 1", vm.T1RankStatus);
                Assert.Contains("Diagnostic", vm.T1RankStatus); Assert.Contains("未对照次日结果评估", vm.T1RankStatus);
                var grid = Assert.IsType<DataGrid>(window.FindName("T1RankGrid"));
                Assert.Same(vm.T1RankRows, grid.ItemsSource); Assert.Equal(3, grid.Items.Count);
                Assert.True(vm.ExportT1RankingCommand.CanExecute(null));
                vm.T1ExportDirectory = exportDir;
                T1RankingTests.Pump(vm.ExportT1RankingCommand.ExecuteAsync(null));
                Assert.Contains("已导出", vm.T1RankStatus);
                var package = Assert.Single(Directory.GetDirectories(exportDir));
                Assert.True(T1RankingArtifact.Verify(package).IsValid);
                Assert.Equal(original, File.ReadAllBytes(path));
            });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void T31_WpfRankingShowsScoreLimitationsAndExclusionReasons()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIHelper-T1Rank", Guid.NewGuid().ToString("N"));
        try
        {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
            {
                var clock = new T1RankingTests.Clock(); var provider = new T1RankingTests.Provider();
                vm.SnapshotClock = clock;
                vm.SnapshotCapture = new IntradaySnapshotCapture(new MinuteObservationService(provider, clock), new QuoteService(provider),
                    new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[]
                {
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600001", Name = "first", Rank = 1, Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } },
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600003", Name = "third", Rank = 2, Features = new SparrowRankingFeatures { Code = "600003", Name = "third" } }
                });
                T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null));
                T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                T1RankingTests.Pump(vm.RunT1RankingCommand.ExecuteAsync(null));
                Assert.Equal(2, vm.T1RankRows.Count);
                var ranked = vm.T1RankRows[0]; var excluded = vm.T1RankRows[1];
                Assert.Contains("未对照次日结果评估", ranked.Details);
                Assert.Contains(ranked.Code, ranked.Details);
                Assert.Contains("20%×100.0000=20.0000", ranked.Details); // component contribution transparency
                Assert.Equal("600001", ranked.Code); Assert.Equal("600003", excluded.Code);
                Assert.Contains("EndpointMissing", excluded.ExclusionReason);
                Assert.Contains("未保存对齐的基准", vm.T1RankStatus); // RS absent because snapshot has no aligned benchmark series
                Assert.Contains("非交易信号", vm.T1RankStatus);
                Assert.Contains("非全市场最佳", vm.T1RankStatus);
            });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void T32_NewerScanInvalidatesStaleRankingAndBlocksLatePublication()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIHelper-T1Rank", Guid.NewGuid().ToString("N"));
        try
        {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
            {
                var clock = new T1RankingTests.Clock(); var provider = new T1RankingTests.Provider();
                vm.SnapshotClock = clock;
                vm.SnapshotCapture = new IntradaySnapshotCapture(new MinuteObservationService(provider, clock), new QuoteService(provider),
                    new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[]
                {
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600001", Name = "first", Rank = 1, Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } }
                });
                T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null));
                T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                var pending = new TaskCompletionSource<T1RankingResult>(); CancellationToken captured = default;
                vm.T1RankingOverride = (snapshot, fingerprint, features, topN, token) => { captured = token; return pending.Task; };
                var ranking = vm.RunT1RankingCommand.ExecuteAsync(null);
                vm.EnableIntradaySnapshotCapture = false;
                T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null)); // newer scan invalidates the stale ranking
                Assert.True(captured.IsCancellationRequested);
                Assert.False(vm.RunT1RankingCommand.CanExecute(null));
                var retained = new IntradaySnapshotStore(dir).Load(vm.LastSnapshotPath!);
                pending.SetResult(T1PriceOnlyRanking.Rank(retained, IntradayFeatureAnalysis.Analyze(retained,
                    IntradaySnapshotStore.Fingerprint(retained), retained.RequestedAsOf), 5));
                Pump(ranking);
                Assert.Empty(vm.T1RankRows);
                Assert.Contains("失效", vm.T1RankStatus);
                Assert.Equal("Completed", vm.ScanOutcome); Assert.Single(vm.Results);
            });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void T33_WindowClosePreventsLateRankingPublication()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIHelper-T1Rank", Guid.NewGuid().ToString("N"));
        try
        {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
            {
                var clock = new T1RankingTests.Clock(); var provider = new T1RankingTests.Provider();
                vm.SnapshotClock = clock;
                vm.SnapshotCapture = new IntradaySnapshotCapture(new MinuteObservationService(provider, clock), new QuoteService(provider),
                    new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[]
                {
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600001", Name = "first", Rank = 1, Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } }
                });
                T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null));
                T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                var pending = new TaskCompletionSource<T1RankingResult>(); CancellationToken captured = default;
                vm.T1RankingOverride = (snapshot, fingerprint, features, topN, token) => { captured = token; return pending.Task; };
                var ranking = vm.RunT1RankingCommand.ExecuteAsync(null);
                window.Close();
                Assert.True(captured.IsCancellationRequested);
                int updates = 0; vm.PropertyChanged += (_, _) => updates++;
                var retained = new IntradaySnapshotStore(dir).Load(vm.LastSnapshotPath!);
                pending.SetResult(T1PriceOnlyRanking.Rank(retained, IntradayFeatureAnalysis.Analyze(retained,
                    IntradaySnapshotStore.Fingerprint(retained), retained.RequestedAsOf), 5));
                Pump(ranking);
                Assert.Empty(vm.T1RankRows); Assert.Equal(0, updates);
            });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    [Fact]
    public void T34_MissingOrCorruptedSnapshotFailsClosedWithoutTouchingLegacyResults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "AIHelper-T1Rank", Guid.NewGuid().ToString("N"));
        try
        {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
            {
                var clock = new T1RankingTests.Clock(); var provider = new T1RankingTests.Provider();
                vm.SnapshotClock = clock;
                vm.SnapshotCapture = new IntradaySnapshotCapture(new MinuteObservationService(provider, clock), new QuoteService(provider),
                    new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[]
                {
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600001", Name = "first", Rank = 1, Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } }
                });
                T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null));
                T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                string path = vm.LastSnapshotPath!;
                File.WriteAllText(path, "{corrupted");
                T1RankingTests.Pump(vm.RunT1RankingCommand.ExecuteAsync(null));
                Assert.Contains("失败", vm.T1RankStatus); Assert.Empty(vm.T1RankRows);
                Assert.Equal("Completed", vm.ScanOutcome); Assert.Single(vm.Results);
                File.Delete(path);
                Assert.False(vm.RunT1RankingCommand.CanExecute(null)); // missing snapshot disables ranking instead of failing
            });
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
    private static SnapshotSymbol Symbol(string code, string name, int rank, double score, MinuteObservation[] rows) =>
        new(code, name, rank, score, "fixture-profile", null, null, T, Meta, IntradayQuality.MissingEventTime, Series(rows), IntradayQuality.Partial);
    // ---- T1-3-C publication-integrity matrix (T24–T27 run as the existing suites and the size guard) ----
    [Fact]
    public void T01_T11_SameContentIsByteIdenticalIdempotentAndReadable()
    {
        string dir = Dir();
        try
        {
            var result = Rank(Snapshot(("600001", "one", 10.2m)), topN: 5);
            var first = T1RankingArtifact.Write(result, dir, CancellationToken.None);
            byte[] csv = File.ReadAllBytes(first.CsvPath), json = File.ReadAllBytes(first.SidecarPath);
            Assert.False(first.ReusedExisting);
            var second = T1RankingArtifact.Write(result, dir, CancellationToken.None);
            Assert.True(second.ReusedExisting);
            Assert.Equal(first.PackageDirectory, second.PackageDirectory);
            Assert.Equal(csv, File.ReadAllBytes(second.CsvPath)); Assert.Equal(json, File.ReadAllBytes(second.SidecarPath));
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(T1RankingArtifact.ContentBytes(result))), first.ContentFingerprint);
            Assert.Contains(first.ContentFingerprint, Path.GetFileName(first.PackageDirectory));
            var verdict = T1RankingArtifact.Verify(first.PackageDirectory, result);
            Assert.True(verdict.IsValid); Assert.Empty(verdict.Problems);
            Assert.Equal(result.Request.RunId, verdict.RunId);
            string csvText = File.ReadAllText(first.CsvPath), jsonText = File.ReadAllText(first.SidecarPath);
            Assert.Contains("88.5", csvText); Assert.Contains("ExclusionCode", csvText);
            Assert.Contains("PriceOnlyV1", jsonText); Assert.Contains("RelativeStrength", jsonText); Assert.Contains("Diagnostic research artifact", jsonText);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void T02_T03_DifferentTopNHasDistinctIdentityAndOriginalPackageSurvives()
    {
        string dir = Dir();
        try
        {
            var snapshot = Snapshot(("600001", "one", 10.2m));
            var top5 = T1RankingArtifact.Write(Rank(snapshot, topN: 5), dir, CancellationToken.None);
            byte[] csv5 = File.ReadAllBytes(top5.CsvPath), json5 = File.ReadAllBytes(top5.SidecarPath);
            var top3 = T1RankingArtifact.Write(Rank(snapshot, topN: 3), dir, CancellationToken.None);
            Assert.NotEqual(top5.PackageDirectory, top3.PackageDirectory);
            Assert.NotEqual(top5.ContentFingerprint, top3.ContentFingerprint);
            Assert.Equal(csv5, File.ReadAllBytes(top5.CsvPath)); Assert.Equal(json5, File.ReadAllBytes(top5.SidecarPath));
            Assert.True(T1RankingArtifact.Verify(top5.PackageDirectory).IsValid);
            Assert.True(T1RankingArtifact.Verify(top3.PackageDirectory).IsValid);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void T04_T05_ContentIdentityCannotCollideAndRunIdsStayAttributable()
    {
        string dir = Dir();
        try
        {
            var snapshot = Snapshot(("600001", "a", 10.2m));
            var a = T1RankingArtifact.Write(Rank(snapshot, topN: 5), dir, CancellationToken.None);
            // Same RunId, different ranking content: distinct immutable package; original survives.
            var bResult = Rank(snapshot with { Symbols = new[] { snapshot.Symbols[0] with { Name = "b" } } }, topN: 5);
            var b = T1RankingArtifact.Write(bResult, dir, CancellationToken.None);
            Assert.NotEqual(a.PackageDirectory, b.PackageDirectory);
            Assert.True(T1RankingArtifact.Verify(a.PackageDirectory).IsValid);
            // A package directory pre-planted with foreign content must fail closed instead of being adopted.
            var c = Rank(snapshot with { Symbols = new[] { snapshot.Symbols[0] with { Name = "c" } } }, topN: 5);
            string planted = Path.Combine(dir, T1RankingArtifact.PackageName(c.Request.RunId, c.Request.RankingVersion,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(T1RankingArtifact.ContentBytes(c)))));
            Directory.CreateDirectory(planted);
            File.Copy(a.CsvPath, Path.Combine(planted, T1RankingArtifact.CsvFileName));
            File.Copy(a.SidecarPath, Path.Combine(planted, T1RankingArtifact.SidecarFileName));
            Assert.Throws<InvalidDataException>(() => T1RankingArtifact.Write(c, dir, CancellationToken.None));
            Assert.Equal(File.ReadAllBytes(a.SidecarPath), File.ReadAllBytes(Path.Combine(planted, T1RankingArtifact.SidecarFileName)));
            // Distinct RunIds stay attributable: package name and verifier both carry the source run.
            var x = Rank(Snapshot(("600001", "x", 10.2m)), topN: 5);
            var y = Rank(Snapshot(("600001", "y", 10.2m)), topN: 5);
            var px = T1RankingArtifact.Write(x, dir, CancellationToken.None);
            var py = T1RankingArtifact.Write(y, dir, CancellationToken.None);
            Assert.NotEqual(px.PackageDirectory, py.PackageDirectory);
            Assert.Contains(x.Request.RunId.ToString("N"), Path.GetFileName(px.PackageDirectory));
            Assert.Contains(y.Request.RunId.ToString("N"), Path.GetFileName(py.PackageDirectory));
            Assert.Equal(x.Request.RunId, T1RankingArtifact.Verify(px.PackageDirectory).RunId);
            Assert.Equal(y.Request.RunId, T1RankingArtifact.Verify(py.PackageDirectory).RunId);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("precancel")] [InlineData("sidecar")] [InlineData("csv")] [InlineData("publish")]
    public void T06_T07_T08_T29_CancellationAtAnyBoundaryPublishesNothing(string stage)
    {
        string dir = Dir();
        try
        {
            using var cts = new CancellationTokenSource();
            if (stage == "precancel") cts.Cancel(); else BoundaryHook = s => { if (s == stage) cts.Cancel(); };
            try { T1RankingArtifact.Write(Rank(Snapshot(("600001", "one", 10.2m)), topN: 5), dir, cts.Token); }
            catch (OperationCanceledException) { }
            finally { BoundaryHook = null; }
            Assert.Empty(Directory.GetFileSystemEntries(dir)); // no final package, no orphan staging
        }
        finally { Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("csv")] [InlineData("publish")]
    public void T09_T10_InjectedWriteOrPublishFailurePreservesPreviousPackages(string stage)
    {
        string dir = Dir();
        try
        {
            var keep = T1RankingArtifact.Write(Rank(Snapshot(("600001", "keep", 10.2m)), topN: 5), dir, CancellationToken.None);
            byte[] keepCsv = File.ReadAllBytes(keep.CsvPath), keepJson = File.ReadAllBytes(keep.SidecarPath);
            BoundaryHook = s => { if (s == stage) throw new IOException("injected"); };
            try { Assert.Throws<IOException>(() => T1RankingArtifact.Write(Rank(Snapshot(("600001", "next", 10.2m)), topN: 5), dir, CancellationToken.None)); }
            finally { BoundaryHook = null; }
            Assert.Single(Directory.GetDirectories(dir)); // no new package, no orphan staging
            Assert.Equal(keepCsv, File.ReadAllBytes(keep.CsvPath)); Assert.Equal(keepJson, File.ReadAllBytes(keep.SidecarPath));
            Assert.True(T1RankingArtifact.Verify(keep.PackageDirectory).IsValid);
        }
        finally { Directory.Delete(dir, true); }
    }
    [Theory]
    [InlineData("csv")] [InlineData("json-content")] [InlineData("json-fingerprint")] [InlineData("cross-csv")]
    [InlineData("missing-csv")] [InlineData("truncated-json")]
    public void T12_T13_T14_T15_T16_TamperingSubstitutionIncompletenessAreRejected(string target)
    {
        string dir = Dir();
        try
        {
            var result = Rank(Snapshot(("600001", "one", 10.2m)), topN: 5);
            string pkg = T1RankingArtifact.Write(result, dir, CancellationToken.None).PackageDirectory;
            string sidecar = Path.Combine(pkg, T1RankingArtifact.SidecarFileName);
            System.Text.Json.Nodes.JsonNode? node;
            switch (target)
            {
                case "csv": File.WriteAllBytes(Path.Combine(pkg, T1RankingArtifact.CsvFileName), File.ReadAllBytes(Path.Combine(pkg, T1RankingArtifact.CsvFileName)).Concat(new byte[] { 0x20 }).ToArray()); break;
                case "json-content":
                    node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar));
                    node["Content"]!["Coverage"]!["CapturedCandidates"] = 99;
                    File.WriteAllText(sidecar, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); break;
                case "json-fingerprint":
                    node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar));
                    node["Fingerprint"] = new string('0', 64);
                    File.WriteAllText(sidecar, node!.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); break;
                case "cross-csv":
                    string other = T1RankingArtifact.Write(Rank(Snapshot(("600001", "two", 10.2m)), topN: 5), dir, CancellationToken.None).PackageDirectory;
                    File.Copy(Path.Combine(other, T1RankingArtifact.CsvFileName), Path.Combine(pkg, T1RankingArtifact.CsvFileName), true); break;
                case "missing-csv": File.Delete(Path.Combine(pkg, T1RankingArtifact.CsvFileName)); break;
                case "truncated-json": File.WriteAllBytes(sidecar, new byte[] { 0x7B }); break; // "{"
            }
            var verdict = T1RankingArtifact.Verify(pkg);
            Assert.False(verdict.IsValid); Assert.NotEmpty(verdict.Problems);
            if (target == "cross-csv") Assert.Contains(verdict.Problems, p => p.Contains("CSV checksum"));
            Assert.Throws<InvalidDataException>(() => T1RankingArtifact.Write(result, dir, CancellationToken.None)); // no silent overwrite of tampered package
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void T18_StructuralInconsistenciesAreRejectedByIndependentChecks()
    {
        string dir = Dir();
        try
        {
            string pkg = T1RankingArtifact.Write(Rank(Snapshot(("600001", "one", 10.2m)), topN: 5), dir, CancellationToken.None).PackageDirectory;
            string sidecar = Path.Combine(pkg, T1RankingArtifact.SidecarFileName);
            var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sidecar))!;
            // Mutate rank sequence but keep envelope fingerprints internally consistent, so the structural checks fire on their own.
            node["Content"]!["Shortlist"]![0]!["T1Rank"] = 9;
            node["Fingerprint"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(node["Content"], new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } })));
            File.WriteAllText(sidecar, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var verdict = T1RankingArtifact.Verify(pkg);
            Assert.False(verdict.IsValid);
            Assert.Contains(verdict.Problems, p => p.Contains("rank sequence"));
            Assert.DoesNotContain(verdict.Problems, p => p.Contains("fingerprint mismatch")); // canonicalization is exact on re-parse
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void T19_T20_LegacyFlatExportsRemainUntouchedAndNeverVerified()
    {
        string dir = Dir();
        try
        {
            // Simulated pre-package flat pair written by the earlier writer generation.
            var legacy = Rank(Snapshot(("600001", "one", 10.2m)), topN: 5);
            Directory.CreateDirectory(dir);
            string legacyJson = Path.Combine(dir, $"{legacy.Request.RunId:N}-priceonlyv1.json");
            string legacyCsv = Path.Combine(dir, $"{legacy.Request.RunId:N}-priceonlyv1.csv");
            File.WriteAllText(legacyJson, JsonSerializer.Serialize(new { SchemaVersion = 1,
                Fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(T1RankingArtifact.ContentBytes(legacy))),
                Content = T1RankingArtifact.Content(legacy) }, new JsonSerializerOptions { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }));
            File.WriteAllText(legacyCsv, "T1Rank,Status,Code\n1,ranked,600001\n");
            byte[] jsonBefore = File.ReadAllBytes(legacyJson), csvBefore = File.ReadAllBytes(legacyCsv);
            var verdict = T1RankingArtifact.VerifyLegacyFlat(legacyJson, legacyCsv);
            Assert.False(verdict.IsValid);
            Assert.Contains(verdict.Problems, p => p.Contains("unsupported/unverified"));
            var fresh = T1RankingArtifact.Write(Rank(Snapshot(("600001", "two", 10.2m)), topN: 5), dir, CancellationToken.None);
            Assert.NotEqual(legacyJson, fresh.SidecarPath); // layouts cannot silently collide
            Assert.Equal(jsonBefore, File.ReadAllBytes(legacyJson)); Assert.Equal(csvBefore, File.ReadAllBytes(legacyCsv));
            Assert.False(T1RankingArtifact.VerifyLegacyFlat(legacyJson, fresh.CsvPath).IsValid); // ambiguous pairing stays unverified
        }
        finally { Directory.Delete(dir, true); }
    }
    [Fact]
    public void T21_T22_T23_WpfExportIdentityReuseFailureAndCloseSafety()
    {
        string dir = Dir(); string exportDir = Path.Combine(dir, "out");
        try
        {
            SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
            {
                var clock = new Clock(); var provider = new Provider();
                vm.SnapshotClock = clock;
                vm.SnapshotCapture = new IntradaySnapshotCapture(new MinuteObservationService(provider, clock), new QuoteService(provider),
                    new MarketCalendarService(provider), new IntradaySnapshotStore(dir), clock);
                vm.Notification = _ => { }; vm.EnableIntradaySnapshotCapture = true;
                vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[]
                {
                    new SparrowRankedCandidate { RankingProfile = "fixture", Code = "600001", Name = "first", Rank = 1, Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } }
                });
                Pump(vm.StartCommand.ExecuteAsync(null));
                Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
                Pump(vm.RunT1RankingCommand.ExecuteAsync(null));
                vm.T1ExportDirectory = exportDir;
                Pump(vm.ExportT1RankingCommand.ExecuteAsync(null));
                Assert.Contains("新发布不可变包", vm.T1RankStatus);
                Assert.Contains("内容指纹", vm.T1RankStatus);
                string pkg = Assert.Single(Directory.GetDirectories(exportDir));
                Assert.True(T1RankingArtifact.Verify(pkg).IsValid);
                Pump(vm.ExportT1RankingCommand.ExecuteAsync(null));
                Assert.Contains("复用现有有效包", vm.T1RankStatus);
                Assert.Single(Directory.GetDirectories(exportDir));
                // An identical re-export reuses the package without writing; force fresh staging with different content.
                vm.TopN = 3;
                Pump(vm.RunT1RankingCommand.ExecuteAsync(null));
                BoundaryHook = s => { if (s == "csv") throw new IOException("injected"); };
                Pump(vm.ExportT1RankingCommand.ExecuteAsync(null));
                BoundaryHook = null;
                Assert.Contains("导出失败", vm.T1RankStatus);
                Assert.DoesNotContain("已导出", vm.T1RankStatus); // failure never reports success
                Assert.True(T1RankingArtifact.Verify(pkg).IsValid); // previous package remains valid
                var reached = new TaskCompletionSource(); var release = new TaskCompletionSource();
                BoundaryHook = s => { if (s == "publish") { reached.TrySetResult(); release.Task.Wait(); } };
                var export = vm.ExportT1RankingCommand.ExecuteAsync(null);
                Pump(reached.Task); // export is parked exactly at the publication boundary
                int updates = 0; vm.PropertyChanged += (_, _) => updates++;
                window.Close();
                release.SetResult();
                Pump(export);
                BoundaryHook = null;
                Assert.Single(Directory.GetDirectories(exportDir)); // only the pre-existing package: no new publication
                Assert.Empty(Directory.GetFileSystemEntries(exportDir, ".staging-*")); // staging cleaned
                Assert.Equal(0, updates); // no late success after close
            });
        }
        finally { Directory.Delete(dir, true); }
    }
    private static string Dir() => Path.Combine(Path.GetTempPath(), "AIHelper-T1Pkg", Guid.NewGuid().ToString("N"));
    internal static Action<string>? BoundaryHook { get => T1RankingArtifact.BoundaryHook; set => T1RankingArtifact.BoundaryHook = value; }
    internal static void Pump(Task task) { if (!task.IsCompleted) { var frame = new DispatcherFrame(); task.GetAwaiter().OnCompleted(() => frame.Continue = false); Dispatcher.PushFrame(frame); } task.GetAwaiter().GetResult(); }
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => T; }
    internal sealed class Provider : IStockDataProvider
    {
        public bool CanHandle(StockDataRequest request) => true;
        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken token = default)
        {
            bool empty = request.Query.TryGetValue("code", out var code) && code == "600003"; // no minute evidence for this candidate
            var list = empty ? Rows(10.2m).Take(0).Select(row => new { Time = row.ExchangeEventTime!.Value.ToOffset(ChinaExchangeTime.Offset).ToString("yyyy-MM-dd HH:mm"), Price = row.PriceYuan * 1000, Number = 1, Amount = 10 })
                : Rows(10.2m).Select(row => new { Time = row.ExchangeEventTime!.Value.ToOffset(ChinaExchangeTime.Offset).ToString("yyyy-MM-dd HH:mm"), Price = row.PriceYuan * 1000, Number = 1, Amount = 10 });
            string json = request.Path == "/api/workday" ? "{\"data\":{\"is_workday\":true}}"
                : request.Path == "/api/minute" ? JsonSerializer.Serialize(new { data = new { List = list } })
                : "{\"data\":[]}";
            return Task.FromResult(new StockDataResult { Success = true, Source = "EastMoney", SourceKind = DataSourceKind.EastMoney, Json = json });
        }
    }
}
