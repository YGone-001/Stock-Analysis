from __future__ import annotations

import asyncio
import os
from datetime import datetime, timedelta
from typing import Any

from app.providers.eastmoney import (
    as_float,
    normalize_code,
    optional_float,
    to_milli,
    to_nullable_milli,
)


class AkShareProvider:
    def __init__(self) -> None:
        self._disable_proxy_env()
        self._akshare = None
        self.available = False
        try:
            import akshare as akshare  # type: ignore
        except Exception:
            return
        self._akshare = akshare
        self.available = True

    async def stock_board_industry(self) -> dict[str, Any]:
        if not self.available:
            return {"data": [], "error": "akshare_not_installed"}
        return await asyncio.to_thread(self._stock_board_industry_sync)

    async def quote(self, codes: str) -> dict[str, Any] | None:
        if not self.available:
            return None
        normalized = unique_codes(codes)
        if not normalized:
            return {"data": []}
        try:
            return await asyncio.to_thread(self._quote_sync, normalized)
        except Exception:
            return None

    async def daily_kline(self, code: str, limit: int) -> dict[str, Any] | None:
        if not self.available:
            return None
        normalized = normalize_code(code)
        if len(normalized) != 6:
            return {"data": []}
        limit = max(1, min(limit, 5000))
        try:
            return await asyncio.to_thread(self._daily_kline_sync, normalized, limit)
        except Exception:
            return None

    async def stock_board_concept(self) -> dict[str, Any]:
        if not self.available:
            return {"data": [], "error": "akshare_not_installed"}
        return await asyncio.to_thread(self._stock_board_concept_sync)

    async def macro_china_money_supply(self) -> dict[str, Any]:
        if not self.available:
            return {"data": [], "error": "akshare_not_installed"}
        return await asyncio.to_thread(self._macro_china_money_supply_sync)

    def _stock_board_industry_sync(self) -> dict[str, Any]:
        frame = self._akshare.stock_board_industry_name_em()
        return {"data": frame.to_dict(orient="records"), "source": "akshare"}

    def _quote_sync(self, codes: list[str]) -> dict[str, Any]:
        frame = self._akshare.stock_zh_a_spot_em()
        if frame is None or frame.empty:
            return {"data": [], "source": "akshare"}

        code_set = set(codes)
        rows = []
        for item in frame.to_dict(orient="records"):
            code = str(item.get("代码") or "").strip()
            if code not in code_set:
                continue
            close = optional_float(item.get("最新价"))
            preclose = optional_float(item.get("昨收"))
            amount = optional_float(item.get("成交额"))
            rows.append(
                {
                    "QuoteSchemaVersion": 2,
                    "SourceEndpoint": "akshare/stock_zh_a_spot_em",
                    "Code": code,
                    "Name": str(item.get("名称") or ""),
                    "TotalHand": optional_float(item.get("成交量")),
                    "Amount": amount,
                    "TotalAmount": amount,
                    "Price": close,
                    "PreClose": preclose,
                    "Volume": optional_float(item.get("成交量")),
                    "OuterVolume": None,
                    "InnerVolume": None,
                    "Wp": None,
                    "Np": None,
                    "Turnover": optional_float(item.get("换手率")),
                    "Percent": optional_float(item.get("涨跌幅")),
                    "BuyLevel": [],
                    "SellLevel": [],
                    "K": {
                        "Close": to_nullable_milli(close),
                        "Last": to_nullable_milli(preclose),
                        "PreClose": to_nullable_milli(preclose),
                        "Open": to_nullable_milli(optional_float(item.get("今开"))),
                        "High": to_nullable_milli(optional_float(item.get("最高"))),
                        "Low": to_nullable_milli(optional_float(item.get("最低"))),
                    },
                }
            )
        return {"data": rows, "source": "akshare"}

    def _daily_kline_sync(self, code: str, limit: int) -> dict[str, Any]:
        end = datetime.now().strftime("%Y%m%d")
        start = (datetime.now() - timedelta(days=max(limit * 3, 365))).strftime("%Y%m%d")
        frame = self._akshare.stock_zh_a_hist(
            symbol=code,
            period="daily",
            start_date=start,
            end_date=end,
            adjust="qfq",
        )
        if frame is None or frame.empty:
            return {"data": [], "source": "akshare"}

        rows = []
        for item in frame.tail(limit).to_dict(orient="records"):
            rows.append(
                {
                    "Time": str(item.get("日期") or ""),
                    "Open": to_milli(as_float(item.get("开盘"))),
                    "Close": to_milli(as_float(item.get("收盘"))),
                    "High": to_milli(as_float(item.get("最高"))),
                    "Low": to_milli(as_float(item.get("最低"))),
                    "Volume": as_float(item.get("成交量")),
                    "Amount": as_float(item.get("成交额")),
                    "Turnover": as_float(item.get("换手率")),
                    "Percent": as_float(item.get("涨跌幅")),
                }
            )
        return {"data": rows, "source": "akshare"}

    def _stock_board_concept_sync(self) -> dict[str, Any]:
        frame = self._akshare.stock_board_concept_name_em()
        return {"data": frame.to_dict(orient="records"), "source": "akshare"}

    def _macro_china_money_supply_sync(self) -> dict[str, Any]:
        frame = self._akshare.macro_china_money_supply()
        return {"data": frame.to_dict(orient="records"), "source": "akshare"}

    @staticmethod
    def _disable_proxy_env() -> None:
        for name in (
            "HTTP_PROXY",
            "HTTPS_PROXY",
            "ALL_PROXY",
            "http_proxy",
            "https_proxy",
            "all_proxy",
        ):
            os.environ.pop(name, None)
        os.environ["NO_PROXY"] = "*"
        os.environ["no_proxy"] = "*"


def unique_codes(codes: str) -> list[str]:
    normalized = []
    seen = set()
    for raw in codes.split(","):
        code = normalize_code(raw)
        if len(code) == 6 and code not in seen:
            normalized.append(code)
            seen.add(code)
    return normalized
