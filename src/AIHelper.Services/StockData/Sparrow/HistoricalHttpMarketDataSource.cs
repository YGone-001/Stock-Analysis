using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHelper.Core.Sparrow;

namespace AIHelper.Services.StockData.Sparrow;

/// <summary>Typed client for the source-locked FastAPI historical contract.  It has no live-provider fallback.</summary>
public sealed class HistoricalHttpMarketDataSource : IHistoricalMarketDataSource
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _client;

    public HistoricalHttpMarketDataSource(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        if (_client.BaseAddress is null) throw new ArgumentException("Historical client requires an explicit gateway BaseAddress.", nameof(client));
    }

    public async Task<IReadOnlyList<HistoricalSourceCapability>> ProbeAsync(CancellationToken cancellationToken = default)
    {
        CapabilityEnvelope? result = await GetAsync<CapabilityEnvelope>("/api/historical/capabilities?source=tushare", cancellationToken);
        return result.Capabilities.Select(item => new HistoricalSourceCapability(item.Capability, item.Status, item.Detail ?? "")).ToArray();
    }

    public async Task<IReadOnlyList<HistoricalSourceSecurity>> GetSecuritiesAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        DataEnvelope<SecurityDto>? result = await GetAsync<DataEnvelope<SecurityDto>>(Range("/api/historical/securities", startDate, endDate), cancellationToken);
        return result.Data.Select(item => new HistoricalSourceSecurity(item.Symbol, item.TsCode, item.Name, item.Market, item.Exchange, item.ListStatus, item.ListDate, item.DelistDate, item.Source ?? "tushare")).ToArray();
    }

    public async Task<IReadOnlyList<HistoricalCalendarDay>> GetCalendarAsync(string exchange, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        DataEnvelope<CalendarDto>? result = await GetAsync<DataEnvelope<CalendarDto>>(Range("/api/historical/calendar", startDate, endDate) + "&exchange=" + Uri.EscapeDataString(exchange), cancellationToken);
        return result.Data.Select(item => new HistoricalCalendarDay(item.TradingDate, item.IsOpen, item.PreviousOpenDate, item.Source ?? "tushare")).ToArray();
    }

    public async Task<IReadOnlyList<HistoricalDailyPrice>> GetDailyPricesAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        DataEnvelope<DailyDto>? result = await GetAsync<DataEnvelope<DailyDto>>(Range("/api/historical/daily", startDate, endDate) + "&ts_code=" + Uri.EscapeDataString(tsCode) + "&adjustment=raw", cancellationToken);
        return result.Data.Select(item => new HistoricalDailyPrice(item.Symbol, item.TsCode, item.TradingDate, item.Open, item.High, item.Low, item.Close, item.PreviousClose, item.Change, item.Percent, item.Volume, item.Amount, item.Source ?? "tushare", HistoricalPriceAdjustmentMode.Raw)).ToArray();
    }

    public async Task<IReadOnlyList<HistoricalTurnover>> GetTurnoverAsync(string tsCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        DataEnvelope<TurnoverDto>? result = await GetAsync<DataEnvelope<TurnoverDto>>(Range("/api/historical/turnover", startDate, endDate) + "&ts_code=" + Uri.EscapeDataString(tsCode), cancellationToken);
        return result.Data.Select(item => new HistoricalTurnover(item.Symbol, item.TsCode, item.TradingDate, item.TurnoverRate, item.Source ?? "tushare")).ToArray();
    }

    public async Task<IReadOnlyList<HistoricalIndexDaily>> GetIndexDailyAsync(string indexCode, DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        DataEnvelope<IndexDto>? result = await GetAsync<DataEnvelope<IndexDto>>(Range("/api/historical/index-daily", startDate, endDate) + "&index_code=" + Uri.EscapeDataString(indexCode), cancellationToken);
        return result.Data.Select(item => new HistoricalIndexDaily(item.IndexCode, item.TradingDate, item.Close, item.PreviousClose, item.Percent, item.Source ?? "tushare")).ToArray();
    }

    public async Task<IReadOnlyList<HistoricalSuspension>> GetSuspensionsAsync(DateOnly startDate, DateOnly endDate, CancellationToken cancellationToken = default)
    {
        DataEnvelope<SuspensionDto>? result = await GetAsync<DataEnvelope<SuspensionDto>>(Range("/api/historical/suspensions", startDate, endDate), cancellationToken);
        return result.Data.Select(item => new HistoricalSuspension(item.Symbol, item.TsCode, item.TradingDate, item.Action, item.Timing, item.Source ?? "tushare")).ToArray();
    }

    private async Task<T> GetAsync<T>(string relativePath, CancellationToken cancellationToken) where T : class
    {
        using HttpResponseMessage response = await _client.GetAsync(relativePath, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden) throw new HistoricalSourceAccessException(HistoricalSourceCapabilityStatus.PermissionDenied, "Tushare historical source denied permission.");
        if (response.StatusCode == HttpStatusCode.ServiceUnavailable) throw new HistoricalSourceAccessException(HistoricalSourceCapabilityStatus.Unavailable, "Tushare historical source is unavailable.");
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, cancellationToken).ConfigureAwait(false)
            ?? throw new HistoricalSourceAccessException(HistoricalSourceCapabilityStatus.Unknown, "Historical gateway returned an empty JSON body.");
    }

    private static string Range(string path, DateOnly start, DateOnly end) => $"{path}?source=tushare&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}";

    public sealed class HistoricalSourceAccessException : Exception
    {
        public HistoricalSourceAccessException(HistoricalSourceCapabilityStatus status, string message) : base(message) => Status = status;
        public HistoricalSourceCapabilityStatus Status { get; }
    }

    private sealed class CapabilityEnvelope { public List<CapabilityDto> Capabilities { get; set; } = []; }
    private sealed class CapabilityDto { public string Capability { get; set; } = ""; public HistoricalSourceCapabilityStatus Status { get; set; } public string? Detail { get; set; } }
    private sealed class DataEnvelope<T> { public List<T> Data { get; set; } = []; }
    private sealed class SecurityDto { public string Symbol { get; set; } = ""; [JsonPropertyName("ts_code")] public string TsCode { get; set; } = ""; public string Name { get; set; } = ""; public string? Market { get; set; } public string? Exchange { get; set; } [JsonPropertyName("list_status")] public string? ListStatus { get; set; } [JsonPropertyName("list_date")] public DateOnly? ListDate { get; set; } [JsonPropertyName("delist_date")] public DateOnly? DelistDate { get; set; } public string? Source { get; set; } }
    private sealed class CalendarDto { [JsonPropertyName("trading_date")] public DateOnly TradingDate { get; set; } [JsonPropertyName("is_open")] public bool IsOpen { get; set; } [JsonPropertyName("previous_open_date")] public DateOnly? PreviousOpenDate { get; set; } public string? Source { get; set; } }
    private sealed class DailyDto { public string Symbol { get; set; } = ""; [JsonPropertyName("ts_code")] public string TsCode { get; set; } = ""; [JsonPropertyName("trading_date")] public DateOnly TradingDate { get; set; } public double? Open { get; set; } public double? High { get; set; } public double? Low { get; set; } public double? Close { get; set; } [JsonPropertyName("previous_close")] public double? PreviousClose { get; set; } public double? Change { get; set; } public double? Percent { get; set; } public double? Volume { get; set; } public double? Amount { get; set; } public string? Source { get; set; } }
    private sealed class TurnoverDto { public string Symbol { get; set; } = ""; [JsonPropertyName("ts_code")] public string TsCode { get; set; } = ""; [JsonPropertyName("trading_date")] public DateOnly TradingDate { get; set; } [JsonPropertyName("turnover_rate")] public double? TurnoverRate { get; set; } public string? Source { get; set; } }
    private sealed class IndexDto { [JsonPropertyName("index_code")] public string IndexCode { get; set; } = ""; [JsonPropertyName("trading_date")] public DateOnly TradingDate { get; set; } public double? Close { get; set; } [JsonPropertyName("previous_close")] public double? PreviousClose { get; set; } public double? Percent { get; set; } public string? Source { get; set; } }
    private sealed class SuspensionDto { public string Symbol { get; set; } = ""; [JsonPropertyName("ts_code")] public string TsCode { get; set; } = ""; [JsonPropertyName("trading_date")] public DateOnly TradingDate { get; set; } public string Action { get; set; } = ""; public string? Timing { get; set; } public string? Source { get; set; } }
}
