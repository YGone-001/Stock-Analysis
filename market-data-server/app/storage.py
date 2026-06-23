from __future__ import annotations

import json
import sqlite3
import time
from pathlib import Path
from typing import Any


class MarketDataStore:
    def __init__(self, db_path: Path) -> None:
        self.db_path = db_path
        self.db_path.parent.mkdir(parents=True, exist_ok=True)
        self._init_db()

    def _connect(self) -> sqlite3.Connection:
        conn = sqlite3.connect(self.db_path)
        conn.row_factory = sqlite3.Row
        return conn

    def _init_db(self) -> None:
        with self._connect() as conn:
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS kline_daily (
                    code TEXT NOT NULL,
                    trade_date TEXT NOT NULL,
                    source TEXT NOT NULL,
                    payload TEXT NOT NULL,
                    updated_at INTEGER NOT NULL,
                    PRIMARY KEY (code, trade_date)
                )
                """
            )
            conn.execute(
                """
                CREATE TABLE IF NOT EXISTS request_log (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    path TEXT NOT NULL,
                    query TEXT NOT NULL,
                    status_code INTEGER NOT NULL,
                    duration_ms REAL NOT NULL,
                    created_at INTEGER NOT NULL
                )
                """
            )

    def save_klines(self, code: str, rows: list[dict[str, Any]], source: str) -> None:
        if not rows:
            return
        now = int(time.time())
        with self._connect() as conn:
            conn.executemany(
                """
                INSERT INTO kline_daily (code, trade_date, source, payload, updated_at)
                VALUES (?, ?, ?, ?, ?)
                ON CONFLICT(code, trade_date)
                DO UPDATE SET source=excluded.source,
                              payload=excluded.payload,
                              updated_at=excluded.updated_at
                """,
                [
                    (
                        code,
                        str(row.get("Time") or ""),
                        source,
                        json.dumps(row, ensure_ascii=False),
                        now,
                    )
                    for row in rows
                    if row.get("Time")
                ],
            )

    def get_klines(self, code: str, limit: int) -> list[dict[str, Any]]:
        with self._connect() as conn:
            rows = conn.execute(
                """
                SELECT payload
                FROM kline_daily
                WHERE code = ?
                ORDER BY trade_date DESC
                LIMIT ?
                """,
                (code, limit),
            ).fetchall()
        parsed = [json.loads(row["payload"]) for row in reversed(rows)]
        return [row for row in parsed if isinstance(row, dict)]

    def log_request(
        self, path: str, query: str, status_code: int, duration_ms: float
    ) -> None:
        with self._connect() as conn:
            conn.execute(
                """
                INSERT INTO request_log (path, query, status_code, duration_ms, created_at)
                VALUES (?, ?, ?, ?, ?)
                """,
                (path, query, status_code, duration_ms, int(time.time())),
            )

    def stats(self) -> dict[str, Any]:
        with self._connect() as conn:
            kline_count = conn.execute("SELECT COUNT(*) FROM kline_daily").fetchone()[0]
            request_count = conn.execute("SELECT COUNT(*) FROM request_log").fetchone()[0]
        return {
            "db_path": str(self.db_path),
            "kline_rows": int(kline_count),
            "request_logs": int(request_count),
        }
