import asyncio
from datetime import date

import pytest

from app.historical_tushare import (
    HistoricalCapabilityStatus,
    HistoricalSourceError,
    HistoricalTushareClient,
)


class FixtureClient(HistoricalTushareClient):
    def __init__(self) -> None:
        super().__init__("test-token")

    async def _call(self, api_name: str, params: dict[str, object], fields: str) -> list[dict[str, object]]:
        assert "token" not in params
        if api_name == "daily":
            return [
                {"ts_code": "600000.SH", "trade_date": "20240104", "open": "10", "high": "11", "low": "9", "close": "10.5", "pre_close": "10", "change": ".5", "pct_chg": "5", "vol": "100", "amount": "250"},
                {"ts_code": "600000.SH", "trade_date": "20240102", "open": "9", "high": "10", "low": "8", "close": "9.8", "pre_close": "9", "change": ".8", "pct_chg": "8.89", "vol": "90", "amount": "200"},
                {"ts_code": "600000.SH", "trade_date": "20240109", "open": "11", "high": "12", "low": "10", "close": "11", "pre_close": "10.5", "change": ".5", "pct_chg": "4.76", "vol": "120", "amount": "300"},
            ]
        if api_name == "stock_basic":
            status = str(params["list_status"])
            return [{"ts_code": "600001.SH", "symbol": "600001", "name": "Delisted", "market": "主板", "exchange": "SSE", "list_status": status, "list_date": "20200101", "delist_date": "20240105"}]
        return []


def test_daily_is_raw_date_range_filtered_ascending_and_preserves_units() -> None:
    rows = asyncio.run(FixtureClient().daily("600000.SH", date(2024, 1, 2), date(2024, 1, 4)))

    assert [row.trading_date for row in rows] == [date(2024, 1, 2), date(2024, 1, 4)]
    assert rows[0].adjustment_mode == "Raw"
    assert rows[0].volume == 90
    assert rows[0].amount == 200
    assert rows[1].previous_close == 10
    assert rows[1].percent == 5


def test_security_query_includes_delisted_statuses_and_filters_by_lifecycle_overlap() -> None:
    rows = asyncio.run(FixtureClient().securities(date(2024, 1, 2), date(2024, 1, 4)))

    assert len(rows) == 1
    assert rows[0].ts_code == "600001.SH"
    assert rows[0].delist_date == date(2024, 1, 5)


def test_duplicate_dates_are_rejected_as_invalid_source_schema() -> None:
    with pytest.raises(HistoricalSourceError) as error:
        HistoricalTushareClient._strict_range([date(2024, 1, 2), date(2024, 1, 2)], date(2024, 1, 2), date(2024, 1, 4), lambda value: value)
    assert error.value.status is HistoricalCapabilityStatus.UNKNOWN


def test_missing_token_probe_is_explicitly_unavailable() -> None:
    result = asyncio.run(HistoricalTushareClient("").probe())

    assert {entry.status for entry in result.capabilities} == {HistoricalCapabilityStatus.UNAVAILABLE}
