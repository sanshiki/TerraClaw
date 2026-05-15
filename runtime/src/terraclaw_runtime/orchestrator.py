"""Main orchestrator — wires everything together and runs the main loop."""

from __future__ import annotations

import asyncio
import signal

import structlog

from terraclaw_runtime.agent.llm import LLMClient
from terraclaw_runtime.agent.loop import AgentLoop
from terraclaw_runtime.agent.tool_registry import ToolRegistry
from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.config import RuntimeConfig

logger = structlog.get_logger()


class TerraClawRuntime:
    """Top-level orchestrator for the TerraClaw runtime."""

    def __init__(self, config: RuntimeConfig):
        self._config = config
        self._bridge = BridgeClient(config.bridge)
        self._llm = LLMClient(config.llm)
        self._agent_id: str | None = None
        self._tools: ToolRegistry | None = None
        self._agent: AgentLoop | None = None

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

            # Build tool registry and agent loop with agent_id
            self._tools = ToolRegistry(self._bridge, self._agent_id)
            self._agent = AgentLoop(
                bridge=self._bridge,
                llm=self._llm,
                tools=self._tools,
                agent_id=self._agent_id,
                llm_call_interval_s=self._config.llm_call_interval_s,
                tick_rate_hz=self._config.tick_rate_hz,
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

    def _handle_shutdown(self) -> None:
        logger.info("shutdown_requested")
        if self._agent:
            self._agent.stop()

    async def stop(self) -> None:
        if self._agent:
            self._agent.stop()
        if self._agent_id:
            await self._bridge.unregister_agent(self._agent_id)
        await self._bridge.disconnect()


async def main() -> None:
    """Entry point for the TerraClaw runtime."""
    import os

    # Load config
    config_path = os.getenv("TERRACLAW_CONFIG", "config/config.yaml")
    config = RuntimeConfig.from_yaml(config_path)
    config.apply_env_overrides()

    # Set verbose flag for debug output
    import terraclaw_runtime._debug as _debug_mod
    _debug_mod.VERBOSE = config.verbose

    # Setup logging — PrintLoggerFactory ensures output on all terminals
    structlog.configure(
        processors=[
            structlog.stdlib.add_log_level,
            structlog.processors.TimeStamper(fmt="iso"),
            structlog.dev.ConsoleRenderer(),
        ],
        context_class=dict,
        logger_factory=structlog.PrintLoggerFactory(),
        wrapper_class=structlog.stdlib.BoundLogger,
        cache_logger_on_first_use=True,
    )

    if not config.llm.api_key:
        logger.error("no_api_key", provider=config.llm.provider)
        return

    runtime = TerraClawRuntime(config)
    await runtime.start()


if __name__ == "__main__":
    asyncio.run(main())
