# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Build C# mod (tModLoader)
dotnet build may be used as a quick local C# compile check. Still remind the user to run Build + Reload inside tModLoader for final validation.

# Install Python runtime
cd runtime && pip install -e .

# Install Python dev dependencies
cd runtime && pip install -e ".[dev]"

# Run Python runtime
cd runtime && python -m terraclaw_runtime.orchestrator

# Run human-mode web UI
cd runtime && python -m terraclaw_runtime.orchestrator --human

# Lint Python
cd runtime && ruff check src/

# Type check Python
cd runtime && mypy src/

# Run Python tests
cd runtime && pytest

# Analyze runtime logs
cd runtime && python scripts/analyze_log.py                                    # default: logs/runtime.log
cd runtime && python scripts/analyze_log.py logs/runtime.log                   # custom path
cd runtime && python scripts/analyze_log.py --turn N                           # Full context of LLM call N (response text, tool calls, results)
cd runtime && python scripts/analyze_log.py --no-timeline                      # Summary only (no full timeline)
cd runtime && python scripts/analyze_log.py --json                             # JSON output

The script parses structlog KeyValueRenderer format and extracts: runtime duration, event counts, LLM call patterns (interval, tokens, finish reasons), tool usage stats, errors/warnings, player instructions, and a full timeline. It also detects common problems: excessive cancel_action calls, stale action_id polling loops, message history bloat, too many follow-up calls, early crashes.

Use `--turn N` before reading raw log — it shows the LLM's response text, tool calls with args, and their results for a single turn, saving context tokens.
```

## Architecture

Two loosely coupled subsystems connected by WebSocket/JSON:

```
[Python Runtime]  ←→  WebSocket ws://127.0.0.1:9777/bridge  ←→  [C# tModLoader Mod]
```

### C# Mod (Terraria/tModLoader)

- **`Core/BridgeModSystem.cs`** — Main entry point. Handles agent registration, action dispatch, WebSocket message routing. Singleton via `Instance`.
- **`Network/`** — WebSocket server, connection manager, message serializer.
- **`AI/`** — NPC agent system:
  - `TerraClawAgent.cs` — Abstract base class: `MoveToward()`, `PlaceTile()`, `BreakTile()`, `Teleport()` etc.
  - `BridgeAgent.cs` — Action queue + dispatch. Has `Execute` methods (`virtual`) for each action type. Base class returns `NOT_IMPLEMENTED`.
  - `ExampleTerraClawAgent.cs` — Default agent. Extends `BridgeAgent`, overrides all `Execute*` methods with real implementations. Created for agent name `"terraclaw"` via factory in `BridgeModSystem`.
  - `TerraClawAgentNPC.cs` — `ModNPC` host. Delegates `AI()` to bound `TerraClawAgent`.
- **`Core/BridgeModSystem.cs:CreateAgent()`** — Factory returning `BridgeAgent` subclass by agent name.
- **`Observation/`** — Game state extraction sent as JSON to runtime.

**Agent registration flow:** Python sends `agent.register {agent_name}` → C# creates `BridgeAgent` subclass via factory → spawns `TerraClawAgentNPC` → responds with `agent_id`. NPC is immortal, no-clip, no gravity.

**Action flow:** Python sends `agent.action {agent_id, action_type, params}` → C# enqueues `PendingAgentAction` → `BridgeAgent.AI()` dequeues and calls `ExecuteAction()` → dispatches to `ExecuteMoveTo()/ExecuteTalk()/etc.` → result sent back via `agent.action.result`.

To add a new action: (1) add case in `ExecuteAction` switch, (2) add `protected virtual` method, (3) override in `ExampleTerraClawAgent`.

### Python Runtime

- **`runtime/src/terraclaw_runtime/orchestrator.py`** — Entry point. Loads config, creates bridge client, starts agent loop or web UI.
- **`runtime/src/terraclaw_runtime/config.py`** — `RuntimeConfig` dataclass. Loads from `config/config.yaml` + env overrides. Path resolution: `get_actions_path()`, `get_identity_path()`, `get_skills_dir()`, `get_system_prompt_path()` — resolves relative to config file location.
- **`runtime/src/terraclaw_runtime/agent/loop.py`** — Agent loop: `observe → think → act`. Calls LLM, handles tool dispatch, manages conversation history.
- **`runtime/src/terraclaw_runtime/agent/prompt_builder.py`** — Assembles LLM context. Loads `system.md` + `identity.md`, formats observations.
- **`runtime/src/terraclaw_runtime/agent/llm.py`** — LLM client wrapper (Anthropic/OpenAI/DeepSeek).
- **`runtime/src/terraclaw_runtime/agent/action_registry.py`** — Loads actions from YAML, generates tool definitions. Internal actions (`get_action_status`, `cancel_action`) are local; others are bridge-sent.
- **`runtime/src/terraclaw_runtime/agent/tool_registry.py`** — Wraps `ActionRegistry` with async handlers. Bridge action handler sends via `BridgeClient` and returns `{status: "started", action_id}`.
- **`runtime/src/terraclaw_runtime/bridge/client.py`** — WebSocket client. `poll_action_result()` checks pending futures then resolved cache. `_resolved_results` dict prevents "always running" bug.
- **`runtime/src/terraclaw_runtime/memory/`** — Spatial, episodic, working memory.
- **`runtime/src/terraclaw_runtime/planning/`** — Plan class for multi-step goals.
- **`runtime/src/terraclaw_runtime/recovery/`** — Monitors for stuck, loop, health, timeout.
- **`runtime/scripts/analyze_log.py`** — Parse `logs/runtime.log` for LLM call analysis.

### Agent Config Structure

```
runtime/
├── config/
│   ├── config.yaml              # Global config (bridge, llm, agent selection)
│   ├── config.yaml.example
│   └── system.md                # Shared system prompt (coordinate system)
└── agents/
    └── <agent-name>/
        ├── identity.md           # Agent personality, rules (optional, appended to system prompt)
        ├── actions.yaml          # Tool/action definitions
        └── skill/                # Lua skills (optional)
```

Set `agent: "<name>"` in `config.yaml`. The factory in `BridgeModSystem.CreateAgent()` must have a matching case.

### Bridge Protocol

Standard JSON envelope: `{id, type, timestamp, session_id, payload}`.

Key message types: `handshake/handshake_ok`, `agent.register/registered`, `agent.action/queued/result`, `agent.observation`, `observation.configure`, `player.chat`, `agent.unregister`.

Observations use hierarchical detail levels: `minimal` / `normal` / `detailed` / `diagnostic`. Tile data is run-length encoded with "interesting" tiles (ores, chests) as structured JSON.

### Action Lifecycle

LLM calls tool → `tool_registry` sends `agent.action` to C# → returns `{"status": "started"}` → C# enqueues and executes → sends `agent.action.result` → Python `poll_action_result()` caches in `_resolved_results`.

Single-turn actions (`ExecutePlaceTile`, `ExecuteTalk`) complete immediately. Multi-turn actions (`ExecuteMoveTo`, `ExecuteWait`) return `null` each tick while running, then return `AgentActionResult` on completion.

### Prompt System

`PromptBuilder.get_system_prompt()` = `config/system.md` (shared) + `agents/<name>/identity.md` (if exists, appended with `\n\n`).

LLM text response = internal reasoning, NOT visible in-game. Must call `talk` tool to speak. Max 80 characters per talk (truncated, not rejected).

### Key Patterns / Gotchas

- **`poll_action_result`** once returned stale "running" for completed actions. Fixed by switching `_resolved_action_ids: set` → `_resolved_results: dict[str, dict]`.
- **Unicode**: `open()` in `action_registry.py` on Windows Chinese systems needs `encoding="utf-8"` to avoid GBK decode errors.
- **ConcurrentQueue** (C#): Action dispatch is thread-safe. `_pendingRegistrations` queues registrations for main-thread processing in `PostUpdateEverything`.
- **Param parsing**: Numeric params go to `PendingAgentAction.Params (Dictionary<string, double>)`, string params to `StringParams (Dictionary<string, string>)`.
- **Log truncation**: LLM response text truncated in debug log at 600 chars, tool_args at 400 chars — these are log-only limits, full content passed through.
