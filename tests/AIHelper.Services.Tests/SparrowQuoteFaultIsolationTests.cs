using System.Collections.Concurrent;
using System.Text.Json;
using AIHelper.Models;
using AIHelper.Services.StockData;
using Xunit;

namespace AIHelper.Services.Tests;

public sealed class SparrowQuoteFaultIsolationTests
{
	[Fact]
	public async Task V2_QuoteBatchTimeout_DoesNotAbortRemainingBatches()
	{
		var provider = new QuoteBatchFaultProvider();
		var progress = new CaptureProgress();
		List<(string Code, string Name)> pool = Enumerable.Range(0, 60)
			.Select(index => ($"60{index:D4}", $"Candidate {index}"))
			.ToList();

		List<SparrowV2Candidate> results = await new SparrowScannerService(provider)
			.ScanWithFeaturesAsync(pool, Parameters(), progress, CancellationToken.None);

		Assert.Equal(10, results.Count);
		Assert.All(results, candidate => Assert.True(string.CompareOrdinal(candidate.Code, "600050") >= 0));
		Assert.Contains("批次失败: 1/2", progress.Log);
	}

	private static SparrowScanParameters Parameters() => new()
	{
		MacroDef = false,
		MinRise = 1,
		MaxRise = 5,
		VolRatio = 1.1,
		MinAmount = 1,
		CheckMA60 = true,
		MinAdhesion = 0,
		MaxAdhesion = 0.15,
		MinTurnover = 3,
		MaxTurnover = 30,
		MomentumThreshold = 0,
		CheckAlpha = false,
		MaxConcurrency = 2,
		UseCache = true
	};

	private static string QuoteJson(IEnumerable<string> codes) => JsonSerializer.Serialize(new
	{
		data = codes.Select(code => new
		{
			QuoteSchemaVersion = 2,
			Code = code,
			Name = code,
			Price = 10.3,
			PreClose = 10.0,
			Percent = 3.0,
			Amount = 100_000_000.0,
			Turnover = 10.0,
			OuterVolume = 1500.0,
			InnerVolume = 1000.0
		})
	});

	private static string KlineJson() => JsonSerializer.Serialize(new
	{
		data = Enumerable.Range(0, 120).Select(index => new
		{
			Time = $"2026-01-{index + 1:D3}",
			Close = (long)Math.Round((8.0 + index * 0.02) * 1000)
		})
	});

	private sealed class QuoteBatchFaultProvider : IStockDataProvider
	{
		private int _quoteBatch;

		public bool CanHandle(StockDataRequest request) => true;

		public Task<StockDataResult> GetDataAsync(
			StockDataRequest request,
			CancellationToken cancellationToken = default)
		{
			if (request.Path == "/api/quote")
			{
				if (Interlocked.Increment(ref _quoteBatch) == 1)
				{
					throw new TaskCanceledException("simulated quote request timeout");
				}
				return Task.FromResult(Success(request,
					QuoteJson(request.Get("code").Split(',', StringSplitOptions.RemoveEmptyEntries))));
			}

			if (request.Path == "/api/kline-all")
			{
				return Task.FromResult(Success(request, KlineJson()));
			}

			return Task.FromResult(new StockDataResult
			{
				Endpoint = request.Endpoint,
				Handled = true,
				Success = false,
				Error = "not configured"
			});
		}

		private static StockDataResult Success(StockDataRequest request, string json) => new()
		{
			Endpoint = request.Endpoint,
			Handled = true,
			Success = true,
			Json = json
		};
	}

	private sealed class CaptureProgress : IProgress<SparrowScanReport>
	{
		private readonly ConcurrentQueue<SparrowScanReport> _items = new();

		public string Log => string.Join('\n', _items
			.Select(item => item.LogMessage)
			.Where(message => !string.IsNullOrWhiteSpace(message)));

		public void Report(SparrowScanReport value) => _items.Enqueue(value);
	}
}
