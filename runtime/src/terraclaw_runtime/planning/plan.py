"""Plan representation — goal decomposition and step tracking."""

from __future__ import annotations

import time
from dataclasses import dataclass, field
from enum import Enum


class PlanStatus(Enum):
    ACTIVE = "active"
    COMPLETE = "complete"
    FAILED = "failed"
    PAUSED = "paused"


@dataclass
class PlanStep:
    description: str
    tool_name: str | None = None
    tool_params: dict | None = None
    status: str = "pending"  # pending, in_progress, completed, failed, skipped
    started_at: float | None = None
    completed_at: float | None = None
    max_duration_s: float = 60.0
    result: str | None = None

    def has_timed_out(self) -> bool:
        if self.started_at is None or self.status != "in_progress":
            return False
        return time.monotonic() - self.started_at > self.max_duration_s


@dataclass
class Plan:
    goal: str
    steps: list[PlanStep] = field(default_factory=list)
    status: PlanStatus = PlanStatus.ACTIVE
    created_at: float = field(default_factory=time.monotonic)
    completed_at: float | None = None
    notes: str = ""

    @property
    def current_step(self) -> PlanStep | None:
        for step in self.steps:
            if step.status == "in_progress":
                return step
        # Return first pending step
        for step in self.steps:
            if step.status == "pending":
                return step
        return None

    def is_complete(self) -> bool:
        return self.status == PlanStatus.COMPLETE or all(
            s.status in ("completed", "skipped", "failed")
            for s in self.steps
            if s.status != "pending"
        )

    def to_context_string(self) -> str:
        lines = [f"Goal: {self.goal}", f"Status: {self.status.value}", "Steps:"]
        for i, step in enumerate(self.steps):
            icon = {"pending": "○", "in_progress": "●", "completed": "✓", "failed": "✗", "skipped": "−"}
            lines.append(f"  {icon.get(step.status, '?')} Step {i + 1}: {step.description}")
            if step.result:
                lines.append(f"     Result: {step.result}")
        if self.notes:
            lines.append(f"Notes: {self.notes}")
        return "\n".join(lines)

    @classmethod
    def from_llm_output(cls, goal: str, steps_data: list[dict]) -> Plan:
        steps = [
            PlanStep(
                description=s.get("description", ""),
                tool_name=s.get("tool"),
                tool_params=s.get("params"),
                max_duration_s=s.get("max_duration_s", 60.0),
            )
            for s in steps_data
        ]
        return cls(goal=goal, steps=steps)
