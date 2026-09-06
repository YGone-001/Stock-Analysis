# AIHelper Market Data Server

Private FastAPI gateway for AIHelper market-data endpoints. It keeps the WPF
client protocol stable while moving public-source parsing and cache logic out of
the desktop app.

## Priority

1. AkShare for A-share realtime quote and daily K-line core screening data.
2. East Money as realtime quote supplement for fields AkShare does not expose,
   such as inner/outer volume, and as the fallback public source.
3. East Money intraday minute data, recent tick details, search, code list, and
   ETF list.
4. Tushare Pro for trading calendar and optional historical K-line fallback.
5. AkShare extension data such as industries, concepts, and macro.

AkShare is the default core screening path when installed. East Money remains
the compatibility and supplement path so the WPF client keeps receiving stable
fields like `Wp`, `Np`, `Amount`, and `K.Close`.

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

AkShare is installed by `requirements.txt`. If it is unavailable at runtime,
core endpoints fall back to East Money and extension endpoints return an empty
data array with `error=akshare_not_installed`.

## Endpoints

```text
GET /health
GET /api/quote?code=000001
GET /api/kline-all?code=000001&type=day&limit=120
GET /api/kline-all?code=000001&type=day&limit=120&source=akshare
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

### `/api/quote` contract (schema v2)

| Property | East Money batch (`ulist.np/get`) | East Money single (`stock/get`) | Unit | Missing |
| --- | --- | --- | --- | --- |
| `Code` / `Name` | `f12` / `f14` | `f57` / `f58` | text | empty string |
| `Price` / `PreClose` | `f2` / `f18` | `f43` / `f60` | RMB/share | `null` |
| `Percent` | `f3` | `f170` | percentage points | `null` |
| `Amount` | `f6` | `f48` | RMB | `null` |
| `Volume` | `f5` | `f47` | hands (100 shares) | `null` |
| `Turnover` | `f8` | `f168` | percentage points | `null` |
| `OuterVolume` / `Wp` | `f34` | `f49` | hands (100 shares) | `null` |
| `InnerVolume` / `Np` | `f35` | `f161` | hands (100 shares) | `null` |

Field identifiers are endpoint-specific. In particular, `stock/get` order-book
fields must not be used as fallbacks for outer/inner volume. Numeric zero is a
real upstream value and is distinct from `null` (unavailable).

## Notes

- The service uses short in-memory TTL cache for realtime endpoints.
- Daily K-line rows and request logs are persisted in SQLite under `.cache/`.
- `StockNameMap.json` and `EtfNameMap.json` are persisted under `.cache/`.
- `/api/workday` falls back to a weekday heuristic when Tushare is not
  configured.
- Historical minute/tick requests keep the `date` parameter for compatibility,
  but the current East Money public adapter only guarantees latest available
  intraday data.
