# Compete 战斗系统 / 选人大厅 / UI 框架 移植方案

> 源工程：`D:\project\compete`（Unity 6000.4.0f1 + URP 17 + NGO 2.13 + UOS KCP + Animancer 7.4.2）
> 目标工程：`D:\tuanjieproject\frined\RedDotSystem-main`（Tuanjie 2022.3.62t11 / 1.9.3 + URP 14.2.0-t1 + NGO 1.13.1 + Unity Transport 1.5 + Animancer 7.4.2）
> 编写日期：2026-10-19
> 文档定位：这是移植的**唯一施工蓝图**。后续每个阶段、每个提交都以本文档的编号（P0/P1/...）和验收项为准；实现过程中发现的新问题必须回写到本文档的“风险登记册”和“变更记录”。

---

## 0. 结论先行

### 0.1 用户已确认的决策（不再反复询问）

| 编号 | 决策项 | 结论 |
|---|---|---|
| D1 | 移植范围 | **核心 + 表现层**：战斗核心、选人大厅、UI 框架、血条/飘字/音效/顿帧/无敌帧/相机、源项目角色与动作资源 |
| D2 | 联机形态 | **房主即服务端（Listen Server）**：一人 StartHost，一人填 IP StartClient；NGO + Unity Transport；不接 competeserves |
| D3 | 输入方案 | **安装 Input System**，`Active Input Handling = Both`，移植 `.inputactions` 并**在目标工程重新生成** C# 包装类 |
| D4 | 角色资源 | **直接移植源项目角色**：安比 + 艾莲（含骨骼、动作、Animancer 资产、音效） |
| D5 | UI 框架 | **全量移植 SkierFramework，并重构现有全部 UI**（不是共存） |
| D6 | 战斗场景 | **复制 `multiplecompete.unity` 修补**；若 Tuanjie 导入后组件丢失/不可用，**允许自动回退重建** `Battle.scene` |
| D7 | 单机调试 | **保留**源项目 `!IsSpawned` 单机路径（不 StartHost 也能打木桩/怪兽） |
| D8 | 入口 | **zhuye 顶部入口行新增“对战”按钮** → SkierFramework 选人/房间 → 准备后切 `Battle` 场景 |
| D9 | 旧面板迁移机制 | **全部抽成预制体**（`Assets/Resources/GameUI/...` + `UIControlData` + `UIView`），场景只保留入口/HUD |
| D10 | 活动/阿福 | **一并纳入框架**：活动面板改 `LuaOnlyView`，阿福面板改 `UIView`；悬浮球本身保留 DDOL |

### 0.2 三条必须提前接受的现实

1. **这不是一次会话能完成的工作**。源项目 466 个脚本、约 1.2 GB 角色/动作资源，目标项目现有 7 个业务面板（好友/聊天 1180 行、背包 577 行、任务 534 行、邮件 375 行、排行榜 311 行、副本 HUD 271 行、阿福 967 行）全部要重构为预制体。按本文档的 P0–P9 推进，预计 **40–60 个提交**。
2. **先保编译与冒烟，再保玩法**。任何阶段结束时工程必须能编译、能进 Play、无新增 Console Error；不允许“先堆代码最后一起修”。
3. **每一步都可回滚**。每个阶段/面板一个独立 git 提交，提交前只暂存本次涉及的文件；不把用户正在编辑的资源、ProjectSettings 自动改写、.unitypackage 导入内容混进提交。

### 0.3 源项目文档的一个缺口

`D:\project\compete\docs\战斗闭环与联机大厅_教学文档.md` 的第 7 章（网络同步）正文缺失（目录有、正文从第 6 章直接跳到第 8 章）。本方案的网络部分**以源码为准**，不依赖该文档。

---

## 1. 源项目功能盘点

### 1.1 工程与依赖

| 类别 | 源项目 | 目标项目 | 处理 |
|---|---|---|---|
| 引擎 | Unity 6000.4.0f1 | Tuanjie 2022.3.62t11 | 语法/API 降级 |
| 渲染 | URP 17.4.0 | URP 14.2.0-t1 | 材质/相机栈适配 |
| 网络 | NGO 2.13.1 + UOS KCP 2K | NGO 1.13.1 + Unity Transport 1.5 | 换 Transport，NGO API 降级 |
| 动画 | Animancer 7.4.2 | Animancer 7.4.2（已存在） | 直接复用 |
| 输入 | Input System 1.19.0 | 无（activeInputHandler=0） | 安装并重新生成 |
| 异步 | UniTask 2.x | UniTask 2.5.11（嵌入包） | 直接复用 |
| 补间 | DOTween | DOTween（Assets/Plugins/Demigiant） | 直接复用 |
| UI | UGUI + TMP | UGUI 1.0.0 + TMP 3.0.9 | 直接复用 |
| 资源 | YooAsset 3.0.5 | YooAsset 3.0.5（嵌入包，仅活动热更使用） | UI 框架改 Resources 适配 |
| 其他 | Odin、Behavior Designer、RootMotion、GAS、Luban | Odin、GAS、XLua、Mm 框架 | 只取战斗必需 |

### 1.2 战斗闭环（一次普攻的 14 跳）

1. `CharacterInputSystem` 全局单例采样输入（Input System）。
2. `Player.Update`：`if (IsSpawned && !IsOwner) return;` —— 只有拥有者端模拟。
3. `PlayerNullState`：`canInput && ConsumeATKInput()` → 起手；移动状态机切 `playerMovementNullState` 锁移动。
4. `PlayerComboStateMachine.OnStateSwitched` → `Player.SyncComboState` 写 `NetworkVariable` + `ServerTime` 时间戳。
5. `PlayerComboState.PlayAttackClip`：Animancer 播放；`state.Events.Clear()`；注册 `linkCancelTime` 检查点与 `hitFrameTime` 打击帧。
6. 打击帧 → `CharacterCombo.ATK()` → `OverlapSphereNonAlloc` → 排除自身 → `HashSet` 去重。
7. `DamageCalculator.CalculateOutgoing`：攻击 × 倍率 × 增伤 × 暴击（出手快照）。
8. `HealthModel.RequestDamage` → `ServerRpc`。
9. 服务端 `CalculateIncoming`：防御公式 → 减伤 clamp 0.9 → 保底 1 → 改血量 → `NetworkVariable` 广播 → 死亡锁。
10. `NotifyDamageClientRpc` 全员 → `EventCenter.Dispatch(E_OnDamage)`。
11. 被击者 `Player.TakeHit()`：先连击→Null，再移动→Hurt（顺序敏感）。
12. 攻方 `HitStop` 压 `timeScale`；飘字/血条/音效/受击动画并行。
13. 远程镜像端按 `netMoveState/netComboState/netComboIndex` 回放；`ApplyRemotePhaseOffset` 按已播时长跳帧。
14. 死亡：连击→Null、移动→Dead；`_isDead` 双端防重；Buff 清空。

### 1.3 双状态机与输入缓冲

- 移动 8 态：Idle / Walking / WalkStop / Dashing / Dodge / Hurt / Dead / MovementNull。
- 连击 3 态：Null / ATKIng / Skill。
- 状态对象化：状态在状态机构造时 `new` 好，运行时零分配。
- 状态类型显式声明（`MovementStateType`/`ComboStateType`），作为网络协议字段，不能用类名。
- 基类模板方法：读输入 → 写黑板 → 平滑转向；子类只做状态切换。
- 输入缓冲：`BufferATKInput()` 记录时间戳；`ConsumeATKInput()` 一次性消费；`ClearATKInputBuffer()` 清空；窗口默认 1s。
- 检查点：`linkCancelTime`（默认 0.6）消费缓冲；`hitFrameTime`（默认 0.35）触发伤害；二者都是归一化时间。
- 收尾三路分流：按移动打断、按 Shift 接前冲、等收尾动画结束。
- 软锁定：仅单机生效（`if (player.IsSpawned) return;`），起手 0.1s 平滑转向，目标粘性 + 加权评分。

### 1.4 伤害 / 属性 / Buff

- 攻方段：`Attack × damageMultiplier` → `×(1+DamageUp)` → 暴击骰。
- 受方段：`× (1 - DEF/(DEF+100))` → `× (1-clamp(DamageDown,0,0.9))` → 0 或 `max(1, damage)`。
- 属性账本：白值型 `(Base+ΣAdd)×ΠMul`；系数型 `(1+ΣAdd)×ΠMul-1`；脏标记缓存；按 `source` 引用删除。
- Buff 三层：`BuffData`（SO 配置）/ `BuffEffect`（SO 策略，禁止运行时字段）/ `BuffInstance`（运行时）。
- 调度器：倒序遍历、循环内置脏、循环外统一 `SyncNetBuffs`、死亡清空、tick 累计器。
- 行为类 Buff 用引用计数（Stun/Silence/Invincible），网络同步用位标志。
- DoT 无视无敌；规则写在结算处（`HealthModel`/`Player`），不在 Buff 内。

### 1.5 表现层

- 血条：`HealthModel.CurrentHP/MaxHP` 是 `BindableProperty<float>`，View 订阅；打开面板时先读当前值。
- 飘字：`DamageTextManager` 订阅 `E_OnDamage` → 世界坐标转屏幕 → 栈池取用 → Tween → 回收；Overlay Canvas、无 Scaler、`raycastTarget=false`。
- 音效：`ActorAudioComponent` 3D 空间化 + AudioMixer 总线；受击/挥砍/语音/脚步。
- 顿帧：`Time.timeScale=0.03`，UniTask `ignoreTimeScale` 计时；新顿帧接管旧顿帧且不恢复。
- 无敌/闪避：右键开窗口（服务端限流），窗口内被打中才触发闪避；`GrantDodgeInvincible` 续全程。
- 相机：场景预置两套 Camera + vcam，按 `NetSeatId` 认领；带 0.5s 重试；停用其它相机。

### 1.6 联机与大厅

- `RoomState`（与 NetworkManager 同物体的 DDOL 组件）6 条 NamedMessage：
  - C→S：`SelectChar`（int）、`SetReady`（int）、`CancelReady`（空）、`QueryPeerChar`（空）
  - S→C：`PeerSelectChar`（int，-1=清空）、`PeerReady`（byte）
- 服务端字典 `_players[clientId] = {IsReady, CharId}`、`_seats[clientId]`；先来先坐。
- 换人 → 准备作废；人数=2 且全 Ready → `NetworkManager.SceneManager.LoadScene("multiplecompete")`；`_sceneLoading` 防重入。
- `BattleSpawnManager`（场景内 NetworkObject）`OnNetworkSpawn` 服务端生成英雄：
  - `Instantiate` → `SpawnWithOwnership(clientId)` → **Spawn 之后**写 `NetSeatId`/`NetCharId`；
  - 不使用 `destroyWithScene:true`（场景事件收尾会提前销毁）。
- 客户端相机按 `NetSeatId` 选座位 0/1 的相机；`-1` 表示未分配，0.5s 重试。

### 1.7 UI 框架（SkierFramework）

- `UIManager`（SingletonMono）：7 层 Canvas（Scene/Background/Normal/Info/Top/Tip/BlackMask）、`UIConfig.json` 注册表、Open/Close/JumpUI/Preload/CloseAll/ReleaseAll/FadeIn/Out。
- `UIViewController`：加载预制体 → `GetOrAddComponent(uiViewType)` → `SetParent` 到层 → 黑边适配 → `UIView.OnInit` → 打开/关闭动画。
- `UIView`：`OnInit/OnAddListener/OnOpen/OnResume/OnPause/OnClose/OnCancel/OnRelease`；自动加 Canvas + GraphicRaycaster；递归排序。
- `UIControlData`：[ControlBinding] 反射绑定；编辑器“复制代码到剪贴板”。
- `UIScrollView` + `UILoopItem`：虚拟列表。
- `UIModelManager` + `UIRenderToTexture`：3D 模型渲染到 RawImage（选人预览）。
- `UIViewAnim`：步骤式开关动画（DOTween）。
- `UIAnim`：Lua 友好的补间静态桥。
- `ResourceManager` + `YooAssetService`：资源加载/对象池/引用计数/场景加载。
- `UILuaView`/`LuaOnlyView`：Lua 热更 UI 壳（`#if XLUA`）。

### 1.8 资源清单（按引用闭包复制，不整目录复制）

| 资源 | 源路径 | 体积/说明 |
|---|---|---|
| 安比模型 | `Assets/Resource/人物/安比.prefab`、`安比/安比.fbx`、`Tex/` | 112 MB 目录内 |
| 艾莲模型 | `Assets/Resource/人物/le_Size02_Ellen_Ani_Idle (1).prefab`、`鲨鱼/` | 同上 |
| 安比动作 | `Assets/Resource/动作/Animation/安比/安比攻击.fbx`（49.2 MB）、`安比移动.fbx`（28.7 MB） | 只这两个 FBX |
| 艾莲动作 | `Assets/Resource/动作/Animation/027_鲨鱼妹_艾莲乔/FBX_redo/*.fbx` | 按 `PlayerSOshatyu.asset` + 攻击列表引用闭包选取 |
| 音效 | `Assets/Resource/audoi/**` | 37.8 MB；按 `SoundData.asset` 与 ComboData 引用选取 |
| 战斗 VFX | `Assets/Third Parts/VFX_Klaus/Prefabs/**` | 10.8 MB；可选取命中/挥砍三类 |
| UI 预制体 | `Assets/HOTS/UI/Prefabs/*.prefab` + `UIConfig.json` | 0.4 MB |
| UI 贴图 | `Assets/Resource/ui/**` | 13.9 MB |
| 配置资产 | `Assets/script/playso/**`、`buff/buffso/**`、`Resources/BuffDatabase.asset`、`SoundData.asset` | 全部 |
| 动作控制器 | 安比/鲨鱼 idle/attack controller | 仅调试用，可后续再迁 |

> **禁止**直接 `robocopy` 整个 `Assets/Resource/动作`（1.08 GB）。必须按 `.asset` 里的 GUID 引用闭包提取，否则 git 仓库会被无关动画撑爆。

### 1.9 源项目已知缺口（移植时不要假装已实现）

- 相机震动：`ComboData.shakeForce` 有字段，`CameraHitFeel` 不存在（`CharacterCombo.cs` TODO）。
- 攻击刀光 VFX：调用被注释，`WeaponBone` 查找被注释。
- 行为类 Buff（眩晕/沉默）计数已就绪，但战斗侧尚未读取。
- 数值权威：服务端不校验客户端上报伤害（`DamageRequest` 注释明确是取舍）。
- 分离轴：相位对齐只做一次性跳帧，不持续软追帧。
- 大厅阶段无消息频率限制。

---

## 2. 目标项目现状与冲突

### 2.1 版本与包

- Tuanjie 2022.3.62t11，C# 9；URP 14.2.0-t1；NGO 1.13.1 + Unity Transport 1.5.0。
- 已存在 `com.unity.cinemachine` 2.10.7、`com.cysharp.unitask` 2.5.11、`com.tuyoogame.yooasset` 3.0.5、XLua 2.1.16、Odin、DOTween、Animancer 7.4.2。
- **没有** Input System；`ProjectSettings/ProjectSettings.asset` 的 `activeInputHandler: 0`。
- **没有** `player` / `enemy` 物理层（TagManager 中 6–31 为空）。
- 现有 UI 相机：`UICameraService`（ScreenSpaceCamera + URP Overlay Stack + 每秒 Sweep）。
- 现有暂停：`PauseManager`（栈式 `timeScale=0`，`savedTimeScale` 记录原倍速）。
- 现有 UI：`zhuye.scene` 内 9 个画布（FriendCanvas / beibao / renwu / youjian / fuben / huodong / agent / shijie / UICamera），大部分 `ConstantPixelSize` 或 `ScaleWithScreenSize` 混用。
- 现有业务：账号/好友/聊天/背包/任务/邮件/排行榜/副本/活动/阿福，全部走自家 `ClientNetworkManager`（TCP + protobuf，端口 8888）。

### 2.2 冲突点清单（必须处理，不能忽略）

| 编号 | 冲突 | 后果 | 处理 |
|---|---|---|---|
| C1 | UI 相机双主人 | `UIManager` 会 `GameObject.Find("UICamera")` 并 `SetParent` 到 DDOL Root，抢走 `UICameraService` 的相机 | 统一由 `UIManager` 拥有；`UICameraService` 改为 Facade |
| C2 | 画布父级/层级 | 现有画布在场景根，新框架要求挂在 7 层 Canvas 下 | 抽预制体后统一由 `UIManager` 加载 |
| C3 | CanvasScaler | 现有 FriendCanvas=ConstantPixelSize、beibao=ScaleWithScreenSize | 统一走框架层 Canvas 的 1920×1080 Shrink；面板内不再挂 Scaler |
| C4 | 嵌套 Canvas | FriendPanel/ListPage 等有嵌套 Canvas（必须各自 GraphicRaycaster） | 抽预制体时保留；`UIView.SortOrder` 会重排 sortingOrder |
| C5 | 输入双系统 | 目标现有代码用 `Input.GetAxisRaw`；源战斗用 Input System | `activeInputHandler=Both`；现有代码不动，战斗用新系统 |
| C6 | 物理层缺失 | `LayerMask.GetMask("enemy")` 返回 0，命中全层/软锁定失效 | **按源项目层号对齐**：3=`player`、6=`environment`、7=`enemy`（源 prefab 已序列化 layer=3/7，改层号会导致命中/碰撞失效）；配置碰撞矩阵 |
| C7 | timeScale 双主人 | `PauseManager` 与 `HitStop` 互相覆盖 | 给 `PauseManager` 增加 HitStop 层/临时倍速 API |
| C8 | NGO 版本 | 2.13 → 1.13 少量 API 差异 | 按第 6.2 节逐条改 |
| C9 | Transport | UOS KCP 组件不存在 | 换 `UnityTransport`，重建联机预制体 |
| C10 | 资源路径 | 源 `Assets/HOTS/UI/UIConfig` + YooAsset location | 改 `Resources/GameUI/UIConfig.json` + Resources 适配 |
| C11 | Unity 6 API | `EntityId/GetEntityId`、`FindObjectsByType` 重载 | 按第 6.1 节改 |
| C12 | 场景 YAML | Unity 6 的 serializedVersion 10/13 | 先试导入；失败回退重建 |
| C13 | 字体 | 源预制体引用 LiberationSans SDF，中文靠回退 | 重映射为目标 NotoSansSC SDF |
| C14 | 命名冲突 | 源全局 `Player`/`HealthModel`/`EventCenter` 等 | 目标已核查无同名类；Animancer 的 `CharacterState` 在 `Animancer.FSM` 命名空间，不冲突 |
| C15 | 现有 E2E 依赖 | 现有面板在 `zhuye.scene` 的序列化引用 | 抽预制体后必须重新接线；每个面板单独回归 |

---

## 3. 目标架构设计

### 3.1 目录规划（ASCII 优先，避免中文路径带来的工具/编码问题）

```
Assets/
├── Scripts/
│   ├── Battle/
│   │   ├── Core/                 # 状态机、Player、数据、伤害、Buff、音效、VFX、工具
│   │   ├── Network/              # RoomState、BattleSpawnManager、BattleBootstrap
│   │   ├── Lobby/                # Xuanze、room、jiaru、Dating（改为 BattleLobbyView）
│   │   └── Presentation/         # HealthModel、DamageText、bloodpanel、BattleHud
│   ├── UI/
│   │   ├── Framework/            # SkierFramework 全量（UIManager/UIView/...）
│   │   └── Framework/Editor/     # UI 创建窗口、UIControlData 编辑器、迁移工具
│   └── ...（现有业务不动）
├── Resources/
│   └── GameUI/                   # 避让已有 Resources/ui（物品图标，Windows 大小写不敏感）
│       ├── UIConfig.json
│       ├── Battle/               # 选人/房间/战斗 HUD 预制体
│       ├── Friend/  Bag/  Quest/ Mail/  Leaderboard/  Dungeon/  Activity/  Agent/
├── Art/
│   └── Battle/
│       ├── Characters/Anbi/  Ellen/
│       ├── Animations/Anbi/  Ellen/
│       ├── VFX/  Audio/
├── Prefabs/
│   └── Battle/                   # Networking.prefab、英雄 prefab、BattleSpawnManager 等
└── Scenes/
    └── Battle.scene              # 由 multiplecompete 修补或重建
```

### 3.2 命名空间与程序集

- **第一阶段不引入 asmdef**：全部脚本继续编进 `Assembly-CSharp`，与现有业务同程序集，避免 Animancer/UniTask/Odin/Netcode/InputSystem 的引用地狱。
- 战斗脚本保持源项目的全局命名空间（已核查目标无同名类），减少逐文件改动。
- UI 框架保持 `SkierFramework` 命名空间。
- 迁移后的旧面板 View 类统一放 `SkierFramework`（与 `UIType` 反射一致）。
- 后续若要 asmdef，单独开阶段做，不在本次混入。

### 3.3 UI 相机与层（D10 + C1 的落地方案）

**唯一真相：`UIManager` 拥有 UI 相机。**

1. `UIManager.Initialize()`：
   - 优先认领场景中名为 `UICamera` 的相机（`zhuye`/`LOGIN` 已有），配置为正交、`cullingMask = UI`、`renderType = Overlay`、`planeDistance=10`、位置 `(0,0,-10)`。
   - 场景没有则创建 `UIRoot/UICamera`（DDOL）。
   - 创建 7 层 Canvas（`ScreenSpaceCamera`，`worldCamera = UI相机`，`sortingOrder = (int)layer`）。
   - 对**所有 Base 相机**注册 Overlay Stack（场景加载后、战斗场景切相机后必须重跑）。
2. `UICameraService` 保留类名与公开 API，但内部改为：
   - `UICamera => UIManager.Instance.UICamera`
   - `EnsureUICamera()` → 调 `UIManager.EnsureUICamera()`
   - `ConvertCanvas(canvas, camera)` → `UIManager.AdoptCanvas(canvas)`
   - `SweepAll()` → `UIManager.EnsureCameraStacks()`（只做相机栈注册与 UI 层清理，不再抢所有权）
   - 这样 `DungeonHud`、`UiFx`、`AgentFloatBall`、`ActivityShell` 等程序化画布的调用点**零改动**。
3. 战斗场景有两台 Base 相机（座位 0/1），`UIManager` 必须把 UI 相机注册进**当前启用的那台**；`Player.TryBindSceneCamera` 里的 `EnsureUICameraInStacks` 改为调用 `UIManager.EnsureCameraStacks()`。

### 3.4 网络拓扑（D2）

```
zhuye 入口「对战」
  → UIManager.Open(BattleLobby)
  → 创建房间：Instantiate(Networking.prefab) → StartHost()
  → 加入房间：输入 IP → UnityTransport.SetConnectionData(ip, 7777) → StartClient()
  → RoomState 汇总选择/准备
  → 双方 Ready → NetworkManager.SceneManager.LoadScene("Battle")
  → BattleSpawnManager.OnNetworkSpawn（仅服务端）生成两个英雄
  → 客户端按 NetSeatId 绑定座位 0/1 相机
```

- 端口：Unity Transport 默认 **7777**（不用 8888，避免与 competeserves 冲突）。
- 场景必须加入 Build Settings（两端构建一致）。
- `Networking.prefab` 结构：`NetworkManager` + `UnityTransport` + `RoomState`（同物体）。
- `NetworkManager.NetworkConfig.Prefabs` 在 `RoomState.Awake` 或 `BattleBootstrap` 运行时注册英雄 prefab（不依赖 `DefaultNetworkPrefabs.asset`，避免复制 Unity 6 资产）。
- 单机调试：不创建 `Networking.prefab`，直接打开 `Battle.scene`，`Player.IsSpawned=false`，相机固定座位 0，木桩/怪兽作为本地目标。

### 3.5 战斗场景装配（D6）

`Battle.scene` 至少包含：

| 对象 | 组件 | 说明 |
|---|---|---|
| `BattleRoot` | `BattleSpawnManager` + `NetworkObject`（场景内放置） | 仅服务端生成英雄 |
| `Spawn_0` / `Spawn_1` | `Transform` | 两个出生点 |
| `Plane` / 地形 | `MeshCollider`/`BoxCollider` | 战斗场地 |
| `Directional Light` | `Light` | 主光 |
| `Camera` / `thirdcamera` | `Camera` + `CinemachineBrain` / `CinemachineVirtualCamera` | 座位 0 |
| `Camera (1)` / `thirdcamera (1)` | 同上 | 座位 1 |
| `DamageTextManager` | `DamageTextManager` | 飘字（DDOL 或场景） |
| `SoundManager` | `AudioManager` | 2D/3D 音效 |
| `EventSystem` | `EventSystem + StandaloneInputModule` | 唯一事件系统 |
| `UICamera` | 由 `UIManager` 认领/创建 | Overlay UI |

> 复制 `multiplecompete.unity` 后先做**组件体检**：`BattleSpawnManager.HeroPrefabList`、`SpawnPoints`、两台 Camera/vcam 的名字、`NetworkObject` 的 `InScenePlaced`、URP 相机数据。若 Tuanjie 报“脚本丢失/场景损坏/序列化版本不支持”，直接走重建路径，不纠缠 YAML。

### 3.6 与现有业务的边界

- 战斗/大厅**不改** `ClientNetworkManager`（TCP 8888）与现有账号体系；NGO 只负责战斗内同步。
- zhuye 的对战入口不依赖登录？**建议要求已登录**（与现有活动入口一致），但战斗本身不向 competeserves 上报。
- 战斗结束返回 zhuye：`NetworkManager.Shutdown()` → `SceneManager.LoadScene("zhuye")` → 关闭战斗 UI。
- 现有背包/任务/邮件不在战斗内开放（避免暂停/timeScale 与网络状态冲突）；若后续要开放，走 `PauseManager` 与 `UIManager` 的层管理。

---

## 4. 分阶段施工计划（每个阶段一个或多个提交）

### P0 工程准备（不动业务代码）

1. 安装 `com.unity.inputsystem`（优先 1.7.0；若 Tuanjie 注册表不支持，试 1.11.2；再不行走 6.3 的旧输入适配回退）。
2. `ProjectSettings/ProjectSettings.asset`：`activeInputHandler: 2`（Both）。
3. `TagManager.asset`：**按源项目层号对齐**新增 3=`player`、6=`environment`、7=`enemy`（目标当前 3/6/7 为空，4=Water、5=UI 已占用，正好可对齐）；配置碰撞矩阵（player/enemy 与 Default 碰撞，player 与 enemy 碰撞）。
4. 建立目录骨架（3.1）。
5. `.gitignore` 检查：不要忽略 `Assets/Scripts/UI/Framework`、`Assets/Resources/GameUI`；确认大文件不被误忽略。
6. 提交信息：`chore(battle): 准备战斗移植工程配置（Input System/物理层/目录）`

**验收**：编辑器刷新无编译错误；`activeInputHandler=2`；层列表出现 player/enemy；空目录有 `.meta` 或下次导入自动生成。

### P1 UI 框架内核移植（先能编译、能开一个空面板）

1. 复制 `Framework/UI` 全量 + `Framework/Other` + `Framework/Resources` 到 `Assets/Scripts/UI/Framework`（保留 `.meta` 以保 GUID）。
2. 删除/替换 `YooAssetService` 依赖（见 6.5）；`ResourceManager` 改 Resources 适配。
3. 修 Unity 6 API（见 6.1）。
4. 复制 `UIConfig.json`，路径改 `Resources/GameUI/UIConfig.json`；`UIType` 先只留 `BattleLobby/room/jiaru/bloodpanel`。
5. `UIManager` 相机/层改为 3.3 方案；`UICameraService` 改 Facade。
6. 做一个 `SmokeView` 空面板，验证 `UIManager.Open/Close`、层排序、黑幕、UI 相机 Stack。
7. 提交：`feat(ui): 移植 SkierFramework 内核并接入 UICameraService（P1）`

**验收**：
- 7 层 Canvas 出现在 `UIRoot` 下，`UICamera` 为 Overlay 且在主相机 Stack 中。
- `UIManager.Open("SmokeView")` 能显示/关闭；关闭后 `activeSelf=false`。
- 现有 zhuye 面板仍能正常显示（未被相机/Canvas 改动破坏）。
- Console 0 Error。

### P2 旧 UI 抽预制体流水线（机制先跑通，只迁 1 个面板做样板）

1. 写 Editor 工具 `UIPrefabMigrationTool`：
   - 选中场景 Canvas 根 → `PrefabUtility.SaveAsPrefabAsset` 到 `Assets/Resources/GameUI/<Group>/<Name>.prefab`。
   - 自动加 `UIControlData`，扫描控制器里 `public/private [SerializeField]` 字段，按“字段名 = 子物体名”生成 `ctrlItemDatas`（类型取字段类型）。
   - 给字段补 `[ControlBinding]`（或生成新 View 类时直接带）。
   - 生成 `UIType` 枚举项与 `UIConfig.json` 条目（幂等）。
2. 先迁 **排行榜面板**（最小、无跨面板依赖）作为样板：
   - `LeaderboardView : UIView` 替代 `ChallengeUIController` 的榜单部分。
   - 入口按钮改为 `UIManager.Open(UIType.LeaderboardView)`。
   - 保留 `VirtualScrollList`（先不换 `UIScrollView`，减少变量；后续再统一）。
3. 提交：`refactor(ui): 建立场景面板抽预制体流水线并迁移排行榜（P2）`

**验收**：zhuye 打开排行榜、数据渲染、关闭、重开、红点/刷新全部与迁移前一致；场景内不再有 `LeaderboardPanel` 实例。

### P3 选人大厅（新框架第一批业务 UI）

1. 重写 `Xuanze`、`room`、`jiaru` 为 `BattleLobbyView`、`BattleRoomView`、`JoinRoomView`（逻辑照搬，UI 用目标工程深色 HUD 重建）。
2. 不迁移 `Dating`：zhuye 本身就是主界面，入口直接开 `BattleLobbyView`，由它提供“创建房间 / 加入房间”。
3. 不直接复制源预制体：源 Xuanze/room 按 2560 宽手工摆放（根 1852×1309、偏移 -401,-233），且背景是《Dimension Lovers》商业宣传立绘，直接搬会错位并有版权风险。改为在 `Assets/Resources/GameUI/Battle/` 程序化重建三个面板（`BattleLobbyPrefabBuilder`，可重复执行）。
4. 联机对象改为 `BattleNetworking.prefab`（NetworkManager + UnityTransport + RoomState，端口 7777），由 `BattleNetworkLauncher` 统一创建/启动/销毁。
5. `room` 的 3D 预览继续用 `UIModelManager`（已修 UICamera 惰性解析、RT 尺寸兜底、预览灯不再抢主光）；P3 阶段预览模型用项目现有角色占位，`BattleHeroCatalog` 是 P7 切换安比/艾莲的唯一改动点。
6. 入口：zhuye 顶部入口行新增 `BattleEntryButton`（x=-1179.6，步长 112.8，独立 `battle` 画布 sort=10），点击 `UIManager.Open(BattleLobbyView)`。
7. 提交：`feat(battle): 移植选人/房间/加入大厅 UI（P3）`

**验收（单机）**：zhuye 点“对战”能开面板；选安比/艾莲能出 3D 预览；准备按钮文案切换；关闭面板回 zhuye；无报错。
**验收（双进程）**：A StartHost、B 填 127.0.0.1 加入；双方互相看到选择/准备；双方 Ready 后切场景（P4 前可先切空场景验证）。双进程实测顺延到 P5（需要可同时运行的第二个客户端）。

### P4 战斗核心（单机优先）

按以下顺序，每一步编译+冒烟：

1. 基础：`IState`、`StateMachine`、`MovementStateType`、`ComboStateType`、`PlayerStateReusableData`、`PlayerComboReusableData`、`BindableProperty`。
2. 数据：`PlayerSO`、`PlayerStateData`、`PlayerMovementData`、`PlayerComboData`、`PlayerComboSOData`、`ComboData`、`ComboContainerData`、`CharacterStatsData`。
3. 移动：`CharacterMoveControllerBase`、`PlayerMovementStateMachine`、8 个移动状态。
4. 连击：`PlayerComboStateMachine`、`PlayerComboState`、`PlayerNullState`、`PlayerATKIngState`、`PlayerSkillState`（先空实现）、`CharacterCombo`。
5. Player 门面：`Player`、`CharacterInputSystem`、`playerinputaction`（重新生成）、`PlayerCameraUtility`、`CharacterNameList`。
6. 伤害/Buff：`DamageCalculator/Request/Result`、`AttrType/ModType`、`MutableAttribute/AttributeModifier`、`AttributeComponent`、`BuffData/Database/Effect/Effects/BehaviorEffects/Instance/BuffNetState`、`BuffComponent`、`CharacterState`。
7. 血量：`HealthModel`。
8. 表现：`DamageText/DamageTextManager`、`bloodpanel`、`ActorAudioComponent`、`AudioManager`、`AudioMixerController`、`SoundData/SoundStyle`。
9. 工具：`GameTimer/TimerManager`、`EventCenter`、`Singleton`、`PlayerStateData`。
10. 提交：每 1–2 个逻辑组一个提交（见第 12 节）。

**验收（单机）**：
- 安比/艾莲可移动、待机/行走/收招/冲刺/闪避/受击/死亡动画正确。
- 左键连招 3 段；收尾按键能接第一段；右键无敌窗口+被打中触发闪避。
- 打木桩掉血、飘字、血条、受击硬直、顿帧、死亡。
- Buff（灼烧 DoT/回血/增伤）按配置生效；DoT 无视无敌。
- 动画打击帧与判定同步（改 clip 长度不需要改代码）。

### P5 网络同步（NGO 1.13 适配）

1. 修 `Player` 的 `NetworkVariable`/`ServerRpc`/`ClientRpc`/`ServerTime`（见 6.2）。
2. `RoomState` + `BattleSpawnManager` 移植；`Networking.prefab` 重建（NetworkManager + UnityTransport + RoomState）。
3. `NetworkManager.NetworkConfig.Prefabs` 注册英雄 prefab。
4. 双进程验证：2P 各自看到对方移动/攻击/受击/死亡；血量一致；远程端相位对齐。
5. 提交：`feat(battle): 接入 NGO 1.13 联机同步与房间生成（P5）`

**验收（双进程）**：
- A 攻击 B，B 掉血、硬直、飘字、血条刷新；A 端看到 B 的受击动画。
- 双方位置/动画状态同步；远程端不重复扣血、不重复顿帧。
- 一方退出/断线，另一方不崩；重开可再战。
- 座位/相机绑定正确（A 用 Camera、B 用 Camera (1)）。

### P6 表现层与音频/特效收口

1. 血条面板预制体 + `bloodpanel` 迁入框架。
2. 飘字 Canvas 迁入框架（`TipLayer`）；`DamageTextManager` 用 `UIManager` 的层。
3. 音频：复制 AudioMixer + `SoundData`；受击/挥砍/语音/脚步接线；远程端策略（可先不播语音）。
4. 攻击 VFX：恢复 `CharacterCombo` 注释的调用，挂武器骨骼；使用目标现有 `VfxManager`（不迁 `VFX_PoolManager`）。
5. 顿帧与 `PauseManager` 合并（见 6.13）。
6. 相机：保留两套预置相机；把按名字查找改成序列化数组（`BattleCameraRig`），消除字符串硬编码。
7. 提交：`feat(battle): 接入血条/飘字/音效/特效/顿帧表现层（P6）`

**验收**：E2E 录屏中能看到完整反馈链；暂停面板打开时顿帧不会把世界“解冻”；关闭暂停后倍速正确。

### P7 角色与动作资源迁移

1. 按引用闭包复制：安比/艾莲 prefab、FBX、材质、贴图、Animancer 资产、`PlayerSO`/`ComboData`/`BuffData`/`SoundData`。
2. 用 Editor 脚本扫描 `.asset` 的 GUID 引用，生成复制清单；只复制清单内文件（含 `.meta`）。
3. 字体/材质重映射：
   - TMP 字体 → 目标 NotoSansSC SDF（或保留 TMP 全局回退）。
   - 材质 → URP/Lit 或目标 YSA Toon；检查粉色/丢失贴图。
4. 英雄 prefab 清理：
   - 移除 `PlayerInput` 组件（输入统一由 `CharacterInputSystem` 管理，避免双开）。
   - 确认 `AnimancerComponent`、`Player`、`HealthModel`、`BuffComponent`、`AttributeComponent`、`CharacterState`、`NetworkObject`、`NetworkTransform`。
   - `PlayerSO` 指向迁移后的资产。
5. 提交：`feat(battle): 迁移安比/艾莲角色与动作资源（P7）`

**验收**：两个英雄在 Battle 场景中可正常渲染/动画/攻击；无粉色材质、无丢贴图、无中文乱码。

### P8 现有面板全量迁移（D5/D9/D10）

按以下顺序，每个面板独立提交与回归：

1. **好友/聊天**（`FriendCanvas/FriendUIRoot/FriendPanel`）→ `FriendView` + `ChatView`；`FriendItemTemplate`/`MessageLeft/RightTemplate` 作为池模板保留在预制体内。
2. ~~**背包**（`beibao/BagPanel`）→ `BagView`~~ → **【2026-10-20 用户定稿：不迁移】** 保留原项目背包（`beibao` 画布 + `BagUIController` + `BagSlotView`，含 500 格虚拟列表/拖拽换位/使用丢弃整理）原样运行，**不使用 Compete 的背包**（`Assets/Scripts/Compete/bag/*` 只作为移植过来的内核脚本留在工程里，不接 UI、不注册 `UIType`/`UIConfig`）。因此 P8 不再做背包预制体、不新增 `BagView`，也不改 `zhuye` 的 `BagPanel`。
3. **任务**（`renwu/QuestPanel`）→ `QuestView`。
4. **邮件**（`youjian/MailPanel`）→ `MailView`。
5. **排行榜/挑战**（`FriendCanvas/FriendUIRoot/LeaderboardPanel`、`ChallengePanel`）→ 两个 View。
6. **副本 HUD**（`fuben` 画布 + `DungeonHud`/`DungeonMinimap`/`DungeonGpuMinimap`/`DungeonBigMap`）→ `DungeonHudView` + 程序化画布 Adopt。
7. **世界聊天**（`shijie/WorldPanel`）→ `WorldChatView`。
8. **活动**（`huodong` + Lua）→ `ActivityView : LuaOnlyView`；热更包 `activity.version` 升版；Lua 模块改为由 `LuaOnlyView` 驱动。
9. **阿福**（`agent/AgentPanel`）→ `AgentView`；悬浮球保留 DDOL，面板打开/关闭改走 `UIManager`。
10. **入口栏**：把 7 个入口按钮 + 红点整合为一个 `HudEntryBar`（`TopLayer` 常驻），或保留在 zhuye 场景但统一由 `UIManager` 管理。**建议统一**，否则“全部抽预制体”不彻底。
11. 每个面板提交：`refactor(ui): <面板> 迁移到 SkierFramework（P8-x）`

**验收（每个面板）**：
- 打开/关闭/重开、层级、排序、点击、滚动、拖拽、输入框光标、红点、数据刷新全部与迁移前一致。
- 场景中不再有对应的旧 Canvas 实例（或旧实例已删除）。
- `UIManager.CloseAll` 后无残留可见面板。
- 现有 E2E 脚本（若有）重新跑通，或补新脚本。

### P9 全量 E2E / 回归 / 文档收口

1. 单机战斗 E2E。
2. 双进程联机 E2E（Host/Client、选人、准备、战斗、死亡、退出、再战）。
3. 现有业务回归（登录→zhuye→好友→背包→任务→邮件→排行榜→副本→活动→阿福）。
4. 性能基线（帧率、GC、DrawCall、网络带宽粗测）。
5. 文档回写：本文件“变更记录/风险登记册/验收结果”。
6. 提交：`docs(battle): 完成移植验收与文档收口（P9）`

---

## 5. 逐文件映射表

### 5.1 战斗核心（源 `Assets/script/` → 目标 `Assets/Scripts/Battle/`）

| 源 | 目标 | 改动级别 | 说明 |
|---|---|---|---|
| `状态机/IState.cs` | `Core/StateMachine/IState.cs` | 原样 | 全局命名空间 |
| `状态机/StateMachine.cs` | `Core/StateMachine/StateMachine.cs` | 原样 | |
| `状态机/MovementStateType.cs` `ComboStateType.cs` | 同目录 | 原样 | 枚举顺序=网络协议，新增只能追加末尾 |
| `状态机/PlayerMovementState.cs` `PlayerMovementStateMachine.cs` | `Core/StateMachine/` | 原样 | 依赖 Input System |
| `状态机/PlayerStates/*`（8 个） | `Core/StateMachine/PlayerStates/` | 原样 | |
| `状态机/PlayerComboState.cs` `playercombat/*`（7 个） | `Core/StateMachine/Combo/` | 原样 | `CharacterCombo` 引用 `AIStateMachine`（可选编译/条件） |
| `player/Player.cs` | `Core/Player/Player.cs` | **改** | Unity 6 API、PauseManager、相机数组 |
| `player/PlayerSO.cs` `PlayerStateData.cs` `CharacterMoveControllerBase.cs` `CharacterNameList.cs` | `Core/Player/` | 原样 | |
| `player/playerinputaction.cs` | `Core/Input/playerinputaction.cs` | **重新生成** | 目标 Input System 版本生成 |
| `单例/CharacterInputSystem.cs` | `Core/Input/CharacterInputSystem.cs` | 原样 | 确认 `Singleton` 基类版本 |
| `单例/Singleton.cs` `SingletonMono.cs` `SingletonEagerMono.cs` | `Core/Common/` | 原样 | 与 UI 框架的 `Singleton` 重名，选一个 |
| `data/*`（7 个） | `Core/Data/` | 原样 | |
| `damage/*`（3 个） | `Core/Damage/` | 原样 | |
| `buff/*`（13 个） | `Core/Buff/` | 原样 | |
| `ui/HealthModel.cs` | `Presentation/HealthModel.cs` | **改** | NGO 1.13、PauseManager |
| `ui/DamageText.cs` `DamageTextManager.cs` | `Presentation/` | 小改 | 目标 UI 层 |
| `newui/bloodpanel.cs` | `Presentation/BloodPanelView.cs` | **改** | 改 `UIView`，预制体化 |
| `sound/*`（5 个） | `Core/Audio/` | 原样 | 复制 AudioMixer |
| `tool/GameTimer.cs` `TimerManager.cs` | `Core/Tools/` | 原样 | |
| `事件/EventCenter.cs` | `Core/Events/EventCenter.cs` | 原样 | |
| `镜头/PlayerCameraUtility.cs` | `Core/Camera/` | 原样 | |
| `镜头/Camera_ZoomController.cs` | `Battle/Presentation/Camera/` | 原样 | 已迁（2026-10-25）：沿用源 GUID `18c4aff2…`，`thirdcamera.prefab` / `tiaozhan.scene` 原有序列化引用自动接上，无资产改动 |
| `镜头/CameraShakeManager.cs` | 不迁 | — | 源战斗未接震屏 |
| `network/RoomState.cs` `BattleSpawnManager.cs` | `Network/` | **改** | NGO 1.13、UnityTransport |
| `newui/Xuanze.cs` `room.cs` `jiaru.cs` `Dating.cs` | `Lobby/` | **改** | 改 `UIView` + 预制体 |
| `vfx/VFX_PoolManager.cs` | 不迁 | — | 用目标 `VfxManager` |
| `AIstate/*` `behavorai/*` | 暂不迁 | — | 单机木桩先不做 AI |
| `bag/*` `equip/*` `skill/*` | 不迁 | — | 目标已有背包；GAS 技能不在本期 |
| `Framework/Lua/*` | 不迁 | — | 目标已有 XLua 胶水 |

### 5.2 UI 框架（源 `Assets/script/Framework/` → 目标 `Assets/Scripts/UI/Framework/`）

| 源 | 目标 | 改动 |
|---|---|---|
| `UI/UIManager.cs` | `UIManager.cs` | 相机/层/资源适配 |
| `UI/UIViewBase/*`（13 个） | `UIViewBase/` | 原样 + 资源路径 |
| `UI/UIScrollView/*` | `UIScrollView/` | 原样 |
| `UI/UIModel/*` | `UIModel/` | `EntityId` 改 `GetInstanceID` |
| `UI/Extends/UIExtension.cs` | `Extends/` | 原样 |
| `UI/UIControlBinding/**` | `UIControlBinding/` | 原样（编辑器抽屉） |
| `UI/UIAdapter.cs` | `UIAdapter.cs` | 原样 |
| `UI/Editor/*`（2 个） | `Editor/` | 原样 |
| `Other/*`（7 个） | `Common/` | 原样（`ObjectPool`/`PrefabPool`/`EventController`/`Layer` 等） |
| `Resources/ResourceManager.cs` | `Resources/ResourceManager.cs` | **改 Resources 适配** |
| `Resources/YooAssetService.cs` | 删除或改空壳 | 见 6.5 |
| `Resources/InstancePool.cs` | 原样 | |

### 5.3 资源（源 → 目标）

| 源 | 目标 | 方式 |
|---|---|---|
| `Assets/Resource/人物/安比.prefab` + `安比/` + `Tex/` | `Assets/Art/Battle/Characters/Anbi/` | 复制 + `.meta` |
| `Assets/Resource/人物/le_Size02_Ellen_Ani_Idle (1).prefab` + `鲨鱼/` | `Assets/Art/Battle/Characters/Ellen/` | 复制 + `.meta` |
| `Assets/Resource/动作/Animation/安比/安比攻击.fbx` `安比移动.fbx` | `Assets/Art/Battle/Animations/Anbi/` | 只复制这两个 |
| `Assets/Resource/动作/Animation/027_.../FBX_redo/*.fbx` | `Assets/Art/Battle/Animations/Ellen/` | 按引用闭包 |
| `Assets/script/playso/**` | `Assets/Art/Battle/Config/PlayerSO/` | 全部 + `.meta` |
| `Assets/script/buff/buffso/**` | `Assets/Art/Battle/Config/Buff/` | 全部 |
| `Assets/Resources/BuffDatabase.asset` | `Assets/Resources/Battle/BuffDatabase.asset` | 路径改，引用改 |
| `Assets/script/sound/SoundData.asset` | `Assets/Resources/Battle/SoundData.asset` | 路径改 |
| `Assets/Resource/audoi/**` | `Assets/Art/Battle/Audio/` | 按 `SoundData`/ComboData 引用闭包 |
| `Assets/Third Parts/VFX_Klaus/Prefabs/Stylized Hit & Slash/**` | `Assets/Art/Battle/VFX/` | 选取命中/挥砍 |
| `Assets/HOTS/UI/Prefabs/*.prefab` | `Assets/Resources/GameUI/Battle/` | 复制 + 字体重映射 |
| `Assets/Resource/ui/**` | `Assets/Resources/Battle/UI/` | 按 UI 预制体引用闭包 |

---

## 6. 兼容性改造清单（代码级）

### 6.1 Unity 6 → Tuanjie 2022.3

| 位置 | 源写法 | 目标写法 |
|---|---|---|
| `ResourceManager.cs:149/189` | `EntityId id = instanceObject.GetEntityId();` | `int id = instanceObject.GetInstanceID();`；字典键 `EntityId`→`int` |
| `UIModelManager.cs:104` | `EntityId id = go.GetEntityId();` | `int id = go.GetInstanceID();` |
| `UIRenderToTexture.cs:136/225` | `EntityId` | `int` / `GetInstanceID()` |
| `Player.cs:368/369/429` | `FindObjectsByType<T>(FindObjectsInactive.Include)` | `FindObjectsOfType<T>(true)` |
| `bloodpanel.cs:80` | `FindObjectsByType<Player>()` | `FindObjectsOfType<Player>()` |
| `Singleton.cs:55` | `FindAnyObjectByType(typeof(T))` | `FindObjectOfType(typeof(T))`（若 Tuanjie 支持 `FindAnyObjectByType` 可保留，实测后决定） |
| 其他 | `Object.FindAnyObjectByType<T>()` | `Object.FindObjectOfType<T>()` |
| `Camera.main` 相关 | 同名/多相机 | 改为显式引用或 `BattleCameraRig` |

> 原则：**优先用 `FindObjectsOfType` / `FindObjectOfType`**，它们在 2022.3 一定存在；性能差异只在初始化阶段，可接受。

### 6.2 NGO 2.13 → 1.13

| API | 2.x | 1.13 | 处理 |
|---|---|---|---|
| `NetworkVariable<T>` | 同 | 同 | 直接编译 |
| `NetworkVariable` 写权限 | `NetworkVariableWritePermission.Owner/Server` | 同 | 直接 |
| `ServerRpc/ClientRpc` | 同 | 同 | 直接 |
| `NetworkManager.ServerTime.Time` | 同 | 同 | 直接 |
| `NetworkObject.SpawnWithOwnership(ulong)` | 同 | 同 | 直接 |
| `NetworkManager.SceneManager.LoadScene` | 同 | 同 | 直接 |
| `SceneEventProgressStatus` | 同 | 同 | 直接 |
| `CustomMessagingManager` | 同 | 同 | 直接 |
| `FastBufferWriter/Reader` | 同 | 同 | 直接 |
| `NetworkBehaviour.OnNetworkSpawn/Despawn` | 同 | 同 | 直接 |
| `NetworkTransform` | 同 | 同 | 直接 |
| `NetworkPrefabsList` | 同 | 同 | 建议运行时注册，避免复制 Unity 6 资产 |
| 传输 | `Kcp2KTransport.host` | `UnityTransport.SetConnectionData(ip, port)` | 重写 `jiaru`、重建 `Networking.prefab` |
| **RPC 写法** | `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]` | `[ServerRpc(RequireOwnership = false)]` | **源项目 2 处必须改**：`HealthModel.RequestDamageServerRpc`、`BuffComponent.AddBuffServerRpc`；`RpcInvokePermission`/`SendTo` 在 NGO 1.13 不存在 |
| RPC 回调 | `[ClientRpc]` | 同 | 直接 |
| 请求无敌 | `[ServerRpc]` | 同 | 直接（注意默认 RequireOwnership=true，拥有者调用没问题） |

> 已知 1.x 限制：`NetworkVariable` 只能在 Spawn 之后写（源项目注释已写明），保持“Spawn 后写 NetSeatId/NetCharId”的顺序。
> 若 1.13 的 `NetworkVariable<T>` 构造函数签名差异（例如缺少默认参数），按实际编译错误补全参数。

### 6.3 Input System

1. `manifest.json` 加 `"com.unity.inputsystem": "1.7.0"`（先试；失败换 1.11.2）。
2. `ProjectSettings/ProjectSettings.asset`：`activeInputHandler: 2`。
3. 复制 `Assets/Resource/动作/playerinputaction.inputactions` → `Assets/Art/Battle/Input/playerinputaction.inputactions`。
4. 在 Inspector 勾选 **Generate C# Class**，生成到 `Assets/Scripts/Battle/Core/Input/playerinputaction.cs`（类名保持 `Playerinputaction`，避免改状态机代码）。
5. 确认 action map 名为 `player`，动作名 `move/attack/defense/skill/dash/look` 与源一致。
6. 英雄 prefab 上的 `PlayerInput` 组件**移除**（`CharacterInputSystem` 已统一管理；保留会导致两套 Action 同时启用/争抢）。
7. 如果 1.7.0 的生成器不支持源 `.inputactions` 的某些字段：用 1.11.2；再不行，保留源 `playerinputaction.cs` 并逐条修 API（见风险 R3）。

### 6.4 UOS KCP → Unity Transport

- `Networking.prefab` 用编辑器脚本重建：
  ```
  GameObject go = new("Networking");
  var nm = go.AddComponent<NetworkManager>();
  var ut = go.AddComponent<UnityTransport>();
  ut.SetConnectionData("127.0.0.1", 7777);
  nm.NetworkTransport = ut;
  go.AddComponent<RoomState>();
  DontDestroyOnLoad(go);
  ```
- `jiaru.OnIpSubmitted`：`ut.SetConnectionData(ip, 7777); nm.StartClient();`
- 端口 7777；避免与 competeserves 的 8888/8897/8899 冲突。
- 双进程测试：Editor + Windows Build，或两个 Build；同一台机用 127.0.0.1。

### 6.5 UI 资源层：YooAsset → Resources

`ResourceManager` 保持公开 API 不变，内部替换：

| 方法 | 新实现 |
|---|---|
| `NormalizeToLocation` | 去掉 `Assets/Resources/` 前缀与扩展名；保留相对路径 |
| `InstantiateAsync(path)` | `Resources.Load<GameObject>(path)` → `Instantiate`；走 `InstancePool` |
| `LoadAssetAsync<T>(path)` | `Resources.Load<T>(path)`；同步结果用 `UniTask.FromResult` 包装 |
| `TryGetAsset<T>` | `Resources.Load<T>` |
| `UnLoadAsset` | 仅清缓存/池；`Resources.UnloadAsset` 可选（不要卸载常驻） |
| `LoadSceneAsync` | `SceneManager.LoadSceneAsync` |
| `ReadTextStringAsync` | `Resources.Load<TextAsset>` |
| `Cleanup` | 清池/缓存，不调用 YooAsset |
| `InitializeAsync` | 直接返回（无异步初始化） |

- `YooAssetService` 可删除；若编辑器工具还引用，保留一个空壳类。
- **活动热更**继续用现有 `LuaActivityHost` 的 YooAsset 链路，不走 UI 框架的 `ResourceManager`。两者互不影响。
- UIConfig 路径：`Resources/GameUI/UIConfig.json`；`UIConfig.GetAllConfigs()` 改为 `Resources.Load<TextAsset>("GameUI/UIConfig")`。
- UIConfig 条目 `path` 改为 `GameUI/<Group>/<Name>`（无扩展名）。

### 6.6 URP 相机栈（Tuanjie 14.2-t1 分叉）

- 目标实测：**不支持两台 Base 相机顺序叠加**；UI 必须是 Overlay 并注册到当前 Base 相机的 `cameraStack`。
- `UIManager` 每次场景加载/战斗相机切换后，遍历所有 Base 相机注册 UI 相机（幂等）。
- 战斗场景两台 Base 相机同时存在但只启用一台；注册时只注册启用的那台，或全部注册（停用的不渲染，无副作用）。
- `Camera.main` 在战斗场景可能指向座位相机；不要依赖它做 UI。

### 6.7 Animancer

- 目标 7.4.2 与源一致；`ClipTransition`、`AnimancerState.Events`、`state.Time/Length` 直接用。
- 复制 Animancer 资产（`.asset`）时保留 `.meta`；`ClipTransition` 的 `_Clip` 引用 AnimationClip（FBX 子资产），必须连 FBX + `.meta` 一起复制。
- 英雄 prefab 的 `AnimancerComponent` 需重新指向迁移后的 Animator/骨骼；若复制 prefab 保 GUID，则无需改。

### 6.8 DOTween / UniTask / TMP

- DOTween：目标已有；`UIViewAnim`/`UIAnim` 直接用。确认 `DOTWEEN` 宏已定义（目标 defineSymbols 有）。
- UniTask：目标嵌入包 2.5.11；`UniTask.WaitForSeconds(realSeconds, ignoreTimeScale:true, cancellationToken:token)` 可用。
- TMP：目标 3.0.9；源 UI 预制体字体引用 `LiberationSans SDF` → 重映射为 `NotoSansSC-Regular SDF`（或依赖 TMP 全局回退）。
- 中文字体：目标已配 NotoSansSC 全局回退；迁移后跑一次“中文渲染检查”（设置/背包/任务/房间）。

### 6.9 层与物理矩阵

- **必须按源项目层号对齐**（源 prefab 的 `m_Layer` 与 `LayerMask.m_Bits` 已序列化，改层号会静默失效）：
  - 3: `player`（源英雄 prefab 根 layer=3）
  - 4: `Water`（目标已有）
  - 5: `UI`（目标已有）
  - 6: `environment`（源场景/怪物相关）
  - 7: `enemy`（源怪兽 prefab layer=7）
- 不要用“player=6/enemy=7”的方案：目标层 6 在源项目语义里是 `environment`，混用会让命中过滤与碰撞矩阵错位。
- 英雄 prefab 根及子物体设 `player`；怪兽/木桩设 `enemy`。
- `Physics.asset`（DynamicsManager）确保 player/enemy 与 Default 的碰撞开启；`OverlapSphere` 不受碰撞矩阵影响，但 CharacterController 移动会。
- `LayerMask.GetMask("player","enemy")` 在层存在后返回非 0。

### 6.10 场景 YAML

- `multiplecompete.unity` 的 `RenderSettings serializedVersion: 10`、部分组件 13，属于 Unity 6 写法。Tuanjie 可能：
  - 正常加载并升级（最好情况）；
  - 报“serializedVersion 不支持/组件丢失”（走回退重建）。
- 复制前先备份原场景；复制后**不要**直接覆盖目标已有场景。
- 若回退重建：按 3.5 表逐项创建，不追求还原原场景美术。

### 6.11 GUID / 中文路径 / 大小写

- 复制资源必须连 `.meta` 一起复制，GUID 才能保持不变；否则 prefab/asset 引用会断。
- 中文文件名在 PowerShell/GBK 控制台下会显示乱码，但 Unity/AssetDatabase 能处理；建议：
  - 目录改 ASCII（`Anbi`/`Ellen`）；
  - 文件名尽量保留（`安比攻击.fbx` 可改 `Anbi_Attack.fbx`，`.meta` 跟着改名，GUID 不变）。
- Windows 大小写不敏感，但 git/Unity 可能区分；统一用 PascalCase/ASCII。

### 6.12 暂停与顿帧共存（C7）

目标 `PauseManager` 当前只存 `savedTimeScale`；源 `HitStop` 直接写 `0.03/1`。两者会互相覆盖。建议改造：

```csharp
// PauseManager 增加：
private static float hitStopScale = 1f;
private static int hitStopCount;
public static void PushHitStop(float scale) { hitStopCount++; hitStopScale = Mathf.Min(hitStopScale, scale); Apply(); }
public static void PopHitStop() { hitStopCount = Mathf.Max(0, hitStopCount - 1); if (hitStopCount == 0) hitStopScale = 1f; Apply(); }
private static void Apply() {
    bool paused = stack.Count > 0;
    Time.timeScale = paused ? 0f : (hitStopCount > 0 ? hitStopScale : savedTimeScale);
    PausedChanged?.Invoke(paused);
}
```

`Player.HitStop` 改为 `PauseManager.PushHitStop(0.03f)` + `UniTask.WaitForSeconds(realSeconds, true)` + `PopHitStop()`；取消/接管时用引用计数或 token 防重复 Pop。
**验收**：暂停面板打开时 `timeScale=0`；关闭后若仍在顿帧则恢复 0.03，顿帧结束回 1；不会出现“关面板后世界仍冻结”。

### 6.13 事件系统 / 输入模块

- 战斗场景只保留一个 `EventSystem`；`StandaloneInputModule` 在 `activeInputHandler=Both` 下仍可用。
- 若切到 `InputSystemUIInputModule`，现有 UI 的点击/拖拽需回归；本期不动，保留 `StandaloneInputModule`。
- `UIManager.Initialize` 里 `EventSystem.current` 可能为空；加兜底创建（照目标现有 `EventSystem_Persistent` 约定）。

### 6.14 代码命名冲突

- 源 `CharacterState` 是全局类；Animancer 的 `CharacterState` 在 `Animancer.FSM` 命名空间，源文件只 `using Animancer;`，不冲突。
- 源 `Singleton<T>` 与目标现有 `Singleton` 若有同名，合并为一个；不要保留两份。
- `Player`/`HealthModel`/`EventCenter`/`DamageCalculator`/`RoomState` 目标均无同名类（已核查）。
- `UIManager`/`UIType`/`UIConfig` 目标无同名类。

---

## 7. 旧 UI 全量抽预制体方案（D9 核心）

### 7.1 目标形态

- 每个业务面板 = `Assets/Resources/GameUI/<Group>/<Name>.prefab`。
- 预制体根挂：
  - `<Name>View : UIView`（业务逻辑）
  - `UIControlData`（控件绑定）
  - `Canvas` + `GraphicRaycaster`（`UIView.OnInit` 会自动补；预制体里可先挂）
- `UIConfig.json` 一条记录：
  ```json
  { "uiType": "BagView", "path": "GameUI/Bag/BagView", "isWindow": false, "uiLayer": "NormalLayer", "isAutoNavigation": false, "isLuaUI": false }
  ```
- 场景中只剩：
  - 入口按钮（`HudEntryBar` 或单独按钮）
  - 常驻 3D/世界对象
  - 战斗场景的 3D 对象

### 7.2 迁移流水线（Editor 工具）

`Assets/Scripts/UI/Framework/Editor/UIPrefabMigrationTool.cs`：

1. **选择**：场景里选 Canvas 根（如 `beibao`）。
2. **保存预制体**：`PrefabUtility.SaveAsPrefabAsset(root, targetPath)`；确保 `includeInactive=true`。
3. **加组件**：预制体根加 `UIControlData`（若没有）。
4. **自动绑定**：
   - 读取根上现有控制器（如 `BagUIController`）的序列化字段；
   - 对每个字段：按字段名在子树里找同名 GameObject，按字段类型取组件；
   - 写入 `ctrlItemDatas`（name/type/targets）；
   - 可选：给字段补 `[ControlBinding]`（需要改源文件，生成新 View 时做）。
5. **生成 View 类**：如果控制器类名不是 `UIView`，生成一个薄包装：
   ```csharp
   public class BagView : UIView {
       [ControlBinding] public Button CloseButton;
       // ... 由工具生成
       public override void OnAddListener() { /* 迁移自 OnOpen/Start 的监听 */ }
       public override void OnRemoveListener() { /* 迁移自 OnClose 的移除 */ }
   }
   ```
6. **更新 UIConfig/UIType**：幂等追加。
7. **场景清理**：删除旧 Canvas 实例（先禁用并保存，确认预制体正常后再删）。
8. **回归**：打开/关闭/数据/交互。

### 7.3 控制器改造模板（以 Bag 为例）

迁移前：

```csharp
public class BagUIController : MonoBehaviour {
    [SerializeField] Button closeButton;
    void Start() { closeButton.onClick.AddListener(OnClose); }
    void OnDestroy() { closeButton.onClick.RemoveListener(OnClose); }
    public void Open() { gameObject.SetActive(true); ... }
}
```

迁移后：

```csharp
public class BagView : UIView {
    [ControlBinding] public Button closeButton;
    public override void OnAddListener() { closeButton.onClick.AddListener(OnCloseClicked); }
    public override void OnRemoveListener() { closeButton.onClick.RemoveListener(OnCloseClicked); }
    public override void OnOpen(object userData) { base.OnOpen(userData); RequestBag(); }
    void OnCloseClicked() => UIManager.Instance.Close(UIType.BagView);
}
```

要点：
- `Start`/`OnEnable` 里的初始化 → `OnInit`（只一次）或 `OnOpen`（每次打开）。
- `OnDestroy` 的退订 → `OnRemoveListener`。
- `gameObject.SetActive` → `UIManager.Open/Close`。
- 跨面板调用 → `UIManager.GetView<T>()` 或 `UIManager.Event`。
- 现有 `VirtualScrollList` 可保留；后续可换 `UIScrollView`。

### 7.4 跨面板引用处理

现有面板之间有大量直接引用（如 `QuestUIController.panelsToCloseOnOpen` 指向 ChallengePanel/LeaderboardPanel/BagPanel）。抽预制体后这些引用会断。统一改为：

1. **互斥打开**：`UIManager.Instance.CloseAll(ignoreId: 当前View)` 或 `UIManager.Event` 广播。
2. **取指定面板**：`UIManager.Instance.GetOpenedView(UIType.XxxView)`。
3. **数据共享**：保留现有 Manager 单例（BagManager/QuestManager/MailManager），不通过 UI 引用。
4. **红点**：`RedDotItem` 目标改为预制体内节点；入口按钮的红点保留在 `HudEntryBar`。

### 7.5 入口栏与 HUD

- 建议新增 `HudEntryBar` 预制体（`TopLayer` 常驻）：
  - 好友 / 排行榜 / 挑战 / 背包 / 任务 / 邮件 / 副本 / 活动 / 阿福 / **对战**
  - 每个按钮保留现有红点组件与图标。
- zhuye 场景只保留 `HudEntryBar` 实例（或由 `UIManager.Open` 常驻）。
- 按钮点击统一 `UIManager.Open(UIType.XxxView)`。
- 现有入口按钮在各自 Canvas 内；先迁移到 `HudEntryBar`，再删除旧 Canvas。

### 7.6 活动面板（LuaOnlyView）

1. 现有 `ActivityPanel.prefab`（`Assets/HotContent/UI/`）+ `activity_main.lua.txt`。
2. 新建 `ActivityView : LuaOnlyView`，`luaModuleName = "UI/ActivityView"`；或直接把 `ActivityShell` 的 Lua 装载逻辑包进 `LuaOnlyView`。
3. `UIConfig.json` 条目 `isLuaUI=true`，`path=GameUI/Activity/ActivityPanel`。
4. Lua 侧改为由 `LuaOnlyView` 注入的生命周期驱动（`OnInit/OnOpen/OnClose`），不再自己建 Canvas。
5. 热更包版本升到 `1.2.0`，`BuildAndPublish` 后验证下载链路。
6. 注意 `#if XLUA`：目标当前没有 `XLUA` 宏；需在 `Player Settings` 加 `XLUA`，并把 `UILuaView` 的 `#if XLUA` 分支启用。确认 XLua 生成代码已包含 `UIView` 相关类型（若 Lua 侧需要）。

### 7.7 阿福面板

1. `agent/AgentPanel` 子树 → `Assets/Resources/GameUI/Agent/AgentView.prefab`。
2. `AgentView : UIView` 承接 `AgentUIController` 的面板逻辑；`AgentFloatBall` 保留 DDOL，只把“打开面板”改为 `UIManager.Open(UIType.AgentView)`。
3. 面板开关状态 `IsPanelOpen` 改查 `UIManager.IsOpen`。
4. 3107/3104 气泡逻辑不变（悬浮球仍常驻）。
5. 回归：登录气泡、单击开面板、双击隐藏、拖动持久化、切场景、历史回放。

### 7.8 副本 HUD

- `DungeonHud`/`DungeonMinimap`/`DungeonGpuMinimap`/`DungeonBigMap` 是程序化 UI，不强求预制体化：
  - 方案 A（推荐）：`DungeonHudView : UIView`，在 `OnInit` 里调用现有 `BuildUi()`；小地图/大地图继续程序化，但 Canvas 通过 `UIManager.AdoptCanvas` 挂到 `TopLayer`/`TipLayer`。
  - 方案 B：把程序化生成结果保存为预制体（不推荐，维护成本高）。
- 3D 世界血条/飘字保持 WorldSpace/Overlay 逻辑，不进 UI 层。

---

## 8. 网络与大厅细节

### 8.1 房间消息协议（逐字保留）

| 消息 | 方向 | payload | 说明 |
|---|---|---|---|
| `RoomState.SelectChar` | C→S | int charId | 点选即发 |
| `RoomState.SetReady` | C→S | int charId | 准备 + 当前选择 |
| `RoomState.CancelReady` | C→S | 无 | 保留 CharId |
| `RoomState.QueryPeerChar` | C→S | 无 | 打开界面拉取 |
| `RoomState.PeerSelectChar` | S→C | int charId（-1=清空） | 转发对手选择 |
| `RoomState.PeerReady` | S→C | byte 1/0 | 推送对手准备 |

### 8.2 时序

```
A(host) StartHost
B(client) SetConnectionData(ip,7777); StartClient
B connected → RoomState.TryRegisterPlayer(B) → 座位 1；给 B 推 A 的选择/准备
A 选人 → ServerSetCharId(A) → 记录 + 给 B 发 PeerSelectChar + 本机事件
B 选人 → SelectChar → ServerSetCharId(B) → 记录 + 给 A 发（A 是 host，走事件）
A/B Ready → CheckStartGame → LoadScene("Battle")
BattleSpawnManager.OnNetworkSpawn(server) → 生成 A/B 英雄（座位 0/1）
客户端收到 NetSeatId → 绑定对应相机
```

### 8.3 断线/退出/再战

- `RoomState.OnClientDisconnected`：删 `_players`/`_seats`，释放座位。
- `room.OnClose`：若不是“正在切战斗场景”，销毁 `Networking.prefab`（断开连接）。
- 战斗结束：服务端/客户端 `NetworkManager.Shutdown()`；`BattleBootstrap` 切回 `zhuye`；`UIManager.CloseAll()`。
- 再战：重新创建 `Networking.prefab`，重新 StartHost/StartClient（不要复用旧 `NetworkManager` 实例）。
- 断线方：`OnClientDisconnectCallback` 关闭房间/战斗 UI，提示“对手已离开”。

### 8.4 防作弊边界（照源项目口径）

- 表现/输入 Owner 权威；数值 Server 权威。
- 服务端 `CalculateIncoming` 只做防御/减伤；攻方伤害由攻方算好上报（源项目取舍）。
- 后续若要加固：服务端按 `sourceId` 校验区间/频率；本期不做，文档标注。

---

## 9. 战斗参数与手感基线

| 参数 | 源默认 | 说明 |
|---|---|---|
| `damageMultiplier` | 0.08 | 攻击力 8% |
| `critRate` / `critMultiplier` | 0.2 / 2.0 | 每目标独立掷骰 |
| `attackDistance` / `comboOffset` | 1 / 1 | 球形判定 |
| `hitFrameTime` / `linkCancelTime` | 0.35 / 0.6 | 归一化 |
| `pauseFrameTime` | 0 | 命中顿帧秒 |
| `DefenseK` | 100 | 防御曲线 |
| `MaxDamageReduction` | 0.9 | 减伤上限 |
| `atkBufferWindow` | 1.0s | 输入缓冲 |
| `TapThreshold` | 0.13s | 轻点/长按 |
| `RemotePhaseBuffer` | 0.05s | 远程相位缓冲 |
| `DodgeInvincibleWindow` / `Cooldown` | 0.3 / 1.5 | 闪避 |
| `HitStop timeScale` | 0.03 | 顿帧 |

> 目标项目已有 `PauseManager`；顿帧必须走 6.12 的合并方案，不能直接写 `Time.timeScale`。

---

## 10. 测试与验收

### 10.1 每阶段通用门禁

1. `unity_editor.refresh`：0 Error；警告只允许已知无害项。
2. Play 冒烟：进入目标场景 → 打开/关闭新 UI → 无 NRE。
3. 录屏（动态表现）或数据断言（静态/逻辑）。
4. `git status` 只包含本阶段文件。

### 10.2 单机战斗 E2E

- 打开 `Battle.scene`，不创建 NetworkManager。
- 断言：英雄存在、`Player.IsSpawned=false`、状态机初始 Idle。
- 模拟输入（Input System 的 `InputTestFixture` 或直接调用 `CharacterInputSystem` 的测试口/反射）。
- 连招 3 段、收尾接段、闪避窗口、Buff、死亡、血条/飘字。
- 视觉：录屏 5–8 秒，VLM 只验形态（动画/UI 出现），数据断言为主。

### 10.3 双进程联机 E2E

1. 启动 Host（Editor 或 Build A）。
2. 启动 Client（Build B），输入 127.0.0.1。
3. 断言双方 `IsConnectedClient`、`RoomState` 人数=2、座位 0/1。
4. 选人/准备/切场景。
5. 断言两个英雄 `IsSpawned`、`OwnerClientId` 正确、`NetSeatId` 0/1。
6. A 攻击 B：B 血量下降、A 端看到 B 受击、双方血条一致。
7. B 退出：A 收到断开、UI 提示。
8. 再战：重新创建 Networking，流程重跑。

### 10.4 现有业务回归

- 登录 → zhuye → 好友/聊天（文字+表情）→ 背包（使用/丢弃/整理/拖拽/500 格滚动）→ 任务（接取/提交/放弃/红点）→ 邮件（读/领取/删除/红点）→ 排行榜（刷新/我的排名）→ 副本（进出/掉落/小地图/大地图）→ 活动（Lua 面板打开/关闭/抽卡/签到）→ 阿福（悬浮球/聊天/历史）。
- 每个面板迁移后立即回归，不等全部迁完。

### 10.5 性能基线

- 战斗场景 60 FPS（目标机）；伤害飘字 < 50 并发；UI DrawCall 记录迁移前后。
- 网络：2 人局带宽粗测；`NetworkTransform` 频率按需调整。
- GC：连招 100 次无持续分配增长。

---

## 11. 风险登记册

| ID | 风险 | 概率 | 影响 | 触发信号 | 对策/回退 |
|---|---|---|---|---|---|
| R1 | Tuanjie 无法安装 Input System 1.x | 中 | 高 | Package Manager 报不可用/编译失败 | 走 6.3 的旧输入适配层（改 10 个状态文件） |
| R2 | `playerinputaction.cs` 1.19 与目标 Input System 不兼容 | 中 | 高 | 生成类编译错误 | 用目标版本重新生成；必要时手改 API |
| R3 | NGO 1.13 与源 2.13 API 差异超出预期 | 中 | 高 | CS 编译错误/运行时 RPC 不触发 | 逐条对照 1.13 文档；必要时升级 NGO（需评估 Tuanjie 兼容） |
| R4 | Unity 6 场景/prefab 在 2022.3 导入损坏 | 高 | 中 | 组件丢失/场景打不开 | 回退重建 Battle.scene；prefab 用编辑器重建 |
| R5 | UI 框架 `EntityId`/`FindObjectsByType` 等 Unity 6 API | 高 | 中 | 编译错误 | 按 6.1 改 |
| R6 | UI 相机双主人/URP Stack 不生效 | 高 | 高 | UI 不显示/3D 黑屏 | 按 3.3 统一；只保留一台 UI Overlay 相机 |
| R7 | 旧面板抽预制体后序列化引用断裂 | 高 | 高 | 面板空白/按钮无效/NRE | 按 7.2 工具 + 逐面板回归；保留旧场景备份 |
| R8 | 字体缺失/中文乱码 | 中 | 中 | 文字显示 □/空白 | 重映射 NotoSansSC；TMP 全局回退 |
| R9 | 材质/Shader 丢失（粉色） | 中 | 中 | 角色粉色/无贴图 | 改用 URP/Lit 或目标 YSA Toon；材质审计 |
| R10 | 动作资源引用闭包漏文件 | 中 | 中 | 动画为空/`ClipTransition` 丢 clip | 用 GUID 引用闭包脚本，不手工挑 |
| R11 | 全量 UI 迁移回归面过大 | 高 | 高 | 多面板同时坏 | 一面板一提交、一面板一回归；保留旧 Canvas 直到验收 |
| R12 | 顿帧/暂停互相覆盖 | 高 | 中 | 关面板后世界冻结/不恢复 | 按 6.12 合并 |
| R13 | 双进程测试环境（端口/构建） | 中 | 低 | 连接失败 | 7777 端口、Build Settings、防火墙 |
| R14 | git 仓库被 1 GB 动作资源撑大 | 高 | 中 | 提交体积巨大 | 引用闭包；必要时 Git LFS（先问用户） |
| R15 | 活动 Lua 热更与 UI 框架冲突 | 中 | 中 | 活动面板打不开/LuaEnv 崩溃 | 保持 LuaEnv 生命周期规则（不在 Lua 回调栈内 Dispose） |
| R16 | 阿福 DDOL 与 UI 框架相机/层级冲突 | 中 | 中 | 悬浮球不显示/面板被遮 | 悬浮球保留 DDOL + 框架相机 Facade；面板走 UIManager |
| R17 | 现有 `UICameraService.SweepAll` 每秒把框架画布改回自己的相机 | 高 | 高 | UI 闪烁/错位 | Facade 化后 Sweep 只注册 Stack，不改 `worldCamera` |
| R18 | C# 9 语法/新 API（target-typed new 等） | 低 | 低 | 编译错误 | 改为显式类型 |

---

## 12. Git 提交计划

### 12.1 提交纪律

- 只暂存本次改动；先 `git status` 区分“我的改动”和“用户正在编辑的资源”。
- 提交信息：中文 + 类型前缀（`feat/fix/refactor/chore/docs/test`）。
- 每个阶段/面板一个提交；大阶段内部可按逻辑组拆分。
- 不 push（除非用户明确要求）。
- 不提交 `Library/`、`Temp/`、`Logs/`、`obj/`、`.csproj` 等生成物。

### 12.2 提交序列（建议）

| # | 阶段 | 提交信息 |
|---|---|---|
| 1 | P0 | `chore(battle): 准备战斗移植工程配置（Input System/物理层/目录）` |
| 2 | P1-a | `feat(ui): 移植 SkierFramework 内核（UIManager/UIView/UIConfig）` |
| 3 | P1-b | `feat(ui): 资源层改 Resources 适配并修复 Unity6 API` |
| 4 | P1-c | `feat(ui): UI 相机统一由 UIManager 拥有（UICameraService Facade）` |
| 5 | P2 | `refactor(ui): 建立场景面板抽预制体流水线（排行榜样板）` |
| 6 | P3-a | `feat(battle): 移植选人/房间 UI 预制体` |
| 7 | P3-b | `feat(battle): zhuye 新增对战入口与大厅流程` |
| 8 | P4-a | `feat(battle): 移植状态机/移动/连击核心` |
| 9 | P4-b | `feat(battle): 移植伤害/属性/Buff/血量` |
| 10 | P4-c | `feat(battle): 移植表现层（飘字/血条/音效/顿帧）` |
| 11 | P5 | `feat(battle): 接入 NGO 1.13 联机同步与战斗生成` |
| 12 | P6 | `feat(battle): 战斗表现层收口（VFX/相机/暂停）` |
| 13 | P7 | `feat(battle): 迁移安比/艾莲角色与动作资源` |
| 14+ | P8 | `refactor(ui): 好友/聊天迁移到 SkierFramework` 等一面板一提交 |
| 最后 | P9 | `docs(battle): 移植验收与文档收口` |

---

## 13. 源码补充研读（2026-10-19，按 `D:\project\compete` 实测）

> 本章是对源工程逐文件实测后的补充，**优先级高于前文的推测**。凡与前文冲突，以本章为准。

### 13.1 输入动作表（`.inputactions` 实测）

源文件：`Assets/Resource/动作/playerinputaction.inputactions`，资产名 `playerinputaction`，只有一个 Action Map：`player`，无 Control Scheme、无手柄绑定。

| Action | Type | 绑定 | 用途 |
|---|---|---|---|
| `move` | Value/Vector2 | `2DVector`：W/S/A/D | 相机相对移动 |
| `dash` | Button | `<Keyboard>/shift` | 冲刺/收尾打断 |
| `attack` | Button | `<Mouse>/leftButton` | 轻攻击 |
| `skill` | Button | `<Keyboard>/e` | 技能 |
| `defense` | Button | `<Mouse>/rightButton` | 防御/闪避窗口 |
| `look` | Value/Vector2 | `<Pointer>/delta` | 相机视角 |
| `zoom` | Value/Axis | `<Mouse>/scroll/y` | 相机缩放（源战斗未强依赖） |

实施要点：
- 复制 `.inputactions` 时保留其 `.meta`（Action 的内部 GUID 不变），在目标工程用目标 Input System 版本重新生成 C# 类；类名保持 `Playerinputaction`，Action Map/动作名逐字不变，状态机代码零改动。
- 目标 Input System 若为 1.7.x，生成类可能与源 1.19.0 有 API 差异；优先重新生成，不要直接复制 `playerinputaction.cs`。
- 没有手柄/触屏绑定；后续若要做移动端，另加 Control Scheme，不改现有 Action 名。

### 13.2 联机预制体实测配置（`Assets/Resources/联机.prefab`）

| 组件 | 字段 | 值 |
|---|---|---|
| `NetworkManager` | `TickRate` | 30 |
| | `EnableSceneManagement` | 1 |
| | `ConnectionApproval` | 0 |
| | `LoadSceneTimeOut` | 120 |
| | `SpawnTimeout` | 10 |
| | `RunInBackground` | 1 |
| | `LogLevel` | 1 |
| | `PlayerPrefab` | 空（英雄由 `BattleSpawnManager` 生成） |
| | `NetworkConfig.Prefabs.NetworkPrefabsLists` | 引用 `DefaultNetworkPrefabs.asset` |
| `Kcp2KTransport`（UOS） | `host` / `port` | `127.0.0.1` / `7777` |
| `RoomState` | `RequiredPlayerCount` | 2 |
| | `HeroCount` | 2 |
| | `BattleSceneName` | `multiplecompete` |

实施要点：
- 目标用 `UnityTransport`，端口 **7777**（源就是 7777；不要改成 8888，避免与 competeserves 冲突）。
- `BattleSceneName` 改为目标场景名 `Battle`。
- `NetworkPrefabsList` 不复制 Unity 6 资产；在 `RoomState`/`BattleBootstrap` 里运行时注册两个英雄 prefab（`NetworkConfig.Prefabs.Add`）。
- `TickRate=30` 保留；`EnableSceneManagement=1` 保留；`RunInBackground=1` 保留（双进程测试必需）。

### 13.3 源项目层号与怪物层（**重要修正**）

源 `ProjectSettings/TagManager.asset` 实测：

| 层号 | 名称 | 用途 |
|---|---|---|
| 3 | `player` | 英雄 prefab 根（实测 `m_Layer: 3`） |
| 4 | `Water` | 目标已有 |
| 5 | `UI` | 目标已有 |
| 6 | `environment` | 环境/场景相关 |
| 7 | `enemy` | 怪兽 prefab 根（实测 `m_Layer: 7`） |

**P0 必须按 3/6/7 对齐**，不要采用“player=6/enemy=7”。原因：源 prefab 的 `m_Layer`、`LayerMask.m_Bits`、`ComboData.enemyLayer` 都已序列化；改层号会静默改变命中/碰撞语义。

另：安比 prefab 的 `Player` 组件里有一个序列化 `LayerMask m_Bits: 64`（= 层 6 `environment`）。迁移时先确认它对应源码里的哪个字段（当前 `Player.cs` 未声明 LayerMask，可能是 prefab 遗留字段），不要盲目保留。

### 13.4 英雄 prefab 组件栈与“必须重建”的结论

**安比 prefab（`Assets/Resource/人物/安比.prefab`）根组件实测：**

```
CharacterController      Height 1.43, Radius 0.3, Slope 45, Step 0.3,
                         Skin 0.08, MinMove 0.001, Center (0, 0.8, 0)
PlayerInput              （Input System 包组件）
AnimancerComponent       （Animancer 7.4.2）
NetworkObject            （NGO 包组件）
Player                   playerSO = Playeranbi.asset
ActorAudioComponent      soundData = SoundData.asset, characterName = 9
HealthModel              initialMaxHP = 100, initialHP = 100
BuffComponent
NetworkTransform         （NGO 包组件）
CharacterInputSystem     （源项目单例组件）
Animator                 Avatar = 安比.fbx, Controller = null, ApplyRootMotion = 1
```

**艾莲 prefab（`Assets/Resource/人物/le_Size02_Ellen_Ani_Idle (1).prefab`）**：组件栈相同；`Animator.Avatar` = Ellen Idle FBX，`ApplyRootMotion = 0`；`playerSO = PlayerSOshatyu.asset`。

**关键结论：不要直接复制英雄 prefab。** 原因：
1. `NetworkObject`/`NetworkTransform` 的脚本 GUID 属于 NGO 包，Unity 6 与 Tuanjie 的 NGO 版本不同 → 直接复制会变成 Missing Script。
2. `PlayerInput` 的脚本 GUID 属于 Input System 包，版本不同同样可能丢失。
3. 源 prefab 根还挂了 `CharacterInputSystem` 单例组件；目标若再建全局单例会重复，必须移除。
4. `AnimancerComponent`/`Player`/`HealthModel`/`BuffComponent` 是本项目脚本，复制 `.meta` 可保 GUID，但包组件仍需重挂。

**P7 重建流程（推荐）：**
1. 复制 FBX + 材质/贴图 + `.meta`；确认 Avatar 可用。
2. 新建空物体，按上表添加：`CharacterController`（尺寸逐字照抄）→ `Animator`（Avatar=FBX，Controller=null，ApplyRootMotion 按英雄）→ `AnimancerComponent` → `Player` → `HealthModel` → `BuffComponent` → `AttributeComponent` → `CharacterState` → `ActorAudioComponent` → `NetworkObject` → `NetworkTransform`。
3. 根 layer=3（player），tag=Untagged；子物体保持原层。
4. **不要**加 `PlayerInput` 和 `CharacterInputSystem`。
5. 赋 `PlayerSO` 与 `SoundData`；`HealthModel` 初始 100/100。
6. 确认 `CharacterController` 本身可作为受击 Collider（源判定用 `OverlapSphere`，不依赖额外受击盒）。
7. 进 Play 冒烟：待机动画、移动、攻击、受击、死亡。

### 13.5 资源引用闭包实测（用于 P7 复制清单）

以“两个英雄 prefab + 两套 PlayerSO/ComboData + BuffDatabase + SoundData + 源大厅 UI 预制体 + 相机/音效/飘字预制体 + 联机预制体”为根，递归解析 `guid:` 引用闭包：

| 目录 | 文件数 | 体积 |
|---|---:|---:|
| `Assets/Resource/动作/Animation`（FBX 动画） | 17 | 319.5 MB |
| `Assets/Resource/人物/怪兽_by_silentradio_...` | 8 | 52.7 MB |
| `Assets/Resource/人物/安比` | 19 | 21.3 MB |
| `Assets/Resource/字体/...ttf` | 1 | 14.3 MB |
| `Assets/Resource/人物/鲨鱼` | 9 | 11.6 MB |
| `Assets/TextMesh Pro/Resources/Fonts & Materials` | 2 | 2.2 MB |
| `Assets/Resource/audoi` | 13 | 1.6 MB |
| 其余（UI 图、prefab、配置资产、shader） | 47 | <2 MB |
| **合计** | **116** | **425.2 MB** |

Top 文件（节选）：
- `安比攻击.fbx` 49.2 MB
- `怪兽.fbx` 38.2 MB
- `安比移动.fbx` 28.7 MB
- Ellen Idle/Hit/Evade/Branch/Walk/Death 等 12 个 FBX，各 12–18 MB

**裁剪建议（P7 执行时确认）：**
- **不要**复制 14.3 MB 的源 TTF：目标已有 NotoSansSC，中文走目标字体。
- **不要**复制 `怪兽` 与 `aibehavior`/`DefaultNetworkPrefabs`，除非单机调试要 AI 怪物；单机木桩可用目标现有敌人 prefab 或空壳。
- **不要**复制 TMP 内置字体/shader：目标 TMP 3.0.9 已有。
- 这样实际复制约 **350 MB**（仍不建议整目录 robocopy；按本清单逐文件复制并保留 `.meta`）。

### 13.6 源 UI 预制体结构（实测）

| 预制体 | 根组件 | 子节点契约 |
|---|---|---|
| `room.prefab` | `Image` + `UIControlData` + `SkierFramework.room` | `preparezhu`、`Roommessage`、`exit`、`anbi`、`roleenemy`、`prepareenemy`、`password`、`rolezhu`、`shayu`、`ready` |
| `bloodpanel.prefab` | `Image` + `UIControlData` | `bloodzhu/zhublood`、`renwu1`、`renwu2`、`bloodenemy/enemyblood` |
| `Xuanze.prefab` | `Image` + `UIControlData` + `SkierFramework.Xuanze` | `exit`、`jionroom`、`createrroom` |
| `jiaru.prefab` | `Image` + `UIControlData` + `SkierFramework.jiaru` | `jieshao`、`shuru`（`Text Area/Placeholder/Text`）、`exit` |
| `Dating.prefab` | `Image` + `UIControlData` + `SkierFramework.Dating` | `compete`、`activity`、`friend`、`setting`、`role`、`chalenge` |

实施要点：
- 源大厅 UI 的类名是 `Xuanze/room/jiaru/Dating/bloodpanel`，与 `UIConfig.json`/`UIType` 逐字对应。**保留类名**（只把基类改成 `UIView`）可以最大限度保留预制体脚本引用与绑定名；不要为了命名好看而重命名，除非同时改预制体/配置/枚举。
- `room`/`bloodpanel` 的 `bloodpanel` 脚本可能挂在根或子节点；复制后先用 `UIControlData` 检查绑定项。
- 字体统一重映射到目标 NotoSansSC；源 UI 预制体默认 `LiberationSans SDF`，中文靠回退，迁移后要实测“准备/未准备/房间号”等中文。

### 13.7 源战斗场景结构（实测）

`multiplecompete.unity` 根对象/组件：

| 对象 | 组件 | 说明 |
|---|---|---|
| `Plane` | Transform/MeshFilter/MeshRenderer/MeshCollider | 战斗场地（位置 -13.3, 0, -95.1） |
| `manager` | `NetworkObject`（InScenePlaced=1）+ `BattleSpawnManager` | 服务端生成入口 |
| `Directional Light` | Light | 主光 |
| `VFX_PoolManager` | `VFX_PoolManager` | 源特效池（目标改用 `VfxManager`） |
| `Global Volume`（子物体 `04_0`） | Volume | 后处理；迁移时需复制/重建 Volume Profile |
| `1` | Transform | 出生点 A（-13.62, 0.94, -95.04） |
| `2` | Transform | 出生点 B（-12.09, 1.49, -94.70） |
| `EventSystem` | EventSystem + InputModule | 唯一事件系统 |
| prefab 实例 | `Camera.prefab` ×2、`thirdcamera.prefab` ×2、`DamageTextManager.prefab`、`soundmanager.prefab` | 两套座位相机 + 飘字 + 音效 |

`BattleSpawnManager.HeroPrefabList` 实测引用两个英雄 prefab；`SpawnPoints` 引用 `1`/`2`。

实施要点：
- 目标场景名 `Battle`，对象名建议保留 `manager`/`1`/`2`/`Camera`/`Camera (1)`/`thirdcamera`/`thirdcamera (1)`，便于对照源逻辑；但更稳的是把 `Player.TryBindSceneCamera` 的“按名字查找”改为 `BattleCameraRig` 序列化数组（P6）。
- `Global Volume` 的后处理 profile 需要单独迁移；目标 URP 14.2 的 Volume 组件/参数与 Unity 6 有差异，建议重建一个轻量 profile（Bloom/Vignette/Color Adjustments），不要直接复制 Unity 6 的 profile 资产。
- `Plane` 的位置很偏（-13,-95），说明源场景坐标不是原点；重建时统一到原点，出生点改为 `(±3, 0, 0)` 之类，避免坐标继承错误。

### 13.8 `HealthModel` / `BuffComponent` 的 NGO API 兼容性

实测源码使用的 API（全部在 NGO 1.13 中存在）：

- `NetworkVariable<float>`（`NetworkVariableReadPermission.Everyone` / `NetworkVariableWritePermission.Server`）
- `NetworkList<BuffNetState>`（`BuffNetState : INetworkSerializable`）
- `[ServerRpc]` / `[ClientRpc]`
- `NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out var no)`
- `NetworkManager.ServerTime.Time`

结论：**不能“无需改 API”**。必须处理 RPC 写法：`HealthModel.RequestDamageServerRpc` 与 `BuffComponent.AddBuffServerRpc` 在源项目用的是 NGO 2.x 的 `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]`，NGO 1.13 **没有** `RpcAttribute`/`SendTo`/`RpcInvokePermission`，要改为 `[ServerRpc(RequireOwnership = false)]`（这两个 RPC 可能由非拥有者/攻击方调用，不能要求 ownership）。其余 `[ClientRpc]`、`RequestInvincibleServerRpc` 的 `[ServerRpc]` 可直接用。`NetworkList` 首次同步不触发 `OnListChanged` 的坑仍然存在，源项目的 `RebuildLocalBuffsFromNet()` 重建逻辑必须保留。

### 13.9 新增风险与修正

| ID | 风险/修正 | 说明 |
|---|---|---|
| R19 | 跨包 prefab 直接复制会 Missing Script | 英雄/联机 prefab 必须重建；UI prefab 的本项目脚本可复制 `.meta` 保 GUID |
| R20 | 层号不一致 | 必须 3=player、6=environment、7=enemy，见 13.3 |
| R21 | 英雄 prefab 自带 `PlayerInput`/`CharacterInputSystem` | 重建时移除，避免输入双开/单例重复 |
| R22 | `DefaultNetworkPrefabs.asset` 是 Unity 6 资产 | 不复制；运行时 `NetworkConfig.Prefabs.Add` 注册英雄 |
| R23 | 源场景 `Global Volume` 后处理 | 不直接复制 profile；P6 重建轻量 Volume |
| R24 | 425 MB 闭包含无关资源 | 按 13.5 裁剪；不要整目录复制 |
| R25 | `Player` prefab 上 `m_Bits:64` 来源不明 | 迁移前用源码/Inspector 确认字段；不要盲目保留 |

### 13.10 对施工计划的修订

- **P0**：层改为 3/6/7；Input System 安装后先在空工程验证生成类，再改 `activeInputHandler=2`。
- **P3**：源大厅 UI 保留类名，只改基类/字体/资源路径；`jiaru` 改 `UnityTransport.SetConnectionData(ip, 7777)`。
- **P5**：联机 prefab 重建（NetworkManager + UnityTransport + RoomState），TickRate 30、EnableSceneManagement 1、端口 7777、场景名 `Battle`；运行时注册英雄 prefab。
- **P6**：重建 `Global Volume`（Bloom/Vignette/Color Adjustments），不要复制 Unity 6 profile。
- **P7**：英雄 prefab 重建（13.4），资源按 13.5 清单复制；不要直接复制 prefab。

### 13.11 RPC 写法兼容（**最高优先级编译风险**）

源项目用了 NGO 2.x 的新 RPC 特性，NGO 1.13 没有：

| 文件:行 | 源写法 | 目标写法 |
|---|---|---|
| `HealthModel.cs:136` | `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)] private void RequestDamageServerRpc(DamageRequest req)` | `[ServerRpc(RequireOwnership = false)] private void RequestDamageServerRpc(DamageRequest req)` |
| `BuffComponent.cs:168` | `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)] private void AddBuffServerRpc(int buffIdHash, ulong sourceId)` | `[ServerRpc(RequireOwnership = false)] private void AddBuffServerRpc(int buffIdHash, ulong sourceId)` |
| `HealthModel.cs:324` | `[ServerRpc] RequestInvincibleServerRpc` | 不变（拥有者调用，默认 `RequireOwnership=true` 可） |
| 其余 | `[ClientRpc]` | 不变 |

注意：
- 这两个 RPC 的调用方**不是**被击者/被挂 Buff 者的拥有者（是攻击方/施法方），所以必须 `RequireOwnership=false`；否则 1.13 会拒绝并打 warning。
- `RpcInvokePermission`、`SendTo`、`RpcAttribute` 在 1.13 中不存在，不能只是删 `InvokePermission`，要整体换成 `[ServerRpc]`。
- 1.13 的 `[ServerRpc]` 方法名必须以 `ServerRpc` 结尾（源方法名已满足）；`[ClientRpc]` 以 `ClientRpc` 结尾（已满足）。

### 13.12 `HealthModel` / `BuffComponent` 完整运行时链路（实测）

**HealthModel（324 行）**
1. `Awake`：若未被 `InitStats` 推过，`ResetHealth(100,100)` 兜底。
2. `InitStats(maxHP, currentHP=-1)`：角色 `Player.Awake` 用 `PlayerSO.statsData.baseMaxHP` 调用；`currentHP<0` 表示满血。
3. `OnNetworkSpawn`：订阅 `netCurrentHP`/`netMaxHP`；服务端 `SyncHPToNetwork()` 初始推送。
4. `RequestDamage(req, source)`：单机本地 `CalculateIncoming` → `TakeDamage`；联网发 `RequestDamageServerRpc`。
5. 服务端 `RequestDamageServerRpc`：`GetSourceObject(sourceId)` → `CalculateIncoming` → `TakeDamage`。
6. `TakeDamage`：
   - `!IsAlive || _isDead` 直接返回；`IsSpawned && !IsServer` 直接返回（客户端不结算）。
   - 无敌且非 DoT：单机派发 `E_DamageBlocked`；服务器 `GrantDodgeInvincible()` + `NotifyDamageClientRpc(..., isBlocked:true)`。
   - 扣血 → `SyncHPToNetwork()` → 死亡锁 `_isDead`。
   - 单机派发 `E_OnDamage`/`E_OnDeath`；联网 `NotifyDamageClientRpc` 全员。
7. `NotifyDamageClientRpc`：按 `isBlocked` 派发 `E_DamageBlocked` 或 `E_OnDamage`；`isDead` 再派发 `E_OnDeath`。订阅方按 `data.target` 过滤。
8. `GrantDodgeInvincible`：用 `PlayerSO.movementData.dodgeData.animationClip.length` 作为无敌时长；clips 为空则 0.5s。
9. `Heal`/`SetMaxHP`/`ResetHealth`：都只在服务端（或单机）执行并 `SyncHPToNetwork`。
10. `RequestInvincible(window, cooldown)`：单机直接 `SetInvincible`；联网 `RequestInvincibleServerRpc`（Clamp window 0.05–1、cooldown 0–10，ServerTime 冷却）。
11. `SetInvincible`：`TimerManager.GetOneTimer(seconds, ...)`；重开前先 `UnregisterTimer` 旧计时器。

**BuffComponent（340 行）**
1. `OnNetworkSpawn`：订阅 `_netBuffs.OnListChanged`；服务端 `SyncNetBuffs()`；客户端 `RebuildLocalBuffsFromNet()`（首次同步不走 OnListChanged）。
2. `Update`：
   - 客户端镜像：`TickClientMirror()` 只递减 `remainTime` 供 UI，不结算、不派发。
   - 服务端/单机：死亡清空；倒序遍历；`duration>0` 才倒计时；`tickInterval>0` 累计 tick；到期 `OnExpire` + 移除 + `NotifyChange`；循环外 `netDirty` 统一 `SyncNetBuffs()`。
3. `AddBuff`：同 `buffIdHash` 叠加 → `stacks=min(stacks+n,maxStack)`、`duration>0` 时**刷新为完整时长**、`OnStackChanged`；新建 → `OnApply`；每次变更 `SyncNetBuffs()` + `NotifyChange`。
4. `RequestAddBuff`：单机直接 `AddBuff`；联网 `AddBuffServerRpc(buffIdHash, sourceId)`；服务端 `BuffDatabase.Resolve` 查不到就丢弃（防伪造）。
5. `RemoveBuff`/`RemoveAllBuffs`：调用策略 `OnRemove`，`SyncNetBuffs`，`NotifyChange`。
6. `SyncNetBuffs`：反向删除 `_netBuffs` 中已不存在的，正向 Add/Set（`Set` 有相等门禁）。
7. `RebuildLocalBuffsFromNet`：清空本地镜像 → 用 `BuffNetState` 重建 → `endServerTime>0` 时 `remainTime = endServerTime - ServerTime`，否则 `float.MaxValue`；统计 `controlFlags` → `CharacterState.SetControlStates(stun,silence,invincible)`；**绝不调用效果钩子**（属性 Modifier 只在服务端生效）。
8. `ToNetState`：永久 Buff 的 `endServerTime=0` 哨兵；`controlFlags` 来自 `effect.controlFlags`。
9. 关键坑：`NetworkList` 首次同步不触发 `OnListChanged`；Host 上写 `_netBuffs` 也会触发 `OnListChanged`，必须 `if (IsServer) return;` 防重入。

### 13.13 层 6 的双重语义（**必须保留**）

源 `Assets/script/Framework/Other/Layer.cs`：

```csharp
public const int Default = 0;
public const int TransparentFX = 1;
public const int IgnoreRaycast = 2;
public const int Water = 4;
public const int UI = 5;
public const int UIRenderToTarget = 6;   // ← 与 TagManager 的 environment 同为 6
```

源 TagManager 层 6 = `environment`（地面/场景），同时 `UIModelManager` 把选人预览模型设到层 6，预览相机/预览灯只处理层 6。也就是说 **层 6 同时承担“地面”和“UI 模型预览”两个语义**。

实施要求：
- 目标 P0 保持 6=`environment`，**不要**新增/改名为 `UIRenderToTarget`，否则 `Layer.UIRenderToTarget` 常量与所有引用都要改。
- 地面 Plane / 地形必须在层 6，否则 `CharacterMoveControllerBase.whatIsGround`（`m_Bits:64`）检测不到地面，角色会一直下落。
- 若将来一定要独立预览层：改 `Layer.UIRenderToTarget` 常量 + `UIModelManager` 相机 cullingMask + 预览灯 cullingMask + 模型 `SetLayerRecursively` 四处，并同步 TagManager；不建议本期做。

### 13.14 相机 / VCam 序列化参数（实测）

**`Camera.prefab`**
- Camera：FOV 45、near 0.01、far 1000、ClearFlags Skybox(1)、cullingMask 全开、HDR 开、MSAA 开、depth 0。
- URP AdditionalCameraData：`m_RenderPostProcessing=0`、`m_Antialiasing=0`、`m_RenderShadows=1`、`m_RequiresDepthTextureOption=2`、VolumeLayerMask 默认。
- CinemachineBrain：`m_UpdateMethod=2`、`m_BlendUpdateMethod=1`、`m_DefaultBlend` style=1/time=2。

**`thirdcamera.prefab`**
- `CinemachineVirtualCamera`：Priority 10、Lens FOV 45/near 0.01/far 1000、`m_StandbyUpdate=2`。
- `CinemachineFramingTransposer`：`m_TrackedObjectOffset=(0,1.2,0)`、`m_CameraDistance=2.8`、`m_SoftZoneWidth/Height=0.85`、`m_CenterOnActivate=1`。
- `CinemachinePOV`：Vertical `Value=8`、Horizontal `Value=0`。
- `CinemachineInputProvider`：`XYAxis`/`ZAxis` 引用 `playerinputaction.inputactions` 的子资产（look/zoom）。
- `CinemachineCollider`：`m_Damping=0.3`、`m_DampingWhenOccluded=0`。
- `Camera_ZoomController`：`defaultDistance`/`lookMinDistance`/`lookMaxDistance`/`zoomSensitivity`/`zoomSpeed`/`currentDistance`。

迁移建议：
- 重建 vcam 时用 `CinemachinePOV` + `CinemachineFramingTransposer` + `CinemachineCollider` + `CinemachineInputProvider`，参数照抄；`XYAxis` 指 `look`，`ZAxis` 指 `zoom`。
- 若目标 Cinemachine 2.10.7 的 `CinemachineInputProvider` 与 Input System 版本不匹配，写一个 20 行的 `BattleCameraInputFeeder` 直接读 `CharacterInputSystem.Look/Zoom` 并写 `CinemachinePOV.m_HorizontalAxis.Value`/`m_VerticalAxis.Value`、`FramingTransposer.m_CameraDistance`，避免依赖 Provider 的序列化 Action 引用。
- 相机 `Camera.main` 在多相机场景不可靠；用 `BattleCameraRig` 显式数组。

### 13.15 角色材质 / Shader / FBX 导入类型（**关键**）

**材质**：安比/艾莲共 9 个 `.mat`，全部使用自定义 Shader `URPGenshinToon`：
- `Anbi_Body_D` / `Anbi_Face_D` / `Anbi_Hair_D` / `Anbi_Weapon_D`
- `Ellen_Body1_Toon` / `Ellen_Body2_Toon` / `Ellen_Face_Toon` / `Ellen_Hair_Toon` / `Ellen_Weapon_Toon`

**Shader 文件**：`Assets/plugins/Shader/二次元/UnityGenshinToonShader-main/`
- `Shaders/URPGenshinToon.shader`
- `Shaders/ToonInput.hlsl`、`ToonForwardPass.hlsl`、`ToonOutlinePass.hlsl`
- `Shaders/ShadowRamp_Default.png`
- `Scripts/MaterialUpdater.cs`（编辑器材质更新工具，可选）

**URP 14.2 兼容性评估**：源码只用了标准 URP API（`Core.hlsl`/`Lighting.hlsl`/`DeclareDepthTexture.hlsl`、`GetWorldSpaceNormalizeViewDir`、`LitInput.hlsl`、`ShadowCasterPass.hlsl`、`DepthOnlyPass.hlsl`、`LitDepthNormalsPass.hlsl`）。已确认目标 URP 14.2.0-t1 的 `ShaderLibrary/ShaderVariablesFunctions.hlsl` 有 `GetWorldSpaceNormalizeViewDir`，且 `LitDepthNormalsPass.hlsl` 存在。**结论：大概率可编译**；迁移后必须用 `unity_shader.compile` 实测（0 error、变体正常），并录屏确认颜色/描边/面部阴影正确。
- 若编译失败：优先按错误改 include/API；仍不行则退回目标 `YSA Toon/Toon Lit`，重配材质贴图与描边（视觉会有差异，需用户确认）。
- ShadowCaster/DepthOnly/DepthNormals 三个 Pass 复用 `LitInput.hlsl`，材质必须保留 `_BaseMap`/`_BaseColor`/`_Cull` 等属性；源材质已满足。

**FBX 导入类型（实测）**：

| 资源 | animationType | avatarSetup | globalScale | rootMotionBone | 含义 |
|---|---|---|---|---|---|
| `安比.fbx`（模型） | 2 (Generic) | 1 (Create From This Model) | 100 | Bip001 | 安比是 Generic 骨架 |
| `安比攻击.fbx` / `安比移动.fbx` | 2 (Generic) | 2 (Copy From Other Avatar) | 100 | Bip001 | 动作是 Generic，复制安比模型 Avatar |
| `Ellen_Ani_Idle.fbx` 等 | 3 (Humanoid) | 1 (Create From This Model) | 1 | 空 | 艾莲是 Humanoid |
| 艾莲攻击/受击/闪避/死亡 FBX | 3 (Humanoid) | 1 | 1 | 空 | Humanoid 动作 |

实施要点：
- **安比动作不能重定向到艾莲**（Generic 绑骨骼）；艾莲 Humanoid 动作理论上可重定向，但本期直接使用原角色。
- 复制安比攻击/移动 FBX 后，`avatarSetup=2` 的“Copy From Other Avatar”引用可能断；打开 FBX 导入设置，把 Avatar 指到安比模型（或改为 Create From This Model），确认 clip 能播放。
- 艾莲 prefab `Animator.ApplyRootMotion=0`，而 `CharacterMoveControllerBase` 用 `Animator.deltaPosition` 做位移。**必须实测艾莲是否能移动**：若 deltaPosition 恒 0，把 ApplyRootMotion 改 1，或在移动状态改用“速度×deltaTime”的 in-place 方案（会与安比手感不同，需用户确认）。
- `CharacterController` 尺寸/参数（两个英雄一致）：Height 1.43、Radius 0.3、Slope 45、Step 0.3、Skin 0.08、MinMove 0.001、Center (0,0.8,0)。
- `Player` 序列化参数：`characterGravity=-9`、`maxVerticalSpeed=20`、`minVerticalSpeed=-30`、`fallOutTimer=0.2`、`GroundDetectionRadius=0.05`、`GroundDetectionOffset=0.05`、`whatIsGround=layer6(environment)`、`SlopDetectionLenth=1`、`moveMult=1`。
- `ActorAudioComponent.characterName` 在源 prefab 上是 `9`（`Null`，通用音效）；若要角色专属语音，改为 `AnBi=3`，并确认 `SoundData` 有对应条目。

### 13.16 目标面板字段清单（迁移工具输入）

以下字段均来自目标现有控制器（`public`/`[SerializeField]`），抽预制体时应自动生成 `UIControlData.ctrlItemDatas` 并补 `[ControlBinding]`。

| 面板 | 控制器 | 字段（按名字绑定） |
|---|---|---|
| 好友/聊天 | `FriendUIController` | entryButton、panelRoot、closeButton、listPage、addButton、newFriendEntryButton、friendListContent、friendItemTemplate、statusText、requestsPage、requestsBackButton、requestListContent、requestItemTemplate、addPage、addBackButton、addFriendInput、addSearchButton、searchResultView、resultNameText、resultIdText、resultApplyButton、chatTitle、messageListContent、messageLeftTemplate、messageRightTemplate、chatInput、sendButton、emojiButton、emojiButtonImage、emojiPanel、emojiScroll、emojiContent、emojiEmptyHint、friendListScroll、messageScroll |
| 背包 | `BagUIController` | entryButton、panelRoot、closeButton、titleText、capacityText、backgroundImage、slotScroll、listContent、rowTemplate、previewIcon、detailNameText、detailMetaText、detailDescText、useButton、discardButton、grantButton、tidyButton、statusText、loadingIndicator、dragLayer + 调参字段（columns/cellSize/颜色/backgroundTileSize/wheelRowsPerNotch/dragAutoScrollRowsPerSecond） |
| 任务 | `QuestUIController` | entryButton、panelRoot、closeButton、panelsToCloseOnOpen、titleText、questScroll、listContent、rowTemplate、detailTitle、detailDesc、detailGoal、stepsText、detailReward、acceptButton、submitButton、abandonButton、gmButton、statusText + 行高/颜色 |
| 邮件 | `MailUIController` | entryButton、panelRoot、closeButton、panelsToCloseOnOpen、titleText、mailScroll、listContent、rowTemplate、detailTitle、detailMeta、detailBody、attachText、claimButton、deleteButton、testMailButton、statusText + 行高/颜色 |
| 排行榜/挑战 | `ChallengeUIController` | challengePanelRoot、entryButton、closeButton、startButton、startLabel、clickZone、clickLabel、countdownText、statusText、leaderboardPanelRoot、leaderboardEntryButton、leaderboardCloseButton、refreshButton、leaderboardScroll、listContent、rowTemplate、myRankText、leaderboardStatusText |
| 副本 HUD | `DungeonHud` | 程序化构建，无序列化面板引用；`Root`/`Rect`/`Group`/`Label` 是浮层条目结构 |
| 阿福 | `AgentUIController` | entryButton、panelRoot、closeButton、panelCanvas、panelRect、titleText、signatureText、avatarImage、avatarGlyph、messageScroll、messageListContent、bubbleLeftTemplate、bubbleRightTemplate、chatInput、sendButton、statusText、thinkingIndicator、thinkingDotsRoot、chatAreaRect、statusRowRect、thinkingRect + 输入框高度参数 |

迁移工具注意：
- `panelsToCloseOnOpen` 是 `GameObject[]`，抽预制体后跨面板引用会断；改为 `UIType[]` 或 `UIManager.CloseAll`。
- `panelRoot`/`entryButton` 在面板预制体里通常不再需要（入口在 `HudEntryBar`），可保留但置空。
- `FriendUIController.Icons/Keys` 是运行时表情数据，不是场景引用，保留代码即可。

### 13.17 目标主场景根画布（实测）

`zhuye.scene` 根对象与画布：

| 根对象 | 状态 | Canvas |
|---|---|---|
| `renwu` | **INACTIVE** | ScreenSpaceCamera(1), sort 10 |
| `beibao` | **INACTIVE** | ScreenSpaceCamera(1), sort 10 |
| `youjian` | **INACTIVE** | ScreenSpaceCamera(1), sort 10 |
| `FriendCanvas` | ACTIVE | ScreenSpaceCamera(1), sort 0 |
| `fuben` | ACTIVE | ScreenSpaceCamera(1), sort 10 |
| `huodong` | ACTIVE | ScreenSpaceCamera(1), sort 10 |
| `agent` | ACTIVE | **ScreenSpaceOverlay(0)**, sort 10 |
| `shijie` | ACTIVE | **ScreenSpaceOverlay(0)**, sort 10 |
| `EventSystem` / `UICamera` / `Main Camera` / `Directional Light` / `RedDotManager` / `MmQuestManager` | ACTIVE | — |

实施要点：
- 迁移时保留“初始 active=false”的语义：`renwu/beibao/youjian` 仍应默认关闭，由入口打开。
- `agent`/`shijie` 是 Overlay，`UICameraService.SweepAll` 会在运行时转 Camera；迁入框架后统一由 `UIManager` 层管理，不再依赖 Sweep。
- `FriendCanvas` 内还有嵌套 Canvas（`FriendPanel/ListPage/RequestsPage/AddPage`），抽预制体时保留；`UIView.SortOrder` 会重排 sortingOrder，迁移后要检查三页互斥时的遮挡。
- `huodong` 是 Lua 活动面板宿主，迁移到 `LuaOnlyView` 后，旧 `ActivityShell` 的画布创建逻辑要拆除，避免双画布。

### 13.18 新增风险登记（承接 R19–R25）

| ID | 风险 | 触发信号 | 对策 |
|---|---|---|---|
| R26 | NGO 2.x RPC 写法在 1.13 编译失败 | `RpcAttribute`/`SendTo`/`RpcInvokePermission` 找不到 | 按 13.11 改 `[ServerRpc(RequireOwnership=false)]` |
| R27 | 层 6 双重语义被破坏 | 角色下落/预览模型不可见 | 保持 6=environment；`Layer.UIRenderToTarget=6` 不改 |
| R28 | 选人预览光照不对 | 预览模型全黑/阴阳脸 | 预览相机只渲染层 6；toon shader 用 `GetMainLight()`，需确认场景主光方向或临时改预览光为主光；必要时预览用 unlit 材质 |
| R29 | 安比 Generic 动作 Avatar 引用断 | clip 不播放/模型 T-pose | 重新指定 Avatar Definition；确认 animationType=Generic |
| R30 | 艾莲 ApplyRootMotion=0 | 艾莲不能移动 | 实测；必要时改 1 或改 in-place 移动 |
| R31 | 源 prefab 自带 PlayerInput/CharacterInputSystem | 输入双开/单例重复 | 重建 prefab 时移除（13.4） |
| R32 | Camera InputProvider 的 Action 引用断 | 鼠标不能转视角/滚轮无效 | 重新指 look/zoom，或写 `BattleCameraInputFeeder` |
| R33 | 源 prefab `characterName=Null` | 角色语音不区分角色 | 需要时改 `AnBi=3` 并补 SoundData |
| R34 | 目标 agent/shijie 是 Overlay | 迁移后层级/相机不一致 | 统一 `UIManager.AdoptCanvas` 转 Camera space |

### 13.19 对施工计划的再次修订

- **P0**：层 3/6/7；层 6 同时是预览层；地面必须层 6。
- **P4**：先改 RPC 写法再编译；`CharacterCombo` 对 `AIStateMachine` 的依赖要解耦（见 13.20）。
- **P6**：相机按 13.14 重建；预览光照/主光问题按 R28 处理；顿帧走 `PauseManager`。
- **P7**：Shader 先编译验证；FBX 按 13.15 设置；英雄 prefab 重建；艾莲移动实测。
- **P8**：按 13.16 字段清单生成绑定；按 13.17 保留初始 active 状态；先迁 `agent/shijie` 之外的四个批次。

### 13.20 `CharacterCombo` 对 AI 的耦合

`CharacterCombo.UpdateAttackLookAtEnemy` 中有：

```csharp
var ai = col.GetComponentInParent<AIStateMachine>();
if (ai != null && ai.target == playerTransform)
    score += comboData.autoAimAttackingMeBonus;
```

若本期不迁 `AIstate/*`（单机木桩无 AI），这段会编译失败。处理方案（按优先级）：
1. 定义一个最小接口 `IAttackTargetProvider { Transform CurrentTarget { get; } }`，AI 和木桩都实现；`CharacterCombo` 只依赖接口。
2. 或直接删除“正在攻击我”加分（软锁定仍可用，少一个权重）。
3. 或把 `AIStateMachine` 整个迁过来（牵连 Behavior Designer/RootMotion，成本高）。

建议 **方案 1**：新增 10 行接口，保留评分语义，且不把 AI 框架拖进本期。

### 13.21 移动状态机逐状态行为（P4 直接照此实现）

`PlayerMovementStateMachine` 构造时创建 8 个状态对象，`OnStateSwitched` 里 `SyncMoveState(StateType)`。`CancelPendingTimers()` 目前只取消 Idle 的轻点计时器。

| 状态 | StateType | Enter | Update | Exit | 备注 |
|---|---|---|---|---|---|
| `PlayerIdlingState` | Idle | `ApplyStateData(idleData)`；订阅 `move.started` | `PollInput()` | 退订 + `CancelPendingTimer()` | 轻点阈值 0.13s；0.13s 后还在按 → Walk，否则 WalkStop；`player==null` 三层兜底 |
| `PlayerWalkingState` | Walking | `ApplyStateData(walkData)`；`ActorAudio.PlayLoopSound(FOOT)` | `PollInput()`；Shift → Dashing；无输入 → WalkStop | `ActorAudio.StopLoopSound()` | 脚步循环 |
| `PlayerWalkStopState` | WalkStop | 无 `walkStopClip` 直接回 Idle；否则播放并挂 `OnEnd` | `PollInput()`；有输入 → Walking | 清 `OnEnd` | 收尾动画期间可重新移动 |
| `PlayerDashingState` | Dashing | `ApplyStateData(dashData)`；`PlayByStyle(DodgeSound)`；挂 `OnEnd` | 不轮询输入（锁定位移） | 清 `OnEnd` | `OnEnd` 回 `walkingState`（即使没输入；源项目如此，P4 可改为按 `IsMoving` 回 Idle/Walk） |
| `PlayerDodgeState` | Dodge | 不调 `base.Enter()`（不订阅输入）；`canInput=false`；播放 `dodgeData.animationClip`；`OnEnd=OnDodgeEnd`；单机 `SetInvincible(clip.length)` | 空 | 清 `OnEnd` | 联网无敌由服务器在拦下伤害时授予；`OnDodgeEnd` 先回连击 Null，再回 Idle/Walk |
| `PlayerHurtState` | Hurt | 不调 `base.Enter()`；`canInput=false`；`PlayByStyle(HIT)`；播放 `hurtData.animationClip`；`OnEnd=OnHurtEnd` | 空 | 清 `OnEnd` | `OnHurtEnd` 先回连击 Null，再强制 Idle |
| `PlayerDeadState` | Dead | 不调 `base.Enter()`；`canInput=false`；`rotationTime=0`；播放 `deadData.animationClip`；`OnEnd=OnDeathAnimationEnd` | 空 | 空 | 无死亡动画直接销毁；`DestroyPlayer()` 见 R35 |
| `PlayerMovementNullState` | Null | `base.Enter()`（仍订阅相机回正/输入事件） | 空 | `base.Exit()` | 攻击/技能期间锁移动的占位状态；TODO 停止移动/恢复移动未实现（由根运动与 `PlayerATKIngState` 控制） |

### 13.22 连击状态机逐状态行为

`PlayerComboStateMachine` 构造时创建 `CharacterCombo`（传入 `PlayerSO.comboData.comboData`）并 `Init()`；`OnStateSwitched` 里 `SyncComboState(StateType)`。

| 状态 | StateType | Enter | Update | 关键行为 |
|---|---|---|---|---|
| `PlayerNullState` | Null | `base.Enter()` 订阅 attack/defense/skill；`ReSetComboInfo()`；`SetATKIndex(0)`；`ClearAnimationEnd()`；非死亡时恢复移动（Idle/Walk） | `canInput && ConsumeATKInput()` → 行走中 `ForwardCombo()`，否则 `LightComboInput()`；移动状态机切 Null；连击切 ATKIng | 输入缓冲在 Update 统一消费；收尾阶段缓冲的按键在此接住 |
| `PlayerATKIngState` | Attacking | `base.Enter()`；远程端 `currentCombo ??= LightCombo`；`UpdateAttackLookAtEnemy()`（单机）；`PlayAttackClip()` | `TickAutoAimRotation()`；收尾阶段按移动 → 恢复移动回 Null；收尾阶段 Shift+移动 → 先回 Null 再切 Dashing | 检查点 `OnLinkCheckpoint` 消费缓冲；`AdvanceToNextSegment` 检查 `canLink`/末段；末段/断连 → `EnterRecovery`；`EnterRecovery` 无收尾动画直接回 Null |
| `PlayerSkillState` | Skill | `base.Enter()`；取 `currentSkill`；远程端回退配置技能；无技能/无动画 → 恢复移动回 Null；否则 `Play(skill.attackClip)` + `OnEnd` | 无 | 只播动画，无打击帧/伤害（源技能未接伤害） |

`PlayerComboState.PlayAttackClip()` 关键顺序：
1. `player.characterAnimancer.Play(transition)`。
2. `player.ApplyRemotePhaseOffset(state)`（远程端相位）。
3. `state.Events.Clear()`（防重复注册）。
4. 远程端：只挂 `OnEnd`，不注册检查点/打击帧、不播音效。
5. 拥有者：挂 `OnEnd`；播语音/武器音；`linkTime>0` → `ClearATKInputBuffer()` + `canInput=true` + `Events.Add(linkTime, OnLinkCheckpoint)`；`hitFrameTime>0` → `Events.Add(hitFrameTime, () => characterCombo.ATK())`。
6. `PlayAttackEndClip()` 只挂 `OnEnd=OnRecoveryEnd`；`ClearAnimationEnd()` 清当前 state 的 `OnEnd`。

### 13.23 `ComboContainerData` / `ComboData` API 与默认值

`ComboContainerData : ScriptableObject`：
- `comboDates`（下标=段数）、`ForwardATKData`、`firstComboData`（`Init` 缓存 `comboDates[0]`）。
- `CreateRuntimeClone()`：`CreateInstance` + 深拷贝列表（元素引用共享）+ `Init()`；**必须在 `CharacterCombo.Init` 里调用**，否则 `SwitchForwardATK` 会污染共享 SO。
- `SwitchForwardATK()`：`comboDates[0] = ForwardATKData`（会改列表）。
- `ResetComboDates()`：`comboDates[0] != firstComboData` 时还原。
- 读取接口：`GetComboData/GetComboMaxCount/GetComboName/GetComboColdTime/GetComboDamageMultiplier/GetComboDistance/GetComboOffset/GetComboHitFrameTime/GetPauseFrameTime/GetComboShakeForce/GetComboSoundStyle/GetAttackClip/GetAttackEndClip/GetLinkCancelTime`。

`ComboData : ScriptableObject` 默认值：
- `damageMultiplier=0.08`、`critRate=0.2`、`critMultiplier=2`、`attackDistance=1`、`comboOffset=1`、`hitFrameTime=0.35`、`linkCancelTime=0.6`、`pauseFrameTime=0`、`shakeForce=0`、`comboColdTime=0`、`enemyLayer=0`（回退 player+enemy）、`hitBuff=null`、`attackClip/attackEndClip` 可空。
- `attackStyle`（Attack/Skill/FinishSkill/SwitchSkill）、`characterName`、`comboName`、`weaponSound[]`、`characterVoice[]`、`universalSound`。

### 13.24 事件总线 / 属性账本 / Buff 策略 API（移植清单）

**EventCenter（437 行，静态单例）**
- `EventCenter.MainInstance`；`AddListener(evt, target, callback)` / `AddListener<T>(evt, target, Action<T>)`；`Dispatch(evt)` / `Dispatch<T>(evt, data)`；`UnregisterTarget(target)`；`Remove(evt, target, callback)`。
- 事件枚举：`E_Attack`(HitData)、`E_OnHit`(HitData)、`E_OnDamage`(DamageData)、`E_DamageBlocked`(DamageData)、`E_OnDeath`(DeathData)、`E_OnKill`、`E_OnStateChanged`(StateChangeData)、`E_SkillCast`(SkillData)、`E_SkillCooldownStart/End`、`E_ComboChanged`(int)、`E_BuffAdd/Remove`(BuffChangeData)、`E_Input*`、`E_RoundStart/End`、`E_MatchEnd`、`E_SceneLoadProgress`(float)。
- 数据结构：`HitData{attacker,target,hitPoint}`、`DamageData{source,target,amount,isCritical,isDoT}`、`DeathData{target}`、`StateChangeData{fromState,toState}`、`SkillData{skillId,cooldown}`、`BuffChangeData{target,buffData,stacks,remainTime}`。

**AttrType（顺序=资产序列化，新增只能末尾追加）**
- 系数型：`DamageUp`、`DamageDown`、`MoveSpeed`（0=无加成）。
- 白值型：`Attack`、`Defense`。
- `AttrTypeUtil.IsRatio(type)`。
- `MutableAttribute`：`BaseValue`（setter 打脏）、`FinalValue`（脏才重算）、`AddModifier`、`RemoveAllFromSource(object)`、`ClearAllModifiers`；白值 `(Base+ΣAdd)×ΠMul`，系数 `(1+ΣAdd)×ΠMul-1`。
- `AttributeComponent : NetworkBehaviour`：`Get/GetValue/GetOwnValue/SetBase/ClearAllModifiers`；`netOwnAttack/netOwnDamageUp` 只同步给 Owner；`Update` 每帧比对发布。
- `AttributeModifier{type,value,source}`；`ModType.Add/Multiply`。

**Buff**
- `BuffData : ScriptableObject`：`buffId/buffIdHash/buffName/effect/duration/maxStack`；`buffIdHash` 空 id 回退资产名；`OnValidate` 警告空 id。
- `BuffEffect : ScriptableObject`：`tickInterval`、`controlFlags`、`OnApply/OnStackChanged/OnTick/OnRemove/OnExpire`；**禁止运行时字段**。
- `BuffAttributeEffect`（AttrType/ModType/每层值）、`BuffDotEffect`（每 tick 伤害/间隔，`isDoT=true`）、`BuffHoTEffect`（每 tick 回血/间隔）、`BuffStunEffect/BuffSilenceEffect/BuffInvincibleEffect`（CharacterState 引用计数）。
- `BuffInstance`：`data/owner/source/stacks/remainTime/tickTimer`。
- `BuffNetState : unmanaged, IEquatable<BuffNetState>, INetworkSerializeByMemcpy`：`buffIdHash/stacks/endServerTime/controlFlags`；NGO 1.13 的 `NetworkList<T> where T: unmanaged, IEquatable<T>` 可接受（已核对 1.13.1 源码），`INetworkSerializeByMemcpy` 也被 1.13 ILPP 支持。
- `BuffControlFlags`：None/Stun/Silence/Invincible。
- `BuffDatabase`：`Resolve(buffIdHash)` 静态查表；两端必须一致（缺表会导致客户端镜像跳过）。
- `CharacterState`：`IsStun/IsSilence/IsInvincible`、`Add/RemoveStun/Silence/Invincible`、`SetControlStates`、`ClearAll`。

### 13.25 移植时必须修的源项目缺陷/怪癖

| ID | 位置 | 现象 | 处理 |
|---|---|---|---|
| R35 | `PlayerDeadState.DestroyPlayer` | 拥有者客户端调用 `NetworkObject.Despawn()`；NGO 客户端无权 Despawn 服务端对象，会失败/打警告 | 改为 `if (IsServer) NetworkObject.Despawn(); else gameObject.SetActive(false);`；或死亡由服务端统一 `Despawn` |
| R36 | `PlayerMovementState.ApplyStateData` | 注释说设置播放速度，实际只 `Play(clip, fade)`，`PlayerStateData.playSpeed` 完全未使用 | P4 补 `characterAnimancer.States.Current.Speed = data.playSpeed`；注意会改变手感，先在文档标注并问用户 |
| R37 | `CharacterCombo.AttackDetection` | `_detectBuffer` 只有 16，超过会静默漏检；只有 auto-aim buffer 有上限警告 | 加大 buffer 或加 `count == buffer.Length` 警告 |
| R38 | `CharacterCombo._hitTargets` | 每次打击帧 `Clear`，是“段级”去重；同一段被重播会再次结算 | 源文档已标为已知不精确；P4 保持，或引入 `slashId` |
| R39 | 英雄 prefab 根 | 自带 `PlayerInput` + `CharacterInputSystem` | 重建 prefab 时移除（13.4） |
| R40 | `PlayerCameraUtility.Init` | `vcam == null` 时 `FindAnyObjectByType<CinemachineVirtualCamera>()` 兜底，可能抓到别的座位 | 改为 `BattleCameraRig` 显式传入；不要依赖兜底 |
| R41 | `PlayerDashingState.OnDashAnimationEnd` | 无条件回 `walkingState`，没有输入时可能“原地走” | P4 改为 `IsMoving ? walkingState : idlingState` |
| R42 | `ComboContainerData.CreateRuntimeClone` | `CreateInstance` 的 clone 没有销毁 | 角色销毁时 `Destroy(clone)`；或接受一次性小泄漏 |
| R43 | `PlayerMovementNullState` | Enter/Exit 的“停止/恢复移动”是 TODO，仅靠根运动与攻击状态锁 | P4 保持现状，确认攻击期间方向输入不产生位移 |

### 13.26 P4 单机验收清单（逐状态）

1. Idle：静止播放 idle；轻点方向 → WalkStop；按住 → Walk。
2. Walk：WASD 相机相对移动；Shift → Dashing；松手 → WalkStop。
3. WalkStop：播放 walkStopClip；重新按方向 → Walk；播完 → Idle。
4. Dashing：播放 dash 动画；播完回 Walk/Idle（按 R41 修正）。
5. Dodge：右键开窗（无动画）；窗口内被打 → 闪避动画 + 全程无敌；播完回 Idle/Walk；冷却内右键无效。
6. Hurt：被打 → 硬直动画；播完回 Idle；DoT 不触发硬直；死亡时不进 Hurt。
7. Dead：血量归零 → 死亡动画 → Despawn/隐藏；死亡后不再受击/闪避/攻击。
8. ATKIng：左键 3 段；每段 0.35 打击帧、0.6 检查点；窗口内按键接段；末段/断连进收尾；收尾按攻击接第一段；收尾按移动打断。
9. Skill：E 键播技能动画；无技能配置时无反应；播完回 Null。
10. 手动验证：`PlayerSO` 的 idle/walk/walkStop/dash/dodge/hurt/dead 动画都能播；`playSpeed` 修正后各状态速度符合配置。

### 13.27 P5 联机验收矩阵

| 场景 | 期望 |
|---|---|
| Host 生成两个英雄 | 服务端 `SpawnWithOwnership`；客户端各看到两个 `Player`，`OwnerClientId` 正确 |
| 座位/相机 | A `NetSeatId=0` 用 Camera/thirdcamera；B=1 用 Camera (1)/thirdcamera (1)；`-1` 时重试 |
| 移动同步 | 拥有者移动 → `netMoveState` + `netStateStartTime`；远程端回放动画 |
| 连击同步 | `netComboState/netComboIndex/netForwardATK`；远程端不注册打击帧、不重复扣血 |
| 伤害链路 | 攻方 `RequestDamageServerRpc` → 服务端 `CalculateIncoming` → `netCurrentHP` + `NotifyDamageClientRpc`；被击者硬直/飘字/血条 |
| Buff | 服务端 `AddBuffServerRpc` → `NetworkList` → 客户端 `RebuildLocalBuffsFromNet`；DoT/属性只在服务端 |
| 无敌/闪避 | 右键 `RequestInvincibleServerRpc` → 服务端开窗；被打中 `isBlocked` → 闪避 + 全程无敌 |
| 死亡 | 服务端死亡锁只发一次 `E_OnDeath`；客户端不重复死亡；Despawn/隐藏按 R35 |
| 断线 | 一方退出 → 另一方收到 `OnClientDisconnectCallback`，关闭战斗 UI，不崩 |
| 再战 | Shutdown → 回 zhuye → 重新创建 Networking → 流程重跑 |

### 13.28 UI 框架内部细节（SkierFramework 逐 API）

#### 13.28.1 `UILayer`（注意：实际是 8 层，不是 README 写的 7 层）

```csharp
public enum UILayer {
    SceneLayer = 1000,
    BackgroundLayer = 2000,
    NormalLayer = 3000,
    DamageLayer = 3500,     // README 漏写，源码存在
    InfoLayer = 4000,
    TopLayer = 5000,
    TipLayer = 6000,
    BlackMaskLayer = 7000,
}
```

- `UILayerLogic`：`PopOrder` 每次 `maxOrder += 30`，`openedViews` 维护打开栈；`OpenUI` 会把下层非 window 界面 `OnPause()` 并 `AddTopViewNum(1)`；`CloseUI` 恢复最上层 `OnResume()` 并 `AddTopViewNum(-1)`。
- `isWindow=true`：打开时不遮挡/不隐藏下层（适合弹窗、确认框、房间/加入房）；`isWindow=false`：打开时下层被 `SetVisible(false)` 隐藏（适合全屏面板：背包/任务/邮件/排行榜）。
- `topViewNum` 是“我上面覆盖了几个非 window 界面”，>0 时 `SetVisible(false)`；关闭时 `AddTopViewNum(-100000)` 归零。
- 目标面板迁移时：全屏面板 `isWindow=false`，弹窗 `true`；`HudEntryBar` 放 `TopLayer` 且 `AddResidentUI`。

#### 13.28.2 `UIManager` 关键 API（迁移时直接用）

| API | 说明 |
|---|---|
| `Initialize()` | 建 `UIRoot`/`UICamera`/8 层 Canvas/黑幕；只执行一次 |
| `InitUIConfig()` | 读 `UIConfig.json` 构建 `_viewControllers` |
| `Open(uiId/UIType, userData, ct)` | 异步；未初始化会先等初始化；未配置会报错 |
| `Close(uiId/UIType, isJump, ct)` | 异步；播关闭动画后隐藏 |
| `JumpUI(cur, curData, next, nextData)` | 打开 next 后关 cur；关 next 时自动还原 cur |
| `CloseAll(ignoreId, closeResidentView)` / `CloseAllAsync` | 批量关；忽略常驻 |
| `ReleaseAll()` / `ReleaseAllAsync` | 回收实例 + 卸载资源 |
| `CloseAllInLayer(UILayer)` / `ReleaseAllInLayer` | 按层批量 |
| `Preload(uiId/UIType)` / `PreloadAll()` | 预加载 |
| `IsOpen(uiId/UIType)` | 是否打开 |
| `GetView<T>(uiId/UIType)` | 取已加载实例（建议不直接用，走事件） |
| `GetOpenedView(uiId/UIType)` | 取已打开实例 |
| `AddResidentUI(uiId/UIType)` | 常驻（CloseAll/ReleaseAll 跳过） |
| `FadeIn/FadeOut/FadeInOut` | 黑幕渐变 |
| `Cancel()` | 关闭 NormalLayer 最上层（ESC） |
| `GetLayerCanvas(UILayer)` | 取层 Canvas（程序化 UI 可挂） |
| `Event` (`EventController<UIEvent>`) | 跨界面事件；`UIEvent` 目前只有 `None`，可扩展 |
| `GetUIBlackType()` / `GetSafeArea()` | 黑边/安全区 |

- `UIManager.ToUIId(UIType)`：`UIType.Max` 转 null；其余用枚举名。
- `EnsureInitializedAsync`：`ResourceManager.InitializeAsync` → `Initialize` → `InitUIConfig`；`IsReady = _viewControllers != null && _isConfigLoaded`。
- `ReloadUIConfig()`：热更后重读 json；失败保旧表；期间 `IsReady=false`，Open/Close 等待；会清 Lua 模块缓存。
- 迁移注意：`UIManager` 是 `SkierFramework.SingletonMono<UIManager>`，与源项目全局 `SingletonMono<T>` 同名但不同命名空间；复制时不要混用。

#### 13.28.3 `UIViewController` 加载/开关/释放流程

`Load(userData)`：
1. `isLoading=true`；若 `isOpen` 先 `order=uiLayer.PopOrder(this)`。
2. `ResourceManager.InstantiateAsync(uiPath)`；失败 → `Release()` + `UIManager.OnUIOpenFailed(uiId)`。
3. 加载期间被关闭 → `Recycle(go)` + `UnLoadAsset(uiPath)` + `Release()`。
4. `uiView = (UIView)go.GetOrAddComponent(uiViewType)`；`uiViewAnim = go.GetComponent<UIViewAnim>()`。
5. `LuaOnlyView` 注入 `luaModuleName`。
6. `uiView.transform.SetParentEx(uiLayer.canvas.transform)`；按 `GetUIBlackType()` 设置锚点/尺寸（None=StretchAll；Height=VertStretchCenter+width；Width=HorStretchMiddle+height）。
7. `uiView.OnInit(go.GetComponent<UIControlData>(), this)`；`SetAsLastSibling`。
8. `isOpen ? Open(userData, isFirstOpen:true) : Close()`。

`Open`：
- `isOpen=true`；若 `uiView==null` → `Load`；否则若 `!isFirstOpen && order>0` → `TrueClose()`；`TrueOpen()`；`uiViewAnim.OpenAsync()`。

`Close`：
- `isOpen=false`；`OnUIClose(uiId)`（触发 JumpUI 还原）；若 `isLoading` 返回；`uiViewAnim.CloseAsync()` → `TrueClose()`。

`TrueOpen`：`uiLayer.OpenUI` → `SetVisible(true)` → `AddTopViewNum(0)` → `uiView.OnOpen(userData)` → `uiView.OnResume()`。
`TrueClose`：`uiLayer.CloseUI` → `AddTopViewNum(-100000)` → `SetVisible(false)` → `uiView.OnPause()` → `uiView.OnClose()`。
`Release`：若打开先 `TrueClose`；`uiView.OnRelease()`；`ResourceManager.Recycle(go)`；清空引用。
`FullRelease`：`Release` + `ResourceManager.UnLoadAsset(uiPath)`。

迁移注意：
- 现有面板的 `Start/OnEnable` 初始化要拆成 `OnInit`（一次性）与 `OnOpen`（每次）；`OnDestroy` 的退订移到 `OnRemoveListener`。
- 现有 `panelRoot.SetActive(false)` 改为 `UIManager.Close`；`SetActive(true)` 改为 `UIManager.Open`。
- `panelsToCloseOnOpen` 改为 `UIManager.CloseAll(ignoreId)` 或按 `UIType` 关闭。
- `UIViewController.Load` 每次 Load 都会 `uiView.OnInit`；池化重开时注意 `UIControlData.BindDataTo` 会重复添加 weak ref（源实现如此，可忽略或去重）。

#### 13.28.4 `UIView` 生命周期与嵌套 Canvas

- `OnInit(UIControlData, UIViewController)`：`BindDataTo(this)`；`_controller=controller`；`GetOrAddComponent<Canvas>()`；`GetOrAddComponent<GraphicRaycaster>()`。
- `OnAddListener` / `OnRemoveListener`：每次打开/关闭。
- `OnOpen(userData)`：`SortOrder()`；`_canvas.overrideSorting=true`；`_canvas.sortingOrder=_controller.order`；`OnAddListener()`；记录 `_lastSelect`。
- `OnResume`：`DefaultSelect` 选中。
- `OnPause` / `OnClose`：`OnRemoveListener`；恢复 `_lastSelect`；`UIAnim.Kill(gameObject)`。
- `OnCancel`：默认 `UIManager.Close(uiId)`。
- `OnRelease`：`UIAnim.KillAllUnder(gameObject)`。
- `SortOrder`：递归给子 Canvas/粒子/SortingGroup/SpriteRenderer 分配 order（从 `_controller.order+1` 开始），并把 layer 设为 `Layer.UI`。
- **迁移注意**：目标 `FriendPanel` 有嵌套 Canvas（ListPage/RequestsPage/AddPage），`SortOrder` 会重排它们的 `sortingOrder`；迁移后必须检查三页互斥时的遮挡。`UIView.OnInit` 会给根加 Canvas+GraphicRaycaster；如果根已有 Canvas，`GetOrAddComponent` 复用。

#### 13.28.5 `UIControlData` 绑定机制（P8 核心）

数据结构：
- `ctrlItemDatas: List<CtrlItemData>`；`CtrlItemData{ name, type, targets[] }`。
- `subUIItemDatas: List<SubUIItemData>`；`SubUIItemData{ name, subUIData }`。
- `bindUIRefs: List<WeakReference<IBindableUI>>`。

绑定规则：
- 只绑定字段上带 `[ControlBinding]` 的字段；`[SubUIBinding]` 绑定子 UI。
- 字段名必须与 `CtrlItemData.name` **完全一致**（`GetCtrlIndex(fi.Name)`）。
- 数组字段：`targets` 按顺序填充 `Array.CreateInstance(elementType, targets.Length)`。
- 单字段：`GetComponent(itemIdx)` 取 `targets[0]` 的对应组件。
- `type` 字符串主要用于编辑器选择/过滤；运行时用字段类型 `fi.FieldType`。
- 编辑器类型表 `_typeMap` 内置 TMP/UGUI/UIScrollView/ScrollRect/Canvas/CanvasGroup/Animation/VideoPlayer/PlayableDirector/GameObject/Transform/RectTransform；**自定义类型（VirtualScrollList/RedDotItem 等）需要加进 `_typeMap`**，否则编辑器无法选；运行时只要字段类型正确即可。

迁移工具建议：
1. 扫描现有控制器的 `public`/`[SerializeField]` 字段（含 `GameObject`/`TMP_Text`/`Button`/`ScrollRect`/`Image` 等）。
2. 在预制体子树里按字段名找同名节点；按字段类型取组件；写入 `ctrlItemDatas`。
3. 给字段补 `[ControlBinding]`（生成新 View 类时直接带）。
4. 对数组字段（如 `Icons`/`Keys`）按名字前缀/顺序填 `targets`。
5. 对 `GameObject[] panelsToCloseOnOpen` 这类跨面板引用，**不要**绑定，改为 `UIType[]` 或 `UIManager.CloseAll`。
6. 生成/更新 `UIType` 枚举与 `UIConfig.json`（幂等）。
7. 用 `UIControlData.CorrectComponents()` / `CheckSubUIs()` 做一次校验（编辑器工具）。

#### 13.28.6 `UIScrollView` / `UILoopItem` / `UISubView`（固定尺寸列表）

`UIScrollView`（挂在 ScrollRect 同物体）：
- Inspector：`m_ScrollRect`、`m_Content`、`m_AxisType`（Horizontal/Vertical）、`m_AlignType`（Left/Right/Top/Bottom/Center）、`m_ItemPivot`、`m_HorizontalStartSpace`/`m_VerticalStartSpace`、`m_HorizontalSpace`/`m_VerticalSpace`、`m_CountOfOtherAxis`（0=自动）、`m_IsPaging`。
- `Awake`：`m_ScrollRect.onValueChanged += OnValueChanged`；按轴关掉另一个方向；**`m_ScrollRect`/`m_Content` 必须预配，否则 NRE**。
- `UpdateList<T>(IList data, GameObject prefab, bool isKeepPos=false, object userData=null)`：
  - `PrefabPool.Create(prefab)`；`prefab.SetActive(false)`；清空 `m_LoopItems`；
  - 计算 child 宽高/每屏数量；设置 Content pivot/anchor/sizeDelta；
  - `m_CurrentIndex=GetCurrentItemIndex()`；`UpdateContent(index)`。
- `UpdateContent(index)`：创建 `(m_HorizontalCount+2)*m_CountOfOtherAxis`（水平）或 `(m_VerticalCount+2)*...`（垂直）个 item；多余回收；`UILoopItem.UpdateData` 摆位。
- `GetLocalPositionByIndex(index)`：按轴/对齐算 `(x,y)`。
- `Select(index)` / `MoveTo(index,duration)` / `OnEndDrag` 分页。
- `UILoopItem : UISubView`：`Index`、`UIScrollView`、`UpdateData(IList,index,userData)`、`CheckSelect(index)`、`OnUpdateData`、`GetRect()`。
- `UISubView`：`Awake→OnInit/OnAddListener`、`OnEnable→OnOpen`、`OnDisable→OnClose`、`OnDestroy→OnRelease/OnRemoveListener`；`UIControlData.BindDataTo` 只一次。
- `PrefabPool`：按 prefab 名/实例池；`Get/Recycle/RecycleUseList/Destroy`；`PrefabPool.Create(prefab)` 不传 poolName 时每个调用独立池（源 UIScrollView 就是这样）。
- **限制**：`UIScrollView` 只支持**固定 child 宽高**（用 `prefab.GetComponent<RectTransform>().rect` 计算），不支持变高行。目标现有：
  - 好友列表/排行榜/任务/邮件/背包/表情格：固定行高，可换 `UIScrollView`。
  - 聊天气泡：变高（`VirtualScrollList` 支持 `GetItemHeight`），**不要**直接换 `UIScrollView`；保留目标 `VirtualScrollList`（可包一层 `UIView`）或扩展 `UIScrollView` 支持变高。
- 迁移注意：`RowTemplate` 必须是预制体/模板节点，`UIScrollView.UpdateList` 会 `PrefabPool.Create` 并 `Instantiate`；模板本身要 `SetActive(false)`。Content 不要挂 `VerticalLayoutGroup`/`ContentSizeFitter`（会被手写摆位覆盖）。

#### 13.28.7 `UIViewAnim` / `UIAnim`（开关动画与业务补间）

`UIViewAnim`：
- 组件字段：`targets`（子节点列表）、`openSteps`/`closeSteps`（`UIViewAnimStep` 列表）、`autoResetOnPlay`。
- 步骤字段：`targetIndex`（-1=自身）、`prop`（Alpha/AnchoredPos/LocalScale/LocalRotation/Clip）、`from/to` 值、`duration`、`delay`、`useCurve`/`curve`、`ease`、`unscaledTime`。
- `OpenAsync/CloseAsync` 返回 `UniTask`；`UIViewController` 会 await。
- 打断：`Interrupt()` Kill 旧序列并从当前实际值续到新终点；`OnDisable` 会 Kill 并放行 await，避免悬挂。
- `Clip` 步骤：DOTween 只计时，legacy `Animation` 播放 clip；不支持倒放，关闭动画要单独配 `closeSteps`。
- `Alpha` 步骤操作根 `CanvasGroup`（自动加）；`AnchoredPos` 要求目标是 `RectTransform`。
- 目标 DOTween 已确认支持 `LinkBehaviour`/`SetLink`（DOTween.XML 有定义），可直接用。

`UIAnim`（静态桥，Lua 友好）：
- API：`FadeTo/FadeFrom/MoveTo/SlideIn/ScaleTo/ScaleFrom/Rotate/Punch/PlayClip/Delay/Kill/KillAllUnder/KillById/IsPlaying/IsHandlePlaying`。
- `onDone` 可传 `Action` 或 `LuaFunction`；完成才回调，打断/Kill 不回调但会 Dispose LuaFunction。
- `SetLink(go, LinkBehaviour.KillOnDestroy)`；`UIView.OnClose/OnRelease` 会 Kill 本界面补间。
- 迁移注意：目标 XLua 源码在 `Assets/XLua`，`UIAnim` 的 `using XLua` 可以编译；如果以后把 UI 框架放进 asmdef，要引用 XLua 程序集。

#### 13.28.8 `UIModelManager` / `UIRenderToTexture`（选人 3D 预览）

`UIModelManager : Singleton<UIModelManager>`：
- `OnInitialize`：建 `UIModelRoot`（DDOL）、`UIModelLight`（Directional，layer 6，shadows none）；`m_UICamera` **被注释掉了**（`//m_UICamera = GameObject.Find("UICamera")...`）。
- `LoadModelToRawImage(path/rawImage/...)`：`ResourceManager.InstantiateAsync` → `UnLoadModelByRawImage` → `LoadModelToRawImage(go,...)`。
- `LoadModelToRawImage(go, rawImage, ...)`：给 RawImage `GetOrAddComponent<UIRenderToTexture>()`；`renderToTexture.Init(pos, isOrth, orthSizeOrFOV, index, cameraDistance)`；模型 `SetLayerRecursively(Layer.UIRenderToTarget=6)`；挂到 `UIModelRoot`；位置 `(100*index, -10000, 0)+offset`；`AddTarget(go, canDrag)`。
- `UnLoadModelByRawImage`：按 `EntityId` 从 `Targets` 移除 → `ResourceManager.Recycle(go)`；空则回收 index/camera。
- `SpawnCamera`：池化 `CameraRTT`，`fieldOfView=30`、`clearFlags=SolidColor`、`cullingMask=1<<6`、`farClipPlane=30`、`orthographicSize=1`。
- `RecyleCamera`：`targetTexture=null`、`SetActive(false)`、入池。

`UIRenderToTexture`：
- `Init`：`InitCamera` → `m_Index=index`；相机 `localPosition=vec+(0,0,cameraDistance)`、`localEulerAngles=(0,180,0)`；创建 `RenderTexture.GetTemporary(rawImage.rect.width, rect.height, 1, ARGB32)`；`camera.targetTexture=rt`；`rawImage.texture=rt`。
- `Update`：未拖拽的目标 `rotation` 缓动回 `identity`。
- 拖拽：`OnBeginDrag` 用射线找目标（层 6），`OnDrag` 绕 Y 旋转，`OnEndDrag` 清空。
- 点击：`OnPointerClick` 射线找目标，触发 `OnTargetClick`。
- `OnDisable`：卸载模型/回收相机/释放 RT（避免隐藏面板后台渲染）。
- `OnDestroy`：回收模型 + 相机 + RT。

**目标项目迁移必须修的 3 个点**：
1. `EntityId` → `int`（`GetInstanceID()`），`Dictionary<EntityId,Target>` → `Dictionary<int,Target>`。
2. `SetClickTarget` 里 `UIModelManager.Instance.UICamera` 为 null（源被注释）；目标 UI 是 ScreenSpaceCamera，必须改为 `m_RawImage.canvas.worldCamera` 或把 `m_UICamera` 设为 `UIManager.Instance.UICamera`。否则点击/拖拽射线位置错误。
3. `Init` 创建 RT 时 `RawImage.rect` 可能为 0（面板未布局/未激活）；`room` 在 `LoadHeroPreview` 前 `SetActive(true)`，但仍可能当帧 rect 未更新。建议 `await UniTask.Yield()` + `Canvas.ForceUpdateCanvases()` 后再 `LoadModelToRawImage`，或按固定尺寸创建 RT。
4. 预览光照：`UIModelLight` 是第二盏 Directional；`URPGenshinToon` 的 `GetMainLight()` 取的是场景主光，不是预览灯。若预览模型全黑/阴阳脸，需临时把场景主光方向对齐预览灯，或预览用 unlit/自定义受光材质。目标 zhuye 有 `Directional Light`，迁移后必须实测。

#### 13.28.9 `ResourceManager` / `InstancePool` / `PrefabPool`

- `ResourceManager : Singleton<ResourceManager>`（纯 C# 单例）：`_assetCaches`/`_assetHandles`/`_residentAssets`/`_loadedAssetInstanceCountDic`/`_objectInstanceIdKeyDic`/`_instancePool`。
- `InstantiateAsync(path)`：缓存命中 → `InternalInstantiate`；未命中 → `LoadAssetAsyncInternalAsync<GameObject>` → `InternalInstantiate`。
- `InternalInstantiate`：`_instancePool.Get(location)` 或 `Instantiate(asset)`；`_instancePool.InitInst`；记录实例→path。
- `Recycle(go)`：按实例 path 回池；非本模块创建的对象直接 `Destroy`。
- `LoadAssetAsync<T>`/`TryGetAsset<T>`/`UnLoadAsset`/`ReleaseRef`/`PreLoadAssetAsync`/`LoadSpriteAsync`/`LoadSceneAsync`/`ReadTextStringAsync`/`ReadTextBytesAsync`。
- `NormalizeToLocation`：去掉 `Assets/Resources/` 前缀和扩展名。
- `InstancePool`：按 location 维护 `Stack<GameObject>`；`Get/InitInst/Recycle/Clear`。
- `PrefabPool`：见 13.28.6；按 poolName 全局字典（可为空）。
- 目标改造：Resources 适配后，`_assetHandles`/`_processingLoadCount` 可简化；`UnLoadAsset` 只清缓存/池（`Resources.UnloadAsset` 可选，不要卸载常驻）。`InstancePool` 仍需保留以支持 UI 池化。

#### 13.28.10 `UIAdapter` / 安全区

- `UIAdapter : MonoBehaviour`：`uIAdaptType`（All/LeftOrTop/RightOrBottom）；每秒检测 `Screen.safeArea` + `Screen.orientation`，改 `RectTransform.anchorMin/Max` 并 `sizeDelta=0`。
- 目标项目已有自己的安全区/相机逻辑；本期 UI 框架迁移**不强制**用 `UIAdapter`，全屏面板用 StretchAll 即可。若要用，注意它会覆盖手写锚点。

#### 13.28.11 P8 面板迁移顺序与每面板检查项

建议顺序（与用户确认的首批一致）：
1. 好友/聊天（先迁 FriendView + ChatView；聊天列表保留 `VirtualScrollList`）。
2. ~~背包（BagView）~~ → **不迁移**：保留原项目 `beibao/BagPanel` + `BagUIController`（2026-10-20 用户定稿，见 §27）。
3. 任务（QuestView；固定行高，可换 `UIScrollView`）。
4. 邮件（MailView；固定行高）。
5. 排行榜（LeaderboardView；固定行高）。
6. 副本 HUD（DungeonHudView；程序化 AdoptCanvas）。
7. 世界聊天（WorldChatView）。
8. 活动（ActivityView : LuaOnlyView）。
9. 阿福（AgentView；悬浮球保留 DDOL）。
10. 入口栏（HudEntryBar；TopLayer 常驻）。

每面板检查项：
- 打开/关闭/重开；`OnInit` 只一次、`OnOpen` 每次；`OnRemoveListener` 不泄漏。
- 层级/排序：与迁移前截图对比；嵌套 Canvas 的 sortingOrder 正确。
- 点击：所有按钮/输入框/滚动/拖拽射线正常；`GraphicRaycaster` 存在。
- 数据：打开即拉取/缓存直渲；红点/状态文案/空态。
- 字体：中文无 □/空白；TMP 字体指向目标 NotoSansSC。
- 分辨率：1920×1080 与 1280×720 下布局不越界。
- 性能：只实例化可见行；关闭后无后台渲染/无残留协程。
- 回归：该面板对应的现有 E2E 或手动流程全过。

---

## 14. 变更记录

| 日期 | 阶段 | 变更 | 提交 |
|---|---|---|---|
| 2026-10-19 | 方案 | 初版文档，记录 D1–D10 决策、P0–P9 计划、风险登记册 | 55cd543 |
| 2026-10-19 | 源码补充 | 实测 Input 动作表、联机配置、层号（3/6/7）、英雄 prefab 组件栈、资源闭包 116 文件/425 MB、UI 预制体契约、战斗场景结构；新增 R19–R25 与 P0/P3/P5/P6/P7 修订 | db826e7 |
| 2026-10-19 | 源码补充 2 | 实测 NGO 2.x RPC 写法（2 处必须改 ServerRpc）、HealthModel/BuffComponent 全链路、层 6 双重语义、相机/VCam 参数、URPGenshinToon 兼容性、FBX Generic/Humanoid 差异、目标面板字段清单与根画布状态；新增 R26–R34、13.19/13.20 | f2f2084 |
| 2026-10-19 | 源码补充 3 | 实测移动 8 态/连击 3 态逐状态行为、ComboContainer/ComboData API、EventCenter 事件与数据结构、AttrType/AttributeComponent/Buff 策略、NGO 1.13 NetworkList 约束；新增 R35–R43、P4 单机与 P5 联机验收矩阵 | a2a2ec6 |
| 2026-10-19 | 源码补充 4 | 实测 UI 框架内部：8 层 UILayer（含 DamageLayer）、UIManager/UIViewController/UIView 生命周期、UIControlData 绑定、UIScrollView/UILoopItem/UISubView、UIViewAnim/UIAnim、UIModelManager/UIRenderToTexture（EntityId/UICamera null/RT 尺寸/预览光照 4 个坑）、ResourceManager/PrefabPool、P8 面板迁移顺序与检查项 | a67923b |
| 2026-10-19 | P0 | 安装 Input System 1.7.0；activeInputHandler=2；层 3/6/7=player/environment/enemy；建战斗/UI/资源目录；发现并修正 `Resources/ui` 大小写冲突（框架改用 `Resources/GameUI`）；refresh 0 error/0 warning | 18c273d |
| 2026-10-19 | P1 | 移植 SkierFramework 内核（102 文件）；ResourceManager 改 Resources；UIManager 自建 SkierUICamera/UICameraService Facade；修 EntityId/Light.cookieSize2D；加 SmokeView 并完成打开/关闭/录屏冒烟；修 “预制体 Default 层导致 UI 不可见” | 31a8c1f |
| 2026-10-19 | P2 | 排行榜拆出 `LeaderboardView` 并抽为预制体（全屏根+700×620 面板，8 项 UIControlData 绑定）；`ChallengeUIController` 只留挑战；删除场景旧面板并保存；新增 `UIPrefabMigrationWindow` 通用迁移工具；运行时/录屏验证通过 | 6d2601d |
| 2026-10-19 | P3 | 重写大厅/房间/加入房间三个 UIView 并程序化重建预制体（深色 HUD）；`RoomState`/`BattleSpawnManager` 思路移植为 `RoomState` + `BattleNetworkLauncher`（UnityTransport 7777）；`UIModelManager` 预览三坑修复；zhuye 新增“对战”入口；单机 E2E/录屏通过 | efb309c |

---

## 15. P0 执行记录（2026-10-19）

| 项 | 结果 |
|---|---|
| Input System | `com.unity.inputsystem` **1.7.0** 安装成功（`unity_package.install_package` 返回 verified） |
| 输入模式 | `ProjectSettings.asset activeInputHandler = 2`（Both）；实测 before/after 都是 2（安装包时已自动/原本为 Both） |
| 物理层 | 3=`player`、6=`environment`、7=`enemy` 写入 `TagManager.asset`；原 3/6/7 为空 |
| 目录 | `Assets/Scripts/Battle/{Core,Network,Lobby,Presentation}`、`Assets/Scripts/UI/Framework{,/Editor}`、`Assets/Art/Battle/{Characters,Animations,Audio,VFX}`、`Assets/Prefabs/Battle`、`Assets/Resources/GameUI/{Battle,Friend,Bag,Quest,Mail,Leaderboard,Dungeon,Activity,Agent,Hud}` |
| **大小写冲突修正** | 目标已有 `Assets/Resources/ui`（物品图标）。Windows 下 `UI` 与 `ui` 是同一目录，因此框架资源改放 `Assets/Resources/GameUI`；误建的空 `Assets/Resources/ui/Battle` 已删除。UIConfig 路径统一为 `GameUI/...` |
| 编译 | `unity_editor.refresh`：**0 errors / 0 warnings** |
| 待提交的 P0 文件 | `Packages/manifest.json`、`Packages/packages-lock.json`、`ProjectSettings/ProjectSettings.asset`、`ProjectSettings/TagManager.asset`、新建目录及 `.meta`、本文档更新 |
| Git 注意 | `manifest.json` 中除了本次的 `com.unity.inputsystem`，还包含工作区原有的 `com.unity.netcode.gameobjects: 1.13.1` 与 `cn.tuanjie.codely.bridge: 1.0.87`；提交时一并纳入，提交信息里注明 |

P0 完成后的下一步：P1（UI 框架内核移植 + Resources 适配 + SmokeView 冒烟）。

---

## 16. P1 执行记录（2026-10-19）

| 项 | 结果 |
|---|---|
| 复制 | `Assets/script/Framework/UI` → `Assets/Scripts/UI/Framework`；`Framework/Other` → `Framework/Common`；`ResourceManager.cs`/`InstancePool.cs` → `Framework/Common`；共 102 个文件（含 `.meta`，保 GUID） |
| Resources 适配 | `ResourceManager` 改为 `Resources.Load`/`Resources.LoadAsync` 的同步封装；保留 `InstantiateAsync/Recycle/LoadAssetAsync/UnLoadAsset/TryGetAsset/LoadSpriteAsync/LoadSceneAsync` 等 API 形状；`UIConfig` 路径改 `GameUI/UIConfig` |
| Unity 6 API 修正 | `EntityId/GetEntityId` → `int/GetInstanceID()`（UIModelManager/UIRenderToTexture）；`Light.cookieSize2D` 在 Tuanjie 不存在，已删除 |
| UI 相机归属 | `UIManager` 自建 `SkierUIRoot` + `SkierUICamera`（DDOL、正交、Overlay）；`UICameraService` 改为 Facade（`SetExternalCamera`/`UICamera` 返回框架相机）；场景旧 `UICamera` 自动禁用；现有 8 个画布重绑到 `SkierUICamera` |
| SmokeView | 新增 `SmokeView : UIView` + `Assets/Resources/GameUI/SmokeView.prefab` + `UIConfig.json` 条目；打开后 `active=True, layer=5(UI), canvasSort=3030, parent=NormalLayer, worldCamera=SkierUICamera`；关闭后 `active=False` |
| 踩坑修复 | 预制体根/子物体默认在 `Default` 层，`SkierUICamera` 只渲染 UI 层 → 面板逻辑正常但画面不可见。已在 `UIView.OnInit` 加 `gameObject.SetLayerRecursively(Layer.UI)` |
| 视觉门禁 | `screenshots/p1_smoke_view_fixed.mp4`（3s，640×360）；VLM 确认中文“SkierFramework 冒烟测试 / UIManager / UIView / UIConfig 正常”与“关闭”按钮清晰、无豆腐块/乱码/粉色错误、未被顶部按钮栏遮挡 |
| 框架行为提示 | `UIViewController.Load` 会按 `GetUIBlackType()` 把视图根拉伸到全屏；因此每个面板预制体应是“全屏透明根 + 内部固定尺寸 Panel”，不要把 Panel 本身当根（SmokeView 当前根被拉伸，属冒烟特例） |
| 编译 | `unity_editor.refresh`：**0 errors / 0 warnings** |

P1 完成后的下一步：P2（旧 UI 抽预制体流水线，先迁排行榜样板）或 P3（选人大厅）。

---

## 17. P2 执行记录（2026-10-19）

| 项 | 结果 |
|---|---|
| 排行榜拆分 | 新建 `Assets/Scripts/UI/Leaderboard/LeaderboardView.cs`（`UIView`，8 个 `[ControlBinding]` 字段）；`ChallengeUIController` 改为只负责挑战，排行榜入口改为 `UIManager.Open(UIType.LeaderboardView)` |
| 预制体 | 从 `zhuye/FriendCanvas/FriendUIRoot/LeaderboardPanel` 抽取为 `Assets/Resources/GameUI/Leaderboard/LeaderboardView.prefab`：全屏包装根 + 原 700×620 面板（右锚 -20）；场景旧 `LeaderboardPanel` 已删除并保存 `zhuye.scene` |
| 绑定 | 自动生成 `UIControlData.ctrlItemDatas` 8 项（titleText/closeButton/refreshButton/leaderboardScroll/listContent/rowTemplate/myRankText/leaderboardStatusText）；运行时 8 项全部非空 |
| UIConfig/UIType | `UIConfig.json` 增加 `LeaderboardView`（`GameUI/Leaderboard/LeaderboardView`，NormalLayer，isWindow=false）；`UIType` 增加 `LeaderboardView` |
| 通用流水线 | 新增 `Assets/Scripts/UI/Framework/Editor/UIPrefabMigrationWindow.cs`（菜单 `Tools/UI/SkierFramework 面板迁移窗口`）：选场景面板 → 生成包装根/View/UIControlData/预制体/UIConfig；`ControlBindingAttribute` 增加可选 `path`，字段名与节点名不一致时用 `[ControlBinding("LeaderboardList")]` 显式映射 |
| 运行时验证 | `view=True, active=True, layer=5, sort=3030, cam=SkierUICamera, parent=NormalLayer, bindings=8`；关闭后 `active=False`；警告 `未连接，忽略发送 LeaderboardRequest` 属未登录预期 |
| 视觉门禁 | `screenshots/p2_leaderboard.mp4`；VLM 确认右侧深色面板、标题“排行榜”、三列表头、刷新/关闭/我的排名，中文清晰、无方块/乱码/粉色、未被顶部按钮栏遮挡 |
| 入口接线 | 静态检查 `ChallengeUIController`：entryButton/closeButton/startButton/clickZone/leaderboardEntryButton/challengePanelRoot 全部非空；挑战面板逻辑保留 |
| 备注 | 入口按钮运行时点击的自动测试因 Play 被 MCP 弹窗暂停而超时（非缺陷）；已用“静态接线 + 直接 UIManager.Open/Close”验证 |
| 编译 | 0 errors；4 条为项目原有 editor 警告（MmQuestConfigExporter/QuestEditorWindow/RedDotConfigWindow/DialogueGraphEditorWindow） |

P2 完成后的下一步：P3（选人大厅）或按 P8 清单继续迁移背包/任务/邮件。

---

## 18. P3 执行记录（2026-10-19）

| 项 | 结果 |
|---|---|
| 视图 | `BattleLobbyView`（创建/加入/关闭）、`JoinRoomView`（IP 输入 + 校验 + StartClient）、`BattleRoomView`（双方 3D 预览、选人、准备、退出、单机调试） |
| 预制体 | `Assets/Resources/GameUI/Battle/{BattleLobbyView,JoinRoomView,BattleRoomView}.prefab`，全屏透明根 + 固定面板；绑定数分别 7/7/22；全部 `UIControlData` 目标非空 |
| 构建工具 | `Assets/Scripts/Battle/Lobby/Editor/BattleLobbyPrefabBuilder.cs`，菜单 `Tools/Battle/P3 一键构建`（可重复执行：预制体覆盖、入口按钮先删后建） |
| 联机 | `Assets/Resources/Battle/BattleNetworking.prefab` = NetworkManager + UnityTransport + RoomState；`EnableSceneManagement=true`，transport 引用已接，`127.0.0.1:7777` |
| 启动器 | `BattleNetworkLauncher`：EnsureInstance/StartHost/StartClient/ShutdownAndDestroy；Host 时 `SetConnectionData(127.0.0.1, 7777, 0.0.0.0)` |
| RoomState | 源逻辑整体保留（6 条 NamedMessage、座位表、对手同步、双方就绪判定）；`BattleSceneName` 留空时只记录“双方就绪”不切场景，等 P4/P5 填场景名 |
| 预览 | `UIModelManager`/`UIRenderToTexture` 三处修复：①UICamera 惰性从 UIManager 解析；②RT 尺寸为 0 时 `Canvas.ForceUpdateCanvases` + 画布尺寸兜底；③预览专用平行光常关，统一用场景主光（YSA Toon/URP Lit 都读 `GetMainLight`） |
| 预览模型 | P3 用项目现有角色占位（`545.fbx`→Anbi、`未名.fbx`→Ellen），`BattleHeroCatalog` 集中管理 CharId/显示名/Resources 路径/取景参数；P7 只改这里 |
| 入口 | zhuye 新增 `battle` 画布（sort=10）+ `BattleEntryButton`（x=-1179.6, y=-24, 110×44, #07C160）+ `BattleEntryButton.cs`；场景已保存 |
| 单机 E2E | 大厅 `active=True, layer=5, sort=3030, parent=NormalLayer, cam=SkierUICamera`；创建房间 `host=True, players=1, seat=0`；选安比后 `selfName=安比, RT=340×360, 可见像素=20466, layer6 renderers=30, camera=1`；准备后 `label=取消准备, prepare=已准备`；退出后 `nm=null, RoomState=null, lobby 恢复` |
| 入口回归 | `battle` 画布/按钮 active；点击入口开大厅；加入房间默认 `127.0.0.1`；非法 IP 提示“请输入有效的 IPv4 地址”且不建会话；返回回到大厅；关闭后大厅不可见 |
| 视觉门禁 | `screenshots/p3_room_preview_v2.mp4`（6s，640×360，2fps 慢放）；VLM 确认标题/关闭键/标签/状态栏均在面板内、中文清晰、模型完整无粉红、选人按钮绿色高亮随选择切换 |
| 已知环境问题 | `exec_runtime_script` 进入 Play 时偶发被编辑器内部错误（Burst `TryStartTrackerByHandle` 主线程异常 / 无服务端时的 socket 失败）触发 Error Pause，导致脚本超时；用“先 `resume`、再执行脚本”的方式绕开。属测试环境，不影响运行时逻辑 |
| 未完成 | 双进程 StartHost/StartClient 实测（需要第二个客户端进程）顺延到 P5；`BattleSpawnManager` 依赖 `Player`，一并在 P5 移植 |
| 编译 | `unity_editor.refresh`：0 errors / 0 warnings |

P3 完成后的下一步：P4（战斗核心单机）或 P5（NGO 1.13 联机 + 双进程验收）。

---

## 19. P4-3 执行记录（2026-10-20，全量移植）

用户策略变更：不再逐模块挑选，改为「源工程代码 + SO + netcode 配置物全量复制，重名用 namespace 隔离」。

| 项 | 结果 |
|---|---|
| 代码全量复制 | `D:\project\compete\Assets\script` 137 个脚本 → `Assets/Scripts/Compete/`，保留源目录结构（`AIstate/bag/behavorai/buff/damage/data/equip/network/player/skill/sound/tool/ui/vfx/事件/基类/单例/状态机/镜头`） |
| namespace 策略 | 无命名空间的脚本统一包进 `namespace Compete`；`behavorai` 保持 `BehaviorDesigner.Runtime.Tasks` 并 `using Compete`；`Framework`（目标已移植为 SkierFramework）与 `newui`（已重写为 Battle/Lobby 视图）不重复复制 |
| 重名处理 | 源工程内部仅 `Singleton`/`SingletonMono` 重名（Framework/Other 与 基类/），排除 Framework 后不再冲突 |
| Behavior Designer | 插件 1184 文件/1.8MB 全量移植（自带 asmdef `BehaviorDesigner.Runtime`），15 个 AI 行为任务可用 |
| Unity 6 → Tuanjie 适配 | Behavior Designer 物理任务 `linearVelocity/linearDamping/angularDamping` → `velocity/drag/angularDrag`（12 处）；`FindObjectsByType(FindObjectsInactive.Include)` → `FindObjectsOfType(true)` |
| NGO 2.13 → 1.13 | `[Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]` → `[ServerRpc(RequireOwnership = false)]`（`BuffComponent` / `HealthModel`） |
| 属性特性 | `[Header]` 作用于属性非法 → `[field: Header]`（AIStateData 2 处） |
| SO 资产 | `AIPlayer.asset`、`ItemDatabase/ItemActionDefaults`、`BagItems/`(13)、`BagActions/`(4)、`aibehavior.asset`(ExternalBehaviorTree)、`111.asset`(NetworkPrefabsList) → `Assets/Scripts/Compete/Resources/`；脚本 `.meta` 按源 GUID 复制（跳过目标已占用 GUID），SO 的 `m_Script` 全部解析为 `Compete.*` |
| netcode 配置物 | `111.asset` = `NetworkPrefabsList`（默认列表含安比/Ellen 预制体，GUID 在目标已存在）；NGO 本身无代码生成物，源工程 runtime .dll 不移植 |
| 输入系统 | 代码与 `.inputactions` 与源工程逐字一致（P4-1 已移植）；补 `InputSystem.inputsettings.asset` 与 `InputSystem_Actions.inputactions`（P4-2）；英雄预制体移除重复的 `PlayerInput` + `CharacterInputSystem`（R39），由全局 `CharacterInputSystem.MainInstance` 唯一读输入 |
| 未移植（有意） | `newui/jiaru.cs`（依赖源工程 UOS KCP 传输，目标用 UnityTransport）、`skill/GAS_AttackTestDriver.cs`（依赖源工程 GAS 生成物 `AS_tt`/`GTagLib`/`InitWithPreset`） |
| 已知孤儿引用 | `ItemAction_Merge.asset`、`shayuattacklist.asset.ForwardATKData` 的 GUID 在源工程也已丢失（非搬迁造成） |
| 编译 | `unity_editor.refresh`：0 error（8 条 warning，均为源工程既有 CS4014/CS0414） |
| 提交 | `a5538b8 feat(battle): 全量移植源工程脚本/SO与 Behavior Designer 依赖（P4-3）` |

后续建议：`Compete` 命名空间内代码是"源工程原样副本"，目标工程实际运行仍走 `Assets/Scripts/Battle/*`（已做 NGO/相机/输入修复）。二者并行存在，切换需按模块逐个对比迁移，避免两套逻辑同时驱动。

---

## 20. P4-4 执行记录（2026-10-20，战斗场景 multiplecompete 全量移植）

目标：把源工程 `Assets/Scenes/multiplecompete.unity` 原样搬进目标工程并跑通「Host 开局 → 房间就绪 → 场景加载 → 英雄生成 → 座位相机绑定」链路。

| 项 | 结果 |
|---|---|
| 场景文件 | `Assets/Scenes/multiplecompete.scene`（沿用源 YAML，13 个根对象与源一一对应；Unity 6 → Tuanjie 2022.3 直接导入无报错） |
| 根对象 | Directional Light / Global Volume(+子物体04_0) / soundmanager / DamageTextManager(+子物体 Plane) / EventSystem / manager(NetworkObject+BattleSpawnManager) / 出生点 1·2 / Camera / Camera (1) / thirdcamera / thirdcamera (1) / VFX_PoolManager(inactive) |
| 复制资源（保留源 GUID，`.meta` 一起复制） | `Assets/Prefabs/Battle/Scene/`：thirdcamera、Camera、DamageTextManager、soundmanager 四个 prefab；`Assets/Audio/Battle/`：NewAudioMixer.mixer、SoundData.asset；`Assets/Settings/SampleSceneProfile.asset` |
| 脚本差异（唯一一处） | 源 `BattleSpawnManager`（`Compete` 命名空间，依赖 `Compete.RoomState`）→ 新增 `Assets/Scripts/Battle/Scene/BattleSpawnManager.cs`（全局命名空间，依赖目标 `RoomState.GetBattlePlayers()` 的 `BattlePlayerInfo`），场景 manager 的 m_Script 指向它 |
| 其余脚本 | 全部命中目标已有实现：P4-3 已把源 GUID 赋给 Battle 脚本（Player / HealthModel / BuffComponent / ActorAudioComponent / CharacterInputSystem / DamageTextManager / AudioManager / SoundData），`Camera_ZoomController` 与 `VFX_PoolManager` 走 `Compete` 原样副本 |
| 联机接线 | `BattleNetworking.prefab` 的 `RoomState.BattleSceneName` 由空 → `multiplecompete`；场景已加入 Build Settings（NGO SceneManager.LoadScene 必需） |
| 资源自愈 | 复制 mixer / SoundData 后，两个英雄 prefab 里原本为空的 `ActorAudioComponent.sfx3DGroup`（Effect）、`soundData` 自动解析回填（GUID 命中） |

**验证（Play，单进程 Host）**：

- 场景加载 0 error；13 个根对象、0 个缺失脚本；Volume profile、地面 URP/Lit（layer 6）、音量组与 SoundData 全部就绪。
- `NetworkManager.StartHost()` → `RoomState` 记录玩家进座 0 → `ServerSetReady(0, 0)` → 触发 `LoadScene("multiplecompete")` → 场景内 manager 的 `BattleSpawnManager.OnNetworkSpawn` 生成英雄：`安比(Clone)`，`Seat=0 / CharId=0 / Owner=0 / IsSpawned=true`，落点 `(-13.62, -0.01, -95.04)`（出生点 1）。
- 座位相机：`thirdcamera.Follow = 英雄`，`Camera` 为 `viewCamera`，`Camera (1)` 与 `thirdcamera (1)` 被停用。
- 录屏 `screenshots/battle_scene_visual.mp4`（VLM 确认）：角色材质完整无粉红、地面与方向光正常、相机稳定跟随、无 UI 报错。

**已知边界（源工程同款，不是回归）**：

- 两套座位相机各带 AudioListener。**已处理**：座位 1 的 `Camera (1)` / `thirdcamera (1)` 默认关闭（`Player` 绑座时会 `SetActive(true)`），未绑座前场景里只有 1 个 listener，不再刷 "There are 2 audio listeners" 警告。
- `VFX_PoolManager.effectDates[0].effectItemData` 在源工程即已丢失 GUID（物体本身是 inactive），场景保留空引用。
- 战斗场景没有 UIManager/画布：`UIManager` 由 `SingletonMono` 懒创建，进入战斗前从 zhuye 带过来。
- 场景重载后 `UICameraService` 会再建一个 DDOL 的 UICamera 兜底（框架既有行为，两个都处于 inactive，不影响渲染）。

---

## 21. P6-1 执行记录（2026-10-20，战斗 HUD 血条）

P4-3 曾把源 `newui` 整体跳过，`bloodpanel` 因此缺失；本轮把它接进 SkierFramework。

| 项 | 结果 |
|---|---|
| 视图脚本 | `Assets/Scripts/UI/Battle/bloodpanel.cs`（`namespace SkierFramework`、类名 `bloodpanel` 与 `UIType.bloodpanel` 同名，`UIConfig` 才能按名字解析出类型）；逻辑逐字沿用源 `newui/bloodpanel.cs`：订 `HealthModel.CurrentHP/MaxHP` 的 `BindableProperty.OnValueChanged` 刷 `Image.fillAmount`，按 `Player.NetCharId` 加载头像 |
| 预制体 | `Assets/GameUI/Battle/bloodpanel.prefab`（源 `Assets/HOTS/UI/Prefabs/bloodpanel.prefab` 直接复制；`UIControlData` 的 GUID 与目标一致，4 条绑定 bloodzhu/renwu1/renwu2/bloodenemy 原样可用；根 Image alpha=0 不遮挡；血条框/填充 sprite 走 `Assets/Resource/ui/血量/04.png` 子图） |
| 头像 | 源头像在 `Assets/Resource/ui/场景/`（不在 YooAsset 收集目录），复制到 `Assets/GameUI/Battle/Portraits/` 并设 `TextureType=Sprite`（**不复制 .meta**，避免 GUID 冲突；复制 .meta 时是 Default 导入，`LoadAssetAsync<Sprite>` 会拿到 null） |
| 注册 | `Assets/GameUI/UIConfig.json` 新增 `bloodpanel → Assets/GameUI/Battle/bloodpanel.prefab`（NormalLayer、非窗口） |

**验证（Play，单进程 Host + 假敌方）**：

- 血条面板可由 `UIManager.Open(UIType.bloodpanel)` 正常加载（YooAsset EditorSimulate 的 ui 包），层级 `NormalLayer`、`sortingOrder=3030`。
- 两侧绑定：`FindObjectsOfType<Player>` 取 `IsOwner` 为己方、其余 `IsSpawned` 为敌方；测试里用 `SpawnWithOwnership(1)` 造出归属假客户端的第二个英雄，设 `NetCharId=1`。
- 头像：`renwu1 = Snipaste_2026-09-10_18-25-50`（安比）、`renwu2 = Snipaste_2026-09-10_18-25-22`（艾莲）加载成功。
- 数值联动：敌方 HP 55/100 → `enemyblood.fillAmount=0.550`；己方 70/100 → `zhublood=0.700`。
- 录屏 `screenshots/battle_hud_e2e.mp4`（VLM 确认）：顶部左右两条带框血条、两侧真实头像、右侧先掉血再左侧掉血、无粉红/乱码/异常遮挡。

**踩坑记录**：`find` 类 API 在本工程要写 `FindObjectsOfType<T>()`（无参 `FindObjectsByType<T>()` 编译报 CS1501）；`UniTaskVoid` 调用要 `.Forget()` 消除 CS4014；Play 模式下 `WaitForSeconds` 会被编辑器暂停冻住，验证脚本改用 `Time.realtimeSinceStartup` 轮询。

---

## 22. P6-2 执行记录（2026-10-20，攻击刀光接入 VfxManager）

| 项 | 结果 |
|---|---|
| 调用点 | `PlayerComboState.PlayAttackClip` 里原本注释的 `PlayAttackVFX(data)` 恢复调用（真实输入路径必然会走到） |
| 实现 | `CharacterCombo.PlayAttackVFX` 由源工程的 `VFX_PoolManager.GetAttachedVFX(characterName, "ATK_Slash")` 改为目标工程统一的 `VfxManager.Instance.PlayAttached(VfxKeys.PlayerAttackSlash, bone)`；骨骼按 `Anbi_Weapon_Bone01 → Anbi_Weapon_02 → Ellen_Weapon → Bip001 R Hand` 兜底查找并缓存（目标 `Player` 的 `WeaponBone` 字段是注释状态，故查找逻辑放在 CharacterCombo，避免改动 Player.cs） |
| 资源 | 直接复用 `Assets/Resources/VfxCatalog.asset` 的 `player_attack_slash`（SwordSlashThinWhite，positionOffset (0.87,0,0) 是副本场景调的补偿值） |

**验证（Play，真实状态机路径）**：

- 还原真实输入前置（`ReusableData.currentCombo = LightCombo`、`currentIndex=0`）后 `SwitchState(ATKIngState)`：
  - 动画切到 `Avatar_Female_Size02_Anbi_Ani_Attack_Normal_01` 且时间推进；
  - 同一帧 `VfxManager.GetActiveCount(player_attack_slash) = 1`，实例 `SwordSlashThinWhite(Clone)` 的父物体是 `Anbi_Weapon_Bone01`。
- 录屏 `screenshots/battle_attack_vfx4.mp4`（VLM 确认）：角色完成挥砍→转身→收招，2~4 秒出现白色弧形刀光，无粉红/异常。
- 教训：强制 `SwitchState(ATKIngState)` 而不设置 `currentCombo` 时，`PlayerATKIngState.Enter → PlayAttackClip` 会因为没有连招容器而静默不播（表现为"状态对了但没动画没特效"）。

**剩余（P6 其他项）**：相机震动（源 `CameraHitFeel`）与"顿帧并入 `PauseManager`"仍未做。

---

## 23. P6-3 执行记录（2026-10-20，伤害飘字坐标修复）

**现象**：真实战斗里血量正常扣、刀光正常播，但从不出现伤害数字；直接 `UIManager.Open(bloodpanel)` 和手动 `Show()` 都正常。

**根因**：`DamageTextManager.OnDamage` 用 `UIManager.Instance.UICamera`（框架的**界面相机**，Overlay/正交）做 `WorldToScreenPoint`。用界面相机投影世界坐标得到的 `z` 恒为负，下一行 `if (screenPos.z < 0f) return;` 把每一条飘字都当成"目标在相机背后"丢弃了（实测敌人头顶投影 z=-83.44）。

**修复**：新增 `ResolveWorldCamera()`——优先 `Camera.main`（两套座位相机都打 MainCamera 标签，绑座后另一台被停用），取不到再退到"任意启用中的非 UI 的 Game 相机"；`OnDamage` 改用它投影。屏幕→Canvas 的换算沿用原 `ScreenToCanvasPosition`（层画布是 `ScreenSpaceCamera + CanvasScaler(Shrink,1920x1080)`，与原实现假设一致）。

**验证**：派发 `E_OnDamage(66, 暴击)` 后 `DamageText` 激活且 `anchoredPosition≈(850,658)` 逐帧上飘到 (850,725) 并淡出；录屏 `screenshots/battle_damage_text_fixed2.mp4`（VLM 确认：橙色"66"在敌方角色头顶上方上飘淡出，无粉色/乱码）。

**顺带记录的环境坑**：编辑器在 `exec_runtime_script` 结束后常处于 paused，`WaitForSeconds`/YooAsset 异步会全部冻住——验证脚本一律用 `Time.realtimeSinceStartup` 轮询，长流程拆成"进场景"与"断言"两段。

---

## 24. P6-4 执行记录（2026-10-20，顿帧并入 PauseManager / 座位相机表 / 音效验收）

| 项 | 结果 |
|---|---|
| 顿帧与暂停 | `PauseManager` 新增 `hitStopScale` / `hitStopCount` / `PushHitStop` / `PopHitStop`，`Apply()` 统一结算：**暂停(0) > 顿帧(0.03) > 正常(savedTimeScale)**；`Player.HitStop` 改为 Push/Pop 引用计数（新顿帧接管时先退旧计数，避免泄漏永久慢放），UniTask 取消异常已捕获 |
| 修复的缺陷 | `Push()` 原来在「栈空」时就抄 `Time.timeScale` 当正常倍速；若暂停发生在顿帧期间会抄到 0.03，关面板后世界永远慢放。改为「栈空且无顿帧」才采样 |
| 相机硬编码 | 新增 `BattleCameraRig`（`Assets/Scripts/Battle/Presentation/Camera/`）：场景内序列化「座位 → 主相机 + 虚拟相机」；`Player.TryBindSceneCamera` 优先问它要相机，没有相机表时回退到旧的名字匹配（保 BattleTest.scene 兼容）。场景 `manager` 物体已挂并填入 2 个座位（Camera/thirdcamera、Camera (1)/thirdcamera (1)） |
| 音效 | 混音器与 `SoundData` 已在 P4-4 复制，`ActorAudioComponent` 引用自愈；实测本机英雄攻击时播放 `安比攻击1` 与 `安比：Ye（攻击）` 两条音效 |

**验证（Play，数据断言）**：

- 顿帧 → 暂停 → 顿帧结束 → 关暂停：`0.030 → 0.000 → 0.000 → 1.000`；
- 反向顺序（先暂停再顿帧，先关暂停）：`0.000 → 0.030 → 1.000`；
- 嵌套顿帧（0.05 + 0.02）：`0.020 → 1 pop 仍 0.020 → 2 pop 1.000`；
- `Player.HitStop` 连续两次接管：期间 `0.030` 且 `IsHitStopping=true`，0.5 秒后 `1.000`、`false`（无计数泄漏）；
- `BattleCameraRig.Instance` 非空，`TryGetSeat(0/1)` 分别返回 `Camera/thirdcamera`、`Camera (1)/thirdcamera (1)`，`seat=2` 返回 false；英雄绑座后 `thirdcamera.Follow=英雄`。

---

## 25. P7 验收记录（2026-10-20）

P7 的资源本体在 P4-2 已全量导入，本轮只做逐项核验：

| 检查项 | 安比 | 艾莲 |
|---|---|---|
| prefab / 骨骼 | `Assets/Resource/人物/安比.prefab`，158 个 Transform | `Assets/Resource/人物/le_Size02_Ellen_Ani_Idle (1).prefab`，299 个 Transform |
| 组件 | Animancer + NetworkObject/NetworkTransform + Player/HealthModel/BuffComponent/ActorAudioComponent（无 PlayerInput、无 CharacterInputSystem） | 同左（6 个 Renderer） |
| 材质 | 无缺 shader / 无粉红（战斗录屏视觉确认） | 6 个 Renderer、0 个坏材质 |
| 动画 | idle `Avatar_Female_Size02_Anbi_Ani_Idle`，攻击 `..._Attack_Normal_01` | 攻击 `shayuattack1` |
| 刀光骨骼 | `Anbi_Weapon_Bone01` | `Ellen_Weapon`（直接调用 `PlayAttackVFX` → active=1，父物体正确） |
| 音效 | 攻击音 + 角色语音各 1 条 | — |

---

## 26. P8-1 执行记录（2026-10-20，好友/聊天：抽预制体与框架视图）

**策略**：好友面板有 1400 行业务逻辑（好友列表 / 新的朋友 / 添加朋友 / 聊天 / 表情 / 输入光标 / 红点），重写风险太高。本轮用「逻辑复用 + 框架适配」：
`FriendUIController` 保留全部逻辑，只把基类从 `MonoBehaviour` 换成 `SkierFramework.UIView`、给 33 个 public 字段补 `[ControlBinding]`（运行期按字段名匹配 `UIControlData.name`）；新增薄适配类 `FriendView : FriendUIController` 负责 `OnOpen/OnClose` 与关闭键改走 `UIManager.Close`。

| 项 | 结果 |
|---|---|
| 视图脚本 | `Assets/Scripts/Friends/FriendUIController.cs`（基类 + `[ControlBinding]` + `ShowListPage/ResetChat` 改 protected + `OnCloseClicked` 改 protected virtual）、`Assets/Scripts/UI/Friends/FriendView.cs`（适配层 + `Open()` 静态入口） |
| 预制体 | `Assets/GameUI/Friends/FriendView.prefab`：wrapper（全屏 + `UIControlData` 33 条绑定 + `FriendView`）+ 原 `FriendPanel` 子树；**1.5 倍缩放补偿**——旧 `FriendCanvas` 是 ConstantPixelSize（1 单位=1px），框架层画布是 1920x1080 参考分辨率，按 1.5 缩放后 1920x1080 下实测面板 1050x930px、右对齐（等价于旧 1280x720 下的 700x620） |
| 注册 | `UIType.FriendView` + `UIConfig.json` 的 `FriendView → Assets/GameUI/Friends/FriendView.prefab`（NormalLayer） |
| 场景 | zhuye 未改动（抽取脚本临时改过层级，已 `git checkout` 还原；旧 `FriendPanel` 与旧控制器继续工作） |

**验证（Play，1920x1080）**：

- `UIManager.Open(UIType.FriendView)` → 视图 `FriendView(Clone)` 0.2s 内出现；`UIControlData` 绑定 33/33（closeButton / listPage / chatInput / sendButton / emojiPanel / friendListScroll / messageScroll 全部非空）。
- Canvas 走 `ScreenSpaceCamera + SkierUICamera`、`overrideSorting=true`、`sortingOrder=3030`，面板屏幕矩形 `(840,75)-(1890,1005)`。
- 交互：添加朋友页切换与返回、表情面板开合、关闭键 → `IsOpen=false` 全部正确。
- 录屏 `screenshots/friend_view_p8_1080p.mp4`（VLM 确认）：右侧双栏面板、左栏标题/绿色"+"、新的朋友条目、右栏聊天占位与输入行；添加朋友页输入框+查询按钮正常；无粉红/乱码/错位。
- 已知（迁移前就存在，非本次回归）：聊天输入框占位文案 `输入消息，Enter 发送 · Shift+Enter 换行` 在 195px 宽、fontSize 16 下 preferredWidth≈299，会折成两行并被 40px 高的输入框裁切；属文案/尺寸问题，待产品确认后改文案或缩字号。

**下一步（P8-1 收尾）**：

1. 把"登录即拉好友列表 + 未读/申请红点种子"从视图里拆出来（目前依赖面板实例存在），否则面板不打开时入口角标不亮。
2. 入口按钮 `FriendEntryButton` 改走 `UIManager.Open(UIType.FriendView)`。
3. 删除 zhuye 里的旧 `FriendPanel` 与 `FriendUIRoot` 上的 `FriendUIController`，回归列表/聊天/表情/输入光标/红点。

**收尾三步已完成（同轮）**：

| 项 | 结果 |
|---|---|
| 红点种子移出视图 | `ChatManager`（常驻数据层）新增订阅 `FriendListResponseReceived` / `FriendRequestsResponseReceived`：好友未读走 `EnsureFriendNode + SeedUnread`（`BeginBatch/EndBatch` 包裹），申请数写 `RedDotKeys.FriendRequests`；登录响应成功后直接发 `SendFriendListRequest + SendFriendRequestsRequest`。与视图内同名逻辑等价且幂等（`Set` 覆盖式） |
| 入口切换 | 新增通用 `OpenUIPanelButton`（挂在按钮上选 `UIType`）；zhuye 的 `FriendEntryButton` 已挂并设为 `FriendView` |
| 删除旧面板 | zhuye 删除 `FriendPanel`（12 个子物体）与 `FriendUIRoot` 上的 `FriendUIController` 组件；`FriendUIController` 里失效的 `entryButton/panelRoot` 字段、`OnEntryClicked` 与相关 `SetActive` 一并清理 |

**收尾验证**：

- 全新 Play 会话（好友面板从未打开过）：`FriendListResponseReceived` 只有 **1 个订阅者**（ChatManager）；模拟一条 `FriendInfo{PlayerId=9002, UnreadCount=5}` → 好友入口 Key14=**5**、好友节点=**5**；模拟 `FriendRequestsResponse.Count=2` → Key15=**2**。说明面板不打开入口角标也会亮。
- 点 `FriendEntryButton` → `FriendView` 打开（`IsOpen=true`）、33 项绑定正常、添加朋友页可切、关闭键 → `IsOpen=false`、再点入口复用同一实例重开。
- zhuye 剩余入口结构：`FriendEntryButton / ChallengeEntryButton / LeaderboardEntryButton / ChallengePanel / ChallengeUIRoot`（挑战与排行榜面板留给 P8-5）。

---

## 27. P8-2 背包：不迁移（定稿，保留原项目背包）

**结论（2026-10-20 用户定稿）**：背包**不做 SkierFramework 迁移**，保留原项目自己的背包实现——`zhuye` 的 `beibao` 画布 + `BagPanel` + `BagUIController`/`BagSlotView`（500 格虚拟列表、拖拽换位、使用/丢弃/整理、程序化深色主题）原样运行；**不使用 Compete 的背包**，`Assets/Scripts/Compete/bag/*` 仅作为移植过来的内核脚本留在工程里，不接 UI、不注册 `UIType`/`UIConfig`。

因此本轮对背包的所有尝试已**全部回退**（`BagUIController.cs` / `UIConfig.json` / `UIType.cs` / `zhuye.scene` 均还原，新增的 `Assets/Scripts/UI/Bag/`、`Assets/GameUI/Bag/` 已删除），当前工作区与 P8-1 提交一致，`UIConfig` 里没有 `Bag` 条目。P8 清单剩：任务、邮件、排行榜/挑战、副本 HUD、世界聊天、活动、阿福、入口栏。

**记录：尝试过程与实测现象**（仅作技术档案，后续若要重开需先解决）

- 跑通的：`BagUIController : UIView` + 18 个 `[ControlBinding]`、`BagView` 适配层、`UIControlData` 18/18 绑定、`UIConfig`/`UIType.BagView` 注册、视图开/关；数据侧 `content.children=11`、`activeCells=60`、`titleText='背包'`、`capacityText='0/0 格'` 全部正常。
- 没通过的（视觉）：只剩白底 + 左侧灰蓝块（SlotScroll 视口底）+ 分隔条 + 右侧占位方块与"使用/丢弃"按钮；标题/容量文字、格子边框网格、物品详情文字不可见。
- 试过三种尺寸策略均无效：固定 `1046x869 × 1.243`、横向拉伸 `1920x869 × 1.243`（两者都溢出屏幕）、不缩放直接铺满 `1920x1080`（数据全对但视觉仍缺内容）。
- 若将来真要迁移，先查两处：程序化背景瓦片/格子边框贴图的生成与赋值路径、标题/详情 TMP 在框架画布（ScreenSpaceCamera + CanvasScaler）下是否被 `UIView.OnInit` 的层级/Canvas 重设影响。

---

## 附录 A：源项目关键文件索引

| 文件 | 行数 | 作用 |
|---|---|---|
| `状态机/StateMachine.cs` | 20 | FSM 基类 |
| `状态机/PlayerMovementStateMachine.cs` | 56 | 移动状态机 |
| `状态机/playercombat/PlayerComboStateMachine.cs` | 32 | 连击状态机 |
| `状态机/PlayerMovementState.cs` | 209 | 移动基类 |
| `状态机/PlayerComboState.cs` | 166 | 连击基类 |
| `状态机/playercombat/CharacterCombo.cs` | 401 | 选招/命中/伤害 |
| `状态机/playercombat/PlayerATKIngState.cs` | 122 | 攻击段 |
| `状态机/playercombat/PlayerNullState.cs` | 87 | 待机起手 |
| `damage/DamageCalculator.cs` | 91 | 六阶段公式 |
| `buff/BuffComponent.cs` | 340 | Buff 调度/同步 |
| `ui/HealthModel.cs` | 324 | 血量/无敌/死亡 |
| `player/Player.cs` | 657 | 角色门面 |
| `network/RoomState.cs` | 486 | 房间裁判 |
| `network/BattleSpawnManager.cs` | 101 | 英雄生成 |
| `newui/room.cs` | 558 | 房间 UI |
| `Framework/UI/UIManager.cs` | 772 | UI 总调度 |
| `Framework/UI/UIViewBase/UIViewController.cs` | 198 | 视图控制器 |
| `Framework/UI/UIViewBase/UIView.cs` | 133 | 视图基类 |
| `Framework/UI/UIScrollView/UIScrollView.cs` | 495 | 虚拟列表 |
| `Framework/UI/UIModel/UIModelManager.cs` | 164 | 3D 预览 |
| `Framework/Resources/ResourceManager.cs` | 548 | 资源管理 |

## 附录 B：目标项目现状索引

| 区域 | 路径 | 说明 |
|---|---|---|
| 主场景 | `Assets/Scenes/zhuye.scene` | 9 个画布、入口按钮、现有业务 |
| UI 相机 | `Assets/Scripts/UI/UICameraService.cs` | ScreenSpaceCamera + Overlay Stack |
| 暂停 | `Assets/Scripts/Pause/PauseManager.cs` | 栈式 timeScale |
| 好友/聊天 | `Assets/Scripts/Friends/FriendUIController.cs` | 1180 行 |
| 背包 | `Assets/Scripts/Bag/BagUIController.cs` | 577 行 |
| 任务 | `Assets/Scripts/Quest/QuestUIController.cs` | 534 行 |
| 邮件 | `Assets/Scripts/Mail/MailUIController.cs` | 375 行 |
| 排行榜/挑战 | `Assets/Scripts/Challenge/ChallengeUIController.cs` | 311 行 |
| 副本 HUD | `Assets/Scripts/Dungeon/DungeonHud.cs` | 271 行 + 小地图/大地图 |
| 阿福 | `Assets/Scripts/Agent/AgentUIController.cs` | 967 行 |
| 活动 | `Assets/Scripts/Activity/*` + `Assets/HotContent/*` | Lua/YooAsset 热更 |

## 附录 C：术语

| 术语 | 含义 |
|---|---|
| Owner-Authoritative | 拥有者客户端驱动自己的表现/输入 |
| Server-Authoritative | 服务端结算数值/状态 |
| Phase Offset | 远程端按已播时长定位动画起点 |
| Hit-Stop | 命中顿帧（timeScale 压低） |
| I-Frame | 无敌窗口 |
| NamedMessage | NGO 无实体命名消息 |
| Listen Server | 房主进程同时是服务端 |
| UIControlData | 预制体控件绑定数据 |
| LuaOnlyView | 纯 Lua 热更 UI 的通用壳 |

## 附录 D：参考资料

- 源项目 `README.md`
- 源项目 `docs/战斗闭环与联机大厅_教学文档.md`（第 7 章缺失）
- 源项目 `Assets/script/Framework/README.md`（UI 框架使用文档）
- 目标项目 `Assets/Documents/Project_Engineering_Audit_Report.md`（工程审计）
- 目标项目 `Assets/Documents/副本小地图系统详解文档.md`（UI/相机约定先例）
- NGO 1.13 官方文档（NetworkVariable / RPC / SceneManagement / UnityTransport）
- UniTask / Animancer 7.4.2 / Cinemachine 2.10 文档
