"""Configuration system — loads from YAML + env vars."""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from pathlib import Path

import yaml


@dataclass
class BridgeConfig:
    url: str = "ws://127.0.0.1:9777/bridge"
    shared_secret: str = "terraclaw-dev"
    reconnect_max_delay_s: float = 30.0
    reconnect_base_delay_s: float = 1.0
    heartbeat_interval_s: float = 2.0
    message_timeout_s: float = 10.0


@dataclass
class LLMConfig:
    provider: str = "anthropic"  # "anthropic" | "openai" | "deepseek"
    model: str = "claude-sonnet-4-6"
    api_key: str = ""
    api_base: str = ""  # Custom base URL (e.g. for DeepSeek / proxy)
    max_tokens: int = 4096
    temperature: float = 0.3
    max_context_tokens: int = 8000


@dataclass
class MemoryConfig:
    db_path: str = "data/memory.db"
    vector_store_path: str = "data/vectors"
    working_memory_max_items: int = 100
    spatial_grid_resolution: int = 1  # tiles per cell


@dataclass
class SkillConfig:
    default_timeout_s: float = 120.0
    max_concurrent_skills: int = 1


@dataclass
class RecoveryConfig:
    stuck_movement_threshold_px: float = 30.0
    stuck_time_window_s: float = 10.0
    loop_max_repeats: int = 5
    loop_time_window_s: float = 30.0
    health_ratio_threshold: float = 0.3
    enemy_proximity_threshold_px: float = 200.0
    breath_threshold: int = 50
    default_action_timeout_s: float = 30.0
    default_skill_timeout_s: float = 120.0


@dataclass
class SandboxConfig:
    enabled: bool = False
    docker_image: str = "terraclaw-sandbox:latest"
    max_runtime_s: float = 30.0
    max_output_chars: int = 10000
    max_memory_mb: int = 256
    network_enabled: bool = False


@dataclass
class ReplayConfig:
    enabled: bool = True
    db_path: str = "data/replays"
    record_all_messages: bool = True
    compression_enabled: bool = True


@dataclass
class PromptConfig:
    max_history_messages: int = 10


@dataclass
class RuntimeConfig:
    bridge: BridgeConfig = field(default_factory=BridgeConfig)
    llm: LLMConfig = field(default_factory=LLMConfig)
    memory: MemoryConfig = field(default_factory=MemoryConfig)
    skill: SkillConfig = field(default_factory=SkillConfig)
    recovery: RecoveryConfig = field(default_factory=RecoveryConfig)
    sandbox: SandboxConfig = field(default_factory=SandboxConfig)
    replay: ReplayConfig = field(default_factory=ReplayConfig)
    prompt: PromptConfig = field(default_factory=PromptConfig)
    tick_rate_hz: float = 10.0
    llm_call_interval_s: float = 5.0
    agent_name: str = "terraclaw"
    log_file: str = ""
    log_level: str = "INFO"
    verbose: bool = False
    human_mode: bool = False  # Run with human-in-the-loop web UI instead of LLM
    _config_dir: Path | None = None  # Set by from_yaml() — parent dir of config.yaml

    @classmethod
    def from_yaml(cls, path: str | Path) -> RuntimeConfig:
        path = Path(path)
        if not path.exists():
            return cls()

        with open(path) as f:
            data = yaml.safe_load(f) or {}

        # Pop deprecated path fields so accidental old config doesn't crash
        prompt_data = data.get("prompt", {}).copy()
        prompt_data.pop("prompts_path", None)
        skill_data = data.get("skill", {}).copy()
        skill_data.pop("skill_definitions_path", None)

        config = cls(
            bridge=BridgeConfig(**data.get("bridge", {})),
            llm=LLMConfig(**data.get("llm", {})),
            memory=MemoryConfig(**data.get("memory", {})),
            skill=SkillConfig(**skill_data),
            recovery=RecoveryConfig(**data.get("recovery", {})),
            sandbox=SandboxConfig(**data.get("sandbox", {})),
            replay=ReplayConfig(**data.get("replay", {})),
            prompt=PromptConfig(**prompt_data),
            tick_rate_hz=data.get("tick_rate_hz", 10.0),
            llm_call_interval_s=data.get("llm_call_interval_s", 5.0),
            agent_name=data.get("agent", "terraclaw"),
            log_file=data.get("log_file", ""),
            log_level=data.get("log_level", "INFO"),
            verbose=data.get("verbose", False),
        )
        config._config_dir = path.parent.resolve()
        return config

    def apply_env_overrides(self) -> None:
        """Override fields from environment variables (mutates in place)."""
        if os.getenv("BRIDGE_URL"):
            self.bridge.url = os.environ["BRIDGE_URL"]
        if os.getenv("BRIDGE_SECRET"):
            self.bridge.shared_secret = os.environ["BRIDGE_SECRET"]
        if os.getenv("ANTHROPIC_API_KEY"):
            self.llm.api_key = os.environ["ANTHROPIC_API_KEY"]
            self.llm.provider = "anthropic"
        if os.getenv("OPENAI_API_KEY"):
            self.llm.api_key = os.environ["OPENAI_API_KEY"]
            self.llm.provider = "openai"
        if os.getenv("DEEPSEEK_API_KEY"):
            self.llm.api_key = os.environ["DEEPSEEK_API_KEY"]
            self.llm.provider = "deepseek"
            if not os.getenv("LLM_API_BASE") and "api_base" not in os.environ:
                self.llm.api_base = "https://api.deepseek.com"
        if os.getenv("LLM_API_BASE"):
            self.llm.api_base = os.environ["LLM_API_BASE"]
        if os.getenv("LLM_MODEL"):
            self.llm.model = os.environ["LLM_MODEL"]
        if os.getenv("LOG_LEVEL"):
            self.log_level = os.environ["LOG_LEVEL"]

    def get_system_prompt_path(self) -> str:
        """Resolve path to the shared system prompt."""
        return str(self._config_dir / "system.md")

    def get_identity_path(self) -> str:
        """Resolve path to the active agent's identity file."""
        return str(self._config_dir.parent / "agents" / self.agent_name / "identity.md")

    def get_actions_path(self) -> str:
        """Resolve path to the active agent's action definitions."""
        return str(self._config_dir.parent / "agents" / self.agent_name / "actions.yaml")

    def get_skills_dir(self) -> str:
        """Resolve path to the active agent's Lua skills directory."""
        return str(self._config_dir.parent / "agents" / self.agent_name / "skill")

    def get_memory_db_path(self) -> str:
        """Resolve memory database path relative to the runtime root."""
        path = Path(self.memory.db_path)
        if path.is_absolute():
            return str(path)
        return str(self._config_dir.parent / path)

    @classmethod
    def from_env(cls) -> RuntimeConfig:
        """Create config with env var overrides only (for backwards compat)."""
        config = cls()
        config.apply_env_overrides()
        return config
