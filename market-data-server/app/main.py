from __future__ import annotations

import time
from datetime import datetime
from typing import Any

import httpx
from fastapi import FastAPI, HTTPException, Query, Request
from starlette.responses import Response

from app.cache import JsonFileCache, TtlCache
from app.config import Settings, load_settings
from app.providers.akshare_provider import AkShareProvider
from app.providers.eastmoney import EastMoneyProvider, normalize_code, previous_weekday
from app.providers.tushare_provider import TushareProvider
from app.storage import MarketDataStore


settings: Settings = load_settings()
memory_cache = TtlCache()
name_cache = JsonFileCache(settings.cache_dir / "StockNameMap.json")
etf_name_cache = JsonFileCache(settings.cache_dir / "EtfNameMap.json")
store = MarketDataStore(settings.db_path)

app = FastAPI(title="AIHelper Market Data Gateway", version="0.1.0")


@app.get("/")
async def root() -> dict[str, Any]:
    return {
        "ok": True,
        "name": "AIHelper Market Data Gateway",
        "port": 8888,
        "docs": "/docs",
        "health": "/health",
        "examples": {
            "quote": "/api/quote?code=000001",
            "kline": "/api/kline-all?code=000001&type=day&limit=120",
            "akshare_kline": "/api/kline-all?code=000001&type=day&limit=120&source=akshare",
        },
    }


@app.on_event("startup")
async def startup() -> None:
    timeout = httpx.Timeout(settings.http_timeout_seconds)
    client = httpx.AsyncClient(timeout=timeout, trust_env=False)
    app.state.http_client = client
    app.state.eastmoney = EastMoneyProvider(client, memory_cache, name_cache, etf_name_cache)
    app.state.tushare = TushareProvider(settings.tushare_token, settings.http_timeout_seconds)
    app.state.akshare = AkShareProvider()


@app.on_event("shutdown")
async def shutdown() -> None:
    await app.state.http_client.aclose()


@app.middleware("http")
async def request_logger(request: Request, call_next: Any) -> Response:
    started = time.perf_counter()
    status_code = 500
    try:
        response = await call_next(request)
        status_code = response.status_code
        return response
    finally:
        duration_ms = (time.perf_counter() - started) * 1000
        store.log_request(
            request.url.path,
            request.url.query,
            status_code,
            duration_ms,
        )


@app.get("/health")
async def health() -> dict[str, Any]:
    return {
        "ok": True,
        "providers": {
            "eastmoney": True,
            "tushare": app.state.tushare.configured,
            "akshare": app.state.akshare.available,
        },
        "cache_dir": str(settings.cache_dir),
        "storage": store.stats(),
    }


@app.get("/api/quote")
async def quote(code: str = Query("")) -> dict[str, Any]:
    akshare_result = await app.state.akshare.quote(code)
    if akshare_result is not None and akshare_result.get("data"):
        await supplement_quote_depth_from_eastmoney(akshare_result, code)
        if quote_has_depth(akshare_result):
            return akshare_result
    eastmoney_result = await app.state.eastmoney.quote(code)
    if eastmoney_result.get("data"):
        return eastmoney_result
    raise HTTPException(status_code=503, detail="quote_source_unavailable")


@app.get("/api/kline-all")
async def kline_all(
    code: str = Query(""),
    type: str = Query("day"),
    limit: int = Query(120, ge=1, le=5000),
    source: str = Query("auto"),
) -> dict[str, Any]:
    if type.lower() not in {"day", "d", "101"}:
        return {"data": []}
    return await get_daily_kline(code, limit, source)


@app.get("/api/index")
async def index_kline(
    code: str = Query(""),
    type: str = Query("day"),
    limit: int = Query(120, ge=1, le=5000),
    source: str = Query("auto"),
) -> dict[str, Any]:
    return await kline_all(code=code, type=type, limit=limit, source=source)


@app.get("/api/minute")
async def minute(code: str = Query(""), date: str | None = Query(None)) -> dict[str, Any]:
    return await app.state.eastmoney.minute(code, date=date)


@app.get("/api/minute-trade-all")
async def minute_trade_all(
    code: str = Query(""), date: str | None = Query(None)
) -> dict[str, Any]:
    return await app.state.eastmoney.ticks(code, date=date)


@app.get("/api/trend")
async def trend(code: str = Query("")) -> dict[str, Any]:
    return await app.state.eastmoney.trend(code)


@app.get("/api/search")
async def search(keyword: str = Query("")) -> dict[str, Any]:
    return await app.state.eastmoney.search(keyword)


@app.get("/api/codes")
async def codes() -> dict[str, Any]:
    return await app.state.eastmoney.code_table(is_etf=False)


@app.get("/api/etf")
async def etf(limit: int = Query(10000)) -> dict[str, Any]:
    _ = limit
    return await app.state.eastmoney.code_table(is_etf=True)


@app.get("/api/workday")
async def workday(date: str = Query("")) -> dict[str, Any]:
    tushare_result = await app.state.tushare.workday(date)
    if tushare_result is not None:
        return tushare_result
    try:
        target = datetime.strptime(date, "%Y%m%d")
    except ValueError:
        target = datetime.now()
    is_workday = target.weekday() < 5
    previous = previous_weekday(target)
    return {
        "data": {
            "is_workday": is_workday,
            "previous": [{"numeric": previous.strftime("%Y%m%d")}],
            "source": "weekday-heuristic",
        }
    }


@app.get("/api/akshare/industry")
async def akshare_industry() -> dict[str, Any]:
    return await app.state.akshare.stock_board_industry()


@app.get("/api/akshare/concept")
async def akshare_concept() -> dict[str, Any]:
    return await app.state.akshare.stock_board_concept()


@app.get("/api/akshare/macro/money-supply")
async def akshare_macro_money_supply() -> dict[str, Any]:
    return await app.state.akshare.macro_china_money_supply()


async def get_daily_kline(code: str, limit: int, source: str) -> dict[str, Any]:
    source = source.lower()
    normalized = normalize_code(code)
    if source in {"cache", "local"}:
        return {"data": store.get_klines(normalized, limit)}

    if source in {"auto", "akshare"}:
        result = await app.state.akshare.daily_kline(code, limit)
        if result is not None:
            rows = result.get("data") or []
            if rows:
                store.save_klines(normalized, rows, source="akshare")
                return result
            if source == "akshare":
                return {"data": []}

    if source in {"auto", "tushare"}:
        result = await app.state.tushare.daily_kline(code, limit)
        if result is not None:
            rows = result.get("data") or []
            store.save_klines(normalized, rows, source="tushare")
            return result
        if source == "tushare":
            return {"data": []}

    result = await app.state.eastmoney.kline(code, limit=limit)
    rows = result.get("data") or []
    if rows:
        store.save_klines(normalized, rows, source="eastmoney")
        return result

    cached = store.get_klines(normalized, limit)
    return {"data": cached}


async def supplement_quote_depth_from_eastmoney(
    akshare_result: dict[str, Any], code: str
) -> None:
    rows = akshare_result.get("data") or []
    if not rows:
        return
    if all(has_numeric_quote_field(row.get("Wp")) and has_numeric_quote_field(row.get("Np")) for row in rows):
        return
    try:
        eastmoney_result = await app.state.eastmoney.quote(code)
    except Exception:
        return
    eastmoney_rows = {
        str(row.get("Code") or ""): row for row in (eastmoney_result.get("data") or [])
    }
    for row in rows:
        supplement = eastmoney_rows.get(str(row.get("Code") or ""))
        if not supplement:
            continue
        for key in (
            "Price",
            "PreClose",
            "Volume",
            "OuterVolume",
            "InnerVolume",
            "Wp",
            "Np",
            "Turnover",
            "Percent",
        ):
            if not has_numeric_quote_field(row.get(key)) and has_numeric_quote_field(supplement.get(key)):
                row[key] = supplement.get(key)
    akshare_result["supplement"] = "eastmoney"


def as_float_like(value: Any) -> float:
    try:
        if value is None or value == "":
            return 0.0
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def has_numeric_quote_field(value: Any) -> bool:
    if value is None or value == "" or value == "-":
        return False
    try:
        float(value)
        return True
    except (TypeError, ValueError):
        return False


def quote_has_depth(result: dict[str, Any]) -> bool:
    rows = result.get("data") or []
    return bool(rows) and all(
        has_numeric_quote_field(row.get("Wp"))
        and has_numeric_quote_field(row.get("Np"))
        for row in rows
    )
