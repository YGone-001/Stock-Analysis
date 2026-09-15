using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace AIHelper.Services.StockData;

/// <summary>
/// Preserves the existing ExternalGateway → EastMoney live-source preference and records its
/// outcome. Health is observability only; it never causes a provider to be skipped.
/// </summary>
public sealed class PreferredStockDataProvider : IStockDataProvider
{
	private readonly IStockDataProvider _primary;
	private readonly IStockDataProvider _fallback;
	private readonly IProviderHealthService? _health;

	public PreferredStockDataProvider(IStockDataProvider primary, IStockDataProvider fallback,
		IProviderHealthService? health = null)
	{
		_primary = primary;
		_fallback = fallback;
		_health = health;
	}

	public bool CanHandle(StockDataRequest request) => _primary.CanHandle(request) || _fallback.CanHandle(request);

	public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
	{
		StockDataResult? primaryResult = null;
		ProviderFailureKind primaryFailure = ProviderFailureKind.None;
		string primaryError = string.Empty;
		bool primaryAttempted = false;

		if (_primary.CanHandle(request))
		{
			primaryAttempted = true;
			long started = Stopwatch.GetTimestamp();
			try
			{
				primaryResult = await _primary.GetDataAsync(request, cancellationToken);
			}
			catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
			{
				throw;
			}
			catch (Exception ex)
			{
				primaryFailure = ProviderFailureClassifier.Classify(ex);
				primaryError = ex.Message;
				RecordFailure(DataSourceKind.ExternalGateway, request, primaryFailure, primaryError, started);
				Log.Warning(ex, "Market data provider failed. Operation={Operation} Provider={Provider} Failure={FailureKind} DurationMs={DurationMs}",
					request.Operation, DataSourceKind.ExternalGateway, primaryFailure, ProviderFailureClassifier.Elapsed(started).TotalMilliseconds);
			}

			if (primaryResult?.Success == true)
			{
				RecordSuccess(ResolveSource(primaryResult, DataSourceKind.ExternalGateway), request, started);
				return primaryResult;
			}

			if (primaryResult is not null)
			{
				primaryFailure = primaryResult.FailureKind == ProviderFailureKind.None
					? ProviderFailureClassifier.Classify(null, primaryResult.Error) : primaryResult.FailureKind;
				primaryError = primaryResult.Error;
				DataSourceKind source = ResolveSource(primaryResult, DataSourceKind.ExternalGateway);
				RecordFailure(source, request, primaryFailure, primaryError, started);
				Log.Warning("Market data provider failed. Operation={Operation} Provider={Provider} Failure={FailureKind} DurationMs={DurationMs}",
					request.Operation, source, primaryFailure, ProviderFailureClassifier.Elapsed(started).TotalMilliseconds);
			}
		}

		if (!_fallback.CanHandle(request)) return primaryResult ?? StockDataResult.NotHandled(request.Endpoint);

		long fallbackStarted = Stopwatch.GetTimestamp();
		try
		{
			StockDataResult fallbackResult = await _fallback.GetDataAsync(request, cancellationToken);
			DataSourceKind fallbackSource = ResolveSource(fallbackResult, DataSourceKind.EastMoney);
			if (fallbackResult.Success)
			{
				RecordSuccess(fallbackSource, request, fallbackStarted);
				FallbackReason reason = !primaryAttempted ? FallbackReason.None
					: primaryFailure == ProviderFailureKind.None
					? FallbackReason.PrimaryFailure : ProviderFailureClassifier.ToFallbackReason(primaryFailure);
				return Copy(fallbackResult, fallbackReason: reason);
			}

			ProviderFailureKind fallbackFailure = fallbackResult.FailureKind == ProviderFailureKind.None
				? ProviderFailureClassifier.Classify(null, fallbackResult.Error) : fallbackResult.FailureKind;
			RecordFailure(fallbackSource, request, fallbackFailure, fallbackResult.Error, fallbackStarted);
			return Copy(fallbackResult, error: BuildFailureMessage(primaryError, fallbackResult.Error),
				fallbackReason: FallbackReason.LiveSourcesUnavailable);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex)
		{
			ProviderFailureKind failure = ProviderFailureClassifier.Classify(ex);
			RecordFailure(DataSourceKind.EastMoney, request, failure, ex.Message, fallbackStarted);
			return new StockDataResult
			{
				Endpoint = request.Endpoint, Handled = true, Success = false,
				Source = DataSourceKind.EastMoney.ToLegacySource(), SourceKind = DataSourceKind.EastMoney,
				Error = BuildFailureMessage(primaryError, ex.Message), FailureKind = failure,
				FallbackReason = FallbackReason.LiveSourcesUnavailable
			};
		}
	}

	private void RecordSuccess(DataSourceKind provider, StockDataRequest request, long started) =>
		_health?.RecordSuccess(provider, request.Operation, ProviderFailureClassifier.Elapsed(started));

	private void RecordFailure(DataSourceKind provider, StockDataRequest request, ProviderFailureKind failure, string message, long started) =>
		_health?.RecordFailure(provider, request.Operation, failure, message, ProviderFailureClassifier.Elapsed(started));

	private static DataSourceKind ResolveSource(StockDataResult result, DataSourceKind fallback) =>
		result.SourceKind != DataSourceKind.Unknown ? result.SourceKind
			: result.Source.ToDataSourceKind() is DataSourceKind.Unknown ? fallback : result.Source.ToDataSourceKind();

	private static string BuildFailureMessage(string primary, string fallback)
	{
		if (string.IsNullOrWhiteSpace(primary)) return fallback;
		if (string.IsNullOrWhiteSpace(fallback)) return primary;
		return $"ExternalGateway: {primary}; EastMoney: {fallback}";
	}

	internal static StockDataResult Copy(StockDataResult source, string? error = null,
		FallbackReason? fallbackReason = null, CacheFreshness? cacheFreshness = null) => new()
	{
		Endpoint = source.Endpoint, Handled = source.Handled, Success = source.Success, Json = source.Json,
		Source = source.Source, SourceKind = source.SourceKind, UsedCache = source.UsedCache,
		IsStale = source.IsStale, IsBackgroundRefresh = source.IsBackgroundRefresh,
		Error = error ?? source.Error, FailureKind = source.FailureKind,
		FallbackReason = fallbackReason ?? source.FallbackReason,
		CacheFreshness = cacheFreshness ?? source.CacheFreshness
	};
}
