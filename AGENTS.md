# Repository Guidelines

## Project Structure & Module Organization

TerraClaw is now a C#-first tModLoader LLM framework. Agent implementations live under `Agents/<AgentName>/`; agent-specific providers may live under `Agents/<AgentName>/Providers/` when reusable components are not enough. Shared C# LLM framework code is in `LLM/`, bridge systems are in `Core/` and `Network/`, reusable extraction helpers are in `Observation/`, and utilities are in `Util/`. The Python worker is under `runtime/src/terraclaw_runtime/`; tests are in `runtime/tests/`; dashboard UI is in `runtime/dashboard/`.

Legacy Python-driven files remain under `runtime/src/terraclaw_runtime/agent/` and `runtime/agents/`. Treat these as deprecated unless explicitly working on compatibility.

## Build, Test, and Development Commands

- `dotnet build` may be used as a quick local C# compile check. Still remind the user to run Build + Reload inside tModLoader for final validation.
- `cd runtime; pip install -e .[dev]`: installs Python runtime and developer tools.
- `cd runtime; pytest`: runs Python tests.
- `cd runtime; ruff check .`: lints Python code.
- `cd runtime; mypy src`: type-checks Python code.
- `cd runtime; python -m terraclaw_runtime.orchestrator`: starts the Python LLM worker.

The bridge listens on `ws://127.0.0.1:9777/bridge`; the dashboard is at `http://127.0.0.1:9090`.

## Coding Style & Naming Conventions

C# targets tModLoader/.NET 8 with nullable reference types and implicit usings enabled. Use 4-space indentation, PascalCase for public types/methods, and camelCase for locals/parameters. Keep agent namespaces aligned with folders, e.g. `TerraClaw.Agents.Guide`. Python requires 3.12+, uses Ruff with 110-character lines, and prefers typed functions.

## Agent Framework Guidelines

New agents should be normal `ModNPC`, `GlobalNPC`, `ModSystem`, or other tModLoader classes that call `LlmBridgeSystem` directly. Use `LlmObservation`, `LlmOutput`, and `LlmRequestHandle`. Prefer reusable observation components such as `TerrariaContext.Npc(npc).Basic().Life()`, `TerrariaContext.World().Time()`, and `Context.Custom("mind").Field(...)`; implement `ISymbolicContextProvider` only for custom collection logic. Keep scheduling in C# via heartbeat timers, state-signature changes, player commands, or tModLoader hooks. Never block in `AI()` or hooks; send a request and poll the handle later. See `docs/CSHARP_AGENT_API.md`.

## Testing Guidelines

For C# changes, `dotnet build` is acceptable for a fast local compile check, but final validation should still be Build + Reload inside tModLoader plus in-game testing. Paste compiler errors back into the task when needed. For Python changes, run `pytest`; add focused tests under `runtime/tests/` for prompt formatting, worker behavior, and message handling.

## Commit & Pull Request Guidelines

Recent commits use short, imperative, lowercase summaries such as `reorganize observation, add scan action`. Keep commits focused. PRs should include affected areas (`Agents`, `LLM`, `runtime`, etc.), tModLoader compile status, Python test commands run, and screenshots/logs for dashboard or in-game behavior.

## Security & Configuration Tips

Do not commit API keys, local secrets, generated databases, or personal runtime config. Use `runtime/config/config.yaml.example` as the template. YAML config has precedence; environment variables only fill missing values.
