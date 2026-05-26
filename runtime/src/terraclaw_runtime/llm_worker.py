"""Generic C#-first LLM worker.

The tMod side owns scheduling, observations, and output contracts. This worker
only listens for llm.request messages, calls the configured model, and returns
llm.response messages.
"""

from __future__ import annotations

import asyncio
import json
import time
from typing import Any

import structlog

from terraclaw_runtime.agent.llm import LLMClient
from terraclaw_runtime.bridge.client import BridgeClient

logger = structlog.get_logger()


class LlmWorker:
    def __init__(self, bridge: BridgeClient, llm: LLMClient, dashboard: Any = None):
        self._bridge = bridge
        self._llm = llm
        self._dashboard = dashboard
        self._running = False
        self._tasks: dict[str, asyncio.Task[None]] = {}

    async def run(self) -> None:
        self._running = True
        logger.info("llm_worker_started")
        print("[LLM WORKER] Waiting for C# llm.request messages...")
        while self._running:
            msg = await self._bridge.receive(timeout=1.0)
            if msg is None:
                continue
            if msg.type == "llm.request":
                request_id = msg.payload.get("request_id", "")
                if request_id:
                    logger.info(
                        "llm_request_received",
                        request_id=request_id,
                        agent_id=msg.payload.get("agent_id", ""),
                        instruction=_short(msg.payload.get("instruction", ""), 160),
                    )
                    if self._dashboard:
                        self._dashboard.publish("llm_request", self._dashboard_payload(
                            msg.payload,
                            turn=self._dashboard.next_turn(),
                        ))
                    self._tasks[request_id] = asyncio.create_task(self._handle_request(msg.payload))
            elif msg.type == "llm.cancel":
                request_id = msg.payload.get("request_id", "")
                task = self._tasks.pop(request_id, None)
                if task and not task.done():
                    task.cancel()

    def stop(self) -> None:
        self._running = False
        for task in self._tasks.values():
            if not task.done():
                task.cancel()

    async def _handle_request(self, payload: dict[str, Any]) -> None:
        request_id = payload.get("request_id", "")
        agent_id = payload.get("agent_id", "")
        timeout_ms = int(payload.get("timeout_ms", 30000))
        t0 = time.monotonic()

        try:
            response = await asyncio.wait_for(
                self._call_llm(payload),
                timeout=max(timeout_ms / 1000.0, 1.0),
            )
            duration_s = round(time.monotonic() - t0, 2)
            logger.info(
                "llm_request_completed",
                request_id=request_id,
                agent_id=agent_id,
                duration_s=duration_s,
                output=_short(json.dumps(response, ensure_ascii=False), 240),
            )
            if self._dashboard:
                self._dashboard.publish("llm_result", {
                    "request_id": request_id,
                    "agent_id": agent_id,
                    "status": "completed",
                    "duration_s": duration_s,
                    "output": response,
                })
            await self._bridge.send_llm_response(
                request_id=request_id,
                agent_id=agent_id,
                status="completed",
                output=response,
            )
        except asyncio.CancelledError:
            logger.info("llm_request_cancelled", request_id=request_id, agent_id=agent_id)
            if self._dashboard:
                self._dashboard.publish("llm_result", {
                    "request_id": request_id,
                    "agent_id": agent_id,
                    "status": "cancelled",
                    "error": "Request cancelled",
                })
            await self._bridge.send_llm_response(
                request_id=request_id,
                agent_id=agent_id,
                status="cancelled",
                error="Request cancelled",
            )
        except Exception as exc:
            logger.exception("llm_request_failed", request_id=request_id)
            if self._dashboard:
                self._dashboard.publish("llm_result", {
                    "request_id": request_id,
                    "agent_id": agent_id,
                    "status": "failed",
                    "error": str(exc),
                    "duration_s": round(time.monotonic() - t0, 2),
                })
            await self._bridge.send_llm_response(
                request_id=request_id,
                agent_id=agent_id,
                status="failed",
                error=str(exc),
            )
        finally:
            self._tasks.pop(request_id, None)

    async def _call_llm(self, payload: dict[str, Any]) -> dict[str, Any]:
        system = payload.get("system") or "Return only JSON matching the provided output contract."
        instruction = payload.get("instruction", "")
        observation = payload.get("observation", {})
        output_contract = payload.get("output_contract", {})

        messages = [{
            "role": "user",
            "content": (
                "Instruction:\n"
                f"{instruction}\n\n"
                "Observation JSON:\n"
                f"{json.dumps(observation, ensure_ascii=False)}\n\n"
                "Output contract JSON Schema:\n"
                f"{json.dumps(output_contract.get('schema', output_contract), ensure_ascii=False)}\n\n"
                "Return only a single JSON object. Do not wrap it in markdown."
            ),
        }]

        result = await self._llm.generate(
            system_prompt=system,
            messages=messages,
            tools=[],
        )
        text = result.text.strip()
        if text.startswith("```"):
            text = _strip_code_fence(text)
        parsed = json.loads(text)
        if not isinstance(parsed, dict):
            raise ValueError("LLM output must be a JSON object")
        return parsed

    @staticmethod
    def _dashboard_payload(payload: dict[str, Any], turn: int | None = None) -> dict[str, Any]:
        observation = payload.get("observation", {})
        contract = payload.get("output_contract", {})
        return {
            "turn": turn,
            "request_id": payload.get("request_id", ""),
            "agent_id": payload.get("agent_id", ""),
            "instruction": payload.get("instruction", ""),
            "timeout_ms": payload.get("timeout_ms", 30000),
            "observation": observation,
            "observation_keys": list(observation.keys()) if isinstance(observation, dict) else [],
            "output_contract": contract,
        }


def _strip_code_fence(text: str) -> str:
    lines = text.splitlines()
    if lines and lines[0].startswith("```"):
        lines = lines[1:]
    if lines and lines[-1].startswith("```"):
        lines = lines[:-1]
    return "\n".join(lines).strip()


def _short(value: Any, max_len: int) -> str:
    text = str(value)
    if len(text) <= max_len:
        return text
    return text[: max_len - 1] + "..."
