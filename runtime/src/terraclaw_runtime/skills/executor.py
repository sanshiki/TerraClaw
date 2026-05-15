"""Skill executor — manages concurrent skill execution and progress tracking."""

from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field
from enum import Enum
from typing import Any


class SkillState(Enum):
    IDLE = "idle"
    CHECKING = "checking"
    RUNNING = "running"
    COMPLETED = "completed"
    FAILED = "failed"
    CANCELLED = "cancelled"


@dataclass
class SkillExecution:
    skill_name: str
    params: dict[str, Any]
    state: SkillState = SkillState.IDLE
    progress: float = 0.0
    started_at: float = 0.0
    current_step: str = ""
    task: asyncio.Task[Any] | None = None

    def elapsed_s(self) -> float:
        if self.started_at == 0:
            return 0.0
        return time.monotonic() - self.started_at


class SkillExecutor:
    """Manages the lifecycle of running skills."""

    def __init__(self, max_concurrent: int = 1, default_timeout_s: float = 120.0):
        self._max_concurrent = max_concurrent
        self._default_timeout_s = default_timeout_s
        self._active: dict[str, SkillExecution] = {}

    @property
    def active_count(self) -> int:
        return len(self._active)

    def has_active(self) -> bool:
        return len(self._active) > 0

    def get_active(self, skill_id: str) -> SkillExecution | None:
        return self._active.get(skill_id)

    async def start(self, skill_id: str, skill_name: str, params: dict[str, Any],
                    coro) -> SkillExecution:
        """Start executing a skill as an async task."""
        if self.active_count >= self._max_concurrent:
            raise RuntimeError(f"Max concurrent skills ({self._max_concurrent}) reached")

        execution = SkillExecution(
            skill_name=skill_name,
            params=params,
            state=SkillState.RUNNING,
            started_at=time.monotonic(),
        )
        execution.task = asyncio.create_task(self._run_with_timeout(skill_id, coro))
        self._active[skill_id] = execution
        return execution

    async def cancel(self, skill_id: str) -> bool:
        """Cancel a running skill."""
        execution = self._active.get(skill_id)
        if execution is None or execution.task is None:
            return False

        execution.task.cancel()
        try:
            await execution.task
        except asyncio.CancelledError:
            pass

        execution.state = SkillState.CANCELLED
        del self._active[skill_id]
        return True

    def cancel_all(self) -> None:
        for skill_id in list(self._active.keys()):
            asyncio.create_task(self.cancel(skill_id))

    async def _run_with_timeout(self, skill_id: str, coro) -> None:
        try:
            timeout = self._default_timeout_s
            await asyncio.wait_for(coro, timeout=timeout)
        except asyncio.TimeoutError:
            execution = self._active.get(skill_id)
            if execution:
                execution.state = SkillState.FAILED
        except asyncio.CancelledError:
            pass
        except Exception:
            execution = self._active.get(skill_id)
            if execution:
                execution.state = SkillState.FAILED
        finally:
            self._active.pop(skill_id, None)
