using System.Collections.Concurrent;
using System.Text.Json;
using AIHelper.Models;
using Serilog;

namespace AIHelper.Services.StockData.Sparrow;

internal enum SparrowKlineFetchFailureReason
{
    Timeout,
    RequestFailed,
    EmptyResponse,
    InvalidResponse
}

internal sealed record SparrowKlineFetchOutcome(
    string Code,
    string? Json,
    SparrowKlineFetchFailureReason? FailureReason,
    string? Error,
    KlineSeries? Series = null)
{
    public bool Success => FailureReason == null && !string.IsNullOrWhiteSpace(Json);
}

internal sealed class SparrowKlineFetchStatistics
{
    private int _downloaded;
    private int _timeout;
    private int _requestFailed;
    private int _emptyResponse;
    private readonly ConcurrentQueue<string> _timeoutExamples = new();
    private readonly ConcurrentQueue<string> _failureExamples = new();

    public int Downloaded => Volatile.Read(ref _downloaded);
    public int Timeout => Volatile.Read(ref _timeout);
    public int RequestFailed => Volatile.Read(ref _requestFailed);
    public int EmptyResponse => Volatile.Read(ref _emptyResponse);
    public int Unavailable => Timeout + RequestFailed + EmptyResponse;

    public void Record(SparrowKlineFetchOutcome outcome)
    {
        if (outcome.Success)
        {
            Interlocked.Increment(ref _downloaded);
            return;
        }

        switch (outcome.FailureReason)
        {
            case SparrowKlineFetchFailureReason.Timeout:
                Interlocked.Increment(ref _timeout);
                EnqueueExample(_timeoutExamples, outcome.Code);
                break;
            case SparrowKlineFetchFailureReason.EmptyResponse:
                Interlocked.Increment(ref _emptyResponse);
                EnqueueExample(_failureExamples, outcome.Code);
                break;
            default:
                Interlocked.Increment(ref _requestFailed);
                EnqueueExample(_failureExamples, outcome.Code);
                break;
        }
    }

    public string Format(
        string title,
        int p3Candidates,
        int cacheHit,
        int networkRequested,
        int usableKline,
        TimeSpan elapsed)
    {
        string timeoutExamples = FormatExamples("Timeout examples", _timeoutExamples);
        string failureExamples = FormatExamples("Failure examples", _failureExamples);
        return $"\n========== {title} Kline Fetch ==========\n\n" +
               $"P3 candidates:      {p3Candidates}\n" +
               $"Cache hit:          {cacheHit}\n" +
               $"Network requested:  {networkRequested}\n" +
               $"Downloaded:         {Downloaded}\n" +
               $"Timeout:            {Timeout}\n" +
               $"Request failed:     {RequestFailed}\n" +
               $"Empty response:     {EmptyResponse}\n" +
               $"Unavailable:        {Unavailable}\n" +
               $"Usable Kline:       {usableKline}\n" +
               $"Elapsed:            {elapsed.TotalSeconds:F1}s\n" +
               timeoutExamples + failureExamples +
               "\n=============================================\n";
    }

    private static void EnqueueExample(ConcurrentQueue<string> queue, string code)
    {
        if (queue.Count < 5)
        {
            queue.Enqueue(code);
        }
    }

    private static string FormatExamples(string title, ConcurrentQueue<string> examples) =>
        examples.IsEmpty ? "" : $"{title}: {string.Join(", ", examples.Take(5))}\n";
}

internal static class SparrowKlineFetchHelper
{
	public static async Task<SparrowKlineFetchOutcome> FetchDailyAsync(
		IKlineService klineService,
		string code,
		int limit,
		bool forceRefresh,
		CancellationToken callerToken)
	{
		ArgumentNullException.ThrowIfNull(klineService);
		try
		{
			callerToken.ThrowIfCancellationRequested();
			MarketDataResult<KlineSeries?> response = await klineService.GetDailyAsync(code, limit, forceRefresh, callerToken);
			if (!response.Success)
			{
				Log.Warning("Sparrow typed Kline request failed for {Code}; Error={Error}", code, response.Error);
				return new SparrowKlineFetchOutcome(code, null, SparrowKlineFetchFailureReason.RequestFailed, response.Error);
			}
			if (response.Data == null || response.Data.Bars.Count == 0)
			{
				return new SparrowKlineFetchOutcome(code, null, SparrowKlineFetchFailureReason.EmptyResponse, "Empty Kline response");
			}
			if (SparrowKlineSnapshotFactory.FromSeries(response.Data) is not { ClosesNewestFirst.Count: > 0 })
			{
				return new SparrowKlineFetchOutcome(code, null, SparrowKlineFetchFailureReason.InvalidResponse, "Invalid Kline response");
			}
			return new SparrowKlineFetchOutcome(code, SerializeForLegacySparrow(response.Data), null, null, response.Data);
		}
		catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
		{
			throw;
		}
		catch (OperationCanceledException ex)
		{
			Log.Warning(ex, "Sparrow typed Kline timeout for {Code}", code);
			return new SparrowKlineFetchOutcome(code, null, SparrowKlineFetchFailureReason.Timeout, ex.Message);
		}
		catch (MarketDataContractException ex)
		{
			Log.Warning(ex, "Sparrow typed Kline contract was invalid for {Code}", code);
			return new SparrowKlineFetchOutcome(code, null, SparrowKlineFetchFailureReason.InvalidResponse, ex.Message);
		}
		catch (Exception ex)
		{
			Log.Warning(ex, "Sparrow typed Kline fetch failed for {Code}", code);
			return new SparrowKlineFetchOutcome(code, null, SparrowKlineFetchFailureReason.RequestFailed, ex.Message);
		}
	}

    public static async Task<SparrowKlineFetchOutcome> FetchAsync(
        IStockDataProvider dataProvider,
        string code,
        StockDataRequest request,
        Func<string, bool> isUsableResponse,
        CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(dataProvider);
        ArgumentNullException.ThrowIfNull(isUsableResponse);

        try
        {
            callerToken.ThrowIfCancellationRequested();
            StockDataResult response = await dataProvider.GetDataAsync(request, callerToken);
            if (!response.Success)
            {
                Log.Warning(
                    "Sparrow Kline request failed for {Code}; Endpoint={Endpoint}; Error={Error}",
                    code, request.Endpoint, response.Error);
                return new SparrowKlineFetchOutcome(
                    code, null, SparrowKlineFetchFailureReason.RequestFailed, response.Error);
            }
            if (string.IsNullOrWhiteSpace(response.Json))
            {
                Log.Warning(
                    "Sparrow Kline response was empty for {Code}; Endpoint={Endpoint}",
                    code, request.Endpoint);
                return new SparrowKlineFetchOutcome(
                    code, null, SparrowKlineFetchFailureReason.EmptyResponse, "Empty response");
            }
            if (!isUsableResponse(response.Json))
            {
                Log.Warning(
                    "Sparrow Kline response was invalid for {Code}; Endpoint={Endpoint}",
                    code, request.Endpoint);
                return new SparrowKlineFetchOutcome(
                    code, null, SparrowKlineFetchFailureReason.InvalidResponse, "Invalid Kline response");
            }

            return new SparrowKlineFetchOutcome(code, response.Json, null, null);
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            Log.Warning(ex, "Sparrow Kline timeout for {Code}; Endpoint={Endpoint}", code, request.Endpoint);
            return new SparrowKlineFetchOutcome(
                code, null, SparrowKlineFetchFailureReason.Timeout, ex.Message);
        }
        catch (TimeoutException ex)
        {
            Log.Warning(ex, "Sparrow Kline timeout for {Code}; Endpoint={Endpoint}", code, request.Endpoint);
            return new SparrowKlineFetchOutcome(
                code, null, SparrowKlineFetchFailureReason.Timeout, ex.Message);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Sparrow Kline fetch failed for {Code}; Endpoint={Endpoint}", code, request.Endpoint);
            return new SparrowKlineFetchOutcome(
                code, null, SparrowKlineFetchFailureReason.RequestFailed, ex.Message);
        }
    }

	public static bool IsUsableKlineJson(string json) =>
		SparrowKlineSnapshotFactory.FromLegacyJson(json) is { ClosesNewestFirst.Count: > 0 };

	private static string SerializeForLegacySparrow(KlineSeries series) => JsonSerializer.Serialize(new
	{
		data = series.Bars.Select(bar => new
		{
			Date = bar.Date.ToString("yyyy-MM-dd"),
			Open = ToMilli(bar.Open),
			High = ToMilli(bar.High),
			Low = ToMilli(bar.Low),
			Close = ToMilli(bar.Close),
			Volume = bar.Volume,
			Amount = bar.Amount,
			Percent = bar.ChangePercent,
			Change = bar.Change,
			Turnover = bar.TurnoverRate
		})
	});

	private static long? ToMilli(double? value) => value.HasValue
		? (long)Math.Round(value.Value * 1000.0)
		: null;
}
