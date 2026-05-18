"""Tool registry — NPC agent tools backed by atomic action definitions."""

from __future__ import annotations

import time
from typing import Any, Protocol

from terraclaw_runtime._debug import dprint
from terraclaw_runtime.agent.action_registry import ActionRegistry
from terraclaw_runtime.bridge.client import BridgeClient

ToolResult = dict[str, Any]


class ToolHandler(Protocol):
    async def __call__(self, arguments: dict[str, Any]) -> ToolResult: ...


class ToolRegistry:
    """Registry of tools the LLM can call to control an NPC agent.

    Tools are defined by an ActionRegistry (loaded from YAML). This class
    wraps them with async dispatch that sends actions via the bridge.
    """

    def __init__(self, actions: ActionRegistry, bridge: BridgeClient, agent_id: str):
        self._actions = actions
        self._bridge = bridge
        self._agent_id = agent_id
        self._handlers: dict[str, ToolHandler] = {}

        # Register a handler for every enabled action
        for name in self._actions.names:
            action_def = self._actions.get(name)
            if action_def is None:
                continue
            self._handlers[name] = self._make_handler(action_def.bridge_action)

    def get_definitions(self) -> list[dict[str, Any]]:
        """Return LLM tool definitions for all enabled actions."""
        return self._actions.get_definitions()

    async def dispatch(self, name: str, arguments: dict[str, Any]) -> ToolResult:
        handler = self._handlers.get(name)
        if handler is None:
            return {"error": f"Unknown or disabled tool: {name}"}
        try:
            return await handler(arguments)
        except Exception as e:
            return {"error": str(e), "tool": name}

    async def redispatch_enabled(self) -> None:
        """Rebuild handlers to match current enabled/disabled state.

        Call this after changing ActionRegistry enable/disable at runtime.
        """
        self._handlers.clear()
        for name in self._actions.names:
            action_def = self._actions.get(name)
            if action_def is None or not action_def.enabled:
                continue
            self._handlers[name] = self._make_handler(action_def.bridge_action)

    def _make_handler(self, bridge_action: str):
        """Factory: returns an async handler that sends a bridge action."""
        async def handler(arguments: dict[str, Any]) -> ToolResult:
            timeout_ms = arguments.pop("timeout_ms", 30000)
            t0 = time.monotonic()
            action_id = await self._bridge.send_agent_action(
                self._agent_id,
                bridge_action,
                arguments,
                timeout_ms=timeout_ms,
            )
            dprint("[TOOL]", f"sending {bridge_action}({arguments}) id={action_id[:12]}...")
            result = await self._bridge.wait_for_agent_action_result(
                action_id, self._agent_id, timeout=timeout_ms / 1000 + 5
            )
            elapsed = time.monotonic() - t0
            status = result.get("status", "unknown")
            dprint("[TOOL]", f"{bridge_action} → {status} in {elapsed:.1f}s")
            return {
                "action_id": action_id,
                "action_type": bridge_action,
                "status": status,
                "result": result.get("result"),
                "error": result.get("error"),
            }

        return handler
