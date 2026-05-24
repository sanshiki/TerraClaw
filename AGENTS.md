# Repository Guidelines

## Project Structure & Module Organization

TerraClaw is split between a C# tModLoader bridge mod and a Python agent runtime. The mod entry point is `TerraClaw.cs`; bridge systems live in `Core/`, WebSocket transport in `Network/`, game state extraction in `Observation/`, agents in `AI/`, items/textures in `Items/`, skills in `Skill/`, and utilities in `Util/`. JSON contracts are in `protocol/schemas/`. The Python runtime is under `runtime/src/terraclaw_runtime/`, with config in `runtime/config/`, agent definitions in `runtime/agents/`, web UI files in `runtime/webui/`, and tests in `runtime/tests/`.

## Build, Test, and Development Commands

- `dotnet build TerraClaw.csproj -c Release`: builds the tModLoader mod with .NET 8.
- `dotnet build TerraClaw.csproj`: faster debug build during mod development.
- `cd runtime; pip install -e .[dev]`: installs the Python runtime and developer tools.
- `cd runtime; pytest`: runs Python tests configured by `runtime/pyproject.toml`.
- `cd runtime; ruff check .`: lints Python code using the repo's Ruff settings.
- `cd runtime; mypy src`: type-checks the Python runtime in strict mode.

Run Terraria through tModLoader for in-game checks. The bridge listens on `ws://127.0.0.1:9777/bridge`.

## Coding Style & Naming Conventions

C# targets `net8.0` with nullable reference types and implicit usings enabled. Use 4-space indentation, PascalCase for public types/methods, camelCase for locals/parameters, and keep namespaces aligned with top-level folders. Python requires 3.12+, uses Ruff with a 110-character line length, and mypy strict mode; prefer typed functions, snake_case modules/functions, and PascalCase classes.

## Testing Guidelines

Current automated coverage is minimal; `runtime/tests/` contains only scaffolding. Add Python tests as `test_*.py` files, especially for message parsing, memory, skills, recovery monitors, and bridge client behavior. For C# changes, verify with `dotnet build` and in-game tModLoader testing. When schemas change, update serializers, runtime message models, and tests together.

## Commit & Pull Request Guidelines

Recent commits use short, imperative, lowercase summaries such as `reorganize observation, add scan action` and `complete memory, add dashboard`. Keep commits focused on one change area. Pull requests should include a concise description, affected areas (`Core`, `Network`, `runtime`, `protocol`, etc.), test/build commands run, linked issues when relevant, and screenshots or logs for UI, dashboard, or in-game behavior changes.

## Security & Configuration Tips

Do not commit API keys, local secrets, generated databases, or personal runtime config. Use `runtime/config/config.yaml.example` as the template for local configuration. Treat `runtime/data/memory.db`, `bin/`, `obj/`, and local tModLoader outputs as generated artifacts unless a change is intentional.
