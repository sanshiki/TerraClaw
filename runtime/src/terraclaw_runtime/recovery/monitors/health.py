"""Health monitor — triggers recovery when health is dangerously low."""

from __future__ import annotations

from terraclaw_runtime.bridge.message import Observation
from terraclaw_runtime.memory.manager import MemoryManager
from terraclaw_runtime.recovery.monitor import Anomaly


class HealthMonitor:
    def __init__(self, health_ratio_threshold: float = 0.3, enemy_proximity_threshold_px: float = 200.0):
        self._health_ratio_threshold = health_ratio_threshold
        self._enemy_proximity_threshold = enemy_proximity_threshold_px

    def check(self, obs: Observation, memory: MemoryManager) -> Anomaly | None:
        hp_ratio = obs.player.health_current / max(obs.player.health_max, 1)

        # Check breath
        if obs.player.is_in_liquid and obs.player.breath_current < 50:
            return Anomaly(
                type="breath_low",
                severity=4,
                detail=f"Breath at {obs.player.breath_current}, drowning imminent",
                source_monitor="health_monitor",
            )

        # Check for nearby enemies
        hostiles = [
            n for n in obs.entities.get("npcs", [])
            if n.get("is_hostile") and n.get("distance_to_player", 999) < self._enemy_proximity_threshold
        ]

        # Critical: low HP + enemies nearby
        if hp_ratio < self._health_ratio_threshold and hostiles:
            return Anomaly(
                type="health_critical",
                severity=5,
                detail=f"HP at {hp_ratio:.0%} with {len(hostiles)} enemies nearby",
                source_monitor="health_monitor",
            )

        # Moderate: low HP, no immediate threat
        if hp_ratio < self._health_ratio_threshold:
            return Anomaly(
                type="health_low",
                severity=3,
                detail=f"HP at {hp_ratio:.0%}, should heal",
                source_monitor="health_monitor",
            )

        # Warning: enemies nearby, HP okay but declining
        if hostiles and hp_ratio < 0.5:
            return Anomaly(
                type="combat_warning",
                severity=2,
                detail=f"HP {hp_ratio:.0%} with {len(hostiles)} enemies nearby",
                source_monitor="health_monitor",
            )

        return None
