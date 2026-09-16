using System.Globalization;
using AIHelper.Core.Sparrow;
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

    string source = Value("source", "tushare");
    string adjustment = Value("adjustment", "raw");
    if (!string.Equals(source, "tushare", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only --source tushare is supported.");
    if (!string.Equals(adjustment, "raw", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only --adjustment raw is supported.");
    Uri gateway = new(Environment.GetEnvironmentVariable("HISTORICAL_GATEWAY_URL") ?? throw new InvalidOperationException("HISTORICAL_GATEWAY_URL is required."));
    using HttpClient client = new() { BaseAddress = gateway };
    string[] explicitSymbols = Value("symbols", "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    HistoricalDatasetScope? scope = explicitSymbols.Length == 0 ? null : new HistoricalDatasetScope(HistoricalDatasetScopeKind.ExplicitSymbolSet, Symbols: explicitSymbols);
    HistoricalDatasetBuildRequest request = new(
        Value("dataset-id", $"tushare-{Value("start")}-{Value("end")}"), Date("start"), Date("end"), Value("output"),
        HistoricalPriceAdjustmentMode.Raw,
        IncludeTurnover: Bool("include-turnover", true), IncludeSuspension: Bool("include-suspension", false), IncludeHistoricalSt: Bool("include-st", false),
        IncludeAdjustmentFactors: Bool("include-adjustment-factors", false), IncludeV2IndexContext: Bool("include-v2-index", true), Scope: scope, ExplicitSymbols: explicitSymbols);
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
