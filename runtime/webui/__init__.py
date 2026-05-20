"""WebUI app factory — creates a FastAPI app bound to existing bridge/tools.

Can be used in two modes:
1. Standalone: `python -m webui.main` — creates its own bridge connection
2. Integrated: `python orchestrator.py --human` — shares orchestrator's bridge
"""

from __future__ import annotations

import asyncio
import logging
import time
from dataclasses import asdict
from typing import Any

import structlog
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse
from fastapi.staticfiles import StaticFiles

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import AgentObservation
from terraclaw_runtime.agent.tool_registry import ToolRegistry
from terraclaw_runtime.agent.prompt_builder import PromptBuilder

HERE = __import__("pathlib").Path(__file__).parent
STATIC = HERE / "static"

logger = structlog.get_logger()


def create_webui_app(
    bridge: BridgeClient,
    tool_registry: ToolRegistry,
    prompt_builder: PromptBuilder,
    agent_id: str,
) -> FastAPI:
    """Create a FastAPI web UI bound to existing bridge/tools.

    Returns an app with WebSocket endpoint at /ws and static files at /.
    The caller controls how to serve it (uvicorn, etc.).
    """
    app = FastAPI()
    app.mount("/static", StaticFiles(directory=str(STATIC)), name="static")

    # ── Shared state (per-app-instance, not module-global) ──

    state: dict[str, Any] = dict(
        bridge=bridge,
        tool_registry=tool_registry,
        prompt_builder=prompt_builder,
        agent_id=agent_id,
        tool_defs=tool_registry.get_definitions(),
        current_observation=None,
        player_instructions=[],
        pending_actions={},
        action_history=[],
        decision_queue=asyncio.Queue(),
        frontend_ws=None,
        _startup_done=False,
    )
    app.state.webui = state

    # ── Routes ───────────────────────────────────────────────

    @app.get("/")
    async def index():
        return FileResponse(str(STATIC / "index.html"))

    @app.get("/api/tools")
    async def get_tools():
        return state["tool_defs"]

    @app.get("/api/config")
    async def get_config():
        return {"agent_id": state["agent_id"]}

    # ── WebSocket ────────────────────────────────────────────

    @app.websocket("/ws")
    async def ws_endpoint(ws: WebSocket):
        await ws.accept()
        s = state
        s["frontend_ws"] = ws

        logger.info("webui_frontend_connected", agent_id=s["agent_id"])

        # Push initial state
        obs = s["current_observation"]
        if obs:
            await ws.send_json({"type": "observation", "data": _serialize_obs(obs)})
        await ws.send_json({"type": "tool_defs", "data": s["tool_defs"]})
        await ws.send_json({
            "type": "connected",
            "agent_id": s["agent_id"],
            "tool_count": len(s["tool_defs"]),
        })

        try:
            while True:
                data = await ws.receive_json()
                msg_type = data.get("type")

                if msg_type == "human_response":
                    tc = data.get("tool_calls", [])
                    logger.info("webui_human_response",
                                text=data.get("text", "")[:80],
                                tool_calls=[t.get("name") for t in tc])
                    await s["decision_queue"].put(data)
                elif msg_type == "instruction":
                    text = data.get("text", "").strip()
                    if text:
                        logger.info("webui_instruction", text=text)
                        s["player_instructions"].append(text)
                        s["action_history"].append({
                            "type": "instruction", "text": text, "timestamp": time.time(),
                        })
                        await _send(s, {"type": "instruction_accepted", "text": text})
                elif msg_type == "clear_instructions":
                    s["player_instructions"].clear()
                    await _send(s, {"type": "instructions_cleared"})
                elif msg_type == "get_context_preview":
                    preview = _build_context_preview(s)
                    await _send(s, {"type": "context_preview", "data": preview})
                elif msg_type == "ping":
                    await ws.send_json({"type": "pong"})
        except WebSocketDisconnect:
            s["frontend_ws"] = None
            logger.info("webui_frontend_disconnected")

    # ── Background tasks (started by app startup event) ──────

    @app.on_event("startup")
    async def startup():
        if state["_startup_done"]:
            return
        state["_startup_done"] = True
        asyncio.create_task(_observation_poller(state))
        asyncio.create_task(_pending_checker(state))
        asyncio.create_task(_snapshot_sender(state))
        asyncio.create_task(_decision_handler(state))

    return app


# ── Helpers ─────────────────────────────────────────────────


async def _send(s: dict, obj: dict):
    ws = s.get("frontend_ws")
    if ws:
        try:
            await ws.send_json(obj)
        except Exception:
            pass


def _serialize_obs(obs: AgentObservation) -> dict:
    d = {"agent_id": obs.agent_id, "sequence": obs.sequence, "tick": obs.tick,
         "timestamp_ms": obs.timestamp_ms}
    d["agent"] = asdict(obs.agent)
    d["world"] = obs.world
    d["entities"] = obs.entities
    d["spatial_window"] = obs.spatial_window
    return d


def _build_context_preview(s: dict) -> str:
    parts = []
    pb = s.get("prompt_builder")
    obs: AgentObservation | None = s.get("current_observation")

    # System prompt
    parts.append("=== SYSTEM PROMPT ===")
    parts.append(pb.get_system_prompt() if pb else "(not loaded)")

    # Observation
    if obs and pb:
        parts.append("\n=== CURRENT OBSERVATION ===")
        parts.append(pb._format_observation(obs))

    # Player instructions
    instrs = s.get("player_instructions", [])
    if instrs:
        parts.append("\n=== PLAYER INSTRUCTIONS ===")
        for instr in instrs:
            parts.append(f"- {instr}")

    # Pending actions
    pending = s.get("pending_actions", {})
    if pending:
        parts.append("\n=== PENDING ACTIONS ===")
        for aid, entry in pending.items():
            elapsed = time.time() - entry.get("start_time", time.time())
            parts.append(f"  {aid[:12]}... {entry.get('action_type', '?')} running for {elapsed:.0f}s")

    return "\n".join(parts)


async def _observation_poller(s: dict):
    bridge: BridgeClient = s["bridge"]
    while True:
        try:
            obs = await bridge.receive_agent_observation(timeout=1.0)
            if obs:
                s["current_observation"] = obs
                logger.debug("webui_observation", tick=obs.tick,
                             pos=(obs.agent.position.x, obs.agent.position.y))
                await _send(s, {"type": "observation", "data": _serialize_obs(obs)})
        except asyncio.CancelledError:
            break
        except Exception as e:
            logger.warning("webui_observation_poller_error", error=str(e)[:120])
            await asyncio.sleep(1.0)


async def _pending_checker(s: dict):
    bridge: BridgeClient = s["bridge"]
    while True:
        pending = s.get("pending_actions", {})
        if pending:
            for aid in list(pending.keys()):
                try:
                    result = await bridge.poll_action_result(aid)
                    if result is not None:
                        entry = pending.pop(aid)
                        entry["status"] = result.get("status", "completed")
                        logger.info("webui_action_resolved",
                                    action_id=aid[:12], status=entry["status"])
                        s["action_history"].append({
                            "type": "action_result", "action_id": aid,
                            "action_type": entry.get("action_type", "?"),
                            "data": result, "timestamp": time.time(),
                        })
                        await _send(s, {
                            "type": "action_resolved", "action_id": aid,
                            "result": result,
                        })
                except Exception as e:
                    logger.warning("webui_pending_check_error", error=str(e)[:80])
        await asyncio.sleep(0.5)


async def _snapshot_sender(s: dict):
    while True:
        await asyncio.sleep(1.0)
        try:
            pending = s.get("pending_actions", {})
            obs = s.get("current_observation")
            history = s.get("action_history", [])
            await _send(s, {
                "type": "state_snapshot",
                "pending": [
                    {"action_id": aid, **entry}
                    for aid, entry in pending.items()
                ],
                "agent_id": obs.agent_id if obs else None,
                "tick": obs.tick if obs else 0,
                "history_tail": history[-10:] if history else [],
            })
        except Exception:
            pass


async def _decision_handler(s: dict):
    bridge: BridgeClient = s["bridge"]
    tool_registry: ToolRegistry = s["tool_registry"]
    decision_queue: asyncio.Queue = s["decision_queue"]

    while True:
        decision = await decision_queue.get()
        text = decision.get("text", "")
        tool_calls = decision.get("tool_calls", [])

        logger.info("webui_decision",
                    text=text[:80] if text else "",
                    tool_count=len(tool_calls),
                    tools=[t.get("name") for t in tool_calls])

        s["action_history"].append({
            "type": "human_response", "text": text,
            "tool_calls": [tc.get("name") for tc in tool_calls],
            "timestamp": time.time(),
        })

        # Clear consumed instructions
        s["player_instructions"].clear()

        # Dispatch each tool
        for tc in tool_calls:
            name = tc.get("name", "")
            args = tc.get("arguments", {})
            try:
                result = await tool_registry.dispatch(name, args)
                if result.get("status") == "started" and result.get("action_id"):
                    aid = result["action_id"]
                    s["pending_actions"][aid] = {
                        "action_type": name, "action_id": aid,
                        "start_time": time.time(), "status": "started",
                    }
                    logger.info("webui_tool_started", tool=name, action_id=aid[:12])
                elif result.get("error"):
                    logger.warning("webui_tool_error", tool=name, error=str(result["error"])[:120])
                aid = result.get("action_id", "")
                s["action_history"].append({
                    "type": "tool_result", "tool": name,
                    "result": result, "timestamp": time.time(),
                    "action_id": aid,
                })
                await _send(s, {"type": "tool_result", "tool": name, "result": result})
            except Exception as e:
                logger.error("webui_tool_exception", tool=name, error=str(e)[:200])
                await _send(s, {"type": "tool_result", "tool": name, "result": {"error": str(e)}})

        # Refresh context preview
        preview = _build_context_preview(s)
        await _send(s, {"type": "context_preview", "data": preview})
