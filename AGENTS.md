# Repository Guidelines

## Project Structure & Module Organization

TerraClaw is a C#-first tModLoader LLM framework. Agent implementations live under `Agents/<AgentName>/`; agent-specific providers may live under `Agents/<AgentName>/Providers/` when reusable components are not enough. Shared C# LLM framework code is in `LLM/`, public API code is in `API/`, `Interfaces/`, `Models/`, `Events/`, and `Registries/`, in-game commands are in `Core/`, reusable extraction helpers are in `Observation/`, dashboard UI is in `UI/`, and utilities are in `Util/`.

Legacy Python/WebSocket runtime code has been removed from the active mod path. Prefer the C# request API and in-game dashboard.

## Build, Test, and Development Commands

- Build + Reload inside tModLoader is the final C# validation path.
- Paste compiler errors back into the task when debugging C# changes.

The in-game dashboard opens with `/terraclawdash` or the `Toggle LLM Dashboard` keybind.

## Coding Style & Naming Conventions

C# targets tModLoader/.NET 8 with nullable reference types and implicit usings enabled. Use 4-space indentation, PascalCase for public types/methods, and camelCase for locals/parameters. Keep agent namespaces aligned with folders, e.g. `TerraClaw.Agents.Guide`.

## Agent Framework Guidelines

New agents should be normal `ModNPC`, `GlobalNPC`, `ModSystem`, or other tModLoader classes that call `LlmBridgeSystem` or `TerraClawApi.RequestLlm` directly. Use `LlmObservation`, `LlmOutput`, `LlmRequestHandle`, and `LlmResult`. Prefer reusable observation components such as `TerrariaContext.Npc(npc).Basic().Life()`, `TerrariaContext.World().Time()`, and `Context.Custom("mind").Field(...)`; implement `ISymbolicContextProvider` only for custom collection logic. Keep scheduling in C# via heartbeat timers, state-signature changes, player commands, or tModLoader hooks. Never block in `AI()` or hooks; send a request and poll the handle later. See `docs/CSHARP_AGENT_API.md`.

## Testing Guidelines

For C# changes, final validation should be Build + Reload inside tModLoader plus in-game testing. Test malformed LLM output through the dashboard when changing output contracts or validation behavior.

## Commit & Pull Request Guidelines

Recent commits use short, imperative, lowercase summaries such as `reorganize observation, add scan action`. Keep commits focused. PRs should include affected areas (`Agents`, `LLM`, etc.), tModLoader compile status, and screenshots/logs for dashboard or in-game behavior.

## Security & Configuration Tips

Do not commit API keys, local secrets, generated databases, or personal runtime config. Use `TerraClawConfig.json` or environment variables for local LLM configuration.
