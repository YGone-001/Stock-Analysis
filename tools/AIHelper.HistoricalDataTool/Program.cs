using System.Globalization;
using System.Text.Json;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;

Dictionary<string, string> options = Parse(args);
try
{
    if (Bool("report-legacy-capability", false))
    {
        Console.WriteLine("LEGACY=Unsupported");
        Console.WriteLine($"LEGACY_VERSION={SparrowStrategyVersions.Legacy}");
        Console.WriteLine($"LEGACY_REASONS={string.Join(',', SparrowLegacyHistoricalReplayCapability.BlockerReasonCodes)}");
        return 0;
    }
    if (Bool("analyze-benchmark", false))
        return await AnalyzeBenchmarkAsync();

    string source = Value("source", "tushare");
    string adjustment = Value("adjustment", "raw");
    if (!string.Equals(source, "tushare", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only --source tushare is supported.");
    if (!string.Equals(adjustment, "raw", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only --adjustment raw is supported.");
    Uri gateway = new(Environment.GetEnvironmentVariable("HISTORICAL_GATEWAY_URL") ?? throw new InvalidOperationException("HISTORICAL_GATEWAY_URL is required."));
    using HttpClient client = new() { BaseAddress = gateway };
    string[] explicitSymbols = Value("symbols", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    string[] benchmarkIds = Value("benchmarks", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    HistoricalDatasetScope? scope = explicitSymbols.Length == 0 ? null : new HistoricalDatasetScope(HistoricalDatasetScopeKind.ExplicitSymbolSet, Symbols: explicitSymbols);
    HistoricalDatasetBuildRequest request = new(
        Value("dataset-id", $"tushare-{Value("start")}-{Value("end")}"), Date("start"), Date("end"), Value("output"),
        HistoricalPriceAdjustmentMode.Raw,
        IncludeTurnover: Bool("include-turnover", true), IncludeSuspension: Bool("include-suspension", false), IncludeHistoricalSt: Bool("include-st", false),
        IncludeAdjustmentFactors: Bool("include-adjustment-factors", false), IncludeV2IndexContext: Bool("include-v2-index", true), Scope: scope, ExplicitSymbols: explicitSymbols,
        BenchmarkIds: benchmarkIds);
    HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(new HistoricalHttpMarketDataSource(client)).BuildAsync(request);
    Console.WriteLine($"DATASET={result.Dataset.DatasetId}");
    Console.WriteLine($"FINGERPRINT={result.Dataset.Fingerprint}");
    Console.WriteLine($"SCOPE={result.Dataset.DatasetScope.Kind}");
    Console.WriteLine($"QUALITY={(result.IsPartial ? "PARTIAL" : "FULL")}");
    Console.WriteLine($"UNIVERSE={result.Dataset.QualitySummary.UniverseQuality}");
    Console.WriteLine($"LIFECYCLE={result.Dataset.QualitySummary.LifecycleQuality}");
    Console.WriteLine($"ST={result.Dataset.QualitySummary.HistoricalStCoverage}");
    Console.WriteLine($"SUSPENSION={result.Dataset.QualitySummary.SuspensionCoverage}");
    Console.WriteLine($"ADJUSTMENT_FACTORS={result.Dataset.QualitySummary.AdjustmentFactorCoverage}");
    Console.WriteLine($"INDEX={result.Dataset.QualitySummary.IndexCoverage}");
    Console.WriteLine($"BENCHMARKS={string.Join(',', result.Dataset.Benchmarks.Keys.OrderBy(value => value, StringComparer.Ordinal))}");
    Console.WriteLine($"UNKNOWN_GAPS={result.Dataset.ObservationDeclarations.Values.Count(item => item.ObservationStatus == HistoricalObservationStatus.UnknownDataGap)}");
    foreach (HistoricalStrategyCapabilityExplanation capability in result.StrategyCapabilities.OrderBy(item => item.Strategy))
    {
        Console.WriteLine($"{capability.Strategy.ToString().ToUpperInvariant()}={capability.Status}");
        Console.WriteLine($"{capability.Strategy.ToString().ToUpperInvariant()}_REASONS={string.Join(',', capability.ReasonCodes)}");
    }
    Console.WriteLine($"OUTPUT={Path.GetFullPath(request.OutputPath)}");
    foreach (string warning in result.Statistics.Warnings) Console.WriteLine($"WARNING={warning}");
    return 0;
}
catch (OperationCanceledException) { Console.Error.WriteLine("CANCELLED"); return 1; }
catch (Exception exception) { Console.Error.WriteLine($"HISTORICAL_DATASET_BUILD_FAILED: {exception.Message}"); return 1; }

string Value(string name, string? fallback = null) => options.TryGetValue(name, out string? value) ? value : fallback ?? throw new ArgumentException($"--{name} is required.");
DateOnly Date(string name) => DateOnly.ParseExact(Value(name), "yyyy-MM-dd", CultureInfo.InvariantCulture);
bool Bool(string name, bool fallback) => options.TryGetValue(name, out string? value) ? bool.Parse(value) : fallback;

async Task<int> AnalyzeBenchmarkAsync()
{
    string datasetPath = Value("dataset");
    HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(datasetPath);
    if (!loaded.Success || loaded.Dataset is null) throw new InvalidOperationException($"Dataset load failed: {string.Join("; ", loaded.Errors)}");

    SparrowStrategyMode strategy = Value("strategy").Trim().ToLowerInvariant() switch
    {
        "classic" => SparrowStrategyMode.Classic,
        "v2" => SparrowStrategyMode.V2,
        _ => throw new ArgumentException("--strategy must be classic or v2.")
    };
    int[] horizons = Value("horizons").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Select(value => int.Parse(value, CultureInfo.InvariantCulture)).Distinct().Order().ToArray();
    if (horizons.Length == 0 || horizons.Any(value => value <= 0)) throw new ArgumentException("--horizons requires positive trading-day values.");
    JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    string parameterJson = await File.ReadAllTextAsync(Value("parameters"));
    SparrowClassicParameterSnapshot? classic = strategy == SparrowStrategyMode.Classic
        ? JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(parameterJson, json) ?? throw new ArgumentException("Classic parameter snapshot is invalid.") : null;
    SparrowV2ParameterSnapshot? v2 = strategy == SparrowStrategyMode.V2
        ? JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(parameterJson, json) ?? throw new ArgumentException("V2 parameter snapshot is invalid.") : null;
    string version = strategy == SparrowStrategyMode.Classic ? SparrowStrategyVersions.Classic : SparrowStrategyVersions.V2;
    SparrowBacktestRequest backtest = new(strategy, version, Date("start"), Date("end"), int.Parse(Value("top-n"), CultureInfo.InvariantCulture), horizons,
        RoundTripCostRate: 0, SlippageRate: 0, ClassicParameters: classic, V2Parameters: v2);
    SparrowBenchmarkAnalysisResult result = new SparrowHistoricalBenchmarkAnalysisEngine().Analyze(loaded.Dataset,
        new SparrowBenchmarkAnalysisRequest(backtest, Value("benchmark")));
    await SparrowHistoricalResultExporter.ExportBenchmarkAnalysisJsonAsync(result, Value("output"));
    Console.WriteLine($"DATASET_ID={result.DatasetId}");
    Console.WriteLine($"DATASET_FINGERPRINT={result.DatasetFingerprint}");
    Console.WriteLine($"ANALYSIS_FINGERPRINT={result.AnalysisFingerprint}");
    Console.WriteLine($"STRATEGY={strategy}");
    Console.WriteLine($"BENCHMARK={result.Request.BenchmarkId}");
    Console.WriteLine($"SUPPORT={result.Support}");
    Console.WriteLine($"SELECTIONS={result.RelativeSelections.Count}");
    foreach (SparrowBenchmarkHorizonMetrics metric in result.HorizonMetrics)
    {
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_SELECTIONS={metric.SelectionCount}");
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_STOCK_AVAILABLE={metric.StockAvailableCount}");
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_BENCHMARK_AVAILABLE={metric.BenchmarkAvailableCount}");
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_EXCESS_AVAILABLE={metric.ExcessAvailableCount}");
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_AVG_EXCESS={metric.AverageExcessReturnPercent?.ToString("R", CultureInfo.InvariantCulture) ?? "N/A"}");
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_MEDIAN_EXCESS={metric.MedianExcessReturnPercent?.ToString("R", CultureInfo.InvariantCulture) ?? "N/A"}");
        Console.WriteLine($"HORIZON_{metric.HorizonTradingDays}_OUTPERFORMANCE={metric.OutperformanceRate?.ToString("R", CultureInfo.InvariantCulture) ?? "N/A"}");
    }
    return result.Support == HistoricalReplaySupport.Supported ? 0 : 1;
}

static Dictionary<string, string> Parse(string[] args)
{
    Dictionary<string, string> output = new(StringComparer.OrdinalIgnoreCase);
    for (int index = 0; index < args.Length; index += 2)
    {
        if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length)
            throw new ArgumentException("Arguments must be --name value pairs.");
        output.Add(args[index][2..], args[index + 1]);
    }
    return output;
}
