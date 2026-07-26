# TerraClaw Framework API

TerraClaw exposes a small public API for other mods. Treat everything outside `API/`, `Interfaces/`,
`Models/`, `Events/`, and `Registries/` as implementation detail unless a type is documented here.

## Mod.Call

Supported commands:

```csharp
mod.Call("GetAPI");       // TerraClaw.API.TerraClawApi
mod.Call("GetVersion");   // string
mod.Call("HasFeature", "llm.requests.v1"); // bool
mod.Call("HasFeature", "llm.validation.v1"); // bool
mod.Call("HasFeature", "llm.result.v1"); // bool
mod.Call("HasFeature", "knowledge.query.v1"); // bool
mod.Call("GetFeatures");  // IReadOnlyCollection<string>
mod.Call("RegisterAgent", definition); // bool, strongly typed ILlmAgentDefinition
mod.Call("RegisterAgent", "mymod:agent", "Agent", "Description", "MyMod", "1.0.0", tags); // bool
mod.Call("HasAgent", "othermod:agent"); // bool
mod.Call("GetAgent", "othermod:agent"); // LlmAgentInfo?
mod.Call("GetAgents"); // IReadOnlyCollection<LlmAgentInfo>
```

Invalid command names, argument counts, or argument types throw `ArgumentException`.

## Strongly Typed Usage

Consumers with a compile-time dependency on TerraClaw can request the API and register their agent metadata:

```csharp
if (ModLoader.TryGetMod("TerraClaw", out Mod terraClaw) &&
    terraClaw.Call("GetAPI") is TerraClaw.API.TerraClawApi api &&
    api.HasFeature("agent.registry.v1"))
{
    api.RegisterAgent(new MyAgentDefinition());
}
```

`ILlmAgentDefinition.AgentId` must be stable, lowercase, 3-96 characters, and contain only letters, numbers,
`.`, `:`, `_`, or `-`. Prefer ids such as `mymod:guide_helper`.

Mods without a compile-time dependency can use the weak `Mod.Call("RegisterAgent", ...)` overload shown above.
The optional `tags` argument must be an `IEnumerable<string>`.

## LLM Requests

Use `TerraClawApi.RequestLlm(...)` instead of accessing `LlmBridgeSystem.Instance` directly. The request is
non-blocking and returns an `LlmRequestHandle` that must be polled from normal tModLoader hooks.

```csharp
LlmRequestHandle handle = api.RequestLlm(
    "mymod:guide_helper",
    systemPrompt,
    instruction,
    LlmObservation.Create().Use(TerrariaContext.World().Time()),
    LlmOutput.Object("say").String("text", required: true, maxLength: 100));
```

Completed output is validated against the `LlmOutput` contract before the handle completes. Use `handle.TryGetResult(out LlmResult result)` for safe field reads, or `handle.TryGetResult(out JsonNode? output)` for raw JSON.

## Knowledge Queries

Use `TerraClawApi.RequestKnowledge(...)` for a non-blocking Terraria Wiki query. The runtime uses wiki.gg's MediaWiki API and returns a `KnowledgeRequestHandle` that should be polled from normal tModLoader hooks.

```csharp
KnowledgeRequestHandle handle = api.RequestKnowledge(
    "mymod:guide_helper",
    "Night's Edge crafting",
    limit: 3,
    extractChars: 700,
    timeoutMs: 20000);

if (handle.TryGetResult(out KnowledgeQueryResult result)) {
    foreach (KnowledgeSearchResult item in result.Results) {
        string title = item.Title;
        string extract = item.Extract;
    }
}
```

Passing `0` for `limit`, `extractChars`, or `timeoutMs` uses `TerraClawConfig.json` knowledge defaults. Weak `Mod.Call` knowledge query commands are intentionally not part of v1.

## Events

The API exposes framework lifecycle events:

```csharp
api.AgentRegistered += OnAgentRegistered;
api.LlmRequestStarted += OnLlmRequestStarted;
api.LlmRequestFinished += OnLlmRequestFinished;
```

`LlmRequestFinished` is queued and dispatched during TerraClaw's update loop. Event handler exceptions are
logged and do not stop other handlers. TerraClaw clears these subscriptions during `Unload`, so consumers
should still unsubscribe from their own `Unload` methods to avoid holding stale references on their side.




