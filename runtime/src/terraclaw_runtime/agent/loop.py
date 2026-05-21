"""Agent loop — NPC agent observe → think → act cycle."""

from __future__ import annotations

import asyncio
import re
import time

import structlog

from terraclaw_runtime.agent.llm import LLMClient, LLMResponse
from terraclaw_runtime.agent.prompt_builder import PromptBuilder
from terraclaw_runtime.agent.tool_registry import ToolRegistry
from terraclaw_runtime.bridge.client import BridgeClient
from terraclaw_runtime.bridge.message import AgentObservation
from terraclaw_runtime.planning.plan import Plan

from terraclaw_runtime.memory.manager import MemoryManager
from terraclaw_runtime._debug import dprint

logger = structlog.get_logger()


def _has_tool_result(msg: dict) -> bool:
    """Check if a conversation message contains a tool_result content block."""
    content = msg.get("content")
    if isinstance(content, list):
        for block in content:
            if isinstance(block, dict) and block.get("type") == "tool_result":
                return True
    return False


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
        system_prompt_path: str = "config/system.md",
        identity_path: str | None = None,
        memory: MemoryManager | None = None,
        max_history_messages: int = 10,
        dashboard: Any = None,  # LiveDashboard instance for real-time UI
    ):
        self._bridge = bridge
        self._llm = llm
        self._tools = tools
        self._agent_id = agent_id
        self._prompt_builder = PromptBuilder(
            system_prompt_path=system_prompt_path,
            identity_path=identity_path,
        )
        self._llm_call_interval_s = llm_call_interval_s
        self._tick_rate_hz = tick_rate_hz
        self._max_history_messages = max_history_messages
        self._memory = memory  # MemoryManager or None
        self._dashboard = dashboard  # LiveDashboard or None

        self._last_llm_call_time = 0.0
        self._plan: Plan | None = None
        self._action_results: list[str] = []
        self._pending_actions: dict[str, dict] = {}  # action_id → {"type": str, "start": float}
        self._recently_resolved: dict[str, float] = {}  # action_id → timestamp; recently completed/cancelled, shown in prompt for a few turns
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
        elapsed = time.monotonic() - self._last_llm_call_time
        return elapsed >= self._llm_call_interval_s

    async def _llm_turn(self, obs: AgentObservation, events: list) -> None:
        """Execute one full LLM turn."""
        logger.debug("llm_turn_start", tick=obs.tick)
        dprint("[AGENT]",f"── LLM turn (tick={obs.tick}) ──")

        # Update memory from latest observation
        if self._memory:
            self._memory.update(obs)
            memory_context = self._memory.get_context_for_llm(obs)
        else:
            memory_context = ""
        plan_context = self._format_plan() if self._plan else ""
        pending_context, completed_context = await self._format_pending_actions()
        resolved_context = self._format_resolved_actions()
        instructions = await self._drain_instructions()
        action_results = "\n".join(self._action_results[-5:]) if self._action_results else ""

        messages = self._prompt_builder.build(
            observation=obs,
            memory_context=memory_context,
            plan_context=plan_context,
            action_results=action_results,
            pending_context=pending_context,
            completed_context=completed_context,
            resolved_context=resolved_context,
            instructions=instructions,
        )

        self._conversation_history.extend(messages)
        self._truncate_history()

        dprint("[AGENT]","Calling LLM...")
        logger.debug("llm_call", tick=obs.tick, messages=len(self._conversation_history),
                     tools=len(self._tools.get_definitions()), msg="LLM generating response",
                     pending_actions=len(self._pending_actions) if self._pending_actions else 0,
                     has_instructions=bool(instructions),
                     action_results=len(self._action_results),
                     pos=(obs.agent.position.x, obs.agent.position.y))

        if self._dashboard:
            self._dashboard.publish("llm_call", {
                "turn": self._dashboard.next_turn(),
                "tick": obs.tick,
                "pos": {"x": obs.agent.position.x, "y": obs.agent.position.y},
                "pending_actions": len(self._pending_actions) if self._pending_actions else 0,
                "has_instructions": bool(instructions),
            })
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

        if self._dashboard:
            self._dashboard.publish("llm_response", {
                "turn": self._dashboard._turn_counter,  # current turn (set by llm_call)
                "text": response.text or "",
                "tool_calls": [
                    {"name": tc.name, "arguments": tc.arguments} for tc in response.tool_calls
                ],
                "duration_s": round(time.monotonic() - t0, 1),
            })

        self._last_llm_call_time = time.monotonic()

        await self._handle_llm_response(response, obs)
        self._action_results.clear()

        # Truncate history again after tool results were added by _handle_llm_response,
        # so a burst of tool calls doesn't flood the history and push out assistant context.
        self._truncate_history()

    async def _handle_llm_response(self, response: LLMResponse, obs: AgentObservation, allow_follow_up: bool = True) -> None:
        logger.debug("llm_response",
                     text=response.text[:600] if response.text else "",
                     tool_calls=[tc.name for tc in response.tool_calls],
                     tokens=response.usage.input_tokens,
                     finish_reason=response.stop_reason,
                     tool_args=[str(tc.arguments)[:400] for tc in response.tool_calls])

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

        # Internal tools (cancel_action, get_action_status) complete immediately
        # and don't need a follow-up — treat them as non-blocking.
        _IMMEDIATE_INTERNAL = frozenset({"cancel_action", "get_action_status"})
        all_non_blocking = True

        for tc in response.tool_calls:
            print("[AGENT]",f"  >> {tc.name}({tc.arguments})")
            result = await self._tools.dispatch(tc.name, tc.arguments)
            logger.debug("tool_result", tool=tc.name, result=str(result)[:200])

            # Track actions that were started (non-blocking dispatch)
            if result.get("status") == "started" and result.get("action_id"):
                self._pending_actions[result["action_id"]] = {
                    "type": result.get("action_type", tc.name),
                    "start": time.monotonic(),
                }
            elif tc.name not in _IMMEDIATE_INTERNAL:
                # Only non-internal tools that return immediately trigger follow-up
                all_non_blocking = False

            # Clean up pending_actions immediately when cancelled
            if tc.name == "cancel_action":
                cancelled_id = result.get("action_id", "")
                if cancelled_id in self._pending_actions:
                    del self._pending_actions[cancelled_id]
                    dprint("[AGENT]", f"  Removed cancelled action {cancelled_id[:12]}... from pending")
                if cancelled_id:
                    self._recently_resolved[cancelled_id] = time.monotonic()

            if self._dashboard:
                self._dashboard.publish("tool_result", {
                    "turn": self._dashboard._turn_counter,
                    "tool": tc.name,
                    "result": result,
                })

            status = result.get("status", "unknown")
            err = result.get("error")
            if err:
                print("[AGENT]",f"  << {tc.name} FAILED: {err}")
                self._action_results.append(f"{tc.name}: FAILED ({err})")
            else:
                print("[AGENT]",f"  << {tc.name}: {status}")
                self._action_results.append(f"{tc.name}: {status}")

            # Record action to memory
            if self._memory:
                self._memory.record_action(obs.tick, tc.name, tc.arguments, result, success=not bool(err))

            self._conversation_history.append({
                "role": "user",
                "content": [{
                    "type": "tool_result",
                    "tool_use_id": tc.id,
                    "content": str(result),
                }],
            })

        # Follow-up: only when (a) there's a meaningful immediate result to show,
        # (b) depth is limited to 1 (no recursive cascade), (c) messages are truncated.
        if response.tool_calls and response.stop_reason in ("tool_use", "tool_calls"):
            if all_non_blocking:
                dprint("[AGENT]", "── Skipping follow-up (all actions non-blocking) ──")
            elif not allow_follow_up:
                dprint("[AGENT]", "── Skipping follow-up (max depth reached) ──")
            else:
                dprint("[AGENT]","── Follow-up LLM call ──")
                await asyncio.sleep(0.5)
                fresh_obs = await self._bridge.receive_agent_observation(timeout=2.0)
                if fresh_obs:
                    pending_context, completed_context = await self._format_pending_actions()
                    resolved_context = self._format_resolved_actions()
                    messages = self._prompt_builder.build(
                        observation=fresh_obs,
                        memory_context="",
                        plan_context="",
                        action_results="\n".join(self._action_results[-5:]),
                        pending_context=pending_context,
                        completed_context=completed_context,
                        resolved_context=resolved_context,
                        instructions=await self._drain_instructions(),
                    )

                    # Truncate combined history to max_history_messages
                    combined = self._conversation_history + messages
                    if len(combined) > self._max_history_messages:
                        combined = combined[-self._max_history_messages:]
                    while combined and _has_tool_result(combined[0]):
                        combined.pop(0)

                    logger.debug("llm_call", tick=fresh_obs.tick, messages=len(combined),
                                 tools=len(self._tools.get_definitions()), msg="LLM follow-up generation",
                                 pending_actions=len(self._pending_actions) if self._pending_actions else 0)
                    follow_up = await self._llm.generate(
                        system_prompt=self._prompt_builder.get_system_prompt(),
                        messages=combined,
                        tools=self._tools.get_definitions(),
                    )
                    dprint("[AGENT]","Follow-up LLM response received")
                    await self._handle_llm_response(follow_up, fresh_obs, allow_follow_up=False)

    def _truncate_history(self) -> None:
        """Truncate conversation_history to max_history_messages and remove stale action refs."""
        if len(self._conversation_history) > self._max_history_messages:
            self._conversation_history = self._conversation_history[-self._max_history_messages:]
        # OpenAI API requires role:tool messages to follow role:assistant with tool_calls.
        # Truncation may strip the assistant but keep the tool_result; clean those up.
        while self._conversation_history and _has_tool_result(self._conversation_history[0]):
            self._conversation_history.pop(0)

        # Strip assistant+tool_result pairs referencing completed/cancelled actions.
        remove_indices: set[int] = set()
        _TERMINAL = ("'cancelled'", "'completed'", "'already_cancelled'")
        for i in range(len(self._conversation_history) - 1, -1, -1):
            content = str(self._conversation_history[i].get("content", ""))
            if any(m in content for m in _TERMINAL):
                remove_indices.add(i)
                if i > 0 and self._conversation_history[i - 1].get("role") == "assistant":
                    remove_indices.add(i - 1)

        # Also remove tool_results whose action_id is no longer pending — these are
        # stale "started" entries from actions that completed without a terminal status.
        for i in range(len(self._conversation_history) - 1, -1, -1):
            if i in remove_indices:
                continue
            content = str(self._conversation_history[i].get("content", ""))
            # Extract action_ids from tool_result content
            for m in re.finditer(r"'action_id':\s*'([^']+)'", content):
                aid = m.group(1)
                if aid not in self._pending_actions:
                    remove_indices.add(i)
                    if i > 0 and self._conversation_history[i - 1].get("role") == "assistant":
                        remove_indices.add(i - 1)
                    break

        if remove_indices:
            self._conversation_history = [
                m for j, m in enumerate(self._conversation_history) if j not in remove_indices
            ]

    async def _drain_instructions(self) -> str:
        """Drain all pending chat instructions from the bridge."""
        lines = []
        while True:
            instr = await self._bridge.receive_instruction(timeout=0.05)
            if instr is None:
                break
            lines.append(f"- {instr}")
        if not lines:
            return ""
        if self._dashboard:
            for line in lines:
                self._dashboard.publish("player_instruction", {"text": line})
        return "The player has sent you these instructions:\n" + "\n".join(lines)

    async def _format_pending_actions(self) -> tuple[str, str]:
        """Return (pending_actions_str, completed_actions_str) for LLM context.

        Checks the bridge for completed actions so the LLM gets timely
        feedback instead of stale "running" entries.
        Falls back to estimated completion after a per-action timeout
        when the bridge result never arrives.
        """
        if not self._pending_actions:
            return "", ""

        _SINGLE_TURN = frozenset({"talk", "teleport", "place_tile", "place_wall", "break_wall"})

        now = time.monotonic()
        pending_lines: list[str] = []
        completed_lines: list[str] = []
        to_remove: list[str] = []

        for aid, info in list(self._pending_actions.items()):
            action_type = info.get("type", "unknown")
            start = info.get("start", now)

            # Check if the action has completed via the bridge's future
            result = await self._bridge.poll_action_result(aid)
            if result is not None:
                status = result.get("status", "completed")
                completed_lines.append(f"  {action_type} ({aid[:12]}...) → {status}")
                self._recently_resolved[aid] = time.monotonic()
                to_remove.append(aid)
                continue

            # Per-action timeout: if the bridge result never arrives,
            # assume the action completed on C# side.
            elapsed = now - start
            if action_type in _SINGLE_TURN:
                timeout = 5.0
            elif action_type == "break_tile":
                timeout = 8.0
            elif action_type == "wait":
                timeout = 6.0
            elif action_type == "move_to":
                timeout = 30.0
            else:
                timeout = 30.0

            if elapsed > timeout:
                # Timed out — assume the action completed on C# side even if
                # the result message was lost (common race with result delivery).
                completed_lines.append(f"  {action_type} ({aid[:12]}...) → estimated completion")
                self._recently_resolved[aid] = time.monotonic()
                to_remove.append(aid)
            else:
                pending_lines.append(f"  {action_type} ({aid[:12]}...) running for {elapsed:.0f}s")

        for aid in to_remove:
            self._pending_actions.pop(aid, None)

        pending_str = "\n".join(pending_lines) if pending_lines else ""
        completed_str = "\n".join(completed_lines) if completed_lines else ""

        if completed_lines:
            dprint("[AGENT]", f"Completed actions:\n{completed_str}")

        return pending_str, completed_str

    def _format_resolved_actions(self) -> str:
        """Format recently resolved (completed/cancelled) action_ids for LLM context.

        Shown for ~60s after resolution so the LLM knows not to poll stale IDs.
        """
        if not self._recently_resolved:
            return ""
        now = time.monotonic()
        lines: list[str] = []
        stale: list[str] = []
        for aid, ts in list(self._recently_resolved.items()):
            if now - ts > 60:
                stale.append(aid)
            else:
                lines.append(f"  {aid[:12]}...")
        for aid in stale:
            del self._recently_resolved[aid]
        if not lines:
            return ""
        return "The following actions were recently completed or cancelled (do not check on them again):\n" + "\n".join(lines)

    def _format_plan(self) -> str:
        if self._plan is None:
            return "No active plan."
        return self._plan.to_context_string()
