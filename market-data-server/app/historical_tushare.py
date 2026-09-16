"""Typed, Tushare-only boundary for immutable historical dataset acquisition.

This module is intentionally separate from ``TushareProvider``.  The latter is a
best-effort live gateway adapter; this module requires an explicit date range and
never falls back to another provider or to the local K-line cache.
"""
from __future__ import annotations

import asyncio
import json
import random
import time
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
    RATE_LIMITED = "RateLimited"
    UNKNOWN = "Unknown"


class HistoricalCoverageStatus(StrEnum):
    FULL = "Full"
    PARTIAL = "Partial"
    UNAVAILABLE = "Unavailable"
    PERMISSION_DENIED = "PermissionDenied"
    RATE_LIMITED = "RateLimited"
    FAILED = "Failed"
    NOT_REQUESTED = "NotRequested"
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


class HistoricalCoverageScope(BaseModel):
    exchange: str | None = None
    symbol: str | None = None
    symbol_partition: str | None = None
    index_code: str | None = None
    list_status: str | None = None
    start_date: date
    end_date: date


class HistoricalCoverageChunk(BaseModel):
    start_date: date
    end_date: date
    returned_rows: int
    response_cap_hit: bool = False


class HistoricalCoverageEvidence(BaseModel):
    """Auditable acquisition fact. Operational timings are intentionally omitted."""
    endpoint: str
    source: str = "tushare"
    scope: HistoricalCoverageScope
    max_rows_per_request: int | None = None
    pagination_supported: bool = False
    permission_requirement: str | None = None
    query_shape: str
    query_count: int = 0
    requested_chunks: list[HistoricalCoverageChunk] = Field(default_factory=list)
    returned_rows: int = 0
    response_cap_hit: bool = False
    duplicate_rows: int = 0
    invalid_rows: int = 0
    retry_count: int = 0
    rate_limit_events: int = 0
    permission_denied_events: int = 0
    expected_count: int | None = None
    observed_count: int = 0
    missing_count: int | None = None
    coverage_status: HistoricalCoverageStatus = HistoricalCoverageStatus.UNKNOWN
    proof_method: str = "source response only"
    failure_reason: str | None = None


class HistoricalAcquisitionEnvelope(BaseModel):
    source: str = "tushare"
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


class HistoricalStDto(BaseModel):
    symbol: str
    ts_code: str
    trading_date: date
    type: str
    source: str = "tushare"


class HistoricalStResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalStDto]
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


class HistoricalAdjustmentFactorDto(BaseModel):
    symbol: str
    ts_code: str
    trading_date: date
    factor: float
    source: str = "tushare"


class HistoricalAdjustmentFactorResponse(BaseModel):
    source: str = "tushare"
    start_date: date
    end_date: date
    data: list[HistoricalAdjustmentFactorDto]
    evidence: list[HistoricalCoverageEvidence] = Field(default_factory=list)


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

    def __init__(self, token: str, timeout_seconds: float = 10, retries: int = 2,
                 minimum_request_interval_seconds: float = 0.05, chunk_days: int = 366) -> None:
        self._token = token.strip()
        self._timeout_seconds = timeout_seconds
        self._retries = max(0, retries)
        self._minimum_request_interval_seconds = max(0.0, minimum_request_interval_seconds)
        self._chunk_days = max(1, chunk_days)
        self._rate_lock = asyncio.Lock()
        self._last_request_started = 0.0

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
            trading_date=_required_date(item, "trade_date"), action=str(item.get("suspend_type") or ""), timing=_text(item.get("suspend_timing"))) for item in rows], start, end, lambda item: item.trading_date, lambda item: (item.trading_date, item.symbol, item.action))

    async def st_statuses(self, start: date, end: date) -> list[HistoricalStDto]:
        rows = await self._call("stock_st", {"start_date": _wire_date(start), "end_date": _wire_date(end)}, "ts_code,trade_date,type")
        return self._strict_range([HistoricalStDto(symbol=str(item.get("ts_code") or "").split(".")[0], ts_code=str(item.get("ts_code") or ""),
            trading_date=_required_date(item, "trade_date"), type=str(item.get("type") or "ST")) for item in rows], start, end, lambda item: item.trading_date, lambda item: (item.trading_date, item.symbol))

    async def adjustment_factors(self, ts_code: str, start: date, end: date) -> list[HistoricalAdjustmentFactorDto]:
        rows = await self._call("adj_factor", {"ts_code": ts_code, "start_date": _wire_date(start), "end_date": _wire_date(end)}, "ts_code,trade_date,adj_factor")
        symbol = ts_code.split(".")[0]
        output: list[HistoricalAdjustmentFactorDto] = []
        for item in rows:
            factor = _number(item.get("adj_factor"))
            if factor is None or factor <= 0:
                raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare adjustment factor must be positive and finite")
            output.append(HistoricalAdjustmentFactorDto(symbol=symbol, ts_code=str(item.get("ts_code") or ts_code), trading_date=_required_date(item, "trade_date"), factor=factor))
        return self._strict_range(output, start, end, lambda item: item.trading_date)

    # The raw methods above intentionally remain available for narrow compatibility callers.
    # The gateway uses the acquisition methods below so a successful HTTP response is never
    # mistaken for a proof that the requested source scope was exhaustively returned.
    async def acquire_securities(self, start: date, end: date) -> tuple[list[HistoricalSecurityDto], list[HistoricalCoverageEvidence]]:
        all_rows: list[HistoricalSecurityDto] = []
        evidence: list[HistoricalCoverageEvidence] = []
        for status in ("L", "D", "P", "G", "UN"):
            rows = await self._call("stock_basic", {"exchange": "", "list_status": status}, "ts_code,symbol,name,market,exchange,list_status,list_date,delist_date")
            dto = [HistoricalSecurityDto(symbol=str(item.get("symbol") or str(item.get("ts_code") or "").split(".")[0]), ts_code=str(item.get("ts_code") or ""),
                name=str(item.get("name") or ""), market=_text(item.get("market")), exchange=_text(item.get("exchange")), list_status=_text(item.get("list_status")),
                list_date=_date(item.get("list_date")), delist_date=_date(item.get("delist_date"))) for item in rows]
            dto = [item for item in dto if (not item.list_date or item.list_date <= end) and (not item.delist_date or item.delist_date >= start)]
            evidence.append(self._evidence("stock_basic", start, end, len(rows), 6000, "status partition", list_status=status,
                proof="status partition response below documented 6000-row cap" if len(rows) < 6000 else "response reached documented cap"))
            all_rows.extend(dto)
        conflicts = self._security_conflicts(all_rows)
        if conflicts:
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare stock_basic partitions contain conflicting semantic security records")
        unique = {item.ts_code: item for item in all_rows}
        return sorted(unique.values(), key=lambda item: item.ts_code), evidence

    async def acquire_calendar(self, exchange: str, start: date, end: date) -> tuple[list[HistoricalCalendarDto], list[HistoricalCoverageEvidence]]:
        output: list[HistoricalCalendarDto] = []
        evidence: list[HistoricalCoverageEvidence] = []
        for left, right in self._chunks(start, end):
            rows = await self._call("trade_cal", {"exchange": exchange, "start_date": _wire_date(left), "end_date": _wire_date(right)}, "cal_date,is_open,pretrade_date")
            dto = self._strict_range([HistoricalCalendarDto(trading_date=_required_date(item, "cal_date"), is_open=str(item.get("is_open")) == "1", previous_open_date=_date(item.get("pretrade_date"))) for item in rows], left, right, lambda item: item.trading_date)
            expected = (right - left).days + 1
            returned = {item.trading_date for item in dto}
            complete = len(returned) == expected and all(left.fromordinal(day) in returned for day in range(left.toordinal(), right.toordinal() + 1))
            evidence.append(self._evidence("trade_cal", left, right, len(dto), None, "date chunks", exchange=exchange, expected=expected, observed=len(returned), missing=expected - len(returned),
                status=HistoricalCoverageStatus.FULL if complete else HistoricalCoverageStatus.PARTIAL, proof="natural-calendar date-set equality"))
            output.extend(dto)
        output = self._strict_range(output, start, end, lambda item: item.trading_date)
        return output, evidence

    async def acquire_daily(self, ts_code: str, start: date, end: date) -> tuple[list[HistoricalDailyPriceDto], list[HistoricalCoverageEvidence]]:
        return await self._acquire_symbol_chunks("daily", ts_code, start, end, "ts_code,trade_date,open,high,low,close,pre_close,change,pct_chg,vol,amount", self._daily_dto, 6000)

    async def acquire_turnover(self, ts_code: str, start: date, end: date) -> tuple[list[HistoricalTurnoverDto], list[HistoricalCoverageEvidence]]:
        return await self._acquire_symbol_chunks("daily_basic", ts_code, start, end, "ts_code,trade_date,turnover_rate", self._turnover_dto, 6000)

    async def acquire_index_daily(self, index_code: str, start: date, end: date) -> tuple[list[HistoricalIndexDailyDto], list[HistoricalCoverageEvidence]]:
        output: list[HistoricalIndexDailyDto] = []
        evidence: list[HistoricalCoverageEvidence] = []
        for left, right in self._chunks(start, end):
            rows = await self._call("index_daily", {"ts_code": index_code, "start_date": _wire_date(left), "end_date": _wire_date(right)}, "ts_code,trade_date,close,pre_close,pct_chg")
            dto = self._strict_range([self._index_dto(item, index_code) for item in rows], left, right, lambda item: item.trading_date)
            valid_code = all(item.index_code == index_code for item in dto)
            evidence.append(self._evidence("index_daily", left, right, len(dto), None, "index/date chunks", index_code=index_code,
                status=HistoricalCoverageStatus.FULL if valid_code else HistoricalCoverageStatus.PARTIAL, proof="per-chunk source rows; consumer must validate against trading calendar",
                failure=None if valid_code else "index_code_mismatch"))
            output.extend(dto)
        return self._strict_range(output, start, end, lambda item: item.trading_date), evidence

    async def acquire_st_statuses(self, trading_dates: list[date]) -> tuple[list[HistoricalStDto], list[HistoricalCoverageEvidence]]:
        output: list[HistoricalStDto] = []
        evidence: list[HistoricalCoverageEvidence] = []
        for day in sorted(set(trading_dates)):
            rows = await self._call("stock_st", {"trade_date": _wire_date(day)}, "ts_code,trade_date,type")
            dto = self._strict_range([HistoricalStDto(symbol=str(item.get("ts_code") or "").split(".")[0], ts_code=str(item.get("ts_code") or ""), trading_date=_required_date(item, "trade_date"), type=str(item.get("type") or "ST")) for item in rows], day, day, lambda item: item.trading_date, lambda item: (item.trading_date, item.symbol))
            evidence.append(self._evidence("stock_st", day, day, len(dto), 1000, "one query per declared trading date", expected=None, observed=len(dto),
                status=HistoricalCoverageStatus.FULL if len(rows) < 1000 else HistoricalCoverageStatus.PARTIAL,
                proof="per-trading-date response below documented 1000-row cap" if len(rows) < 1000 else "response reached documented cap"))
            output.extend(dto)
        return output, evidence

    async def acquire_suspensions(self, start: date, end: date) -> tuple[list[HistoricalSuspensionDto], list[HistoricalCoverageEvidence]]:
        output: list[HistoricalSuspensionDto] = []
        evidence: list[HistoricalCoverageEvidence] = []
        for left, right in self._chunks(start, end):
            rows = await self._call("suspend_d", {"start_date": _wire_date(left), "end_date": _wire_date(right)}, "ts_code,trade_date,suspend_type,suspend_timing")
            dto = self._strict_range([HistoricalSuspensionDto(symbol=str(item.get("ts_code") or "").split(".")[0], ts_code=str(item.get("ts_code") or ""), trading_date=_required_date(item, "trade_date"), action=str(item.get("suspend_type") or ""), timing=_text(item.get("suspend_timing"))) for item in rows], left, right, lambda item: item.trading_date, lambda item: (item.trading_date, item.symbol, item.action))
            evidence.append(self._evidence("suspend_d", left, right, len(dto), None, "date chunks", proof="no documented cap/expected-set contract; raw S/R evidence only", status=HistoricalCoverageStatus.PARTIAL))
            output.extend(dto)
        return self._strict_range(output, start, end, lambda item: item.trading_date, lambda item: (item.trading_date, item.symbol, item.action)), evidence

    async def acquire_adjustment_factors(self, ts_code: str, start: date, end: date) -> tuple[list[HistoricalAdjustmentFactorDto], list[HistoricalCoverageEvidence]]:
        return await self._acquire_symbol_chunks("adj_factor", ts_code, start, end, "ts_code,trade_date,adj_factor", self._factor_dto, None, partial=True)

    async def _acquire_symbol_chunks(self, endpoint: str, ts_code: str, start: date, end: date, fields: str, mapper: Any, cap: int | None, partial: bool = False) -> tuple[list[Any], list[HistoricalCoverageEvidence]]:
        output: list[Any] = []
        evidence: list[HistoricalCoverageEvidence] = []
        async def collect(left: date, right: date) -> None:
            rows = await self._call(endpoint, {"ts_code": ts_code, "start_date": _wire_date(left), "end_date": _wire_date(right)}, fields)
            if cap is not None and len(rows) >= cap:
                evidence.append(self._evidence(endpoint, left, right, len(rows), cap, "symbol/date chunks", symbol=ts_code,
                    status=HistoricalCoverageStatus.PARTIAL, proof="source-cap response split deterministically", failure="response_cap_hit_split"))
                if left < right:
                    middle = left.fromordinal((left.toordinal() + right.toordinal()) // 2)
                    await collect(left, middle)
                    await collect(middle.fromordinal(middle.toordinal() + 1), right)
                    return
                evidence.append(self._evidence(endpoint, left, right, len(rows), cap, "symbol/date chunks", symbol=ts_code, status=HistoricalCoverageStatus.PARTIAL, proof="minimum date chunk reached source cap", failure="response_cap_hit"))
            dto = self._strict_range([mapper(item, ts_code) for item in rows], left, right, lambda item: item.trading_date)
            evidence.append(self._evidence(endpoint, left, right, len(dto), cap, "symbol/date chunks", symbol=ts_code,
                status=HistoricalCoverageStatus.PARTIAL if partial else HistoricalCoverageStatus.FULL,
                proof="bounded chunk response below documented cap" if cap is not None else "source has no documented cap/expected-set contract"))
            output.extend(dto)
        for left, right in self._chunks(start, end):
            await collect(left, right)
        return self._strict_range(output, start, end, lambda item: item.trading_date), evidence

    @staticmethod
    def _daily_dto(item: dict[str, Any], ts_code: str) -> HistoricalDailyPriceDto:
        return HistoricalDailyPriceDto(symbol=ts_code.split(".")[0], ts_code=str(item.get("ts_code") or ts_code), trading_date=_required_date(item, "trade_date"), open=_number(item.get("open")), high=_number(item.get("high")), low=_number(item.get("low")), close=_number(item.get("close")), previous_close=_number(item.get("pre_close")), change=_number(item.get("change")), percent=_number(item.get("pct_chg")), volume=_number(item.get("vol")), amount=_number(item.get("amount")))

    @staticmethod
    def _turnover_dto(item: dict[str, Any], ts_code: str) -> HistoricalTurnoverDto:
        return HistoricalTurnoverDto(symbol=ts_code.split(".")[0], ts_code=str(item.get("ts_code") or ts_code), trading_date=_required_date(item, "trade_date"), turnover_rate=_number(item.get("turnover_rate")))

    @staticmethod
    def _index_dto(item: dict[str, Any], index_code: str) -> HistoricalIndexDailyDto:
        return HistoricalIndexDailyDto(index_code=str(item.get("ts_code") or index_code), trading_date=_required_date(item, "trade_date"), close=_number(item.get("close")), previous_close=_number(item.get("pre_close")), percent=_number(item.get("pct_chg")))

    @staticmethod
    def _factor_dto(item: dict[str, Any], ts_code: str) -> HistoricalAdjustmentFactorDto:
        factor = _number(item.get("adj_factor"))
        if factor is None or factor <= 0:
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare adjustment factor must be positive and finite")
        return HistoricalAdjustmentFactorDto(symbol=ts_code.split(".")[0], ts_code=str(item.get("ts_code") or ts_code), trading_date=_required_date(item, "trade_date"), factor=factor)

    def _chunks(self, start: date, end: date) -> list[tuple[date, date]]:
        chunks: list[tuple[date, date]] = []
        left = start
        while left <= end:
            right = min(end, left.fromordinal(left.toordinal() + self._chunk_days - 1))
            chunks.append((left, right))
            left = right.fromordinal(right.toordinal() + 1)
        return chunks

    @staticmethod
    def _security_conflicts(rows: list[HistoricalSecurityDto]) -> bool:
        values: dict[str, tuple[object, ...]] = {}
        for item in rows:
            semantic = (item.symbol, item.name, item.market, item.exchange, item.list_date, item.delist_date)
            previous = values.setdefault(item.ts_code, semantic)
            if previous != semantic:
                return True
        return False

    @staticmethod
    def _evidence(endpoint: str, start: date, end: date, returned: int, cap: int | None, query_shape: str, *, exchange: str | None = None, symbol: str | None = None, index_code: str | None = None, list_status: str | None = None, expected: int | None = None, observed: int | None = None, missing: int | None = None, status: HistoricalCoverageStatus | None = None, proof: str, failure: str | None = None) -> HistoricalCoverageEvidence:
        cap_hit = cap is not None and returned >= cap
        return HistoricalCoverageEvidence(endpoint=endpoint, scope=HistoricalCoverageScope(exchange=exchange, symbol=symbol, index_code=index_code, list_status=list_status, start_date=start, end_date=end), max_rows_per_request=cap, query_shape=query_shape, query_count=1, requested_chunks=[HistoricalCoverageChunk(start_date=start, end_date=end, returned_rows=returned, response_cap_hit=cap_hit)], returned_rows=returned, response_cap_hit=cap_hit, expected_count=expected, observed_count=returned if observed is None else observed, missing_count=missing, coverage_status=status or (HistoricalCoverageStatus.PARTIAL if cap_hit else HistoricalCoverageStatus.FULL), proof_method=proof, failure_reason=failure)

    async def _call(self, api_name: str, params: dict[str, Any], fields: str) -> list[dict[str, Any]]:
        last: HistoricalSourceError | None = None
        for attempt in range(self._retries + 1):
            await self._wait_for_request_slot()
            try:
                return await asyncio.to_thread(self._call_sync, api_name, params, fields)
            except HistoricalSourceError as exc:
                last = exc
                if exc.status in {HistoricalCapabilityStatus.PERMISSION_DENIED, HistoricalCapabilityStatus.UNAVAILABLE} or attempt == self._retries:
                    raise
                # Bounded exponential backoff with small jitter. asyncio.sleep is cancellable.
                await asyncio.sleep(min(2.0, 0.15 * (2 ** attempt)) + random.uniform(0.0, 0.05))
        raise last or HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Historical source call failed")

    async def _wait_for_request_slot(self) -> None:
        if self._minimum_request_interval_seconds <= 0:
            return
        async with self._rate_lock:
            now = time.monotonic()
            delay = self._minimum_request_interval_seconds - (now - self._last_request_started)
            if delay > 0:
                await asyncio.sleep(delay)
            self._last_request_started = time.monotonic()

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
            if any(x in lowered for x in ("permission", "积分", "权限")):
                status = HistoricalCapabilityStatus.PERMISSION_DENIED
            elif any(x in lowered for x in ("frequency", "rate limit", "rate_limit", "调用频率", "访问频次", "限频", "频率")):
                status = HistoricalCapabilityStatus.RATE_LIMITED
            else:
                status = HistoricalCapabilityStatus.UNKNOWN
            raise HistoricalSourceError(status, _safe_message(message))
        data = payload.get("data") or {}
        keys, items = data.get("fields") or [], data.get("items") or []
        if not isinstance(keys, list) or not isinstance(items, list):
            raise HistoricalSourceError(HistoricalCapabilityStatus.UNKNOWN, "Tushare response schema is invalid")
        return [dict(zip(keys, row)) for row in items if isinstance(row, list)]

    @staticmethod
    def _strict_range(items: list[Any], start: date, end: date, date_of: Any, key_of: Any | None = None) -> list[Any]:
        selected = [item for item in items if start <= date_of(item) <= end]
        selected.sort(key=date_of)
        keys = [(key_of(item) if key_of else date_of(item)) for item in selected]
        if len(keys) != len(set(keys)):
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
