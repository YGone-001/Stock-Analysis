using System.Text.Json;
using AIHelper.Models;
using AIHelper.Services.StockData;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class EastMoneyQuoteMapperTests
{
    [Fact]
    public void SingleQuote_DoesNotUseOrderBookFieldsAsOuterInnerFallback()
    {
        using JsonDocument document = JsonDocument.Parse("""
            { "f57":"600519", "f43":1330, "f60":1298.88, "f34":27411, "f35":18005 }
            """);

        StockQuoteSnapshot quote = EastMoneyQuoteMapper.MapSingle(document.RootElement);

        Assert.Null(quote.OuterVolume);
        Assert.Null(quote.InnerVolume);
        Assert.Null(quote.Wp);
        Assert.Null(quote.Np);
    }

    [Fact]
    public void BatchQuote_DoesNotUseSingleEndpointFieldsAsFallback()
    {
        using JsonDocument document = JsonDocument.Parse("""
            { "f12":"600519", "f2":1330, "f18":1298.88, "f49":89.555, "f161":18005 }
            """);

        StockQuoteSnapshot quote = EastMoneyQuoteMapper.MapBatch(document.RootElement);

        Assert.Null(quote.OuterVolume);
        Assert.Null(quote.InnerVolume);
    }

    [Fact]
    public void BatchQuote_MapsEndpointSpecificOuterInnerFields()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "f12":"600519", "f14":"贵州茅台", "f2":1330, "f18":1298.88,
              "f3":2.4, "f5":45416, "f6":6022594729, "f8":0.36,
              "f34":27411, "f35":18005
            }
            """);

        StockQuoteSnapshot quote = EastMoneyQuoteMapper.MapBatch(document.RootElement);

        Assert.Equal(EastMoneyQuoteMapper.BatchEndpoint, quote.SourceEndpoint);
        Assert.Equal(27411, quote.OuterVolume);
        Assert.Equal(18005, quote.InnerVolume);
        Assert.Equal(quote.Volume, quote.OuterVolume + quote.InnerVolume);
    }

    [Fact]
    public void SingleQuote_MapsOnlyDocumentedOuterInnerFields()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {
              "f57":"600519", "f58":"贵州茅台", "f43":1330, "f60":1298.88,
              "f170":2.4, "f47":45416, "f48":6022594729, "f168":0.36,
              "f49":27411, "f161":18005
            }
            """);

        StockQuoteSnapshot quote = EastMoneyQuoteMapper.MapSingle(document.RootElement);

        Assert.Equal(EastMoneyQuoteMapper.SingleEndpoint, quote.SourceEndpoint);
        Assert.Equal(27411, quote.OuterVolume);
        Assert.Equal(18005, quote.InnerVolume);
        Assert.Equal(quote.Volume, quote.OuterVolume + quote.InnerVolume);
    }

    [Fact]
    public void BatchAndSingleQuote_UseSameNormalizedUnitsForCommonFields()
    {
        using JsonDocument batchDocument = JsonDocument.Parse("""
            {
              "f12":"600519", "f14":"贵州茅台", "f2":1330, "f18":1298.88,
              "f3":2.4, "f5":45416, "f6":6022594729, "f8":0.36,
              "f34":27411, "f35":18005
            }
            """);
        using JsonDocument singleDocument = JsonDocument.Parse("""
            {
              "f57":"600519", "f58":"贵州茅台", "f43":1330, "f60":1298.88,
              "f170":2.4, "f47":45416, "f48":6022594729, "f168":0.36,
              "f49":27411, "f161":18005
            }
            """);

        StockQuoteSnapshot batch = EastMoneyQuoteMapper.MapBatch(batchDocument.RootElement);
        StockQuoteSnapshot single = EastMoneyQuoteMapper.MapSingle(singleDocument.RootElement);

        Assert.Equal(batch.Price, single.Price);
        Assert.Equal(batch.PreClose, single.PreClose);
        Assert.Equal(batch.Percent, single.Percent);
        Assert.Equal(batch.Amount, single.Amount);
        Assert.Equal(batch.Volume, single.Volume);
        Assert.Equal(batch.Turnover, single.Turnover);
        Assert.Equal(batch.OuterVolume, single.OuterVolume);
        Assert.Equal(batch.InnerVolume, single.InnerVolume);
    }

    [Fact]
    public void MissingAndZeroHaveDifferentWireRepresentations()
    {
        using JsonDocument missingDocument = JsonDocument.Parse("{\"f12\":\"600519\",\"f2\":10}");
        using JsonDocument zeroDocument = JsonDocument.Parse("{\"f12\":\"600519\",\"f2\":10,\"f34\":0,\"f35\":0}");

        string missingJson = JsonSerializer.Serialize(EastMoneyQuoteMapper.MapBatch(missingDocument.RootElement));
        string zeroJson = JsonSerializer.Serialize(EastMoneyQuoteMapper.MapBatch(zeroDocument.RootElement));

        using JsonDocument missing = JsonDocument.Parse(missingJson);
        using JsonDocument zero = JsonDocument.Parse(zeroJson);
        Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("OuterVolume").ValueKind);
        Assert.Equal(JsonValueKind.Null, missing.RootElement.GetProperty("Wp").ValueKind);
        Assert.Equal(0, zero.RootElement.GetProperty("OuterVolume").GetDouble());
        Assert.Equal(StockQuoteSnapshot.CurrentSchemaVersion,
            zero.RootElement.GetProperty("QuoteSchemaVersion").GetInt32());
    }

    [Fact]
    public void NormalizedSnapshot_IsConsumableBySparrowContract()
    {
        using JsonDocument source = JsonDocument.Parse("""
            { "f12":"600519", "f2":10.5, "f18":10, "f3":5, "f6":100000000, "f8":1.2, "f34":150, "f35":100 }
            """);
        string json = JsonSerializer.Serialize(EastMoneyQuoteMapper.MapBatch(source.RootElement));
        using JsonDocument normalized = JsonDocument.Parse(json);

        bool parsed = SparrowQuoteDataContract.TryParse(normalized.RootElement, out SparrowQuoteData quote);

        Assert.True(parsed);
        Assert.Equal(5, quote.PriceDerivedPercent);
        Assert.Equal(150, quote.OuterVolume);
        Assert.Equal(100, quote.InnerVolume);
    }
}
