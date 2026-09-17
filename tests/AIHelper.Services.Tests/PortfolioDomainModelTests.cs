using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class PortfolioDomainModelTests
{
    [Fact]
    public void SimulationRequest_ValidatesRequiredIdentityAndAssumptions()
    {
        PortfolioSimulationRequest request = Request();
        Assert.Equal(1_000_000m, request.InitialCapital);
        Assert.Equal(10, request.TopN);
        Assert.Equal(20, request.HorizonTradingDays);
        Assert.Equal(PortfolioPositionSizingMethod.EqualWeight, request.PositionSizingMethod);
        Assert.Equal(PortfolioExecutionModel.CloseBased, request.ExecutionModel);

        Assert.Throws<ArgumentOutOfRangeException>(() => Request(initialCapital: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(horizonTradingDays: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(commissionRate: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Request(slippageRate: -1));
        Assert.Throws<ArgumentException>(() => Request(startDate: new DateOnly(2026, 2, 2), endDate: new DateOnly(2026, 2, 1)));
    }

    [Fact]
    public void Trade_RequiresPositivePriceQuantityAndNotional()
    {
        PortfolioTrade trade = Trade();
        Assert.Equal(PortfolioTradeSide.Buy, trade.Side);
        Assert.Equal("600519", trade.Symbol);
        Assert.Equal(100, trade.Quantity);

        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioTrade("600519", Date, PortfolioTradeSide.Buy, 100, 0, 10_000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioTrade("600519", Date, PortfolioTradeSide.Buy, -1, 100, 10_000, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioTrade("600519", Date, PortfolioTradeSide.Buy, 100, 100, 0, 0));
    }

    [Fact]
    public void Position_EnforcesOpenAndClosedLifecycleFacts()
    {
        PortfolioPosition open = OpenPosition();
        Assert.Equal(PortfolioPositionStatus.Open, open.Status);
        Assert.Null(open.ExitDate);

        Assert.Throws<ArgumentException>(() => new PortfolioPosition("600519", Date, 100, 100, 10_000, 10, PortfolioPositionStatus.Closed));
        Assert.Throws<ArgumentException>(() => new PortfolioPosition("600519", Date, 100, 100, 10_000, 10, PortfolioPositionStatus.Open,
            exitDate: Date.AddDays(1), exitPrice: 110, realizedPnL: 900));

        PortfolioPosition closed = new("600519", Date, 100, 100, 10_000, 10, PortfolioPositionStatus.Closed,
            exitDate: Date.AddDays(20), exitPrice: 110, exitNotional: 11_000, exitFee: 11, realizedPnL: 979, returnPercent: 9.79);
        Assert.Equal(PortfolioPositionStatus.Closed, closed.Status);
        Assert.Equal(979m, closed.RealizedPnL);
    }

    [Fact]
    public void SnapshotAndEquityPoint_RequireReconciledNonNegativeEquity()
    {
        PortfolioSnapshot snapshot = new(Date, 900_000, 100_000, 1_000_000);
        PortfolioEquityPoint point = new(Date, 900_000, 100_000, 1_000_000, 0, 0);
        Assert.Equal(1_000_000m, snapshot.TotalEquity);
        Assert.Equal(1_000_000m, point.TotalEquity);

        Assert.Throws<ArgumentException>(() => new PortfolioSnapshot(Date, 900_000, 100_000, 999_999));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioEquityPoint(Date, -1, 1, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioEquityPoint(Date, 0, 0, 0, double.NaN, 0));
    }

    [Fact]
    public void Attribution_RequiresARealizedFiniteClosedPositionContribution()
    {
        PortfolioAttribution attribution = new("600519", Date, Date.AddDays(20), 20, 100, 979, 9.79, .000979, true);
        Assert.True(attribution.Winning);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioAttribution("600519", Date, Date, -1, 100, 0, 0, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PortfolioAttribution("600519", Date, Date, 0, 100, 0, double.PositiveInfinity, 0, false));
    }

    [Fact]
    public void Result_CopiesCollectionsAndSupportsSystemTextJsonContracts()
    {
        List<PortfolioTrade> sourceTrades = [Trade()];
        SparrowPortfolioSimulationResult result = new(Request(), "dataset-fingerprint", sourceTrades,
            positions: [OpenPosition()], equityCurve: [new PortfolioEquityPoint(Date, 990_000, 10_000, 1_000_000, 0, 0)],
            attributions: [new PortfolioAttribution("600519", Date, Date.AddDays(20), 20, 100, 979, 9.79, .000979, true)], warnings: ["fixture"]);
        sourceTrades.Add(new PortfolioTrade("600000", Date, PortfolioTradeSide.Buy, 10, 1, 10, 0));

        Assert.Single(result.Trades);
        Assert.IsAssignableFrom<IReadOnlyList<PortfolioTrade>>(result.Trades);
        string json = JsonSerializer.Serialize(result);
        Assert.Contains("DatasetFingerprint", json, StringComparison.Ordinal);
        Assert.Contains("InitialCapital", json, StringComparison.Ordinal);
        SparrowPortfolioSimulationResult? roundTripped = JsonSerializer.Deserialize<SparrowPortfolioSimulationResult>(json);
        Assert.NotNull(roundTripped);
        Assert.Equal(result.DatasetFingerprint, roundTripped.DatasetFingerprint);
        Assert.Single(roundTripped.Trades);
        Assert.Throws<ArgumentException>(() => new SparrowPortfolioSimulationResult(Request(), "other-fingerprint"));
    }

    private static readonly DateOnly Date = new(2026, 2, 2);

    private static PortfolioSimulationRequest Request(
        decimal initialCapital = 1_000_000,
        int horizonTradingDays = 20,
        decimal commissionRate = 0,
        decimal slippageRate = 0,
        DateOnly? startDate = null,
        DateOnly? endDate = null) => new(
            "dataset", "dataset-fingerprint", SparrowStrategyMode.V2, SparrowStrategyVersions.V2, "parameters-fingerprint",
            startDate ?? Date, endDate ?? Date.AddDays(20), 10, horizonTradingDays, initialCapital,
            PortfolioPositionSizingMethod.EqualWeight, commissionRate, slippageRate);

    private static PortfolioTrade Trade() => new("600519", Date, PortfolioTradeSide.Buy, 100, 100, 10_000, 10);
    private static PortfolioPosition OpenPosition() => new("600519", Date, 100, 100, 10_000, 10, PortfolioPositionStatus.Open);
}
