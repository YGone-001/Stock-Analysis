from __future__ import annotations

import asyncio
import json
from datetime import datetime, timedelta
from typing import Any
from urllib.parse import urlencode
from urllib.request import Request, urlopen

import httpx

from app.cache import JsonFileCache, TtlCache


EASTMONEY_REFERER = "https://quote.eastmoney.com/"
EASTMONEY_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"
)


class EastMoneyProvider:
    def __init__(
        self,
        client: httpx.AsyncClient,
        memory_cache: TtlCache,
        name_cache: JsonFileCache,
        etf_name_cache: JsonFileCache,
    ) -> None:
        self.client = client
        self.memory_cache = memory_cache
        self.name_cache = name_cache
        self.etf_name_cache = etf_name_cache

    async def quote(self, codes: str, refresh: bool = False) -> dict[str, Any]:
        normalized = unique_codes(codes)
        if not normalized:
            return {"data": []}

        # v2 uses endpoint-specific outer/inner mappings and nullable missing values.
        cache_key = "quote:v2:" + ",".join(normalized)
        if not refresh:
            cached = self.memory_cache.get(cache_key)
            if cached is not None:
                return cached

        if len(normalized) == 1:
            result = await self._single_quote(normalized[0])
            if result.get("data"):
                return self.memory_cache.set(cache_key, result, ttl_seconds=2)

        secids = ",".join(to_secid(code) for code in normalized)
        fields = ",".join(
            [
                "f12",
                "f14",
                "f2",
                "f3",
                "f5",
                "f6",
                "f15",
                "f16",
                "f17",
                "f18",
                "f8",
                "f34",
                "f35",
            ]
        )
        payload = await self._get_json(
            "https://push2.eastmoney.com/api/qt/ulist.np/get",
            params={"fltt": "2", "invt": "2", "fields": fields, "secids": secids},
        )
        rows = (payload.get("data") or {}).get("diff") or []
        data = []
        for item in rows:
            code = str(item.get("f12") or "")
            close = optional_float(item.get("f2"))
            preclose = optional_float(item.get("f18"))
            amount = optional_float(item.get("f6"))
            data.append(
                {
                    "QuoteSchemaVersion": 2,
                    "SourceEndpoint": "ulist.np/get",
                    "Code": code,
                    "Name": str(item.get("f14") or ""),
                    "TotalHand": optional_float(item.get("f5")),
                    "Amount": amount,
                    "TotalAmount": amount,
                    "Price": optional_float(item.get("f2")),
                    "PreClose": optional_float(item.get("f18")),
                    "Volume": optional_float(item.get("f5")),
                    "OuterVolume": optional_float(item.get("f34")),
                    "InnerVolume": optional_float(item.get("f35")),
                    "Wp": optional_float(item.get("f34")),
                    "Np": optional_float(item.get("f35")),
                    "Turnover": optional_float(item.get("f8")),
                    "Percent": optional_float(item.get("f3")),
                    "BuyLevel": [],
                    "SellLevel": [],
                    "K": {
                        "Close": to_nullable_milli(close),
                        "Last": to_nullable_milli(preclose),
                        "PreClose": to_nullable_milli(preclose),
                        "Open": to_nullable_milli(optional_float(item.get("f17"))),
                        "High": to_nullable_milli(optional_float(item.get("f15"))),
                        "Low": to_nullable_milli(optional_float(item.get("f16"))),
                    },
                }
            )
        result = {"data": data}
        if data:
            return self.memory_cache.set(cache_key, result, ttl_seconds=2)
        return result

    async def _single_quote(self, code: str) -> dict[str, Any]:
        payload = await self._get_json(
            "https://push2.eastmoney.com/api/qt/stock/get",
            params={
                "ut": "fa5fd1943c7b386f172d6893dbfba10b",
                "fltt": "2",
                "invt": "2",
                "secid": to_secid(code),
                "fields": (
                    "f57,f58,f43,f44,f45,f46,f47,f48,f49,f60,f161,"
                    "f168,f170"
                ),
            },
        )
        item = payload.get("data") or {}
        if not item:
            return {"data": []}
        close = optional_float(item.get("f43"))
        preclose = optional_float(item.get("f60"))
        return {
            "data": [
                {
                    "QuoteSchemaVersion": 2,
                    "SourceEndpoint": "stock/get",
                    "Code": str(item.get("f57") or code),
                    "Name": str(item.get("f58") or ""),
                    "TotalHand": optional_float(item.get("f47")),
                    "Amount": optional_float(item.get("f48")),
                    "TotalAmount": optional_float(item.get("f48")),
                    "Price": optional_float(item.get("f43")),
                    "PreClose": optional_float(item.get("f60")),
                    "Volume": optional_float(item.get("f47")),
                    "OuterVolume": optional_float(item.get("f49")),
                    "InnerVolume": optional_float(item.get("f161")),
                    "Wp": optional_float(item.get("f49")),
                    "Np": optional_float(item.get("f161")),
                    "Turnover": optional_float(item.get("f168")),
                    "Percent": optional_float(item.get("f170")),
                    "BuyLevel": [],
                    "SellLevel": [],
                    "K": {
                        "Close": to_nullable_milli(close),
                        "Last": to_nullable_milli(preclose),
                        "PreClose": to_nullable_milli(preclose),
                        "Open": to_nullable_milli(optional_float(item.get("f46"))),
                        "High": to_nullable_milli(optional_float(item.get("f44"))),
                        "Low": to_nullable_milli(optional_float(item.get("f45"))),
                    },
                }
            ]
        }

    async def kline(self, code: str, limit: int = 120, refresh: bool = False) -> dict[str, Any]:
        normalized = normalize_code(code)
        if len(normalized) != 6:
            return {"data": []}
        limit = max(1, min(limit, 5000))
        cache_key = f"kline:{normalized}:{limit}"
        if not refresh:
            cached = self.memory_cache.get(cache_key)
            if cached is not None:
                return cached

        payload = await self._get_json(
            "https://push2his.eastmoney.com/api/qt/stock/kline/get",
            params={
                "secid": to_secid(normalized, raw_code=code),
                "klt": "101",
                "fqt": "1",
                "beg": "0",
                "end": "20500101",
                "ut": "fa5fd1943c7b386f172d6893dbfba10b",
                "rtntype": "6",
                "fields1": "f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13",
                "fields2": "f51,f52,f53,f54,f55,f56,f57,f58,f59,f60,f61",
            },
        )
        klines = ((payload.get("data") or {}).get("klines") or [])[-limit:]
        data = []
        for row in klines:
            parts = str(row).split(",")
            if len(parts) < 6:
                continue
            data.append(
                {
                    "Time": parts[0],
                    "Open": to_milli(as_float(parts[1])),
                    "Close": to_milli(as_float(parts[2])),
                    "High": to_milli(as_float(parts[3])),
                    "Low": to_milli(as_float(parts[4])),
                    "Volume": as_float(parts[5]),
                }
            )
        result = {"data": data}
        if data:
            return self.memory_cache.set(cache_key, result, ttl_seconds=300)
        return result

    async def minute(self, code: str, date: str | None = None) -> dict[str, Any]:
        normalized = normalize_code(code)
        if len(normalized) != 6:
            return {"data": {"List": []}}
        cache_key = f"minute:{normalized}:{date or 'latest'}"
        cached = self.memory_cache.get(cache_key)
        if cached is not None:
            return cached

        payload = await self._get_json(
            "https://push2his.eastmoney.com/api/qt/stock/trends2/get",
            params={
                "secid": to_secid(normalized, raw_code=code),
                "ndays": "1",
                "iscr": "0",
                "fields1": "f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13",
                "fields2": "f51,f52,f53,f54,f55,f56,f57,f58",
            },
        )
        trends = (payload.get("data") or {}).get("trends") or []
        target_date = normalize_date(date)
        rows = []
        for row in trends:
            parts = str(row).split(",")
            if len(parts) < 6:
                continue
            stamp = parts[0]
            if target_date and not stamp.startswith(target_date):
                continue
            rows.append(
                {
                    "Time": stamp,
                    "Price": to_milli(as_float(parts[2])),
                    "Number": as_float(parts[5]),
                    "Amount": as_float(parts[6]) if len(parts) > 6 else 0,
                }
            )
        result = {"data": {"List": rows}}
        if rows:
            return self.memory_cache.set(cache_key, result, ttl_seconds=10)
        return result

    async def trend(self, code: str, refresh: bool = False) -> dict[str, Any]:
        normalized = normalize_code(code)
        if len(normalized) != 6:
            return {"data": {}}
        cache_key = f"trend:{normalized}:{code}"
        if not refresh:
            cached = self.memory_cache.get(cache_key)
            if cached is not None:
                return cached

        payload = await self._get_json(
            "https://push2his.eastmoney.com/api/qt/stock/trends2/get",
            params={
                "secid": to_secid(normalized, raw_code=code),
                "ndays": "1",
                "iscr": "0",
                "fields1": "f1,f2,f3,f4,f5,f6,f7,f8,f9,f10,f11,f12,f13",
                "fields2": "f51,f52,f53,f54,f55,f56,f57,f58",
            },
        )
        data = payload.get("data") or {}
        result = {"data": data}
        if data and data.get("trends"):
            return self.memory_cache.set(cache_key, result, ttl_seconds=10)
        return result

    async def ticks(self, code: str, date: str | None = None) -> dict[str, Any]:
        normalized = normalize_code(code)
        if len(normalized) != 6:
            return {"data": {"List": []}}
        cache_key = f"ticks:{normalized}:{date or 'latest'}"
        cached = self.memory_cache.get(cache_key)
        if cached is not None:
            return cached

        payload = await self._get_json(
            "https://push2.eastmoney.com/api/qt/stock/details/get",
            params={
                "secid": to_secid(normalized, raw_code=code),
                "pos": "-500",
                "fields1": "f1,f2,f3,f4",
                "fields2": "f51,f52,f53,f54,f55",
            },
        )
        details = (payload.get("data") or {}).get("details") or []
        rows = []
        for row in details:
            parts = str(row).split(",")
            if len(parts) < 5:
                continue
            rows.append(
                {
                    "Time": parts[0],
                    "Price": to_milli(as_float(parts[1])),
                    "Number": as_float(parts[2]),
                    "Status": parts[4],
                }
            )
        result = {"data": {"List": rows}}
        if rows:
            return self.memory_cache.set(cache_key, result, ttl_seconds=5)
        return result

    async def search(self, keyword: str) -> dict[str, Any]:
        keyword = (keyword or "").strip()
        if not keyword:
            return {"code": 0, "data": []}
        payload = await self._get_json(
            "https://searchapi.eastmoney.com/api/suggest/get",
            params={
                "type": "14",
                "count": "20",
                "token": "D43BF722C8E33BDC906FB84D85E326E8",
                "input": keyword,
            },
        )
        items = (payload.get("QuotationCodeTable") or {}).get("Data") or []
        name_map = self.name_cache.read_dict()
        data = []
        for item in items:
            code = normalize_code(str(item.get("Code") or ""))
            name = str(item.get("Name") or "")
            quote_id = str(item.get("QuoteID") or "")
            classify = str(item.get("Classify") or "")
            security_type_name = str(item.get("SecurityTypeName") or "")
            if (
                len(code) != 6
                or not name
                or not is_cn_quote(quote_id)
                or not is_supported_search_item(classify, security_type_name)
            ):
                continue
            name_map[code] = name
            data.append({"code": code, "name": name})
        if name_map:
            self.name_cache.write_dict(name_map)
        return {"code": 0, "data": data}

    async def code_table(self, is_etf: bool) -> dict[str, Any]:
        cache = self.etf_name_cache if is_etf else self.name_cache
        name_map = cache.read_dict()
        if not name_map:
            fs_list = (
                ["b:MK0021", "b:MK0022", "b:MK0023", "b:MK0024"]
                if is_etf
                else ["m:0+t:6", "m:0+t:80", "m:1+t:2", "m:1+t:23"]
            )
            for fs in fs_list:
                await self._append_list(name_map, fs)
            if name_map:
                cache.write_dict(name_map)

        rows = [
            {"code": code, "name": name}
            for code, name in sorted(name_map.items(), key=lambda item: item[0])
        ]
        if is_etf:
            return {"data": {"list": rows}}
        return {"data": {"codes": rows}}

    async def _append_list(self, name_map: dict[str, str], fs: str) -> None:
        for page in range(1, 101):
            payload = await self._get_json(
                "https://20.push2.eastmoney.com/api/qt/clist/get",
                params={
                    "po": "1",
                    "np": "1",
                    "fltt": "2",
                    "invt": "2",
                    "fid": "f12",
                    "fields": "f12,f14",
                    "pn": str(page),
                    "pz": "100",
                    "fs": fs,
                },
            )
            rows = (payload.get("data") or {}).get("diff") or []
            if not rows:
                break
            for item in rows:
                code = normalize_code(str(item.get("f12") or ""))
                name = str(item.get("f14") or "")
                if len(code) == 6 and name:
                    name_map[code] = name
            if len(rows) < 100:
                break

    async def _get_json(self, url: str, params: dict[str, str]) -> dict[str, Any]:
        return await asyncio.to_thread(self._get_json_sync, url, params)

    def _get_json_sync(self, url: str, params: dict[str, str]) -> dict[str, Any]:
        full_url = f"{url}?{urlencode(params)}"
        request = Request(
            full_url,
            headers={"Referer": EASTMONEY_REFERER, "User-Agent": EASTMONEY_UA},
        )
        try:
            timeout = getattr(self.client.timeout, "read", None) or 10
            with urlopen(request, timeout=timeout) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except (OSError, ValueError):
            return {}
        return payload if isinstance(payload, dict) else {}


def normalize_code(code: str) -> str:
    return (
        (code or "")
        .strip()
        .lower()
        .replace("sh", "")
        .replace("sz", "")
        .replace("bj", "")
    )


def unique_codes(codes: str) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for raw in (codes or "").split(","):
        code = normalize_code(raw)
        if len(code) == 6 and code not in seen:
            seen.add(code)
            result.append(code)
    return result


def to_secid(code: str, raw_code: str | None = None) -> str:
    raw = (raw_code or code or "").strip().lower()
    normalized = normalize_code(code)
    if raw.startswith("sh"):
        market = "1"
    elif raw.startswith("sz") or raw.startswith("bj"):
        market = "0"
    elif normalized.startswith(("6", "5")):
        market = "1"
    else:
        market = "0"
    return f"{market}.{normalized}"


def as_float(value: Any) -> float:
    if value is None or value == "-":
        return 0.0
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def optional_float(value: Any) -> float | None:
    """Preserve unavailable quote fields as None; numeric zero remains a real value."""
    if value is None or value == "" or value == "-":
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def to_milli(value: float) -> int:
    if value <= 0:
        return 0
    return round(value * 1000)


def to_nullable_milli(value: float | None) -> int | None:
    if value is None:
        return None
    if value <= 0:
        return 0
    return round(value * 1000)


def is_cn_quote(quote_id: str) -> bool:
    return quote_id.startswith("0.") or quote_id.startswith("1.")


def is_supported_search_item(classify: str, security_type_name: str) -> bool:
    if classify.lower() in {"astock", "fund"}:
        return True
    return security_type_name in {"沪A", "深A", "基金"}


def normalize_date(date: str | None) -> str | None:
    if not date:
        return None
    clean = date.strip()
    if len(clean) == 8 and clean.isdigit():
        return f"{clean[:4]}-{clean[4:6]}-{clean[6:]}"
    return clean


def previous_weekday(date: datetime) -> datetime:
    current = date - timedelta(days=1)
    while current.weekday() >= 5:
        current -= timedelta(days=1)
    return current
