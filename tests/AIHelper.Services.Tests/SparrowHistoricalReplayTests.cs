using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowHistoricalReplayTests
{
    [Fact]
    public void SnapshotBuilder_NeverExposesFutureBars()
    {
        HistoricalMarketDataset dataset = Dataset(); DateOnly replayDate = dataset.TradingDates[65];
        HistoricalMarketSnapshot snapshot = new HistoricalSnapshotBuilder().Build(dataset, replayDate);
        Assert.All(snapshot.Securities.SelectMany(security => security.Klines.Bars), bar => Assert.True(DateOnly.FromDateTime(bar.Date) <= replayDate));
        Assert.All(snapshot.Securities, security => Assert.Equal(66, security.Klines.Bars.Count));
    }

    [Fact]
    public void FuturePrices_CannotChangeV2ReplayCandidateRankOrScore()
    {
        DateOnly date = Dataset().TradingDates[65];
        SparrowReplayRequest request = V2Request(date);
        SparrowReplayResult baseline = new SparrowHistoricalReplayEngine().Replay(Dataset(), request);
        SparrowReplayResult changedFuture = new SparrowHistoricalReplayEngine().Replay(Dataset(futureMultiplier: 10), request);
        Assert.Equal(baseline.Selections.Select(x => (x.Code, x.RankedCandidate.Rank, x.RankedCandidate.TotalScore)), changedFuture.Selections.Select(x => (x.Code, x.RankedCandidate.Rank, x.RankedCandidate.TotalScore)));
    }

    [Fact]
    public void ClassicAndV2Replay_ReuseExistingEvaluatorsAndRanking()
    {
        HistoricalMarketDataset dataset = Dataset(); DateOnly date = dataset.TradingDates[65]; var engine = new SparrowHistoricalReplayEngine();
        SparrowReplayResult classic = engine.Replay(dataset, ClassicRequest(date)); SparrowReplayResult v2 = engine.Replay(dataset, V2Request(date));
        Assert.Equal(HistoricalReplaySupport.Supported, classic.Support); Assert.Equal(HistoricalReplaySupport.Supported, v2.Support);
        Assert.NotEmpty(classic.Selections); Assert.NotEmpty(v2.Selections);
        Assert.All(classic.Selections, x => Assert.Equal(SparrowRankingProfileV1.Name, x.RankedCandidate.RankingProfile));
        Assert.All(v2.Selections, x => Assert.Equal(SparrowRankingProfileV1.Name, x.RankedCandidate.RankingProfile));
    }

    [Fact]
    public void OutcomeEvaluator_UsesMarketTradingDatesAndMissingIsNotZero()
    {
        HistoricalMarketDataset dataset = Dataset(); DateOnly date = dataset.TradingDates[65];
        IReadOnlyList<ForwardReturn> values = new HistoricalOutcomeEvaluator().Evaluate(dataset, date, "600000", new[] { 1, 3, 5, 10, 20, 50 });
        Assert.All(values.Take(5), value => Assert.True(value.Available));
        Assert.Equal((Close(66) / Close(65) - 1) * 100, values[0].ReturnPercent!.Value, 10);
        Assert.False(values[^1].Available); Assert.Null(values[^1].ReturnPercent);
    }

    [Fact]
    public void VersionAndCapabilityGaps_AreExplicit()
    {
        HistoricalMarketDataset dataset = Dataset(capabilities: new HistoricalDataCapabilities(false, true, false, false, true, true));
        SparrowReplayResult unsupported = new SparrowHistoricalReplayEngine().Replay(dataset, V2Request(dataset.TradingDates[65]));
        Assert.Equal(HistoricalReplaySupport.Unsupported, unsupported.Support);
        Assert.Contains("HistoricalFieldUnavailable", Assert.Single(unsupported.Warnings));
        Assert.Throws<ArgumentException>(() => new SparrowHistoricalReplayEngine().Replay(Dataset(), V2Request(dataset.TradingDates[65]) with { StrategyVersion = "wrong" }));
    }

    [Fact]
    public void Backtest_IsDeterministic_AndUsesAvailableOutcomeDenominator()
    {
        HistoricalMarketDataset dataset = Dataset(); var request = new SparrowBacktestRequest(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, dataset.TradingDates[60], dataset.TradingDates[70], 2, new[] { 1, 20 }, ClassicParameters: null, V2Parameters: V2()); var engine = new SparrowHistoricalBacktestEngine();
        SparrowBacktestResult first = engine.Run(dataset, request); SparrowBacktestResult second = engine.Run(dataset, request);
        Assert.Equal(first.Selections.Select(x => (x.ReplayDate, x.Selection.Code)), second.Selections.Select(x => (x.ReplayDate, x.Selection.Code)));
        SparrowHorizonMetrics horizon20 = first.Metrics.Single(x => x.HorizonTradingDays == 20);
        Assert.True(horizon20.AvailableCount <= horizon20.SelectionCount); Assert.Equal(SparrowHistoricalFingerprint.Parameters(request), SparrowHistoricalFingerprint.Parameters(request));
    }

    private static SparrowReplayRequest ClassicRequest(DateOnly date) => new(SparrowStrategyMode.Classic, SparrowStrategyVersions.Classic, date, 2, ClassicParameters: new SparrowClassicParameterSnapshot(false, 1, 5, 1.1, 1, true, 0, .15));
    private static SparrowReplayRequest V2Request(DateOnly date) => new(SparrowStrategyMode.V2, SparrowStrategyVersions.V2, date, 2, V2Parameters: V2());
    private static SparrowV2ParameterSnapshot V2() => new(false, 1, 5, 1.1, 1, true, 0, .15, 3, 30, 0, false);
    private static double Close(int index) => 8 + index * .02;
    private static HistoricalMarketDataset Dataset(double futureMultiplier = 1, HistoricalDataCapabilities? capabilities = null)
    {
        DateOnly start = new(2026, 1, 1); DateOnly[] dates = Enumerable.Range(0, 90).Select(i => start.AddDays(i)).ToArray();
        KlineSeries[] klines = new[] { "600000", "600001" }.Select((code, stock) => new KlineSeries(code, dates.Select((date, i) => new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, i > 65 ? Close(i) * futureMultiplier : Close(i), null, null, null, null, null)).ToArray())).ToArray();
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => new[] { "600000", "600001" }.Select((code, index) => new HistoricalQuoteObservation(date, new QuoteSnapshot(code, code, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, index == 0 ? 1500 : 3000, 1000, null, null, null)))).ToArray();
        HistoricalMarketContext[] context = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, 0.5)).ToArray();
        return new HistoricalMarketDataset("test-dataset", dates, quotes, klines, context, capabilities, "Unknown", "Test");
    }
}
