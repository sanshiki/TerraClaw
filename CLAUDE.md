# TerraClaw Development Notes

TerraClaw is now a C#-first tModLoader LLM framework. The old Python/WebSocket runtime path has been removed from the active mod.

## Commands

```powershell
# Final validation path
# Build + Reload inside tModLoader, then test in game
```

The in-game dashboard opens with `/terraclawdash` or the `Toggle LLM Dashboard` keybind. Example TerraClaw agents receive player instructions through `/agent <instruction>`.

## Architecture

```text
[tModLoader C# agent]
  build LlmObservation
  declare LlmOutput
  call LlmBridgeSystem or TerraClawApi.RequestLlm
  validate JSON output
  poll LlmRequestHandle
  read with LlmResult or raw JsonNode
  apply local game behavior
```

Important areas:

- `LLM/` - request bridge, prompt builder, output contracts, validation, result reader, Terraria context components.
- `API/`, `Interfaces/`, `Models/`, `Events/`, `Registries/` - public framework API for other mods.
- `Agents/` - built-in example agents.
- `Core/` - in-game chat commands.
- `UI/` - in-game LLM dashboard and debug log.
- `Observation/` - reusable extractors still used by context providers.

## Agent Pattern

New agents should be normal tModLoader classes such as `ModNPC`, `GlobalNPC`, or `ModSystem`. Keep scheduling in C#, never block in `AI()` or hooks, and poll `LlmRequestHandle` later.

Prefer:

```csharp
if (handle.TryGetResult(out LlmResult result)) {
    string type = result.Type;
}
```

Use raw `JsonNode` parsing only when the result shape is intentionally custom.

## Validation

`LlmOutput` validates model output before a request is marked completed. Invalid output fails the handle and appears in the in-game dashboard with the raw response and validation error.
