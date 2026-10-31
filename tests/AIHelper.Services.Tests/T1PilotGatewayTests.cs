using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using AIHelper.Services.StockData;
using Xunit;
using Fixture = AIHelper.Services.Tests.T1EvidenceReadinessTests.Fixture;

namespace AIHelper.Services.Tests;

[Collection("T1ArtifactWrites")]
public sealed class T1PilotGatewayTests
{
    private sealed class Handler(bool configured, HttpStatusCode status, bool oversized) : HttpMessageHandler
    {
        internal readonly List<string> Requests = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath; Requests.Add(request.RequestUri.PathAndQuery);
            if (path == "/health") return Task.FromResult(Response(new { providers = new { tushare = configured } }));
            if (status != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(status));
            if (oversized) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new string('x', IntradaySnapshotStore.MaximumBytes + 1)) });
            var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(v => v.Split('=', 2)).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));
            Assert.Equal("tushare", query["source"]);
            DateOnly start = DateOnly.Parse(query["start_date"]), end = DateOnly.Parse(query["end_date"]);
            if (path == "/api/historical/calendar")
            {
                int count = end.DayNumber - start.DayNumber + 1;
                var data = Enumerable.Range(0, count).Select(n => new { trading_date = start.AddDays(n), is_open = n is 0 or 3, previous_open_date = n == 0 ? start.AddDays(-1) : start, source = "tushare" }).ToArray();
                return Task.FromResult(Response(new { data, evidence = Evidence("trade_cal", start, end, count, query["exchange"], null) }));
            }
            Assert.Equal("/api/historical/daily", path); Assert.Equal(start, end); Assert.Equal("raw", query["adjustment"]);
            string id = query["ts_code"];
            return Task.FromResult(Response(new { data = new[] { new { symbol = id[..6], ts_code = id, trading_date = start, open = 10, high = 11, low = 9, close = 10.5, previous_close = 10, volume = 100, amount = 1000, source = "tushare" } }, evidence = Evidence("daily", start, end, 1, null, id) }));
        }
        private static object[] Evidence(string endpoint, DateOnly start, DateOnly end, int count, string? exchange, string? symbol) => new object[]
        {
            new { endpoint, source = "tushare", scope = new { start_date = start, end_date = end, exchange, symbol }, query_count = 1,
                requested_chunks = new[] { new { start_date = start, end_date = end, returned_rows = count, response_cap_hit = false } },
                returned_rows = count, response_cap_hit = false, duplicate_rows = 0, invalid_rows = 0, retry_count = 0,
                rate_limit_events = 0, permission_denied_events = 0, expected_count = count, observed_count = count, missing_count = 0,
                coverage_status = "Full", proof_method = "synthetic exact scope fixture" }
        };
        private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
    [Theory]
    [InlineData(true, HttpStatusCode.OK, false, "SOURCE_DECLARED")]
    [InlineData(false, HttpStatusCode.OK, false, "CREDENTIAL_REQUIRED")]
    [InlineData(true, HttpStatusCode.Forbidden, false, "PERMISSION_DENIED")]
    [InlineData(true, HttpStatusCode.TooManyRequests, false, "RATE_LIMITED")]
    [InlineData(true, HttpStatusCode.OK, true, "SOURCE_ERROR")]
    public async Task RealHttpAdapterAndGatewayConfigurationPathWorkEntirelyOffline(bool configured, HttpStatusCode status, bool oversized, string expected)
    {
        using var f = new Fixture(); int days = new DateOnly(2010, 10, 29).DayNumber - f.Snapshot.TradingDate.DayNumber;
        var original = f.Snapshot;
        var old = original with { TradingDate = original.TradingDate.AddDays(days), RequestedAsOf = original.RequestedAsOf.AddDays(days),
            AcquisitionStartedAtUtc = original.AcquisitionStartedAtUtc.AddDays(days), CapturedAtUtc = original.CapturedAtUtc.AddDays(days),
            Scan = original.Scan with { ScanStartedAtUtc = original.Scan.ScanStartedAtUtc.AddDays(days), ScanCompletedAtUtc = original.Scan.ScanCompletedAtUtc.AddDays(days) },
            Symbols = original.Symbols.Select(c => c with { Minutes = c.Minutes with { Rows = c.Minutes.Rows.Select(r => r with
                { ExchangeEventTime = r.ExchangeEventTime?.AddDays(days), AvailableAtUtc = r.AvailableAtUtc?.AddDays(days), ObservedAtUtc = r.ObservedAtUtc.AddDays(days), TransportReceivedAtUtc = r.TransportReceivedAtUtc?.AddDays(days) }).ToArray() } }).ToArray() };
        var (input, features) = T1ResearchRetentionTests.Inputs(f, old);
        var archive = T1ResearchSamples.Retain(input.Evidence.SnapshotPath!, input.Evidence.PackageDirectory!, features, Path.Combine(f.Root, "archive"), T1EvidenceOrigin.TEST_FIXTURE);
        var handler = new Handler(configured, status, oversized);
        T1ResearchCommand.GatewayClientOverride = () => new HttpClient(handler, disposeHandler: false) { BaseAddress = new("http://fixture.invalid"), MaxResponseContentBufferSize = IntradaySnapshotStore.MaximumBytes };
        var writer = new StringWriter(); var error = new StringWriter();
        try
        {
            var options = new Dictionary<string, string> { ["probe-t1"] = "true", ["sample"] = archive.Directory,
                ["output"] = T1OutcomeAssociationTests.IgnoredStorage(f), ["allow-network"] = "true", ["provider"] = "tushare" };
            Assert.Equal(2, await T1ResearchCommand.RunAsync(options, writer, error)); Assert.Empty(error.ToString());
            Assert.Contains(expected, writer.ToString());
            if (!configured) Assert.Equal(new[] { "/health" }, handler.Requests);
            else if (status != HttpStatusCode.OK || oversized) Assert.Equal(2, handler.Requests.Count); // health then one bounded calendar attempt
            else
            {
                Assert.Equal(7, handler.Requests.Count); // health + one exchange calendar + five exact-target daily envelopes
                Assert.Contains("OBSERVED_DAILY_RECORDS=5", writer.ToString()); Assert.Contains("UNKNOWN_OR_BLOCKED_BASIS=5", writer.ToString());
            }
            Assert.DoesNotContain(handler.Requests, r => r.Contains("capabilities") || r.Contains("securities") || r.Contains("suspensions") || r.Contains("minute"));
            Assert.Contains("AUTHENTICATED_PRODUCTION_SAMPLES=0", writer.ToString());
        }
        finally { T1ResearchCommand.GatewayClientOverride = null; handler.Dispose(); }
    }
}
