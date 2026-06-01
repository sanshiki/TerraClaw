# TerraClaw

TerraClaw is a tModLoader framework for connecting Terraria NPC behavior to an LLM. The current architecture is **C#-first**: Terraria/tModLoader code owns agent scheduling, observation construction, prompt rendering, model calls, output contracts, and in-game behavior.

## Current Architecture

```text
[C# tModLoader agents]
  build observation + output contract
  renders flat symbolic prompt
  calls OpenAI/openai-compatible model
  poll LlmRequestHandle
  apply structured output locally
```

The WebSocket bridge on `ws://127.0.0.1:9777/bridge` remains available for legacy tools and dashboards, but it is no longer required for LLM requests.

## Project Structure

```text
Agents/
  Guide/                         # Vanilla Guide reactive talking agent
    GuideLlmGlobalNPC.cs
    Providers/
      GuideContextProviders.cs
  TerraClaw/                     # Floating demo NPC agent
    ExampleTerraClawAgent.cs
LLM/                             # C# LLM request/contract/prompt/model framework
Core/, Network/, Event/          # tModLoader bridge systems
Observation/                     # Legacy/default spatial/entity extractors
runtime/src/terraclaw_runtime/   # Legacy Python worker, prompt tests, dashboard code
runtime/dashboard/               # Live request/result dashboard
protocol/schemas/                # Older protocol schema references
```

## Quick Start

1. Build + Reload the mod inside tModLoader.
2. Set OpenAI/openai-compatible environment variables before launching tModLoader:

```powershell
$env:OPENAI_API_KEY = "..."
$env:LLM_MODEL = "gpt-4o-mini"
# Optional for OpenAI-compatible endpoints:
# $env:LLM_API_BASE = "https://api.example.com/v1"
```

3. Spawn an agent and use `/agent ...`.

The debug dashboard can be started separately when needed:

```powershell
cd runtime
python -m terraclaw_dashboard
```

Then open `http://127.0.0.1:9090`.

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

`ISymbolicContextProvider` and `LlmOutput` both generate compact prompt metadata automatically. The C# prompt builder sends the LLM a flat symbolic observation and flat output contract instead of verbose raw JSON.

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

The Python runtime is no longer required for C# LLM requests. It remains useful for legacy experiments, prompt-builder regression tests, and dashboard work:

- `llm_worker.py`: deprecated Python worker for the former `llm.request` / `llm.response` path
- `prompt_builder.py`: reference implementation and tests for flat symbolic prompts
- `terraclaw_dashboard`: standalone debug dashboard for C# LLM request/result events
- `dashboard/`: browser UI and live event broadcaster

Useful commands:

```powershell
cd runtime
pip install -e .[dev]
pytest
```

## Configuration

The active C# LLM path first reads:

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
- Compile C# changes in tModLoader.
- Python tests live in `runtime/tests/`.
- Debug dashboard is available at `http://127.0.0.1:9090` while `python -m terraclaw_dashboard` is running.

## Todos

- [ ] Add TileContextBuilder and world memory: Convert surrounding tiles to a sematic mapping, including overall topology and specific interesting tiles.
- [ ] Transplant python llm worker to C#. (openai related llm api calling tools)

## License

MIT
