# TerraClaw API Construction Notes

TerraClaw's public API is intentionally small. The framework exposes request construction, agent metadata, request lifecycle events, output validation, and result reading. It does not expose a global action registry or runtime loop.

## Public Surface

Stable public areas:

- `TerraClaw.API` - `TerraClawApi` entry point.
- `TerraClaw.Interfaces` - public contracts such as `ILlmAgentDefinition`.
- `TerraClaw.Models` - immutable metadata snapshots.
- `TerraClaw.Events` - public event args.
- `TerraClaw.Registries` - validated metadata registries.
- documented `TerraClaw.LLM` types: `LlmObservation`, `LlmOutput`, `LlmRequestHandle`, and `LlmResult`.

Everything else should be treated as implementation detail unless documented in `FRAMEWORK_API.md`.

## Request Shape

```csharp
LlmRequestHandle handle = api.RequestLlm(
    agentId,
    systemPrompt,
    instruction,
    observation,
    output,
    timeoutMs);
```

The caller owns scheduling and game behavior. TerraClaw owns prompt packaging, model dispatch, output validation, dashboard logging, and the pollable handle.

## Output Validation

`LlmOutput` defines TerraClaw output semantics:

- `Object` returns one typed object.
- `OneOf` returns exactly one typed object.
- `AnyOf` returns one typed object, an array of typed objects, an `outputs` array, or a keyed aggregate object.
- `AllOf` accepts aggregate forms and requires every declared branch.

Validation errors fail the handle and are visible in the in-game dashboard.

## Result Reading

Use `LlmResult` for lightweight field reads:

```csharp
if (handle.TryGetResult(out LlmResult result) && result.Is("say"))
    Main.NewText(result.String("text"));
```

For aggregate outputs:

```csharp
if (result.TryGetBranch("cached_text", out var branch))
    state.CachedChatText = branch.String("text");
```
