"""Action registry — loads atomic action definitions from YAML and manages capabilities."""

from __future__ import annotations

from pathlib import Path
from typing import Any

import yaml


class ActionRegistry:
    """Registry of atomic action definitions loaded from a YAML file.

    Each action definition specifies its LLM-facing description, parameters,
    bridge action type, and whether it's currently enabled.
    """

    def __init__(self, path: str | Path):
        self._path = Path(path)
        self._actions: dict[str, ActionDef] = {}

    def load(self) -> None:
        """Load action definitions from the YAML file."""
        if not self._path.exists():
            return

        with open(self._path) as f:
            data = yaml.safe_load(f) or {}

        for name, cfg in data.items():
            self._actions[name] = ActionDef(
                name=name,
                description=cfg.get("description", ""),
                enabled=cfg.get("enabled", True),
                parameters=cfg.get("parameters", {"type": "object", "properties": {}}),
                bridge_action=cfg.get("bridge_action", name),
            )

    @property
    def names(self) -> list[str]:
        return list(self._actions.keys())

    def get(self, name: str) -> ActionDef | None:
        return self._actions.get(name)

    def is_enabled(self, name: str) -> bool:
        act = self._actions.get(name)
        return act is not None and act.enabled

    def enable(self, name: str) -> None:
        act = self._actions.get(name)
        if act:
            act.enabled = True

    def disable(self, name: str) -> None:
        act = self._actions.get(name)
        if act:
            act.enabled = False

    def get_enabled(self) -> dict[str, ActionDef]:
        return {n: a for n, a in self._actions.items() if a.enabled}

    def get_definitions(self) -> list[dict[str, Any]]:
        """Return LLM tool definitions for all enabled actions."""
        result = []
        for act in self._actions.values():
            if not act.enabled:
                continue
            result.append({
                "type": "function",
                "function": {
                    "name": act.name,
                    "description": act.description,
                    "parameters": act.parameters,
                },
            })
        return result

    def get_action(self, name: str) -> str | None:
        """Get the bridge action_type string for a named action."""
        act = self._actions.get(name)
        if act is None or not act.enabled:
            return None
        return act.bridge_action


class ActionDef:
    """Definition of a single atomic action."""

    def __init__(
        self,
        name: str,
        description: str,
        enabled: bool,
        parameters: dict[str, Any],
        bridge_action: str,
    ):
        self.name = name
        self.description = description
        self.enabled = enabled
        self.parameters = parameters
        self.bridge_action = bridge_action
