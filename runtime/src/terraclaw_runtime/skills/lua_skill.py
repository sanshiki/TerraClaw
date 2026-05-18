"""Lua skill — loads skill logic from sandboxed .lua scripts."""

from __future__ import annotations

import asyncio
import time
from pathlib import Path
from typing import TYPE_CHECKING, Any

from lupa import LuaRuntime

if TYPE_CHECKING:
    from terraclaw_runtime.bridge.client import BridgeClient

# Lua globals that are prohibited for safety
_RESTRICTED_GLOBALS = frozenset({
    "io", "os", "dofile", "loadfile", "require", "package",
    "debug", "rawget", "rawset", "rawequal",
})


class LuaSkill:
    """A skill defined by a Lua script.

    The .lua file must return a table with:
      name: str           — skill name
      description: str    — description for LLM
      execute: function   — fn(ctx, params) -> {success: bool, message: str, ...}
    """

    def __init__(self, filepath: str | Path):
        self.filepath = Path(filepath)
        self.name: str = self.filepath.stem
        self.description: str = ""
        self._execute_fn = None
        self._lua = self._make_runtime()
        self._load()

    def _make_runtime(self) -> LuaRuntime:
        """Create a sandboxed Lua runtime."""
        return LuaRuntime(
            unpack_returned_tuples=True,
            register_eval=False,
            attribute_filter=self._attribute_filter,
        )

    def _attribute_filter(self, obj, name) -> bool:
        """Prevent access to restricted globals."""
        if name in _RESTRICTED_GLOBALS:
            return False
        return True

    def _load(self) -> None:
        if not self.filepath.exists():
            return

        source = self.filepath.read_text(encoding="utf-8")
        # Execute the Lua source — it should return a table
        result = self._lua.execute(f"return (function()\n{source}\nend)()")

        if result is None:
            return

        self.name = getattr(result, "name", self.name) or self.name
        self.description = getattr(result, "description", "") or ""
        self._execute_fn = getattr(result, "execute", None)

    def check_preconditions(self, obs: Any, params: dict) -> tuple[bool, str]:
        """Check Lua-defined preconditions (default: pass)."""
        precond = getattr(self._lua.globals(), "check_preconditions", None)
        if precond is None:
            return True, ""
        try:
            ok, msg = precond(_to_lua_table(self._lua, obs), _to_lua_table(self._lua, params))
            return bool(ok), str(msg or "")
        except Exception as e:
            return False, f"Precondition error: {e}"

    async def execute(self, bridge: BridgeClient, obs: Any, params: dict) -> dict:
        """Execute the Lua skill in a thread pool."""
        if self._execute_fn is None:
            return {"success": False, "message": "No execute function defined"}

        loop = asyncio.get_running_loop()
        agent_id = getattr(bridge, "agent_id", "") or ""
        context = _SkillContext(loop, bridge, agent_id)

        def _run():
            ctx_table = _to_lua_table(self._lua, context)
            params_table = _to_lua_table(self._lua, params)
            result = self._execute_fn(ctx_table, params_table)
            return _from_lua_table(result) if result is not None else {}

        return await loop.run_in_executor(None, _run)


class _SkillContext:
    """Context passed to Lua skills for bridge interaction.

    Methods are synchronous (called from Lua in a thread pool) and bridge
    to the async event loop via run_coroutine_threadsafe.
    """

    def __init__(self, loop: asyncio.AbstractEventLoop, bridge: BridgeClient, agent_id: str):
        self._loop = loop
        self._bridge = bridge
        self._agent_id = agent_id

    def send_action(self, action_type: str, params: dict = None) -> dict:
        """Send an atomic action and wait for the result."""
        if params is None:
            params = {}

        future = asyncio.run_coroutine_threadsafe(
            self._bridge.send_agent_action(
                agent_id=self._agent_id,
                action_type=action_type,
                params=params,
                timeout_ms=30000,
            ),
            self._loop,
        )
        return {"action_id": future.result(timeout=35)}

    def wait_for_action_result(self, action_id: str, timeout: float = 35.0) -> dict:
        """Wait for a specific action result."""
        future = asyncio.run_coroutine_threadsafe(
            self._bridge.wait_for_agent_action_result(
                action_id=action_id,
                agent_id=self._agent_id,
                timeout=timeout,
            ),
            self._loop,
        )
        return future.result(timeout=timeout + 5)

    def wait(self, duration_ms: int) -> None:
        """Sleep for a duration."""
        future = asyncio.run_coroutine_threadsafe(
            asyncio.sleep(duration_ms / 1000.0),
            self._loop,
        )
        future.result(timeout=duration_ms / 1000.0 + 2)

    @property
    def tick_count(self) -> int:
        """Return approximate tick count (not real-time accurate from thread)."""
        return int(time.monotonic() * 60)


def _to_lua_table(lua: LuaRuntime, obj: Any) -> Any:
    """Recursively convert a Python dict/list to a Lua table."""
    if obj is None:
        return None
    if isinstance(obj, (str, int, float, bool)):
        return obj
    if isinstance(obj, dict):
        t = lua.table_from({})
        for k, v in obj.items():
            t[_to_lua_table(lua, k)] = _to_lua_table(lua, v)
        return t
    if isinstance(obj, (list, tuple)):
        t = lua.table_from([])
        for i, v in enumerate(obj, 1):
            t[i] = _to_lua_table(lua, v)
        return t
    return str(obj)


def _from_lua_table(obj: Any) -> Any:
    """Recursively convert a Lua result to plain Python types."""
    if obj is None:
        return None
    if isinstance(obj, (str, int, float, bool)):
        return obj
    # Check for lupa table types
    typename = type(obj).__name__
    if "table" in typename:
        # Try as dict first
        py_dict: dict = {}
        py_list: list = []
        is_list = True
        for k, v in obj.items():
            if isinstance(k, int) and k >= 1:
                while len(py_list) < k:
                    py_list.append(None)
                py_list[k - 1] = _from_lua_table(v)
            else:
                is_list = False
                py_dict[_from_lua_table(k)] = _from_lua_table(v)
        if is_list and py_list:
            return [x for x in py_list if x is not None]
        return py_dict
    return str(obj)
