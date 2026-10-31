using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;
using Fixture = AIHelper.Services.Tests.T1EvidenceReadinessTests.Fixture;

namespace AIHelper.Services.Tests;

[Collection("T1ArtifactWrites")]
public sealed class T1OutcomePilotTests
{
    private sealed class Clock(bool beforeOutcome = false) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => beforeOutcome ? new(2026, 10, 31, 8, 0, 0, TimeSpan.Zero) : new(2026, 11, 7, 8, 0, 0, TimeSpan.Zero); }
    private sealed class Source : IHistoricalMarketDataSource
    {
        internal readonly List<(string Exchange, DateOnly Start, DateOnly End)> Calendars = new();
        internal readonly List<(string Id, DateOnly Start, DateOnly End)> Daily = new();
        internal string? MissingExchange, MissingSymbol, BadDaily;
        internal HistoricalSourceCapabilityStatus? Failure;
        internal bool Partial;
        public Task<HistoricalAcquisitionResult<HistoricalCalendarDay>> AcquireCalendarAsync(string exchange, DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
        {
            Calendars.Add((exchange, start, end));
            if (exchange == MissingExchange) return Task.FromResult(new HistoricalAcquisitionResult<HistoricalCalendarDay>(Array.Empty<HistoricalCalendarDay>()));
            var calendar = T1EvidenceReadinessTests.Calendar(exchange == "SZSE" ? start.AddDays(7) : start.AddDays(3));
            return Task.FromResult(calendar.Acquisition with { CoverageEvidence = calendar.Acquisition.CoverageEvidence.Select(e => e with { Scope = e.Scope with { Exchange = exchange } }).ToArray() });
        }
        public Task<HistoricalAcquisitionResult<HistoricalDailyPrice>> AcquireDailyPricesAsync(string tsCode, DateOnly start, DateOnly end, CancellationToken cancellationToken = default)
        {
            Daily.Add((tsCode, start, end));
            if (Failure is { } failure) throw new HistoricalHttpMarketDataSource.HistoricalSourceAccessException(failure, "fixture source observation");
            var bar = T1EvidenceReadinessTests.Outcome().Daily! with { Symbol = tsCode[..6], TsCode = tsCode, TradingDate = start };
            if (BadDaily == "date") bar = bar with { TradingDate = start.AddDays(1) };
            if (BadDaily == "symbol") bar = bar with { Symbol = "999999" };
            if (BadDaily == "basis") bar = bar with { AdjustmentMode = HistoricalPriceAdjustmentMode.ForwardAdjusted };
            if (BadDaily == "fields") bar = bar with { Low = 999 };
            var rows = bar.Symbol == MissingSymbol ? Array.Empty<HistoricalDailyPrice>() : new[] { bar };
            var coverage = T1EvidenceReadinessTests.Calendar().Acquisition.CoverageEvidence[0] with
            {
                Endpoint = "daily", Scope = new(start, end, Symbol: tsCode), ExpectedCount = 1, ObservedCount = rows.Length,
                ReturnedRows = rows.Length, MissingCount = 1 - rows.Length,
                RequestedChunks = new[] { new HistoricalCoverageChunk(start, end, rows.Length) },
                CoverageStatus = Partial ? HistoricalCoverageAcquisitionStatus.Partial : HistoricalCoverageAcquisitionStatus.Full
            };
            return Task.FromResult(new HistoricalAcquisitionResult<HistoricalDailyPrice>(rows, new[] { coverage }));
        }
        // These calls would escape the saved-pool scope. The pilot must never invoke them.
        public Task<IReadOnlyList<HistoricalSourceCapability>> ProbeAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("unbounded capability probe");
        public Task<IReadOnlyList<HistoricalSourceSecurity>> GetSecuritiesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("security master");
        public Task<IReadOnlyList<HistoricalCalendarDay>> GetCalendarAsync(string exchange, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("use acquisition envelope");
        public Task<IReadOnlyList<HistoricalDailyPrice>> GetDailyPricesAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("use acquisition envelope");
        public Task<IReadOnlyList<HistoricalTurnover>> GetTurnoverAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<HistoricalIndexDaily>> GetIndexDailyAsync(string indexCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<HistoricalSuspension>> GetSuspensionsAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException("market-wide suspension query forbidden");
        public Task<IReadOnlyList<HistoricalStStatus>> GetStStatusesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
        public Task<IReadOnlyList<HistoricalSourceAdjustmentFactor>> GetAdjustmentFactorsAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private static T1PilotRequest Authorized => new(true, "tushare", true, true);
    [Fact]
    public async Task T17_T18_T19_T27_T28_T29_T30_PerExchangeAcquisitionUsesOnlySavedPoolAndPreservesUnknowns()
    {
        using var f = new Fixture(); var archive = T1ResearchRetentionTests.Archive(f); var source = new Source();
        byte[] snapshot = File.ReadAllBytes(T1ResearchSamples.SnapshotPath(archive.Directory));
        byte[] ranking = File.ReadAllBytes(Path.Combine(T1ResearchSamples.RankingPath(archive.Directory, archive.Manifest), T1RankingArtifact.SidecarFileName));
        var evidence = await T1OutcomePilot.AcquireAsync(archive.Directory, Authorized, source, new Clock());
        Assert.Equal(3, source.Calendars.Count); Assert.Equal(5, source.Daily.Count);
        Assert.All(source.Calendars, c => { Assert.Equal(archive.Manifest.Content.TradingDate, c.Start); Assert.Equal(c.Start.AddDays(14), c.End); });
        Assert.All(source.Daily, d => { Assert.Equal(d.Start, d.End); Assert.Equal(d.Id.EndsWith(".SZ") ? new DateOnly(2026, 11, 6) : new DateOnly(2026, 11, 2), d.Start); });
        var a = T1OutcomePilot.Associate(archive.Directory, evidence);
        Assert.True(T1EvidenceReadiness.VerifyAssociation(a)); Assert.Equal(5, a.Content.Coverage.DeclaredAvailableBars);
        Assert.Equal(5, T1OutcomePilot.EstablishedCoverage(evidence, a)); Assert.Equal(0, a.Content.Coverage.UsableDeclaredAssociations);
        Assert.Equal(0, a.Content.Coverage.SourceCoverage.AuthenticatedProductionSamples);
        Assert.All(a.Content.Records[0].Candidates, c => { Assert.False(c.AssociationUsableDeclared); Assert.Equal(T1PriceBasisState.UNKNOWN_BASIS, c.Outcome.PriceBasis); Assert.Contains("SESSION_STATUS_UNVERIFIED", c.Reasons); });
        Assert.All(a.Content.Records[0].Derivation!.Outcomes, o => { Assert.Null(o.CorporateAction); Assert.Null(o.Suspension); Assert.Null(o.Security); Assert.Equal(T1OutcomeSession.UNKNOWN, o.Session); Assert.Equal(new Clock().GetUtcNow(), o.RetrievedAtUtc); });
        Assert.Equal(snapshot, File.ReadAllBytes(T1ResearchSamples.SnapshotPath(archive.Directory)));
        Assert.Equal(ranking, File.ReadAllBytes(Path.Combine(T1ResearchSamples.RankingPath(archive.Directory, archive.Manifest), T1RankingArtifact.SidecarFileName)));
    }
    [Fact]
    public async Task T20_MissingSzseCalendarDoesNotBlockOtherExchangesOrEraseCandidates()
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source { MissingExchange = "SZSE" };
        var evidence = await T1OutcomePilot.AcquireAsync(a.Directory, Authorized, source, new Clock());
        var result = T1OutcomePilot.Associate(a.Directory, evidence);
        Assert.Equal(3, source.Daily.Count); Assert.Equal(5, result.Content.Records[0].Candidates.Count);
        Assert.All(result.Content.Records[0].Candidates, c =>
        {
            if (c.Exchange == "SZSE") { Assert.Null(c.Calendar!.NextTradingDate); Assert.Contains("CALENDAR_NOT_VERIFIED", c.Reasons); }
            else Assert.Equal(T1ReadinessStatus.PASS, c.Calendar!.Status);
            Assert.DoesNotContain("SYMBOL_IDENTITY_MISMATCH", c.Reasons);
        });
    }
    [Fact]
    public async Task T21_T33_MissingBarDoesNotAdvanceTargetOrBecomeSuspension()
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source { MissingSymbol = "000001" };
        var evidence = await T1OutcomePilot.AcquireAsync(a.Directory, Authorized, source, new Clock()); var result = T1OutcomePilot.Associate(a.Directory, evidence);
        Assert.Equal(5, source.Daily.Count); var candidate = result.Content.Records[0].Candidates.Single(c => c.Original.Symbol == "000001");
        Assert.Equal(new DateOnly(2026, 11, 6), candidate.Calendar!.NextTradingDate);
        Assert.Equal(T1OutcomeSourceState.DATA_MISSING, candidate.Outcome.SourceState);
        Assert.Contains("MISSING_TARGET_DAILY_BAR", candidate.Reasons); Assert.DoesNotContain("SUSPENDED", candidate.Reasons);
        Assert.Equal(1, result.Content.Coverage.MissingBars); Assert.Equal(0, result.Content.Coverage.SupportedSuspensions);
    }
    [Theory]
    [InlineData("offline")] [InlineData("not-configured")] [InlineData("credential")]
    public async Task T22_T23_UnconfiguredCredentialsAndOfflineModesMakeNoProviderRequest(string kind)
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source();
        var request = kind switch { "offline" => Authorized with { AllowNetwork = false }, "not-configured" => Authorized with { Configured = false }, _ => Authorized with { CredentialAvailable = false } };
        var evidence = await T1OutcomePilot.AcquireAsync(a.Directory, request, source, new Clock());
        Assert.Empty(source.Calendars); Assert.Empty(source.Daily);
        Assert.Contains(kind switch { "offline" => "OFFLINE_NO_PROVIDER_CALLS", "not-configured" => "SOURCE_NOT_CONFIGURED", _ => "CREDENTIAL_REQUIRED" }, evidence.Content.Reasons);
        Assert.All(evidence.Content.Daily, d => Assert.Null(d.Acquisition));
    }
    [Theory]
    [InlineData(0)] [InlineData(32)]
    public async Task T24_DateScopeBoundsAreCheckedBeforeRequests(int days)
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source();
        await Assert.ThrowsAsync<ArgumentException>(() => T1OutcomePilot.AcquireAsync(a.Directory, Authorized with { CalendarDays = days }, source));
        Assert.Empty(source.Calendars); Assert.Empty(source.Daily);
    }
    [Fact]
    public async Task T24_MoreThanTwentyCapturedIdentitiesCannotReachProvider()
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source();
        var content = a.Manifest.Content with { Candidates = Enumerable.Repeat(a.Manifest.Content.Candidates[0], 21).ToArray() };
        File.WriteAllBytes(Path.Combine(a.Directory, T1ResearchSamples.ManifestFile), T1ResearchStorage.Bytes(a.Manifest with { Content = content, Fingerprint = T1ResearchStorage.Fingerprint(content) }));
        await Assert.ThrowsAsync<InvalidDataException>(() => T1OutcomePilot.AcquireAsync(a.Directory, Authorized, source));
        Assert.Empty(source.Calendars); Assert.Empty(source.Daily);
    }
    [Fact]
    public async Task T25_UnsupportedProviderIsRejectedWithoutProviderOrCredentialDiscovery()
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source();
        await Assert.ThrowsAsync<ArgumentException>(() => T1OutcomePilot.AcquireAsync(a.Directory, Authorized with { Provider = "other" }, source));
        Assert.Empty(source.Calendars); Assert.Empty(source.Daily);
    }
    [Theory]
    [InlineData(HistoricalSourceCapabilityStatus.PermissionDenied, T1OutcomeSourceState.PERMISSION_DENIED)]
    [InlineData(HistoricalSourceCapabilityStatus.RateLimited, T1OutcomeSourceState.RATE_LIMITED)]
    [InlineData(HistoricalSourceCapabilityStatus.Unavailable, T1OutcomeSourceState.SOURCE_ERROR)]
    public async Task T26_ObservedAccessFailuresKeepTheirActualClassification(HistoricalSourceCapabilityStatus failure, T1OutcomeSourceState state)
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source { Failure = failure };
        var evidence = await T1OutcomePilot.AcquireAsync(a.Directory, Authorized, source, new Clock());
        Assert.All(evidence.Content.Daily, d => Assert.Equal(state, d.State));
        Assert.Equal(5, source.Daily.Count); // one attempt per symbol; no retry or background polling
        Assert.True(T1EvidenceReadiness.VerifyAssociation(T1OutcomePilot.Associate(a.Directory, evidence)));
    }
    [Theory]
    [InlineData("date")] [InlineData("symbol")] [InlineData("basis")]
    public async Task ForeignDailyDataAndIncompatibleAdjustmentCannotBeImportedAsCorrectTarget(string kind)
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f);
        await Assert.ThrowsAsync<InvalidDataException>(() => T1OutcomePilot.AcquireAsync(a.Directory, Authorized, new Source { BadDaily = kind }, new Clock()));
    }
    [Fact]
    public async Task PartialCoverageAndInvalidFieldsCannotClaimEstablishedCoverage()
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f);
        var partial = await T1OutcomePilot.AcquireAsync(a.Directory, Authorized, new Source { Partial = true }, new Clock());
        Assert.Equal(0, T1OutcomePilot.EstablishedCoverage(partial, T1OutcomePilot.Associate(a.Directory, partial)));
        var invalid = await T1OutcomePilot.AcquireAsync(a.Directory, Authorized, new Source { BadDaily = "fields" }, new Clock());
        var writer = new StringWriter(); T1ResearchReport.Write(a.Directory, invalid, writer);
        Assert.Contains("OUTCOME_COVERAGE_ESTABLISHED=NOT_VERIFIED", writer.ToString());
        Assert.Equal(0, T1OutcomePilot.Associate(a.Directory, invalid).Content.Coverage.ValidatedFields);
    }
    [Fact]
    public async Task FutureTargetSessionIsNotPolledOrPretendedAvailable()
    {
        using var f = new Fixture(); var a = T1ResearchRetentionTests.Archive(f); var source = new Source();
        var evidence = await T1OutcomePilot.AcquireAsync(a.Directory, Authorized, source, new Clock(true));
        Assert.Equal(3, source.Calendars.Count); Assert.Empty(source.Daily); Assert.Contains("TARGET_SESSION_NOT_COMPLETED", evidence.Content.Reasons);
        Assert.All(evidence.Content.Daily, d => { Assert.NotNull(d.TargetDate); Assert.Null(d.Acquisition); });
        Assert.Equal(T1ReadinessStatus.BLOCKED, T1OutcomePilot.Associate(a.Directory, evidence).Content.Coverage.SourceCoverage.Overall);
    }
    [Fact]
    public async Task T31_T32_T34_FileBackedOfflineCliRetentionProbeImportAndInventoryAreOperational()
    {
        using var f = new Fixture(); var (input, features) = T1ResearchRetentionTests.Inputs(f);
        string featuresFile = Path.Combine(f.Root, "original-features.json"); File.WriteAllBytes(featuresFile, T1ResearchStorage.Bytes(features));
        var writer = new StringWriter(); var error = new StringWriter();
        var retain = new Dictionary<string, string> { ["retain-t1"] = "true", ["snapshot"] = input.Evidence.SnapshotPath!, ["ranking-package"] = input.Evidence.PackageDirectory!, ["features"] = featuresFile, ["archive-root"] = Path.Combine(f.Root, "archive"), ["test-fixture"] = "true" };
        Assert.Equal(2, await T1ResearchCommand.RunAsync(retain, writer, error)); Assert.Empty(error.ToString());
        string sample = Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, "archive"))); string output = T1OutcomeAssociationTests.IgnoredStorage(f);
        var probe = new Dictionary<string, string> { ["probe-t1"] = "true", ["sample"] = sample, ["output"] = output };
        writer.GetStringBuilder().Clear(); Assert.Equal(2, await T1ResearchCommand.RunAsync(probe, writer, error)); Assert.Contains("OFFLINE_NO_PROVIDER_CALLS", writer.ToString());
        var source = new Source(); var evidence = await T1OutcomePilot.AcquireAsync(sample, Authorized, source, new Clock());
        string saved = T1OutcomePilot.Publish(evidence, sample, output); byte[] original = File.ReadAllBytes(saved);
        probe["import"] = saved; writer.GetStringBuilder().Clear(); Assert.Equal(2, await T1ResearchCommand.RunAsync(probe, writer, error));
        Assert.Empty(error.ToString()); Assert.Contains("OUTCOME_COVERAGE_ESTABLISHED=SOURCE_DECLARED", writer.ToString());
        Assert.Contains("REAL_SAMPLE_NOT_YET_CAPTURED", writer.ToString()); Assert.Contains("AUTHENTICATED_PRODUCTION_SAMPLES=0", writer.ToString());
        Assert.Equal(original, File.ReadAllBytes(saved)); Assert.Equal(5, source.Daily.Count);
        Assert.All(Directory.GetDirectories(Path.Combine(output, "associations")), p => Assert.True(T1EvidenceReadiness.VerifyAssociationPackage(p)));
        writer.GetStringBuilder().Clear();
        Assert.Equal(2, await T1ResearchCommand.RunAsync(new Dictionary<string, string> { ["report-t1"] = "true", ["archive-root"] = Path.Combine(f.Root, "archive"), ["outcome-root"] = output }, writer, error));
        Assert.Contains("OBSERVED_NEXT_DAY_BARS=5/5", writer.ToString()); Assert.Contains("FIXTURES=1", writer.ToString());
        string report = writer.ToString(); foreach (string forbidden in new[] { "WinRate", "ReturnPercent", "Profitability", "Sharpe", "PnL" }) Assert.DoesNotContain(forbidden, report);
        writer.GetStringBuilder().Clear();
        Assert.Equal(2, await T1ResearchCommand.RunAsync(new Dictionary<string, string> { ["probe-t1"] = "true" }, writer, error));
        Assert.Contains("REAL_SAMPLE_NOT_YET_CAPTURED", writer.ToString()); Assert.Contains("NOT_APPLICABLE", writer.ToString());
        writer.GetStringBuilder().Clear(); T1ResearchReport.Inventory(Path.Combine(f.Root, "empty"), 25, writer); Assert.Contains("REAL_SAMPLE_NOT_YET_CAPTURED", writer.ToString());
    }
    [Theory]
    [InlineData("unknown-option")] [InlineData("two-modes")] [InlineData("network-import")]
    public async Task CliInvalidModesNeverFallThroughToDatasetAcquisition(string kind)
    {
        using var f = new Fixture(); var archive = T1ResearchRetentionTests.Archive(f);
        var args = new Dictionary<string, string> { ["probe-t1"] = "true", ["sample"] = archive.Directory, ["output"] = T1OutcomeAssociationTests.IgnoredStorage(f) };
        if (kind == "unknown-option") args["download-all"] = "true";
        if (kind == "two-modes") args["retain-t1"] = "true";
        if (kind == "network-import") { args["allow-network"] = "true"; args["import"] = "missing.json"; }
        Assert.Equal(1, await T1ResearchCommand.RunAsync(args, new StringWriter(), new StringWriter()));
    }
}
