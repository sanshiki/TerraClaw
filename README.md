# TerraClaw

TerraClaw is a C#-first tModLoader LLM framework. It lets Terraria mod code build compact observations, declare structured output contracts, send non-blocking model requests, validate model output, and inspect requests in an in-game dashboard.

## Architecture

```text
[tModLoader mod code]
  build LlmObservation + LlmOutput
  render flat symbolic prompt
  call OpenAI/openai-compatible model from C#
  validate structured JSON output
  poll LlmRequestHandle
  apply behavior locally
```

## Project Structure

```text
Agents/                 # Example C# agents
API/, Interfaces/       # Public framework API and contracts
LLM/                    # LLM request, prompt, validation, and result reader framework
Core/                   # In-game commands
Observation/            # Reusable extraction helpers used by context providers
UI/                     # In-game LLM dashboard
Util/                   # Shared helpers
docs/                   # API documentation
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

## LLM Framework

Mod code calls:

```csharp
LlmRequestHandle handle = LlmBridgeSystem.Instance.Request(
    agentId,
    systemPrompt,
    instruction,
    observation,
    outputContract,
    timeoutMs);
```

Requests are non-blocking. Poll the handle later from normal tModLoader hooks:

```csharp
if (handle.TryGetResult(out LlmResult result) && result.Is("talk")) {
    string text = result.String("text");
}
```

The raw JSON path is still available when needed:

```csharp
if (handle.TryGetResult(out JsonNode? output)) {
    // custom parsing
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

Output contracts are built with `LlmOutput`:

```csharp
var output = LlmOutput.OneOf(
    LlmOutput.Object("talk").String("text", required: true, maxLength: 80),
    LlmOutput.Object("move_to").Number("x", true).Number("y", true)
);
```

`LlmOutput` generates prompt metadata and validates completed model output before the request handle is marked completed. Invalid output fails the request with a dashboard-visible validation error.

See [C# Agent API](docs/CSHARP_AGENT_API.md) and [Framework API](docs/FRAMEWORK_API.md) for implementation details and public API usage.

## Examples

### Guide Agent

`Agents/Guide/GuideLlmGlobalNPC.cs` attaches to vanilla `NPCID.Guide` through `GlobalNPC`. It leaves vanilla AI untouched while producing immediate overhead text, cached right-click chat, and a small emotion state.

### TerraClaw Demo Agent

`Agents/TerraClaw/ExampleTerraClawAgent.cs` is a self-contained `ModNPC` demo. `AgentSpawner` spawns it directly, `/agent ...` sends it one instruction, and its `AI()` method sends non-blocking LLM requests, polls results, and applies local actions such as talking, moving, scanning, and breaking tiles.

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
- Final C# validation should be Build + Reload inside tModLoader plus in-game testing.
- Use `/terraclawdash` or the dashboard keybind to inspect recent LLM requests, validation errors, and responses in-game.

## License

MIT

## TODO

- [ ] ReAct paradigm
  - Realtime, interactive, and game-loop friendly rather than a blocking agent loop.
  - Current `callback` mechanism in `ExampleTerraClawAgent` can be a reference.
- [ ] Memory Interface
  - Long/short-term memory storage and compaction.
  - Semantic memory over embeddings.
  - Spatial knowledge graph over world state.
