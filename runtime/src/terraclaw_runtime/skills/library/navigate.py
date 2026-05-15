"""Navigate skill — pathfind and move to a destination."""

from __future__ import annotations

import asyncio
from typing import Any

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import Action, Observation
from terraclaw_runtime.skills.registry import SkillResult


class NavigateSkill:
    name = "navigate"
    description = "Navigate to a tile coordinate. Uses pathfinding and can take several seconds. Supports mining through blocks and placing to bridge gaps."

    async def check_preconditions(self, obs: Observation, params: dict[str, Any]) -> tuple[bool, str]:
        if "target_x" not in params or "target_y" not in params:
            return False, "target_x and target_y are required"
        return True, ""

    async def execute(self, bridge: BridgeClient, obs: Observation, params: dict[str, Any]) -> SkillResult:
        """Execute navigation by sending a navigate_to action and monitoring progress."""
        action = Action(
            action_type="navigate_to",
            params={
                "target_x": params["target_x"],
                "target_y": params["target_y"],
                "max_duration_ms": params.get("max_duration_ms", 60000),
                "allow_mining": params.get("allow_mining", True),
                "allow_placing": params.get("allow_placing", True),
            },
            timeout_ms=params.get("max_duration_ms", 60000) + 5000,
        )

        await bridge.send_action(action)

        # Monitor until we arrive or timeout
        start_time = asyncio.get_event_loop().time()
        timeout = (params.get("max_duration_ms", 60000) + 5000) / 1000.0
        target_x, target_y = params["target_x"], params["target_y"]

        while True:
            elapsed = asyncio.get_event_loop().time() - start_time
            if elapsed > timeout:
                return SkillResult(success=False, message="Navigation timed out")

            fresh = await bridge.receive_observation(timeout=0.5)
            if fresh:
                player_tx = int(fresh.player.position.x / 16)
                player_ty = int(fresh.player.position.y / 16)
                dist = ((player_tx - target_x) ** 2 + (player_ty - target_y) ** 2) ** 0.5
                if dist <= 2:
                    return SkillResult(success=True, message=f"Arrived at ({target_x}, {target_y})",
                                      data={"position": (player_tx, player_ty)})

            await asyncio.sleep(0.5)

    async def cancel(self) -> None:
        pass
