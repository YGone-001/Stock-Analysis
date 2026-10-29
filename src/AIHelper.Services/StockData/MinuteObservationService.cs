using System.Globalization;
using System.Text.Json;
using AIHelper.Models;

namespace AIHelper.Services.StockData;

/// <summary>Companion adapter for /api/minute. Never substitutes daily or 30/60-minute bars.</summary>
public sealed class MinuteObservationService(IStockDataProvider provider, TimeProvider? clock = null) : IMinuteObservationService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public async Task<MinuteObservationResult> GetAsync(string symbol, DateOnly date, CancellationToken cancellationToken = default)
    {
        MarketDataMetadata metadata = new("unknown", false, false, false);
        try
        {
            var response = await provider.GetDataAsync(StockDataRequest.Parse($"/api/minute?code={Uri.EscapeDataString(symbol)}&date={date:yyyyMMdd}"), cancellationToken);
            var received = _clock.GetUtcNow();
            metadata = new(response.Source, response.UsedCache, response.IsStale, response.IsBackgroundRefresh, response.SourceKind, response.FallbackReason, response.CacheFreshness);
            if (!response.Success) return new(Array.Empty<MinuteObservation>(), IntradayQuality.SourceFailure, "minute transport/source failure", metadata);
            using var document = JsonDocument.Parse(response.Json);
            DateTimeOffset? originalReceipt = response.UsedCache ? null : response.SourceKind == DataSourceKind.EastMoney ? received : null;
            string providerIdentity = response.SourceKind == DataSourceKind.EastMoney ? "EastMoney/kline/klt=1" : "unknown gateway minute provider";
            if (document.RootElement.TryGetProperty("observation", out var evidence))
            {
                if (evidence.TryGetProperty("provider", out var identity) && identity.GetString() == "EastMoney/trends2/ndays=1")
                    providerIdentity = "EastMoney/trends2/ndays=1";
                if (evidence.TryGetProperty("observed_at_utc", out var receipt) && DateTimeOffset.TryParse(receipt.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var original) && original.Offset == TimeSpan.Zero && original <= received)
                    originalReceipt = original;
                if (evidence.TryGetProperty("used_cache", out var cached) && cached.ValueKind == JsonValueKind.True)
                    metadata = metadata with { UsedCache = true };
            }
            var rows = new List<MinuteObservation>();
            var seen = new HashSet<DateTimeOffset>();
            bool malformed = false;
            var list = document.RootElement.GetProperty("data").GetProperty("List");
            if (list.GetArrayLength() > 500) return new(Array.Empty<MinuteObservation>(), IntradayQuality.Partial, "minute row limit exceeded", metadata);
            foreach (var item in list.EnumerateArray())
            {
                DateTimeOffset? stamp = null;
                if (item.TryGetProperty("Time", out var time) && time.ValueKind == JsonValueKind.String)
                {
                    var text = time.GetString();
                    if (DateTime.TryParseExact(text, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)) stamp = new DateTimeOffset(local, ChinaExchangeTime.Offset);
                    else if (text is not null && (text.EndsWith("+08:00", StringComparison.Ordinal) || text.EndsWith("Z", StringComparison.Ordinal)) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var explicitTime)) stamp = explicitTime;
                }
                if (stamp is { } value && !seen.Add(value)) malformed = true;
                if (stamp is null) malformed = true;
                decimal? price = Number(item, "Price");
                // Both repository providers explicitly apply ToMilli/to_milli to source price.
                // Number/Amount units and interval closure lack a verified declaration: retain source values.
                rows.Add(new(symbol, stamp, originalReceipt ?? received, originalReceipt,
                    providerIdentity.StartsWith("EastMoney/", StringComparison.Ordinal) ? price / 1000m : null, Number(item, "Number"), Number(item, "Amount"), "unknown", "unknown", metadata, TransportReceivedAtUtc: received, Provider: providerIdentity));
            }
            var status = rows.Count == 0 ? IntradayQuality.Unavailable : rows.Any(r => r.ExchangeEventTime is { } t && ChinaExchangeTime.Date(t) != date) ? IntradayQuality.WrongTradingDate : IntradayQuality.Partial;
            return new(rows.OrderBy(r => r.ExchangeEventTime).ToArray(), status,
                rows.Count == 0 ? "requested date unavailable; no arbitrary historical coverage asserted" : malformed ? "invalid/duplicate timestamps; units or interval closure unverified" : "source volume/amount units and minute interval closure unverified", metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new(Array.Empty<MinuteObservation>(), IntradayQuality.SourceFailure, "malformed or unavailable minute response", metadata); }
    }
    private static decimal? Number(JsonElement item, string name) => item.TryGetProperty(name, out var value)
        && decimal.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}
