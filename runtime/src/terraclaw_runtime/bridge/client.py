"""Async WebSocket client for the TerraClaw bridge."""

from __future__ import annotations

import asyncio
import json
import time
from typing import Any, AsyncIterator

import structlog
import websockets
from websockets.asyncio.client import ClientConnection

from terraclaw_runtime._debug import dprint
from terraclaw_runtime.bridge.message import (
    Action,
    ActionResult,
    AgentObservation,
    GameEvent,
    MessageEnvelope,
)
from terraclaw_runtime.config import BridgeConfig

logger = structlog.get_logger()


class BridgeClient:
    """Async WebSocket client that connects to the tModLoader bridge mod."""

    def __init__(self, config: BridgeConfig):
        self._config = config
        self._ws: ClientConnection | None = None
        self._session_id: str | None = None
        self.agent_id: str | None = None  # set after register_agent
        self._connected = False
        self._rx_queue: asyncio.Queue[MessageEnvelope] = asyncio.Queue(maxsize=500)
        self._tx_queue: asyncio.Queue[str] = asyncio.Queue(maxsize=200)
        self._recv_task: asyncio.Task[None] | None = None
        self._send_task: asyncio.Task[None] | None = None
        self._last_send_time = 0.0
        self._pending: dict[str, asyncio.Future] = {}
        self._pending_lock: asyncio.Lock = asyncio.Lock()
        self._resolved_results: dict[str, dict] = {}  # action_id → cached result for completed actions
        self._instructions_queue: asyncio.Queue[str] = asyncio.Queue()

    @property
    def connected(self) -> bool:
        return self._connected and self._ws is not None

    @property
    def session_id(self) -> str | None:
        return self._session_id

    async def connect(self) -> None:
        """Connect to the bridge and perform handshake."""
        delay = self._config.reconnect_base_delay_s

        while True:
            try:
                logger.info("connecting", url=self._config.url)
                self._ws = await websockets.connect(
                    self._config.url,
                    max_size=1_048_576,  # 1 MB, matching BridgeConfig.MaxMessageSizeBytes
                    ping_interval=self._config.heartbeat_interval_s,
                )

                # Handshake
                handshake = {
                    "id": str(__import__("uuid").uuid4()),
                    "type": "handshake",
                    "timestamp": int(time.time() * 1000),
                    "payload": {
                        "version": "1.0",
                        "auth": self._config.shared_secret,
                        "client_name": "terraclaw-runtime",
                        "capabilities": ["observations", "actions", "events", "skills"],
                    },
                }
                await self._ws.send(json.dumps(handshake))

                # Wait for handshake response
                raw = await asyncio.wait_for(self._ws.recv(), timeout=5.0)
                response = json.loads(raw)

                if response.get("type") != "handshake_ok":
                    error_msg = response.get("payload", {}).get("message", "Handshake failed")
                    logger.error("handshake_failed", error=error_msg)
                    raise ConnectionError(error_msg)

                self._session_id = response["payload"]["session_id"]
                self._connected = True

                # Start send/receive loops
                self._recv_task = asyncio.create_task(self._recv_loop())
                self._send_task = asyncio.create_task(self._send_loop())

                logger.info("connected", session_id=self._session_id)
                return

            except (OSError, ConnectionError, asyncio.TimeoutError) as e:
                logger.warning("connection_failed", error=str(e), retry_delay_s=delay)
                self._connected = False
                await asyncio.sleep(delay)
                delay = min(delay * 2, self._config.reconnect_max_delay_s)

    async def disconnect(self) -> None:
        """Gracefully close the connection."""
        self._connected = False
        for task in [self._recv_task, self._send_task]:
            if task and not task.done():
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
        if self._ws:
            await self._ws.close()
            self._ws = None
        logger.info("disconnected")

    async def _recv_loop(self) -> None:
        """Continuously receive messages from the bridge."""
        assert self._ws is not None
        while self._connected:
            try:
                raw = await asyncio.wait_for(self._ws.recv(), timeout=1.0)
                envelope = json.loads(raw)
                msg = MessageEnvelope(
                    id=envelope.get("id", ""),
                    type=envelope.get("type", ""),
                    timestamp=envelope.get("timestamp", 0),
                    session_id=envelope.get("session_id"),
                    in_reply_to=envelope.get("in_reply_to"),
                    payload=envelope.get("payload", {}),
                )

                # Route agent.action.result directly to the waiting future
                if msg.type == "agent.action.result":
                    action_id = msg.payload.get("action_id", "")
                    if action_id:
                        async with self._pending_lock:
                            future = self._pending.get(action_id)
                            if future is not None and not future.done():
                                future.set_result(msg.payload)
                                self._pending.pop(action_id, None)
                                self._resolved_results[action_id] = msg.payload
                                continue

                # Route player.chat instructions to the instructions queue
                if msg.type == "player.chat":
                    text = msg.payload.get("text", "")
                    if text:
                        await self._instructions_queue.put(text)
                    continue

                # Everything else goes to the shared queue
                await self._rx_queue.put(msg)
            except asyncio.TimeoutError:
                continue
            except websockets.ConnectionClosed:
                logger.warning("connection_closed")
                self._connected = False
                break
            except Exception as e:
                logger.error("recv_error", error=str(e))

    async def _send_loop(self) -> None:
        """Continuously send queued messages to the bridge."""
        assert self._ws is not None
        while self._connected:
            try:
                message = await asyncio.wait_for(self._tx_queue.get(), timeout=0.5)
                await self._ws.send(message)
                self._last_send_time = time.time()
            except asyncio.TimeoutError:
                continue
            except websockets.ConnectionClosed:
                logger.warning("connection_closed_during_send")
                self._connected = False
                break
            except Exception as e:
                logger.error("send_error", error=str(e))

    def _enqueue(self, msg_type: str, payload: dict) -> str:
        """Build a message envelope and queue it for sending. Returns the message ID."""
        msg_id = str(__import__("uuid").uuid4())
        envelope = json.dumps({
            "id": msg_id,
            "type": msg_type,
            "timestamp": int(time.time() * 1000),
            "session_id": self._session_id,
            "payload": payload,
        })
        self._tx_queue.put_nowait(envelope)
        return msg_id

    # ── Public API ──────────────────────────────────────────

    async def subscribe_events(self, event_types: list[str]) -> None:
        self._enqueue("event.subscribe", {"event_types": event_types})

    async def unsubscribe_events(self, event_types: list[str]) -> None:
        self._enqueue("event.unsubscribe", {"event_types": event_types})

    async def configure_observations(self, spatial_radius: int = 30, send_rate_hz: int = 10) -> None:
        self._enqueue("observation.configure", {
            "spatial_radius": spatial_radius,
            "send_rate_hz": send_rate_hz,
        })

    async def request_observation(self) -> None:
        self._enqueue("observation.request", {})

    async def register_agent(self, position: tuple[float, float] | None = None, agent_name: str = "terraclaw") -> dict:
        """Register an NPC agent. Returns the registration response."""
        payload: dict = {"agent_name": agent_name}
        if position:
            payload["position"] = {"x": position[0], "y": position[1]}
        self._enqueue("agent.register", payload)
        dprint("[BRIDGE]", "Waiting for agent.registered...")
        deadline = time.monotonic() + 5.0
        while time.monotonic() < deadline:
            msg = await self.receive(timeout=1.0)
            if msg and msg.type == "agent.registered":
                self.agent_id = msg.payload.get("agent_id")
                print(f"[BRIDGE] Agent registered: id={self.agent_id[:8] if self.agent_id else '?'}... "
                      f"pos=({msg.payload.get('position', {}).get('x', '?')}, {msg.payload.get('position', {}).get('y', '?')})")
                return msg.payload
        print("[BRIDGE] Agent registration timed out!")
        return {"error": "Agent registration timed out"}

    async def send_agent_action(self, agent_id: str, action_type: str, params: dict, timeout_ms: int = 30000) -> str:
        """Send an action for the NPC agent to execute. Returns the action ID."""
        action_id = str(__import__("uuid").uuid4())
        payload = {
            "agent_id": agent_id,
            "action_type": action_type,
            "params": params,
            "timeout_ms": timeout_ms,
            "action_id": action_id,  # must match the returned ID so C# sends it back
        }

        # Register a future so _recv_loop can route the result back
        loop = asyncio.get_running_loop()
        future = loop.create_future()
        async with self._pending_lock:
            self._pending[action_id] = future

        self._tx_queue.put_nowait(json.dumps({
            "id": action_id,
            "type": "agent.action",
            "timestamp": int(time.time() * 1000),
            "session_id": self._session_id,
            "payload": payload,
        }))
        return action_id

    async def wait_for_agent_action_result(self, action_id: str, agent_id: str, timeout: float = 60.0) -> dict:
        """Wait for an agent.action.result matching the given action_id (future-based, no polling)."""
        async with self._pending_lock:
            future = self._pending.get(action_id)
        if future is None:
            async with self._pending_lock:
                result = self._resolved_results.get(action_id)
            if result:
                return result
            return {"status": "unknown", "action_id": action_id}
        try:
            result = await asyncio.wait_for(future, timeout=timeout)
            async with self._pending_lock:
                self._resolved_results[action_id] = result
            return result
        except asyncio.TimeoutError:
            return {"status": "timeout", "action_id": action_id, "error": "Timed out waiting for agent action result"}
        finally:
            async with self._pending_lock:
                self._pending.pop(action_id, None)

    async def has_pending_action(self, action_id: str) -> bool:
        """Check if an action_id is known (still pending or already resolved)."""
        async with self._pending_lock:
            return action_id in self._pending or action_id in self._resolved_results

    async def poll_action_result(self, action_id: str) -> dict | None:
        """Non-blocking: check if a result is available. Returns None if still running.

        Checks both pending futures and the resolved-results cache so that
        completed or cancelled actions always return their result on subsequent polls.
        """
        async with self._pending_lock:
            future = self._pending.get(action_id)
            if future is not None:
                if future.done():
                    try:
                        return future.result()
                    except Exception as e:
                        return {"status": "error", "error": str(e)}
                return None
            # Check resolved cache — action completed and future was cleaned up
            result = self._resolved_results.get(action_id)
            return result

    async def cancel_action(self, agent_id: str, action_id: str | None = None) -> None:
        """Send a cancel/interrupt message for the agent's running action.

        Also resolves the pending future for the cancelled action so that
        subsequent poll_action_result calls return "cancelled" instead of None.
        """
        payload: dict[str, Any] = {"agent_id": agent_id}
        if action_id:
            payload["action_id"] = action_id
        self._enqueue("agent.action.cancel", payload)

        # Resolve the pending future immediately so polls don't show stale "running"
        if action_id:
            async with self._pending_lock:
                future = self._pending.pop(action_id, None)
                self._resolved_results[action_id] = {"status": "cancelled", "action_id": action_id}
            if future is not None and not future.done():
                future.set_result({"status": "cancelled", "action_id": action_id})

    async def unregister_agent(self, agent_id: str) -> None:
        """Unregister / despawn an NPC agent."""
        self._enqueue("agent.unregister", {"agent_id": agent_id})

    async def receive_instruction(self, timeout: float = 0.1) -> str | None:
        """Receive the next pending player.chat instruction (non-blocking)."""
        try:
            return await asyncio.wait_for(self._instructions_queue.get(), timeout=timeout)
        except asyncio.TimeoutError:
            return None

    async def receive_agent_observation(self, timeout: float = 1.0) -> AgentObservation | None:
        """Receive the next agent.observation message."""
        while True:
            msg = await self.receive(timeout=timeout)
            if msg is None:
                return None
            if msg.type == "agent.observation":
                return AgentObservation.from_payload(msg.payload)

    async def send_ping(self) -> None:
        self._enqueue("ping", {})

    async def receive(self, timeout: float = 1.0) -> MessageEnvelope | None:
        """Receive the next message from the bridge."""
        try:
            return await asyncio.wait_for(self._rx_queue.get(), timeout=timeout)
        except asyncio.TimeoutError:
            return None

    async def receive_events(self, timeout: float = 0.1) -> list[GameEvent]:
        """Drain all pending observation.event messages."""
        events: list[GameEvent] = []
        while True:
            msg = await self.receive(timeout=timeout)
            if msg is None:
                break
            if msg.type == "observation.event":
                events.append(GameEvent.from_payload(msg.payload))
            elif msg.type == "action.result":
                # Put non-event messages back? For now, just collect events.
                pass
        return events
