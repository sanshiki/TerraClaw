"""Replay recorder — records all observations, actions, events, and LLM calls for later replay."""

from __future__ import annotations

import json
import sqlite3
import time


class ReplayRecorder:
    """Records a complete session for deterministic replay and debugging."""

    def __init__(self, db_path: str):
        self._db = sqlite3.connect(db_path, check_same_thread=False)
        self._create_tables()

    def _create_tables(self) -> None:
        self._db.executescript("""
            CREATE TABLE IF NOT EXISTS frames (
                sequence INTEGER PRIMARY KEY,
                tick INTEGER,
                timestamp_ms INTEGER,
                observation_json TEXT
            );

            CREATE TABLE IF NOT EXISTS actions (
                id TEXT PRIMARY KEY,
                tick_sent INTEGER,
                tick_completed INTEGER,
                action_json TEXT,
                result_json TEXT
            );

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                tick INTEGER,
                timestamp_ms INTEGER,
                event_type TEXT,
                event_json TEXT
            );

            CREATE TABLE IF NOT EXISTS llm_calls (
                id TEXT PRIMARY KEY,
                tick INTEGER,
                timestamp_ms INTEGER,
                prompt_json TEXT,
                response_json TEXT,
                latency_ms INTEGER,
                tokens_in INTEGER,
                tokens_out INTEGER,
                model TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_frames_tick ON frames(tick);
            CREATE INDEX IF NOT EXISTS idx_actions_tick ON actions(tick_sent);
            CREATE INDEX IF NOT EXISTS idx_events_tick ON events(tick);
        """)
        self._db.commit()

    def record_observation(self, obs_json: str) -> None:
        obs = json.loads(obs_json)
        self._db.execute(
            "INSERT INTO frames (sequence, tick, timestamp_ms, observation_json) VALUES (?, ?, ?, ?)",
            (obs.get("sequence", 0), obs.get("tick", 0),
             obs.get("timestamp_ms", int(time.time() * 1000)), obs_json),
        )
        self._db.commit()

    def record_action(self, action_id: str, tick_sent: int, tick_completed: int,
                      action_json: str, result_json: str = "") -> None:
        if tick_completed > 0:
            self._db.execute(
                """INSERT OR REPLACE INTO actions
                   (id, tick_sent, tick_completed, action_json, result_json)
                   VALUES (?, ?, ?, ?, ?)""",
                (action_id, tick_sent, tick_completed, action_json, result_json),
            )
        else:
            self._db.execute(
                "INSERT INTO actions (id, tick_sent, tick_completed, action_json, result_json) VALUES (?, ?, ?, ?, ?)",
                (action_id, tick_sent, 0, action_json, result_json),
            )
        self._db.commit()

    def record_event(self, event_type: str, tick: int, event_json: str) -> None:
        self._db.execute(
            "INSERT INTO events (tick, timestamp_ms, event_type, event_json) VALUES (?, ?, ?, ?)",
            (tick, int(time.time() * 1000), event_type, event_json),
        )
        self._db.commit()

    def record_llm_call(self, call_id: str, tick: int, prompt_json: str,
                        response_json: str, latency_ms: int,
                        tokens_in: int, tokens_out: int, model: str) -> None:
        self._db.execute(
            """INSERT INTO llm_calls (id, tick, timestamp_ms, prompt_json, response_json, latency_ms, tokens_in, tokens_out, model)
               VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)""",
            (call_id, tick, int(time.time() * 1000),
             prompt_json, response_json, latency_ms, tokens_in, tokens_out, model),
        )
        self._db.commit()

    def close(self) -> None:
        self._db.close()
