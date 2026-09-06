using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class StockQuoteContractValidatorTests
{
    [Fact]
    public void RejectsLegacyQuoteCacheWithoutSchemaVersion()
    {
        const string legacy = """
            { "data": [{ "Code":"600519", "Wp":27411, "Np":18005 }] }
            """;

        Assert.False(StockQuoteContractValidator.IsCurrentSchema(legacy));
    }

    [Fact]
    public void AcceptsV2QuoteWithUnavailableOuterInner()
    {
        const string current = """
            {
              "data": [{
                "QuoteSchemaVersion":2,
                "Code":"600519",
                "OuterVolume":null,
                "InnerVolume":null,
                "Wp":null,
                "Np":null
              }]
            }
            """;

        Assert.True(StockQuoteContractValidator.IsCurrentSchema(current));
    }
}
