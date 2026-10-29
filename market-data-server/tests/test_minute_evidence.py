import asyncio
from datetime import datetime

from app.cache import JsonFileCache, TtlCache
from app.providers.eastmoney import EastMoneyProvider


def test_minute_retains_unknown_units_original_receipt_and_cache_evidence(tmp_path):
    async def run():
        provider = EastMoneyProvider(None, TtlCache(), JsonFileCache(tmp_path / "names.json"), JsonFileCache(tmp_path / "etf.json"))
        calls = []
        async def fixture(url, params):
            calls.append(params)
            return {"data": {"trends": ["2026-10-29 14:30,1,12.34,1,1,8,1234", "2026-10-29 14:31,1,bad,1,1,bad"]}}
        provider._get_json = fixture
        first = await provider.minute("600001", "20261029")
        assert first["data"]["List"][0] == {"Time": "2026-10-29 14:30", "Price": 12340, "Number": 8.0, "Amount": 1234.0}
        assert first["data"]["List"][1]["Price"] is None
        assert first["data"]["List"][1]["Number"] is None
        assert first["data"]["List"][1]["Amount"] is None
        cached = await provider.minute("600001", "20261029")
        assert cached["observation"]["used_cache"] is True
        assert cached["observation"]["observed_at_utc"] == first["observation"]["observed_at_utc"]
        assert datetime.fromisoformat(first["observation"]["observed_at_utc"]).utcoffset().total_seconds() == 0
        assert cached["observation"]["volume_unit"] == "unknown"
        assert cached["observation"]["amount_unit"] == "unknown"
        historical = await provider.minute("600001", "20261028")
        assert historical["data"]["List"] == []
        assert all(p["ndays"] == "1" for p in calls)
    asyncio.run(run())


def test_explicit_benchmark_market_prefix_is_not_bare_stock_code():
    from app.providers.eastmoney import normalize_code, to_secid
    assert to_secid(normalize_code("sh000001"), raw_code="sh000001") == "1.000001"
    assert to_secid(normalize_code("sz000001"), raw_code="sz000001") == "0.000001"
