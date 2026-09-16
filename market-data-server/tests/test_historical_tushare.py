import asyncio
from datetime import date

import pytest

from app.historical_tushare import (
    HistoricalCoverageStatus,
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
        if api_name == "stock_st":
            return [{"ts_code": "600000.SH", "trade_date": "20240102", "type": "ST"}]
        if api_name == "suspend_d":
            return [{"ts_code": "600000.SH", "trade_date": "20240104", "suspend_type": "S", "suspend_timing": None}, {"ts_code": "600001.SH", "trade_date": "20240104", "suspend_type": "S", "suspend_timing": None}]
        if api_name == "adj_factor":
            return [{"ts_code": "600000.SH", "trade_date": "20240102", "adj_factor": "100"}]
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


def test_st_suspension_and_factor_are_typed_and_not_name_inference() -> None:
    client = FixtureClient()
    st = asyncio.run(client.st_statuses(date(2024, 1, 2), date(2024, 1, 4)))
    suspension = asyncio.run(client.suspensions(date(2024, 1, 2), date(2024, 1, 4)))
    factor = asyncio.run(client.adjustment_factors("600000.SH", date(2024, 1, 2), date(2024, 1, 4)))

    assert st[0].symbol == "600000"
    assert st[0].type == "ST"
    assert len(suspension) == 2
    assert factor[0].factor == 100


def test_st_is_acquired_once_per_declared_trading_day_and_cap_hit_is_not_full() -> None:
    class StCapClient(FixtureClient):
        def __init__(self) -> None:
            super().__init__()
            self.calls: list[date] = []

        async def _call(self, api_name: str, params: dict[str, object], fields: str) -> list[dict[str, object]]:
            if api_name != "stock_st":
                return await super()._call(api_name, params, fields)
            self.calls.append(date.fromisoformat(str(params["trade_date"])[0:4] + "-" + str(params["trade_date"])[4:6] + "-" + str(params["trade_date"])[6:8]))
            return [{"ts_code": f"{value:06d}.SH", "trade_date": params["trade_date"], "type": "ST"} for value in range(1000)]

    client = StCapClient()
    _, evidence = asyncio.run(client.acquire_st_statuses([date(2024, 1, 2), date(2024, 1, 3)]))

    assert client.calls == [date(2024, 1, 2), date(2024, 1, 3)]
    assert all(item.response_cap_hit for item in evidence)
    assert all(item.coverage_status is HistoricalCoverageStatus.PARTIAL for item in evidence)


def test_daily_chunk_boundaries_are_deterministic_and_do_not_overlap() -> None:
    client = FixtureClient()
    client._chunk_days = 2

    assert client._chunks(date(2024, 1, 1), date(2024, 1, 5)) == [
        (date(2024, 1, 1), date(2024, 1, 2)),
        (date(2024, 1, 3), date(2024, 1, 4)),
        (date(2024, 1, 5), date(2024, 1, 5)),
    ]


def test_daily_cap_hit_is_recorded_and_split_deterministically() -> None:
    class DailyCapClient(FixtureClient):
        def __init__(self) -> None:
            super().__init__()
            self._chunk_days = 5000
            self.calls: list[tuple[date, date]] = []

        async def _call(self, api_name: str, params: dict[str, object], fields: str) -> list[dict[str, object]]:
            if api_name != "daily":
                return await super()._call(api_name, params, fields)
            left = date.fromisoformat(str(params["start_date"])[0:4] + "-" + str(params["start_date"])[4:6] + "-" + str(params["start_date"])[6:8])
            right = date.fromisoformat(str(params["end_date"])[0:4] + "-" + str(params["end_date"])[4:6] + "-" + str(params["end_date"])[6:8])
            self.calls.append((left, right))
            if (right - left).days > 365:
                return [{"ts_code": "600000.SH", "trade_date": left.strftime("%Y%m%d")} for _ in range(6000)]
            return [{"ts_code": "600000.SH", "trade_date": left.strftime("%Y%m%d"), "close": "10"}]

    _, evidence = asyncio.run(DailyCapClient().acquire_daily("600000.SH", date(2020, 1, 1), date(2022, 1, 1)))
    assert any(item.response_cap_hit for item in evidence)
    assert any(item.failure_reason == "response_cap_hit_split" for item in evidence)
    assert any(item.coverage_status is HistoricalCoverageStatus.FULL for item in evidence)


def test_calendar_gap_or_broken_pretrade_chain_cannot_be_full() -> None:
    class CalendarClient(FixtureClient):
        async def _call(self, api_name: str, params: dict[str, object], fields: str) -> list[dict[str, object]]:
            if api_name != "trade_cal":
                return await super()._call(api_name, params, fields)
            return [
                {"cal_date": "20240101", "is_open": "1", "pretrade_date": None},
                {"cal_date": "20240103", "is_open": "1", "pretrade_date": "20240101"},
            ]

    _, evidence = asyncio.run(CalendarClient().acquire_calendar("SSE", date(2024, 1, 1), date(2024, 1, 3)))
    assert evidence[0].coverage_status is HistoricalCoverageStatus.PARTIAL
    assert evidence[0].missing_count == 1


def test_rate_limit_message_is_classified_without_reclassifying_unknown_errors(monkeypatch: pytest.MonkeyPatch) -> None:
    client = HistoricalTushareClient("token", minimum_request_interval_seconds=0)

    class Response:
        def __enter__(self) -> "Response": return self
        def __exit__(self, *_: object) -> None: pass
        def read(self) -> bytes: return b'{"code":-1,"msg":"frequency limit"}'

    monkeypatch.setattr("app.historical_tushare.urlopen", lambda *_args, **_kwargs: Response())
    with pytest.raises(HistoricalSourceError) as error:
        client._call_sync("daily", {}, "ts_code")
    assert error.value.status is HistoricalCapabilityStatus.RATE_LIMITED
