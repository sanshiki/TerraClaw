# TerraClaw

TerraClaw is a tModLoader bridge for connecting Terraria mod code to an LLM. It provides model-call plumbing, observation and prompt packaging hooks, structured output contracts, and in-game debugging tools that other systems can build on.

## Architecture

```text
[tModLoader mod code]
  build observation + output contract
  render flat symbolic prompt
  call OpenAI/openai-compatible model
  poll LlmRequestHandle
  apply structured output locally
```

## Project Structure

```text
Agents/
  Guide/                         # Vanilla Guide reactive talking agent
    GuideLlmGlobalNPC.cs
  TerraClaw/                     # Floating demo NPC agent
    ExampleTerraClawAgent.cs
LLM/                             # LLM request/contract/prompt/model framework
Core/, Network/, Event/          # Bridge systems and shared event plumbing
Observation/                     # Reusable observation extractors
UI/                              # In-game LLM dashboard
Util/                            # Shared helpers
docs/                            # API documentation
```

## Quick Start

1. Build + Reload the mod inside tModLoader.
2. Configure an OpenAI/openai-compatible API key.
3. Send an LLM request from mod code, or try the included NPC examples with `/agent ...`.

You can configure the model with environment variables before launching tModLoader:

```powershell
$env:OPENAI_API_KEY = "..."
$env:LLM_MODEL = "gpt-4o-mini"
# Optional for OpenAI-compatible endpoints:
# $env:LLM_API_BASE = "https://api.example.com/v1"
```

The in-game debug dashboard can be opened with `/terraclawdash` or the `Toggle LLM Dashboard` keybind.

You may use `dotnet build` as a quick local C# compile check. It is not the source of truth for this tModLoader mod; still use Build + Reload inside tModLoader and paste compiler errors when debugging C# changes.

## LLM Framework

Mod code calls:

```csharp
LlmBridgeSystem.Instance.Request(
    agentId,
    systemPrompt,
    instruction,
    observation,
    outputContract,
    timeoutMs);
```

The request returns an `LlmRequestHandle`. Callers keep running every frame and poll the handle later:

```csharp
if (handle.TryGetResult(out JsonNode? output)) {
    // apply JSON result locally
}
```

Observations are built with reusable components plus optional custom fields:

```csharp
var observation = LlmObservation.Create()
    .Use(TerrariaContext.Npc(npc).Basic().Life().Home())
    .Use(TerrariaContext.World().Time().Moon().Progression())
    .Use(TerrariaContext.Entities().HostilesNear(npc.Center, 800f, max: 5))
    .Use(Context.Custom("mind", "agent memory")
        .Field("emotion", state.Emotion)
        .Field("trigger", trigger));
```

Use built-in components for common Terraria state, `Context.Custom(...)` for small agent-owned state, and `ISymbolicContextProvider` when a feature needs custom collection logic.

Output contracts are built with `LlmOutput`:

```csharp
var output = LlmOutput.OneOf(
    LlmOutput.Object("talk").String("text", required: true, maxLength: 80),
    LlmOutput.Object("move_to").Number("x", true).Number("y", true)
);
```

`ISymbolicContextProvider` and `LlmOutput` both generate compact prompt metadata automatically. The prompt builder sends the LLM a flat symbolic observation and flat output contract instead of verbose raw JSON.

See [C# Agent API](docs/CSHARP_AGENT_API.md) for implementation details and examples.

## Examples

### Guide Agent

The mod includes examples that connect NPC behavior to an LLM. `Agents/Guide/GuideLlmGlobalNPC.cs` attaches to vanilla `NPCID.Guide` through `GlobalNPC`. It does not override movement or vanilla AI. It only:

- observes Guide/world/nearby hostile state,
- sends low-frequency or state-change LLM requests,
- displays immediate `combat_text`,
- caches `cached_text` for right-click Guide chat,
- maintains a simple `Emotion` state.

### TerraClaw Demo Agent

`Agents/TerraClaw/ExampleTerraClawAgent.cs` is a self-contained `ModNPC` demo. `AgentSpawner` spawns it directly, `/agent ...` sends it one instruction, and its `AI()` method sends a non-blocking LLM request, polls the result, and displays a short `talk` response.

## Configuration

The mod first reads:

```text
Documents\My Games\Terraria\tModLoader\TerraClawConfig.json
```

Example:

```json
{
  "llm": {
    "api_key": "sk-...",
    "model": "gpt-4o-mini",
    "api_base": "",
    "max_tokens": 4096,
    "temperature": 0.3
  }
}
```

Missing fields fall back to environment variables:

- `OPENAI_API_KEY`
- `LLM_MODEL` (default: `gpt-4o-mini`)
- `LLM_API_BASE` for OpenAI-compatible endpoints
- `LLM_MAX_TOKENS`
- `LLM_TEMPERATURE`

## Development Notes

- C# files target tModLoader/.NET 8 conventions.
- Use `dotnet build` for quick C# compile checks, then compile C# changes in tModLoader for final validation.
- Use `/terraclawdash` or the dashboard keybind to inspect recent LLM requests and responses in-game.

## License

MIT
