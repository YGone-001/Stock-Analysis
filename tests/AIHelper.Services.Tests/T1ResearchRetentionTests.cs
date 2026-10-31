using System.Diagnostics;
using System.IO;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Xunit;
using Fixture = AIHelper.Services.Tests.T1EvidenceReadinessTests.Fixture;

namespace AIHelper.Services.Tests;

[Collection("T1ArtifactWrites")]
public sealed class T1ResearchRetentionTests
{
    internal static (T1AssociationInput Input, IReadOnlyList<IntradayFeatureResult> Features) Inputs(Fixture f, IntradaySnapshot? snapshot = null)
    {
        if (snapshot is null)
        {
            var input = T1AssociationCorrectionTests.Mixed(f);
            snapshot = T1EvidenceReadiness.VerifySnapshot(input.Evidence.SnapshotPath).Snapshot!;
            return (input, IntradayFeatureAnalysis.Analyze(snapshot, IntradaySnapshotStore.Fingerprint(snapshot), snapshot.RequestedAsOf));
        }
        string path = new IntradaySnapshotStore(Path.Combine(f.Root, "input-" + snapshot.Scan.RunId.ToString("N"))).Save(snapshot);
        var features = IntradayFeatureAnalysis.Analyze(snapshot, IntradaySnapshotStore.Fingerprint(snapshot), snapshot.RequestedAsOf);
        var package = T1RankingArtifact.Write(T1PriceOnlyRanking.Rank(snapshot, features, 2), Path.Combine(f.Root, "packages"), CancellationToken.None);
        return (new(new(path, package.PackageDirectory, Origin: T1EvidenceOrigin.TEST_FIXTURE)), features);
    }
    internal static T1ResearchArchive Archive(Fixture f)
    {
        var (input, features) = Inputs(f);
        return T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory!, features, Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE);
    }
    [Fact]
    public void T01_T02_T03_T05_T15_ArchivePreservesOriginalBytesIdentityAndActualTiming()
    {
        using var f = new Fixture(); var (input, features) = Inputs(f);
        var snapshot = T1EvidenceReadiness.VerifySnapshot(input.Evidence.SnapshotPath).Snapshot!;
        var timing = new T1ResearchTimings(snapshot.CapturedAtUtc.AddSeconds(1), snapshot.CapturedAtUtc.AddSeconds(2), snapshot.CapturedAtUtc.AddSeconds(3));
        string root = Path.Combine(f.Root, "archive");
        var a = T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory!, features, root, T1EvidenceOrigin.TEST_FIXTURE, timing);
        var b = T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory! + Path.DirectorySeparatorChar, features, root, T1EvidenceOrigin.TEST_FIXTURE, timing);
        Assert.True(b.ReusedExisting); Assert.Equal(a.Directory, b.Directory); Assert.Equal(a.Manifest.ArchivedAtUtc, b.Manifest.ArchivedAtUtc);
        Assert.Equal(File.ReadAllBytes(input.Evidence.SnapshotPath!), File.ReadAllBytes(T1ResearchSamples.SnapshotPath(a.Directory)));
        foreach (var name in new[] { T1RankingArtifact.CsvFileName, T1RankingArtifact.SidecarFileName })
            Assert.Equal(File.ReadAllBytes(Path.Combine(input.Evidence.PackageDirectory!, name)), File.ReadAllBytes(Path.Combine(T1ResearchSamples.RankingPath(a.Directory, a.Manifest), name)));
        var m = T1ResearchSamples.Verify(a.Directory).Content;
        Assert.Equal(a.Manifest.Fingerprint, T1ResearchSamples.Verify(a.Directory + Path.DirectorySeparatorChar).Fingerprint);
        Assert.True(T1RankingArtifact.Verify(T1ResearchSamples.RankingPath(a.Directory, a.Manifest)).IsValid);
        Assert.Equal(snapshot.Scan.RunId, m.RunId); Assert.Equal(snapshot.Scan.ParameterFingerprint, m.ParameterFingerprint);
        Assert.Equal(snapshot.Scan.ScanStartedAtUtc, m.ScanStartedAtUtc); Assert.Equal(snapshot.Scan.ScanCompletedAtUtc, m.ScanCompletedAtUtc);
        Assert.Equal(snapshot.RequestedAsOf, m.RequestedCutoff); Assert.Equal(snapshot.AcquisitionStartedAtUtc, m.AcquisitionStartedAtUtc);
        Assert.Equal(snapshot.CapturedAtUtc, m.CapturedAtUtc); Assert.Equal(timing, m.Timings);
        Assert.Equal(new T1RankingCoverage(5, 4, 1, 2, 2), m.Coverage); Assert.Equal(5, m.Candidates.Count);
        Assert.Equal(snapshot.Symbols.Select(s => (s.Symbol, s.Rank, s.Score)), m.Candidates.Select(c => (c.Original.Symbol, c.Original.OriginalRank, c.Original.OriginalScore)));
    }
    [Fact]
    public void T04_ArchiveSurvivesOperationalHundredSnapshotCleanup()
    {
        using var f = new Fixture();
        var features = IntradayFeatureAnalysis.Analyze(f.Snapshot, IntradaySnapshotStore.Fingerprint(f.Snapshot), f.Snapshot.RequestedAsOf);
        var a = T1ResearchSamples.Retain(f.SnapshotPath, f.Package.PackageDirectory, features, Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE);
        var store = new IntradaySnapshotStore(f.Root);
        for (int i = 0; i < 105; i++) store.Save(f.Snapshot with { Scan = f.Snapshot.Scan with { RunId = Guid.NewGuid() } });
        Assert.False(File.Exists(f.SnapshotPath)); Assert.Equal(100, Directory.GetFiles(f.Root, "*.json").Length);
        Assert.Equal(a.Manifest.Fingerprint, T1ResearchSamples.Verify(a.Directory).Fingerprint);
    }
    [Fact]
    public void T06_DivergentRankingUnderSameRunCannotReplaceArchive()
    {
        using var f = new Fixture(); var a = Archive(f); var (input, features) = Inputs(f);
        var source = T1EvidenceReadiness.VerifySnapshot(input.Evidence.SnapshotPath).Snapshot!;
        var ranking = T1RankingArtifact.Write(T1PriceOnlyRanking.Rank(source, features, 1), Path.Combine(f.Root, "packages"), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, ranking.PackageDirectory, features,
            Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE));
        Assert.Equal(a.Manifest.Fingerprint, T1ResearchSamples.Verify(a.Directory).Fingerprint);
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, "archive")));
    }
    [Theory]
    [InlineData("precancel", false)] [InlineData("copied", false)] [InlineData("publish", false)]
    [InlineData("copied", true)] [InlineData("publish", true)]
    public void T07_T08_CancellationAndFailurePublishNoPartialAndPreservePriorSamples(string boundary, bool fail)
    {
        using var f = new Fixture(); var a = Archive(f);
        var (input, features) = Inputs(f, f.Snapshot with { Scan = f.Snapshot.Scan with { RunId = Guid.Parse("22222222-2222-2222-2222-222222222222") } });
        using var cts = new CancellationTokenSource();
        if (boundary == "precancel") cts.Cancel();
        T1ResearchSamples.BoundaryHook = stage => { if (stage == boundary) { if (fail) throw new IOException("fixture"); cts.Cancel(); } };
        try
        {
            var error = Record.Exception(() => T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory!, features,
                Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE, token: cts.Token));
            if (fail) Assert.IsType<IOException>(error); else Assert.IsType<OperationCanceledException>(error);
        }
        finally { T1ResearchSamples.BoundaryHook = null; }
        Assert.Equal(a.Manifest.Fingerprint, T1ResearchSamples.Verify(a.Directory).Fingerprint);
        Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, "archive")));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.Root, "archive"))); // no run lock or partial file survives
    }
    [Fact]
    public void T11_MissingFeaturesAreNeverSilentlyRecomputed()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => T1ResearchSamples.Retain(f.SnapshotPath, f.Package.PackageDirectory, Array.Empty<IntradayFeatureResult>(), Path.Combine(f.Root, "archive")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "archive")));
    }
    [Fact]
    public void T12_T13_UnknownUnitsIntervalAndPriceBasisStayUnknown()
    {
        using var f = new Fixture();
        var s = f.Snapshot with { Symbols = f.Snapshot.Symbols.Select(c => c with { Minutes = c.Minutes with { Rows = c.Minutes.Rows.Select(r => r with
            { VolumeUnit = "unknown", AmountUnit = "unknown", Interval = "source-labelled minute; interval closure unverified", EventTimeSemanticsVerified = false }).ToArray() } }).ToArray() };
        var (input, features) = Inputs(f, s);
        var a = T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory!, features, Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE);
        Assert.All(a.Manifest.Content.Candidates, c => { Assert.False(c.IntervalClosureVerified); Assert.Equal("UNKNOWN_BASIS", c.PriceBasis); if (c.MinuteObservations > 0) { Assert.Equal(new[] { "unknown" }, c.VolumeUnits); Assert.Equal(new[] { "unknown" }, c.AmountUnits); } });
        Assert.Null(a.Manifest.Content.Timings.RankingCompletedAtUtc); Assert.Null(a.Manifest.Content.Timings.FeatureCompletedAtUtc);
    }
    [Fact]
    public void T14_T16_PostCutoffAndOutOfWindowCaptureCannotBeBackdatedOrCertified()
    {
        using var f = new Fixture();
        var s = f.Snapshot with { CapturedAtUtc = f.Snapshot.CapturedAtUtc.AddMinutes(20), Symbols = f.Snapshot.Symbols.Select(c => c with
            { Minutes = c.Minutes with { Rows = c.Minutes.Rows.Select(r => r with { TransportReceivedAtUtc = r.TransportReceivedAtUtc?.AddMinutes(30) }).ToArray() } }).ToArray() };
        var (input, features) = Inputs(f, s);
        var a = T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory!, features, Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE);
        Assert.True(a.Manifest.Content.OutsideStudyWindow); Assert.Equal(s.CapturedAtUtc, a.Manifest.Content.CapturedAtUtc);
        Assert.Equal(s.RequestedAsOf, a.Manifest.Content.RequestedCutoff);
        Assert.DoesNotContain(a.Manifest.Content.EvidenceClass, new[] { T1EvidenceClass.LIVE_RECORDED_PIT, T1EvidenceClass.ARCHIVED_PIT_REPLAY });
        Assert.All(a.Manifest.Content.Candidates.Where(c => c.MinuteObservations > 0), c => Assert.Equal(T1ReadinessStatus.FAIL, c.CutoffConsistency));
    }
    [Theory]
    [InlineData("snapshot")] [InlineData("csv")] [InlineData("json")] [InlineData("features")] [InlineData("manifest")] [InlineData("extra")]
    public void ArchiveIndependentVerificationRejectsChangedComponents(string component)
    {
        using var f = new Fixture(); var a = Archive(f);
        string path = component switch
        {
            "snapshot" => T1ResearchSamples.SnapshotPath(a.Directory), "features" => Path.Combine(a.Directory, T1ResearchSamples.FeaturesFile),
            "manifest" => Path.Combine(a.Directory, T1ResearchSamples.ManifestFile),
            "extra" => Path.Combine(a.Directory, "unexpected.txt"),
            _ => Path.Combine(T1ResearchSamples.RankingPath(a.Directory, a.Manifest), component == "csv" ? T1RankingArtifact.CsvFileName : T1RankingArtifact.SidecarFileName)
        };
        File.AppendAllText(path, "bad"); Assert.NotNull(Record.Exception(() => T1ResearchSamples.Verify(a.Directory)));
    }
    [Fact]
    public void ArchiveDirectoryIdentityMustMatchManifest()
    {
        using var f = new Fixture(); var a = Archive(f); string renamed = Path.Combine(f.Root, "archive", "wrong-identity");
        Assert.StartsWith(Path.GetFullPath(f.Root), Path.GetFullPath(renamed)); Assert.StartsWith(Path.GetFullPath(f.Root), Path.GetFullPath(a.Directory));
        Directory.Move(a.Directory, renamed);
        Assert.Throws<InvalidDataException>(() => T1ResearchSamples.Verify(renamed));
    }
    [Fact]
    public void ArchiveBoundsAndTraversalFailBeforePublication()
    {
        using var f = new Fixture();
        Assert.Throws<InvalidDataException>(() => T1ResearchStorage.Root(Path.Combine(f.Root, "..", "escape")));
        using (var file = File.Create(Path.Combine(f.Root, "oversized.json"))) file.SetLength(IntradaySnapshotStore.MaximumBytes + 1L);
        Assert.Throws<InvalidDataException>(() => T1ResearchStorage.Read(Path.Combine(f.Root, "oversized.json")));
        string ignored = T1OutcomeAssociationTests.IgnoredStorage(f);
        Assert.Throws<InvalidDataException>(() => T1ResearchStorage.Root(Path.GetDirectoryName(Path.GetDirectoryName(ignored)!)!));
    }
    [Fact]
    public void ArchiveRejectsDirectoryJunctionAndPreservesItsTarget()
    {
        using var f = new Fixture(); string target = Path.Combine(f.Root, "target"), link = Path.Combine(f.Root, "link"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep"), "unchanged");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; process.WaitForExit(); Assert.Equal(0, process.ExitCode);
        try { Assert.Throws<InvalidDataException>(() => T1ResearchStorage.Root(link)); Assert.Equal("unchanged", File.ReadAllText(Path.Combine(target, "keep"))); }
        finally { Directory.Delete(link); }
    }
    [Theory]
    [InlineData("new-scan")] [InlineData("close")] [InlineData("cancel")]
    public void T09_T10_LateWpfRetentionCannotPublishAfterOwnershipIsLost(string interruption)
    {
        using var f = new Fixture();
        SparrowWindowRealUiTests.RunWindowTest((window, vm) =>
        {
            T1RankingTests.Configure(vm, Path.Combine(f.Root, "wpf-snapshots")); vm.ResearchOrigin = T1EvidenceOrigin.TEST_FIXTURE;
            vm.T1ExportDirectory = Path.Combine(f.Root, "wpf-ranking"); vm.ResearchArchiveRoot = Path.Combine(f.Root, "wpf-archive");
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { new SparrowRankedCandidate
                { Code = "600001", Name = "first", Rank = 1, RankingProfile = "fixture", Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } } });
            T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null)); Assert.False(vm.RetainT1ResearchSampleCommand.CanExecute(null));
            T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null)); T1RankingTests.Pump(vm.RunT1RankingCommand.ExecuteAsync(null));
            T1RankingTests.Pump(vm.ExportT1RankingCommand.ExecuteAsync(null)); Assert.True(vm.RetainT1ResearchSampleCommand.CanExecute(null));
            string source = vm.LastSnapshotPath!;
            var package = Assert.Single(Directory.GetDirectories(vm.T1ExportDirectory));
            using var staged = T1ResearchSamples.Prepare(source, package, vm.LastFeatureResults!, vm.ResearchArchiveRoot, T1EvidenceOrigin.TEST_FIXTURE);
            var pending = new TaskCompletionSource<T1PreparedResearchSample>(); CancellationToken captured = default;
            vm.ResearchPrepareOverride = token => { captured = token; return pending.Task; };
            var task = vm.RetainT1ResearchSampleCommand.ExecuteAsync(null);
            if (interruption == "new-scan") T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null));
            else if (interruption == "close") window.Close(); else vm.CancelScanCommand.Execute(null);
            Assert.True(captured.IsCancellationRequested); int updates = 0; vm.PropertyChanged += (_, _) => updates++;
            pending.SetResult(staged); T1RankingTests.Pump(task);
            if (interruption == "close") Assert.Equal(0, updates);
            Assert.Empty(Directory.GetDirectories(vm.ResearchArchiveRoot));
        });
    }
    [Fact]
    public void WpfOperatorCanRetainCompletedExportAndMissingFeaturesStayDisabled()
    {
        using var f = new Fixture();
        SparrowWindowRealUiTests.RunWindowTest((_, vm) =>
        {
            T1RankingTests.Configure(vm, Path.Combine(f.Root, "wpf-snapshots")); vm.ResearchOrigin = T1EvidenceOrigin.TEST_FIXTURE;
            vm.T1ExportDirectory = Path.Combine(f.Root, "wpf-ranking"); vm.ResearchArchiveRoot = Path.Combine(f.Root, "wpf-archive");
            vm.ScanOverride = (_, _) => Task.FromResult<IReadOnlyList<SparrowRankedCandidate>>(new[] { new SparrowRankedCandidate
                { Code = "600001", Name = "first", Rank = 1, RankingProfile = "fixture", Features = new SparrowRankingFeatures { Code = "600001", Name = "first" } } });
            T1RankingTests.Pump(vm.StartCommand.ExecuteAsync(null)); T1RankingTests.Pump(vm.AnalyzeFeaturesCommand.ExecuteAsync(null));
            T1RankingTests.Pump(vm.RunT1RankingCommand.ExecuteAsync(null)); T1RankingTests.Pump(vm.ExportT1RankingCommand.ExecuteAsync(null));
            T1RankingTests.Pump(vm.RetainT1ResearchSampleCommand.ExecuteAsync(null));
            string archive = Assert.Single(Directory.GetDirectories(vm.ResearchArchiveRoot));
            var m = T1ResearchSamples.Verify(archive); Assert.Equal(vm.LastSnapshotRunId, m.Content.RunId);
            Assert.NotNull(m.Content.Timings.FeatureCompletedAtUtc); Assert.NotNull(m.Content.Timings.RankingCompletedAtUtc); Assert.NotNull(m.Content.Timings.ExportCompletedAtUtc);
            Assert.Contains("已留存", vm.ResearchRetentionStatus);
            var pending = new TaskCompletionSource<IReadOnlyList<IntradayFeatureResult>>(); vm.FeatureAnalysisOverride = (_, _, _, _) => pending.Task;
            var analyzing = vm.AnalyzeFeaturesCommand.ExecuteAsync(null); Assert.False(vm.RetainT1ResearchSampleCommand.CanExecute(null)); Assert.False(vm.ExportT1RankingCommand.CanExecute(null));
            pending.SetResult(Array.Empty<IntradayFeatureResult>()); T1RankingTests.Pump(analyzing);
            Assert.False(vm.RetainT1ResearchSampleCommand.CanExecute(null));
        });
    }
}
