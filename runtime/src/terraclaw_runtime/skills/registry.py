"""Skill registry — manages skill definitions and dispatches skill execution."""

from __future__ import annotations

from pathlib import Path
from dataclasses import dataclass, field
from typing import TYPE_CHECKING, Any, Protocol

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.skills.lua_skill import LuaSkill

if TYPE_CHECKING:
    from terraclaw_runtime.bridge.message import AgentObservation as Observation


class SkillRunner(Protocol):
    """Protocol for skill implementations."""
    name: str
    description: str

    async def check_preconditions(self, obs: Observation, params: dict[str, Any]) -> tuple[bool, str]: ...
    async def execute(self, bridge: BridgeClient, obs: Observation, params: dict[str, Any]) -> SkillResult: ...
    async def cancel(self) -> None: ...


@dataclass
class SkillResult:
    success: bool
    message: str = ""
    data: dict[str, Any] = field(default_factory=dict)
    actions_sent: int = 0


@dataclass
class SkillStats:
    attempts: int = 0
    successes: int = 0
    failures: int = 0
    avg_duration_ms: float = 0.0

    @property
    def success_rate(self) -> float:
        if self.attempts == 0:
            return 0.0
        return self.successes / self.attempts


class SkillRegistry:
    """Registry of reusable skills the LLM can invoke.

    Supports both Python-based SkillRunner implementations and
    .lua scripts loaded from a directory.
    """

    def __init__(self, bridge: BridgeClient):
        self._bridge = bridge
        self._skills: dict[str, SkillRunner] = {}
        self._stats: dict[str, SkillStats] = {}
        self._active_skill: str | None = None

    def register(self, skill: SkillRunner) -> None:
        self._skills[skill.name] = skill
        self._stats[skill.name] = SkillStats()

    def load_from_directory(self, path: str | Path) -> int:
        """Load all .lua files from a directory as skills."""
        skills_dir = Path(path)
        if not skills_dir.is_dir():
            return 0

        loaded = 0
        for lua_file in sorted(skills_dir.glob("*.lua")):
            lua_skill = LuaSkill(lua_file)
            wrapper = _LuaSkillWrapper(lua_skill)
            self.register(wrapper)
            loaded += 1
        return loaded

    def has_active(self) -> bool:
        return self._active_skill is not None

    def get(self, name: str) -> SkillRunner | None:
        return self._skills.get(name)

    def describe_all(self) -> str:
        lines = ["Available skills:"]
        for name, skill in self._skills.items():
            stats = self._stats.get(name, SkillStats())
            rate = f"{stats.success_rate:.0%}" if stats.attempts > 0 else "N/A"
            lines.append(f"  - {name}: {skill.description} (success rate: {rate})")
        return "\n".join(lines)

    def get_tool_definitions(self) -> list[dict[str, Any]]:
        return [
            {
                "type": "function",
                "function": {
                    "name": f"skill_{skill.name}",
                    "description": skill.description,
                    "parameters": {
                        "type": "object",
                        "properties": {
                            "skill_name": {"type": "string", "const": skill.name},
                        },
                    },
                },
            }
            for skill in self._skills.values()
        ]

    async def execute_skill(self, name: str, params: dict[str, Any],
                            obs: Observation) -> SkillResult:
        skill = self._skills.get(name)
        if skill is None:
            return SkillResult(success=False, message=f"Unknown skill: {name}")

        ok, reason = await skill.check_preconditions(obs, params)
        if not ok:
            self._record_result(name, False)
            return SkillResult(success=False, message=f"Precondition failed: {reason}")

        self._active_skill = name
        try:
            result = await skill.execute(self._bridge, obs, params)
            self._record_result(name, result.success)
            return result
        except Exception as e:
            self._record_result(name, False)
            return SkillResult(success=False, message=str(e))
        finally:
            self._active_skill = None

    def _record_result(self, name: str, success: bool) -> None:
        stats = self._stats.get(name, SkillStats())
        stats.attempts += 1
        if success:
            stats.successes += 1
        else:
            stats.failures += 1

    def recommend(self, obs: Observation) -> list[str]:
        recommended = []
        for name, stats in self._stats.items():
            if stats.attempts >= 3 and stats.success_rate >= 0.7:
                recommended.append(name)
        return recommended


class _LuaSkillWrapper:
    """Wraps a LuaSkill to conform to the SkillRunner Protocol."""

    def __init__(self, lua_skill: LuaSkill):
        self._skill = lua_skill

    @property
    def name(self) -> str:
        return self._skill.name

    @property
    def description(self) -> str:
        return self._skill.description

    async def check_preconditions(self, obs: Observation, params: dict[str, Any]) -> tuple[bool, str]:
        return self._skill.check_preconditions(obs, params)

    async def execute(self, bridge: BridgeClient, obs: Observation, params: dict[str, Any]) -> SkillResult:
        result = await self._skill.execute(bridge, obs, params)
        return SkillResult(
            success=result.get("success", False),
            message=result.get("message", result.get("error", "")),
            data=result,
        )

    async def cancel(self) -> None:
        pass
