# AIHelper Market Data Server

Private FastAPI gateway for AIHelper market-data endpoints. It keeps the WPF
client protocol stable while moving public-source parsing and cache logic out of
the desktop app.

## Priority

1. East Money realtime quote, daily K-line, search, code list, ETF list.
2. East Money intraday minute data and recent tick details.
3. Tushare Pro for trading calendar, historical K-line, and financial data.
4. AkShare for non-core research data such as industries, concepts, and macro.

East Money is the default realtime path. Tushare is wired as an optional source
for trading calendar and daily K-line data. AkShare extension endpoints are
available when the optional package is installed.

## Run

```powershell
cd market-data-server
python -m pip install -r requirements.txt
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

Then set AIHelper data source to:

```text
http://127.0.0.1:8000
```

## Optional Sources

Tushare:

```powershell
$env:TUSHARE_TOKEN="your-token"
python -m uvicorn app.main:app --host 127.0.0.1 --port 8000
```

When `TUSHARE_TOKEN` is set:

- `/api/workday` uses Tushare `trade_cal`.
- `/api/kline-all` uses Tushare in `source=auto` mode, then falls back to East
  Money if Tushare is unavailable.

AkShare:

```powershell
python -m pip install akshare
```

If AkShare is not installed, extension endpoints return an empty data array with
`error=akshare_not_installed`.

## Endpoints

```text
GET /health
GET /api/quote?code=000001
GET /api/kline-all?code=000001&type=day&limit=120
GET /api/kline-all?code=000001&type=day&limit=120&source=tushare
GET /api/kline-all?code=000001&type=day&limit=120&source=cache
GET /api/index?code=000001&type=day&limit=120
GET /api/minute?code=000001
GET /api/minute-trade-all?code=000001
GET /api/search?keyword=平安
GET /api/codes
GET /api/etf
GET /api/workday?date=20260622
GET /api/akshare/industry
GET /api/akshare/concept
GET /api/akshare/macro/money-supply
```

## Notes

- The service uses short in-memory TTL cache for realtime endpoints.
- Daily K-line rows and request logs are persisted in SQLite under `.cache/`.
- `StockNameMap.json` and `EtfNameMap.json` are persisted under `.cache/`.
- `/api/workday` falls back to a weekday heuristic when Tushare is not
  configured.
- Historical minute/tick requests keep the `date` parameter for compatibility,
  but the current East Money public adapter only guarantees latest available
  intraday data.
