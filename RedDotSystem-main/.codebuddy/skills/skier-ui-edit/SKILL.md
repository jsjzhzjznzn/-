---
name: skier-ui-edit
description: 安全修改 RedDotSystem-main 里已经登记的 UGUI UI 预制体（已在 Assets/Game/UI/UIConfig.json 注册的界面）：定位注册信息、Prefab Mode 编辑、同步 UIControlData 与 [ControlBinding] 字段、保持 UIConfig/UIType/路径不被破坏、改完验证。当用户要改已有界面/弹窗/预制体的布局、控件、绑定、名称或配置时使用。Use when modifying an already-registered UI prefab in this project.
---

# 修改已登记的 UI 预制体

适用：预制体已经出现在 `Assets/Game/UI/UIConfig.json`，现在要改布局、加/删控件、改绑定、
改窗口属性、改名字或路径。

- 新建界面 → `skier-ui-create`
- 查 YooAsset / 打包 / 热更 → `skier-ui-yooasset`

---

## 1. 先定位“登记信息”（只读，必须做）

1. 从 `Assets/Game/UI/UIConfig.json` 找到条目：

```json
{
    "uiType": "HomeView",
    "path": "Assets/Game/UI/Home/HomeView.prefab",
    "isWindow": false,
    "uiLayer": "NormalLayer",
    "isLuaUI": false,
    "luaModuleName": ""
}
```

2. 记下 `uiType` / `path` / `isWindow` / `uiLayer`，改完要逐项核对没有被动过。
3. 找到 View 类：`Assets/Game/Scripts/UI/**/<uiType>.cs`（类名不一定等于文件名，但本项目约定相等）。
4. `manage_prefabs get_info(prefab_path)` 记录当前的：
   - `guid`（路径没变时必须保持不变）
   - `rootObjectName`
   - `rootComponentTypes`
5. `git status --short -- <prefab路径> <cs路径> <UIConfig.json>`，有未提交改动先告知用户，别覆盖。

> 预制体不在 `Assets/Game/UI` 下、或 JSON 里没有条目 → 这不是“已登记预制体”，
> 不要在这个 skill 里硬改，先按 `skier-ui-create` 完成登记。

---

## 2. 改动类型 → 必须同步的地方

| 你想改 | 要动哪里 | 是否影响热更 |
| --- | --- | --- |
| 布局、颜色、图片、层级顺序 | 只改预制体 | 可热更 |
| 新增/删除可交互控件 | 预制体 + `UIControlData` + View 字段 | **C# 改动，需重新出客户端** |
| 改绑定字段名 | 子物体名（建议）+ `CtrlItemData.name` + View 字段 | C# 改动 |
| 改 `isWindow` / `uiLayer` | 只改 `UIConfig.json`（或 `Tools/UI管理`） | 可热更 |
| 改预制体文件名/路径 | 预制体 + `UIConfig.path`（`uiType` 不变时） | 可热更 |
| 改 `uiType` / View 类名 | 预制体文件名 + 根节点名 + 类名 + 枚举 + JSON | C# 改动 |
| 改 View 逻辑 | 只改 `.cs` | C# 改动 |
| 纯 Lua UI（`isLuaUI:true`） | 预制体 + Lua 模块 + JSON | 可热更（无 C#） |

**结论：只改美术/布局 = 最安全；加控件字段 = 一定要重新编译客户端，不能只发 ui 包。**

---

## 3. 标准编辑流程（MCP / 编辑器）

### 3.1 打开源预制体

用 Prefab Stage，**不要**在场景里选一个实例改（实例改动不会回写源预制体）：

```text
manage_prefabs { action: "open_prefab_stage",
                 prefab_path: "Assets/Game/UI/Home/HomeView.prefab" }
```

确认当前阶段：

```text
读资源 mcpforunity://editor/prefab-stage
```

### 3.2 结构编辑

- 加子物体：`manage_gameobject create`，`layer: "UI"`，父节点指到预制体里的目标节点。
- 加组件：`manage_components add`（`Image` / `Button` / `TMP_Text` / `CanvasGroup` ...）。
- 改 RectTransform：锚点、`sizeDelta`、`anchoredPosition` 按设计稿设置。
- 删除控件：先确认没有 View 字段和 `UIControlData` 条目还在引用它。
- 所有新 UI 节点 `layer = 5 (UI)`。
- 不要给预制体根加 `Canvas` / `CanvasScaler` / `GraphicRaycaster`。
- 子界面/嵌套面板如果自带 Canvas，必须同时有 `GraphicRaycaster`，否则点击射线收不到。

### 3.3 绑定三处同步（新增控件时）

绑定能否成功只看三件事，名字必须完全一致：

1. 预制体里的控件对象名（建议一致）
2. 根节点 `UIControlData.ctrlItemDatas[i]` 的 `name`
3. View 类字段名 `[ControlBinding] public Button closeButton;`

运行期是按 **字段名 → ctrlItemDatas.name** 匹配的；对象名不一致不会报错，但排查困难，建议统一。

#### 方式 A：人在 Inspector 里改（推荐）

1. 在预制体根节点的 `UIControlData` 上点 `+` 添加条目。
2. `name` 填字段名，`targets` 拖控件（类型会自动修正）。
3. 右键 `UIControlData` 组件 → **复制代码到剪贴板(Public/Private)** → 粘到 View 类的字段区。
4. 不要删除再重建 `UIControlData` 组件（会丢全部绑定）。

> `Tools/UI管理` 的警告原文就是这个意思：
> “修改 UIControlData 时不要重建删了创建，而是右键复制代码到剪贴板，主动粘贴到对应代码块”。

#### 方式 B：Agent 用 execute_code 改（先关闭 Prefab Stage）

```csharp
var path = "Assets/Game/UI/Home/HomeView.prefab";
var root = PrefabUtility.LoadPrefabContents(path);
try
{
    var data = root.GetComponent<SkierFramework.UIControlData>();
    var target = root.transform.Find("CloseButton")?.GetComponent<UnityEngine.UI.Button>();
    if (data == null || target == null) return "missing UIControlData or target";
    if (data.ctrlItemDatas == null)
        data.ctrlItemDatas = new System.Collections.Generic.List<SkierFramework.CtrlItemData>();

    var item = new SkierFramework.CtrlItemData
    {
        name = "closeButton",
        type = "Button",
        targets = new UnityEngine.Object[] { target },
    };
    data.ctrlItemDatas.Add(item);

    // 触发框架自带的校验/类型修正（编辑器专有方法）
    if (!data.CorrectComponents()) return "CorrectComponents failed";
    if (data.subUIItemDatas != null && !data.CheckSubUIs()) return "CheckSubUIs failed";

    PrefabUtility.SaveAsPrefabAsset(root, path);
}
finally
{
    PrefabUtility.UnloadPrefabContents(root);
}
AssetDatabase.SaveAssets();
return "ok";
```

改完后在 View 类里补字段：

```csharp
[ControlBinding] public Button closeButton;
```

**删除控件**：从 `ctrlItemDatas` 移除条目 + 删除 View 字段。
只删其中之一会留下警告或空字段。

### 3.4 保存

```text
manage_prefabs { action: "save_prefab_stage", prefab_path: "..." }
manage_prefabs { action: "close_prefab_stage" }
```

> MCP 直接 `SaveAsPrefabAsset` 可能绕过 `UIBindingPrefabSaveHelper` 的自动修正。
> 用 execute_code 保存时，务必像上面那样主动调用 `CorrectComponents()` / `CheckSubUIs()`。

### 3.5 编译与验证

1. `refresh_unity`（有 C# 改动时 compile request）。
2. `read_console`：0 error；警告里不应出现：
   - `can not find binding control of name [xxx] in prefab`
   - `控件名字 [xxx] 第 1 项与第 N 项重复`
   - `控件 [xxx] 第 1 项不是 xxx 类型`
   - `[LoadAssetAsync] ... 加载失败`
3. `manage_prefabs get_info` 再查一次：
   - 路径、GUID 与改前一致（没做移动/改名的话）
   - `rootComponentTypes` 仍包含 `UIControlData` 和 View 组件
4. `UIConfig.json` 条目逐字段核对（`uiType/path/isWindow/uiLayer/isLuaUI`）。
5. 进 Play，打开界面，确认布局和交互：
   ```csharp
   SkierFramework.UIManager.Instance.Open(SkierFramework.UIType.HomeView, null).Forget();
   ```
6. 需要更彻底时跑 `skier-ui-yooasset` 里的“UI 登记自检”代码。

---

## 4. 改名 / 移动预制体（最容易把登记弄坏）

`uiType` 不变、只挪路径：

1. 在 Unity 里移动/重命名（不要用资源管理器直接拖，避免 .meta 丢失）。
2. 改 `UIConfig.json` 的 `path`。
3. `refresh_unity`，进 Play 验证 `IsLocationValid`。

`uiType` 也要改（例如 `ShopView` → `StoreView`）：

必须同步 **5 处**：

1. 预制体文件名
2. 预制体根节点名
3. View 类名
4. `UIType` 枚举（新增值，旧的删掉或保留兼容；`Max` 只增不减）
5. `UIConfig.json` 的 `uiType` + `path`

同时检查所有 `UIManager.Open(UIType.ShopView)`、`Close`、`GetView<ShopView>` 调用点。
漏一个就编译失败或打开旧界面。

---

## 5. 热更边界（改之前先回答）

- 只改预制体 / `UIConfig.json` / Lua → 可以只发 `ui` 包。
  `Tools/UI热更/构建并发布 UI 包` → 客户端 `CheckUIUpdateAsync` → `ReloadUIConfig`。
- 改了 C#（加 `[ControlBinding]` 字段、改 View 逻辑、改枚举）→ **必须重新出客户端**。
  只发 ui 包不会更新 C# 程序集。
- 纯 Lua UI（`isLuaUI: true`）不在此限制内。

`ReloadUIConfig` 会先 `CloseAll` 再重建注册表，改完包后所有 UI 会重开，属正常现象。

---

## 6. 绝对不能做

- ❌ 在场景里改预制体实例，然后以为源预制体变了。
- ❌ 删除再重建根节点 `UIControlData`（全部绑定清零）。
- ❌ 改完预制体不保存就关 Prefab Stage。
- ❌ 只改预制体路径不改 `UIConfig.path`（运行期仍加载旧路径）。
- ❌ 只改 `uiType` 不改枚举/类名（`uiType解析异常`）。
- ❌ 给根节点加 `CanvasScaler`（和层 Canvas 双重缩放）。
- ❌ 在 Play 模式里改预制体（退出 Play 就丢）。
- ❌ 用 `PrefabUtility.SaveAsPrefabAsset` 从场景实例另存成新 prefab（GUID/登记全部变化）。
- ❌ 在 `Tools/UI管理` 窗口开着时直接改 `UIConfig.json`
  （窗口缓存 `uiJsonDatas`，之后点“保存设置”会覆盖你的修改）。
  改 JSON 前先关掉该窗口，或全程只用窗口改。
- ❌ 用 `Tools/UI管理` 创建/保存后不检查 JSON：
  `SaveJson()` 只保留 `UIType` 枚举里的条目，纯 Lua 条目（如 `LuaDemoView`）
  和“枚举没写成功的新 UI”都会被静默删掉。

---

## 7. 失败对照表

| 现象 | 原因 | 修复 |
| --- | --- | --- |
| 改完没变化 | 改的是场景实例 / 没保存 Prefab Stage | 打开源预制体重改并保存 |
| 字段为 null + `can not find binding control` | `ctrlItemDatas` 没条目或名字不一致 | 三处同步 |
| `控件名字 [...] 重复` | 两个条目同名 | 删除重复项 |
| `控件 [...] 不是 xxx 类型` | 字段类型/条目 type 与目标组件不匹配 | 改 `type` 或字段类型 |
| `控件 ... 不是当前 Prefab 下的控件` | targets 拖了场景对象/别的预制体 | 重新拖当前预制体里的组件 |
| `未配置uiType` | JSON 被覆盖/条目丢失 | 从 git 恢复并重登 |
| `Location already exists` | 新名字和包内其他文件重名 | 换唯一文件名 |
| 打开后层级乱 | 改了层 Canvas / 根 CanvasScaler | 移除预制体自带 Scaler，检查 `uiLayer` |
