using System.Text.Json;
using AIHelper.Services.StockData.Sparrow;

namespace AIHelper.Services.StockData;

public static class T1ResearchCommand
{
    internal static Func<HttpClient?>? GatewayClientOverride;
    public static async Task<int> RunAsync(IReadOnlyDictionary<string, string> options, TextWriter output, TextWriter error, CancellationToken token = default)
    {
        try
        {
            if (options.ContainsKey("help"))
            {
                output.WriteLine("--retain-t1 --snapshot FILE --ranking-package DIR --features FILE --archive-root DIR [--test-fixture]");
                output.WriteLine("--probe-t1 --sample DIR --output IGNORED_DIR [--allow-network --provider tushare] [--calendar-days 14] [--import FILE]");
                output.WriteLine("--report-t1 (--sample DIR [--pilot-evidence FILE] | --archive-root DIR [--outcome-root DIR] [--limit 25])");
                output.WriteLine("Default OFFLINE. Network requires explicit tushare selection and existing HISTORICAL_GATEWAY_URL. Credentials stay on the configured gateway. No performance evaluation.");
                return 0;
            }
            string? Value(string name) => options.GetValueOrDefault(name);
            string Required(string name) => Value(name) is { Length: > 0 } value ? value : throw new ArgumentException("REQUIRED_OPTION");
            bool Flag(string name) => options.TryGetValue(name, out string? value) && bool.Parse(value);
            var modes = new[] { "retain-t1", "probe-t1", "report-t1" }.Where(options.ContainsKey).ToArray();
            if (modes.Length != 1) throw new ArgumentException("CONFLICTING_T1_OPERATIONS");
            string[] allowed = modes[0] switch
            {
                "retain-t1" => new[] { "retain-t1", "snapshot", "ranking-package", "features", "archive-root", "test-fixture" },
                "probe-t1" => new[] { "probe-t1", "sample", "output", "allow-network", "provider", "calendar-days", "import" },
                _ => new[] { "report-t1", "sample", "pilot-evidence", "archive-root", "outcome-root", "limit" }
            };
            if (options.Keys.Any(k => !allowed.Contains(k, StringComparer.OrdinalIgnoreCase))) throw new ArgumentException("UNKNOWN_T1_OPTION");
            if (modes[0] == "retain-t1")
            {
                var features = T1ResearchStorage.Load<IntradayFeatureResult[]>(Required("features"));
                var retained = T1ResearchSamples.Retain(Required("snapshot"), Required("ranking-package"), features, Required("archive-root"),
                    Flag("test-fixture") ? T1EvidenceOrigin.TEST_FIXTURE : T1EvidenceOrigin.UNVERIFIED, token: token);
                output.WriteLine("ARCHIVE=" + retained.Directory + "; REUSED=" + retained.ReusedExisting);
                T1ResearchReport.Write(retained.Directory, null, output); return 2;
            }
            if (modes[0] == "report-t1")
            {
                if (Value("sample") is { } sample)
                {
                    if (Value("archive-root") is not null) throw new ArgumentException("AMBIGUOUS_REPORT_SCOPE");
                    T1ResearchReport.Write(sample, Value("pilot-evidence") is { } file ? T1OutcomePilot.Load(file) : null, output);
                }
                else T1ResearchReport.Inventory(Required("archive-root"), int.Parse(Value("limit") ?? "25", System.Globalization.CultureInfo.InvariantCulture), output, Value("outcome-root"));
                return 2;
            }
            if (Value("sample") is null) { output.WriteLine("ENGINEERING_READY; REAL_SAMPLE_NOT_YET_CAPTURED; PRODUCTION_EVALUATION_BLOCKED; COVERAGE=NOT_APPLICABLE"); return 2; }
            string samplePath = Required("sample"); var manifest = T1ResearchSamples.Verify(samplePath);
            string root = T1ResearchStorage.Root(Required("output"), requireIgnored: true);
            string fullSample = T1ResearchStorage.PathChecked(samplePath);
            if (root.Equals(fullSample, StringComparison.OrdinalIgnoreCase) || root.StartsWith(fullSample + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("OUTCOME_OUTPUT_OVERLAPS_IMMUTABLE_SAMPLE");
            bool network = Flag("allow-network"); string? provider = Value("provider");
            int days = int.Parse(Value("calendar-days") ?? "14", System.Globalization.CultureInfo.InvariantCulture);
            if (days is < 1 or > 31 || manifest.Content.Candidates.Count is < 1 or > 20 || provider is not (null or "tushare")) throw new ArgumentException("PILOT_SCOPE_INVALID");
            T1PilotEvidence evidence;
            if (Value("import") is { } input)
            {
                if (network) throw new ArgumentException("IMPORT_MUST_BE_OFFLINE");
                evidence = T1OutcomePilot.Load(input); _ = T1OutcomePilot.Associate(samplePath, evidence);
            }
            else
            {
                output.WriteLine($"SCOPE=one retained decision; DATE={manifest.Content.TradingDate:yyyy-MM-dd} through {manifest.Content.TradingDate.AddDays(days):yyyy-MM-dd}; SYMBOLS={string.Join(',', manifest.Content.Candidates.Select(c => c.Original.Symbol))}; DAILY=each exchange's exact mapped target only; MODE={(network ? "EXPLICIT_NETWORK" : "OFFLINE")}");
                using var client = network && provider == "tushare" ? GatewayClient() : null;
                bool configured = client is not null, credential = false;
                if (client is not null)
                {
                    try
                    {
                        using var response = await client.GetAsync("/health", token).ConfigureAwait(false); response.EnsureSuccessStatusCode();
                        using var health = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false));
                        credential = health.RootElement.GetProperty("providers").GetProperty("tushare").GetBoolean();
                    }
                    catch (Exception ex) when (ex is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException)
                    { output.WriteLine("SOURCE_ERROR: gateway configuration status could not be observed; no provider request made."); T1ResearchReport.Write(samplePath, null, output); return 2; }
                }
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(90));
                evidence = await T1OutcomePilot.AcquireAsync(samplePath, new(network, provider, configured, credential, days),
                    client is null ? null : new HistoricalHttpMarketDataSource(client), token: budget.Token).ConfigureAwait(false);
            }
            string sourceFile = T1OutcomePilot.Publish(evidence, samplePath, root, token);
            var association = T1OutcomePilot.Associate(samplePath, evidence);
            var publication = T1EvidenceReadiness.PublishAssociation(association, Path.Combine(root, "associations"), token, new[] { samplePath, sourceFile });
            output.WriteLine("PILOT_EVIDENCE=" + sourceFile); output.WriteLine("ASSOCIATION_PACKAGE=" + publication.PackageDirectory);
            T1ResearchReport.Write(samplePath, evidence, output); return 2;
        }
        catch (OperationCanceledException) { output.WriteLine("CANCELLED; previously retained samples are preserved."); return 2; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException or FormatException or OverflowException)
        { error.WriteLine("T1_RESEARCH_INPUT_OR_STORAGE_INVALID: check explicit bounded inputs and independent/ignored storage. No source data was replaced."); return 1; }
    }
    private static HttpClient? GatewayClient()
    {
        if (GatewayClientOverride is not null) return GatewayClientOverride();
        string? configured = Environment.GetEnvironmentVariable("HISTORICAL_GATEWAY_URL"); // the existing documented mechanism only
        if (string.IsNullOrWhiteSpace(configured)) return null;
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) throw new ArgumentException("GATEWAY_URL_INVALID");
        return new(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = IntradaySnapshotStore.MaximumBytes };
    }
}
