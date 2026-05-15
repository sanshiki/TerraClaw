#!/usr/bin/env python3
"""Simple debug client to test the TerraClaw bridge mod without the full runtime."""

from __future__ import annotations

import asyncio
import json
import sys

import websockets


async def main():
    if len(sys.argv) < 2:
        print("Usage: python debug_client.py <command> [args...]")
        print("Commands: ping, move, jump, observe, interact")
        return

    url = "ws://127.0.0.1:9777/bridge"
    secret = "terraclaw-dev"

    async with websockets.connect(url) as ws:
        # Handshake
        handshake = {
            "id": "debug-1",
            "type": "handshake",
            "timestamp": 0,
            "payload": {
                "version": "1.0",
                "auth": secret,
                "client_name": "debug_client",
                "capabilities": ["observations", "actions"],
            },
        }
        await ws.send(json.dumps(handshake))
        response = json.loads(await ws.recv())
        print(f"Handshake: {response['type']}")

        if response["type"] != "handshake_ok":
            print(f"Error: {response.get('payload', {}).get('message', 'Unknown error')}")
            return

        session_id = response["payload"]["session_id"]
        print(f"Connected! Session: {session_id[:8]}...")

        # Subscribe to events
        await ws.send(json.dumps({
            "id": "sub-1", "type": "event.subscribe", "session_id": session_id,
            "timestamp": 0,
            "payload": {"event_types": ["player.*", "entity.*"]},
        }))

        cmd = sys.argv[1]

        if cmd == "ping":
            await ws.send(json.dumps({
                "id": "ping-1", "type": "ping", "session_id": session_id, "timestamp": 0, "payload": {},
            }))
            resp = json.loads(await ws.recv())
            print(f"Pong: {resp['type']}")

        elif cmd == "move":
            direction = sys.argv[2] if len(sys.argv) > 2 else "right"
            await ws.send(json.dumps({
                "id": "move-1", "type": "action.execute", "session_id": session_id, "timestamp": 0,
                "payload": {
                    "action_id": "move-test",
                    "action_type": "move",
                    "params": {"direction": direction, "duration_ms": 2000},
                    "priority": 1,
                    "timeout_ms": 5000,
                },
            }))
            print(f"Sent move {direction}")

        elif cmd == "jump":
            await ws.send(json.dumps({
                "id": "jump-1", "type": "action.execute", "session_id": session_id, "timestamp": 0,
                "payload": {
                    "action_id": "jump-test",
                    "action_type": "jump",
                    "params": {"duration_ms": 300},
                    "priority": 1,
                    "timeout_ms": 2000,
                },
            }))
            print("Sent jump")

        elif cmd == "observe":
            await ws.send(json.dumps({
                "id": "obs-1", "type": "observation.request", "session_id": session_id, "timestamp": 0,
                "payload": {},
            }))
            # Read a few messages
            for _ in range(5):
                try:
                    msg = json.loads(await asyncio.wait_for(ws.recv(), timeout=2.0))
                    if msg["type"] == "observation.state":
                        p = msg["payload"]["player"]
                        print(f"Player at ({p['position']['x']:.0f}, {p['position']['y']:.0f}) "
                              f"HP: {p['health']['current']}/{p['health']['max']}")
                except asyncio.TimeoutError:
                    break

        elif cmd == "interact":
                tx = int(sys.argv[2]) if len(sys.argv) > 2 else 0
                ty = int(sys.argv[3]) if len(sys.argv) > 3 else 0
                await ws.send(json.dumps({
                    "id": "interact-1", "type": "action.execute", "session_id": session_id, "timestamp": 0,
                    "payload": {
                        "action_id": "interact-test",
                        "action_type": "interact",
                        "params": {"target_x": tx, "target_y": ty, "interaction_type": "auto"},
                        "priority": 1,
                        "timeout_ms": 3000,
                    },
                }))
                print(f"Sent interact at ({tx}, {ty})")

        # Wait for any responses
        await asyncio.sleep(1.0)


if __name__ == "__main__":
    asyncio.run(main())
