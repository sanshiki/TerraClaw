"""Message type definitions matching the protocol JSON schemas."""

from __future__ import annotations

import uuid
from dataclasses import dataclass, field
from typing import Any

# ── Envelope ──────────────────────────────────────────────


@dataclass
class MessageEnvelope:
    id: str = field(default_factory=lambda: str(uuid.uuid4()))
    type: str = ""
    timestamp: int = 0
    session_id: str | None = None
    in_reply_to: str | None = None
    payload: dict[str, Any] = field(default_factory=dict)


# ── Observation ───────────────────────────────────────────


@dataclass
class Vec2:
    x: float = 0.0
    y: float = 0.0


@dataclass
class PlayerState:
    position: Vec2 = field(default_factory=Vec2)
    velocity: Vec2 = field(default_factory=Vec2)
    health_current: int = 0
    health_max: int = 0
    mana_current: int = 0
    mana_max: int = 0
    breath_current: int = 0
    breath_max: int = 0
    direction: str = "right"
    is_grounded: bool = False
    is_in_liquid: bool = False
    liquid_type: str = "none"
    active_buffs: list[dict[str, Any]] = field(default_factory=list)
    mount: str | None = None
    grappling: bool = False
    selected_hotbar_slot: int = 0

    @classmethod
    def from_payload(cls, data: dict[str, Any]) -> PlayerState:
        pos = data.get("position", {})
        vel = data.get("velocity", {})
        hp = data.get("health", {})
        mp = data.get("mana", {})
        br = data.get("breath", {})
        return cls(
            position=Vec2(x=pos.get("x", 0), y=pos.get("y", 0)),
            velocity=Vec2(x=vel.get("x", 0), y=vel.get("y", 0)),
            health_current=hp.get("current", 0),
            health_max=hp.get("max", 0),
            mana_current=mp.get("current", 0),
            mana_max=mp.get("max", 0),
            breath_current=br.get("current", 0),
            breath_max=br.get("max", 0),
            direction=data.get("direction", "right"),
            is_grounded=data.get("is_grounded", False),
            is_in_liquid=data.get("is_in_liquid", False),
            liquid_type=data.get("liquid_type", "none"),
            active_buffs=data.get("active_buffs", []),
            mount=data.get("mount"),
            grappling=data.get("grappling", False),
            selected_hotbar_slot=data.get("selected_hotbar_slot", 0),
        )


@dataclass
class Observation:
    sequence: int = 0
    tick: int = 0
    timestamp_ms: int = 0
    player: PlayerState = field(default_factory=PlayerState)
    world: dict[str, Any] = field(default_factory=dict)
    spatial_window: dict[str, Any] = field(default_factory=dict)
    entities: dict[str, Any] = field(default_factory=dict)
    inventory: dict[str, Any] = field(default_factory=dict)
    ui_state: dict[str, Any] = field(default_factory=dict)
    raw_payload: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_payload(cls, data: dict[str, Any]) -> Observation:
        return cls(
            sequence=data.get("sequence", 0),
            tick=data.get("tick", 0),
            timestamp_ms=data.get("timestamp_ms", 0),
            player=PlayerState.from_payload(data.get("player", {})),
            world=data.get("world", {}),
            spatial_window=data.get("spatial_window", {}),
            entities=data.get("entities", {}),
            inventory=data.get("inventory", {}),
            ui_state=data.get("ui_state", {}),
            raw_payload=data,
        )

    def has_significant_event(self) -> bool:
        """Check if there are signs of significant events in this observation."""
        hp_ratio = self.player.health_current / max(self.player.health_max, 1)
        if hp_ratio < 0.5:
            return True
        if self.player.is_in_liquid and self.player.breath_current < 50:
            return True
        hostile_nearby = any(
            n.get("is_hostile") and n.get("distance_to_player", 999) < 200
            for n in self.entities.get("npcs", [])
        )
        if hostile_nearby:
            return True
        return bool(self.world.get("bosses_alive")) or bool(self.world.get("active_events"))


# ── Agent Observation ──────────────────────────────────────


@dataclass
class AgentState:
    position: Vec2 = field(default_factory=Vec2)
    velocity: Vec2 = field(default_factory=Vec2)
    tile_position: tuple[int, int] = (0, 0)
    direction: str = "right"

    @classmethod
    def from_payload(cls, data: dict[str, Any]) -> AgentState:
        pos = data.get("position", {})
        vel = data.get("velocity", {})
        tp = data.get("tile_position", {})
        return cls(
            position=Vec2(x=pos.get("x", 0), y=pos.get("y", 0)),
            velocity=Vec2(x=vel.get("x", 0), y=vel.get("y", 0)),
            tile_position=(tp.get("x", 0), tp.get("y", 0)),
            direction=data.get("direction", "right"),
        )


@dataclass
class AgentObservation:
    agent_id: str = ""
    sequence: int = 0
    tick: int = 0
    timestamp_ms: int = 0
    agent: AgentState = field(default_factory=AgentState)
    world: dict[str, Any] = field(default_factory=dict)
    spatial_window: dict[str, Any] = field(default_factory=dict)
    entities: dict[str, Any] = field(default_factory=dict)
    raw_payload: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_payload(cls, data: dict[str, Any]) -> AgentObservation:
        return cls(
            agent_id=data.get("agent_id", ""),
            sequence=data.get("sequence", 0),
            tick=data.get("tick", 0),
            timestamp_ms=data.get("timestamp_ms", 0),
            agent=AgentState.from_payload(data.get("agent", {})),
            world=data.get("world", {}),
            spatial_window=data.get("spatial_window", {}),
            entities=data.get("entities", {}),
            raw_payload=data,
        )


# ── Action ────────────────────────────────────────────────


@dataclass
class Action:
    action_id: str = field(default_factory=lambda: str(uuid.uuid4()))
    action_type: str = ""
    params: dict[str, Any] = field(default_factory=dict)
    priority: int = 1
    cancel_on: list[str] = field(default_factory=list)
    timeout_ms: int = 30000
    interruptible: bool = True

    def to_payload(self) -> dict[str, Any]:
        return {
            "action_id": self.action_id,
            "action_type": self.action_type,
            "params": self.params,
            "priority": self.priority,
            "cancel_on": self.cancel_on,
            "timeout_ms": self.timeout_ms,
            "interruptible": self.interruptible,
        }


@dataclass
class ActionResult:
    action_id: str = ""
    status: str = ""  # completed, failed, cancelled, timeout, rejected
    success: bool = False
    output: dict[str, Any] | None = None
    error_code: str | None = None
    error_message: str | None = None
    tick_completed: int = 0
    duration_ms: int = 0

    @classmethod
    def from_payload(cls, data: dict[str, Any]) -> ActionResult:
        err = data.get("error", {})
        result = data.get("result", {})
        return cls(
            action_id=data.get("action_id", ""),
            status=data.get("status", ""),
            success=result.get("success", data.get("status") == "completed"),
            output=result.get("output") if isinstance(result, dict) else None,
            error_code=err.get("code") if isinstance(err, dict) else None,
            error_message=err.get("message") if isinstance(err, dict) else None,
            tick_completed=data.get("tick_completed", 0),
            duration_ms=data.get("duration_ms", 0),
        )


# ── Event ─────────────────────────────────────────────────


@dataclass
class GameEvent:
    event_type: str = ""
    tick: int = 0
    timestamp_ms: int = 0
    importance: int = 5
    position: Vec2 | None = None
    data: dict[str, Any] = field(default_factory=dict)

    @classmethod
    def from_payload(cls, data: dict[str, Any]) -> GameEvent:
        pos = data.get("position")
        return cls(
            event_type=data.get("event_type", ""),
            tick=data.get("tick", 0),
            timestamp_ms=data.get("timestamp_ms", 0),
            importance=data.get("importance", 5),
            position=Vec2(x=pos["x"], y=pos["y"]) if pos else None,
            data=data.get("data", {}),
        )
