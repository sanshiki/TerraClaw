"""Standalone TerraClaw dashboard.

This process does not call an LLM. It connects to the tModLoader bridge,
listens for C# LLM debug events, and forwards them to the existing browser
dashboard for inspection.
"""

from __future__ import annotations

import asyncio
import os
import signal

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.config import RuntimeConfig
from terraclaw_runtime.dashboard import LiveDashboard


async def main() -> None:
    config_path = os.getenv("TERRACLAW_CONFIG", "config/config.yaml")
    config = RuntimeConfig.from_yaml(config_path)
    config.apply_env_overrides()

    dashboard_port = int(os.getenv("DASHBOARD_PORT", "9090"))
    dashboard = LiveDashboard()
    bridge = BridgeClient(config.bridge)
    stop = asyncio.Event()

    loop = asyncio.get_running_loop()
    for sig in (signal.SIGINT, signal.SIGTERM):
        try:
            loop.add_signal_handler(sig, stop.set)
        except NotImplementedError:
            pass

    dashboard_task = asyncio.create_task(dashboard.serve(port=dashboard_port))

    try:
        await bridge.connect()
        print(f"[DASHBOARD] Connected to bridge: {config.bridge.url}")
        listener_task = asyncio.create_task(_listen_bridge(bridge, dashboard, stop))
        instruction_task = asyncio.create_task(_listen_instructions(bridge, dashboard, stop))
        await stop.wait()
    finally:
        for task in (locals().get("listener_task"), locals().get("instruction_task"), dashboard_task):
            if task and not task.done():
                task.cancel()
                try:
                    await task
                except asyncio.CancelledError:
                    pass
        await bridge.disconnect()


async def _listen_bridge(bridge: BridgeClient, dashboard: LiveDashboard, stop: asyncio.Event) -> None:
    while not stop.is_set():
        msg = await bridge.receive(timeout=0.5)
        if msg is None:
            continue

        if msg.type == "llm.debug.request":
            payload = dict(msg.payload)
            payload["turn"] = dashboard.next_turn()
            dashboard.publish("llm_request", payload)
        elif msg.type == "llm.debug.result":
            dashboard.publish("llm_result", dict(msg.payload))


async def _listen_instructions(bridge: BridgeClient, dashboard: LiveDashboard, stop: asyncio.Event) -> None:
    while not stop.is_set():
        text = await bridge.receive_instruction(timeout=0.5)
        if text:
            dashboard.publish("player_instruction", {"text": text})


if __name__ == "__main__":
    asyncio.run(main())
