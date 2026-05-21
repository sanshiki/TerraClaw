"""Working memory — volatile short-term context for the current agent session."""

from __future__ import annotations

import time
from collections import deque
from dataclasses import dataclass, field
from typing import Any

from terraclaw_runtime.bridge.message import AgentObservation, Vec2


@dataclass
class WorkingMemory:
    """Holds recent observations, the current plan, and active constraints."""

    max_items: int = 100
    recent_observations: deque[AgentObservation] = field(default_factory=deque)
    recent_positions: deque[tuple[float, float, float]] = field(default_factory=deque)  # (x, y, timestamp)
    recent_actions: deque[dict[str, Any]] = field(default_factory=deque)
    recent_events: deque[dict[str, Any]] = field(default_factory=deque)
    current_goal: str = ""
    active_constraints: list[str] = field(default_factory=list)
    last_llm_response: str = ""
    session_stats: dict[str, Any] = field(default_factory=dict)

    def update_observation(self, obs: AgentObservation) -> None:
        self.recent_observations.append(obs)
        if len(self.recent_observations) > self.max_items:
            self.recent_observations.popleft()

        self.recent_positions.append((
            obs.agent.position.x,
            obs.agent.position.y,
            time.monotonic(),
        ))
        if len(self.recent_positions) > 300:  # ~30 seconds at 10Hz
            self.recent_positions.popleft()

    def add_action(self, action_type: str, params: dict[str, Any]) -> None:
        self.recent_actions.append({
            "type": action_type,
            "params": params,
            "timestamp": time.monotonic(),
        })
        if len(self.recent_actions) > 50:
            self.recent_actions.popleft()

    def add_event(self, event_type: str, data: dict[str, Any]) -> None:
        self.recent_events.append({
            "type": event_type,
            "data": data,
            "timestamp": time.monotonic(),
        })
        if len(self.recent_events) > 50:
            self.recent_events.popleft()

    def get_positions_last_n_seconds(self, seconds: float) -> list[tuple[float, float]]:
        """Get position history for the last N seconds."""
        cutoff = time.monotonic() - seconds
        return [(x, y) for x, y, ts in self.recent_positions if ts >= cutoff]

    def get_recent_summary(self) -> str:
        """Summarize recent activity for the LLM."""
        lines = []
        if self.current_goal:
            lines.append(f"Current goal: {self.current_goal}")
        if self.active_constraints:
            lines.append(f"Constraints: {', '.join(self.active_constraints)}")

        recent_actions = list(self.recent_actions)[-5:]
        if recent_actions:
            lines.append("Recent actions:")
            for a in recent_actions:
                lines.append(f"  - {a['type']}")

        recent_events = [e for e in self.recent_events if e.get("importance", 5) >= 7][-5:]
        if recent_events:
            lines.append("Significant events:")
            for e in recent_events:
                lines.append(f"  - {e['type']}")

        return "\n".join(lines)

    def clear(self) -> None:
        self.recent_observations.clear()
        self.recent_positions.clear()
        self.recent_actions.clear()
        self.recent_events.clear()
        self.current_goal = ""
        self.active_constraints.clear()
