# TerraClaw Strong Reference LLM API

This document is for mod developers who compile against TerraClaw and declare a strong tModLoader dependency with `modReferences`.

It covers only the LLM-facing framework surface: observations, output contracts, LLM requests, result polling, and knowledge requests. Game behavior such as NPC movement, combat, tile editing, projectiles, dialogue hooks, and scheduling remains owned by your mod.

## Dependency Setup

In your mod's `build.txt`:

```ini
modReferences = TerraClaw
```

For compile-time types, reference the extracted TerraClaw assembly in your IDE/project. In tModLoader, extract TerraClaw from the mod browser/workshop management UI, then reference the extracted `TerraClaw_*.dll` from `ModSources/ModAssemblies`.

Do not put TerraClaw in `dllReferences`; that field is for non-mod assemblies.

Use these namespaces:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using TerraClaw.API;
using TerraClaw.LLM;
using TerraClaw.Knowledge;
using Terraria;
using Terraria.ModLoader;
```

## Public LLM Surface

Strong-reference agents should use:

- `TerraClawApi`
- `LlmObservation`
- `TerrariaContext`
- `Context.Custom(...)`
- `IContextProvider` and `ISymbolicContextProvider`
- `LlmOutput` and `LlmObjectBuilder`
- `LlmRequestHandle`
- `LlmResult`
- `KnowledgeRequestHandle`
- `KnowledgeQueryResult`, `KnowledgeSearchResult`, and `KnowledgeSourceInfo`

Prefer `TerraClawApi.RequestLlm(...)` over direct access to `LlmBridgeSystem.Instance`. Direct bridge access is available inside TerraClaw's implementation, but `TerraClawApi` is the intended cross-mod entry point.

## Getting the API

Strong-reference mods can use the typed static entry point directly:

```csharp
private TerraClawApi? _terraClaw;

public override void PostSetupContent()
{
    TerraClawApi? api = TerraClaw.TerraClaw.Api;
    if (api == null || !api.HasFeature("llm.requests.v1"))
        return;

    _terraClaw = api;
}

public override void Unload()
{
    _terraClaw = null;
}
```

`modReferences = TerraClaw` ensures TerraClaw is loaded before your mod, but the API is still a runtime object. Resolve it after TerraClaw has loaded, and clear your cached reference in `Unload`.

`Mod.Call("GetAPI")` remains useful for weak dependencies or compatibility code that avoids compile-time references. It is not required for the strong-reference workflow described here.

Recommended feature checks:

```csharp
api.HasFeature("llm.requests.v1");
api.HasFeature("llm.validation.v1");
api.HasFeature("llm.result.v1");
api.HasFeature("knowledge.query.v1");
api.HasFeature("knowledge.sources.v1");
```

## Basic Request Loop

Requests are non-blocking. Start a request, store the handle, and poll it later from your own `AI()`, `ModSystem.PostUpdateEverything()`, or another normal tModLoader hook.

```csharp
private LlmRequestHandle? _llm;

private void MaybeRequest(NPC npc)
{
    if (_terraClaw == null)
        return;
    if (_llm != null && _llm.IsPending)
        return;

    LlmObservation observation = BuildObservation(npc);
    LlmOutput output = BuildOutputContract();

    _llm = _terraClaw.RequestLlm(
        agentId: "mymod:basic_agent",
        systemPrompt: "You are a Terraria NPC assistant. Return only JSON matching the contract.",
        instruction: "Choose the next response for this NPC.",
        observation: observation,
        output: output,
        timeoutMs: 30000);
}

private void PollRequest(NPC npc)
{
    if (_llm == null || _llm.IsPending)
        return;

    if (_llm.TryGetResult(out LlmResult result))
    {
        ApplyResult(npc, result);
    }
    else if (_llm.IsDone)
    {
        string error = _llm.Error ?? _llm.Status.ToString();
        // Log or store the failure in your own agent state.
    }

    _llm = null;
}
```

Only one pending LLM request is allowed per `agentId`. Use a stable agent id per logical agent, and avoid request spam with your own cooldowns or state-change checks.

## Observations

`LlmObservation` is request-local state sent to the LLM. It has two forms:

- verbose JSON for dashboard/debugging,
- compact symbolic JSON for token-efficient prompts.

Use built-in context builders first:

```csharp
private static LlmObservation BuildObservation(NPC npc)
{
    return LlmObservation.Create()
        .Use(TerrariaContext.Npc(npc).Basic().Life().Home())
        .Use(TerrariaContext.World().Time().Moon().Progression())
        .Use(TerrariaContext.Entities().HostilesNear(npc.Center, 800f, max: 5))
        .Use(TerrariaContext.Tiles(npc.Center, radiusTiles: 24).Area(maxSpecials: 12))
        .Use(Context.Custom("mind", "agent-owned state")
            .Field("mode", "idle", "current local behavior mode")
            .Field("last_player_text", "", "latest player instruction")
            .Field("cooldown", 0, "ticks until next voluntary action"));
}
```

Available built-in context entry points:

```csharp
TerrariaContext.Npc(npc).Basic().Life().Home()
TerrariaContext.Player(player).Basic().Location().Buffs().Equipment().Inventory()
TerrariaContext.World().Time().Moon().Progression()
TerrariaContext.Entities().HostilesNear(center, radius, max)
TerrariaContext.Tiles(center, radiusTiles).Area(maxSpecials)
```

Use `Context.Custom(symbol, description).Field(...)` for small agent-owned state such as memory, goals, trigger reason, pending action, or the latest knowledge result.

```csharp
.Use(Context.Custom("mem", "agent memory")
    .Field("goal", currentGoal, "current player goal")
    .Field("summary", shortMemory, "short memory summary"))
```

Use short, stable symbols such as `mind`, `mem`, `input`, or your own compact keys. Field order matters because symbolic values are emitted as ordered arrays with generated legends.

### Custom Providers

Implement `ISymbolicContextProvider` when collection logic is reused or too large for inline `Context.Custom(...)`.

```csharp
public sealed class MoodContextProvider : ISymbolicContextProvider
{
    private readonly string _mood;

    public MoodContextProvider(string mood)
    {
        _mood = mood;
    }

    public string Symbol => "mood";
    public string Description => "agent mood state";

    public IReadOnlyList<SymbolicField> Fields { get; } =
        new[] { new SymbolicField("value", "current mood label") };

    public void AddContext(JsonObject target)
    {
        target["mood"] = new JsonObject
        {
            ["value"] = _mood,
        };
    }

    public JsonArray ToSymbolicValues()
    {
        var values = new JsonArray();
        values.Add(_mood);
        return values;
    }
}
```

Then add it with:

```csharp
LlmObservation.Create().Use(new MoodContextProvider("curious"));
```

## Output Contracts

`LlmOutput` defines the JSON shape TerraClaw asks the model to return. Completed output is validated against this contract before `TryGetResult(out LlmResult result)` succeeds.

For a single action:

```csharp
private static LlmOutput BuildOutputContract()
{
    return LlmOutput.Object("say", "Say one short line.")
        .String("text", "message to display", maxLength: 100)
        .String("summary", "brief reasoning summary", maxLength: 200);
}
```

For agents with multiple possible actions, prefer `OneOf`:

```csharp
private static LlmOutput BuildOutputContract()
{
    return LlmOutput.OneOf(
        LlmOutput.Object("say", "Say one short line.")
            .String("text", "message to display", maxLength: 100)
            .Boolean("callback", "true if the agent should continue acting")
            .String("summary", "brief reasoning summary", maxLength: 200),

        LlmOutput.Object("set_goal", "Replace the local goal memory.")
            .String("goal", "new goal text", maxLength: 200)
            .Boolean("callback", "true if the agent should continue acting")
            .String("summary", "brief reasoning summary", maxLength: 200),

        LlmOutput.Object("ask_knowledge", "Request wiki knowledge before deciding.")
            .String("query", "short keyword-only wiki query", maxLength: 120)
            .Boolean("callback", "true if the agent should continue after knowledge returns")
            .String("summary", "brief reasoning summary", maxLength: 200));
}
```

Supported field builder methods:

```csharp
.String(name, description, required: true, maxLength: 100)
.Number(name, description, required: true, defaultValue: 0)
.Boolean(name, description, required: true)
```

Fields are required by default. Use `required: false` only when your result application code can handle missing values.

Use:

- `LlmOutput.OneOf(...)` when the model must choose exactly one branch.
- `LlmOutput.AnyOf(...)` when the model may return one or more branches.
- `LlmOutput.AllOf(...)` when every branch must be satisfied.

Most basic agents should use `OneOf`.

## Reading Results

Use `LlmResult` for common field reads:

```csharp
private void ApplyResult(NPC npc, LlmResult result)
{
    switch (result.Type)
    {
        case "say":
            string text = result.String("text");
            bool callback = result.Bool("callback");
            break;

        case "set_goal":
            string goal = result.String("goal");
            break;

        case "ask_knowledge":
            string query = result.String("query");
            break;
    }
}
```

Available readers:

```csharp
result.Type
result.Is("say")
result.String("text", fallback: "")
result.Int("tile_x", fallback: 0)
result.Number("distance", fallback: 0)
result.Bool("callback", fallback: false)
result.Raw
result.TryDeserialize<T>(out T? value)
```

For `AnyOf` or array-style outputs, use:

```csharp
foreach (LlmResult item in result.Items())
{
    // handle each output item
}
```

For keyed branch output, use:

```csharp
if (result.TryGetBranch("say", out LlmResult say))
{
    string text = say.String("text");
}
```

## Knowledge Requests

Knowledge requests query TerraClaw's configured wiki sources. They are also non-blocking.

```csharp
private KnowledgeRequestHandle? _knowledge;
private string _lastKnowledgeContext = "";

private void StartKnowledgeQuery(string agentId, string query)
{
    if (_terraClaw == null || !_terraClaw.HasFeature("knowledge.query.v1"))
        return;
    if (_knowledge != null && _knowledge.IsPending)
        return;

    _knowledge = _terraClaw.RequestKnowledge(
        agentId,
        query,
        limit: 3,
        extractChars: 700,
        timeoutMs: 20000);
}

private void PollKnowledge()
{
    if (_knowledge == null || _knowledge.IsPending)
        return;

    if (_knowledge.TryGetResult(out KnowledgeQueryResult result))
    {
        _lastKnowledgeContext = string.Join(" | ", result.Results.Select(item =>
            $"{item.Title}: {item.Extract} Source: {item.Url}"));
    }
    else if (_knowledge.IsDone)
    {
        _lastKnowledgeContext = _knowledge.Error ?? _knowledge.Status.ToString();
    }

    _knowledge = null;
}
```

Put the returned knowledge summary into the next LLM observation:

```csharp
.Use(Context.Custom("know", "latest wiki query result")
    .Field("results", _lastKnowledgeContext, "compact search result summaries"))
```

To inspect and restrict sources:

```csharp
IReadOnlyList<KnowledgeSourceInfo> sources = api.GetKnowledgeSources();

if (api.HasKnowledgeSource("terraria-zh"))
{
    KnowledgeRequestHandle handle = api.RequestKnowledgeFromSources(
        "mymod:basic_agent",
        "天顶剑 配方",
        new[] { "terraria-zh" },
        limit: 3,
        extractChars: 700,
        timeoutMs: 20000);
}
```

Passing `0` for `limit`, `extractChars`, or `timeoutMs` uses TerraClaw's configured defaults.

## Minimal Agent Skeleton

This is the LLM part only. Movement, spawning, chat commands, networking, and visual behavior belong in your mod.

```csharp
public sealed class MyLlmAgentState
{
    public LlmRequestHandle? Llm;
    public KnowledgeRequestHandle? Knowledge;
    public string LastKnowledge = "";
    public string QueuedInstruction = "";
}

public sealed class MyAgentNpc : ModNPC
{
    private TerraClawApi? _api;
    private readonly MyLlmAgentState _state = new();

    public override void AI()
    {
        EnsureApi();
        PollKnowledge();
        PollLlm();
        MaybeStartLlm();
    }

    private void EnsureApi()
    {
        if (_api != null)
            return;

        TerraClawApi? api = TerraClaw.TerraClaw.Api;
        if (api == null || !api.HasFeature("llm.requests.v1"))
            return;

        _api = api;
    }

    private void MaybeStartLlm()
    {
        if (_api == null)
            return;
        if (_state.Llm != null && _state.Llm.IsPending)
            return;
        if (_state.Knowledge != null && _state.Knowledge.IsPending)
            return;
        if (string.IsNullOrWhiteSpace(_state.QueuedInstruction))
            return;

        string instruction = _state.QueuedInstruction;
        _state.QueuedInstruction = "";

        _state.Llm = _api.RequestLlm(
            "mymod:my_agent",
            "You are a Terraria NPC assistant. Return only JSON matching the contract.",
            instruction,
            BuildObservation(instruction),
            BuildContract(),
            timeoutMs: 30000);
    }

    private LlmObservation BuildObservation(string instruction)
    {
        var observation = LlmObservation.Create()
            .Use(TerrariaContext.Npc(NPC).Basic().Life())
            .Use(TerrariaContext.World().Time())
            .Use(Context.Custom("input", "request input")
                .Field("instruction", instruction, "latest player instruction"));

        if (!string.IsNullOrWhiteSpace(_state.LastKnowledge))
        {
            observation.Use(Context.Custom("know", "latest wiki query result")
                .Field("results", _state.LastKnowledge, "compact source summaries"));
        }

        return observation;
    }

    private static LlmOutput BuildContract()
    {
        return LlmOutput.OneOf(
            LlmOutput.Object("say", "Say one short line.")
                .String("text", "message text", maxLength: 100)
                .String("summary", "short reasoning summary", maxLength: 200),
            LlmOutput.Object("knowledge_query", "Query wiki knowledge.")
                .String("query", "short keyword-only query", maxLength: 120)
                .String("summary", "short reasoning summary", maxLength: 200));
    }

    private void PollLlm()
    {
        if (_state.Llm == null || _state.Llm.IsPending)
            return;

        if (_state.Llm.TryGetResult(out LlmResult result))
        {
            if (result.Is("say"))
            {
                Main.NewText($"<{NPC.FullName}> {result.String("text")}");
            }
            else if (result.Is("knowledge_query"))
            {
                StartKnowledge(result.String("query"));
            }
        }

        _state.Llm = null;
    }

    private void StartKnowledge(string query)
    {
        if (_api == null || !_api.HasFeature("knowledge.query.v1"))
            return;

        _state.Knowledge = _api.RequestKnowledge(
            "mymod:my_agent:knowledge",
            query,
            limit: 3,
            extractChars: 700,
            timeoutMs: 20000);
    }

    private void PollKnowledge()
    {
        if (_state.Knowledge == null || _state.Knowledge.IsPending)
            return;

        if (_state.Knowledge.TryGetResult(out KnowledgeQueryResult result))
        {
            _state.LastKnowledge = string.Join(" | ", result.Results.Select(item =>
                $"{item.Title}: {item.Extract} Source: {item.Url}"));
            _state.QueuedInstruction = "Use the latest knowledge result to answer or choose the next action.";
        }

        _state.Knowledge = null;
    }
}
```

## Failure Handling

An LLM request can end as:

```csharp
LlmRequestStatus.Completed
LlmRequestStatus.Failed
LlmRequestStatus.Cancelled
LlmRequestStatus.TimedOut
```

A knowledge request can end as:

```csharp
KnowledgeRequestStatus.Completed
KnowledgeRequestStatus.Failed
KnowledgeRequestStatus.Cancelled
KnowledgeRequestStatus.TimedOut
```

For failed or timed-out requests, read `handle.Error`. Invalid LLM JSON or output that does not match the `LlmOutput` contract is reported as a failed request and appears in the TerraClaw dashboard.

## Contract Guidelines

Keep contracts small and action-oriented:

- Use one output branch per game action your mod can actually execute.
- Put game safety rules in C#; do not rely on the model to enforce them.
- Add a `summary` field when you want traceable reasoning in logs or memory.
- Add a `callback` boolean when your agent supports multi-turn autonomous continuation.
- Use short knowledge queries. Keyword-style queries usually work better than full sentences.
- Feed knowledge results into the next LLM observation instead of blocking while waiting.
- Avoid exposing your entire world or inventory state every request; compose only the context needed for the decision.

## Public Boundary

For strong-reference LLM agents, treat these as stable public areas:

- `TerraClaw.API`
- `TerraClaw.LLM` types documented here
- `TerraClaw.Knowledge` request/result DTOs documented here
- `TerraClaw.Interfaces`, `TerraClaw.Models`, `TerraClaw.Events`, and `TerraClaw.Registries` when using agent metadata or events

Do not build external mods against TerraClaw's sample agents, UI, config loaders, HTTP clients, or utility classes. Those are implementation details unless documented as public API.
