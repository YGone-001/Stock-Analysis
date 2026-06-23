from __future__ import annotations

import asyncio
import json
from datetime import datetime, timedelta
from typing import Any
from urllib.request import Request, urlopen

from app.providers.eastmoney import as_float, normalize_code, to_milli


TUSHARE_API_URL = "http://api.tushare.pro"


class TushareProvider:
    def __init__(self, token: str, timeout_seconds: float = 10) -> None:
        self.token = token.strip()
        self.timeout_seconds = timeout_seconds

    @property
    def configured(self) -> bool:
        return bool(self.token)

    async def workday(self, date: str) -> dict[str, Any] | None:
        if not self.configured:
            return None
        target = parse_yyyymmdd(date)
        if target is None:
            return None
        start = (target - timedelta(days=45)).strftime("%Y%m%d")
        end = target.strftime("%Y%m%d")
        items = await self._call(
            "trade_cal",
            {"exchange": "SSE", "start_date": start, "end_date": end},
            "cal_date,is_open,pretrade_date",
        )
        if not items:
            return None

        by_date = {str(row.get("cal_date")): row for row in items}
        target_row = by_date.get(target.strftime("%Y%m%d"))
        is_workday = bool(target_row and int(as_float(target_row.get("is_open"))) == 1)
        previous = None
        if target_row and target_row.get("pretrade_date"):
            previous = str(target_row["pretrade_date"])
        if previous is None:
            open_days = [
                str(row.get("cal_date"))
                for row in items
                if int(as_float(row.get("is_open"))) == 1
                and str(row.get("cal_date")) < target.strftime("%Y%m%d")
            ]
            previous = open_days[-1] if open_days else None
        return {
            "data": {
                "is_workday": is_workday,
                "previous": [{"numeric": previous}] if previous else [],
                "source": "tushare",
            }
        }

    async def daily_kline(self, code: str, limit: int) -> dict[str, Any] | None:
        if not self.configured:
            return None
        normalized = normalize_code(code)
        if len(normalized) != 6:
            return {"data": []}
        end = datetime.now().strftime("%Y%m%d")
        start = (datetime.now() - timedelta(days=max(limit * 3, 365))).strftime("%Y%m%d")
        items = await self._call(
            "daily",
            {"ts_code": to_ts_code(normalized), "start_date": start, "end_date": end},
            "trade_date,open,high,low,close,vol",
        )
        if not items:
            return None
        rows = []
        for item in sorted(items, key=lambda row: str(row.get("trade_date")))[-limit:]:
            date = str(item.get("trade_date") or "")
            rows.append(
                {
                    "Time": f"{date[:4]}-{date[4:6]}-{date[6:8]}",
                    "Open": to_milli(as_float(item.get("open"))),
                    "Close": to_milli(as_float(item.get("close"))),
                    "High": to_milli(as_float(item.get("high"))),
                    "Low": to_milli(as_float(item.get("low"))),
                    "Volume": as_float(item.get("vol")),
                }
            )
        return {"data": rows}

    async def _call(
        self, api_name: str, params: dict[str, Any], fields: str
    ) -> list[dict[str, Any]]:
        return await asyncio.to_thread(self._call_sync, api_name, params, fields)

    def _call_sync(
        self, api_name: str, params: dict[str, Any], fields: str
    ) -> list[dict[str, Any]]:
        body = json.dumps(
            {
                "api_name": api_name,
                "token": self.token,
                "params": params,
                "fields": fields,
            }
        ).encode("utf-8")
        request = Request(
            TUSHARE_API_URL,
            data=body,
            headers={"Content-Type": "application/json"},
            method="POST",
        )
        try:
            with urlopen(request, timeout=self.timeout_seconds) as response:
                payload = json.loads(response.read().decode("utf-8"))
        except (OSError, ValueError):
            return []
        if payload.get("code") != 0:
            return []
        data = payload.get("data") or {}
        fields_list = data.get("fields") or []
        rows = data.get("items") or []
        return [dict(zip(fields_list, row)) for row in rows]


def to_ts_code(code: str) -> str:
    if code.startswith(("6", "5")):
        return f"{code}.SH"
    if code.startswith(("4", "8", "9")):
        return f"{code}.BJ"
    return f"{code}.SZ"


def parse_yyyymmdd(value: str) -> datetime | None:
    try:
        return datetime.strptime(value, "%Y%m%d")
    except ValueError:
        return None
