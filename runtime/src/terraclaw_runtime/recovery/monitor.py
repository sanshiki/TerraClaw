"""Recovery monitor — orchestrates all detectors and escalates recovery actions."""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Any

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import Action, Observation
from terraclaw_runtime.config import RecoveryConfig
from terraclaw_runtime.memory.manager import MemoryManager
from terraclaw_runtime.recovery.monitors.stuck import StuckDetector
from terraclaw_runtime.recovery.monitors.loop import LoopDetector
from terraclaw_runtime.recovery.monitors.health import HealthMonitor
from terraclaw_runtime.recovery.monitors.timeout import TimeoutMonitor


class RecoveryLevel(Enum):
    NONE = 0
    RETRY = 1
    INTERRUPT = 2
    ESCAPE = 3
    REPLAN = 4
    EMERGENCY = 5


@dataclass
class Anomaly:
    type: str
    severity: int  # 1-5
    detail: str = ""
    source_monitor: str = ""
    timestamp: float = field(default_factory=time.monotonic)


class RecoveryMonitor:
    """Monitors agent state and triggers recovery actions when anomalies are detected."""

    def __init__(self, config: RecoveryConfig):
        self._config = config
        self._monitors = [
            StuckDetector(
                movement_threshold_px=config.stuck_movement_threshold_px,
                time_window_s=config.stuck_time_window_s,
            ),
            LoopDetector(
                max_repeats=config.loop_max_repeats,
                time_window_s=config.loop_time_window_s,
            ),
            HealthMonitor(
                health_ratio_threshold=config.health_ratio_threshold,
                enemy_proximity_threshold_px=config.enemy_proximity_threshold_px,
            ),
            TimeoutMonitor(
                default_action_timeout_s=config.default_action_timeout_s,
                default_skill_timeout_s=config.default_skill_timeout_s,
            ),
        ]
        self._anomaly_history: list[Anomaly] = []
        self._bridge: BridgeClient | None = None
        self._last_recovery_time = 0.0

    def set_bridge(self, bridge: BridgeClient) -> None:
        self._bridge = bridge

    def check(self, obs: Observation, memory: MemoryManager) -> Anomaly | None:
        """Run all monitors and return the most severe anomaly, if any."""
        anomalies: list[Anomaly] = []

        for monitor in self._monitors:
            anomaly = monitor.check(obs, memory)
            if anomaly:
                anomalies.append(anomaly)

        if not anomalies:
            return None

        # Pick the most severe
        anomalies.sort(key=lambda a: a.severity, reverse=True)
        worst = anomalies[0]
        self._anomaly_history.append(worst)

        # Keep history bounded
        if len(self._anomaly_history) > 100:
            self._anomaly_history = self._anomaly_history[-100:]

        return worst

    def get_recovery_level(self, anomaly: Anomaly) -> RecoveryLevel:
        """Determine the appropriate recovery level for an anomaly."""
        consecutive = self._count_consecutive(anomaly.source_monitor)

        if anomaly.severity >= 4 or consecutive >= 3:
            return RecoveryLevel.EMERGENCY
        elif anomaly.severity >= 3 or consecutive >= 2:
            return RecoveryLevel.ESCAPE if anomaly.type == "health_critical" else RecoveryLevel.REPLAN
        elif anomaly.severity >= 2:
            return RecoveryLevel.INTERRUPT
        else:
            return RecoveryLevel.RETRY

    async def execute_recovery(self, level: RecoveryLevel, anomaly: Anomaly) -> str:
        """Execute the recovery action. Returns a description of what was done."""
        if self._bridge is None:
            return "No bridge connected"

        # Throttle recovery actions
        now = time.monotonic()
        if now - self._last_recovery_time < 1.0:
            return "Recovery throttled"
        self._last_recovery_time = now

        if level == RecoveryLevel.RETRY:
            return f"Retry: {anomaly.detail}"

        elif level == RecoveryLevel.INTERRUPT:
            await self._bridge.clear_action_queue()
            return f"Interrupted: cancelled current actions due to {anomaly.type}"

        elif level == RecoveryLevel.ESCAPE:
            await self._bridge.clear_action_queue()

            # Use recall potion / magic mirror if available
            await self._bridge.send_action(Action(
                action_type="use_consumable",
                params={"item_type": "Magic Mirror", "from_slot": -1},
                priority=0,
                timeout_ms=3000,
            ))
            await self._bridge.send_action(Action(
                action_type="use_consumable",
                params={"item_type": "Recall Potion", "from_slot": -1},
                priority=0,
                timeout_ms=3000,
            ))

            # Also try to heal
            await self._bridge.send_action(Action(
                action_type="use_consumable",
                params={"item_type": "Healing Potion"},
                priority=0,
                timeout_ms=3000,
            ))

            return f"Escape: teleporting home and healing due to {anomaly.type}"

        elif level == RecoveryLevel.REPLAN:
            await self._bridge.clear_action_queue()
            return f"Replan: full replan needed due to {anomaly.type}"

        elif level == RecoveryLevel.EMERGENCY:
            await self._bridge.clear_action_queue()

            # Emergency sequence: heal, escape, wait
            await self._bridge.send_action(Action(
                action_type="use_consumable",
                params={"item_type": "Healing Potion"},
                priority=0,
                timeout_ms=3000,
            ))
            await self._bridge.send_action(Action(
                action_type="use_consumable",
                params={"item_type": "Magic Mirror"},
                priority=0,
                timeout_ms=3000,
            ))
            await self._bridge.send_action(Action(
                action_type="wait",
                params={"duration_ms": 3000},
                priority=0,
            ))

            return f"Emergency: full emergency recovery for {anomaly.type}"

        return f"Unknown recovery level: {level}"

    def _count_consecutive(self, source_monitor: str) -> int:
        """Count how many consecutive anomalies are from the same monitor."""
        count = 0
        for a in reversed(self._anomaly_history):
            if a.source_monitor == source_monitor:
                count += 1
            else:
                break
        return count
