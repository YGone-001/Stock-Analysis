from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path


@dataclass(frozen=True)
class Settings:
    host: str
    port: int
    http_timeout_seconds: float
    cache_dir: Path
    db_path: Path
    tushare_token: str
    historical_min_request_interval_seconds: float
    historical_chunk_days: int


def load_settings() -> Settings:
    cache_dir = Path(os.getenv("CACHE_DIR", ".cache"))
    db_path = Path(os.getenv("DB_PATH", str(cache_dir / "market_data.sqlite3")))
    return Settings(
        host=os.getenv("HOST", "127.0.0.1"),
        port=int(os.getenv("PORT", "8000")),
        http_timeout_seconds=float(os.getenv("HTTP_TIMEOUT_SECONDS", "10")),
        cache_dir=cache_dir,
        db_path=db_path,
        tushare_token=os.getenv("TUSHARE_TOKEN", "").strip(),
        historical_min_request_interval_seconds=float(os.getenv("HISTORICAL_MIN_REQUEST_INTERVAL_SECONDS", "0.05")),
        historical_chunk_days=int(os.getenv("HISTORICAL_CHUNK_DAYS", "366")),
    )
