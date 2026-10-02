using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;
using AIHelper.Models;
using AIHelper.Services.StockData.Sparrow;

Dictionary<string, string> options = Parse(args);
try
{
    List<string> detectedModes = new();
    if (Bool("report-legacy-capability", false)) detectedModes.Add("legacy-capability");
    if (options.ContainsKey("verify-reproducibility")) detectedModes.Add("reproducibility-verification");
    if (options.ContainsKey("reproduce-experiment")) detectedModes.Add("research-reexecution");
    if (options.ContainsKey("report-experiment") || options.ContainsKey("report-lineage") || options.ContainsKey("report-comparison")) detectedModes.Add("research-reporting");
    if (Bool("analyze-benchmark", false)) detectedModes.Add("benchmark-analysis");
    if (Bool("export-portfolio-research", false)) detectedModes.Add("portfolio-research-export");
    if (options.ContainsKey("source") || options.ContainsKey("dataset-id") || options.ContainsKey("symbols") || options.ContainsKey("benchmarks"))
        detectedModes.Add("dataset-build");

    if (detectedModes.Count > 1)
    {
        Console.Error.WriteLine($"CONFLICTING_OPERATION_MODES: Multiple operational modes were specified: {string.Join(", ", detectedModes)}");
        return 1;
    }

    if (Bool("report-legacy-capability", false))
    {
        Console.WriteLine("LEGACY=Unsupported");
        Console.WriteLine($"LEGACY_VERSION={SparrowStrategyVersions.Legacy}");
        Console.WriteLine($"LEGACY_REASONS={string.Join(',', SparrowLegacyHistoricalReplayCapability.BlockerReasonCodes)}");
        return 0;
    }
    if (options.TryGetValue("verify-reproducibility", out string? verifyExperimentId))
        return await VerifyReproducibilityAsync(verifyExperimentId);
    if (options.TryGetValue("reproduce-experiment", out string? reproduceExperimentId))
        return await ReproduceExperimentAsync(reproduceExperimentId);
    if (options.ContainsKey("report-experiment") || options.ContainsKey("report-lineage") || options.ContainsKey("report-comparison"))
        return await ExportResearchReportAsync();
    if (Bool("analyze-benchmark", false))
        return await AnalyzeBenchmarkAsync();
    if (Bool("export-portfolio-research", false))
        return await ExportPortfolioResearchAsync();

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

async Task<int> VerifyReproducibilityAsync(string experimentId)
{
    string experimentStore = Value("experiment-store");
    string artifactPath = Value("artifact");

    if (!File.Exists(artifactPath))
    {
        Console.Error.WriteLine($"ARTIFACT_NOT_FOUND: Artifact file '{artifactPath}' was not found.");
        return 1;
    }

    JsonResearchExperimentRepository repository = new(experimentStore);
    PersistedResearchExperimentRecord record;
    try
    {
        record = await repository.GetAsync(experimentId);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"EXPERIMENT_LOAD_FAILED: {exception.Message}");
        return 1;
    }

    SparrowResearchReproducibilityVerifier verifier = new();
    ResearchReproducibilityVerificationResult result;
    try
    {
        result = await verifier.VerifyAsync(record, artifactPath);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"VERIFICATION_FAILED: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"VERIFICATION_STATUS={result.Status}");
    if (result.ExperimentId is not null) Console.WriteLine($"EXPERIMENT_ID={result.ExperimentId}");
    if (result.ExperimentFingerprint is not null) Console.WriteLine($"EXPERIMENT_FINGERPRINT={result.ExperimentFingerprint}");
    if (result.DatasetFingerprint is not null) Console.WriteLine($"DATASET_FINGERPRINT={result.DatasetFingerprint}");
    if (result.ArtifactVersion is not null) Console.WriteLine($"ARTIFACT_VERSION={result.ArtifactVersion}");
    if (result.ArtifactFingerprint is not null) Console.WriteLine($"ARTIFACT_FINGERPRINT={result.ArtifactFingerprint}");
    Console.WriteLine($"CHECK_COUNT={result.CheckCount}");
    Console.WriteLine($"FAILED_CHECK_COUNT={result.FailedCheckCount}");
    foreach (ResearchReproducibilityCheck check in result.Checks)
    {
        Console.WriteLine($"CHECK={check.Code}:{check.Status}");
    }
    foreach (string reason in result.ReasonCodes)
    {
        Console.WriteLine($"REASON={reason}");
    }

    return result.Status == ResearchReproducibilityVerificationStatus.Verified ? 0 : 1;
}

async Task<int> ReproduceExperimentAsync(string experimentId)
{
    string experimentStore = Value("experiment-store");
    string artifactPath = Value("artifact");
    string datasetPath = Value("dataset");
    string? parametersPath = options.TryGetValue("parameters", out string? p) ? p : null;
    string? reproductionOutputPath = options.TryGetValue("reproduction-output", out string? ro) ? ro : null;

    if (!File.Exists(artifactPath))
    {
        Console.Error.WriteLine($"ARTIFACT_NOT_FOUND: Artifact file '{artifactPath}' was not found.");
        return 1;
    }

    if (!File.Exists(datasetPath))
    {
        Console.Error.WriteLine($"DATASET_NOT_FOUND: Dataset file '{datasetPath}' was not found.");
        return 1;
    }

    JsonResearchExperimentRepository repository = new(experimentStore);
    PersistedResearchExperimentRecord record;
    try
    {
        record = await repository.GetAsync(experimentId);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"EXPERIMENT_LOAD_FAILED: {exception.Message}");
        return 1;
    }

    SparrowResearchReexecutionValidator validator = new();
    ResearchReexecutionValidationResult result;
    try
    {
        result = await validator.ValidateReexecutionAsync(record, artifactPath, datasetPath, parametersPath);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"REEXECUTION_VALIDATION_FAILED: {exception.Message}");
        return 1;
    }

    Console.WriteLine($"REEXECUTION_STATUS={result.Status}");
    void WriteField(string key, string? val) { if (!string.IsNullOrWhiteSpace(val)) Console.WriteLine($"{key}={val}"); }
    WriteField("EXPERIMENT_ID", result.ExperimentId);
    WriteField("EXPERIMENT_FINGERPRINT", result.ExperimentFingerprint);
    WriteField("DATASET_FINGERPRINT", result.DatasetFingerprint);
    WriteField("PARAMETER_FINGERPRINT", result.StrategyParameterFingerprint);
    WriteField("ORIGINAL_ARTIFACT_FINGERPRINT", result.OriginalArtifactFingerprint);
    WriteField("REPRODUCED_ARTIFACT_FINGERPRINT", result.ReproducedArtifactFingerprint);
    Console.WriteLine($"CHECK_COUNT={result.CheckCount}");
    Console.WriteLine($"FAILED_CHECK_COUNT={result.FailedCheckCount}");
    foreach (var check in result.Checks) Console.WriteLine($"CHECK={check.Code}:{check.Status}");
    foreach (var reason in result.ReasonCodes) Console.WriteLine($"REASON={reason}");

    if (!string.IsNullOrWhiteSpace(reproductionOutputPath))
    {
        string? directory = Path.GetDirectoryName(reproductionOutputPath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        JsonSerializerOptions outputOptions = new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        };
        await File.WriteAllTextAsync(reproductionOutputPath, JsonSerializer.Serialize(result, outputOptions));
    }

    return result.Status == ResearchReexecutionStatus.Equivalent ? 0 : 1;
}

async Task<int> ExportResearchReportAsync()
{
    int selected = (options.ContainsKey("report-experiment") ? 1 : 0) + (options.ContainsKey("report-lineage") ? 1 : 0) + (options.ContainsKey("report-comparison") ? 1 : 0);
    if (selected != 1) throw new ArgumentException("Specify exactly one of --report-experiment, --report-lineage, or --report-comparison.");
    string format = Value("report-format", "markdown").Trim().ToLowerInvariant();
    if (format is not ("markdown" or "json")) throw new ArgumentException("--report-format must be markdown or json.");
    JsonResearchExperimentRepository repository = new(Value("experiment-store"));
    ResearchExperimentReportBuilder builder = new();
    MarkdownResearchExperimentReportRenderer renderer = new();
    ResearchExperimentReportExporter exporter = new(renderer);
    string output = Value("output");
    if (options.TryGetValue("report-experiment", out string? experimentId))
    {
        PersistedResearchExperimentRecord record = await repository.GetAsync(experimentId);
        ResearchExperimentReport report = builder.BuildExperimentReport(record);
        if (format == "markdown") await exporter.ExportMarkdownAsync(report, output); else await exporter.ExportJsonAsync(report, output);
        Console.WriteLine($"REPORT_TYPE={report.Identity.ReportType}"); Console.WriteLine($"REPORT_FINGERPRINT={report.Identity.ReportFingerprint}");
    }
    else if (options.TryGetValue("report-lineage", out experimentId))
    {
        PersistedResearchExperimentRecord record = await repository.GetAsync(experimentId);
        ResearchExperimentLineageReport report = builder.BuildLineageReport(record);
        if (format == "markdown") await exporter.ExportMarkdownAsync(report, output); else await exporter.ExportJsonAsync(report, output);
        Console.WriteLine($"REPORT_TYPE={report.Identity.ReportType}"); Console.WriteLine($"REPORT_FINGERPRINT={report.Identity.ReportFingerprint}");
    }
    else
    {
        string[] identifiers = Value("report-comparison").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (identifiers.Length != 2 || string.Equals(identifiers[0], identifiers[1], StringComparison.Ordinal)) throw new ArgumentException("--report-comparison requires two distinct comma-separated experiment IDs.");
        PersistedResearchExperimentRecord first = await repository.GetAsync(identifiers[0]);
        PersistedResearchExperimentRecord second = await repository.GetAsync(identifiers[1]);
        ResearchExperimentComparisonReport report = builder.BuildComparisonReport(new ResearchExperimentComparisonService().Compare(first, second));
        if (format == "markdown") await exporter.ExportMarkdownAsync(report, output); else await exporter.ExportJsonAsync(report, output);
        Console.WriteLine($"REPORT_TYPE={report.Identity.ReportType}"); Console.WriteLine($"REPORT_FINGERPRINT={report.Identity.ReportFingerprint}");
    }
    Console.WriteLine($"OUTPUT={Path.GetFullPath(output)}");
    return 0;
}

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

async Task<int> ExportPortfolioResearchAsync()
{
    if (!Bool("simulate-portfolio", false) || !Bool("analyze-portfolio", false))
        throw new ArgumentException("Portfolio research export requires --simulate-portfolio true and --analyze-portfolio true.");
    HistoricalDatasetLoadResult loaded = await new HistoricalDatasetJsonLoader().LoadAsync(Value("dataset"));
    if (!loaded.Success || loaded.Dataset is null) throw new InvalidOperationException($"Dataset load failed: {string.Join("; ", loaded.Errors)}");
    SparrowStrategyMode strategy = Value("strategy").Trim().ToLowerInvariant() switch
    {
        "classic" => SparrowStrategyMode.Classic,
        "v2" => SparrowStrategyMode.V2,
        _ => throw new ArgumentException("--strategy must be classic or v2.")
    };
    JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    SparrowClassicParameterSnapshot? classic = null;
    SparrowV2ParameterSnapshot? v2 = null;
    if (options.TryGetValue("parameters", out string? parameterPath))
    {
        string parameterJson = await File.ReadAllTextAsync(parameterPath);
        classic = strategy == SparrowStrategyMode.Classic ? JsonSerializer.Deserialize<SparrowClassicParameterSnapshot>(parameterJson, json) : null;
        v2 = strategy == SparrowStrategyMode.V2 ? JsonSerializer.Deserialize<SparrowV2ParameterSnapshot>(parameterJson, json) : null;
    }
    else
    {
        classic = strategy == SparrowStrategyMode.Classic ? SparrowClassicParameterSnapshot.From(new SparrowClassicScanParameters()) : null;
        v2 = strategy == SparrowStrategyMode.V2 ? SparrowV2ParameterSnapshot.From(new SparrowScanParameters()) : null;
    }
    if (strategy == SparrowStrategyMode.Classic && classic is null || strategy == SparrowStrategyMode.V2 && v2 is null)
        throw new ArgumentException("Strategy parameter snapshot is invalid.");
    string version = strategy == SparrowStrategyMode.Classic ? SparrowStrategyVersions.Classic : SparrowStrategyVersions.V2;
    int topN = int.Parse(Value("top-n"), CultureInfo.InvariantCulture);
    int horizon = int.Parse(Value("horizon"), CultureInfo.InvariantCulture);
    decimal initialCapital = decimal.Parse(Value("initial-capital"), CultureInfo.InvariantCulture);
    SparrowBacktestRequest backtestRequest = new(strategy, version, Date("start"), Date("end"), topN, new[] { horizon }, ClassicParameters: classic, V2Parameters: v2);
    SparrowBacktestResult backtest = new SparrowHistoricalBacktestEngine().Run(loaded.Dataset, backtestRequest);
    PortfolioSimulationRequest portfolioRequest = new(loaded.Dataset.DatasetId, loaded.Dataset.Fingerprint, strategy, version,
        backtest.ParameterFingerprint, backtestRequest.StartDate, backtestRequest.EndDate, topN, horizon, initialCapital,
        PortfolioPositionSizingMethod.EqualWeight, 0, 0);
    SparrowPortfolioSimulationResult simulation = await new SparrowPortfolioSimulationEngine().SimulateAsync(backtest, portfolioRequest, loaded.Dataset);
    SparrowPortfolioPerformanceResult performance = await new SparrowPortfolioPerformanceAnalyzer().AnalyzeAsync(simulation, loaded.Dataset);
    SparrowPortfolioResearchArtifact artifact = await new SparrowPortfolioResearchExporter().ExportAsync(performance, Value("output"));
    Console.WriteLine($"DATASET={loaded.Dataset.DatasetId}");
    Console.WriteLine($"FINGERPRINT={artifact.DatasetFingerprint}");
    Console.WriteLine($"ANALYSIS_FINGERPRINT={artifact.AnalysisFingerprint}");
    Console.WriteLine($"ARTIFACT_FINGERPRINT={artifact.ArtifactFingerprint}");
    Console.WriteLine($"STRATEGY={artifact.Strategy.Mode}:{artifact.Strategy.Version}");
    Console.WriteLine($"PORTFOLIO={artifact.PortfolioConfigurationFingerprint}");
    Console.WriteLine($"INITIAL_CAPITAL={artifact.PerformanceSummary.InitialCapital.ToString(CultureInfo.InvariantCulture)}");
    Console.WriteLine($"FINAL_EQUITY={artifact.PerformanceSummary.FinalEquity.ToString(CultureInfo.InvariantCulture)}");
    Console.WriteLine($"TOTAL_RETURN_PERCENT={artifact.PerformanceSummary.TotalReturnPercent.ToString("R", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"MAXIMUM_DRAWDOWN_PERCENT={artifact.PerformanceSummary.MaximumDrawdownPercent.ToString("R", CultureInfo.InvariantCulture)}");
    Console.WriteLine($"TRADE_COUNT={artifact.PerformanceSummary.TradeCount}");
    Console.WriteLine($"OUTPUT_FILE={Path.GetFullPath(Value("output"))}");
    return 0;
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
