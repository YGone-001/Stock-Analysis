from __future__ import annotations

import asyncio
from typing import Any


class AkShareProvider:
    def __init__(self) -> None:
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

    def _stock_board_concept_sync(self) -> dict[str, Any]:
        frame = self._akshare.stock_board_concept_name_em()
        return {"data": frame.to_dict(orient="records"), "source": "akshare"}

    def _macro_china_money_supply_sync(self) -> dict[str, Any]:
        frame = self._akshare.macro_china_money_supply()
        return {"data": frame.to_dict(orient="records"), "source": "akshare"}
