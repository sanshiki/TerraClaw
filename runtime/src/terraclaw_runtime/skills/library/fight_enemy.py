"""FightEnemy skill — engage in combat with nearby enemies."""

from __future__ import annotations

import asyncio
from typing import Any

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import Action, Observation
from terraclaw_runtime.skills.registry import SkillResult


class FightEnemySkill:
    name = "fight_enemy"
    description = "Engage in combat with nearby enemies. Can target a specific enemy type or fight the nearest hostile. Supports melee, ranged, and flee styles."

    async def check_preconditions(self, obs: Observation, params: dict[str, Any]) -> tuple[bool, str]:
        # Check if there are enemies nearby
        npcs = obs.entities.get("npcs", [])
        hostile = [n for n in npcs if n.get("is_hostile")]
        if not hostile:
            return False, "No hostile enemies nearby"
        return True, ""

    async def execute(self, bridge: BridgeClient, obs: Observation, params: dict[str, Any]) -> SkillResult:
        style = params.get("style", "melee")
        target_type = params.get("target_type")
        max_duration_ms = params.get("max_duration_ms", 30000)

        await bridge.send_action(Action(
            action_type="combat",
            params={
                "style": style,
                "target_type": target_type,
                "max_duration_ms": max_duration_ms,
            },
            timeout_ms=max_duration_ms + 5000,
        ))

        # Monitor combat
        start_time = asyncio.get_event_loop().time()
        timeout = (max_duration_ms + 5000) / 1000.0

        while True:
            elapsed = asyncio.get_event_loop().time() - start_time
            if elapsed > timeout:
                return SkillResult(success=True, message="Combat duration elapsed")

            fresh = await bridge.receive_observation(timeout=0.5)
            if fresh:
                hostile = [n for n in fresh.entities.get("npcs", []) if n.get("is_hostile")]
                if not hostile:
                    return SkillResult(success=True, message="No hostiles remaining")

                # Check health
                hp_ratio = fresh.player.health_current / max(fresh.player.health_max, 1)
                if hp_ratio < 0.3:
                    await bridge.send_action(Action(
                        action_type="combat",
                        params={"style": "flee", "max_duration_ms": 10000},
                        timeout_ms=15000,
                    ))
                    return SkillResult(success=False, message="Fled due to low health")

            await asyncio.sleep(0.5)

    async def cancel(self) -> None:
        pass
