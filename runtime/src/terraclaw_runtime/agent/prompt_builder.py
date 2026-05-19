"""Prompt builder — assembles LLM context for NPC agent control."""

from __future__ import annotations

from pathlib import Path
from typing import Any

from terraclaw_runtime.bridge.message import AgentObservation

# Embedded fallback prompt for when the markdown file is unavailable
_FALLBACK_SYSTEM_PROMPT = (
    "You are an autonomous NPC agent inside Terraria. "
    "You float through the world with no collision or gravity."
)


class PromptBuilder:
    """Assembles LLM context for the NPC agent.

    Loads the system prompt from a markdown file on first access.
    """

    def __init__(self, prompts_path: str = "config/prompts", max_context_tokens: int = 8000):
        self._prompts_path = Path(prompts_path)
        self._max_tokens = max_context_tokens
        self._system_prompt: str | None = None

    def get_system_prompt(self, variables: dict[str, str] | None = None) -> str:
        """Return the system prompt, loading from markdown on first call.

        Supports {variable} substitution in the prompt text.
        """
        if self._system_prompt is None:
            self._system_prompt = self._load_prompt("system.md")

        prompt = self._system_prompt
        if variables:
            for key, val in variables.items():
                prompt = prompt.replace(f"{{{key}}}", val)
        return prompt

    def build(
        self,
        observation: AgentObservation,
        memory_context: str = "",
        plan_context: str = "",
        action_results: str = "",
        pending_context: str = "",
        completed_context: str = "",
        instructions: str = "",
        resolved_context: str = "",
    ) -> list[dict[str, Any]]:
        messages: list[dict[str, Any]] = []

        if memory_context:
            messages.append({"role": "user", "content": f"[Relevant Memory]\n{memory_context}"})
        if plan_context:
            messages.append({"role": "user", "content": f"[Current Plan]\n{plan_context}"})
        if action_results:
            messages.append({"role": "user", "content": f"[Action Results]\n{action_results}"})
        if pending_context:
            messages.append({"role": "user", "content": f"[Pending Actions]\n{pending_context}"})
        if completed_context:
            messages.append({"role": "user", "content": f"[Completed Actions]\n{completed_context}"})
        if resolved_context:
            messages.append({"role": "user", "content": f"[Resolved Actions]\n{resolved_context}"}
)
        if instructions:
            messages.append({"role": "user", "content": f"[Player Instructions]\n{instructions}"})

        obs_text = self._format_observation(observation)
        messages.append({"role": "user", "content": f"[Current Observation]\n{obs_text}"})

        return messages

    def _load_prompt(self, name: str) -> str:
        """Load a prompt from a markdown file, falling back to embedded default."""
        path = self._prompts_path / name
        if path.exists():
            return path.read_text(encoding="utf-8").strip()
        return _FALLBACK_SYSTEM_PROMPT

    def _format_observation(self, obs: AgentObservation) -> str:
        a = obs.agent
        w = obs.world
        entities = obs.entities

        lines = [
            f"Tick: {obs.tick} | Time: {w.get('time', {}).get('hour', 0):.0f}:{w.get('time', {}).get('minute', 0):02.0f} {'(day)' if w.get('time', {}).get('is_day') else '(night)'}",
            f"Layer: {w.get('depth_layer', '?')} | Weather: {w.get('weather', 'clear')} | Hardmode: {w.get('hardmode', False)}",
            "",
            f"Position: ({a.position.x:.0f}, {a.position.y:.0f}) px | Tile: ({a.tile_position[0]}, {a.tile_position[1]})",
        ]

        # Entities
        npcs = entities.get("npcs", [])
        if npcs:
            lines.append("\nNearby NPCs:")
            for n in npcs:
                dist = n.get("distance_to_center", 0)
                hostile = "HOSTILE" if n.get("is_hostile") else "friendly"
                boss = " BOSS" if n.get("is_boss") else ""
                lines.append(f"  {n['type']} [{hostile}{boss}] at ({n['position']['x']:.0f}, {n['position']['y']:.0f}) dist={dist:.0f}px")

        items = entities.get("items_on_ground", [])
        if items:
            lines.append("\nItems on ground:")
            for item in items[:10]:
                dist = item.get("distance_to_center", 0)
                lines.append(f"  {item['type']} x{item['stack']} dist={dist:.0f}px")

        projectiles = entities.get("projectiles", [])
        hostile_proj = [p for p in projectiles if p.get("is_hostile")]
        if hostile_proj:
            lines.append(f"\nHostile projectiles: {len(hostile_proj)} nearby")

        # Active events / bosses
        bosses = w.get("bosses_alive", [])
        events = w.get("active_events", [])
        if bosses:
            lines.append(f"\nBosses alive: {', '.join(bosses)}")
        if events:
            lines.append(f"Active events: {', '.join(events)}")

        # Interesting tiles nearby with positions
        spatial = obs.spatial_window
        interesting = spatial.get("tiles", {}).get("interesting", [])
        if interesting:
            lines.append("\nNotable tiles nearby (tile coordinates):")
            for t in interesting[:20]:
                pos = t.get("pos", {})
                tx = pos.get("x", "?")
                ty = pos.get("y", "?")
                ttype = t.get("type", "unknown")
                lines.append(f"  ({tx}, {ty}) {ttype}")

        return "\n".join(lines)
