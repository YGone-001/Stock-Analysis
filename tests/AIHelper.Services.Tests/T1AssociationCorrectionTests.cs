using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData;
using Xunit;
using Fixture = AIHelper.Services.Tests.T1EvidenceReadinessTests.Fixture;

namespace AIHelper.Services.Tests;

[Collection("T1ArtifactWrites")]
public sealed class T1AssociationCorrectionTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private static T1CalendarEvidence Calendar(string exchange, DateOnly? target = null)
    {
        var c = T1EvidenceReadinessTests.Calendar(target);
        return c with { Exchange = exchange, Acquisition = c.Acquisition with { CoverageEvidence = c.Acquisition.CoverageEvidence
            .Select(e => e with { Scope = e.Scope with { Exchange = exchange } }).ToArray() } };
    }
    private static T1AssociationInput WithCalendars(T1AssociationInput input) => input with
    {
        Evidence = input.Evidence with { Calendar = null },
        Calendars = new Dictionary<string, T1CalendarEvidence> { ["SSE"] = Calendar("SSE"), ["SZSE"] = Calendar("SZSE"), ["BSE"] = Calendar("BSE") }
    };
    private static T1AssociationArtifact Build(T1AssociationInput input) => T1EvidenceReadiness.Associate(new[] { input });

    internal static T1AssociationArtifact Rehash(T1AssociationContent content)
    {
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        return new(content.SchemaVersion, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(content, options))), content);
    }
    internal static T1AssociationInput Mixed(Fixture f)
    {
        string[] symbols = { "600001", "000001", "300001", "430001", "600005" };
        var s = f.Snapshot with { Symbols = f.Snapshot.Symbols.Select((c, i) => c with { Symbol = symbols[i],
            Minutes = c.Minutes with { Rows = c.Minutes.Rows.Select(r => r with { Symbol = symbols[i] }).ToArray() } }).ToArray() };
        string path = new IntradaySnapshotStore(Path.Combine(f.Root, "mixed")).Save(s);
        var ranking = T1PriceOnlyRanking.Rank(s, IntradayFeatureAnalysis.Analyze(s, IntradaySnapshotStore.Fingerprint(s), s.RequestedAsOf), 2);
        var package = T1RankingArtifact.Write(ranking, Path.Combine(f.Root, "mixed-packages"), CancellationToken.None);
        var outcomes = s.Symbols.Select(c =>
        {
            string exchange = c.Symbol.StartsWith('6') ? "SSE" : c.Symbol.StartsWith('4') ? "BSE" : "SZSE";
            string id = c.Symbol + (exchange == "SSE" ? ".SH" : exchange == "SZSE" ? ".SZ" : ".BJ");
            var o = T1EvidenceReadinessTests.Outcome();
            return o with { Symbol = c.Symbol, Exchange = exchange, SourceInstrumentId = id,
                Daily = o.Daily! with { Symbol = c.Symbol, TsCode = id }, Basis = o.Basis! with { SourceInstrumentId = id } };
        }).ToArray();
        return new(new(path, package.PackageDirectory, T1EvidenceReadinessTests.Calendar(), outcomes, Origin: T1EvidenceOrigin.TEST_FIXTURE),
            outcomes.ToDictionary(o => o.Symbol, o => o.Basis!, StringComparer.Ordinal));
    }

    [Fact]
    public void T01_MissingMatchingCalendarMustNotMislabelValidSzseIdentity()
    {
        using var f = new Fixture();
        var artifact = T1EvidenceReadiness.Associate(new[] { Mixed(f) });
        var candidate = artifact.Content.Records[0].Candidates.Single(c => c.Original.Symbol == "000001");
        Assert.DoesNotContain("SYMBOL_IDENTITY_MISMATCH", candidate.Outcome.Reasons);
        Assert.Contains("CALENDAR_NOT_VERIFIED", candidate.Outcome.Reasons);
    }

    [Fact]
    public void SelfRehashedBasisContradictionCannotRemainUsable()
    {
        using var f = new Fixture(); var a = T1EvidenceReadiness.Associate(new[] { T1OutcomeAssociationTests.Input(f) });
        var r = a.Content.Records[0]; var c = r.Candidates[0];
        c = c with { Outcome = c.Outcome with { PriceBasis = T1PriceBasisState.INCOMPATIBLE_BASIS,
            Reasons = c.Outcome.Reasons.Append("PRICE_BASIS_MISMATCH").Order(StringComparer.Ordinal).ToArray() } };
        var candidates = new[] { c }.Concat(r.Candidates.Skip(1)).ToArray();
        r = r with { Candidates = candidates, Evidence = r.Evidence with { Outcomes = candidates.Select(x => x.Outcome).ToArray() } };
        var coverage = a.Content.Coverage with { BasisAligned = 4, BasisBlocked = 1,
            SourceCoverage = a.Content.Coverage.SourceCoverage with { Samples = new[] { r.Evidence }, PriceBasisMismatches = 1 } };
        Assert.False(T1EvidenceReadiness.VerifyAssociation(Rehash(a.Content with { Records = new[] { r }, Coverage = coverage })));
    }

    [Theory]
    [InlineData("SSE")] [InlineData("SZSE")]
    public void T02_T03_SingleExchangeSupportsLegacyCalendarOption(string exchange)
    {
        using var f = new Fixture(); var input = Mixed(f);
        var source = T1EvidenceReadiness.VerifySnapshot(input.Evidence.SnapshotPath).Snapshot!;
        source = source with { Symbols = source.Symbols.Where(s => exchange == "SSE" ? s.Symbol.StartsWith('6') : s.Symbol.StartsWith('0') || s.Symbol.StartsWith('3')).ToArray() };
        string path = new IntradaySnapshotStore(Path.Combine(f.Root, "single")).Save(source);
        var rank = T1PriceOnlyRanking.Rank(source, IntradayFeatureAnalysis.Analyze(source, IntradaySnapshotStore.Fingerprint(source), source.RequestedAsOf), 2);
        var package = T1RankingArtifact.Write(rank, Path.Combine(f.Root, "single-packages"), CancellationToken.None);
        var symbols = source.Symbols.Select(s => s.Symbol).ToHashSet();
        input = input with { Evidence = input.Evidence with { SnapshotPath = path, PackageDirectory = package.PackageDirectory,
            Calendar = Calendar(exchange), Outcomes = input.Evidence.Outcomes!.Where(o => symbols.Contains(o.Symbol)).ToArray() },
            DecisionBases = input.DecisionBases!.Where(p => symbols.Contains(p.Key)).ToDictionary() };
        var a = Build(input);
        Assert.True(T1EvidenceReadiness.VerifyAssociation(a));
        Assert.All(a.Content.Records[0].Candidates, c => { Assert.True(c.AssociationUsableDeclared); Assert.Equal(exchange, c.Exchange); });
    }

    [Fact]
    public void T04_T05_T08_T12_T13_T23_MixedPoolHasAllOriginalAttributionAndCorrectCalendars()
    {
        using var f = new Fixture(); var input = WithCalendars(Mixed(f));
        byte[] snapshot = File.ReadAllBytes(input.Evidence.SnapshotPath!);
        string[] files = Directory.GetFiles(input.Evidence.PackageDirectory!); var bytes = files.Select(File.ReadAllBytes).ToArray();
        var a = Build(input); var r = a.Content.Records[0];
        Assert.True(T1EvidenceReadiness.VerifyAssociation(a)); Assert.Equal(2, a.SchemaVersion);
        Assert.Equal(new T1RankingCoverage(5, 4, 1, 2, 2), r.Evidence.Coverage);
        Assert.Equal(5, r.Candidates.Count); Assert.Equal(2, r.Candidates.Count(c => c.Category == T1CandidateAssociationCategory.SHORTLISTED));
        Assert.Single(r.Candidates, c => c.Category == T1CandidateAssociationCategory.EXCLUDED);
        var truncated = r.Candidates.Where(c => c.Category == T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED).ToArray();
        Assert.Equal(2, truncated.Length); Assert.All(truncated, c => { Assert.Null(c.T1Rank); Assert.Null(c.ExclusionCode); });
        Assert.Equal(T1ReadinessStatus.NOT_VERIFIED, r.Evidence.FullEligibleOrdering);
        Assert.All(r.Candidates, c =>
        {
            Assert.True(c.AssociationUsableDeclared); Assert.Equal(T1ReadinessStatus.PASS, c.Calendar!.Status);
            Assert.Equal(new DateOnly(2026, 11, 2), c.Calendar.NextTradingDate); Assert.Equal(64, c.Calendar.EvidenceFingerprint.Length);
            Assert.Equal(c.Exchange, c.Calendar.Exchange); Assert.Empty(c.Calendar.Reasons);
        });
        var original = T1EvidenceReadiness.VerifySnapshot(input.Evidence.SnapshotPath).Snapshot!;
        Assert.Equal(original.Symbols.Select(s => (s.Symbol, s.Rank, s.Score)), r.Candidates.Select(c => (c.Original.Symbol, c.Original.OriginalRank, c.Original.OriginalScore)));
        Assert.Equal(snapshot, File.ReadAllBytes(input.Evidence.SnapshotPath!));
        for (int i = 0; i < files.Length; i++) Assert.Equal(bytes[i], File.ReadAllBytes(files[i]));
        Assert.Equal(0, a.Content.Coverage.SourceCoverage.AuthenticatedProductionSamples);
    }

    [Fact]
    public void T06_MissingSzseCalendarOnlyBlocksSzseAndRetainsAllIdentities()
    {
        using var f = new Fixture(); var input = WithCalendars(Mixed(f));
        input = input with { Calendars = input.Calendars!.Where(p => p.Key != "SZSE").ToDictionary() };
        var a = Build(input); Assert.True(T1EvidenceReadiness.VerifyAssociation(a));
        Assert.Equal(5, a.Content.Records[0].Candidates.Count);
        Assert.All(a.Content.Records[0].Candidates, c =>
        {
            Assert.Equal(c.Exchange != "SZSE", c.AssociationUsableDeclared);
            if (c.Exchange == "SZSE") { Assert.Contains("CALENDAR_NOT_VERIFIED", c.Reasons); Assert.Null(c.Calendar!.NextTradingDate); }
            Assert.DoesNotContain("SYMBOL_IDENTITY_MISMATCH", c.Reasons);
        });
        Assert.Equal(2, a.Content.Coverage.SourceCoverage.MissingCalendarCoverage);
    }

    [Fact]
    public void T07_T11_ImpersonatedOrAmbiguousCalendarsCannotBeAccepted()
    {
        using var f = new Fixture(); var input = WithCalendars(Mixed(f));
        Assert.Throws<ArgumentException>(() => Build(input with { Calendars = new Dictionary<string, T1CalendarEvidence> { ["SZSE"] = Calendar("SSE") } }));
        Assert.Throws<ArgumentException>(() => Build(input with { Evidence = input.Evidence with { Calendar = Calendar("SSE") } }));
        var contradictory = Calendar("SZSE");
        contradictory = contradictory with { Acquisition = contradictory.Acquisition with { Data = contradictory.Acquisition.Data.Append(contradictory.Acquisition.Data[0]).ToArray() } };
        var a = Build(input with { Calendars = new Dictionary<string, T1CalendarEvidence> { ["SSE"] = Calendar("SSE"), ["SZSE"] = contradictory, ["BSE"] = Calendar("BSE") } });
        Assert.True(T1EvidenceReadiness.VerifyAssociation(a));
        Assert.All(a.Content.Records[0].Candidates.Where(c => c.Exchange == "SZSE"), c => { Assert.False(c.AssociationUsableDeclared); Assert.Equal(T1ReadinessStatus.FAIL, c.Calendar!.Status); });
    }

    [Fact]
    public void T09_T10_T14_HolidaysAndOhlcStayDownstreamAndTargetsNeverFollowMissingBars()
    {
        using var f = new Fixture(); var input = WithCalendars(Mixed(f)); var first = Build(input);
        var target = f.Snapshot.TradingDate.AddDays(7);
        var maps = input.Calendars!.ToDictionary(); maps["SZSE"] = Calendar("SZSE", target);
        var outcomes = input.Evidence.Outcomes!.Select(o => o.Exchange != "SZSE" ? o : o with
        {
            TargetDate = target, RetrievedAtUtc = o.RetrievedAtUtc!.Value.AddDays(4),
            Daily = o.Daily! with { TradingDate = target, Open = 20, High = 22, Low = 18, Close = 21 },
            CorporateAction = o.CorporateAction! with { TargetDate = target, RetrievedAtUtc = o.CorporateAction.RetrievedAtUtc.AddDays(4) }
        }).ToArray();
        var second = Build(input with { Calendars = maps, Evidence = input.Evidence with { Outcomes = outcomes } });
        var a = first.Content.Records[0]; var b = second.Content.Records[0];
        Assert.True(T1EvidenceReadiness.VerifyAssociation(second)); Assert.Null(b.Evidence.Calendar.NextTradingDate);
        Assert.Equal("MULTIPLE", b.Evidence.Calendar.Exchange);
        Assert.All(b.Candidates, c => { Assert.True(c.AssociationUsableDeclared); Assert.Equal(c.Exchange == "SZSE" ? target : new DateOnly(2026, 11, 2), c.Calendar!.NextTradingDate); });
        Assert.Equal(a.Evidence.SnapshotFingerprint, b.Evidence.SnapshotFingerprint); Assert.Equal(a.Evidence.PackageFingerprint, b.Evidence.PackageFingerprint);
        Assert.Equal(a.Evidence.EvidenceClass, b.Evidence.EvidenceClass); Assert.Equal(a.Evidence.Captured, b.Evidence.Captured);
        Assert.Equal(JsonSerializer.Serialize(a.Derivation!.Ranked, Json), JsonSerializer.Serialize(b.Derivation!.Ranked, Json)); // includes stored T1 scores and attribution
        outcomes[1] = outcomes[1] with { Daily = null };
        var missing = Build(input with { Calendars = maps, Evidence = input.Evidence with { Outcomes = outcomes } });
        var candidate = missing.Content.Records[0].Candidates.Single(c => c.Original.Symbol == outcomes[1].Symbol);
        Assert.Equal(target, candidate.Calendar!.NextTradingDate); Assert.False(candidate.AssociationUsableDeclared);
    }

    [Theory]
    [InlineData("calendar")] [InlineData("fields")] [InlineData("rank")] [InlineData("identity")]
    [InlineData("target")] [InlineData("availability")] [InlineData("source")] [InlineData("reason")]
    [InlineData("category")] [InlineData("original-score")] [InlineData("duplicate")]
    public void T18_T19_T20_RehashedContradictoryCandidatesFailEvenWithSynchronizedSample(string kind)
    {
        using var f = new Fixture(); var a = Build(WithCalendars(Mixed(f))); var r = a.Content.Records[0];
        var c = r.Candidates.First(c => c.T1Rank is not null);
        Assert.True(c.AssociationUsableDeclared);
        var changed = kind switch
        {
            "calendar" => c with { Calendar = c.Calendar! with { Status = T1ReadinessStatus.NOT_VERIFIED } },
            "fields" => c with { Outcome = c.Outcome with { FieldValidity = T1ReadinessStatus.FAIL } },
            "rank" => c with { T1Rank = 19 },
            "identity" => c with { Outcome = c.Outcome with { Symbol = "999999" } },
            "target" => c with { Calendar = c.Calendar! with { NextTradingDate = c.Calendar.NextTradingDate!.Value.AddDays(1) } },
            "availability" => c with { OutcomeAvailableDeclared = false },
            "source" => c with { Outcome = c.Outcome with { SourceAvailability = T1ReadinessStatus.FAIL } },
            "reason" => c with { Reasons = new[] { "OUTCOME_AVAILABLE_DECLARED" } },
            "category" => c with { Category = (T1CandidateAssociationCategory)999 },
            "original-score" => c with { Original = c.Original with { OriginalScore = c.Original.OriginalScore + 1 } },
            _ => c with { Original = r.Candidates.Last().Original }
        };
        var candidates = r.Candidates.Select(x => x == c ? changed : x).ToArray();
        r = r with { Candidates = candidates, Evidence = r.Evidence with { Outcomes = candidates.Select(x => x.Outcome).ToArray() } };
        var forged = Rehash(a.Content with { Records = new[] { r }, Coverage = a.Content.Coverage with
            { SourceCoverage = a.Content.Coverage.SourceCoverage with { Samples = new[] { r.Evidence } } } });
        Assert.False(T1EvidenceReadiness.VerifyAssociation(forged));
    }

    [Theory]
    [InlineData("basis")] [InlineData("calendar")] [InlineData("fields")] [InlineData("symbol")]
    [InlineData("target")] [InlineData("source")] [InlineData("session")] [InlineData("request")]
    public void RehashedRawDeclarationChangesMustReplayAcceptedVerifiers(string kind)
    {
        using var f = new Fixture(); var a = Build(WithCalendars(Mixed(f))); var r = a.Content.Records[0]; var p = r.Derivation!;
        var o = p.Outcomes[0];
        o = kind switch
        {
            "basis" => o with { Basis = o.Basis! with { AdjustmentMode = HistoricalPriceAdjustmentMode.ForwardAdjusted } },
            "fields" => o with { Daily = o.Daily! with { Low = 999 } },
            "symbol" => o with { Daily = o.Daily! with { Symbol = "999999" } },
            "target" => o with { TargetDate = o.TargetDate.AddDays(1) },
            "source" => o with { State = T1OutcomeSourceState.PERMISSION_DENIED },
            "session" => o with { Session = T1OutcomeSession.UNKNOWN },
            _ => o
        };
        p = p with { Outcomes = new[] { o }.Concat(p.Outcomes.Skip(1)).ToArray() };
        if (kind == "calendar") p = p with { Calendars = new Dictionary<string, T1CalendarEvidence>() };
        if (kind == "request") p = p with { RankingRequest = p.RankingRequest! with { RunId = Guid.Empty } };
        Assert.False(T1EvidenceReadiness.VerifyAssociation(Rehash(a.Content with { Records = new[] { r with { Derivation = p } } })));
    }

    [Theory]
    [InlineData("breakdown")] [InlineData("missing")] [InlineData("aligned")] [InlineData("blocked")]
    [InlineData("usable")] [InlineData("ratio")] [InlineData("declared")] [InlineData("validated")]
    [InlineData("unavailable")] [InlineData("suspended")] [InlineData("execution")] [InlineData("captured")]
    [InlineData("source-linked")] [InlineData("source-strategy")] [InlineData("source-calendar")]
    public void T21_T22_RehashedCoverageAndBreakdownCountersAreIndependentlyRecomputed(string kind)
    {
        using var f = new Fixture(); var a = Build(WithCalendars(Mixed(f))); var c = a.Content.Coverage;
        c = kind switch
        {
            "breakdown" => c with { Breakdown = new[] { c.Breakdown[0] with { AssociatedDeclared = 4 } } },
            "missing" => c with { MissingBars = 1 }, "aligned" => c with { BasisAligned = 4 }, "blocked" => c with { BasisBlocked = 1 },
            "usable" => c with { UsableDeclaredAssociations = 4, AssociationCoverage = "4/5" },
            "ratio" => c with { AssociationCoverage = "4/5" }, "declared" => c with { DeclaredAvailableBars = 4 },
            "validated" => c with { ValidatedFields = 4 }, "unavailable" => c with { UnavailableSources = 1 },
            "suspended" => c with { SupportedSuspensions = 1 }, "execution" => c with { NoExecutionEvidence = 4 },
            "captured" => c with { CandidateAssociations = 4, TotalCapturedCandidates = 4 },
            "source-linked" => c with { SourceCoverage = c.SourceCoverage with { LinkedPairs = 2 } },
            "source-strategy" => c with { SourceCoverage = c.SourceCoverage with { Strategies = new Dictionary<string, int> { ["V2"] = 2 } } },
            _ => c with { SourceCoverage = c.SourceCoverage with { MissingCalendarCoverage = 1 } }
        };
        Assert.False(T1EvidenceReadiness.VerifyAssociation(Rehash(a.Content with { Coverage = c })));
    }

    [Fact]
    public void T24_T25_NoSourceOrCallerMetadataCanGrantProductionReadiness()
    {
        var a = Build(new(new(null, null))); Assert.True(T1EvidenceReadiness.VerifyAssociation(a));
        Assert.Equal(T1ReadinessStatus.BLOCKED, a.Content.Coverage.SourceCoverage.Overall);
        Assert.Equal(0, a.Content.Coverage.SourceCoverage.AuthenticatedProductionSamples);
        using var f = new Fixture(); a = Build(WithCalendars(Mixed(f))); var r = a.Content.Records[0];
        var proof = r.Derivation! with { SourceFacts = r.Derivation!.SourceFacts with { PitAvailability = T1ReadinessStatus.PASS, EvidenceClass = T1EvidenceClass.LIVE_RECORDED_PIT } };
        Assert.False(T1EvidenceReadiness.VerifyAssociation(Rehash(a.Content with { Records = new[] { r with { Derivation = proof } } })));
    }

    [Theory]
    [InlineData("duplicate")] [InlineData("version")] [InlineData("ambiguous")] [InlineData("unsupported")]
    public void T11_CalendarCollectionCliRejectsAmbiguityAndInvalidIdentity(string kind)
    {
        using var f = new Fixture(); var path = Path.Combine(f.Root, "calendars.json");
        var entries = kind == "duplicate" ? new[] { Calendar("SZSE"), Calendar("SZSE") } : new[] { Calendar(kind == "unsupported" ? "NYSE" : "SZSE") };
        File.WriteAllText(path, JsonSerializer.Serialize(new T1ExchangeCalendarInput(kind == "version" ? 2 : 1, entries), Json));
        var args = new Dictionary<string, string> { ["calendars"] = path };
        if (kind == "ambiguous") args["calendar"] = Path.Combine(f.Root, "missing.json");
        Assert.Equal(1, T1EvidenceReadiness.RunAssociationCommand(args, new StringWriter(), new StringWriter()));
    }

    [Fact]
    public void T15_T16_T17_MixedCliPublicationIsDeterministicAndDoesNotMigrateLegacyBytes()
    {
        using var f = new Fixture(); var input = WithCalendars(Mixed(f)); var artifact = Build(input);
        string output = T1OutcomeAssociationTests.IgnoredStorage(f);
        var legacy = Rehash(T1EvidenceReadiness.Associate(Array.Empty<T1AssociationInput>()).Content with { SchemaVersion = 1 });
        string old = Path.Combine(output, "t1-association-" + legacy.Fingerprint); Directory.CreateDirectory(old);
        string oldFile = Path.Combine(old, "association.json"); File.WriteAllText(oldFile, T1EvidenceReadiness.CanonicalAssociation(legacy));
        byte[] oldBytes = File.ReadAllBytes(oldFile);
        Assert.True(T1EvidenceReadiness.VerifyLegacyAssociationIntegrity(legacy)); Assert.False(T1EvidenceReadiness.VerifyAssociation(legacy));
        Assert.False(T1EvidenceReadiness.VerifyAssociationPackage(old));
        Assert.Throws<InvalidDataException>(() => T1EvidenceReadiness.PublishAssociation(legacy, output));
        string calendars = Path.Combine(f.Root, "calendars.json"), outcomes = Path.Combine(f.Root, "outcomes.json");
        File.WriteAllText(calendars, JsonSerializer.Serialize(new T1ExchangeCalendarInput(1, input.Calendars!.Values.Reverse().ToArray()), Json));
        File.WriteAllText(outcomes, JsonSerializer.Serialize(new T1AssociationOutcomeInput(input.Evidence.Outcomes!, input.DecisionBases), Json));
        var args = new Dictionary<string, string> { ["snapshot"] = input.Evidence.SnapshotPath!, ["ranking-package"] = input.Evidence.PackageDirectory!,
            ["calendars"] = calendars, ["outcomes"] = outcomes, ["output"] = output, ["test-fixture"] = "true" };
        var writer = new StringWriter(); var error = new StringWriter();
        Assert.Equal(2, T1EvidenceReadiness.RunAssociationCommand(args, writer, error)); Assert.Empty(error.ToString());
        Assert.Contains("PUBLICATION=PUBLISHED", writer.ToString()); Assert.Contains("CANDIDATES=5", writer.ToString());
        string package = Path.Combine(output, "t1-association-" + artifact.Fingerprint);
        Assert.True(T1EvidenceReadiness.VerifyAssociationPackage(package));
        byte[] bytes = File.ReadAllBytes(Path.Combine(package, "association.json")); writer.GetStringBuilder().Clear();
        Assert.Equal(2, T1EvidenceReadiness.RunAssociationCommand(args, writer, error)); Assert.Contains("PUBLICATION=REUSED", writer.ToString());
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(package, "association.json"))); Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));
    }
}
