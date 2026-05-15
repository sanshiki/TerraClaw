"""Memory manager — coordinates all memory subsystems."""

from __future__ import annotations

from terraclaw_runtime.bridge.message import Observation
from terraclaw_runtime.memory.episodic.store import EpisodicStore
from terraclaw_runtime.memory.spatial.world_map import SpatialMemory
from terraclaw_runtime.memory.working import WorkingMemory


class MemoryManager:
    """Coordinates spatial, episodic, and working memory."""

    def __init__(self, db_path: str, working_max_items: int = 100):
        self.spatial = SpatialMemory(db_path)
        self.episodic = EpisodicStore(db_path)
        self.working = WorkingMemory(max_items=working_max_items)

    def update(self, obs: Observation) -> None:
        """Update all memory subsystems from a new observation."""
        # Working memory — always updated
        self.working.update_observation(obs)

        # Spatial memory — update from spatial window
        player_tx = int(obs.player.position.x / 16)
        player_ty = int(obs.player.position.y / 16)
        if obs.spatial_window:
            self.spatial.update_from_observation(
                obs.tick, player_tx, player_ty, obs.spatial_window,
            )

        # Episodic — record significant state changes
        hp_ratio = obs.player.health_current / max(obs.player.health_max, 1)
        if hp_ratio < 0.5:
            self.episodic.record_event(
                obs.tick, "player.low_health",
                {"current": obs.player.health_current, "max": obs.player.health_max},
                importance=0.7,
            )

    def get_context_for_llm(self, obs: Observation) -> str:
        """Assemble memory context for the LLM prompt."""
        parts = []

        # Working memory summary
        working_summary = self.working.get_recent_summary()
        if working_summary:
            parts.append(working_summary)

        # Spatial context
        player_tx = int(obs.player.position.x / 16)
        player_ty = int(obs.player.position.y / 16)
        spatial = self.spatial.query_nearby(player_tx, player_ty, radius_tiles=40)
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
