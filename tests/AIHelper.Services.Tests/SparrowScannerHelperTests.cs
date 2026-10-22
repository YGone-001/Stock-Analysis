using System.Text.Json;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public class SparrowSymbolNormalizerTests
{
    [Theory]
    [InlineData("1.600000", "600000")]
    [InlineData("0.300001", "300001")]
    [InlineData("600000", "600000")]
    [InlineData("300001", "300001")]
    [InlineData(" 1.600000 ", "600000")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsExchangePrefix(string? input, string expected)
    {
        Assert.Equal(expected, SparrowSymbolNormalizer.Normalize(input));
    }
}

public class SparrowJsonElementTests
{
    [Fact]
    public void EnumerateDataArray_ReturnsItems()
    {
        const string json = "{\"data\":[{\"Code\":\"600000\"},{\"Code\":\"600001\"}]}";
        JsonElement[] items = SparrowJsonElement.EnumerateDataArray(json).ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal("600000", SparrowJsonElement.GetString(items[0], "Code"));
    }

    [Fact]
    public void EnumerateDataArray_NoData_ReturnsEmpty()
    {
        Assert.Empty(SparrowJsonElement.EnumerateDataArray("{\"other\":[]}"));
    }

    [Fact]
    public void TryGetPropertyIgnoreCase_MatchesCaseInsensitively()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"Close\":12.5}");
        Assert.True(SparrowJsonElement.TryGetPropertyIgnoreCase(doc.RootElement, "close", out JsonElement value));
        Assert.Equal(12.5, value.GetDouble());
    }

    [Fact]
    public void TryGetDouble_ReadsNumberAndString()
    {
        using JsonDocument doc = JsonDocument.Parse("{\"n\":12.5,\"s\":\"3.25\"}");
        Assert.True(SparrowJsonElement.TryGetDouble(doc.RootElement, "n", out double n));
        Assert.Equal(12.5, n);
        Assert.True(SparrowJsonElement.TryGetDouble(doc.RootElement, "s", out double s));
        Assert.Equal(3.25, s);
    }
}

public class SparrowKlineJsonParserTests
{
    private const string WrappedJson = "{\"data\":[{\"Close\":10100},{\"Close\":10200},{\"Close\":10300}]}";

    [Fact]
    public void ParseClosesNewestFirst_ReversesAndExtractsLatest()
    {
        List<double> closes = SparrowKlineJsonParser.ParseClosesNewestFirst(WrappedJson, out double latest);
        Assert.Equal(new[] { 10.3, 10.2, 10.1 }, closes);
        Assert.Equal(10.3, latest);
    }

    [Fact]
    public void ParseClosesOldestFirst_DividesMilliUnits()
    {
        List<double> closes = SparrowKlineJsonParser.ParseClosesOldestFirst(WrappedJson);
        Assert.Equal(new[] { 10.1, 10.2, 10.3 }, closes);
    }

    [Fact]
    public void ParseClosesOldestFirst_HandlesListEnvelope()
    {
        const string json = "{\"data\":{\"list\":[{\"Close\":5000},{\"Close\":6000}]}}";
        List<double> closes = SparrowKlineJsonParser.ParseClosesOldestFirst(json);
        Assert.Equal(new[] { 5.0, 6.0 }, closes);
    }

    [Fact]
    public void ParseClosesOldestFirst_InvalidJsonShape_ReturnsEmpty()
    {
        Assert.Empty(SparrowKlineJsonParser.ParseClosesOldestFirst("{\"data\":{}}"));
    }
}
