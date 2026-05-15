"""Tool registry — NPC agent tools that send agent.action messages."""

from __future__ import annotations

import time
from typing import Any, Protocol

from terraclaw_runtime._debug import dprint
from terraclaw_runtime.bridge.client import BridgeClient

ToolResult = dict[str, Any]


class ToolHandler(Protocol):
    async def __call__(self, arguments: dict[str, Any]) -> ToolResult: ...


class ToolRegistry:
    """Registry of tools the LLM can call to control an NPC agent."""

    def __init__(self, bridge: BridgeClient, agent_id: str):
        self._bridge = bridge
        self._agent_id = agent_id
        self._handlers: dict[str, ToolHandler] = {}
        self._definitions: list[dict[str, Any]] = []
        self._register_all()

    def register(self, name: str, description: str, parameters: dict[str, Any], handler: ToolHandler) -> None:
        self._handlers[name] = handler
        self._definitions.append({
            "type": "function",
            "function": {
                "name": name,
                "description": description,
                "parameters": parameters,
            },
        })

    def get_definitions(self) -> list[dict[str, Any]]:
        return self._definitions

    async def dispatch(self, name: str, arguments: dict[str, Any]) -> ToolResult:
        handler = self._handlers.get(name)
        if handler is None:
            return {"error": f"Unknown tool: {name}"}
        try:
            return await handler(arguments)
        except Exception as e:
            return {"error": str(e), "tool": name}

    def _register_all(self) -> None:
        # ── Movement ──
        self.register(
            "move_to", "Move the NPC toward world coordinates. The NPC can fly through walls.",
            {
                "type": "object",
                "properties": {
                    "x": {"type": "number", "description": "Target world X coordinate (pixels)"},
                    "y": {"type": "number", "description": "Target world Y coordinate (pixels)"},
                    "speed": {"type": "number", "description": "Movement speed in pixels/tick", "default": 4.0},
                    "arrival_radius": {"type": "number", "description": "Distance at which arrival is confirmed", "default": 16.0},
                },
                "required": ["x", "y"],
            },
            self._agent_action("move_to"),
        )

        self.register(
            "teleport", "Instantly teleport the NPC to world coordinates.",
            {
                "type": "object",
                "properties": {
                    "x": {"type": "number"},
                    "y": {"type": "number"},
                },
                "required": ["x", "y"],
            },
            self._agent_action("teleport"),
        )

        # ── World manipulation ──
        self.register(
            "break_tile", "Break/destroy a tile at the specified tile coordinates.",
            {
                "type": "object",
                "properties": {
                    "tx": {"type": "integer", "description": "Tile X coordinate"},
                    "ty": {"type": "integer", "description": "Tile Y coordinate"},
                },
                "required": ["tx", "ty"],
            },
            self._agent_action("break_tile"),
        )

        self.register(
            "break_wall", "Break/destroy a wall at the specified tile coordinates.",
            {
                "type": "object",
                "properties": {
                    "tx": {"type": "integer"},
                    "ty": {"type": "integer"},
                },
                "required": ["tx", "ty"],
            },
            self._agent_action("break_wall"),
        )

        self.register(
            "place_tile", "Place a tile at the specified tile coordinates.",
            {
                "type": "object",
                "properties": {
                    "tx": {"type": "integer", "description": "Tile X coordinate"},
                    "ty": {"type": "integer", "description": "Tile Y coordinate"},
                    "tile_type": {"type": "integer", "description": "Tile type ID (e.g. 0=dirt, 1=stone)", "default": 0},
                    "style": {"type": "integer", "default": 0},
                },
                "required": ["tx", "ty"],
            },
            self._agent_action("place_tile"),
        )

        self.register(
            "place_wall", "Place a wall at the specified tile coordinates.",
            {
                "type": "object",
                "properties": {
                    "tx": {"type": "integer"},
                    "ty": {"type": "integer"},
                    "wall_type": {"type": "integer", "description": "Wall type ID", "default": 0},
                },
                "required": ["tx", "ty"],
            },
            self._agent_action("place_wall"),
        )

        # ── Utility ──
        self.register(
            "wait", "Wait / idle for a duration.",
            {
                "type": "object",
                "properties": {
                    "duration_ms": {"type": "integer", "minimum": 0, "maximum": 60000, "default": 1000},
                },
            },
            self._agent_action("wait"),
        )

    def _agent_action(self, action_type: str):
        async def handler(arguments: dict[str, Any]) -> ToolResult:
            timeout_ms = arguments.pop("timeout_ms", 30000)
            t0 = time.monotonic()
            action_id = await self._bridge.send_agent_action(
                self._agent_id,
                action_type,
                arguments,
                timeout_ms=timeout_ms,
            )
            dprint("[TOOL]",f"sending {action_type}({arguments}) id={action_id[:12]}...")
            # Wait for the actual result from the bridge
            result = await self._bridge.wait_for_agent_action_result(
                action_id, self._agent_id, timeout=timeout_ms / 1000 + 5
            )
            elapsed = time.monotonic() - t0
            status = result.get("status", "unknown")
            dprint("[TOOL]",f"{action_type} → {status} in {elapsed:.1f}s")
            return {
                "action_id": action_id,
                "action_type": action_type,
                "status": status,
                "result": result.get("result"),
                "error": result.get("error"),
            }

        return handler
