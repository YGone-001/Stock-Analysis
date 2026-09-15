using AIHelper.Core.StockData;
using AIHelper.Models;

namespace AIHelper.Core.Sparrow;

public enum HistoricalReplaySupport { Supported, Partial, Unsupported }

public sealed record HistoricalDataCapabilities(
    bool HasAmount,
    bool HasTurnover,
    bool HasOuterVolume,
    bool HasInnerVolume,
    bool HasClassicMarketRegime,
    bool HasV2ShanghaiDailyPercent)
{
    public static HistoricalDataCapabilities Complete { get; } = new(true, true, true, true, true, true);
}

public sealed record HistoricalQuoteObservation(DateOnly TradingDate, QuoteSnapshot Quote);
public sealed record HistoricalMarketContext(
    DateOnly TradingDate,
    SparrowMarketRegime? ClassicMarketRegime = null,
    double? V2ShanghaiDailyPercent = null);

/// <summary>Immutable, preloaded research data. It never performs live market-data I/O.</summary>
public sealed class HistoricalMarketDataset
{
    public HistoricalMarketDataset(
        string datasetId,
        IEnumerable<DateOnly> tradingDates,
        IEnumerable<HistoricalQuoteObservation> quotes,
        IEnumerable<KlineSeries> klineSeries,
        IEnumerable<HistoricalMarketContext>? marketContexts = null,
        HistoricalDataCapabilities? capabilities = null,
        string priceAdjustmentMode = "Unknown",
        string source = "InMemory")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(datasetId);
        DatasetId = datasetId;
        TradingDates = tradingDates.Distinct().OrderBy(date => date).ToArray();
        if (TradingDates.Count == 0) throw new ArgumentException("Dataset requires at least one trading date.", nameof(tradingDates));
        if (TradingDates.Zip(TradingDates.Skip(1)).Any(pair => pair.First >= pair.Second)) throw new ArgumentException("Trading dates must be ascending and unique.", nameof(tradingDates));
        Quotes = quotes.GroupBy(item => (item.TradingDate, item.Quote.Symbol), StringComparerTuple.Ordinal)
            .ToDictionary(group => group.Key, group => group.Single().Quote, StringComparerTuple.Ordinal);
        Klines = klineSeries.ToDictionary(series => series.Symbol, StringComparer.Ordinal);
        MarketContexts = (marketContexts ?? Array.Empty<HistoricalMarketContext>())
            .ToDictionary(context => context.TradingDate);
        Capabilities = capabilities ?? HistoricalDataCapabilities.Complete;
        PriceAdjustmentMode = priceAdjustmentMode;
        Source = source;
        Fingerprint = SparrowHistoricalFingerprint.Dataset(this);
    }

    public string DatasetId { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<DateOnly> TradingDates { get; }
    public IReadOnlyDictionary<(DateOnly Date, string Symbol), QuoteSnapshot> Quotes { get; }
    public IReadOnlyDictionary<string, KlineSeries> Klines { get; }
    public IReadOnlyDictionary<DateOnly, HistoricalMarketContext> MarketContexts { get; }
    public HistoricalDataCapabilities Capabilities { get; }
    public string PriceAdjustmentMode { get; }
    public string Source { get; }
    public bool TryGetQuote(DateOnly date, string symbol, out QuoteSnapshot quote) => Quotes.TryGetValue((date, symbol), out quote!);

    private sealed class StringComparerTuple : IEqualityComparer<(DateOnly Date, string Symbol)>
    {
        public static StringComparerTuple Ordinal { get; } = new();
        public bool Equals((DateOnly Date, string Symbol) x, (DateOnly Date, string Symbol) y) => x.Date == y.Date && StringComparer.Ordinal.Equals(x.Symbol, y.Symbol);
        public int GetHashCode((DateOnly Date, string Symbol) value) => HashCode.Combine(value.Date, StringComparer.Ordinal.GetHashCode(value.Symbol));
    }
}

public sealed record HistoricalSecuritySnapshot(QuoteSnapshot Quote, KlineSeries Klines);
public sealed record HistoricalMarketSnapshot(
    DateOnly TradingDate,
    IReadOnlyList<HistoricalSecuritySnapshot> Securities,
    HistoricalMarketContext? MarketContext,
    HistoricalReplaySupport Support,
    IReadOnlyList<string> Warnings);

public sealed record SparrowClassicParameterSnapshot(bool MacroDef, double MinRise, double MaxRise, double VolRatio, double MinAmount, bool CheckMA60, double MinAdhesion, double MaxAdhesion)
{
    public SparrowClassicScanParameters ToParameters() => new() { MacroDef = MacroDef, MinRise = MinRise, MaxRise = MaxRise, VolRatio = VolRatio, MinAmount = MinAmount, CheckMA60 = CheckMA60, MinAdhesion = MinAdhesion, MaxAdhesion = MaxAdhesion };
    public static SparrowClassicParameterSnapshot From(SparrowClassicScanParameters p) => new(p.MacroDef, p.MinRise, p.MaxRise, p.VolRatio, p.MinAmount, p.CheckMA60, p.MinAdhesion, p.MaxAdhesion);
}
public sealed record SparrowV2ParameterSnapshot(bool MacroDef, double MinRise, double MaxRise, double VolRatio, double MinAmount, bool CheckMA60, double MinAdhesion, double MaxAdhesion, double MinTurnover, double MaxTurnover, double MomentumThreshold, bool CheckAlpha)
{
    public SparrowScanParameters ToParameters() => new() { MacroDef = MacroDef, MinRise = MinRise, MaxRise = MaxRise, VolRatio = VolRatio, MinAmount = MinAmount, CheckMA60 = CheckMA60, MinAdhesion = MinAdhesion, MaxAdhesion = MaxAdhesion, MinTurnover = MinTurnover, MaxTurnover = MaxTurnover, MomentumThreshold = MomentumThreshold, CheckAlpha = CheckAlpha };
    public static SparrowV2ParameterSnapshot From(SparrowScanParameters p) => new(p.MacroDef, p.MinRise, p.MaxRise, p.VolRatio, p.MinAmount, p.CheckMA60, p.MinAdhesion, p.MaxAdhesion, p.MinTurnover, p.MaxTurnover, p.MomentumThreshold, p.CheckAlpha);
}
public sealed record SparrowReplayRequest(SparrowStrategyMode StrategyMode, string StrategyVersion, DateOnly TradingDate, int TopN, SparrowClassicParameterSnapshot? ClassicParameters = null, SparrowV2ParameterSnapshot? V2Parameters = null);
public sealed record SparrowReplaySelection(string Code, string Name, SparrowRankedCandidate RankedCandidate, string ReasonCode, string Reason);
public sealed record SparrowReplayResult(SparrowReplayRequest Request, string ParameterFingerprint, string DatasetId, string DatasetFingerprint, HistoricalReplaySupport Support, IReadOnlyList<SparrowReplaySelection> Selections, IReadOnlyList<string> Warnings);
public sealed record ForwardReturn(int HorizonTradingDays, double? EntryPrice, double? ExitPrice, double? ReturnPercent, bool Available);
public sealed record SparrowBacktestRequest(SparrowStrategyMode StrategyMode, string StrategyVersion, DateOnly StartDate, DateOnly EndDate, int TopN, IReadOnlyList<int> Horizons, double RoundTripCostRate = 0, double SlippageRate = 0, SparrowClassicParameterSnapshot? ClassicParameters = null, SparrowV2ParameterSnapshot? V2Parameters = null);
public sealed record SparrowBacktestSelection(DateOnly ReplayDate, SparrowReplaySelection Selection, IReadOnlyList<ForwardReturn> Outcomes);
public sealed record SparrowHorizonMetrics(int HorizonTradingDays, int SelectionCount, int AvailableCount, double? AverageReturnPercent, double? MedianReturnPercent, double? WinRate);
public sealed record SparrowBacktestResult(SparrowBacktestRequest Request, string ParameterFingerprint, string DatasetId, string DatasetFingerprint, IReadOnlyList<SparrowBacktestSelection> Selections, IReadOnlyList<SparrowHorizonMetrics> Metrics, IReadOnlyList<string> Warnings);
