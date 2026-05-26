"""Live LLM dashboard — real-time WebSocket event stream + web frontend."""

from __future__ import annotations

import asyncio
import json
import time
from pathlib import Path
from typing import Any

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.responses import FileResponse

HERE = Path(__file__).resolve().parent
DASHBOARD_HTML = HERE.parent.parent.parent / "dashboard" / "index.html"


class LiveDashboard:
    """Real-time event broadcaster for LLM agent turns.

    Usage:
        dashboard = LiveDashboard()
        asyncio.create_task(dashboard.serve(port=9090))
        # ... in agent loop:
        dashboard.publish("llm_response", {...})
    """

    def __init__(self):
        self._app = self._build_app()
        self._websockets: set[WebSocket] = set()
        self._turn_counter = 0

    def _build_app(self) -> FastAPI:
        app = FastAPI()

        @app.get("/")
        async def index():
            return FileResponse(str(DASHBOARD_HTML))

        @app.websocket("/ws")
        async def ws(ws: WebSocket):
            await ws.accept()
            self._websockets.add(ws)
            # Send catch-up: latest turn count
            await ws.send_json({"type": "hello", "data": {"turn": self._turn_counter}})
            try:
                while True:
                    msg = await ws.receive_text()
                    # Client-side pings are fine, just ignore
            except WebSocketDisconnect:
                pass
            finally:
                self._websockets.discard(ws)

        return app

    async def serve(self, host: str = "127.0.0.1", port: int = 9090) -> None:
        """Start the uvicorn server (blocking)."""
        try:
            import uvicorn
        except ImportError:
            print("[DASHBOARD] Install uvicorn: pip install uvicorn")
            return
        cfg = uvicorn.Config(self._app, host=host, port=port, log_level="warning")
        cfg.install_signal_handlers = False
        server = uvicorn.Server(cfg)
        print(f"[DASHBOARD] Live LLM dashboard → http://{host}:{port}")
        await server.serve()

    def publish(self, event_type: str, data: dict[str, Any]) -> None:
        """Broadcast an event to all connected WebSocket clients.

        Thread-safe — schedules the send on the asyncio event loop.
        """
        msg = json.dumps({"type": event_type, "data": data, "ts": time.time()})
        for ws in list(self._websockets):
            try:
                asyncio.ensure_future(ws.send_text(msg))
            except Exception:
                pass

    def next_turn(self) -> int:
        self._turn_counter += 1
        return self._turn_counter
