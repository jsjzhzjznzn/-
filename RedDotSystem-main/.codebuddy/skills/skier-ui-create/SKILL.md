---
name: skier-ui-create
description: 在 RedDotSystem-main（Tuanjie/Unity）里新建并登记一个 UGUI 界面：UGUI 预制体 + UIView 子类 + [ControlBinding] + UIType 枚举 + Assets/Game/UI/UIConfig.json + YooAsset ui 包收集。当用户要新建界面/HUD/弹窗/窗口/UI 预制体，或说“做个面板/加个界面/登记 UI/在 UI 管理里加一个”时使用。Use when creating any new UI panel, HUD, popup, window, or UI prefab in this project.
---

# 新建并登记 UI（SkierFramework + YooAsset）

本 skill 只做一件事：**新增 UI 时把“预制体 → 代码 → 配置 → 枚举 → 资源收集”一次做全**，
保证 `UIManager.Instance.Open(...)` 能打开、编辑器模拟和 HostPlay 都能加载。

已有 UI 的改动走 `skier-ui-edit`；只排查 YooAsset/打包问题走 `skier-ui-yooasset`。

---

## 0. 先读（禁止凭记忆）

| 文件 | 作用 |
| --- | --- |
| `AGENTS.md` | 项目铁律：UI 必须用 UGUI 预制体 + SkierFramework，禁止运行期拼 UI |
| `Assets/Game/UI/UIConfig.json` | 运行时**唯一** UI 注册表 |
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIConfig.cs` | 读取/解析 `UIConfig.json`，`GetType` 规则 |
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIView.cs` | 生命周期、`UIControlData` 绑定、Canvas 自动挂载 |
| `Assets/Game/Scripts/UI/Framework/Editor/UICreateWindow.cs` | `Tools/UI管理` 的实现 |
| `Assets/BundleCollectorSetting.asset` | YooAsset `ui` 包收集范围（`Assets/Game/UI`） |
| `Assets/Game/UI/Home/HomeView.prefab` + `Assets/Game/Scripts/UI/Home/HomeView.cs` | 标准范例（根节点结构 / 绑定写法） |

注意：`Assets/Game/HOTS/UI/UIConfig.json` 是移植残留，**运行时不会读它**，不要改。

---

## 1. 铁律（违反任一条 = UI 打不开）

1. 只做 **UGUI 预制体**；禁止在运行时用 C# `new GameObject/Text/Button` 拼界面。
2. 预制体必须放在 `Assets/Game/UI/<Feature>/<Name>View.prefab`。
   YooAsset 的 `ui` 包只收集 `Assets/Game/UI`，放别处运行期一定加载失败。
3. 文件名 = 根节点名 = `UIView` 子类名 = `UIConfig.uiType` = `UIType` 枚举名（C# UI）。
4. 根节点必须有 `RectTransform`；只要 View 用了 `[ControlBinding]`，根节点必须有 `UIControlData`。
5. **所有** UI 预制体都必须在 `Assets/Game/UI/UIConfig.json` 登记；
   C# UI 还必须写进 `UIType` 枚举。没有“临时 UI”“测试 UI”例外。
6. 改预制体只改源预制体（Prefab Mode / Prefab Asset），不要改场景里的实例。
7. 预制体根节点**不要**挂 `Canvas`、`CanvasScaler`、`GraphicRaycaster`。
   运行时 `UIView.OnInit` 会自动补 Canvas + Raycaster，缩放由层 Canvas 统一负责。

---

## 2. 登记清单（缺一不可）

| # | 登记项 | 位置 | 缺失症状 |
| --- | --- | --- | --- |
| 1 | 预制体在收集目录 | `Assets/Game/UI/<Feature>/<Name>View.prefab` | `[LoadAssetAsync] ... location 不合法` |
| 2 | 根节点 `RectTransform` | 预制体根 | UI 加载后布局/锚点异常 |
| 3 | `UIControlData` | 预制体根（有绑定时） | 字段永远是 null，`can not find binding control` |
| 4 | View 类 `: UIView` | `Assets/Game/Scripts/UI/**/<Name>View.cs` | `NO VIEW TYPE` / `GetOrAddComponent(null)` |
| 5 | `UIType` 枚举 | `Assets/Game/Scripts/UI/Framework/UIViewBase/UIType.cs` | `uiType解析异常` |
| 6 | `UIConfig.json` 条目 | `Assets/Game/UI/UIConfig.json` | `未配置uiType:xxx` |
| 7 | `ui` 包发布（仅真机热更） | `Tools/UI热更/构建并发布 UI 包` | 真机仍是旧界面 |

> 3 是“有 `[ControlBinding]` 时”的硬要求；如果 View 只用 public 序列化字段（如 `SmokeView`），
> 可以没有 `UIControlData`，但新界面不推荐这种写法。

---

## 3. 命名与目录

```
Assets/Game/UI/
  Home/HomeView.prefab
  Bag/BagView.prefab
  Shop/ShopView.prefab          ← 新增示例

Assets/Game/Scripts/UI/
  Home/HomeView.cs
  Shop/ShopView.cs
```

- 功能名用 PascalCase，类/文件统一以 `View` 结尾。
- 同一个 `ui` 包里 **预制体文件名不能重复**（YooAsset 的寻址地址是文件名）。
  例如两个目录都叫 `ItemCell.prefab`，进 Play 会抛 `Location already exists: 'ItemCell'`。
- `uiType` 用类名，不要带命名空间。

---

## 4. 路线 A：人在编辑器里做（推荐给美术/策划）

1. 在 `Assets/Game/UI/<Feature>/` 做好 UGUI 预制体。
   - 根节点：`RectTransform`，锚点 `0,0 → 1,1`，`sizeDelta = 0`，Layer = `UI`。
   - 根节点按需挂 `UIControlData`，把要绑定的控件配进去。
     **先配绑定，再登记**，这样生成的脚本会自动带字段。
2. 选中预制体 → 菜单 `Tools/UI管理`。
3. 在窗口里拖入“UI预制体”，选择：
   - `不热更新UI（纯C#代码）`：新界面建议先选这个，逻辑最直观。
   - `热更新UI（C#壳+Lua）`：需要 Lua 热更逻辑时选。
   - `是否为窗口`：全屏页 = 否；浮层/弹窗 = 是（见下方语义）。
   - `UILayer设置`：一般 `NormalLayer`。
4. 点“创建UI”。工具会生成 View 脚本、写 `UIConfig.json`；
   **不要假设它写好了 `UIType`**（见坑 1）。
5. 立刻打开 `UIType.cs`，手动添加 `ShopView = 18,` 并把 `Max` 改成 `19`。
6. 等编译完成，打开预制体，在根节点上 Add Component 生成的 View 类。
7. 进 Play，用 `UIManager.Instance.Open(UIType.XXX)` 验证。

> `isWindow` 语义（`UILayerLogic.OpenUI/CloseUI`）：
> `false` = 全屏页，打开时把下层 UI `SetActive(false)`；
> `true` = 窗口/弹窗，下层 UI 只暂停不隐藏。

### 坑 1：`Tools/UI管理` 现在写不进 `UIType`（还有撞 `Max` 的风险）

`UICreateWindow` 用下面这行插入新枚举：

```csharp
Regex.Replace(dedupedStr, "Max,", $"{uiName},\n\t\tMax,");
```

当前 `UIType.cs` 结尾是 `Max = 18,`，字符串里根本没有 `Max,` 这个子串，正则匹配不到，
所以**“创建UI”后枚举通常不会新增**。后果连环：

- 运行时 `uiType解析异常`；
- 重开 `Tools/UI管理` 时该 UI 不在“已创建UI”列表；
- 再点任何“保存设置”，`SaveJson()` 会把这条 JSON 一起删掉（它只保留枚举里存在的 UI）。

即使把结尾改成不带值的 `Max,`，插入的 `NewView,` 隐式值也会等于 `Max` 的旧值，造成枚举值重复
（`type == UIType.Max` 误判）。

正确做法：创建后立刻手动写：

```csharp
ShopView = 18,
Max = 19,
```

规则：新值 = 当前 `Max` 的值，`Max` 再加 1；所有值显式写。

### 坑 2：`Tools/UI管理` 保存时会丢掉纯 Lua 条目

`UICreateWindow.SaveJson()` 只遍历 `uiNames`，而 `uiNames` 只来自 `UIType` 枚举成员。
纯 Lua UI（`isLuaUI: true`，不在枚举里，例如 `LuaDemoView`）不在其中。

**只要在 `Tools/UI管理` 里点了“创建UI / 保存设置 / 删除UI”，都要回头检查
`UIConfig.json` 里 `LuaDemoView` 这一条还在不在。** 被删了就从 git 恢复该条。

### 坑 3：工具不会自动给预制体挂 View 组件

`UICreateWindow` 只生成 `.cs` 文件，不 `AddComponent`。
不挂也能跑（`UIViewController.Load` 会 `GetOrAddComponent`），但 Inspector 序列化字段会丢默认值，
所以新界面请手动挂上。

---

## 5. 路线 B：Agent 自动做（MCP + execute_code）

> 先问清楚：界面名、属于哪个功能、全屏还是弹窗、层级、需要哪些控件。
> 不要自己编美术布局；结构/样式优先参考同类现有预制体。

### Phase 0 预检（只读）

1. 读 `Assets/Game/UI/UIConfig.json`，确认 `uiType` 不重复。
2. 读 `UIType.cs`，确认类名不重复、记下当前 `Max` 的值。
3. 查同名预制体（`Assets/Game/UI` 全目录），避免 YooAsset 文件名冲突。
4. `git status --short -- Assets/Game/UI Assets/Game/Scripts/UI` 看是否有未提交改动，
   避免覆盖用户正在改的文件。

### Phase 1 建预制体

文件：`Assets/Game/UI/<Feature>/<Name>View.prefab`

用 `execute_code` 建一个最小骨架（按实际设计补子物体）：

```csharp
// 目录需已存在：Assets/Game/UI/Shop
var root = new GameObject("ShopView", typeof(RectTransform));
root.layer = 5; // UI
var rt = (RectTransform)root.transform;
rt.anchorMin = Vector2.zero;
rt.anchorMax = Vector2.one;
rt.offsetMin = Vector2.zero;
rt.offsetMax = Vector2.zero;
rt.pivot = new Vector2(0.5f, 0.5f);

// 子物体示例：关闭按钮
var closeGo = new GameObject("CloseButton",
    typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button));
closeGo.layer = 5;
closeGo.transform.SetParent(root.transform, false);
var crt = (RectTransform)closeGo.transform;
crt.anchorMin = crt.anchorMax = new Vector2(1f, 1f);
crt.sizeDelta = new Vector2(96, 96);
crt.anchoredPosition = new Vector2(-64, -64);

// 绑定数据：name 必须和 View 字段名一致
var data = root.AddComponent<SkierFramework.UIControlData>();
data.ctrlItemDatas = new System.Collections.Generic.List<SkierFramework.CtrlItemData>
{
    new SkierFramework.CtrlItemData
    {
        name = "closeButton",
        type = "Button",
        targets = new UnityEngine.Object[] { closeGo.GetComponent<UnityEngine.UI.Button>() },
    },
};

var prefab = PrefabUtility.SaveAsPrefabAsset(root, "Assets/Game/UI/Shop/ShopView.prefab");
UnityEngine.Object.DestroyImmediate(root);
AssetDatabase.SaveAssets();
return prefab != null;
```

要点：
- 子物体命名 = 绑定字段名；`UIControlData` 里的 `name` 才是绑定 key（不是 GameObject 名，但保持一致）。
- 所有 UI 子物体 `layer = 5`。
- 不要给根节点加 `Canvas/Scaler`。
- 需要 TMP 文本时用 `TMPro.TextMeshProUGUI`，不要用旧 `Text`。

### Phase 2 写 View 类

文件：`Assets/Game/Scripts/UI/<Feature>/<Name>View.cs`

```csharp
using SkierFramework;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class ShopView : UIView
{
    [ControlBinding] public Button closeButton;
    [ControlBinding] public TMP_Text titleText;

    public override void OnOpen(object userData)
    {
        base.OnOpen(userData);
    }
}
```

- 类名必须等于 `uiType`。放全局命名空间或 `SkierFramework` 都能被 `UIConfig.GetType` 找到。
- 字段名 = `UIControlData` 条目名；类型必须和 `targets[0]` 的组件类型一致。
- 生命周期：`OnInit` 只一次，`OnOpen` 每次打开，`OnAddListener/OnRemoveListener` 管事件订阅。

### Phase 3 登记 `UIConfig.json`

在 `items` 末尾追加（保持 UTF-8、4 空格缩进，不动其他条目）：

```json
{
    "uiType": "ShopView",
    "path": "Assets/Game/UI/Shop/ShopView.prefab",
    "isWindow": false,
    "uiLayer": "NormalLayer",
    "isAutoNavigation": false,
    "isLuaUI": false,
    "luaModuleName": ""
}
```

- `path` 写带 `.prefab` 的完整 `Assets/...` 路径；`ResourceManager` 会自动去扩展名。
- `uiLayer` 必须是 `UILayer` 枚举名（`NormalLayer`/`TopLayer`/`TipLayer`...），写错会回退并报错。
- 纯 Lua UI 才允许 `isLuaUI: true` + 不在枚举里 + 无 View 类；C# UI 必须 `false`。

### Phase 4 登记 `UIType`

```csharp
ShopView = 18,
Max = 19,
```

### Phase 5 编译 + 挂 View 组件

1. `refresh_unity`（compile request），`read_console` 确认 0 error。
2. 关闭 Prefab Stage 后执行：

```csharp
var path = "Assets/Game/UI/Shop/ShopView.prefab";
var root = PrefabUtility.LoadPrefabContents(path);
try
{
    if (root.GetComponent<ShopView>() == null)
        root.AddComponent<ShopView>();
    PrefabUtility.SaveAsPrefabAsset(root, path);
}
finally
{
    PrefabUtility.UnloadPrefabContents(root);
}
AssetDatabase.SaveAssets();
return true;
```

> `PrefabUtility.LoadPrefabContents` 与已打开的 Prefab Stage 同时操作会冲突；
> 要么先 `close_prefab_stage`，要么直接在 Prefab Stage 里 Add Component 再保存。

### Phase 6 验证（全过才算登记完成）

1. `read_console`：0 error。
2. `manage_prefabs get_info`：
   `rootComponentTypes` 含 `RectTransform`、`UIControlData`、`ShopView`。
3. `UIConfig.json` 里条目路径与实际文件一致。
4. 进 Play 后，用 `execute_code` 检查 YooAsset 是否认识这个 location：

```csharp
return SkierFramework.YooAssetService.Instance.IsLocationValid(
    "Assets/Game/UI/Shop/ShopView");
```

5. 打开 UI，确认无绑定警告：

```csharp
SkierFramework.UIManager.Instance.Open(SkierFramework.UIType.ShopView, null).Forget();
return "open requested";
```

6. Console 里不应出现：
   - `can not find binding control of name`
   - `控件名字 [...] 重复`
   - `[LoadAssetAsync] ... 加载失败`

---

## 6. 常见坑速查

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| `未配置uiType:ShopView` | JSON 没登记 / `uiType` 拼错 | 补 `UIConfig.json` |
| `uiType解析异常` | 没进 `UIType` 枚举，或纯 Lua UI 忘了 `isLuaUI:true` | 补枚举 / 改标记 |
| `Location already exists` | `ui` 包里有同名文件（地址=文件名） | 重命名，保持包内唯一 |
| `[LoadAssetAsync] ... location 不合法` | 预制体不在 `Assets/Game/UI`，或没刷新 | 移回收集目录 + Refresh |
| 打开后按钮没反应 | `UIControlData` 缺条目 / 字段名不一致 | 三处同步（子物体名、条目名、字段名） |
| 界面被拉伸/双重缩放 | 预制体根挂了 `CanvasScaler` | 删掉，交给层 Canvas |
| 改了 `Tools/UI管理` 后 LuaDemo 消失 | `SaveJson` 丢纯 Lua 条目 | 从 git 恢复该条 |
| 枚举判断异常 / `uiType解析异常` | `Tools/UI管理` 没写入枚举，或新枚举值撞了 `Max` | 手动补显式编号，`Max` 加 1 |

---

## 7. 禁止事项

- ❌ 运行期 `new Canvas/Text/Button` 拼界面（AGENTS.md 明令禁止）。
- ❌ 把 UI 预制体放到 `Assets/Game/UI` 之外（`Assets/Game/HOTS/UI`、`Assets/Resources`、场景目录）。
- ❌ 改 `Assets/Game/HOTS/UI/UIConfig.json`（残留文件，运行时不用）。
- ❌ 用 `Resources.Load` 加载 UI 预制体（必须走 `UIManager` / `ResourceManager`）。
- ❌ 为了“省事”把新预制体直接改名成已登记的名字（会撞 YooAsset 地址）。
- ❌ 只改 `UIConfig.json` 不建 View 类（C# UI 会加载出空壳/报错）。
- ❌ 只挂 View 组件不登记（`UIManager.Open` 找不到）。
