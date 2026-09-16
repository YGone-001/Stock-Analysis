using System.Text.Json;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class HistoricalDatasetSchemaV2Tests
{
    [Fact]
    public async Task V2Loader_LoadsExplicitUniverseAndStructuredProvenance()
    {
        HistoricalDatasetFile file = FileModel();
        HistoricalDatasetLoadResult result = await Load(file);

        Assert.True(result.Success);
        HistoricalMarketDataset dataset = Assert.IsType<HistoricalMarketDataset>(result.Dataset);
        Assert.Equal(HistoricalDatasetJsonLoader.CurrentSchemaVersion, dataset.SchemaVersion);
        Assert.Equal(HistoricalUniverseQuality.Complete, dataset.Metadata.UniverseQuality);
        Assert.Equal(HistoricalPriceAdjustmentMode.ForwardAdjusted, dataset.PriceAdjustmentMode);
        Assert.Equal(HistoricalFieldCoverage.Full, dataset.GetFieldCapability(HistoricalField.OuterVolume)!.Coverage);
        Assert.Equal(3, dataset.Universes.Count);
    }

    [Fact]
    public async Task V2Loader_RejectsFullCoverageClaimWhenQuoteValueIsMissing()
    {
        HistoricalDatasetFile file = FileModel();
        file.Quotes![0].OuterVolume = null;
        HistoricalDatasetLoadResult result = await Load(file);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("OuterVolume", StringComparison.Ordinal));
    }

    [Fact]
    public async Task V2Loader_RejectsDuplicatePriceProvenanceAsMixedAdjustment()
    {
        HistoricalDatasetFile file = FileModel();
        file.PriceSeriesProvenance!.Add(new HistoricalPriceSeriesProvenance("600000", "second", HistoricalPriceAdjustmentMode.Raw));
        HistoricalDatasetLoadResult result = await Load(file);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("mixed adjustment", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task V2Loader_RejectsInvalidAdjustmentAndUnknownSecurityReference()
    {
        HistoricalDatasetFile invalidAdjustment = FileModel();
        invalidAdjustment.PriceSeriesProvenance![0] = invalidAdjustment.PriceSeriesProvenance[0] with { AdjustmentMode = (HistoricalPriceAdjustmentMode)99 };
        HistoricalDatasetLoadResult adjustmentResult = await Load(invalidAdjustment);
        Assert.False(adjustmentResult.Success);
        Assert.Contains(adjustmentResult.Errors, error => error.Contains("adjustment", StringComparison.OrdinalIgnoreCase));

        HistoricalDatasetFile unknownSymbol = FileModel();
        unknownSymbol.Quotes![0].Symbol = "999999";
        HistoricalDatasetLoadResult symbolResult = await Load(unknownSymbol);
        Assert.False(symbolResult.Success);
        Assert.Contains(symbolResult.Errors, error => error.Contains("historical security", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task V1Loader_RemainsReadableAndUsesDerivedPartialUniverse()
    {
        HistoricalDatasetFile file = FileModel();
        file.SchemaVersion = HistoricalDatasetJsonLoader.LegacySchemaVersion;
        file.Metadata = null; file.Securities = null; file.Universes = null; file.FieldCapabilities = null;
        file.PriceSeriesProvenance = null; file.MarketContextProvenance = null;
        file.Capabilities = HistoricalDataCapabilities.Complete;
        file.PriceAdjustmentMode = "ForwardAdjusted";
        HistoricalDatasetLoadResult result = await Load(file);

        Assert.True(result.Success);
        Assert.Equal(HistoricalDatasetJsonLoader.LegacySchemaVersion, result.Dataset!.SchemaVersion);
        Assert.Equal(HistoricalUniverseQuality.DerivedFromObservations, result.Dataset.Metadata.UniverseQuality);
        Assert.All(result.Dataset.Universes.Values, universe => Assert.NotEqual(HistoricalUniverseQuality.Complete, universe.Quality));
        Assert.Equal(HistoricalFieldOrigin.LegacyDeclared, result.Dataset.GetFieldCapability(HistoricalField.OuterVolume)!.Origin);
        Assert.All(result.Dataset.PriceSeriesProvenance.Values, value => Assert.Equal(HistoricalPriceAdjustmentMode.ForwardAdjusted, value.AdjustmentMode));
    }

    [Fact]
    public void UniverseAsOf_ExcludesFutureListingAndUsesExclusiveDelistingBoundary()
    {
        HistoricalMarketDataset dataset = RuntimeDataset();
        HistoricalUniverseResolver resolver = new();
        DateOnly beforeDelisting = dataset.TradingDates[0];
        DateOnly delistingEffectiveDate = dataset.TradingDates[1];

        HistoricalUniverseResolution before = resolver.GetUniverseAsOf(dataset, beforeDelisting);
        Assert.Equal(new[] { "600000", "600001" }, before.ActiveSecurities.Select(security => security.Symbol));
        Assert.Equal(HistoricalObservationStatus.NotYetListed, before.ExcludedStatuses["600002"]);

        HistoricalUniverseResolution boundary = resolver.GetUniverseAsOf(dataset, delistingEffectiveDate);
        Assert.DoesNotContain(boundary.ActiveSecurities, security => security.Symbol == "600001");
        Assert.Equal(HistoricalObservationStatus.Delisted, boundary.ExcludedStatuses["600001"]);
    }

    [Fact]
    public void SnapshotBuilder_PreservesMissingAndExplicitSuspendedObservationSemantics()
    {
        HistoricalMarketDataset dataset = RuntimeDataset(
            omitQuote: "600001",
            declarations: new[] { new HistoricalObservationDeclaration(new DateOnly(2025, 1, 3), "600000", HistoricalSecurityStatus.Suspended, HistoricalObservationStatus.Suspended, "Source declared halt.") });
        HistoricalSnapshotBuilder builder = new();

        HistoricalMarketSnapshot missingQuote = builder.Build(dataset, new DateOnly(2025, 1, 1));
        Assert.Contains(missingQuote.Observations, value => value.Security.Symbol == "600001" && value.ObservationStatus == HistoricalObservationStatus.MissingQuote);
        Assert.DoesNotContain(missingQuote.Securities, value => value.Security.Symbol == "600001");

        HistoricalMarketSnapshot suspended = builder.Build(dataset, new DateOnly(2025, 1, 3));
        Assert.Contains(suspended.Observations, value => value.Security.Symbol == "600000" && value.ObservationStatus == HistoricalObservationStatus.Suspended);
    }

    [Fact]
    public void SnapshotBuilder_ReportsMissingKlineWithoutCallingItSuspended()
    {
        HistoricalMarketDataset dataset = RuntimeDataset(omitKline: "600000");
        HistoricalMarketSnapshot snapshot = new HistoricalSnapshotBuilder().Build(dataset, dataset.TradingDates[0]);
        HistoricalSecurityObservation observation = Assert.Single(snapshot.Observations, value => value.Security.Symbol == "600000");
        Assert.Equal(HistoricalObservationStatus.MissingKline, observation.ObservationStatus);
        Assert.NotEqual(HistoricalSecurityStatus.Suspended, observation.SecurityStatus);
    }

    [Fact]
    public void V2Fingerprint_CoversSemanticFieldsButNotCreatedAt()
    {
        HistoricalMarketDataset baseline = RuntimeDataset();
        HistoricalMarketDataset createdAtChanged = RuntimeDataset(createdAt: DateTimeOffset.UtcNow.AddYears(1));
        HistoricalMarketDataset listingChanged = RuntimeDataset(listingDate: new DateOnly(2024, 12, 2));
        HistoricalMarketDataset delistingChanged = RuntimeDataset(delistingEffectiveDate: new DateOnly(2025, 1, 3));
        HistoricalMarketDataset outerVolumeChanged = RuntimeDataset(outerVolume: 99d);
        HistoricalMarketDataset contextChanged = RuntimeDataset(shanghaiPercent: 1.5);
        HistoricalMarketDataset capabilityChanged = RuntimeDataset(outerCoverage: HistoricalFieldCoverage.Partial);
        HistoricalMarketDataset adjustmentChanged = RuntimeDataset(adjustmentMode: HistoricalPriceAdjustmentMode.Raw);
        HistoricalMarketDataset universeChanged = RuntimeDataset(includeFutureSecurityInUniverse: false);

        Assert.Equal(baseline.Fingerprint, createdAtChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, listingChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, delistingChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, outerVolumeChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, contextChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, capabilityChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, adjustmentChanged.Fingerprint);
        Assert.NotEqual(baseline.Fingerprint, universeChanged.Fingerprint);
    }

    [Theory]
    [InlineData("600000", "Normal", true)]
    [InlineData("000001", "Normal", true)]
    [InlineData("300001", "Normal", true)]
    [InlineData("688001", "Normal", false)]
    [InlineData("600001", "*ST Sample", false)]
    [InlineData("430001", "Normal", false)]
    public void ClassicEligibility_IsSharedByLiveCompatibilityMethod(string symbol, string name, bool expected)
    {
        Assert.Equal(expected, SparrowClassicUniverseEligibility.Evaluate(symbol, name).Eligible);
        Assert.Equal(expected, SparrowClassicScanner.IsEligibleStock(symbol, name));
    }

    [Fact]
    public async Task V2Loader_PreservesCurrentUniverseFallbackAsNonCompleteQuality()
    {
        HistoricalDatasetFile file = FileModel();
        file.Metadata!.UniverseQuality = HistoricalUniverseQuality.CurrentUniverseFallback;
        file.Universes!.ForEach(universe => universe.Quality = HistoricalUniverseQuality.CurrentUniverseFallback);
        HistoricalDatasetLoadResult result = await Load(file);
        Assert.True(result.Success);
        Assert.Equal(HistoricalUniverseQuality.CurrentUniverseFallback, result.Dataset!.Metadata.UniverseQuality);
        Assert.Contains(result.Warnings, warning => warning.Contains("partial", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void V2Replay_SkipsPartialFieldObservationWithoutFailingOpen()
    {
        HistoricalMarketDataset dataset = RuntimeDataset(days: 70, partialOuterForSecondSecurity: true, outerCoverage: HistoricalFieldCoverage.Partial);
        DateOnly replayDate = dataset.TradingDates[^1];
        var request = new SparrowReplayRequest(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, replayDate, 10,
            V2Parameters: new SparrowV2ParameterSnapshot(false, 1, 5, 1.1, 1, false, 0, .15, 3, 30, 0, false));

        SparrowReplayResult result = new SparrowHistoricalReplayEngine().Replay(dataset, request);
        Assert.Equal(HistoricalReplaySupport.Supported, result.Support);
        Assert.DoesNotContain(result.Selections, selection => selection.Code == "600001");
        Assert.Contains(result.Warnings, warning => warning.Contains("600001", StringComparison.Ordinal));
    }

    private static HistoricalDatasetFile FileModel()
    {
        DateOnly[] dates = new[] { new DateOnly(2025, 1, 1), new DateOnly(2025, 1, 2), new DateOnly(2025, 1, 3) };
        return new HistoricalDatasetFile
        {
            SchemaVersion = HistoricalDatasetJsonLoader.CurrentSchemaVersion,
            DatasetId = "v2-fixture",
            Source = "legacy-compatible-root",
            Metadata = new HistoricalDatasetMetadataFile { DatasetId = "v2-fixture", Source = "fixture", CreatedAt = DateTimeOffset.UtcNow, UniverseQuality = HistoricalUniverseQuality.Complete },
            TradingDates = dates.ToList(),
            Securities = new List<HistoricalSecurityFile>
            {
                new() { Symbol = "600000", Name = "Alpha", SecurityType = HistoricalSecurityType.Stock, Market = HistoricalSecurityMarket.Shanghai, ListingDate = new(2020, 1, 1), LifecycleQuality = HistoricalLifecycleQuality.Complete },
                new() { Symbol = "600001", Name = "Beta", SecurityType = HistoricalSecurityType.Stock, Market = HistoricalSecurityMarket.Shanghai, ListingDate = new(2020, 1, 1), DelistingEffectiveDate = new(2026, 1, 1), LifecycleQuality = HistoricalLifecycleQuality.Complete }
            },
            Universes = dates.Select(date => new HistoricalUniverseSnapshotFile { TradingDate = date, SecuritySymbols = new List<string> { "600000", "600001" }, Quality = HistoricalUniverseQuality.Complete, Source = "fixture" }).ToList(),
            FieldCapabilities = FullCapabilities(),
            Quotes = dates.SelectMany(date => new[] { Quote(date, "600000", "Alpha", 20), Quote(date, "600001", "Beta", 20) }).ToList(),
            Klines = new List<HistoricalKlineSeriesFile>
            {
                new() { Symbol = "600000", Bars = dates.Select((date, index) => Bar(date, 10 + index)).ToList() },
                new() { Symbol = "600001", Bars = dates.Select((date, index) => Bar(date, 10 + index)).ToList() }
            },
            PriceSeriesProvenance = new List<HistoricalPriceSeriesProvenance>
            {
                new("600000", "fixture", HistoricalPriceAdjustmentMode.ForwardAdjusted), new("600001", "fixture", HistoricalPriceAdjustmentMode.ForwardAdjusted)
            },
            MarketContexts = new List<HistoricalMarketContextFile>(),
            MarketContextProvenance = new List<HistoricalMarketContextProvenance>(),
            ObservationDeclarations = new List<HistoricalObservationDeclaration>()
        };
    }

    private static HistoricalMarketDataset RuntimeDataset(int days = 3, string? omitQuote = null, string? omitKline = null, IEnumerable<HistoricalObservationDeclaration>? declarations = null, DateTimeOffset? createdAt = null, DateOnly? listingDate = null, DateOnly? delistingEffectiveDate = null, double outerVolume = 20, double shanghaiPercent = .5, HistoricalFieldCoverage outerCoverage = HistoricalFieldCoverage.Full, bool partialOuterForSecondSecurity = false, HistoricalPriceAdjustmentMode adjustmentMode = HistoricalPriceAdjustmentMode.ForwardAdjusted, bool includeFutureSecurityInUniverse = true)
    {
        DateOnly start = new(2025, 1, 1);
        DateOnly[] dates = Enumerable.Range(0, days).Select(index => start.AddDays(index)).ToArray();
        HistoricalSecurity[] securities =
        {
            new("600000", "Alpha", HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai, listingDate ?? new DateOnly(2020, 1, 1), LifecycleQuality: HistoricalLifecycleQuality.Complete),
            new("600001", "Beta", HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai, new DateOnly(2020, 1, 1), delistingEffectiveDate ?? new DateOnly(2025, 1, 2), HistoricalLifecycleQuality.Complete),
            new("600002", "Future", HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai, new DateOnly(2025, 1, 3), LifecycleQuality: HistoricalLifecycleQuality.ListingKnown)
        };
        string[] symbols = securities.Select(security => security.Symbol).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => symbols.Where(symbol => symbol != omitQuote).Select(symbol =>
            new HistoricalQuoteObservation(date, new QuoteSnapshot(symbol, securities.Single(security => security.Symbol == symbol).Name, 10.3, 10, 3, 1, 100_000_000, 10, symbol == "600001" && partialOuterForSecondSecurity ? null : outerVolume, 10, 10, 10.4, 9.9)))).ToArray();
        KlineSeries[] klines = symbols.Where(symbol => symbol != omitKline).Select(symbol => new KlineSeries(symbol, dates.Select((date, index) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), 10 + index * .02, 10.2 + index * .02, 9.9 + index * .02, 10 + index * .02, 1, 1, null, null, null)).ToArray())).ToArray();
        string[] universeSymbols = includeFutureSecurityInUniverse ? symbols : symbols.Where(symbol => symbol != "600002").ToArray();
        HistoricalUniverseSnapshot[] universes = dates.Select(date => new HistoricalUniverseSnapshot(date, universeSymbols, HistoricalUniverseQuality.Complete, "fixture")).ToArray();
        HistoricalMarketContext[] contexts = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, shanghaiPercent)).ToArray();
        HistoricalPriceSeriesProvenance[] price = symbols.Select(symbol => new HistoricalPriceSeriesProvenance(symbol, "fixture", adjustmentMode, dates[0], dates[^1])).ToArray();
        HistoricalMarketContextProvenance[] contextProvenance = dates.Select(date => new HistoricalMarketContextProvenance(date, HistoricalFieldOrigin.Observed, "fixture", HistoricalFieldOrigin.Observed, "fixture")).ToArray();
        List<HistoricalFieldCapability> capabilities = FullCapabilities();
        capabilities[5] = capabilities[5] with { Coverage = outerCoverage };
        return new HistoricalMarketDataset("runtime-v2", dates, quotes, klines, contexts, null, adjustmentMode.ToString(), "fixture", 2,
            new HistoricalDatasetMetadata("runtime-v2", "fixture", createdAt ?? new DateTimeOffset(2025, 1, 4, 0, 0, 0, TimeSpan.Zero), HistoricalUniverseQuality.Complete),
            securities, universes, capabilities, price, contextProvenance, declarations);
    }

    private static List<HistoricalFieldCapability> FullCapabilities() => new()
    {
        new(HistoricalField.Price, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
        new(HistoricalField.PreviousClose, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full),
        new(HistoricalField.ChangePercent, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full, HistoricalValueUnit.Percentage),
        new(HistoricalField.Amount, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full, HistoricalValueUnit.CurrencyBaseUnit),
        new(HistoricalField.Turnover, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full, HistoricalValueUnit.Percentage),
        new(HistoricalField.OuterVolume, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full, HistoricalValueUnit.Hands),
        new(HistoricalField.InnerVolume, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full, HistoricalValueUnit.Hands)
    };
    private static HistoricalQuoteFile Quote(DateOnly date, string symbol, string name, double outer) => new() { TradingDate = date, Symbol = symbol, Name = name, Price = 10.3, PreviousClose = 10, ChangePercent = 3, Volume = 1, Amount = 100, Turnover = 10, OuterVolume = outer, InnerVolume = 10 };
    private static HistoricalKlineBarFile Bar(DateOnly date, double close) => new() { Date = date.ToDateTime(TimeOnly.MinValue), Open = close - .1, High = close + .1, Low = close - .2, Close = close, Volume = 1, Amount = 1, ChangePercent = 1, Change = .1, TurnoverRate = 1 };
    private static async Task<HistoricalDatasetLoadResult> Load(HistoricalDatasetFile file)
    {
        string path = Path.Combine(Path.GetTempPath(), $"aihelper-v2-{Guid.NewGuid():N}.json");
        try { await File.WriteAllTextAsync(path, JsonSerializer.Serialize(file)); return await new HistoricalDatasetJsonLoader().LoadAsync(path); }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
