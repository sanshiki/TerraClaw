# C# Agent API

This document describes the current C#-first TerraClaw framework for adding LLM-backed Terraria agents.

## Core Concepts

### `LlmBridgeSystem`

`LlmBridgeSystem` sends generic LLM requests from C# using the OpenAI .NET SDK and routes responses back by `request_id`.

```csharp
var handle = LlmBridgeSystem.Instance.Request(
    agentId,
    systemPrompt,
    instruction,
    observation,
    outputContract,
    timeoutMs: 30000);
```

Requests are non-blocking. Keep the returned `LlmRequestHandle` and poll it from game update hooks or `AI()`.

### `LlmRequestHandle`

Use this to handle LLM latency without blocking Terraria's 60 FPS loop.

```csharp
if (handle.TryGetResult(out JsonNode? output)) {
    // parse and apply output
}
```

Only one pending request is allowed per `agentId`; `LlmBridgeSystem.Request` returns the existing pending handle when one already exists.

### `LlmObservation`

`LlmObservation` combines reusable components, custom symbolic state, legacy providers, and debug fields.

```csharp
var obs = LlmObservation.Create()
    .Use(TerrariaContext.Npc(npc).Basic().Life().Home())
    .Use(TerrariaContext.World().Time().Moon().Progression())
    .Use(Context.Custom("mind", "agent memory")
        .Field("emotion", state.Emotion)
        .Field("trigger", trigger))
    .Custom("debug_note", "optional verbose-only extension");
```

`Use(...)` is the preferred fluent API. `With(IContextProvider)` is still supported for existing custom providers. `Custom(...)` adds small extension fields under `x`; `Context.Custom(...)` creates a named symbolic component with generated prompt docs.

### Built-In Context Components

`TerrariaContext` provides reusable observation components:

```csharp
TerrariaContext.Npc(npc).Basic().Life().Home()
TerrariaContext.World().Time().Moon().Progression()
TerrariaContext.Entities().HostilesNear(npc.Center, 800f, max: 5)
TerrariaContext.Tiles(npc.Center, radiusTiles: 24).Area(maxSpecials: 12)
```

Compose these per request. For example, an event callback can send only `World().Time()` plus a custom event payload instead of a full observation.
Tile context intentionally summarizes topology tags and notable tiles instead of sending a raw tile grid.

### `ISymbolicContextProvider`

Use this for token-efficient prompt data.

```csharp
public sealed class MyProvider : ISymbolicContextProvider {
    public string Symbol => "m";
    public string Description => "My compact state";
    public IReadOnlyList<SymbolicField> Fields { get; } = new[] {
        new SymbolicField("hp", "current health"),
        new SymbolicField("mood", "agent mood"),
    };

    public void AddContext(JsonObject target) { ... }
    public JsonArray ToSymbolicValues() => new() { hp, mood };
}
```

The C# prompt builder automatically renders:

```text
m=[hp,mood]
```

No manual system prompt edits are needed when fields change.

### `LlmOutput`

Use `LlmOutput` to declare structured output options.

```csharp
var output = LlmOutput.OneOf(
    LlmOutput.Object("talk")
        .String("text", required: true, maxLength: 80),
    LlmOutput.Object("set_state")
        .String("state", required: true, maxLength: 40)
);
```

`LlmOutput` generates:

- full JSON Schema for debugging/future validation,
- compact output legend,
- flat output contract for the prompt.

## Adding an Agent

### 1. Create an Agent Folder

Use:

```text
Agents/<AgentName>/
  <AgentCode>.cs
  Providers/
    <AgentName>ContextProviders.cs
```

Examples:

```text
Agents/Guide/GuideLlmGlobalNPC.cs
Agents/Guide/Providers/GuideContextProviders.cs
Agents/TerraClaw/ExampleTerraClawAgent.cs
```

### 2. Choose an Attachment Model

Use `ModNPC` when you own a spawned NPC:

```csharp
public sealed class MyAgentNpc : ModNPC {
    public override void AI() { ... }
}
```

Use `GlobalNPC` when attaching to vanilla NPCs:

```csharp
public sealed class MyGlobalNpcAgent : GlobalNPC {
    public override void AI(NPC npc) { ... }
    public override void GetChat(NPC npc, ref string chat) { ... }
}
```

For tModLoader hook signatures, check the official preview docs when uncertain:

```text
https://docs.tmodloader.net/docs/preview/index.html
```

### 3. Compose Observations

Start with reusable components:

```csharp
var obs = LlmObservation.Create()
    .Use(TerrariaContext.Npc(npc).Basic().Life())
    .Use(TerrariaContext.World().Time())
    .Use(Context.Custom("mind")
        .Field("state", localState)
        .Field("queued", queue.Count));
```

Use agent-specific providers only when collection logic is complex or reused across files. Put them in `Agents/<AgentName>/Providers/`. Keep reusable generic providers in `LLM/`.

Use short symbols:

- `g` for Guide state,
- `tw` for town/world state,
- `gm` for Guide memory,
- `gh` for Guide nearby hostiles.

Prefer compact arrays for symbolic values and preserve verbose JSON in `AddContext` for dashboard debugging. Existing provider-based code remains valid, but the component API should be the default for new agents.

### 4. Define Output Contract

Choose the simplest logic:

- `OneOf`: model must choose exactly one output branch.
- `AnyOf`: model may return one object or an array of output objects.
- `AllOf`: model should satisfy all branches; parser must support aggregate output.

For reactive NPC dialogue, prefer `AnyOf` unless every response must include every branch.

### 5. Add Scheduling

Keep scheduling in C#:

- heartbeat timers for low-frequency updates,
- signature diffs for state-change callbacks,
- player command or interaction hooks for explicit triggers,
- cooldowns to avoid request spam.

Never block in `AI()` or tModLoader hooks. Send a request and poll `LlmRequestHandle` later.

### 6. Apply Results Locally

Parse only the output types your agent supports. Keep game behavior local and deterministic.

```csharp
switch (type) {
    case "combat_text":
        CombatText.NewText(npc.Hitbox, Color.Gold, text);
        break;
    case "cached_text":
        state.CachedChatText = text;
        break;
}
```

## Validation

Use `dotnet build` as a quick local C# compile check when useful. It is not final validation for this tModLoader project; still Build + Reload in tModLoader and fix any compiler or runtime errors from there.

Python-only changes can be checked with:

```powershell
cd runtime
pytest
```

## Deprecated Path

The older Python-driven `AgentLoop`, `agent.register`, and `agent.action` queue path is deprecated for new agents. New C# agents should implement behavior directly in tModLoader hooks and call `LlmBridgeSystem.Request`.
