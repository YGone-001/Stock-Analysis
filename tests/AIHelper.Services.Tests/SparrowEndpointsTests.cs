using AIHelper.Core.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowEndpointsTests
{
    [Fact]
    public void Quote_BuildsExpectedUrls()
    {
        Assert.Equal("/api/quote?code=600000,600001", SparrowEndpoints.Quote("600000,600001", false));
        Assert.Equal("/api/quote?code=600000&refresh=1", SparrowEndpoints.Quote("600000", true));
    }

    [Fact]
    public void QuoteAll_BuildsExpectedUrls()
    {
        Assert.Equal("/api/quote-all", SparrowEndpoints.QuoteAll(false));
        Assert.Equal("/api/quote-all?refresh=1", SparrowEndpoints.QuoteAll(true));
    }

    [Fact]
    public void KlineAll_BuildsExpectedUrls()
    {
        Assert.Equal("/api/kline-all?code=600000&type=day&limit=120", SparrowEndpoints.KlineAll("600000", 120, false));
        Assert.Equal("/api/kline-all?code=600000&type=day&limit=120&refresh=1", SparrowEndpoints.KlineAll("600000", 120, true));
        Assert.Equal("/api/kline-all?code=600000&limit=120", SparrowEndpoints.KlineAll("600000", 120, false, type: null));
        Assert.Equal("/api/kline-all?code=600000&limit=120&refresh=1", SparrowEndpoints.KlineAll("600000", 120, true, type: null));
    }

    [Fact]
    public void Index_BuildsExpectedUrls()
    {
        Assert.Equal("/api/index?code=sh000001&limit=2", SparrowEndpoints.Index("sh000001", 2, false));
        Assert.Equal("/api/index?code=sh000001&limit=2&refresh=1", SparrowEndpoints.Index("sh000001", 2, true));
    }

    [Fact]
    public void Trend_BuildsExpectedUrls()
    {
        Assert.Equal("/api/trend?code=1.000001", SparrowEndpoints.Trend("1.000001", false));
        Assert.Equal("/api/trend?code=1.000001&refresh=1", SparrowEndpoints.Trend("1.000001", true));
    }
}
