#!/usr/bin/env python3
"""Standalone WebUI entry point — creates its own bridge connection.

Usage:
    python webui/main.py
    python -m webui.main
"""

from __future__ import annotations

import asyncio
import logging
import sys
from pathlib import Path

# Ensure runtime/ is in sys.path so both `webui` and `terraclaw_runtime` resolve
_runtime_root = str(Path(__file__).resolve().parent.parent)
if _runtime_root not in sys.path:
    sys.path.insert(0, _runtime_root)

import structlog
import uvicorn

from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.agent.action_registry import ActionRegistry
from terraclaw_runtime.agent.tool_registry import ToolRegistry
from terraclaw_runtime.agent.prompt_builder import PromptBuilder
from terraclaw_runtime.config import BridgeConfig

from webui import create_webui_app

HERE = Path(__file__).parent
CONFIG_DIR = HERE.parent / "config"
LOG_FILE = HERE.parent / "logs" / "runtime.log"


def _setup_logging():
    """Configure structlog for console + file output (standalone mode)."""
    shared_processors = [
        structlog.stdlib.filter_by_level,
        structlog.stdlib.add_log_level,
        structlog.stdlib.PositionalArgumentsFormatter(),
        structlog.processors.TimeStamper(fmt="iso"),
        structlog.stdlib.ProcessorFormatter.wrap_for_formatter,
    ]

    structlog.configure(
        processors=shared_processors,
        context_class=dict,
        logger_factory=structlog.stdlib.LoggerFactory(),
        wrapper_class=structlog.stdlib.BoundLogger,
        cache_logger_on_first_use=True,
    )

    # Suppress noisy network libs
    for noisy in ("httpcore", "httpx", "websockets"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    root = logging.getLogger()
    root.setLevel(logging.DEBUG)

    # Console handler
    console = logging.StreamHandler(sys.stdout)
    console.setLevel(logging.DEBUG)
    console.setFormatter(
        structlog.stdlib.ProcessorFormatter(
            processor=structlog.dev.ConsoleRenderer(),
        )
    )
    root.addHandler(console)

    # File handler (KeyValueRenderer for analyze_log.py compatibility)
    LOG_FILE.parent.mkdir(parents=True, exist_ok=True)
    file_handler = logging.FileHandler(str(LOG_FILE), encoding="utf-8", mode="w")
    file_handler.setLevel(logging.DEBUG)
    file_handler.setFormatter(
        structlog.stdlib.ProcessorFormatter(
            processor=structlog.processors.KeyValueRenderer(
                key_order=["timestamp", "level", "event"],
                sort_keys=False,
            ),
        )
    )
    root.addHandler(file_handler)

    print(f"[WEBUI] Logging to {LOG_FILE}")


def main():
    """Standalone entry: creates bridge connection, starts web server."""
    _setup_logging()
    logger = structlog.get_logger()

    async def startup():
        cfg = BridgeConfig(url="ws://127.0.0.1:9777/bridge")
        bridge = BridgeClient(cfg)

        logger.info("webui_standalone_starting")

        await bridge.connect()
        result = await bridge.register_agent()
        agent_id = bridge.agent_id or result.get("agent_id", "?")
        logger.info("webui_agent_registered", agent_id=agent_id[:12])

        actions_yaml = CONFIG_DIR / "actions.yaml"
        action_registry = ActionRegistry(str(actions_yaml))
        action_registry.load()
        tool_registry = ToolRegistry(action_registry, bridge, agent_id)
        prompt_builder = PromptBuilder(
            prompts_path=str(CONFIG_DIR / "prompts"),
        )

        app = create_webui_app(bridge, tool_registry, prompt_builder, agent_id)

        print(f"[WEBUI] Ready → http://127.0.0.1:8080 ({len(tool_registry.get_definitions())} tools)")

        config = uvicorn.Config(app, host="127.0.0.1", port=8080, log_level="info")
        server = uvicorn.Server(config)
        await server.serve()

    try:
        asyncio.run(startup())
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
