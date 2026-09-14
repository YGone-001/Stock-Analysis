import asyncio

import httpx

from app.main import app


def test_health_is_available_without_market_data_network_access() -> None:
    async def request_health() -> httpx.Response:
        async with app.router.lifespan_context(app):
            transport = httpx.ASGITransport(app=app)
            async with httpx.AsyncClient(
                transport=transport, base_url="http://testserver"
            ) as client:
                return await client.get("/health")

    response = asyncio.run(request_health())

    assert response.status_code == 200
    payload = response.json()
    assert payload["ok"] is True
    assert set(payload["providers"]) == {"eastmoney", "tushare", "akshare"}
    assert "storage" in payload
