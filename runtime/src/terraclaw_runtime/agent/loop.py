"""Agent loop — NPC agent observe → think → act cycle."""

from __future__ import annotations

import asyncio
import time

import structlog

from terraclaw_runtime.agent.llm import LLMClient, LLMResponse
from terraclaw_runtime.agent.prompt_builder import PromptBuilder
from terraclaw_runtime.agent.tool_registry import ToolRegistry
from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import AgentObservation
from terraclaw_runtime.planning.plan import Plan, PlanStatus

from terraclaw_runtime._debug import dprint

logger = structlog.get_logger()


class AgentLoop:
    """Agent loop — controls an NPC agent via LLM."""

    def __init__(
        self,
        bridge: BridgeClient,
        llm: LLMClient,
        tools: ToolRegistry,
        agent_id: str,
        llm_call_interval_s: float = 5.0,
        tick_rate_hz: float = 10.0,
        prompts_path: str = "config/prompts",
    ):
        self._bridge = bridge
        self._llm = llm
        self._tools = tools
        self._agent_id = agent_id
        self._prompt_builder = PromptBuilder(prompts_path=prompts_path)
        self._llm_call_interval_s = llm_call_interval_s
        self._tick_rate_hz = tick_rate_hz

        self._last_llm_call_time = 0.0
        self._plan: Plan | None = None
        self._action_results: list[str] = []
        self._conversation_history: list[dict] = []
        self._running = False

    @property
    def plan(self) -> Plan | None:
        return self._plan

    async def run(self) -> None:
        """Main loop — runs until stopped."""
        self._running = True
        logger.info("agent_loop_started", agent_id=self._agent_id)

        while self._running:
            try:
                obs = await self._bridge.receive_agent_observation(timeout=1.0)
                if obs is None:
                    continue

                events = await self._bridge.receive_events(timeout=0.05)
                print("[AGENT]",f"Observation — pos=({obs.agent.position.x:.0f}, {obs.agent.position.y:.0f}) layer={obs.world.get('depth_layer', '?')}")

                if self._should_call_llm(obs, events):
                    await self._llm_turn(obs, events)

            except asyncio.CancelledError:
                break
            except Exception:
                logger.exception("agent_loop_error")
                await asyncio.sleep(1.0)

        logger.info("agent_loop_stopped")

    def stop(self) -> None:
        self._running = False

    def _should_call_llm(self, obs: AgentObservation, events: list) -> bool:
        if self._plan is None or self._plan.status == PlanStatus.COMPLETE:
            return True

        elapsed = time.monotonic() - self._last_llm_call_time
        return elapsed >= self._llm_call_interval_s

    async def _llm_turn(self, obs: AgentObservation, events: list) -> None:
        """Execute one full LLM turn."""
        logger.debug("llm_turn_start", tick=obs.tick)
        dprint("[AGENT]",f"── LLM turn (tick={obs.tick}) ──")

        memory_context = ""
        plan_context = self._format_plan() if self._plan else ""
        action_results = "\n".join(self._action_results[-5:]) if self._action_results else ""

        messages = self._prompt_builder.build(
            observation=obs,
            memory_context=memory_context,
            plan_context=plan_context,
            action_results=action_results,
        )

        self._conversation_history.extend(messages)
        if len(self._conversation_history) > 20:
            self._conversation_history = self._conversation_history[-20:]

        dprint("[AGENT]","Calling LLM...")
        t0 = time.monotonic()
        response = await self._llm.generate(
            system_prompt=self._prompt_builder.get_system_prompt(),
            messages=self._conversation_history,
            tools=self._tools.get_definitions(),
        )
        dprint("[AGENT]",f"LLM responded in {time.monotonic() - t0:.1f}s")
        if response.text:
            print("[AGENT]",f"LLM says:\n{response.text}")
        if response.tool_calls:
            dprint("[AGENT]",f"LLM tools: {', '.join(f'{tc.name}({tc.arguments})' for tc in response.tool_calls)}")

        self._last_llm_call_time = time.monotonic()

        await self._handle_llm_response(response, obs)
        self._action_results.clear()

    async def _handle_llm_response(self, response: LLMResponse, obs: AgentObservation) -> None:
        logger.debug("llm_response",
                     text=response.text[:200] if response.text else "",
                     tool_calls=[tc.name for tc in response.tool_calls],
                     tokens=response.usage.input_tokens)

        content_blocks = []
        if response.text:
            content_blocks.append({"type": "text", "text": response.text})
        for tc in response.tool_calls:
            content_blocks.append({
                "type": "tool_use", "id": tc.id, "name": tc.name, "input": tc.arguments,
            })

        self._conversation_history.append({
            "role": "assistant",
            "content": content_blocks if content_blocks else None,
        })

        for tc in response.tool_calls:
            print("[AGENT]",f"  >> {tc.name}({tc.arguments})")
            result = await self._tools.dispatch(tc.name, tc.arguments)
            logger.debug("tool_result", tool=tc.name, result=str(result)[:200])

            status = result.get("status", "unknown")
            err = result.get("error")
            if err:
                print("[AGENT]",f"  << {tc.name} FAILED: {err}")
                self._action_results.append(f"{tc.name}: FAILED ({err})")
            else:
                print("[AGENT]",f"  << {tc.name}: {status}")
                self._action_results.append(f"{tc.name}: {status}")

            self._conversation_history.append({
                "role": "user",
                "content": [{
                    "type": "tool_result",
                    "tool_use_id": tc.id,
                    "content": str(result),
                }],
            })

        if response.tool_calls and response.stop_reason in ("tool_use", "tool_calls"):
            dprint("[AGENT]","── Follow-up LLM call (tool results sent back) ──")
            await asyncio.sleep(0.5)
            fresh_obs = await self._bridge.receive_agent_observation(timeout=2.0)
            if fresh_obs:
                messages = self._prompt_builder.build(
                    observation=fresh_obs,
                    memory_context="",
                    plan_context="",
                    action_results="\n".join(self._action_results[-5:]),
                )

                follow_up = await self._llm.generate(
                    system_prompt=self._prompt_builder.get_system_prompt(),
                    messages=self._conversation_history + messages,
                    tools=self._tools.get_definitions(),
                )
                dprint("[AGENT]","Follow-up LLM response received")
                await self._handle_llm_response(follow_up, fresh_obs)

    def _format_plan(self) -> str:
        if self._plan is None:
            return "No active plan."
        return self._plan.to_context_string()
