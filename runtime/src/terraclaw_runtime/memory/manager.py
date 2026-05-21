"""Memory manager — coordinates all memory subsystems."""

from __future__ import annotations

from pathlib import Path

from terraclaw_runtime.bridge.message import AgentObservation
from terraclaw_runtime.memory.episodic.store import EpisodicStore
from terraclaw_runtime.memory.spatial.world_map import SpatialMemory
from terraclaw_runtime.memory.working import WorkingMemory


class MemoryManager:
    """Coordinates spatial, episodic, and working memory."""

    def __init__(self, db_path: str, working_max_items: int = 100):
        # Ensure parent directory exists
        Path(db_path).parent.mkdir(parents=True, exist_ok=True)
        self.spatial = SpatialMemory(db_path)
        self.episodic = EpisodicStore(db_path)
        self.working = WorkingMemory(max_items=working_max_items)

    def update(self, obs: AgentObservation) -> None:
        """Update all memory subsystems from a new observation."""
        # Working memory — always updated
        self.working.update_observation(obs)

        # Spatial memory — update from spatial window
        agent_tx = int(obs.agent.position.x / 16)
        agent_ty = int(obs.agent.position.y / 16)
        if obs.spatial_window:
            self.spatial.update_from_observation(
                obs.tick, agent_tx, agent_ty, obs.spatial_window,
            )

    def record_action(self, tick: int, action_type: str, params: dict,
                      result: dict, success: bool) -> None:
        """Record an action in working + episodic memory."""
        self.working.add_action(action_type, params)
        self.episodic.record_action(tick, action_type, params, result, success)

    def get_context_for_llm(self, obs: AgentObservation) -> str:
        """Assemble memory context for the LLM prompt."""
        parts = []

        # Working memory summary
        working_summary = self.working.get_recent_summary()
        if working_summary:
            parts.append(working_summary)

        # Spatial context
        agent_tx = int(obs.agent.position.x / 16)
        agent_ty = int(obs.agent.position.y / 16)
        spatial = self.spatial.query_nearby(agent_tx, agent_ty, radius_tiles=40)
        if spatial:
            parts.append(spatial)

        # Recent notable episodes
        recent = self.episodic.get_recent(limit=10, min_importance=0.5)
        if recent:
            lines = ["Recent notable events:"]
            for ep in recent:
                lines.append(f"  [t={ep['tick']}] {ep['summary']}")
            parts.append("\n".join(lines))

        return "\n\n".join(parts)

    def close(self) -> None:
        self.spatial.close()
        self.episodic.close()
