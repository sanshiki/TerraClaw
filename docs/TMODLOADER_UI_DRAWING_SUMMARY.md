# tModLoader UI 绘制总结：TerraClaw Dashboard

本文总结 TerraClaw 当前 in-game dashboard 的 UI 是如何绘制、更新和响应输入的。对应代码主要在 `UI/` 目录：

- `TerraClawDashboardSystem.cs`：UI 生命周期、插入绘制层、开关状态。
- `TerraClawDashboardState.cs`：实际 UI 树、布局、按钮、列表、滚动、刷新逻辑。
- `TerraClawDashboardCommand.cs`：聊天命令 `/terraclawdash`。
- `TerraClawDashboardKeybinds.cs` 和 `TerraClawDashboardPlayer.cs`：快捷键触发。
- `LlmDebugLog.cs`：dashboard 展示的数据缓存和变更通知。

## 总体架构

TerraClaw Dashboard 不是在某个 hook 里直接手写一堆 `SpriteBatch.Draw(...)`。它使用 tModLoader/Terraria 内置 UI 框架：

```text
ModSystem
  -> UserInterface
     -> UIState
        -> UIPanel / UIList / UIText / UITextPanel / UIScrollbar
```

核心流程是：

1. `TerraClawDashboardSystem.Load()` 在客户端创建 `UserInterface` 和 `TerraClawDashboardState`。
2. 玩家通过命令或快捷键调用 `Toggle()`。
3. `SetOpen(true)` 把 `_state` 设置到 `_interface`，并刷新 dashboard 数据。
4. 每帧 `UpdateUI(GameTime)` 更新当前 UIState。
5. `ModifyInterfaceLayers(...)` 把绘制委托插入到 Terraria 的界面层中。
6. 绘制委托调用 `_interface.Draw(Main.spriteBatch, new GameTime())`。

服务器端不加载 UI：

```csharp
if (Main.dedServ)
    return;
```

这很重要，因为 dedicated server 没有图形界面，也不应该创建客户端 UI 对象。

## 绘制入口：ModSystem

`TerraClawDashboardSystem` 是整个 UI 的宿主。它保存：

```csharp
private UserInterface? _interface;
private TerraClawDashboardState? _state;
public bool IsOpen { get; private set; }
```

### Load / Unload

`Load()` 只负责创建 UI 对象，不立即显示：

```csharp
_state = new TerraClawDashboardState();
_interface = new UserInterface();
```

`Unload()` 清空引用，避免重载 Mod 后残留静态实例：

```csharp
_interface = null;
_state = null;
Instance = null;
```

### 打开和关闭

Dashboard 的开关通过 `SetOpen(bool open)` 控制：

```csharp
IsOpen = open;
_interface.SetState(open ? _state : null);
if (open)
    _state.Refresh();
```

`UserInterface.SetState(...)` 是关键。传入 `UIState` 时 UI 激活；传入 `null` 时 UI 关闭。`UIState.OnActivate()` 和 `UIState.OnDeactivate()` 会随之触发。

## 更新入口：UpdateUI

tModLoader 的 `ModSystem.UpdateUI(GameTime gameTime)` 用于每帧更新 UI 逻辑：

```csharp
public override void UpdateUI(GameTime gameTime)
{
    if (IsOpen)
        _interface?.Update(gameTime);
}
```

这里不会绘制，只处理 UI 元素状态、鼠标悬停、点击、滚动、内部刷新等逻辑。实际行为会进入 `TerraClawDashboardState.Update(...)`。

## 绘制入口：ModifyInterfaceLayers

UI 的真正绘制发生在 `ModifyInterfaceLayers(List<GameInterfaceLayer> layers)`：

```csharp
int mouseTextIndex = layers.FindIndex(layer => layer.Name.Equals("Vanilla: Mouse Text"));
```

代码找到 vanilla 的 `"Vanilla: Mouse Text"` 层，然后在它前面插入 TerraClaw 自己的层：

```csharp
layers.Insert(mouseTextIndex, new LegacyGameInterfaceLayer(
    "TerraClaw: LLM Dashboard",
    delegate
    {
        if (IsOpen)
            _interface?.Draw(Main.spriteBatch, new GameTime());
        return true;
    },
    InterfaceScaleType.UI));
```

这样做的效果是：

- Dashboard 属于 UI 层，而不是世界绘制层。
- Dashboard 会受 UI 缩放影响，使用 `InterfaceScaleType.UI`。
- 插在 mouse text 之前，避免覆盖 Terraria 最顶层的鼠标文本提示。
- 当 `IsOpen == false` 时，绘制委托仍存在，但不会画任何东西。

## UI 树：TerraClawDashboardState

`TerraClawDashboardState : UIState` 定义具体控件。它在 `OnInitialize()` 中构建一次 UI 树。

当前结构大致如下：

```text
UIState
  root UIPanel
    title UIText
    Close button
    Clear button
    listPanel UIPanel
      entryList UIList
      listScrollbar UIScrollbar
    detailPanel UIPanel
      summary UIText
      tab buttons
      detailList UIList
      detailScrollbar UIScrollbar
```

### 根面板

根面板 `_root` 使用百分比宽高，并居中：

```csharp
_root.Width.Set(0f, 0.72f);
_root.Height.Set(0f, 0.68f);
_root.HAlign = 0.5f;
_root.VAlign = 0.5f;
```

`Width.Set(pixel, percent)` 的第二个参数是父容器比例。这里表示宽度占屏幕 UI 区域的 72%，高度占 68%。

### 左侧请求列表

左侧 `listPanel` 固定宽度 280 像素：

```csharp
var listPanel = Panel(280f, 0f);
listPanel.Top.Set(44f, 0f);
listPanel.Height.Set(-54f, 1f);
```

`Height.Set(-54f, 1f)` 表示高度为父级高度减 54 像素。这种写法常用于保留顶部标题栏和边距。

`UIList` 放在 panel 内，并绑定 `UIScrollbar`：

```csharp
_entryList = new UIList();
_entryList.Width.Set(-26f, 1f);
_entryList.Height.Set(0f, 1f);
_entryList.ListPadding = 6f;

var listScrollbar = new UIScrollbar();
listScrollbar.HAlign = 1f;
listScrollbar.Height.Set(0f, 1f);
_entryList.SetScrollbar(listScrollbar);
```

### 右侧详情区域

右侧 `detailPanel` 从左侧列表后面开始，并占剩余宽度：

```csharp
detailPanel.Left.Set(292f, 0f);
detailPanel.Width.Set(-292f, 1f);
```

其中 292 像素包含左侧面板宽度和间距。详情区域顶部显示 summary，中间是 tab 按钮，下面是可滚动详情列表。

## 元素绘制由 UIElement 自动完成

在 `TerraClawDashboardState` 中没有直接调用 `SpriteBatch.Draw(...)`。例如按钮是这样创建的：

```csharp
private static UITextPanel<string> Button(string text, float width, float height)
{
    var button = new UITextPanel<string>(text, 0.8f, false);
    button.Width.Set(width, 0f);
    button.Height.Set(height, 0f);
    button.BackgroundColor = new Color(35, 42, 60, 240);
    button.BorderColor = new Color(82, 96, 128);
    return button;
}
```

`UITextPanel<string>` 自己负责绘制背景、边框和文字。`UIPanel`、`UIText`、`UIList`、`UIScrollbar` 也都是 Terraria UI 框架提供的元素，绘制由 `_interface.Draw(...)` 递归触发。

如果以后需要自定义绘制，可以继承 `UIElement` 或某个现有元素，并重写 `DrawSelf(SpriteBatch spriteBatch)`。当前 dashboard 没有这么做。

## 输入处理

### 命令打开

`TerraClawDashboardCommand` 注册聊天命令：

```csharp
public override string Command => "terraclawdash";
public override CommandType Type => CommandType.Chat;
```

执行 `/terraclawdash` 时调用：

```csharp
TerraClawDashboardSystem.Instance?.Toggle();
```

### 快捷键打开

`TerraClawDashboardKeybinds` 注册快捷键：

```csharp
ToggleDashboard = KeybindLoader.RegisterKeybind(Mod, "ToggleDashboard", "OemTilde");
```

`TerraClawDashboardPlayer.ProcessTriggers(...)` 每帧检查：

```csharp
if (TerraClawDashboardKeybinds.ToggleDashboard?.JustPressed == true)
    TerraClawDashboardSystem.Instance?.Toggle();
```

### 鼠标占用和滚轮

`TerraClawDashboardState.Update(...)` 中，如果鼠标在根面板内：

```csharp
Main.LocalPlayer.mouseInterface = true;
```

这会告诉 Terraria：当前鼠标正在操作 UI，避免点击穿透到游戏世界。

滚轮逻辑手动转给当前悬停的 `UIList`：

```csharp
if (_detailList.ContainsPoint(Main.MouseScreen))
    _detailList.ViewPosition -= Terraria.GameInput.PlayerInput.ScrollWheelDeltaForUI;
else if (_entryList.ContainsPoint(Main.MouseScreen))
    _entryList.ViewPosition -= Terraria.GameInput.PlayerInput.ScrollWheelDeltaForUI;
```

## 数据刷新机制

Dashboard 展示的数据来自 `LlmDebugLog`。LLM 请求发出时记录 request，结果返回时记录 result。`LlmDebugLog` 内部用 lock 保护列表，并最多保留 50 条：

```csharp
private const int MaxEntries = 50;
private static readonly object Sync = new();
private static readonly List<LlmDebugEntry> Entries = new();
```

每次数据变化后触发：

```csharp
Changed?.Invoke();
```

`TerraClawDashboardState.OnActivate()` 订阅事件：

```csharp
LlmDebugLog.Changed += MarkDirty;
Refresh();
```

`OnDeactivate()` 取消订阅：

```csharp
LlmDebugLog.Changed -= MarkDirty;
```

`MarkDirty()` 不直接重建 UI，只设置标记：

```csharp
_needsRefresh = true;
```

随后在 UI update 阶段处理：

```csharp
if (_needsRefresh)
{
    _needsRefresh = false;
    Refresh();
}
```

这种做法避免在日志变更事件里直接修改 UI 树，更适合游戏主循环模型。

## 列表重建

刷新时会拿一份日志快照：

```csharp
IReadOnlyList<LlmDebugEntry> entries = LlmDebugLog.GetSnapshot();
```

然后重建左侧请求列表和右侧详情：

```csharp
RebuildEntries(entries);
RebuildDetails(entries.FirstOrDefault(entry => entry.RequestId == _selectedRequestId));
Recalculate();
```

`Recalculate()` 很关键。动态增删 UI 元素后，需要重新计算布局，否则列表尺寸和滚动条状态可能不正确。

左侧每条记录是一个 `UITextPanel<string>` 按钮：

```csharp
var row = Button(RowTitle(entry), 0f, 58f);
row.Width.Set(0f, 1f);
```

点击后更新选中 request：

```csharp
row.OnLeftClick += (_, _) =>
{
    _selectedRequestId = requestId;
    Refresh();
};
```

右侧根据当前 tab 选择展示内容：

```csharp
string body = _tab switch
{
    DetailTab.Symbolic => entry.SymbolicObservationJson,
    DetailTab.Observation => entry.ObservationJson,
    DetailTab.Contract => entry.OutputContractJson,
    DetailTab.Output => entry.OutputJson,
    _ => "",
};
```

## 当前实现的限制

当前 dashboard 是可用的调试 UI，但有几个需要注意的点：

- 文本换行是用固定字符数 `Wrap(text, 72)`，不是按真实字体宽度测量；不同 UI 缩放下可能不完全准确。
- Tab 按钮没有根据 `_tab` 显示选中态，用户只能从内容判断当前页。
- `Clear` 只清空日志，没有立即显式调用 `Refresh()`；它依赖 `LlmDebugLog.Changed` 事件触发刷新。
- 列表刷新采用清空并重建全部元素，日志上限只有 50 条，所以目前成本可以接受。
- `_interface.Draw(Main.spriteBatch, new GameTime())` 使用了新的 `GameTime`，当前 UI 绘制不依赖 elapsed time，因此没有实际问题；如果未来加入动画，最好传入真实帧时间或把动画逻辑放在 `UpdateUI`。

## 扩展 tModLoader UI 的建议

添加新 dashboard 或扩展当前 dashboard 时，可以沿用这个模式：

1. 用 `ModSystem` 持有 `UserInterface` 和 `UIState`。
2. 在 `Load()` 中跳过 `Main.dedServ`。
3. 在 `UpdateUI()` 中调用 `_interface.Update(gameTime)`。
4. 在 `ModifyInterfaceLayers()` 中插入 `LegacyGameInterfaceLayer` 并调用 `_interface.Draw(...)`。
5. 在 `UIState.OnInitialize()` 中构建固定 UI 树。
6. 动态数据变化时设置 dirty flag，在 `Update()` 中刷新 UI。
7. 鼠标位于 UI 内时设置 `Main.LocalPlayer.mouseInterface = true`。
8. 动态增删元素后调用 `Recalculate()`。

如果只是普通面板、按钮、文字、列表，优先使用 Terraria 提供的 `UIPanel`、`UIText`、`UITextPanel<T>`、`UIList`、`UIScrollbar`。只有在需要特殊纹理、shader、裁剪或复杂图形时，再考虑自定义 `UIElement.DrawSelf(...)` 或直接使用 `SpriteBatch`。
