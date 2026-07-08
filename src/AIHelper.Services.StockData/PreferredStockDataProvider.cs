using System.Threading;
using System.Threading.Tasks;

namespace AIHelper.Services.StockData;

public sealed class PreferredStockDataProvider : IStockDataProvider
{
	private readonly IStockDataProvider _primary;

	private readonly IStockDataProvider _fallback;

	public PreferredStockDataProvider(IStockDataProvider primary, IStockDataProvider fallback)
	{
		_primary = primary;
		_fallback = fallback;
	}

	public bool CanHandle(StockDataRequest request)
	{
		return _primary.CanHandle(request) || _fallback.CanHandle(request);
	}

	public async Task<StockDataResult> GetDataAsync(StockDataRequest request, CancellationToken cancellationToken = default)
	{
		if (_primary.CanHandle(request))
		{
			StockDataResult? primaryResult = null;
			try
			{
				primaryResult = await _primary.GetDataAsync(request, cancellationToken);
			}
			catch (System.Exception ex)
			{
				System.Diagnostics.Trace.WriteLine($"PreferredStockDataProvider: Primary provider threw exception: {ex}");
			}

			if (primaryResult != null && primaryResult.Success)
			{
				return primaryResult;
			}
			string err = primaryResult?.Error ?? "Exception occurred";
			string source = primaryResult?.Source ?? "Primary";
			StockDataLog.Write(request.Path, request.Get("code"), "-", null, false, "primary=" + source + ", error=" + err + "; falling back");
		}

		return _fallback.CanHandle(request)
			? await _fallback.GetDataAsync(request, cancellationToken)
			: StockDataResult.NotHandled(request.Endpoint);
	}
}
