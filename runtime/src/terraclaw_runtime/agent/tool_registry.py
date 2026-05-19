"""Tool registry — NPC agent tools backed by atomic action definitions."""

from __future__ import annotations

from typing import Any, Protocol

from terraclaw_runtime._debug import dprint
from terraclaw_runtime.agent.action_registry import ActionRegistry, is_internal
from terraclaw_runtime.bridge.client import BridgeClient

ToolResult = dict[str, Any]


class ToolHandler(Protocol):
    async def __call__(self, arguments: dict[str, Any]) -> ToolResult: ...


class ToolRegistry:
    """Registry of tools the LLM can call to control an NPC agent.

    Tools are defined by an ActionRegistry (loaded from YAML). This class
    wraps them with async dispatch that sends actions via the bridge.
    Bridge actions return immediately (non-blocking); internal actions
    like status polling and cancel are handled locally.
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

            if is_internal(name):
                self._handlers[name] = self._make_internal_handler(name)
            else:
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

            if is_internal(name):
                self._handlers[name] = self._make_internal_handler(name)
            else:
                self._handlers[name] = self._make_handler(action_def.bridge_action)

    # ── Bridge-action handler factory (non-blocking) ────────────

    def _make_handler(self, bridge_action: str):
        """Factory: returns an async handler that sends a bridge action and returns immediately."""
        async def handler(arguments: dict[str, Any]) -> ToolResult:
            timeout_ms = arguments.pop("timeout_ms", 30000)
            action_id = await self._bridge.send_agent_action(
                self._agent_id,
                bridge_action,
                arguments,
                timeout_ms=timeout_ms,
            )
            dprint("[TOOL]", f"started {bridge_action}({arguments}) id={action_id[:12]}...")
            return {
                "status": "started",
                "action_id": action_id,
                "action_type": bridge_action,
            }

        return handler

    # ── Internal handlers (local, no bridge dispatch) ──────────

    def _make_internal_handler(self, name: str):
        if name == "get_action_status":
            return self._handle_get_status
        elif name == "cancel_action":
            return self._handle_cancel_action
        return lambda args: {"error": f"Unknown internal action: {name}"}

    async def _handle_get_status(self, arguments: dict[str, Any]) -> ToolResult:
        action_id = arguments.get("action_id", "")
        if not action_id:
            return {"error": "Missing action_id parameter"}

        # Check if the action result is already available
        result = await self._bridge.poll_action_result(action_id)
        if result is not None:
            return {
                "action_id": action_id,
                "status": result.get("status", "completed"),
                "result": result.get("result"),
                "error": result.get("error"),
            }

        # Distinguish "pending/running" from "unknown" — return an error for unknown IDs
        # so the LLM sees a clear signal to stop polling stale action IDs.
        if not await self._bridge.has_pending_action(action_id):
            return {
                "action_id": action_id,
                "error": f"Action '{action_id}' is not tracked — it was already completed or cancelled. Do not check this action_id again.",
            }
        return {"action_id": action_id, "status": "running"}

    async def _handle_cancel_action(self, arguments: dict[str, Any]) -> ToolResult:
        action_id = arguments.get("action_id", "")
        # If the action's future is already resolved (completed/cancelled),
        # return a distinct status so the LLM knows there's nothing to cancel.
        if action_id:
            existing = await self._bridge.poll_action_result(action_id)
            if existing is not None:
                return {"status": "already_cancelled", "action_id": action_id, "result": existing}
        await self._bridge.cancel_action(self._agent_id, action_id or None)
        return {"status": "cancel_sent", "action_id": action_id}
