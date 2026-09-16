using System.Text.Json;
using System.IO;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowLegacyHistoricalReplayCapabilityTests
{
    private static readonly string[] ExpectedBlockers =
    {
        "LEGACY_STRATEGY_IDENTITY_NOT_ACTIVE",
        "LEGACY_INTRADAY_QUOTE_SEMANTICS_UNAVAILABLE",
        "LEGACY_OUTER_VOLUME_UNAVAILABLE",
        "LEGACY_INNER_VOLUME_UNAVAILABLE",
        "LEGACY_INTRADAY_MARKET_REGIME_UNAVAILABLE",
        "LEGACY_KLINE_ASOF_SEMANTICS_UNVERIFIED",
        "LEGACY_CURRENT_UNIVERSE_DEPENDENCY"
    };

    [Fact]
    public void BlockerCodes_AreExhaustiveOrderedAndDistinct()
    {
        IReadOnlyList<string> blockers = SparrowLegacyHistoricalReplayCapability.BlockerReasonCodes;

        Assert.Equal(ExpectedBlockers, blockers);
        Assert.Equal(blockers.Count, blockers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void CapabilityResult_IsAlwaysUnsupportedAndPreservesDatasetIdentity()
    {
        HistoricalMarketDataset dataset = Dataset("legacy-dataset-id");
        var request = new SparrowLegacyHistoricalReplayRequest(new DateOnly(2026, 6, 10),
            new Dictionary<string, string> { ["note"] = "evidence-only" });

        SparrowLegacyHistoricalReplayResult result = SparrowLegacyHistoricalReplayCapability.CreateUnsupported(request, dataset);

        Assert.Equal("Legacy", result.StrategyIdentity);
        Assert.Equal(SparrowStrategyVersions.Legacy, result.StrategyVersion);
        Assert.Equal(request.TradingDate, result.TradingDate);
        Assert.Equal(dataset.DatasetId, result.DatasetId);
        Assert.Equal(dataset.Fingerprint, result.DatasetFingerprint);
        Assert.Equal(HistoricalReplaySupport.Unsupported, result.Support);
        Assert.Empty(result.Selections);
        Assert.Equal(ExpectedBlockers, result.BlockerReasonCodes);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains("no strategy evaluator", result.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Result_IsDeterministicAndRequestEvidenceIsFrozen()
    {
        var evidence = new Dictionary<string, string> { ["z"] = "last", ["a"] = "first" };
        var request = new SparrowLegacyHistoricalReplayRequest(new DateOnly(2026, 6, 10), evidence);
        HistoricalMarketDataset dataset = Dataset("same-id");
        SparrowLegacyHistoricalReplayResult first = SparrowLegacyHistoricalReplayCapability.CreateUnsupported(request, dataset);
        SparrowLegacyHistoricalReplayResult second = SparrowLegacyHistoricalReplayCapability.CreateUnsupported(request, dataset);
        evidence["a"] = "changed-after-request";

        Assert.Equal(new[] { "a", "z" }, request.ParameterEvidence.Keys);
        Assert.Equal("first", request.ParameterEvidence["a"]);
        Assert.Equal(first.DatasetId, second.DatasetId);
        Assert.Equal(first.DatasetFingerprint, second.DatasetFingerprint);
        Assert.Equal(first.BlockerReasonCodes, second.BlockerReasonCodes);
        Assert.Equal(first.Selections, second.Selections);
        Assert.Equal(first.Support, second.Support);
    }

    [Fact]
    public async Task LegacyCapabilityExporter_WritesExplicitStableJson()
    {
        HistoricalMarketDataset dataset = Dataset("dataset-identity-is-verbatim");
        SparrowLegacyHistoricalReplayResult result = SparrowLegacyHistoricalReplayCapability.CreateUnsupported(
            new SparrowLegacyHistoricalReplayRequest(new DateOnly(2026, 6, 10)), dataset);
        string path = Path.Combine(Path.GetTempPath(), $"legacy-capability-{Guid.NewGuid():N}.json");

        try
        {
            await SparrowHistoricalResultExporter.ExportLegacyReplayCapabilityJsonAsync(result, path);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            JsonElement root = json.RootElement;

            Assert.Equal("Legacy", root.GetProperty("StrategyIdentity").GetString());
            Assert.Equal("legacy", root.GetProperty("StrategyVersion").GetString());
            Assert.Equal("Unsupported", root.GetProperty("Support").GetString());
            Assert.Equal(dataset.DatasetId, root.GetProperty("DatasetId").GetString());
            Assert.Equal(dataset.Fingerprint, root.GetProperty("DatasetFingerprint").GetString());
            Assert.Equal(ExpectedBlockers, root.GetProperty("BlockerReasonCodes").EnumerateArray().Select(item => item.GetString()));
            Assert.Equal(0, root.GetProperty("Selections").GetArrayLength());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void LegacyIsNotAddedToRunnableReplayOrBacktestStrategyMode()
    {
        Assert.Equal(new[] { "Classic", "V2", "Compare" }, Enum.GetNames<SparrowStrategyMode>());
        Assert.DoesNotContain(typeof(SparrowLegacyHistoricalReplayCapability).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "AIHelper.Services", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(SparrowLegacyHistoricalReplayCapability).Assembly.GetTypes(),
            type => type.Name is "SparrowHistoricalReplayEngine" or "SparrowHistoricalBacktestEngine");
    }

    private static HistoricalMarketDataset Dataset(string id) => new(
        id,
        new[] { new DateOnly(2026, 6, 10) },
        Array.Empty<HistoricalQuoteObservation>(),
        Array.Empty<AIHelper.Core.StockData.KlineSeries>(),
        source: "LegacyCapabilityTest",
        schemaVersion: 2);
}
