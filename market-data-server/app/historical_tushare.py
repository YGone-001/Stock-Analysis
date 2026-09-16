"""Typed, Tushare-only boundary for immutable historical dataset acquisition.

This module is intentionally separate from ``TushareProvider``.  The latter is a
best-effort live gateway adapter; this module requires an explicit date range and
never falls back to another provider or to the local K-line cache.
"""
from __future__ import annotations

import asyncio
import json
from datetime import date
from enum import StrEnum
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

from pydantic import BaseModel, Field


TUSHARE_API_URL = "http://api.tushare.pro"


class HistoricalCapabilityStatus(StrEnum):
    AVAILABLE = "Available"
    UNAVAILABLE = "Unavailable"
    PERMISSION_DENIED = "PermissionDenied"
    UNKNOWN = "Unknown"


class HistoricalSourceError(RuntimeError):
    def __init__(self, status: HistoricalCapabilityStatus, message: str) -> None:
        super().__init__(message)
        self.status = status


class HistoricalCapability(BaseModel):
    capability: str
    status: HistoricalCapabilityStatus
    detail: str = ""


class HistoricalCapabilityProbeResponse(BaseModel):
    source: str = "tushare"
    capabilities: list[HistoricalCapability]


class HistoricalSecurityDto(BaseModel):
    symbol: str
    ts_code: str
    name: str
    market: str | None = None
    exchange: str | None = None
    list_status: str | None = None
    list_date: date | None = None
    delist_date: date | None = None
    source: str = "tushare"


class HistoricalSecurityResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalSecurityDto]


class HistoricalCalendarDto(BaseModel):
    trading_date: date
    is_open: bool
    previous_open_date: date | None = None
    source: str = "tushare"


class HistoricalCalendarResponse(BaseModel):
    source: str = "tushare"
    exchange: str
    start_date: date
    end_date: date
    data: list[HistoricalCalendarDto]


class HistoricalDailyPriceDto(BaseModel):
    symbol: str
    ts_code: str
    trading_date: date
    open: float | None = None
    high: float | None = None
    low: float | None = None
    close: float | None = None
    previous_close: float | None = None
    change: float | None = None
    percent: float | None = None
    volume: float | None = Field(default=None, description="Tushare unit: hands")
    amount: float | None = Field(default=None, description="Tushare unit: thousand currency units")
    source: str = "tushare"
    adjustment_mode: str = "Raw"


class HistoricalDailyPriceResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalDailyPriceDto]


class HistoricalTurnoverDto(BaseModel):
    symbol: str
    ts_code: str
    trading_date: date
    turnover_rate: float | None = Field(default=None, description="Percentage")
    source: str = "tushare"


class HistoricalTurnoverResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalTurnoverDto]


class HistoricalIndexDailyDto(BaseModel):
    index_code: str
    trading_date: date
    close: float | None = None
    previous_close: float | None = None
    percent: float | None = None
    source: str = "tushare"


class HistoricalIndexDailyResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalIndexDailyDto]


class HistoricalSuspensionDto(BaseModel):
    symbol: str
    ts_code: str
    trading_date: date
    action: str
    timing: str | None = None
    source: str = "tushare"


class HistoricalSuspensionResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalSuspensionDto]


def _date(value: Any) -> date | None:
    text = str(value or "").strip()
    if len(text) != 8 or not text.isdigit():
        return None
    return date(int(text[:4]), int(text[4:6]), int(text[6:]))


def _number(value: Any) -> float | None:
    if value is None or value == "":
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


class HistoricalTushareClient:
    """No-cache, date-range client.  It never exposes the configured token."""

    mandatory_apis = ("stock_basic", "daily", "trade_cal")
    optional_apis = ("daily_basic", "index_daily", "stock_st", "suspend_d", "adj_factor")

    def __init__(self, token: str, timeout_seconds: float = 10, retries: int = 2) -> None:
        self._token = token.strip()
        self._timeout_seconds = timeout_seconds
        self._retries = max(0, retries)

    @property
    def configured(self) -> bool:
        return bool(self._token)

    async def probe(self) -> HistoricalCapabilityProbeResponse:
        if not self.configured:
            return HistoricalCapabilityProbeResponse(capabilities=[
                HistoricalCapability(capability=name, status=HistoricalCapabilityStatus.UNAVAILABLE,
                                     detail="TUSHARE_TOKEN is not configured")
                for name in (*self.mandatory_apis, *self.optional_apis)
            ])
        probes = {
            "stock_basic": ("stock_basic", {"exchange": "", "list_status": "L"}, "ts_code"),
            "daily": ("daily", {"trade_date": "19910101"}, "ts_code"),
            "trade_cal": ("trade_cal", {"exchange": "SSE", "start_date": "19910101", "end_date": "19910101"}, "cal_date"),
            "daily_basic": ("daily_basic", {"trade_date": "19910101"}, "ts_code"),
            "index_daily": ("index_daily", {"ts_code": "000001.SH", "start_date": "19910101", "end_date": "19910101"}, "ts_code"),
            "stock_st": ("stock_st", {"trade_date": "19910101"}, "ts_code"),
            "suspend_d": ("suspend_d", {"trade_date": "19910101"}, "ts_code"),
            "adj_factor": ("adj_factor", {"trade_date": "19910101"}, "ts_code"),
        }
        output: list[HistoricalCapability] = []
        for name, (api, params, fields) in probes.items():
            try:
                await self._call(api, params, fields)
                output.append(HistoricalCapability(capability=name, status=HistoricalCapabilityStatus.AVAILABLE))
            except HistoricalSourceError as exc:
                output.append(HistoricalCapability(capability=name, status=exc.status, detail=str(exc)))
        return HistoricalCapabilityProbeResponse(capabilities=output)

    async def securities(self, start: date, end: date) -> list[HistoricalSecurityDto]:
        # Query all source statuses intentionally: a current-L-only request is survivorship biased.
        rows: list[HistoricalSecurityDto] = []
        for status in ("L", "D", "P", "G", "UN"):
            for item in await self._call("stock_basic", {"exchange": "", "list_status": status},
                                         "ts_code,symbol,name,market,exchange,list_status,list_date,delist_date"):
                list_date, delist_date = _date(item.get("list_date")), _date(item.get("delist_date"))
                if list_date and list_date > end:
                    continue
                # Tushare documentation does not prove whether delist_date is inclusive.  Preserve it as source data;
                # the C# adapter explicitly maps no +1-day semantic conversion.
                if delist_date and delist_date < start:
                    continue
                ts_code = str(item.get("ts_code") or "")
                rows.append(HistoricalSecurityDto(symbol=str(item.get("symbol") or ts_code.split(".")[0]), ts_code=ts_code,
                    name=str(item.get("name") or ts_code), market=_text(item.get("market")), exchange=_text(item.get("exchange")),
                    list_status=_text(item.get("list_status")), list_date=list_date, delist_date=delist_date))
        return sorted(_unique_by(rows, lambda item: item.ts_code), key=lambda item: item.ts_code)

    async def calendar(self, exchange: str, start: date, end: date) -> list[HistoricalCalendarDto]:
        rows = await self._call("trade_cal", {"exchange": exchange, "start_date": _wire_date(start), "end_date": _wire_date(end)},
                                "cal_date,is_open,pretrade_date")
        return self._strict_range([HistoricalCalendarDto(trading_date=_required_date(item, "cal_date"), is_open=str(item.get("is_open")) == "1",
            previous_open_date=_date(item.get("pretrade_date"))) for item in rows], start, end, lambda item: item.trading_date)

    async def daily(self, ts_code: str, start: date, end: date) -> list[HistoricalDailyPriceDto]:
        rows = await self._call("daily", {"ts_code": ts_code, "start_date": _wire_date(start), "end_date": _wire_date(end)},
            "ts_code,trade_date,open,high,low,close,pre_close,change,pct_chg,vol,amount")
        symbol = ts_code.split(".")[0]
        return self._strict_range([HistoricalDailyPriceDto(symbol=symbol, ts_code=str(item.get("ts_code") or ts_code), trading_date=_required_date(item, "trade_date"),
            open=_number(item.get("open")), high=_number(item.get("high")), low=_number(item.get("low")), close=_number(item.get("close")),
            previous_close=_number(item.get("pre_close")), change=_number(item.get("change")), percent=_number(item.get("pct_chg")),
            volume=_number(item.get("vol")), amount=_number(item.get("amount"))) for item in rows], start, end, lambda item: item.trading_date)

    async def turnover(self, ts_code: str, start: date, end: date) -> list[HistoricalTurnoverDto]:
        rows = await self._call("daily_basic", {"ts_code": ts_code, "start_date": _wire_date(start), "end_date": _wire_date(end)},
            "ts_code,trade_date,turnover_rate")
        symbol = ts_code.split(".")[0]
        return self._strict_range([HistoricalTurnoverDto(symbol=symbol, ts_code=str(item.get("ts_code") or ts_code),
            trading_date=_required_date(item, "trade_date"), turnover_rate=_number(item.get("turnover_rate"))) for item in rows], start, end, lambda item: item.trading_date)

    async def index_daily(self, index_code: str, start: date, end: date) -> list[HistoricalIndexDailyDto]:
        rows = await self._call("index_daily", {"ts_code": index_code, "start_date": _wire_date(start), "end_date": _wire_date(end)},
            "ts_code,trade_date,close,pre_close,pct_chg")
        return self._strict_range([HistoricalIndexDailyDto(index_code=str(item.get("ts_code") or index_code),
            trading_date=_required_date(item, "trade_date"), close=_number(item.get("close")), previous_close=_number(item.get("pre_close")),
            percent=_number(item.get("pct_chg"))) for item in rows], start, end, lambda item: item.trading_date)

    async def suspensions(self, start: date, end: date) -> list[HistoricalSuspensionDto]:
        rows = await self._call("suspend_d", {"start_date": _wire_date(start), "end_date": _wire_date(end)},
                                "ts_code,trade_date,suspend_type,suspend_timing")
        return self._strict_range([HistoricalSuspensionDto(symbol=str(item.get("ts_code") or "").split(".")[0], ts_code=str(item.get("ts_code") or ""),
            trading_date=_required_date(item, "trade_date"), action=str(item.get("suspend_type") or ""), timing=_text(item.get("suspend_timing"))) for item in rows], start, end, lambda item: item.trading_date)

    async def _call(self, api_name: str, params: dict[str, Any], fields: str) -> list[dict[str, Any]]:
        return await asyncio.to_thread(self._call_sync_retry, api_name, params, fields)

    def _call_sync_retry(self, api_name: str, params: dict[str, Any], fields: str) -> list[dict[str, Any]]:
        last: HistoricalSourceError | None = None
        for attempt in range(self._retries + 1):
            try:
                return self._call_sync(api_name, params, fields)
            except HistoricalSourceError as exc:
                last = exc
                if exc.status in {HistoricalCapabilityStatus.PERMISSION_DENIED, HistoricalCapabilityStatus.UNAVAILABLE} or attempt == self._retries:
                    raise
        raise last or HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Historical source call failed")

    def _call_sync(self, api_name: str, params: dict[str, Any], fields: str) -> list[dict[str, Any]]:
        if not self.configured:
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNAVAILABLE, "TUSHARE_TOKEN is not configured")
        body = json.dumps({"api_name": api_name, "token": self._token, "params": params, "fields": fields}).encode("utf-8")
        request = Request(TUSHARE_API_URL, data=body, headers={"Content-Type": "application/json"}, method="POST")
        try:
            with urlopen(request, timeout=self._timeout_seconds) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except HTTPError as exc:
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, f"Tushare HTTP status {exc.code}") from exc
        except (URLError, OSError, ValueError) as exc:
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare network or response error") from exc
        if payload.get("code") != 0:
            message = str(payload.get("msg") or "Tushare rejected request")
            lowered = message.lower()
            status = HistoricalCapabilityStatus.PERMISSION_DENIED if any(x in lowered for x in ("permission", "积分", "权限")) else HistoricalCapabilityStatus.UNKNOWN
            raise HistoricalSourceError(status, _safe_message(message))
        data = payload.get("data") or {}
        keys, items = data.get("fields") or [], data.get("items") or []
        if not isinstance(keys, list) or not isinstance(items, list):
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare response schema is invalid")
        return [dict(zip(keys, row)) for row in items if isinstance(row, list)]

    @staticmethod
    def _strict_range(items: list[Any], start: date, end: date, date_of: Any) -> list[Any]:
        selected = [item for item in items if start <= date_of(item) <= end]
        selected.sort(key=date_of)
        dates = [date_of(item) for item in selected]
        if len(dates) != len(set(dates)):
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare response has duplicate trading dates")
        return selected


def _required_date(item: dict[str, Any], field: str) -> date:
    result = _date(item.get(field))
    if result is None:
        raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, f"Tushare response has invalid {field}")
    return result


def _wire_date(value: date) -> str:
    return value.strftime("%Y%m%d")


def _text(value: Any) -> str | None:
    text = str(value or "").strip()
    return text or None


def _unique_by(items: list[Any], key: Any) -> list[Any]:
    output: dict[str, Any] = {}
    for item in items:
        output.setdefault(str(key(item)), item)
    return list(output.values())


def _safe_message(message: str) -> str:
    # Tokens must never reach an API response, exception output, or dataset provenance.
    return message.replace("token", "credential")[:300]
