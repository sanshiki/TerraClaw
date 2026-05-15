"""Episodic memory — timeline of significant events and action-outcome pairs."""

from __future__ import annotations

import json
import sqlite3
import time
from typing import Any


class EpisodicStore:
    """Stores and retrieves episodic memories (events, actions, LLM calls)."""

    def __init__(self, db_path: str):
        self._db = sqlite3.connect(db_path, check_same_thread=False)
        self._create_tables()

    def _create_tables(self) -> None:
        self._db.executescript("""
            CREATE TABLE IF NOT EXISTS episodes (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tick INTEGER,
                timestamp_ms INTEGER,
                episode_type TEXT,
                summary TEXT,
                data_json TEXT,
                importance REAL DEFAULT 0.5,
                embedding_id TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_episodes_tick ON episodes(tick);
            CREATE INDEX IF NOT EXISTS idx_episodes_type ON episodes(episode_type);
        """)
        self._db.commit()

    def record_event(self, tick: int, event_type: str, data: dict[str, Any],
                     importance: float = 0.5) -> int:
        """Record a game event."""
        summary = self._summarize_event(event_type, data)
        cursor = self._db.execute(
            """INSERT INTO episodes (tick, timestamp_ms, episode_type, summary, data_json, importance)
               VALUES (?, ?, ?, ?, ?, ?)""",
            (tick, int(time.time() * 1000), event_type, summary, json.dumps(data), importance),
        )
        self._db.commit()
        return cursor.lastrowid or 0

    def record_action(self, tick: int, action_type: str, params: dict[str, Any],
                      result: dict[str, Any], success: bool) -> int:
        """Record an action and its result."""
        summary = f"{'OK' if success else 'FAIL'} {action_type}: {self._summarize_params(params)}"
        cursor = self._db.execute(
            """INSERT INTO episodes (tick, timestamp_ms, episode_type, summary, data_json, importance)
               VALUES (?, ?, ?, ?, ?, ?)""",
            (tick, int(time.time() * 1000), f"action.{action_type}", summary,
             json.dumps({"params": params, "result": result, "success": success}),
             0.3 if success else 0.8),
        )
        self._db.commit()
        return cursor.lastrowid or 0

    def record_llm_call(self, tick: int, prompt_summary: str, response_summary: str,
                        tokens_in: int, tokens_out: int) -> int:
        """Record an LLM interaction."""
        cursor = self._db.execute(
            """INSERT INTO episodes (tick, timestamp_ms, episode_type, summary, data_json, importance)
               VALUES (?, ?, ?, ?, ?, ?)""",
            (tick, int(time.time() * 1000), "llm.call",
             f"LLM: {response_summary[:200]}",
             json.dumps({"prompt": prompt_summary[:500], "tokens_in": tokens_in, "tokens_out": tokens_out}),
             0.5),
        )
        self._db.commit()
        return cursor.lastrowid or 0

    def get_recent(self, limit: int = 20, min_importance: float = 0.0) -> list[dict[str, Any]]:
        """Get the most recent episodes."""
        rows = self._db.execute(
            """SELECT tick, episode_type, summary, importance, data_json
               FROM episodes
               WHERE importance >= ?
               ORDER BY id DESC
               LIMIT ?""",
            (min_importance, limit),
        ).fetchall()

        return [
            {"tick": r[0], "type": r[1], "summary": r[2], "importance": r[3], "data": json.loads(r[4]) if r[4] else {}}
            for r in reversed(rows)
        ]

    def get_actions_last_n_seconds(self, seconds: float, current_tick: int, ticks_per_second: int = 60) -> list[dict[str, Any]]:
        """Get actions from the last N seconds of game time."""
        min_tick = current_tick - int(seconds * ticks_per_second)
        rows = self._db.execute(
            """SELECT tick, episode_type, summary, data_json
               FROM episodes
               WHERE episode_type LIKE 'action.%'
                 AND tick >= ?
               ORDER BY tick""",
            (min_tick,),
        ).fetchall()

        return [
            {"tick": r[0], "type": r[1], "summary": r[2], "data": json.loads(r[3]) if r[3] else {}}
            for r in rows
        ]

    def get_count_since(self, tick: int) -> int:
        """Count episodes since a given tick."""
        row = self._db.execute("SELECT COUNT(*) FROM episodes WHERE tick >= ?", (tick,)).fetchone()
        return row[0] if row else 0

    @staticmethod
    def _summarize_event(event_type: str, data: dict[str, Any]) -> str:
        if event_type == "player.damage":
            return f"Took {data.get('amount', 0)} damage"
        elif event_type == "player.death":
            return "Player died"
        elif event_type == "entity.npc_death":
            return f"Killed {data.get('npc_type', 'enemy')}"
        elif event_type == "tile.broken":
            return f"Broke tile at ({data.get('position', {}).get('x', '?')}, {data.get('position', {}).get('y', '?')})"
        return f"{event_type}"

    @staticmethod
    def _summarize_params(params: dict[str, Any]) -> str:
        parts = []
        for k, v in params.items():
            if isinstance(v, (int, float, str)):
                parts.append(f"{k}={v}")
        return ", ".join(parts[:3])

    def close(self) -> None:
        self._db.close()
