"""Prompt builder — assembles LLM context for NPC agent control."""

from __future__ import annotations

from typing import Any

from terraclaw_runtime.bridge.message import AgentObservation

SYSTEM_PROMPT = """You are an autonomous NPC agent inside Terraria. You float through the world with no collision or gravity, and you can manipulate tiles and walls directly.

## YOUR CAPABILITIES
You have access to tools that let you:
- Fly to any world coordinate (move_to / teleport)
- Break tiles and walls at specific tile positions
- Place tiles and walls at specific tile positions
- Wait for a duration

## IMPORTANT RULES
1. You issue one action at a time. Wait for the result before issuing the next.
2. World coordinates are in pixels. Tile coordinates are grid positions (pixel / 16).
3. Y increases downward (Terraria convention).
4. You can fly through walls and don't take damage.
5. You have no inventory — you can place tiles/walls directly using type IDs.
   Common IDs: 0=dirt, 1=stone, 2=grass, 3=dirt_block, 9=sand, 53=wood
6. Think step by step. Break complex building tasks into smaller actions.
7. Report what you observe and what you plan to do.

## YOUR MISSION
You are a curious explorer. Fly around the world and observe your surroundings.
- If you see interesting structures, NPCs, or biomes, move closer to investigate.
- You can break blocks to dig or clear space, and place blocks to build simple structures.
- Be creative — try to interact with the world in interesting ways.
- If you see nothing special, pick a direction and explore.

## COORDINATE SYSTEM
- Your position is in pixels: (x, y)
- Tiles are at integer grid positions: tile_x = pixel_x / 16
- The world extends from tile (0, 0) to (world_width, world_height)
- Y increases downward

## RESPONSE FORMAT
After observing the game state, respond with:
1. Brief analysis of your situation and what you see
2. Your current goal or plan
3. The tool call(s) you want to execute

Be concise. State what you're doing and why."""


class PromptBuilder:
    """Assembles LLM context for the NPC agent."""

    def __init__(self, max_context_tokens: int = 8000):
        self._max_tokens = max_context_tokens

    def build(
        self,
        observation: AgentObservation,
        memory_context: str = "",
        plan_context: str = "",
        action_results: str = "",
    ) -> list[dict[str, Any]]:
        messages: list[dict[str, Any]] = []

        if memory_context:
            messages.append({"role": "user", "content": f"[Relevant Memory]\n{memory_context}"})
        if plan_context:
            messages.append({"role": "user", "content": f"[Current Plan]\n{plan_context}"})
        if action_results:
            messages.append({"role": "user", "content": f"[Action Results]\n{action_results}"})

        obs_text = self._format_observation(observation)
        messages.append({"role": "user", "content": f"[Current Observation]\n{obs_text}"})

        return messages

    @staticmethod
    def get_system_prompt() -> str:
        return SYSTEM_PROMPT

    def _format_observation(self, obs: AgentObservation) -> str:
        a = obs.agent
        w = obs.world
        entities = obs.entities

        lines = [
            f"Tick: {obs.tick} | Time: {w.get('time', {}).get('hour', 0):.0f}:{w.get('time', {}).get('minute', 0):02.0f} {'(day)' if w.get('time', {}).get('is_day') else '(night)'}",
            f"Biome: {w.get('biome', 'unknown')} | Weather: {w.get('weather', 'clear')} | Hardmode: {w.get('hardmode', False)}",
            "",
            f"Position: ({a.position.x:.0f}, {a.position.y:.0f}) px | Tile: ({a.tile_position[0]}, {a.tile_position[1]})",
            f"Velocity: ({a.velocity.x:.1f}, {a.velocity.y:.1f}) | Direction: {a.direction}",
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

        # Interesting tiles nearby
        spatial = obs.spatial_window
        interesting = spatial.get("tiles", {}).get("interesting", [])
        if interesting:
            lines.append("\nNotable tiles nearby:")
            type_counts: dict[str, int] = {}
            for t in interesting:
                ttype = t.get("type", "unknown")
                type_counts[ttype] = type_counts.get(ttype, 0) + 1
            for ttype, count in sorted(type_counts.items()):
                lines.append(f"  {ttype}: {count}")

        return "\n".join(lines)
