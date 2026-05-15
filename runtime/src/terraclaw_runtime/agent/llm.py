"""LLM client supporting Anthropic and OpenAI providers."""

from __future__ import annotations

import json
import time
from typing import Any

import structlog
from anthropic import Anthropic, AsyncAnthropic
from openai import AsyncOpenAI

from terraclaw_runtime._debug import dprint
from terraclaw_runtime.config import LLMConfig

logger = structlog.get_logger()


class LLMClient:
    """Multi-provider LLM client for the agent loop."""

    def __init__(self, config: LLMConfig):
        self._config = config
        self._anthropic: AsyncAnthropic | None = None
        self._openai: AsyncOpenAI | None = None

    async def _get_client(self):
        if self._config.provider == "anthropic":
            if self._anthropic is None:
                kwargs = {"api_key": self._config.api_key}
                if self._config.api_base:
                    kwargs["base_url"] = self._config.api_base
                self._anthropic = AsyncAnthropic(**kwargs)
            return "anthropic"
        elif self._config.provider in ("openai", "deepseek"):
            if self._openai is None:
                kwargs = {"api_key": self._config.api_key}
                if self._config.api_base:
                    kwargs["base_url"] = self._config.api_base
                self._openai = AsyncOpenAI(**kwargs)
            return "openai"
        raise ValueError(f"Unknown provider: {self._config.provider}")

    async def generate(
        self,
        system_prompt: str,
        messages: list[dict[str, Any]],
        tools: list[dict[str, Any]],
        max_tokens: int | None = None,
    ) -> LLMResponse:
        """Send a request to the LLM and return a structured response."""
        provider = await self._get_client()
        max_tok = max_tokens or self._config.max_tokens

        if provider == "anthropic":
            return await self._call_anthropic(system_prompt, messages, tools, max_tok)
        else:
            return await self._call_openai(system_prompt, messages, tools, max_tok)

    async def _call_anthropic(
        self,
        system_prompt: str,
        messages: list[dict[str, Any]],
        tools: list[dict[str, Any]],
        max_tokens: int,
    ) -> LLMResponse:
        assert self._anthropic is not None

        # Convert tools to Anthropic format
        anthropic_tools = []
        for tool in tools:
            anthropic_tools.append({
                "name": tool["name"],
                "description": tool.get("description", ""),
                "input_schema": {
                    "type": "object",
                    "properties": tool.get("parameters", {}).get("properties", {}),
                    "required": tool.get("parameters", {}).get("required", []),
                },
            })

        response = await self._anthropic.messages.create(
            model=self._config.model,
            system=system_prompt,
            messages=messages,
            tools=anthropic_tools,
            max_tokens=max_tokens,
            temperature=self._config.temperature,
        )

        return self._parse_anthropic_response(response)

    @staticmethod
    def _to_openai_message(msg: dict[str, Any]) -> dict[str, Any]:
        """Convert Anthropic-style message to OpenAI-format message."""
        role = msg.get("role", "user")
        content = msg.get("content")

        # Convert tool_result: {"role": "user", "content": [{"type": "tool_result", "tool_use_id": id, "content": str}]}
        if isinstance(content, list):
            for block in content:
                if isinstance(block, dict) and block.get("type") == "tool_result":
                    return {
                        "role": "tool",
                        "tool_call_id": block.get("tool_use_id", ""),
                        "content": block.get("content", ""),
                    }

        # Convert assistant messages — OpenAI expects tool_calls separate from content
        if role == "assistant" and isinstance(content, list):
            text_parts = []
            tool_calls = []
            for block in content:
                if isinstance(block, dict):
                    if block.get("type") == "text":
                        text_parts.append(block.get("text", ""))
                    elif block.get("type") == "tool_use":
                        tool_calls.append({
                            "id": block.get("id", ""),
                            "type": "function",
                            "function": {
                                "name": block.get("name", ""),
                                "arguments": json.dumps(block.get("input", {})),
                            },
                        })
            result = {"role": "assistant"}
            text = "\n".join(text_parts)
            if text:
                result["content"] = text
            if tool_calls:
                result["tool_calls"] = tool_calls
            return result

        # Default pass-through
        if isinstance(content, list):
            texts = []
            for block in content:
                if isinstance(block, dict) and block.get("type") == "text":
                    texts.append(block.get("text", ""))
            return {"role": role, "content": "\n".join(texts)}

        return {"role": role, "content": content or ""}

    async def _call_openai(
        self,
        system_prompt: str,
        messages: list[dict[str, Any]],
        tools: list[dict[str, Any]],
        max_tokens: int,
    ) -> LLMResponse:
        assert self._openai is not None

        dprint("[LLM]",f"API call: model={self._config.model}, messages={len(messages)}, tools={len(tools)}")
        t0 = time.monotonic()

        openai_messages = [{"role": "system", "content": system_prompt}]
        for msg in messages:
            openai_messages.append(self._to_openai_message(msg))

        response = await self._openai.chat.completions.create(
            model=self._config.model,
            messages=openai_messages,
            tools=tools,
            max_tokens=max_tokens,
            temperature=self._config.temperature,
        )

        elapsed = time.monotonic() - t0
        parsed = self._parse_openai_response(response)
        dprint("[LLM]",f"API response in {elapsed:.1f}s — text={len(parsed.text)} chars, tools={len(parsed.tool_calls)}, "
                     f"in_tokens={parsed.usage.input_tokens}, out_tokens={parsed.usage.output_tokens}")
        return parsed

    def _parse_anthropic_response(self, response: Any) -> LLMResponse:
        content_blocks = response.content
        text_parts = []
        tool_calls = []

        for block in content_blocks:
            if block.type == "text":
                text_parts.append(block.text)
            elif block.type == "tool_use":
                tool_calls.append(ToolCall(
                    id=block.id,
                    name=block.name,
                    arguments=block.input if isinstance(block.input, dict) else {},
                ))

        return LLMResponse(
            text="\n".join(text_parts),
            tool_calls=tool_calls,
            stop_reason=getattr(response, "stop_reason", "end_turn"),
            usage=LLMUsage(
                input_tokens=getattr(response.usage, "input_tokens", 0),
                output_tokens=getattr(response.usage, "output_tokens", 0),
            ),
        )

    def _parse_openai_response(self, response: Any) -> LLMResponse:
        choice = response.choices[0]
        message = choice.message
        tool_calls = []

        text = message.content or ""

        if message.tool_calls:
            for tc in message.tool_calls:
                import json
                try:
                    args = json.loads(tc.function.arguments)
                except json.JSONDecodeError:
                    args = {}
                tool_calls.append(ToolCall(
                    id=tc.id,
                    name=tc.function.name,
                    arguments=args,
                ))

        return LLMResponse(
            text=text,
            tool_calls=tool_calls,
            stop_reason=choice.finish_reason or "stop",
            usage=LLMUsage(
                input_tokens=response.usage.prompt_tokens if response.usage else 0,
                output_tokens=response.usage.completion_tokens if response.usage else 0,
            ),
        )


from dataclasses import dataclass, field


@dataclass
class ToolCall:
    id: str = ""
    name: str = ""
    arguments: dict[str, Any] = field(default_factory=dict)


@dataclass
class LLMUsage:
    input_tokens: int = 0
    output_tokens: int = 0


@dataclass
class LLMResponse:
    text: str = ""
    tool_calls: list[ToolCall] = field(default_factory=list)
    stop_reason: str = ""
    usage: LLMUsage = field(default_factory=LLMUsage)

    @property
    def is_tool_calls(self) -> bool:
        return len(self.tool_calls) > 0
