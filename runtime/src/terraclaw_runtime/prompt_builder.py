"""Prompt construction for C#-first LLM requests."""

from __future__ import annotations

import json
from typing import Any


class FlatSymbolicPromptBuilder:
    """Build prompts from symbolic observation data and generated legends."""

    @staticmethod
    def build(
        *,
        system: str,
        instruction: str,
        observation: dict[str, Any],
        symbolic_observation: dict[str, Any] | None,
        output_contract: dict[str, Any],
    ) -> tuple[str, list[dict[str, str]]]:
        symbolic = symbolic_observation or _fallback_symbolic(observation)
        data = symbolic.get("data", symbolic)
        legend = symbolic.get("legend", {})
        docs = symbolic.get("docs", {})
        conventions = symbolic.get("conventions", {})

        system_prompt = "\n\n".join(part for part in [
            system.strip(),
            _format_return_rule(output_contract),
            _format_observation_legend(legend, docs, conventions),
            _format_output_contract(output_contract),
        ] if part)

        contract_payload = _contract_payload_for_user(output_contract)
        messages = [{
            "role": "user",
            "content": (
                "Instruction:\n"
                f"{instruction}\n\n"
                "Observation:\n"
                f"{json.dumps(data, ensure_ascii=False, separators=(',', ':'))}\n\n"
                "Output contract:\n"
                f"{contract_payload}"
            ),
        }]
        return system_prompt, messages


def _format_observation_legend(
    legend: dict[str, Any],
    docs: dict[str, Any],
    conventions: dict[str, Any],
) -> str:
    if not legend:
        return ""

    lines = ["Observation uses flat symbolic JSON:"]
    for symbol, fields in legend.items():
        if isinstance(fields, list):
            lines.append(f"{symbol}=[{','.join(str(f) for f in fields)}]")
        else:
            lines.append(f"{symbol}={fields}")

    if conventions:
        lines.append("")
        lines.append("Conventions:")
        for key, value in conventions.items():
            lines.append(f"{key}: {value}")

    compact_docs = _compact_docs(docs)
    if compact_docs:
        lines.append("")
        lines.append("Field docs:")
        lines.extend(compact_docs)

    return "\n".join(lines)


def _format_output_contract(output_contract: dict[str, Any]) -> str:
    flat = output_contract.get("flat")
    if isinstance(flat, dict) and flat.get("choices"):
        return _format_output_flat(flat)
    return _format_output_legend(output_contract.get("legend", []))


def _format_return_rule(output_contract: dict[str, Any]) -> str:
    flat = output_contract.get("flat")
    mode = flat.get("mode") if isinstance(flat, dict) else None
    if mode == "any":
        return "Return only JSON matching the output contract. For multiple outputs, return an array of JSON objects. Do not wrap it in markdown."
    return "Return only a single JSON object matching the output contract. Do not wrap it in markdown."


def _contract_payload_for_user(output_contract: dict[str, Any]) -> str:
    flat = output_contract.get("flat")
    if isinstance(flat, dict) and flat.get("choices"):
        return _compact_flat_contract(flat)
    legend = output_contract.get("legend")
    if legend:
        return json.dumps(legend, ensure_ascii=False, separators=(",", ":"))
    schema = output_contract.get("schema", output_contract)
    return json.dumps(schema, ensure_ascii=False, separators=(",", ":"))


def _compact_flat_contract(flat: dict[str, Any]) -> str:
    parts = [f"mode={flat.get('mode', 'object')}"]
    choices = []
    for choice in flat.get("choices", []):
        if not isinstance(choice, dict):
            continue
        fields = choice.get("fields", [])
        rendered = [_render_flat_field(f) for f in fields if isinstance(f, dict)]
        output_type = choice.get("type", "?")
        choices.append(f"{output_type}({';'.join(rendered)})")
    if choices:
        parts.append("choices=" + "|".join(choices))
    return "\n".join(parts)


def _format_output_flat(flat: dict[str, Any]) -> str:
    mode = flat.get("mode", "object")
    mode_label = {
        "one": "choose exactly one",
        "any": "choose one or more",
        "all": "include all",
        "object": "return",
    }.get(str(mode), str(mode))

    lines = ["Output contract:", f"{mode_label}:"]
    type_names: list[str] = []
    for choice in flat.get("choices", []):
        if not isinstance(choice, dict):
            continue
        output_type = str(choice.get("type", "?"))
        type_names.append(output_type)
        fields = choice.get("fields", [])
        rendered = []
        for field in fields:
            if isinstance(field, dict):
                rendered.append(_render_flat_field(field))
        body = ",".join(rendered)
        lines.append(f"{output_type}={{type:\"{output_type}\"{(',' + body) if body else ''}}}")

    if type_names:
        lines.append(f"Required selector: type in [{','.join(type_names)}]")
    lines.append("Field suffix: !=required, ?=optional.")
    return "\n".join(lines)


def _render_flat_field(field: dict[str, Any]) -> str:
    name = field.get("name", "?")
    field_type = _short_type(str(field.get("type", "any")))
    required = "!" if field.get("required") else "?"
    parts = [f"{name}:{field_type}{required}"]
    if "max" in field:
        parts.append(f"max={field['max']}")
    if "default" in field:
        parts.append(f"default={field['default']}")
    return " ".join(parts)


def _short_type(field_type: str) -> str:
    return {
        "string": "str",
        "number": "num",
        "integer": "int",
        "boolean": "bool",
    }.get(field_type, field_type)


def _format_output_legend(legend: list[Any]) -> str:
    if not legend:
        return ""

    lines = ["Output choices:"]
    for item in legend:
        if not isinstance(item, dict):
            continue
        output_type = item.get("type", "?")
        fields = item.get("fields", [])
        field_names = []
        for field in fields:
            if not isinstance(field, dict):
                continue
            suffix = "" if field.get("required") else "?"
            field_names.append(f"{field.get('name', '?')}{suffix}")
        lines.append(f"{output_type}={{type:\"{output_type}\"{(',' + ','.join(field_names)) if field_names else ''}}}")
    return "\n".join(lines)


def _compact_docs(docs: dict[str, Any]) -> list[str]:
    lines: list[str] = []
    for symbol, doc in docs.items():
        if isinstance(doc, str):
            lines.append(f"{symbol}: {doc}")
            continue
        if not isinstance(doc, dict):
            continue
        desc = doc.get("description", "")
        if desc:
            lines.append(f"{symbol}: {desc}")
        fields = doc.get("fields", {})
        if isinstance(fields, dict):
            for name, field_desc in fields.items():
                if field_desc:
                    lines.append(f"{symbol}.{name}: {field_desc}")
    return lines


def _fallback_symbolic(observation: dict[str, Any]) -> dict[str, Any]:
    return {
        "data": {"x": observation},
        "legend": {"x": list(observation.keys())},
        "docs": {"x": "uncompressed observation fields"},
    }
