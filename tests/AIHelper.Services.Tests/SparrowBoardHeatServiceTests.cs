using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

/// <summary>Locks the sector-heat data channel: percentiles, symbol mapping, fail-open and cache behavior.</summary>
public sealed class SparrowBoardHeatServiceTests
{
    [Fact]
    public async Task GetAsync_JoinsBoardPercentilesWithIndustryMembership()
    {
        SparrowBoardHeatService service = new(new FakeProvider(
            boards: """{"data":[{"Code":"BK0475","Name":"酿酒行业","Percent":4.0,"MainInflow":1.0},{"Code":"BK0478","Name":"银行","Percent":1.0,"MainInflow":2.0},{"Code":"BK0479","Name":"钢铁","Percent":-2.0,"MainInflow":0.0}]}""",
            map: """{"data":[{"Code":"600000","Industry":"银行"},{"Code":"600001","Industry":"钢铁"},{"Code":"600002","Industry":"银行"}]}"""));

        SparrowBoardHeatResult result = await service.GetAsync(useCache: false, CancellationToken.None);

        Assert.True(result.Available);
        Assert.Equal(3, result.HeatPercentileBySymbol.Count);
        // Midrank percentiles over {-2, 1, 4}: 钢铁 0, 银行 50, 酿酒 100.
        Assert.Equal(0d, result.HeatPercentileBySymbol["600001"], 10);
        Assert.Equal(50d, result.HeatPercentileBySymbol["600000"], 10);
        Assert.Equal(50d, result.HeatPercentileBySymbol["600002"], 10);
    }

    [Fact]
    public async Task GetAsync_FailsOpenWhenEitherSourceFails()
    {
        SparrowBoardHeatService boardsFail = new(new FakeProvider(
            boards: "broken", map: """{"data":[{"Code":"600000","Industry":"银行"}]}""", ok: false));
        SparrowBoardHeatService mapFail = new(new FakeProvider(
            boards: """{"data":[{"Code":"BK1","Name":"银行","Percent":1.0}]}""", map: "broken"));

        Assert.False((await boardsFail.GetAsync(false, CancellationToken.None)).Available);
        Assert.False((await mapFail.GetAsync(false, CancellationToken.None)).Available);
        Assert.Equal(SparrowBoardHeatResult.Unavailable, await boardsFail.GetAsync(false, CancellationToken.None));
    }

    [Fact]
    public async Task GetAsync_CachesTheSnapshotAndParsesItBack()
    {
        var provider = new FakeProvider(
            boards: """{"data":[{"Code":"BK0478","Name":"银行","Percent":1.0,"MainInflow":0.0}]}""",
            map: """{"data":[{"Code":"600000","Industry":"银行"}]}""");
        var cache = new SparrowMarketDataCache();
        SparrowBoardHeatService service = new(provider, cache);

        SparrowBoardHeatResult first = await service.GetAsync(useCache: false, CancellationToken.None);
        Assert.True(first.Available);

        provider.Fail = true;
        SparrowBoardHeatResult cached = await service.GetAsync(useCache: true, CancellationToken.None);
        Assert.True(cached.Available);
        Assert.Equal(first.HeatPercentileBySymbol["600000"], cached.HeatPercentileBySymbol["600000"], 10);

        SparrowBoardHeatResult parsed = SparrowBoardHeatService.ParseCached(cache.SnapshotForTesting(SparrowDataCachePolicy.BoardHeatCacheKey)!)!;
        Assert.True(parsed.Available);
        // The single ranked board sits at the 100 endpoint under the engine's midrank convention.
        Assert.Equal(100d, parsed.HeatPercentileBySymbol["600000"], 10);
    }

    [Fact]
    public void Endpoints_BuildBoardAndIndustryUrls()
    {
        Assert.Equal("/api/board-heat", SparrowEndpoints.BoardHeat(false));
        Assert.Equal("/api/board-heat?refresh=1", SparrowEndpoints.BoardHeat(true));
        Assert.Equal("/api/industry-map", SparrowEndpoints.IndustryMap(false));
        Assert.Equal("/api/industry-map?refresh=1", SparrowEndpoints.IndustryMap(true));
    }

    private sealed class FakeProvider : IStockDataProvider
    {
        private readonly string _boards;
        private readonly string _map;
        private readonly bool _ok;

        public FakeProvider(string boards, string map, bool ok = true)
        {
            _boards = boards;
            _map = map;
            _ok = ok;
            Fail = false;
        }

        public bool Fail { get; set; }

        public bool CanHandle(StockDataRequest request) => true;

        public Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
        {
            bool success = _ok && !Fail;
            string json = request.Path == "/api/board-heat" ? _boards : _map;
            return Task.FromResult(new StockDataResult
            {
                Endpoint = request.Endpoint,
                Handled = true,
                Success = success,
                Json = json
            });
        }
    }
}

/// <summary>Test-only view of a persisted cache entry.</summary>
file static class CacheTestExtensions
{
    public static string? SnapshotForTesting(this SparrowMarketDataCache cache, string key) => cache.TryGet(key, true, out string? value) ? value : null;
}
