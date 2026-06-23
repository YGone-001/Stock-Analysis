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
			StockDataResult primaryResult = await _primary.GetDataAsync(request, cancellationToken);
			if (primaryResult.Success)
			{
				return primaryResult;
			}
			StockDataLog.Write(request.Path, request.Get("code"), "-", null, false, "primary=" + primaryResult.Source + ", error=" + primaryResult.Error + "; falling back");
		}

		return _fallback.CanHandle(request)
			? await _fallback.GetDataAsync(request, cancellationToken)
			: StockDataResult.NotHandled(request.Endpoint);
	}
}
