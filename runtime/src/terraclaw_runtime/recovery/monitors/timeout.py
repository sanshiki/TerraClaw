"""Timeout monitor — detects actions that have taken too long."""

from __future__ import annotations

from terraclaw_runtime.bridge.message import Observation
from terraclaw_runtime.memory.manager import MemoryManager
from terraclaw_runtime.recovery.monitor import Anomaly


class TimeoutMonitor:
    def __init__(self, default_action_timeout_s: float = 30.0, default_skill_timeout_s: float = 120.0):
        self._action_timeout = default_action_timeout_s
        self._skill_timeout = default_skill_timeout_s

    def check(self, obs: Observation, memory: MemoryManager) -> Anomaly | None:
        # Check if an action has been running too long without result
        recent_actions = memory.working.recent_actions
        if not recent_actions:
            return None

        import time
        now = time.monotonic()
        latest = recent_actions[-1]
        elapsed = now - latest.get("timestamp", now)

        if elapsed > self._action_timeout:
            return Anomaly(
                type="action_timeout",
                severity=2,
                detail=f"Action '{latest.get('type', 'unknown')}' running for {elapsed:.0f}s",
                source_monitor="timeout_monitor",
            )

        return None
