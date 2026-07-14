# TerraClaw 外部 API 介绍

TerraClaw 的目标是作为 tModLoader 的 LLM Framework / Library Mod，让其他 Mod 可以复用它的 LLM 请求、Agent 注册和生命周期事件，而不需要直接依赖内部实现类。

外部 Mod 应只依赖这些公共区域：

- `TerraClaw.API`
- `TerraClaw.Interfaces`
- `TerraClaw.Models`
- `TerraClaw.Events`
- `TerraClaw.Registries`
- 已公开记录的 `TerraClaw.LLM` 请求构造类型，例如 `LlmObservation`、`LlmOutput`、`LlmRequestHandle`

其他目录中的类默认视为内部实现，后续版本可能调整，不建议外部 Mod 直接调用。

## 核心入口

外部 Mod 通过 `Mod.Call("GetAPI")` 获取 `TerraClawApi`：

```csharp
if (ModLoader.TryGetMod("TerraClaw", out Mod terraClaw) &&
    terraClaw.Call("GetAPI") is TerraClaw.API.TerraClawApi api)
{
    // use api here
}
```

如果你的 Mod 不想编译期引用 TerraClaw，也可以只使用弱类型 `Mod.Call` 命令。弱类型调用适合只做注册、查询和能力探测的 Mod。

## 版本与能力探测

TerraClaw API 版本当前为 `1.0.0`。外部 Mod 不应只根据版本号判断功能，推荐优先使用 `HasFeature`。

```csharp
string version = (string)terraClaw.Call("GetVersion");
bool canRequestLlm = (bool)terraClaw.Call("HasFeature", "llm.requests.v1");
```

当前功能标识：

| Feature | 含义 |
| --- | --- |
| `api.v1` | `TerraClawApi` 主入口可用 |
| `modcall.v1` | 基础 `Mod.Call` 命令可用 |
| `llm.requests.v1` | 可通过 API 发起非阻塞 LLM 请求 |
| `llm.validation.v1` | LLM 输出会按 `LlmOutput` 契约验证 |
| `llm.result.v1` | 可使用 `LlmResult` 读取结构化输出 |
| `agent.registry.v1` | 可注册和查询外部 Agent 元数据 |
| `events.v1` | 可订阅公开生命周期事件 |

## Agent 注册

外部 Mod 可以向 TerraClaw 注册自己的 Agent 元数据。注册不是创建 NPC，也不会自动启动逻辑；它用于让框架、调试 UI 或其他 Mod 知道某个 Agent 存在。

强类型方式：

```csharp
public sealed class MyAgentDefinition : ILlmAgentDefinition
{
    public string AgentId => "mymod:guide_helper";
    public string DisplayName => "Guide Helper";
    public string Description => "Adds LLM-assisted guide behavior.";
    public string OwnerModName => "MyMod";
    public string Version => "1.0.0";
    public IReadOnlyCollection<string> Tags => new[] { "npc", "guide" };
}

api.RegisterAgent(new MyAgentDefinition());
```

弱类型方式：

```csharp
terraClaw.Call(
    "RegisterAgent",
    "mymod:guide_helper",
    "Guide Helper",
    "Adds LLM-assisted guide behavior.",
    "MyMod",
    "1.0.0",
    new[] { "npc", "guide" });
```

`AgentId` 规则：

- 长度 3 到 96。
- 只能包含小写字母、数字、`.`、`:`、`_`、`-`。
- 推荐格式为 `modname:agent_name`。
- 重复注册相同 ID 会抛出 `InvalidOperationException`。

查询方式：

```csharp
bool exists = api.HasAgent("mymod:guide_helper");
LlmAgentInfo? info = api.GetAgent("mymod:guide_helper");
IReadOnlyCollection<LlmAgentInfo> allAgents = api.GetAgents();
```

返回的 `LlmAgentInfo` 是只读快照，不会暴露 TerraClaw 内部 Registry。

## 发起 LLM 请求

有编译期引用 TerraClaw 的 Mod 可以通过 `TerraClawApi.RequestLlm(...)` 发起非阻塞请求。

```csharp
LlmRequestHandle handle = api.RequestLlm(
    agentId: "mymod:guide_helper",
    systemPrompt: "You are a Terraria NPC assistant. Return only JSON.",
    instruction: "React to the current world state.",
    observation: LlmObservation.Create()
        .Use(TerrariaContext.World().Time()),
    output: LlmOutput.Object("say", "Say one short line.")
        .String("text", required: true, maxLength: 100),
    timeoutMs: 30000);
```

请求不会阻塞游戏主循环。调用方需要在自己的 `AI()`、`PostUpdateEverything()` 或其他 tModLoader Hook 中轮询句柄：

```csharp
if (handle.IsPending)
    return;

if (handle.TryGetResult(out LlmResult result))
{
    string type = result.Type;
    string text = result.String("text");
}
else if (handle.IsDone)
{
    string? error = handle.Error;
}
```

不要在 `AI()` 或 Hook 中等待 LLM 结果。正确做法是保存 `LlmRequestHandle`，之后每帧检查状态。输出不符合 `LlmOutput` 契约时，请求会失败，并可在 dashboard 中看到验证错误。

## 生命周期事件

`TerraClawApi` 提供以下事件：

```csharp
api.AgentRegistered += OnAgentRegistered;
api.LlmRequestStarted += OnLlmRequestStarted;
api.LlmRequestFinished += OnLlmRequestFinished;
```

事件含义：

| 事件 | 触发时机 |
| --- | --- |
| `AgentRegistered` | 有外部或内部 Agent 注册成功后 |
| `LlmRequestStarted` | LLM 请求进入 TerraClaw 桥接层后 |
| `LlmRequestFinished` | LLM 请求完成、失败、取消或超时后 |

`LlmRequestFinished` 会排队到 TerraClaw 的更新循环中派发，避免异步请求线程直接调用外部 Mod 逻辑。事件处理器抛出的异常会被记录为 warning，不会阻断其他处理器。

外部 Mod 仍应在自己的 `Unload()` 中退订事件：

```csharp
public override void Unload()
{
    if (_api != null)
    {
        _api.AgentRegistered -= OnAgentRegistered;
        _api.LlmRequestStarted -= OnLlmRequestStarted;
        _api.LlmRequestFinished -= OnLlmRequestFinished;
    }

    _api = null;
}
```

## Mod.Call 命令表

| 命令 | 参数 | 返回值 |
| --- | --- | --- |
| `GetAPI` | 无 | `TerraClawApi` |
| `GetVersion` | 无 | `string` |
| `HasFeature` | `string feature` | `bool` |
| `GetFeatures` | 无 | `IReadOnlyCollection<string>` |
| `RegisterAgent` | `ILlmAgentDefinition definition` | `bool` |
| `RegisterAgent` | `agentId, displayName, description, ownerModName, version, tags?` | `bool` |
| `HasAgent` | `string agentId` | `bool` |
| `GetAgent` | `string agentId` | `LlmAgentInfo?` |
| `GetAgents` | 无 | `IReadOnlyCollection<LlmAgentInfo>` |

命令名错误、参数数量错误或参数类型错误会抛出 `ArgumentException`。在 TerraClaw 尚未完成加载时请求 API 会抛出 `InvalidOperationException`。

## 推荐使用模式

1. 在 `Load()` 或较晚的初始化阶段获取 TerraClaw API。
2. 先调用 `HasFeature(...)` 判断能力是否存在。
3. 注册自己的 Agent 元数据。
4. 在游戏 Hook 中发起非阻塞 LLM 请求。
5. 保存并轮询 `LlmRequestHandle`。
6. 在 `Unload()` 中退订事件并清理自己的引用。

TerraClaw 会在自身 `Unload()` 时清空 Registry、事件订阅和静态 API 引用。外部 Mod 不应缓存跨 Mod Reload 的旧 API 实例。

