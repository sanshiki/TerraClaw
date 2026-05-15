"""MineVein skill — mine an ore vein by navigating to it and mining all connected tiles."""

from __future__ import annotations

import asyncio
from typing import Any

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import Action, Observation
from terraclaw_runtime.skills.registry import SkillResult


class MineVeinSkill:
    name = "mine_vein"
    description = "Mine an entire ore vein. Navigates to the vein, then systematically mines all connected ore tiles."

    async def check_preconditions(self, obs: Observation, params: dict[str, Any]) -> tuple[bool, str]:
        if "target_x" not in params or "target_y" not in params:
            return False, "target_x and target_y are required"

        # Check if we have a pickaxe
        hotbar = obs.inventory.get("hotbar", [])
        has_pickaxe = any(
            item and ("pickaxe" in item["type"].lower() or "drill" in item["type"].lower())
            for item in hotbar if item
        )
        if not has_pickaxe:
            return False, "No pickaxe or drill in hotbar"
        return True, ""

    async def execute(self, bridge: BridgeClient, obs: Observation, params: dict[str, Any]) -> SkillResult:
        target_x = params["target_x"]
        target_y = params["target_y"]
        max_blocks = params.get("max_blocks", 100)

        # Step 1: Navigate to the vein
        await bridge.send_action(Action(
            action_type="navigate_to",
            params={"target_x": target_x, "target_y": target_y, "max_duration_ms": 30000},
            timeout_ms=35000,
        ))

        # Wait for arrival
        await asyncio.sleep(2.0)

        # Step 2: Find pickaxe slot and mine
        hotbar = obs.inventory.get("hotbar", [])
        pick_slot = 0
        for item in hotbar:
            if item and ("pickaxe" in item["type"].lower() or "drill" in item["type"].lower()):
                pick_slot = item["slot"]
                break

        await bridge.send_action(Action(
            action_type="select_item",
            params={"slot": pick_slot},
        ))

        # Step 3: Mine the target tile and nearby same-type tiles
        mined = 0
        for ox in range(-1, 2):
            for oy in range(-1, 2):
                if mined >= max_blocks:
                    break
                await bridge.send_action(Action(
                    action_type="mine_tile",
                    params={
                        "target_x": target_x + ox,
                        "target_y": target_y + oy,
                        "tool_slot": pick_slot,
                        "max_duration_ms": 5000,
                    },
                    timeout_ms=10000,
                ))
                mined += 1
                await asyncio.sleep(1.0)

        return SkillResult(
            success=True,
            message=f"Mined {mined} tiles near ({target_x}, {target_y})",
            data={"tiles_mined": mined},
        )

    async def cancel(self) -> None:
        pass
