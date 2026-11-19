# UIConfig 热更打通方案

## 1 现状

`UIConfig.json` 是 UI 框架的注册表（`uiType → 预制体路径 / 层次 / 是否窗口 / Lua 模块名`），当前状态：

| 环节 | 现状 | 证据 |
| --- | --- | --- |
| 数据位置 | 在 YooAsset 的 `ui` 包里，不在客户端内置目录 | `Assets/BundleCollectorSetting.asset`：`PackageName: ui` → `GroupName: GameUI`（`GroupDesc: "UI 预制体 + UIConfig"`，`CollectPath: Assets/Game/UI`） |
| 运行时读取 | 作为 TextAsset 走资源系统读，路径固定 | `Assets/Game/Scripts/UI/Framework/UIViewBase/UIConfig.cs`：`ResourceManager.Instance.LoadAssetAsync<TextAsset>("Assets/Game/UI/UIConfig.json")` |
| 换表刷新 | 已实现，含失败保护 | `UIManager.ReloadUIConfig()`：重读 → 关全部界面 → 释放实例与句柄 → 换注册表 → 清 Lua 模块缓存；解析为空时保留旧表 |
| 触发链路 | 已接好但无人调用 | `YooAssetService.UpdatePackageAsync(pkg)` → 抛 `OnPackageUpdated` → `UIManager` 收到后 `ReloadUIConfig()` |

三个缺口导致它现在不可能热更：

1. **没有调用点**：`UpdatePackageAsync` 全工程只有一个调用者 `UIManager.CheckUIUpdateAsync()`，而它自己没有任何调用者。
2. **下载模式不成立**：`YooAssetService.RemoteServiceFactory` 从未被赋值，`CreateOptions` 在 HostPlay 分支拿不到远程服务时会**静默退回 OfflinePlay**；且 `GetDefaultRunMode()` 在真机返回 `OfflinePlay`（只读包内）。
3. **没有发布通道**：菜单里只有 `Tools/活动热更/构建并发布活动包`，没有 ui 包的构建与发布。

## 2 设计

### 2.1 运行模式开关

新增 PlayerPrefs 键 `UIConfig_UseHostPlayMode`（默认 `0` = 关，保持现有行为）。

开启时在 `RuntimeInitializeOnLoadMethod(SubsystemRegistration)` 阶段完成两件事：

```csharp
YooAssetService.ForceRunMode = YooAssetRunMode.HostPlay;
YooAssetService.RemoteServiceFactory = packageName =>
    packageName == YooAssetService.DefaultPackageName
        ? YooRemoteService.ForPackage(packageName, HotContentRoot + "/" + packageName)
        : null;
```

选 `SubsystemRegistration` 而不是 `BeforeSceneLoad`：前者早于后者，能保证在 `ModuleHub` 初始化资源包（`ModuleHub.Boot` → `ResourceManager.InitializeAsync`）之前生效，避免出现「开关开了但包已经按 OfflinePlay 初始化完」的竞态。

### 2.2 触发时机

`UIConfigHotUpdate` 自举后订阅 `LoginResponseReceived`，登录成功且开关开启时调用 `UIManager.Instance.CheckUIUpdateAsync()`，**不阻塞登录**：失败只记日志（`LastError`），登录流程照常进主页。

不做「运行中手动刷新」入口：`ReloadUIConfig` 会拆掉并重建全部界面（含常驻 UI），登录成功那一瞬是唯一没有玩家操作、可以安全重建的时刻。

### 2.3 ui 包发布

新增编辑器工具 `UiPackagePublisher`（菜单 `Tools/UI 热更/构建并发布 ui 包`），参数照抄 `ActivityPackagePublisher`（SBP 管线、`HashName` 文件名风格、LZ4 压缩、`VerifyBuildingResult`）。

发布布局与活动包**分目录**，避免 HashName 文件互相覆盖：

```
competeserves/HotContent/
  ├─ activity.version + *.bundle            ← 活动包（现状，拷平在根）
  └─ ui/
       ├─ ui.version + *.bundle             ← ui 包（本方案新增）
```

服务端无需改动：`HotContentServer` 用相对路径解析（`Path.GetFullPath(Path.Combine(_root, relative))` + 穿越防护），已验证子目录可正常服务。

### 2.5 能热更的范围（必须在文档里写清）

- **只能热更 json 表本身**：改 `uiType` 指向哪个预制体、哪个层次、是否窗口、绑哪个 Lua 模块。`UIConfig.cs` 的解析逻辑与 `UIManager` 的加载逻辑都是 C#，编译在包里，改不了。
- **表里指向的预制体必须也在 `ui` 包里**（`YooAssetService.LocationRoutes` 只覆盖 `Assets/Game/UI`、`Assets/Content/LuaScripts`、`Assets/Game/Resources/ui`）。指向包外资源会加载失败。

## 3 文件影响面

新增：

- `Assets/Game/Scripts/UI/Framework/Common/UIConfigHotUpdate.cs`（运行模式开关 + 登录后触发检查）
- `Assets/Game/Scripts/UI/Framework/Editor/UiPackagePublisher.cs`（构建并发布 ui 包）

不改：`UIManager` / `YooAssetService` / `UIConfig`（链路已存在）；`LoginSceneController` 不动（由新增类自己订阅登录回执）；服务端不改代码（仅多一个 `HotContent/ui/` 目录）。

## 4 验收

| 编号 | 场景 | 断言 |
| --- | --- | --- |
| A1 | 开关关（编辑器模拟）改 `UIConfig.json` | 打开对应界面立即使用新数据，行为与改造前一致 |
| A2 | 开关开 + 发布新版本后启动客户端 | 日志出现 `[YooAssetService] 包已更新: ui → <版本>`，随后 `ReloadUIConfig` 生效 |
| A3 | A2 之后连续打开 6 个界面 | 无异常，界面渲染正常 |
| A4 | 发布一个内容损坏的 json 版本 | 日志出现「沿用当前注册表」，UI 不空、仍可打开 |

## 5 风险

| 风险 | 说明与对策 |
| --- | --- |
| 开启 HostPlay 后**整个 ui 包**（所有 UI 预制体、图标、Lua 脚本）都从 CDN 取 | CDN 未发布或端口不通会直接黑屏。因此开关默认关，只在显式切换后生效；关闭即回到包内资源 |
| 首次拉全量 UI 资源体积不小 | 本机 localhost 也要数秒；本期不做进度 UI，只记日志 |
| 重载会拆掉常驻 UI | 只放在登录成功那一刻触发；不提供运行时手动刷新 |
| 发布必须基于已导入内容 | 改完 json 必须 `unity_editor.refresh` 再构建发布，否则会打上一版（与活动包同款老坑） |

---

## 6 实测结果（2026-11-03）

实现已落地并通过实机验证，客户端 commit 86551e09，服务端 commit d631b4c。

| 编号 | 结果 |
| --- | --- |
| A1 | 开关关（编辑器模拟）：改 UIConfig.json 立即生效，行为与改造前一致 |
| A2 | 开关开 + 发布 1.0.1：启动日志依次出现 `[YooAssetService] 初始化成功，模式=HostPlay` → `[YooAssetService] 包已更新：ui → 1.0.1` → `[UIConfig热更] ui 包已更新，UIConfig 注册表已重载` → `[UIManager] UI 注册表已重建，共 26 项`（与本地一致） |
| A3 | 重载后背包 / 成就 / 影像簿 / 角色页均能打开，数据正常（背包 500/24） |
| A4 | 未构造损坏版本，未验证；解析失败保留旧表的分支由 `UIManager.ReloadUIConfig` 的既有实现保证 |

### 6.1 过程中定位到的三个非功能问题

1. **ui 包一度完全构建不出来**：YooAsset 算依赖时抛 `Found invalid asset: Assets/Game/Scripts/buff/buffso/BuffDot.asset`。根因是该资产的类 `BuffDotEffect` 定义在 `BuffEffects.cs`（一个文件多个类），Unity 无法为它生成 MonoScript，`m_Script` 恒为 `{fileID: 0}`，`LoadAssetAtPath` 返回 null。已删除该孤儿资产（commit a78f8ab6），依赖链是 CharacterView.prefab → 预览预制体 → playso/anbi.asset → Resources/BuffDatabase.asset → BuffBurning.asset → BuffDot.asset。
2. **发布目录里已有同版本号的旧包**：`HotContent/ui/` 存着 2026-10-21 的发布且版本号同为 1.0.0，按"文件是否存在"轮询会立刻误报成功，客户端实际下到的是三个月前的旧表（17 项）。发布器现在会先清理旧补丁与清单，且构建必须用同步调用（`EditorApplication.delayCall` 调度的构建会被随后的域重载吃掉，产物根本不生成）。
3. **服务端单文件上限**：ui 包里 CharacterView 的依赖被打成 39.69MB 的一个 bundle，超过旧的 20MB 上限被 413 拒绝。上限已抬到 128MB（服务端 commit d631b4c），下发走流式拷贝不影响内存。抬高上限后该 bundle 正常下载（客户端角色页在 HostPlay 下可正常打开）。

### 6.2 已知噪声

热更重载期间，其它系统对 `UIManager` 的并发查询会打出若干条 `[UIManager] 尚未初始化完成，无法查询！`：这是 `ReloadUIConfig` 期间 `IsReady=false` 的预期表现（设计上并发的 `Open` 会等待本次重建结束），不是缺陷。