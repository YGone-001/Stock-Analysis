using System.Text.Json;
using System.IO;
using System.Diagnostics;
using AIHelper.Core.Sparrow;
using AIHelper.Core.StockData;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowPortfolioResearchExportTests
{
    [Fact]
    public async Task ExportAsync_WritesValidJsonWithRequiredResearchFields()
    {
        string path = TempPath();
        try
        {
            SparrowPortfolioResearchArtifact artifact = await new SparrowPortfolioResearchExporter().ExportAsync(Result("dataset-a"), path);
            Assert.True(File.Exists(path));
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            JsonElement root = json.RootElement;
            Assert.Equal(SparrowPortfolioResearchArtifact.CurrentArtifactVersion, root.GetProperty("artifactVersion").GetString());
            Assert.Equal("dataset-a", root.GetProperty("datasetFingerprint").GetString());
            Assert.True(root.TryGetProperty("strategy", out _));
            Assert.True(root.TryGetProperty("portfolio", out _));
            Assert.True(root.TryGetProperty("performance", out _));
            Assert.True(root.TryGetProperty("equityCurve", out _));
            Assert.True(root.TryGetProperty("positions", out _));
            Assert.True(root.TryGetProperty("attribution", out _));
            Assert.Equal(artifact.AnalysisFingerprint, root.GetProperty("analysisFingerprint").GetString());
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task ExportAsync_IsByteStableForEquivalentInputs()
    {
        string first = TempPath(); string second = TempPath();
        try
        {
            SparrowPortfolioResearchExporter exporter = new();
            SparrowPortfolioResearchArtifact one = await exporter.ExportAsync(Result("dataset-a"), first);
            SparrowPortfolioResearchArtifact two = await exporter.ExportAsync(Result("dataset-a"), second);
            Assert.Equal(one.AnalysisFingerprint, two.AnalysisFingerprint);
            Assert.Equal(await File.ReadAllTextAsync(first), await File.ReadAllTextAsync(second));
        }
        finally { if (File.Exists(first)) File.Delete(first); if (File.Exists(second)) File.Delete(second); }
    }

    [Fact]
    public void Artifact_CanonicalizesEveryExportedCollectionOrdering()
    {
        SparrowPortfolioResearchArtifact artifact = new("AIHelper.HistoricalDataTool", Result("dataset-a"));
        Assert.Equal(new[] { "600000", "600001" }, artifact.Trades.Select(trade => trade.Symbol));
        Assert.Equal(new[] { "600000", "600001" }, artifact.Positions.Select(position => position.Symbol));
        Assert.Equal(new[] { new DateOnly(2026, 1, 2), new DateOnly(2026, 1, 3) }, artifact.EquityCurve.Select(point => point.Date));
        Assert.Equal(new[] { "600000", "600001" }, artifact.Attribution.Select(item => item.Symbol));
        Assert.Equal(artifact.Warnings.OrderBy(value => value, StringComparer.Ordinal), artifact.Warnings);
    }

    [Fact]
    public void ArtifactIdentity_IsStableWithoutTimestampAndSensitiveToDatasetFingerprint()
    {
        SparrowPortfolioResearchArtifact first = new("AIHelper.HistoricalDataTool", Result("dataset-a"));
        SparrowPortfolioResearchArtifact sameInputsLater = new("AIHelper.HistoricalDataTool", Result("dataset-a"));
        SparrowPortfolioResearchArtifact changedDataset = new("AIHelper.HistoricalDataTool", Result("dataset-b"));
        Assert.Equal(first.AnalysisFingerprint, sameInputsLater.AnalysisFingerprint);
        Assert.Equal(first.StrategyFingerprint, sameInputsLater.StrategyFingerprint);
        Assert.NotEqual(first.AnalysisFingerprint, changedDataset.AnalysisFingerprint);
    }

    [Fact]
    public void Artifact_AlwaysDisclosesResearchLimitationsAndUnavailableBenchmarkStatus()
    {
        SparrowPortfolioResearchArtifact artifact = new("AIHelper.HistoricalDataTool", Result("dataset-a"));
        Assert.Contains("Close based historical simulation only", artifact.Limitations);
        Assert.Contains("No intraday execution", artifact.Limitations);
        Assert.Contains("No market impact model", artifact.Limitations);
        Assert.Contains("No leverage", artifact.Limitations);
        Assert.Contains("No optimization", artifact.Limitations);
        Assert.Contains("No live trading", artifact.Limitations);
        Assert.Equal("Unavailable", artifact.BenchmarkSummary.Status);
    }

    [Fact]
    public async Task HistoricalTool_PortfolioResearchExportRunsOfflineWithoutGatewayEnvironment()
    {
        string datasetPath = TempPath(); string outputPath = TempPath();
        try
        {
            HistoricalMarketDataset dataset = CliDataset();
            await new HistoricalDatasetJsonWriter().WriteAsync(dataset, datasetPath);
            string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = root,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.ArgumentList.Add("run"); start.ArgumentList.Add("--no-build"); start.ArgumentList.Add("--project"); start.ArgumentList.Add("tools/AIHelper.HistoricalDataTool"); start.ArgumentList.Add("--");
            foreach (string argument in new[] { "--export-portfolio-research", "true", "--simulate-portfolio", "true", "--analyze-portfolio", "true", "--dataset", datasetPath,
                "--strategy", "v2", "--start", dataset.TradingDates[65].ToString("yyyy-MM-dd"), "--end", dataset.TradingDates[66].ToString("yyyy-MM-dd"),
                "--initial-capital", "1000000", "--top-n", "2", "--horizon", "1", "--output", outputPath }) start.ArgumentList.Add(argument);
            start.Environment.Remove("HISTORICAL_GATEWAY_URL");
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the historical tool.");
            string stdout = await process.StandardOutput.ReadToEndAsync(); string stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.True(process.ExitCode == 0, $"CLI failed: {stderr}");
            Assert.Contains("OUTPUT_FILE=", stdout, StringComparison.Ordinal);
            using JsonDocument json = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            Assert.Equal(dataset.Fingerprint, json.RootElement.GetProperty("datasetFingerprint").GetString());
        }
        finally { if (File.Exists(datasetPath)) File.Delete(datasetPath); if (File.Exists(outputPath)) File.Delete(outputPath); }
    }

    private static SparrowPortfolioPerformanceResult Result(string datasetFingerprint)
    {
        DateOnly first = new(2026, 1, 2); DateOnly second = new(2026, 1, 3);
        PortfolioSimulationRequest request = new("fixture", datasetFingerprint, SparrowStrategyMode.V2, SparrowStrategyVersions.V2,
            "strategy-parameters", first, second, 2, 1, 1_000, PortfolioPositionSizingMethod.EqualWeight, 0, 0);
        PortfolioTrade[] trades =
        {
            new("600001", second, PortfolioTradeSide.Sell, 110, 1, 110, 0),
            new("600000", first, PortfolioTradeSide.Buy, 100, 1, 100, 0)
        };
        PortfolioPosition[] positions =
        {
            new("600001", second, 100, 1, 100, 0, PortfolioPositionStatus.Open),
            new("600000", first, 100, 1, 100, 0, PortfolioPositionStatus.Closed, second, 110, 110, 0, 10, 10)
        };
        SparrowPortfolioSimulationResult simulation = new(request, datasetFingerprint, trades, positions, warnings: new[] { "z-warning", "a-warning" });
        PortfolioEquityCurve curve = new(new[]
        {
            new PortfolioEquityPoint(second, 910, 100, 1_010, .01, .01),
            new PortfolioEquityPoint(first, 900, 100, 1_000, null, 0)
        }.OrderBy(point => point.Date));
        PortfolioAttribution[] attribution =
        {
            new("600001", second, second, 0, 1, -5, -5, -.5, false),
            new("600000", first, second, 1, 1, 10, 10, 1, true)
        };
        PortfolioPerformanceMetrics metrics = new(1_000, 1_010, 1, -1, second, 2, 1, 1, .5);
        return new SparrowPortfolioPerformanceResult(simulation, curve, metrics, attribution, new[] { "z-warning", "a-warning" });
    }

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"portfolio-research-{Guid.NewGuid():N}.json");

    private static HistoricalMarketDataset CliDataset()
    {
        DateOnly[] dates = Enumerable.Range(0, 70).Select(index => new DateOnly(2026, 1, 1).AddDays(index)).ToArray();
        string[] symbols = ["600000", "600001"];
        HistoricalQuoteObservation[] quotes = dates.SelectMany(date => symbols.Select((symbol, index) => new HistoricalQuoteObservation(date,
            new QuoteSnapshot(symbol, symbol, 10.3, 10, 3, null, index == 0 ? 100_000_000 : 200_000_000, 10, 1_500, 1_000, null, null, null)))).ToArray();
        KlineSeries[] klines = symbols.Select(symbol => new KlineSeries(symbol, dates.Select((date, index) =>
            new KlineBar(date.ToDateTime(TimeOnly.MinValue), null, null, null, 8 + index * .02, null, null, null, null, null)).ToArray())).ToArray();
        HistoricalSecurity[] securities = symbols.Select(symbol => new HistoricalSecurity(symbol, symbol, HistoricalSecurityType.Stock, HistoricalSecurityMarket.Shanghai, dates[0], null, HistoricalLifecycleQuality.Partial, dates[0])).ToArray();
        HistoricalUniverseSnapshot[] universes = dates.Select(date => new HistoricalUniverseSnapshot(date, symbols, HistoricalUniverseQuality.Partial, "fixture")).ToArray();
        HistoricalFieldCapability[] fields = Enum.GetValues<HistoricalField>().Select(field => new HistoricalFieldCapability(field, HistoricalFieldOrigin.Observed, HistoricalFieldCoverage.Full)).ToArray();
        HistoricalPriceSeriesProvenance[] provenance = symbols.Select(symbol => new HistoricalPriceSeriesProvenance(symbol, "fixture", HistoricalPriceAdjustmentMode.Raw, dates[0], dates[^1])).ToArray();
        HistoricalMarketContext[] contexts = dates.Select(date => new HistoricalMarketContext(date, new SparrowMarketRegime { Defensive = false }, .5)).ToArray();
        HistoricalMarketContextProvenance[] contextProvenance = dates.Select(date => new HistoricalMarketContextProvenance(date, HistoricalFieldOrigin.Observed, "fixture", HistoricalFieldOrigin.Observed, "fixture")).ToArray();
        return new HistoricalMarketDataset("cli-fixture", dates, quotes, klines, contexts, priceAdjustmentMode: "Raw", source: "fixture", schemaVersion: 2,
            metadata: new HistoricalDatasetMetadata("cli-fixture", "fixture", null, HistoricalUniverseQuality.Partial), securities: securities, universes: universes,
            fieldCapabilities: fields, priceSeriesProvenance: provenance, marketContextProvenance: contextProvenance,
            qualitySummary: new HistoricalDatasetQualitySummary(HistoricalUniverseQuality.Partial, HistoricalLifecycleQuality.Partial, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.None, HistoricalFieldCoverage.Full, HistoricalFieldCoverage.None));
    }
}
