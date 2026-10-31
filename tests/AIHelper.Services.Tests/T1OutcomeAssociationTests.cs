using System.Diagnostics;
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
public sealed class T1OutcomeAssociationTests
{
    private static T1AssociationInput Input(Fixture f)
    {
        var outcomes = f.Snapshot.Symbols.Select(s =>
        {
            var original = T1EvidenceReadinessTests.Outcome();
            string id = s.Symbol + ".SH";
            return original with { Symbol = s.Symbol, SourceInstrumentId = id,
                Daily = original.Daily! with { Symbol = s.Symbol, TsCode = id }, Basis = original.Basis! with { SourceInstrumentId = id } };
        }).ToArray();
        return new(f.Request() with { Calendar = T1EvidenceReadinessTests.Calendar(), Outcomes = outcomes },
            outcomes.ToDictionary(o => o.Symbol, o => o.Basis!, StringComparer.Ordinal));
    }
    private static T1AssociationArtifact Build(T1AssociationInput input) => T1EvidenceReadiness.Associate(new[] { input });
    private static T1AssociationRecord Record(T1AssociationArtifact artifact) => Assert.Single(artifact.Content.Records);

    [Fact]
    public void T01_T08_T09_T10_T11_T26_T27_T33_T37_CompletePoolHasDeclaredAssociationsAndImmutableInputs()
    {
        using var f = new Fixture(); var input = Input(f);
        byte[] snapshot = File.ReadAllBytes(f.SnapshotPath), csv = File.ReadAllBytes(f.Package.CsvPath), sidecar = File.ReadAllBytes(f.Package.SidecarPath);
        var artifact = Build(input); var record = Record(artifact); var coverage = artifact.Content.Coverage;
        Assert.True(T1EvidenceReadiness.VerifyAssociation(artifact));
        Assert.Equal(5, record.Candidates.Count); Assert.Equal(5, coverage.CandidateAssociations);
        Assert.Equal(2, record.Candidates.Count(c => c.Category == T1CandidateAssociationCategory.SHORTLISTED));
        var omitted = record.Candidates.Where(c => c.Category == T1CandidateAssociationCategory.ELIGIBLE_TRUNCATED_DECLARED).ToArray();
        Assert.Equal(2, omitted.Length);
        Assert.All(omitted, c => { Assert.Null(c.ExclusionCode); Assert.Null(c.T1Rank); Assert.Equal(T1ReadinessStatus.NOT_VERIFIED, c.IndependentEligibility); Assert.Contains("FULL_ORDERING_NOT_VERIFIED", c.Reasons); });
        var excluded = Assert.Single(record.Candidates, c => c.Category == T1CandidateAssociationCategory.EXCLUDED);
        Assert.Equal(f.Ranking.Excluded[0].Code, excluded.ExclusionCode); Assert.Equal(f.Ranking.Excluded[0].Reason, excluded.ExclusionReason);
        Assert.Equal(4, coverage.SourceCoverage.EligibleCandidates); Assert.Equal(1, coverage.SourceCoverage.ExcludedCandidates);
        Assert.Equal(5, coverage.DeclaredAvailableBars); Assert.Equal(5, coverage.ValidatedFields); Assert.Equal(5, coverage.BasisAligned);
        Assert.Equal("5/5", coverage.AssociationCoverage); Assert.Equal(5, coverage.NoExecutionEvidence);
        Assert.Equal(1, coverage.SourceCoverage.FixtureSamples); Assert.Equal(0, coverage.SourceCoverage.AuthenticatedProductionSamples);
        Assert.Equal(T1ReadinessStatus.BLOCKED, coverage.SourceCoverage.Overall);
        Assert.NotEqual(T1EvidenceClass.LIVE_RECORDED_PIT, record.Evidence.EvidenceClass);
        Assert.Equal(T1EvidenceReadiness.CanonicalAssociation(artifact), T1EvidenceReadiness.CanonicalAssociation(Build(input)));
        Assert.Equal(snapshot, File.ReadAllBytes(f.SnapshotPath)); Assert.Equal(csv, File.ReadAllBytes(f.Package.CsvPath)); Assert.Equal(sidecar, File.ReadAllBytes(f.Package.SidecarPath));
    }

    [Theory]
    [InlineData("missing")] [InlineData("snapshot")] [InlineData("package")]
    [InlineData("run")] [InlineData("fingerprint")] [InlineData("score")]
    public void T02_T03_T04_T05_T06_T07_InvalidSourcePairCannotCertifyAssociation(string kind)
    {
        using var f = new Fixture(); var input = Input(f);
        if (kind == "missing") input = input with { Evidence = input.Evidence with { SnapshotPath = Path.Combine(f.Root, "absent.json") }, DecisionBases = null };
        if (kind == "snapshot") { File.WriteAllText(f.SnapshotPath, "{broken"); input = input with { DecisionBases = null }; }
        if (kind == "package") File.AppendAllText(f.Package.CsvPath, "tamper");
        if (kind is "run" or "fingerprint" or "score")
        {
            var r = f.Ranking;
            r = kind switch
            {
                "run" => r with { Request = r.Request with { RunId = Guid.NewGuid() } },
                "fingerprint" => r with { Request = r.Request with { SnapshotFingerprint = new string('A', 64) } },
                _ => r with { Shortlist = r.Shortlist.Select(c => c with { Explanation = c.Explanation with { OriginalScore = 999 } }).ToArray() }
            };
            input = input with { Evidence = f.Request(r) with { Calendar = input.Evidence.Calendar, Outcomes = input.Evidence.Outcomes } };
        }
        var record = Record(Build(input));
        Assert.NotEqual(T1ReadinessStatus.PASS, record.Evidence.Binding);
        Assert.All(record.Candidates, c => Assert.False(c.AssociationUsableDeclared));
        if (kind is "missing" or "snapshot") Assert.Empty(record.Candidates);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void T12_T13_DuplicateOrForeignCandidateCannotEnterCoverage(bool duplicate)
    {
        using var f = new Fixture(); var input = Input(f); var rows = input.Evidence.Outcomes!;
        input = input with { Evidence = input.Evidence with { Outcomes = duplicate ? rows.Append(rows[0]).ToArray() : new[] { rows[0] with { Symbol = "600999" } } } };
        Assert.Throws<ArgumentException>(() => Build(input));
    }

    [Theory]
    [InlineData(3)] [InlineData(7)]
    public void T14_T15_CalendarMapsTheFirstDeclaredOpenDay(int gap)
    {
        using var f = new Fixture(); var input = Input(f);
        var target = f.Snapshot.TradingDate.AddDays(gap);
        var artifact = Build(input with { Evidence = input.Evidence with { Calendar = T1EvidenceReadinessTests.Calendar(target), Outcomes = null } });
        Assert.Equal(target, Record(artifact).Evidence.Calendar.NextTradingDate);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void T16_T17_PartialOrContradictoryCalendarCannotMap(bool contradictory)
    {
        using var f = new Fixture(); var input = Input(f); var c = input.Evidence.Calendar!;
        c = c with { Acquisition = contradictory ? c.Acquisition with { Data = c.Acquisition.Data.Append(c.Acquisition.Data[0] with { IsOpen = false }).ToArray() }
            : c.Acquisition with { CoverageEvidence = Array.Empty<HistoricalCoverageEvidence>() } };
        var record = Record(Build(input with { Evidence = input.Evidence with { Calendar = c } }));
        Assert.Null(record.Evidence.Calendar.NextTradingDate); Assert.All(record.Candidates, a => Assert.False(a.AssociationUsableDeclared));
    }

    [Theory]
    [InlineData("missing")] [InlineData("suspended")] [InlineData("lifecycle")] [InlineData("basis")] [InlineData("action")]
    public void T18_T19_T20_T21_T22_T23_MissingOrUntradableOutcomesRemainVisible(string kind)
    {
        using var f = new Fixture(); var input = Input(f); var o = input.Evidence.Outcomes![0];
        o = kind switch
        {
            "missing" => o with { Daily = null },
            "suspended" => o with { Daily = null, Session = T1OutcomeSession.SUSPENDED, Suspension = new(o.Symbol, o.SourceInstrumentId, o.TargetDate, "S", null, "fixture") },
            "lifecycle" => o with { Daily = null, Session = T1OutcomeSession.DELISTED },
            "basis" => o with { Basis = o.Basis! with { AdjustmentMode = HistoricalPriceAdjustmentMode.ForwardAdjusted } },
            _ => o with { CorporateAction = null }
        };
        var artifact = Build(input with { Evidence = input.Evidence with { Outcomes = new[] { o } } });
        var record = Record(artifact); var a = record.Candidates[0];
        Assert.Equal(5, record.Candidates.Count); Assert.False(a.AssociationUsableDeclared);
        Assert.Equal(o.TargetDate, record.Evidence.Calendar.NextTradingDate);
        if (kind == "missing") { Assert.Contains("MISSING_TARGET_DAILY_BAR", a.Outcome.Reasons); Assert.DoesNotContain("SUSPENDED", a.Outcome.Reasons); }
        if (kind == "suspended") { Assert.Contains("SUSPENDED", a.Outcome.Reasons); Assert.Equal(1, artifact.Content.Coverage.SupportedSuspensions); }
        if (kind == "lifecycle") { Assert.DoesNotContain("DELISTED", a.Outcome.Reasons); Assert.Contains("LIFECYCLE_EFFECTIVE_DATE_UNVERIFIED", a.Outcome.Reasons); }
        if (kind == "basis") Assert.Contains("PRICE_BASIS_MISMATCH", a.Outcome.Reasons);
        if (kind == "action") Assert.Contains("CORPORATE_ACTION_UNVERIFIED", a.Outcome.Reasons);
    }

    [Fact]
    public void T24_T25_ChangingOnlyNextDayOhlcCannotChangeDecisionAttributionOrPitClass()
    {
        using var f = new Fixture(); var input = Input(f); var first = Build(input);
        var changed = input.Evidence.Outcomes!.Select(o => o with { Daily = o.Daily! with { Open = 20, High = 22, Low = 18, Close = 21 } }).ToArray();
        var second = Build(input with { Evidence = input.Evidence with { Outcomes = changed } });
        var a = Record(first); var b = Record(second);
        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(a.Evidence.SnapshotFingerprint, b.Evidence.SnapshotFingerprint); Assert.Equal(a.Evidence.PackageFingerprint, b.Evidence.PackageFingerprint);
        Assert.Equal(a.Evidence.EvidenceClass, b.Evidence.EvidenceClass); Assert.Equal(a.Evidence.Captured, b.Evidence.Captured);
        Assert.Equal(a.Candidates.Select(c => (c.Original, c.T1Rank)), b.Candidates.Select(c => (c.Original, c.T1Rank)));
        Assert.NotEqual(a.Candidates[0].Outcome.EvidenceFingerprint, b.Candidates[0].Outcome.EvidenceFingerprint);
    }

    [Fact]
    public void T28_T29_T30_NoSamplesAndDuplicatePairsHaveTruthfulDenominators()
    {
        var empty = T1EvidenceReadiness.Associate(Array.Empty<T1AssociationInput>());
        Assert.Equal("NO_SAMPLE", empty.Content.Coverage.SourceCoverage.SampleState);
        Assert.Equal("NOT_APPLICABLE", empty.Content.Coverage.AssociationCoverage);
        using var f = new Fixture(); var input = Input(f);
        Assert.Throws<ArgumentException>(() => T1EvidenceReadiness.Associate(new[] { input, input }));
    }

    [Fact]
    public void T31_T32_DatesRunsAndOriginalStrategiesStayDistinct()
    {
        using var f = new Fixture(); var first = Input(f);
        var s = f.Snapshot with { TradingDate = f.Snapshot.TradingDate.AddDays(7), RequestedAsOf = f.Snapshot.RequestedAsOf.AddDays(7),
            AcquisitionStartedAtUtc = f.Snapshot.AcquisitionStartedAtUtc.AddDays(7), CapturedAtUtc = f.Snapshot.CapturedAtUtc.AddDays(7),
            Scan = f.Snapshot.Scan with { RunId = Guid.NewGuid(), Strategy = "V3", StrategyVersion = "v3", ScanStartedAtUtc = f.Snapshot.Scan.ScanStartedAtUtc.AddDays(7), ScanCompletedAtUtc = f.Snapshot.Scan.ScanCompletedAtUtc.AddDays(7) },
            Symbols = f.Snapshot.Symbols.Select(c => c with { Minutes = c.Minutes with { Rows = c.Minutes.Rows.Select(r => r with { ExchangeEventTime = r.ExchangeEventTime?.AddDays(7), ObservedAtUtc = r.ObservedAtUtc.AddDays(7), AvailableAtUtc = r.AvailableAtUtc?.AddDays(7), TransportReceivedAtUtc = r.TransportReceivedAtUtc?.AddDays(7) }).ToArray() } }).ToArray() };
        var path = new IntradaySnapshotStore(Path.Combine(f.Root, "second")).Save(s);
        var rank = T1PriceOnlyRanking.Rank(s, IntradayFeatureAnalysis.Analyze(s, IntradaySnapshotStore.Fingerprint(s), s.RequestedAsOf), 2);
        var package = T1RankingArtifact.Write(rank, Path.Combine(f.Root, "second-packages"), CancellationToken.None);
        var second = new T1AssociationInput(new(path, package.PackageDirectory, Origin: T1EvidenceOrigin.TEST_FIXTURE));
        var result = T1EvidenceReadiness.Associate(new[] { second, first });
        Assert.Equal(2, result.Content.Coverage.SourceCoverage.DistinctDecisionDates); Assert.Equal(2, result.Content.Coverage.SourceCoverage.DistinctRunIds);
        Assert.Equal(new[] { "V2", "V3" }, result.Content.Coverage.Breakdown.Select(b => b.Strategy));
        Assert.Equal(T1EvidenceReadiness.CanonicalAssociation(result), T1EvidenceReadiness.CanonicalAssociation(T1EvidenceReadiness.Associate(new[] { first, second })));
    }

    private static string IgnoredStorage(Fixture f)
    {
        string repo = Path.Combine(f.Root, "publication-repo"); Directory.CreateDirectory(repo);
        var start = new ProcessStartInfo("git") { WorkingDirectory = repo, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("init"); start.ArgumentList.Add("-q");
        using var p = Process.Start(start)!; p.WaitForExit(); Assert.Equal(0, p.ExitCode);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), "/local-research-data/\n");
        return Path.Combine(repo, "local-research-data", "associations");
    }

    [Fact]
    public void T33_T34_ImmutablePublicationReusesAndRejectsTampering()
    {
        using var f = new Fixture(); string output = IgnoredStorage(f); var artifact = Build(Input(f));
        var first = T1EvidenceReadiness.PublishAssociation(artifact, output);
        byte[] bytes = File.ReadAllBytes(Path.Combine(first.PackageDirectory, T1EvidenceReadiness.AssociationFileName));
        var second = T1EvidenceReadiness.PublishAssociation(artifact, output);
        Assert.True(second.ReusedExisting); Assert.Equal(first.PackageDirectory, second.PackageDirectory);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(second.PackageDirectory, T1EvidenceReadiness.AssociationFileName)));
        Assert.True(T1EvidenceReadiness.VerifyAssociationPackage(first.PackageDirectory));
        File.AppendAllText(Path.Combine(first.PackageDirectory, T1EvidenceReadiness.AssociationFileName), "tamper");
        Assert.False(T1EvidenceReadiness.VerifyAssociationPackage(first.PackageDirectory));
        Assert.Throws<InvalidDataException>(() => T1EvidenceReadiness.PublishAssociation(artifact, output));
    }

    [Theory]
    [InlineData("written", false)] [InlineData("publish", false)] [InlineData("written", true)] [InlineData("publish", true)]
    public void T35_T36_FailureOrCancellationPreservesPriorPublication(string boundary, bool cancel)
    {
        using var f = new Fixture(); string output = IgnoredStorage(f); var artifact = Build(Input(f));
        var first = T1EvidenceReadiness.PublishAssociation(artifact, output); var path = Path.Combine(first.PackageDirectory, T1EvidenceReadiness.AssociationFileName);
        byte[] original = File.ReadAllBytes(path);
        var different = Build(Input(f) with { Evidence = Input(f).Evidence with { Outcomes = null } });
        using var cts = new CancellationTokenSource();
        T1EvidenceReadiness.AssociationBoundaryHook = stage => { if (stage == boundary) { if (cancel) cts.Cancel(); else throw new IOException("fixture failure"); } };
        try
        {
            if (cancel) Assert.Throws<OperationCanceledException>(() => T1EvidenceReadiness.PublishAssociation(different, output, cts.Token));
            else Assert.Throws<IOException>(() => T1EvidenceReadiness.PublishAssociation(different, output));
        }
        finally { T1EvidenceReadiness.AssociationBoundaryHook = null; }
        Assert.Single(Directory.GetDirectories(output)); Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void T38_CliHelpAndNoDataPathStayOfflineAndNeverPrintPerformanceFields()
    {
        var writer = new StringWriter(); var error = new StringWriter();
        Assert.Equal(0, T1EvidenceReadiness.RunAssociationCommand(new Dictionary<string, string> { ["help"] = "true" }, writer, error));
        writer.GetStringBuilder().Clear();
        Assert.Equal(2, T1EvidenceReadiness.RunAssociationCommand(new Dictionary<string, string>(), writer, error));
        Assert.Contains("NO_AUTHORITATIVE_SAMPLE", writer.ToString()); Assert.Contains("CALENDAR_NOT_VERIFIED", writer.ToString());
        Assert.Contains("OUTCOME_SOURCE_NOT_VERIFIED", writer.ToString());
        string json = T1EvidenceReadiness.CanonicalAssociation(T1EvidenceReadiness.Associate(Array.Empty<T1AssociationInput>()));
        Assert.DoesNotContain("WinRate", json); Assert.DoesNotContain("ReturnPercent", json); Assert.DoesNotContain("PnL", json);
    }

    [Fact]
    public void OperationalFixtureFilesUseTheDocumentedOfflineCommandAndIgnoredOutput()
    {
        using var f = new Fixture(); var input = Input(f); string output = IgnoredStorage(f);
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        string calendar = Path.Combine(f.Root, "calendar.json"), outcomes = Path.Combine(f.Root, "outcomes.json");
        File.WriteAllText(calendar, JsonSerializer.Serialize(input.Evidence.Calendar, options));
        File.WriteAllText(outcomes, JsonSerializer.Serialize(new T1AssociationOutcomeInput(input.Evidence.Outcomes!, input.DecisionBases), options));
        var args = new Dictionary<string, string> { ["snapshot"] = f.SnapshotPath, ["ranking-package"] = f.Package.PackageDirectory,
            ["calendar"] = calendar, ["outcomes"] = outcomes, ["test-fixture"] = "true", ["output"] = output };
        var writer = new StringWriter(); var error = new StringWriter();
        Assert.Equal(2, T1EvidenceReadiness.RunAssociationCommand(args, writer, error));
        Assert.Empty(error.ToString()); Assert.Contains("PUBLICATION=PUBLISHED", writer.ToString());
        Assert.True(T1EvidenceReadiness.VerifyAssociationPackage(Assert.Single(Directory.GetDirectories(output))));
        writer.GetStringBuilder().Clear();
        Assert.Equal(2, T1EvidenceReadiness.RunAssociationCommand(args, writer, error));
        Assert.Contains("PUBLICATION=REUSED", writer.ToString());
        args["snapshot"] = Path.Combine(f.Root, "missing.json"); args.Remove("output");
        writer.GetStringBuilder().Clear();
        Assert.Equal(2, T1EvidenceReadiness.RunAssociationCommand(args, writer, error));
        Assert.Contains("NO_AUTHORITATIVE_SAMPLE", writer.ToString());
    }

    [Fact]
    public void RehashedDuplicateCandidateAndUnignoredOrSourceOutputAreRejected()
    {
        using var f = new Fixture(); var artifact = Build(Input(f)); var r = Record(artifact);
        var corrupt = artifact.Content with { Records = new[] { r with { Candidates = r.Candidates.Append(r.Candidates[0]).ToArray() } } };
        var json = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var forged = new T1AssociationArtifact(1, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(corrupt, json))), corrupt);
        Assert.False(T1EvidenceReadiness.VerifyAssociation(forged));
        string ignored = IgnoredStorage(f);
        Assert.Throws<InvalidDataException>(() => T1EvidenceReadiness.PublishAssociation(artifact, Path.GetDirectoryName(Path.GetDirectoryName(ignored)!)!));
        Assert.Throws<InvalidDataException>(() => T1EvidenceReadiness.PublishAssociation(artifact, ignored, protectedInputs: new[] { Path.GetDirectoryName(ignored)! }));
    }
}
