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
    skill_definitions_path: str = "config/skills"


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
class RuntimeConfig:
    bridge: BridgeConfig = field(default_factory=BridgeConfig)
    llm: LLMConfig = field(default_factory=LLMConfig)
    memory: MemoryConfig = field(default_factory=MemoryConfig)
    skill: SkillConfig = field(default_factory=SkillConfig)
    recovery: RecoveryConfig = field(default_factory=RecoveryConfig)
    sandbox: SandboxConfig = field(default_factory=SandboxConfig)
    replay: ReplayConfig = field(default_factory=ReplayConfig)
    tick_rate_hz: float = 10.0
    llm_call_interval_s: float = 5.0
    log_level: str = "INFO"
    verbose: bool = False

    @classmethod
    def from_yaml(cls, path: str | Path) -> RuntimeConfig:
        path = Path(path)
        if not path.exists():
            return cls()

        with open(path) as f:
            data = yaml.safe_load(f) or {}

        return cls(
            bridge=BridgeConfig(**data.get("bridge", {})),
            llm=LLMConfig(**data.get("llm", {})),
            memory=MemoryConfig(**data.get("memory", {})),
            skill=SkillConfig(**data.get("skill", {})),
            recovery=RecoveryConfig(**data.get("recovery", {})),
            sandbox=SandboxConfig(**data.get("sandbox", {})),
            replay=ReplayConfig(**data.get("replay", {})),
            tick_rate_hz=data.get("tick_rate_hz", 10.0),
            llm_call_interval_s=data.get("llm_call_interval_s", 5.0),
            log_level=data.get("log_level", "INFO"),
            verbose=data.get("verbose", False),
        )

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

    @classmethod
    def from_env(cls) -> RuntimeConfig:
        """Create config with env var overrides only (for backwards compat)."""
        config = cls()
        config.apply_env_overrides()
        return config
