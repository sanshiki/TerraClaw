"""Loop detector — detects when the same action pattern repeats excessively."""

from __future__ import annotations

from terraclaw_runtime.bridge.message import Observation
from terraclaw_runtime.memory.manager import MemoryManager
from terraclaw_runtime.recovery.monitor import Anomaly


class LoopDetector:
    def __init__(self, max_repeats: int = 5, time_window_s: float = 30.0):
        self._max_repeats = max_repeats
        self._time_window = time_window_s

    def check(self, obs: Observation, memory: MemoryManager) -> Anomaly | None:
        actions = memory.episodic.get_actions_last_n_seconds(
            self._time_window, obs.tick,
        )

        if len(actions) < self._max_repeats:
            return None

        # Extract action types
        action_types = [a.get("type", "") for a in actions]

        # Look for repeating patterns using sliding windows of sizes 2, 3, 4
        for window_size in [4, 3, 2]:
            if len(action_types) < window_size * 2:
                continue

            # Check the last few windows
            pattern = tuple(action_types[-window_size:])
            count = 0
            for i in range(len(action_types) - window_size + 1):
                if tuple(action_types[i:i + window_size]) == pattern:
                    count += 1

            if count >= self._max_repeats:
                return Anomaly(
                    type="action_loop",
                    severity=3,
                    detail=f"Action pattern repeated {count} times: {list(pattern)}",
                    source_monitor="loop_detector",
                )

        return None
