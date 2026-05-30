# TerraClaw

TerraClaw is a tModLoader framework for connecting Terraria NPC behavior to an LLM. The current architecture is **C#-first**: Terraria/tModLoader code owns agent scheduling, observation construction, output contracts, and in-game behavior. The Python runtime is a generic LLM worker that receives requests, calls the configured model, and returns structured JSON.

## Current Architecture

```text
[C# tModLoader agents]
  build observation + output contract
  send llm.request
        |
        v
[Python LLM worker]
  renders flat symbolic prompt
  calls model
  sends llm.response
        |
        v
[C# agents]
  poll LlmRequestHandle
  apply structured output locally
```

The bridge uses WebSocket JSON messages on `ws://127.0.0.1:9777/bridge`.

## Project Structure

```text
Agents/
  Guide/                         # Vanilla Guide reactive talking agent
    GuideLlmGlobalNPC.cs
    Providers/
      GuideContextProviders.cs
  TerraClaw/                     # Floating demo NPC agent
    ExampleTerraClawAgent.cs
LLM/                             # C# LLM request/contract framework
Core/, Network/, Event/          # tModLoader bridge systems
Observation/                     # Legacy/default spatial/entity extractors
runtime/src/terraclaw_runtime/   # Python LLM worker and prompt builder
runtime/dashboard/               # Live request/result dashboard
protocol/schemas/                # Older protocol schema references
```

## Quick Start

1. Build + Reload the mod inside tModLoader.
2. Configure `runtime/config/config.yaml` with your LLM provider, model, and API key.
3. Start the Python worker:

```powershell
cd runtime
python -m terraclaw_runtime.orchestrator
```

4. Open the dashboard:

```text
http://127.0.0.1:9090
```

Do not use `dotnet build` as the source of truth for this tModLoader mod. Compile in tModLoader and paste compile errors when debugging C# changes.

## C#-First LLM Framework

C# agents call:

```csharp
LlmBridgeSystem.Instance.Request(
    agentId,
    systemPrompt,
    instruction,
    observation,
    outputContract,
    timeoutMs);
```

The request returns an `LlmRequestHandle`. Agents keep running every frame and poll the handle later:

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

`ISymbolicContextProvider` and `LlmOutput` both generate compact prompt metadata automatically. The Python worker sends the LLM a flat symbolic observation and flat output contract instead of verbose raw JSON.

See [C# Agent API](docs/CSHARP_AGENT_API.md) for implementation details and the process for adding an agent.

## Built-In Agents

### Guide Agent

`Agents/Guide/GuideLlmGlobalNPC.cs` attaches to vanilla `NPCID.Guide` through `GlobalNPC`. It does not override movement or vanilla AI. It only:

- observes Guide/world/nearby hostile state,
- sends low-frequency or state-change LLM requests,
- displays immediate `combat_text`,
- caches `cached_text` for right-click Guide chat,
- maintains a simple `Emotion` state.

### TerraClaw Demo Agent

`Agents/TerraClaw/ExampleTerraClawAgent.cs` is a self-contained `ModNPC` demo. `AgentSpawner` spawns it directly, `/agent ...` sends it one instruction, and its `AI()` method sends a non-blocking LLM request, polls the result, and displays a short `talk` response.

## Legacy Systems

The following systems still exist but are **legacy/deprecated** relative to the current C#-first framework:

- `runtime/src/terraclaw_runtime/agent/loop.py`
- `runtime/agents/terraclaw/actions.yaml`
- `agent.register`, `agent.observation`, `agent.action`, `agent.action.result`
- Python-side planning/memory/recovery/skills loop

They may still be useful as references, but new work should prefer `LlmBridgeSystem`, `LlmObservation`, `ISymbolicContextProvider`, `LlmOutput`, and `LlmRequestHandle`.

## Python Runtime

The active Python path is:

- `llm_worker.py`: listens for `llm.request`, calls the model, returns `llm.response`
- `prompt_builder.py`: builds flat symbolic prompts
- `dashboard/`: publishes live request/result events

Useful commands:

```powershell
cd runtime
pip install -e .[dev]
pytest
python -m terraclaw_runtime.orchestrator
```

## Configuration

Config is loaded from `runtime/config/config.yaml`. YAML has precedence; environment variables fill only missing values. Key fields:

- `llm.provider`
- `llm.model`
- `llm.api_key`
- `llm.api_base`
- `bridge.url`

## Development Notes

- C# files target tModLoader/.NET 8 conventions.
- Compile C# changes in tModLoader.
- Python tests live in `runtime/tests/`.
- Dashboard is available at `http://127.0.0.1:9090` while the runtime is running.

## Todos

- [ ] Add TileContextBuilder and world memory: Convert surrounding tiles to a sematic mapping, including overall topology and specific interesting tiles.

## License

MIT
