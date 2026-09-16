using System.Globalization;
using AIHelper.Core.Sparrow;
using AIHelper.Services.StockData.Sparrow;

Dictionary<string, string> options = Parse(args);
try
{
    string source = Value("source", "tushare");
    string adjustment = Value("adjustment", "raw");
    if (!string.Equals(source, "tushare", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only --source tushare is supported.");
    if (!string.Equals(adjustment, "raw", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Only --adjustment raw is supported.");
    Uri gateway = new(Environment.GetEnvironmentVariable("HISTORICAL_GATEWAY_URL") ?? throw new InvalidOperationException("HISTORICAL_GATEWAY_URL is required."));
    using HttpClient client = new() { BaseAddress = gateway };
    HistoricalDatasetBuildRequest request = new(
        Value("dataset-id", $"tushare-{Value("start")}-{Value("end")}"), Date("start"), Date("end"), Value("output"),
        HistoricalPriceAdjustmentMode.Raw,
        Bool("include-turnover", true), Bool("include-suspension", false), Bool("include-st", false), Bool("include-v2-index", true));
    HistoricalDatasetBuildResult result = await new HistoricalDatasetBuilder(new HistoricalHttpMarketDataSource(client)).BuildAsync(request);
    Console.WriteLine($"DATASET={result.Dataset.DatasetId}");
    Console.WriteLine($"FINGERPRINT={result.Dataset.Fingerprint}");
    Console.WriteLine($"QUALITY={(result.IsPartial ? "PARTIAL" : "SOURCE_BACKED")}");
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
