# Repository Guidelines

## Project Structure & Module Organization

TerraClaw is now a C#-first tModLoader LLM framework. Agent implementations live under `Agents/<AgentName>/`; agent-specific providers may live under `Agents/<AgentName>/Providers/` when reusable components are not enough. Shared C# framework code is in `AI/` and `LLM/`, bridge systems are in `Core/` and `Network/`, reusable extraction helpers are in `Observation/`, and utilities are in `Util/`. The Python worker is under `runtime/src/terraclaw_runtime/`; tests are in `runtime/tests/`; dashboard UI is in `runtime/dashboard/`.

Legacy Python-driven files remain under `runtime/src/terraclaw_runtime/agent/`, `runtime/agents/`, and `AI/BridgeAgent.cs`. Treat these as deprecated unless explicitly working on compatibility.

## Build, Test, and Development Commands

- Compile C# only inside tModLoader using Build + Reload. Do not use `dotnet build` for validation.
- `cd runtime; pip install -e .[dev]`: installs Python runtime and developer tools.
- `cd runtime; pytest`: runs Python tests.
- `cd runtime; ruff check .`: lints Python code.
- `cd runtime; mypy src`: type-checks Python code.
- `cd runtime; python -m terraclaw_runtime.orchestrator`: starts the Python LLM worker.

The bridge listens on `ws://127.0.0.1:9777/bridge`; the dashboard is at `http://127.0.0.1:9090`.

## Coding Style & Naming Conventions

C# targets tModLoader/.NET 8 with nullable reference types and implicit usings enabled. Use 4-space indentation, PascalCase for public types/methods, and camelCase for locals/parameters. Keep agent namespaces aligned with folders, e.g. `TerraClaw.Agents.Guide`. Python requires 3.12+, uses Ruff with 110-character lines, and prefers typed functions.

## Agent Framework Guidelines

New agents should use `LlmBridgeSystem`, `LlmObservation`, `LlmOutput`, and `LlmRequestHandle`. Prefer reusable observation components such as `TerrariaContext.Npc(npc).Basic().Life()`, `TerrariaContext.World().Time()`, and `Context.Custom("mind").Field(...)`; implement `ISymbolicContextProvider` only for custom collection logic. Keep scheduling in C# via heartbeat timers, state-signature changes, player commands, or tModLoader hooks. Never block in `AI()` or hooks; send a request and poll the handle later. See `docs/CSHARP_AGENT_API.md`.

## Testing Guidelines

For C# changes, compile in tModLoader and test in-game. Paste compiler errors back into the task when needed. For Python changes, run `pytest`; add focused tests under `runtime/tests/` for prompt formatting, worker behavior, and message handling.

## Commit & Pull Request Guidelines

Recent commits use short, imperative, lowercase summaries such as `reorganize observation, add scan action`. Keep commits focused. PRs should include affected areas (`Agents`, `LLM`, `runtime`, etc.), tModLoader compile status, Python test commands run, and screenshots/logs for dashboard or in-game behavior.

## Security & Configuration Tips

Do not commit API keys, local secrets, generated databases, or personal runtime config. Use `runtime/config/config.yaml.example` as the template. YAML config has precedence; environment variables only fill missing values.
