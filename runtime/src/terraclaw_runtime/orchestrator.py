"""Main orchestrator — wires everything together and runs the main loop."""

from __future__ import annotations

import asyncio
import logging
import signal
import sys
import os
from pathlib import Path

import structlog

from terraclaw_runtime.agent.action_registry import ActionRegistry
from terraclaw_runtime.agent.llm import LLMClient
from terraclaw_runtime.agent.loop import AgentLoop
from terraclaw_runtime.agent.tool_registry import ToolRegistry
from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.config import RuntimeConfig
from terraclaw_runtime.skills.registry import SkillRegistry

logger = structlog.get_logger()


class TerraClawRuntime:
    """Top-level orchestrator for the TerraClaw runtime."""

    def __init__(self, config: RuntimeConfig):
        self._config = config
        self._bridge = BridgeClient(config.bridge)
        self._llm = LLMClient(config.llm)
        self._agent_id: str | None = None
        self._tools: ToolRegistry | None = None
        self._skills: SkillRegistry | None = None
        self._agent: AgentLoop | None = None
        self._human_mode_stop: asyncio.Event | None = None
        self._webui_server = None  # uvicorn.Server instance

    async def start(self) -> None:
        """Connect to the bridge, register NPC agent, start agent loop."""
        logger.info("terraclaw_runtime_starting", version="0.1.0")

        loop = asyncio.get_running_loop()
        for sig in (signal.SIGINT, signal.SIGTERM):
            try:
                loop.add_signal_handler(sig, self._handle_shutdown)
            except NotImplementedError:
                pass

        try:
            await self._bridge.connect()

            # Register an NPC agent at the local player's position
            logger.info("registering_agent")
            result = await self._bridge.register_agent()
            if "error" in result:
                logger.error("agent_registration_failed", error=result.get("error"))
                return

            self._agent_id = result.get("agent_id")
            logger.info("agent_registered", agent_id=self._agent_id, entity_index=result.get("entity_index"))

            # Build action registry and tool registry
            actions = ActionRegistry(self._config.actions_path)
            actions.load()
            self._tools = ToolRegistry(actions, self._bridge, self._agent_id)

            # Build skill registry (loads Lua skills from config/skills/)
            self._skills = SkillRegistry(self._bridge)
            loaded = self._skills.load_from_directory(self._config.skill.skill_definitions_path)
            if loaded:
                logger.info("lua_skills_loaded", count=loaded)

            if self._config.human_mode:
                await self._start_human_mode()
            else:
                self._agent = AgentLoop(
                    bridge=self._bridge,
                    llm=self._llm,
                    tools=self._tools,
                    agent_id=self._agent_id,
                    llm_call_interval_s=self._config.llm_call_interval_s,
                    tick_rate_hz=self._config.tick_rate_hz,
                    prompts_path=self._config.prompt.prompts_path,
                    max_history_messages=self._config.prompt.max_history_messages,
                )
                await self._agent.run()
        except Exception:
            logger.exception("runtime_fatal_error")
            raise
        finally:
            if self._agent_id:
                logger.info("unregistering_agent", agent_id=self._agent_id)
                await self._bridge.unregister_agent(self._agent_id)
            await self._bridge.disconnect()

    async def _start_human_mode(self):
        """Start human-in-the-loop web UI instead of the LLM agent loop."""
        try:
            import uvicorn
        except ImportError:
            logger.error("human_mode_requires_uvicorn",
                         message="Install: pip install uvicorn")
            return

        from webui import create_webui_app
        from terraclaw_runtime.agent.prompt_builder import PromptBuilder

        prompt_builder = PromptBuilder(
            prompts_path=self._config.prompt.prompts_path,
        )

        app = create_webui_app(
            bridge=self._bridge,
            tool_registry=self._tools,
            prompt_builder=prompt_builder,
            agent_id=self._agent_id,
        )

        config = uvicorn.Config(app, host="127.0.0.1", port=8080, log_level="info")
        config.install_signal_handlers = not sys.platform.startswith("win")
        server = uvicorn.Server(config)
        self._webui_server = server

        self._human_mode_stop = asyncio.Event()

        print(f"\n[HUMAN MODE] Web UI → http://127.0.0.1:8080")
        print(f"[HUMAN MODE] Press Ctrl+C to stop\n")

        # Run server in background
        server_task = asyncio.create_task(server.serve())

        try:
            # Wait for shutdown signal
            await self._human_mode_stop.wait()
        finally:
            server.should_exit = True
            await server_task

    def _handle_shutdown(self) -> None:
        logger.info("shutdown_requested")
        if self._agent:
            self._agent.stop()
        if self._human_mode_stop:
            self._human_mode_stop.set()
        if self._webui_server:
            self._webui_server.should_exit = True

    async def stop(self) -> None:
        if self._agent:
            self._agent.stop()
        if self._human_mode_stop:
            self._human_mode_stop.set()
        if self._webui_server:
            self._webui_server.should_exit = True
        if self._agent_id:
            await self._bridge.unregister_agent(self._agent_id)
        await self._bridge.disconnect()


def _setup_logging(config: RuntimeConfig) -> None:
    """Configure structlog for console output and optional file logging."""
    level = logging.DEBUG if config.verbose else logging.INFO

    # Shared structlog processors
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

    # Suppress network library debug noise in file logs
    for noisy in ("httpcore", "httpx", "websockets"):
        logging.getLogger(noisy).setLevel(logging.WARNING)

    # Set up root logger
    root_logger = logging.getLogger()
    root_logger.setLevel(level)

    # Console handler with rich ANSI rendering
    console_handler = logging.StreamHandler(sys.stdout)
    console_handler.setLevel(level)
    console_handler.setFormatter(
        structlog.stdlib.ProcessorFormatter(
            processor=structlog.dev.ConsoleRenderer(),
        )
    )
    root_logger.addHandler(console_handler)

    # Optional file handler with key=value format
    if config.log_file:
        log_path = Path(config.log_file)
        log_path.parent.mkdir(parents=True, exist_ok=True)
        file_handler = logging.FileHandler(config.log_file, encoding="utf-8", mode="w")
        file_handler.setLevel(logging.DEBUG)  # Always debug level in file
        file_handler.setFormatter(
            structlog.stdlib.ProcessorFormatter(
                processor=structlog.processors.KeyValueRenderer(
                    key_order=["timestamp", "level", "event"],
                    sort_keys=False,
                ),
            )
        )
        root_logger.addHandler(file_handler)
        print(f"[LOG] Writing debug log to {config.log_file}")


async def main() -> None:
    """Entry point for the TerraClaw runtime."""

    # Parse CLI flags
    cli_human = "--human" in sys.argv

    # Load config
    config_path = os.getenv("TERRACLAW_CONFIG", "config/config.yaml")
    config = RuntimeConfig.from_yaml(config_path)
    config.apply_env_overrides()
    config.human_mode = config.human_mode or cli_human

    # Set verbose flag for debug output
    import terraclaw_runtime._debug as _debug_mod
    _debug_mod.VERBOSE = config.verbose

    # Setup logging — console + optional file
    _setup_logging(config)

    if not config.human_mode and not config.llm.api_key:
        logger.error("no_api_key", provider=config.llm.provider)
        return

    runtime = TerraClawRuntime(config)
    await runtime.start()


if __name__ == "__main__":
    asyncio.run(main())
