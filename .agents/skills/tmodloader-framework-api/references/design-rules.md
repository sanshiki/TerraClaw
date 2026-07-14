# Framework API 设计规则

本参考由 `docs/API_CONSTRUCT.md` 提炼。设计或审查具体公共契约时读取。

## 边界

推荐按职责组织：

```text
API/          公共入口
Interfaces/   第三方可实现的契约
Events/       公共生命周期通知
Registries/   注册、校验和查询
Models/       DTO 与公共数据模型
Systems/      tModLoader 生命周期桥接
Internal/     禁止第三方依赖的实现
Networking/   网络协议与同步
Content/      Mod 内容
```

公共 API 可以依赖公共接口和公共模型。`Internal` 可以实现公共接口，但公共签名不能出现内部类型。

## API 与接口

通过窄 API 暴露行为：

```csharp
public sealed class SkillApi
{
    public void RegisterSkill(ISkill skill);
    public bool HasSkill(string id);
    public ISkill? GetSkill(string id);
    public IReadOnlyCollection<ISkill> GetSkills();
}

public interface ISkill
{
    string Id { get; }
    void Execute(Player player);
}
```

不要公开字段、内部管理器或可修改集合。对于频繁变化的状态，优先返回不可变 DTO 快照，避免只读包装仍暴露实时内部状态。

## Registry

只允许通过方法注册和查询。明确规定：

- ID 的格式、大小写和命名空间规则。
- 重复 ID 是拒绝、替换还是幂等。
- 注册允许发生在哪个生命周期阶段。
- 返回顺序是否稳定。
- 卸载、世界切换和 Mod 重载时如何清理。
- 哪些数据需要服务器权威和网络同步。

不要让第三方通过 `Registry.Entries.Add(...)` 绕过这些规则。

## 事件

在第三方需要观察的稳定生命周期点发布事件，例如注册完成、执行完成、世界加载和保存。事件参数使用公共接口或 DTO。

规定事件发生在操作前还是操作后、运行在哪一侧、是否允许取消，以及处理器抛异常时的行为。保存订阅或提供可靠退订路径，确保 `Unload` 后没有旧 Mod 实例被静态事件持有。

## Mod.Call

将 `Mod.Call` 作为非强类型调用方的薄适配层：

```csharp
public override object? Call(params object[] args)
{
    if (args.Length == 0 || args[0] is not string command)
        throw new ArgumentException("The first argument must be a command name.", nameof(args));

    return command switch
    {
        "GetAPI" => Api,
        "GetVersion" => Version,
        "HasFeature" when args.Length >= 2 && args[1] is string feature =>
            Api.HasFeature(feature),
        _ => throw new ArgumentException($"Unknown or invalid command: {command}", nameof(args)),
    };
}
```

真实逻辑留在 API 类中。为命令名、参数数量、参数类型、返回类型和错误行为建立稳定约定。不要依赖无提示的强制转换失败。

典型调用方式：

```csharp
if (ModLoader.TryGetMod("ModName", out Mod mod) &&
    mod.Call("GetAPI") is SkillApi api)
{
    api.RegisterSkill(new MySkill());
}
```

若希望使用强类型 `SkillApi`，消费方通常需要编译期引用该库 Mod。对只存在运行时依赖的消费方，提供由 `Mod.Call` 返回基础 CLR 类型或另行定义稳定的弱类型协议。

## 生命周期

在 `Load` 中创建 API 和 Registry，在 `Unload` 中按相反顺序清理。避免不可空静态属性在卸载后保留旧对象；根据仓库的 nullable 约定使用可空属性、受控访问器或明确异常。

区分 Mod 生命周期、世界生命周期和玩家生命周期。不要把世界数据永久留在 Mod 级单例中，也不要在客户端接受应由服务器决定的注册或状态变更。

## DTO

DTO 只承载公共数据，不包含访问内部服务的业务逻辑。公开稳定字段；对集合使用只读类型；必要时复制可变数据。不要把 `InternalSkill`、数据库实体、序列化模型或网络包类型直接作为返回值。

## 兼容性

发布后保留已有签名、返回含义、异常行为和默认值。新增需求优先采用：

1. 新方法或属性。
2. 不改变旧行为的重载。
3. 新接口并保留旧接口适配层。
4. `HasFeature` 能力探测。
5. 必须破坏兼容时才引入新的主版本契约。

不要把 `Register(value)` 原地改为 `Register(value, force)` 并删除旧方法。保留旧入口，将其委托给新实现。

## 安全检查

- 验证来自其他 Mod 的空值、类型、ID 和生命周期状态。
- 不信任第三方实现会遵守线程、性能或异常约定。
- 不在 `AI()`、Hook 或主线程生命周期方法中阻塞等待外部工作。
- 多人状态由服务器权威决定，并明确同步协议的版本和未知消息处理。
- 公共事件调用第三方代码时隔离或定义异常传播策略。
