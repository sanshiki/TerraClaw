"""Stuck detector — detects when the player hasn't moved significantly."""

from __future__ import annotations

import math

from terraclaw_runtime.bridge.message import Observation
from terraclaw_runtime.memory.manager import MemoryManager
from terraclaw_runtime.recovery.monitor import Anomaly


class StuckDetector:
    def __init__(self, movement_threshold_px: float = 30.0, time_window_s: float = 10.0):
        self._movement_threshold = movement_threshold_px
        self._time_window = time_window_s

    def check(self, obs: Observation, memory: MemoryManager) -> Anomaly | None:
        positions = memory.working.get_positions_last_n_seconds(self._time_window)

        if len(positions) < 2:
            return None

        start_x, start_y = positions[0]
        end_x, end_y = positions[-1]

        displacement = math.sqrt((end_x - start_x) ** 2 + (end_y - start_y) ** 2)

        # Compute total path length
        total_path = 0.0
        for i in range(1, len(positions)):
            dx = positions[i][0] - positions[i - 1][0]
            dy = positions[i][1] - positions[i - 1][1]
            total_path += math.sqrt(dx * dx + dy * dy)

        # Stuck: very little net movement
        if displacement < self._movement_threshold and obs.player.is_grounded:
            return Anomaly(
                type="stuck",
                severity=3,
                detail=f"Only moved {displacement:.1f}px in {self._time_window}s",
                source_monitor="stuck_detector",
            )

        # Inefficient movement: moving but going in circles
        if total_path > 0 and displacement / max(total_path, 0.01) < 0.1 and total_path > 100:
            return Anomaly(
                type="inefficient_movement",
                severity=2,
                detail=f"Path efficiency {displacement / total_path:.1%}",
                source_monitor="stuck_detector",
            )

        return None
