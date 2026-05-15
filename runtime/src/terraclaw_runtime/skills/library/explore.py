"""Explore skill — systematically explore unknown territory."""

from __future__ import annotations

import asyncio
from typing import Any

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import Action, Observation
from terraclaw_runtime.skills.registry import SkillResult


class ExploreSkill:
    name = "explore"
    description = "Explore the surrounding area. Moves in an expanding spiral pattern, reporting interesting discoveries."

    async def check_preconditions(self, obs: Observation, params: dict[str, Any]) -> tuple[bool, str]:
        return True, ""

    async def execute(self, bridge: BridgeClient, obs: Observation, params: dict[str, Any]) -> SkillResult:
        direction = params.get("direction", "right")
        steps = params.get("steps", 5)
        step_distance = params.get("step_distance_ms", 2000)

        discoveries: list[str] = []

        for i in range(steps):
            # Move in the exploration direction
            await bridge.send_action(Action(
                action_type="move",
                params={
                    "direction": direction,
                    "duration_ms": step_distance,
                    "auto_jump": True,
                },
                timeout_ms=step_distance + 3000,
            ))

            await asyncio.sleep(step_distance / 1000.0 + 0.5)

            # Observe what we found
            fresh = await bridge.receive_observation(timeout=1.0)
            if fresh:
                interesting = fresh.spatial_window.get("tiles", {}).get("interesting", [])
                for tile in interesting:
                    ttype = tile.get("type", "")
                    if ttype not in ("door", "work_bench", "pressure_plate"):
                        discoveries.append(ttype)

                # Check for enemies
                hostiles = [n for n in fresh.entities.get("npcs", []) if n.get("is_hostile")]
                if hostiles:
                    discoveries.append(f"enemies:{len(hostiles)}")

        return SkillResult(
            success=True,
            message=f"Explored {steps} steps {direction}. Found: {', '.join(discoveries) if discoveries else 'nothing notable'}",
            data={"discoveries": discoveries, "steps": steps},
        )

    async def cancel(self) -> None:
        pass
