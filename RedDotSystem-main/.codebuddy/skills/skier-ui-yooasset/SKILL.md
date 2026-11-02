---
name: skier-ui-yooasset
description: RedDotSystem-main 的 UI 与 YooAsset 联动：ui 包收集规则、location 路径规则、编辑器模拟/HostPlay 切换、UI 热更发布、UIConfig 与预制体加载链路、以及“已登记 UI 是否真的能被加载”的一键自检和排错。当用户问 UI 加载失败、YooAsset 收集、ui 包打包发布、热更更新界面、资源地址/路径问题时使用。Use for UI + YooAsset integration, packaging, hot update, and load-failure troubleshooting in this project.
---

# UI × YooAsset 联动

一句话：**UI 预制体和 `UIConfig.json` 必须被 `ui` 包收集，`UIManager` 才能按 `UIConfig.path` 加载；
编辑器模拟每次进 Play 自动重建清单，真机 HostPlay 必须构建并发布 `ui` 包。**

相关代码：

| 文件 | 职责 |
| --- | --- |
| `Assets/Game/Scripts/UI/Framework/Common/YooAssetService.cs` | 包初始化、路由、加载、热更 |
| `Assets/Game/Scripts/UI/Framework/Common/ResourceManager.cs` | 路径归一化、实例化、缓存/池 |
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIConfig.cs` | 通过 YooAsset 读 `UIConfig.json` |
| `Assets/Game/Scripts/UI/Framework/UIManager.cs` | 打开/关闭、包更新后 `ReloadUIConfig` |
| `Assets/Game/Scripts/UI/Framework/Editor/UIPackagePublisher.cs` | 构建并发布 `ui` 包 |
| `Assets/BundleCollectorSetting.asset` | 收集目录/寻址/分包规则 |

---

## 1. 包与收集拓扑

`ui` 包（`YooAssetService.DefaultPackageName = "ui"`）包含三组：

| 收集目录 | 分组 | 内容 |
| --- | --- | --- |
| `Assets/Game/UI` | GameUI | UI 预制体 + `UIConfig.json` |
| `Assets/Content/LuaScripts` | LuaScripts | Lua 模块（`.lua.txt`） |
| `Assets/Game/Resources/ui` | ItemIcons | 物品图标等 |

其他相关：

| 目录 | 包 | 说明 |
| --- | --- | --- |
| `Assets/Game/Art/Battle` | `battle` | `PackageNames` 里当前**未初始化**（空包会让模拟构建失败） |
| `Assets/Content/HotContent` | `activity` | 由 `LuaActivityHost` 自行管理，不走 `YooAssetService` |

收集规则（四个包一致）：

```yaml
EnableAddressable: 1
SupportExtensionless: 1
AddressRuleName: AddressByFileName      # 地址 = 文件名（不含扩展名）
PackRuleName: PackSeparately            # 每个资源一个 bundle → 改一个预制体只出一个新包
FilterRuleName: CollectAll
```

### 关键结论

1. **预制体不在 `Assets/Game/UI` 就不会进 `ui` 包** → 运行期一定加载失败。
2. **寻址地址是文件名**：`ui` 包内文件名必须唯一。
   两个 `ItemCell.prefab` 会导致清单加载抛 `Location already exists: 'ItemCell'`。
3. `SupportExtensionless` + 本项目对清单的扩展映射，使
   `Assets/Game/UI/Shop/ShopView` 和 `Assets/Game/UI/Shop/ShopView.prefab` 都能命中。
4. 不需要为每个 UI 单独配置收集器；放到 `Assets/Game/UI` 即被收集。
   但**登记**仍然要写 `UIConfig.json`（运行时注册表），两件事不能互相替代。

---

## 2. 运行时加载链路

```text
UIManager.Open(UIType.ShopView)
  └─ UIViewController.Load
       ├─ UIManager 初始化时：UIConfig.GetAllConfigs()
       │    └─ ResourceManager.LoadAssetAsync<TextAsset>("Assets/Game/UI/UIConfig.json")
       │         └─ YooAssetService.GetPackageByLocation → ui
       │         └─ pkg.LoadAssetAsync<TextAsset>("Assets/Game/UI/UIConfig")   （去扩展名）
       ├─ ResourceManager.InstantiateAsync(cfg.path)
       │    └─ NormalizeToLocation：斜杠归一化 + 去扩展名
       │    └─ YooAssetService.GetPackageByLocation("Assets/Game/UI/...") → ui
       │    └─ pkg.InstantiateAsync(location) → AssetHandle
       │    └─ 实例缓存 / 对象池
       └─ go.GetOrAddComponent(viewType) + UIControlData.BindDataTo(view)
```

要点：

- `UIConfig.GetAllConfigs` 解析完会立即 `UnLoadAsset(UIConfigPath)`。
  **Play 中直接改 `UIConfig.json` 不会生效**，要等 `ReloadUIConfig()`。
- `ResourceManager` 持有 `AssetHandle` 与引用计数；不要自己 `Destroy` UI 或手动 Release。
- 路径前缀路由（最长匹配）：
  `Assets/Game/UI` / `Assets/Content/LuaScripts` / `Assets/Game/Resources/ui` → `ui`；
  `Assets/Game/Art/Battle` → `battle`；未命中走默认 `ui`。

---

## 3. 路径规则（`NormalizeToLocation`）

```csharp
path = path.Trim().Replace('\\', '/');
if (!path.StartsWith("Assets/") && !path.StartsWith("Packages/"))
    path = "Assets/" + path;
// 兼容旧前缀 Assets/Game/Resources/（会去掉该前缀）
// 去掉最后一个 '/' 之后的扩展名
```

因此：

| 写法 | 结果 |
| --- | --- |
| `Assets/Game/UI/Shop/ShopView.prefab` | `Assets/Game/UI/Shop/ShopView` |
| `Assets/Game/UI/Shop/ShopView` | 同上 |
| `Assets/Game/UI/UIConfig.json` | `Assets/Game/UI/UIConfig` |
| `GameUI/ShopView` | `Assets/GameUI/ShopView`（不推荐，别用相对路径） |

**统一规范：`UIConfig.path` 永远写带 `.prefab` 的完整 `Assets/Game/UI/...` 路径。**

---

## 4. 编辑器模拟 vs 真机 HostPlay

| | 编辑器（默认） | 真机 / HostPlay |
| --- | --- | --- |
| 模式 | `EditorSimulate` | `HostPlay`（远端不可达时自动降级 `OfflinePlay`） |
| 资源来源 | AssetDatabase | 优先 `StreamingAssets/yoo/ui` 首包；远端可用时按清单增量下载 |
| 清单 | 每次初始化 `EditorSimulateBuildInvoker.Build("ui")` 自动重建 | `RequestPackageVersion` → 远端清单 |
| 新建/移动 UI 后 | `Refresh` + **重进 Play** | 构建并发布 `ui` 包 + 客户端检查更新 |
| 需要打包吗 | 不需要 | 需要 |

### 编辑器里新增/修改 UI 后的正确动作

1. 保存预制体和 `UIConfig.json`。
2. `refresh_unity`（等编译结束）。
3. **退出 Play 再进 Play**（模拟清单在进入 Play 初始化时重建；Play 中不会热感知新文件）。
4. 验证 `IsLocationValid`。

### 切换模式

`Tools/UI热更/切换资源模式（编辑器模拟 / HostPlay）`

- 通过 PlayerPrefs 记忆，**重进 Play 生效**。
- HostPlay 需要先启动 `competeserves` 的静态服务（默认 `127.0.0.1:8899`）并发布过 `ui` 包。

### 首包兜底（重要）

`UIPackagePublisher` 用 `BundledCopyOption.OnlyCopyAll` 把版本/清单/资源一并拷到
`Assets/StreamingAssets/yoo/ui`，所以：

1. **出包前必须执行一次 `Tools/UI热更/构建并发布 UI 包`**，否则安装包里没有 UI 资源。
   `UIBuildPreflight` 会在 Build 前检查并给 Warning。
2. 打包后启动：先探测 `{RemoteServerRoot}/ui/ui.version`（3 秒超时）。
   - 可达 → `HostPlay`：读首包 + 增量下载远端差异。
   - 不可达 → 自动降级 `OfflinePlay`：只读首包，**断网也能进登录界面**。
3. 远端地址不再硬编码：`Tools/UI热更/设置远端地址（yoo_remote.json）`
   写 `Assets/StreamingAssets/yoo_remote.json`，运行时优先读取。

> 在线首启时 YooAsset 仍会把资源下载进沙盒缓存（首包只保证"能用"，
> 不会自动导入缓存），这是已知的一次性开销。

---

## 5. UI 热更（HostPlay）

### 客户端入口

- **登录成功后自动检查**：`LoginSceneController.EnterGameAsync()`
  → `YooAssetService.CheckAndUpdatePackageAsync("ui")`
  - 有新版：登录界面显示「发现新版本，更新中 xx%」，下载完才切 `zhuye`
  - 无新版 / 失败 / 超时（120s）：直接进游戏，当前版本继续可用
- 按钮/手动触发：`await UIManager.Instance.CheckUIUpdateAsync();`

流程（安全版：先下载、后激活）：

```text
CheckAndUpdatePackageAsync
  ├─ RequestPackageVersion          // 只查版本，不动激活清单
  ├─ PrefetchManifest               // 预取新清单（只落缓存，不激活）
  ├─ HostPlay: CreateResourceDownloader(prefetch) + 下载差异
  ├─ LoadPackageManifest(new)       // 全部成功后才激活
  └─ OnPackageUpdated("ui")
       └─ UIManager.ReloadUIConfig()
            ├─ CloseAll（含常驻 UI）
            ├─ 重新读取 UIConfig.json
            └─ 重建 UI 注册表
```

> 中途失败/取消时 `ActiveManifest` 仍是旧版本，
> 不会出现“清单换了但资源没下完”的半套状态。

### 热更能改什么

| 改动 | 只发 ui 包 | 必须出新客户端 |
| --- | --- | --- |
| 预制体布局/美术 | ✅ | |
| `UIConfig.json` 的 `isWindow` / `uiLayer` | ✅ | |
| 已有界面的 Lua 逻辑 | ✅ | |
| `UIConfig.path` 换预制体 | ✅ | |
| 新增/改名 C# View 类 | | ✅ |
| 新增 `[ControlBinding]` 字段 | | ✅ |
| 改 `UIType` 枚举 | | ✅ |
| 新增纯 Lua UI（`isLuaUI:true`） | ✅ | |

> 新界面如果可以接受纯 Lua 逻辑，用 `isLuaUI: true` + `LuaOnlyView` + Lua 模块，
> 就能做到“预制体 + JSON + Lua”全热更，不动 C#。

---

## 6. 构建与发布

菜单：`Tools/UI热更/构建并发布 UI 包`

- 管线：SBP（`ScriptableBuildPipeline`）
- 包名：`ui`
- 压缩：LZ4；文件名：HashName；`EnableSharePackRule = true`
- 输出：`<项目同级>/competeserves/HotContent/ui`
- 首包：同一批文件会拷到 `Assets/StreamingAssets/yoo/ui`（`OnlyCopyAll`），随安装包发布
- 发布动作：把版本目录里的 `.bundle` / `.bytes` / `.hash` 拷平，并写 `ui.version`
- 远端拼接：`YooRemoteService.ForPackage("ui", "http://127.0.0.1:8899")`
  → `http://127.0.0.1:8899/ui/<fileName>`

发布前检查：

1. 预制体和 `UIConfig.json` 已保存、Unity 已 Refresh。
2. `UIConfig.json` 是合法 JSON（多余逗号/注释会让解析失败）。
3. 没有运行期脚本引用 `UnityEditor`（要包在 `#if UNITY_EDITOR` 里），否则 SBP 构建报 `CS0246`。
4. 没有同名资源（地址冲突）。
5. 新 UI 的 C# 代码已经包含在本次客户端里（热更不更新程序集）。

---

## 7. 一键自检：UI 登记 + 预制体结构

在编辑器里用 `execute_code` 跑（只读，不改资源）。
它会检查：配置里的路径是否存在、根节点是否有 `UIControlData`、View 类型/组件是否存在、
`Assets/Game/UI` 下有没有漏登记的预制体。

```csharp
var wrapper = JsonUtility.FromJson<SkierFramework.UIConfigJsonList>(
    System.IO.File.ReadAllText("Assets/Game/UI/UIConfig.json"));
string projectRoot = System.IO.Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/');
var issues = new System.Collections.Generic.List<string>();
var registered = new System.Collections.Generic.HashSet<string>();
int count = 0;

foreach (var item in wrapper.items)
{
    count++;
    registered.Add(item.path);
    string full = System.IO.Path.Combine(projectRoot, item.path);
    if (!System.IO.File.Exists(full))
    {
        issues.Add("MISSING PREFAB: " + item.uiType + " -> " + item.path);
        continue;
    }

    var go = UnityEditor.AssetDatabase.LoadAssetAtPath<GameObject>(item.path);
    if (go == null)
    {
        issues.Add("LOAD FAIL: " + item.path);
        continue;
    }

    if (go.GetComponent<SkierFramework.UIControlData>() == null)
        issues.Add("NO UIControlData: " + item.path);

    if (item.isLuaUI)
    {
        if (go.GetComponent<SkierFramework.LuaOnlyView>() == null)
            issues.Add("NO LuaOnlyView: " + item.path);
    }
    else
    {
        var t = SkierFramework.UIConfig.GetType(item.uiType);
        if (t == null) t = SkierFramework.UIConfig.GetType("SkierFramework." + item.uiType);
        if (t == null) issues.Add("NO VIEW TYPE: " + item.uiType);
        else if (!typeof(SkierFramework.UIView).IsAssignableFrom(t))
            issues.Add("NOT UIView: " + t.FullName);
        else if (go.GetComponent(t) == null)
            issues.Add("PREFAB MISSING VIEW COMPONENT: " + t.Name);
    }
}

foreach (var p in System.IO.Directory.GetFiles(
    System.IO.Path.Combine(projectRoot, "Assets/Game/UI"), "*.prefab",
    System.IO.SearchOption.AllDirectories))
{
    string rel = p.Substring(projectRoot.Length + 1).Replace('\\', '/');
    if (!registered.Contains(rel)) issues.Add("NOT REGISTERED: " + rel);
}

return "entries=" + count + " issues=" + issues.Count + "\n" +
       string.Join("\n", issues.ToArray());
```

### 结果怎么读

| 输出 | 严重级别 | 处理 |
| --- | --- | --- |
| `MISSING PREFAB` | 硬错误 | 路径写错/预制体被删 → 修 `UIConfig.path` |
| `NO VIEW TYPE` | 硬错误 | 补 View 类 / 补 `UIType` / 等编译 |
| `NOT UIView` | 硬错误 | 类没继承 `UIView` |
| `NOT REGISTERED` | 硬错误 | 预制体漏登记 → 补 `UIConfig.json` |
| `LOAD FAIL` | 硬错误 | 资源导入失败/GUID 损坏 |
| `PREFAB MISSING VIEW COMPONENT` | 规范问题 | 运行期会自动补，但 Inspector 序列化字段会丢；建议手动挂 |
| `NO UIControlData` | 看情况 | View 只用 public 序列化字段时可接受（如 `SmokeView`）；用了 `[ControlBinding]` 则必须补 |
| `NO LuaOnlyView` | 规范问题 | 运行期会自动补；建议挂上便于配 `luaModuleName` |

### 运行时再补一刀

进 Play 后：

```csharp
return SkierFramework.YooAssetService.Instance.IsLocationValid(
    "Assets/Game/UI/Home/HomeView");
```

返回 `false` = 清单里没有这个 location（没收集/没刷新/没重进 Play/路径错）。

---

## 8. 排错对照表

| 日志 / 现象 | 原因 | 修复 |
| --- | --- | --- |
| `未找到配置：Assets/Game/UI/UIConfig.json` | JSON 不在 `ui` 包 / YooAsset 未初始化 | 确认在收集目录；检查 `InitializeAsync` |
| `UIConfig.json 解析失败` | JSON 语法错误 | 用 JSON 校验器修 |
| `UIConfig.json 中的：... uiType解析异常` | 没进 `UIType` 枚举（C# UI） | 补枚举；纯 Lua 加 `isLuaUI:true` |
| `uiLayer 不存在：XxxLayer（UIName）` | JSON 层级名拼错 | 改成 `UILayer` 枚举名 |
| `存在相同的uiType:xxx` | JSON 有重复条目 | 删重复项 |
| `未配置uiType:xxx， 请检查UIConfig.cs！` | JSON 没登记 | 补 `UIConfig.json` |
| `[LoadAssetAsync] ... 加载失败：YooAsset 尚未初始化！` | 初始化没跑完/失败 | 检查 `YooAssetService.InitializeAsync`、模式、远端 |
| `[LoadAssetAsync] ... 加载失败：资源包未初始化或 location 不合法！` | 路径不在清单 / 拼错 | `IsLocationValid` 验证；确认在 `Assets/Game/UI` |
| `[YooAssetService] 未找到资源包：...` | 路由前缀不匹配 / 包没建 | 检查 `LocationRoutes` 与 `PackageNames` |
| `[InstantiateAsync] 加载失败：...` | 上面两种失败的最终表现 | 同上 |
| `Location already exists: 'X'` | `ui` 包内文件名重复 | 重命名保持唯一 |
| `Package version not found` / 版本请求失败 | 没发布 / `ui.version` 没铺 / 服务没起 | 重新发布；检查 `competeserves/HotContent/ui` |
| 清单/下载失败 | 远端地址不对 / 文件缺失 | 检查 `RemoteServerRoot`、`YooRemoteService`、网络 |
| 真机打开还是旧界面 | 没发版 / 没调 `CheckUIUpdateAsync` / 缓存 | 发新版本号；确认更新成功；清缓存 |
| 热更后所有 UI 关闭重开 | `ReloadUIConfig` 的正常行为 | 无需处理 |
| 构建报 `CS0246` / 找不到 UnityEditor | 运行期脚本引用了编辑器 API | 包 `#if UNITY_EDITOR` |
| 预制体在编辑器能开、真机打不开 | 只在 AssetDatabase 存在，没进包 | 确认在 `Assets/Game/UI` 且构建成功 |

---

## 9. 禁止事项

- ❌ 把 UI 预制体/图标放在收集目录外，然后指望 `UIManager` 能加载。
- ❌ 在 `ui` 包里制造同名文件（地址 = 文件名）。
- ❌ 用 `Resources.Load` 或 `AssetDatabase` 在运行时代替 YooAsset。
- ❌ 热更 ui 包却改了 C#，然后声称“已经热更了”。
- ❌ 改 `Assets/Game/HOTS/UI/UIConfig.json`。
- ❌ 把 `LoginPanel.prefab` 拖回 `LOGIN.scene`（被场景引用 = 打进安装包，热更不生效）。
      登录界面由 `LoginSceneBootstrap` 从 `Assets/Game/UI/Login/LoginPanel.prefab` 加载。
- ❌ 使用 `Assets/Game/HOTS/UI/Prefabs/` 下的旧预制体（Dating / jiaru / room / Xuanze / bloodpanel）：
      它们不在任何收集器里、也没登记 `UIConfig`，永远不会热更（当前 0 引用）。
      要启用先把预制体移到 `Assets/Game/UI/` 再登记。
- ❌ 在 Play 中改 `UIConfig.json` 后期望立刻生效（需 `ReloadUIConfig`）。
- ❌ 手动 `Destroy` 由 `UIManager` 打开的 UI（会破坏缓存/引用计数）。
- ❌ 把场景/战斗资源随便拖进 UI 预制体：依赖会被 share pack 打进 `ui` 包，包体膨胀甚至构建失败。
