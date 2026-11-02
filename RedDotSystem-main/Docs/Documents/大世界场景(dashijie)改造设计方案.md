# 大世界场景（dashijie）改造设计方案

> 状态：已实施（2026-10-28，实测结论见 §8）
> 日期：2026-10-28（同日修订：**大世界本轮不接对话功能**；追加数字键切角色与 `thirdcamera.prefab` 相机）
> 范围：`duihua` → `dashijie` 场景改名、玩家换成 `安比dan`（复用玩家战斗组件）、大世界 HUD、热键面板、世界暂停、数字键切角色、场景存档白名单
> 不改动：任务/背包/邮件/成就/角色等面板内部实现、战斗数值与技能系统、协议字段
> **本轮不做**：大世界内的对话（含流萤交谈）、由对话派生的通用交互组件、对话正反打机位与对话存档续播。对话系统本体与相关资产**保留在工程里、本轮不接进大世界**。

---

## 0. 需求拆解

| # | 需求（用户原话） | 落地目标 |
|---|---|---|
| 1 | 对话场景改名为 `dashijie` | 场景资产改名，主页「开始冒险」进入新场景 |
| 2 | 主页点开始冒险直接进场景，默认生成 `安比大世界` 由玩家操控 | 场景预置 `安比大世界` 预制体实例，复用现有 `Player` 组件操控（位移由 KCC 驱动，见 `大世界角色KCC控制器接入方案.md`） |
| 3 | 在这个场景做「大世界」 | 保留大厅环境与流萤 NPC 陈设，作为大世界的一处可漫游区域；**本轮不接对话** |
| 4 | 按 `B` 出现背包面板 | 大世界 HUD 接管热键，直接打开框架面板 |
| 5 | HUD 仿照参考图，但不要攻击与移动相关图标，小地图也不做 | 左上任务追踪 + 右上功能图标行 + 底部角色血条 + 聊天入口（交互提示条随对话一起推迟） |
| 6 | 任务面板和主页任务面板一样，其它面板同理 | 全部复用 `UIType` 已注册视图，不新建业务面板 |
| 7 | 打开背包/设置等面板时，大世界暂停，但 UI 逻辑不暂停 | 复用 `PauseManager`：`Time.timeScale=0`，UI 用非缩放时间继续 |
| 8 | 摄像机用 `Assets/Game/Prefabs/Battle/Scene/thirdcamera.prefab`；切换角色时别忘了切换摄像机对象 | 场景放 `thirdcamera.prefab` 实例；世界内按 `1–4` 换角色，换人时把 vcam 的 `Follow/LookAt` 指向新角色 |

### 已确认的关键取舍

| 取舍 | 决定 |
|---|---|
| 角色控制 | 复用现有 `Player` 组件（Animancer 走跑动画、POV 相机、根运动），不新写控制器 |
| 战斗操作 | 保留鼠标左键攻击、右键闪避、Shift 冲刺等操作，只是不画攻击/技能/跳跃按钮 |
| 场景环境 | 沿用现有大厅（`未命名.fbx`），不生成户外地形；流萤 NPC 保留为陈设，**不挂交互、不接对话** |
| 热键全集 | 背包 `B`、任务 `Y`、角色 `C`、成就 `V`、邮件 `M`、好友 `G`、排行榜 `L`、世界聊天 `T`、设置 `Esc` |
| 交互键 | 本轮不启用。`F` 预留给后续宝箱/传送点；好友键仍用 `G`，避免以后接交互时再改一遍 |
| 交互实现 | 本轮不做通用交互组件；`WorldInteractable` 方案**推迟**，等真的要接宝箱/传送点/对话时再落地 |
| 相机 | 玩法机位固定用 `thirdcamera.prefab`，场景里不再保留 `CM_ThirdPerson`；`Camera` 主相机（CinemachineBrain）保留 |
| 世界切人 | 数字键 `1–4` 硬切实例（安比 / 艾莲），旧角色停用后销毁，相机只换跟随目标；不做溶解/渐隐过渡 |

---

## 1. 现状分析

### 1.1 场景 `Assets/Game/Scenes/dashijie.scene`

| 层级 | 内容 |
|---|---|
| `Camera` | 主相机 + `CinemachineBrain` + `AudioListener`（由原 `Main Camera` 改名） |
| `thirdcamera` | `Assets/Game/Prefabs/Battle/Scene/thirdcamera.prefab` 实例：`CinemachineVirtualCamera`（POV + FramingTransposer）+ `CinemachineInputProvider` + `Camera_ZoomController`（滚轮 1~6，默认 2.8）+ `CinemachineCollider`；`Follow/LookAt` 由 `Player` 与切人器在运行时绑到玩家 |
| `安比大世界` | `Assets/Content/Resource/人物/Real/安比大世界.prefab` 实例：位移由 KCC（`KinematicCharacterMotor + AnbiWorldKccDriver`）驱动，动画/连招/闪避/技能/血量仍由 `Player` 负责；出生点 `(0, 0.02, -6)`、朝向 identity、层 `player` |
| `DashijieRoot` | 场景入口：`DashijieSceneBootstrap` + `DashijiePlayerSwitcher` + `DashijieSceneSaveController` |
| `CM_DialogNPC` / `CM_DialogPlayer` | 对话正反打机位（由 `GenshinCameraDirector` 现场计算位置）。**不启用**，物体保留 |
| `未命名 (1)` | 室内大厅 FBX，240 个 `MeshCollider`，物体层为 `environment`（第 6 层）；FBX 自带相机已改名 `ModelCamera` 并停用（避免与 `Player` 按名绑定的 `Camera` 重名） |
| `DialogueRunner` / `GenshinDemoRoot` | 对话框架运行时与导演、相机导演。**整体停用**，物体保留 |
| `PostFX_Volume` | 叙事调色 profile `duihua_PostFX`（资产名不改） |
| `HallLights` / `CharacterFillLight` | 主光、角色补光 |

场景已在 Build Settings 中（改名走 `AssetDatabase.MoveAsset`，保留原 `duihua.scene` 的 guid）。

### 1.2 玩家预制体 `Assets/Content/Resource/人物/Real/安比大世界.prefab`

| 项 | 实测值 |
|---|---|
| 根组件 | `Player` + `AnimancerComponent` + `Animator` + `CharacterController`（h1.43 / r0.3 / center 0,0.8,0）+ `NetworkObject` + `HealthModel` + `BuffComponent` + `ActorAudioComponent` |
| 角色配置 | `Player.playerSO = Assets/Game/Scripts/playso/安比连招/Playeranbi.asset`（已配置，含移动/连招动画引用） |
| 地面检测 | `CharacterMoveControllerBase.whatIsGround = 64`，即第 6 层 `environment`，与大厅碰撞层一致 |
| 网络 | 继承 `NetworkBehaviour`；场景没有 `NetworkManager` 时 `IsSpawned=false`，`Player` 走单机分支（本地状态机 + 本地装备加成），与挑战场景同机制 |
| 根节点 | 层级为 `player`（第 3 层），缩放 1 |

### 1.3 `Player` 的相机绑定约定

- `Player.TryBindSceneCamera()` 在 `Start` 后每 0.5 秒重试，按名称查找：
  - 主相机 GameObject 名 `Camera`
  - 跟随虚拟相机 GameObject 名 `thirdcamera`
- 绑定后把 `vcam.Follow/LookAt` 指向角色根，并停用场景里其它 vcam 与其它主相机（UI 相机除外）。
- `Player.Start()` 会锁定并隐藏鼠标（`CursorLockMode.Locked`）。

### 1.4 暂停与输入

| 系统 | 现状 |
|---|---|
| `PauseManager` | 静态分层暂停栈，层非空即 `Time.timeScale=0`；`PausedChanged` 事件；顿帧与暂停共用同一结算口 |
| 现有层 | `Settings / Bag / Mail / Quest / Character / Achievement / Friend / Leaderboard / Chat / Cutscene / Other` |
| `CharacterInputSystem` | Input System 强类型输入；输入缓冲在 `timeScale<=0` 时拒绝写入；全部角色输入属性先过 `PlayerInputGate` |
| 光标 | `DashijieHudView` 自己派生（面板打开/暂停时释放显示，恢复后锁定隐藏） |

### 1.5 UI 框架

- 视图枚举 `SkierFramework.UIType`（当前 0~25），注册表 `Assets/Game/UI/UIConfig.json`。
- 视图是 `UIView` 子类，类名必须与 `UIType` 同名；预制体不挂 Canvas/CanvasScaler，由框架补。
- `UIManager.Open/Close` 是 `UniTask`；`UIViewAnim` 的补间默认吃缩放时间。
- `SettingsView` 自带暂停：`SettingsViewArgs{pauseWorld=true,showReturnHome=true}` 时在 `OnOpen` Push、`OnClose` Pop。

### 1.6 场景存档

- 客户端 `DashijieSceneSaveController : SceneSaveController`：位置/朝向 + POV 双轴 + 相机距离，`sceneName` 默认 `"dashijie"`；位置恢复先禁用 `CharacterController` 再落位。
- 服务端 `SceneSaveRepository.AllowedScenes = { "dashijie" }`，不在白名单内的场景名一律拒绝读写。
- 对话节点字段不再写入也不再读取（大世界不接对话）。

### 1.7 可复用的全局数据

- `QuestManager`：`RuntimeInitializeOnLoadMethod` 自举 + `DontDestroyOnLoad`，跨场景可读；`QuestsUpdated` 事件；`TopLevelConfigs`/`StateOf`/`DisplayProgress`。
- `HealthModel`：`CurrentHP`/`MaxHP` 是 `BindableProperty<float>`，带 `OnValueChanged`。
- 城市模型 `未命名 (1)` 的 240 个 `MeshCollider` 全在第 6 层 `environment`，既是地面也是墙体，与玩家 `whatIsGround` 对齐。场景不额外放隐形碰撞盒：可行走范围就是城市模型的真实地面与墙体。

---

## 2. 设计

### 2.1 场景改名与相机

- 用 `AssetDatabase.MoveAsset` 把 `Assets/Game/Scenes/duihua.scene` 改名为 `Assets/Game/Scenes/dashijie.scene`：保留原 GUID，场景内所有引用与 Build Settings 条目随之更新，不重建场景。
- 物体改名/替换（供 `Player` 按名绑定）：
  - `Main Camera` → `Camera`
  - 删除 `CM_ThirdPerson`，改放 `Assets/Game/Prefabs/Battle/Scene/thirdcamera.prefab` 实例，物体名 `thirdcamera`
  - 大厅 FBX 实例内置的同名 `Camera` 改名 `ModelCamera` 并停用（否则按名绑定会选错主相机）
  - 删除旧 demo HUD `GenshinMenuCanvas`
- 代码中的场景名字符串改为 `dashijie`：`HomeView` 的入口、`DashijieSceneSaveController.sceneName` 默认值、`SceneSaveController`/`SceneSaveService` 的参数注释。
- 后处理资产保持文件名 `duihua_PostFX.asset`（场景内引用不变），不为改名做无收益的资产迁移。
- 对话相关物体（`DialogueRunner`、`GenshinDemoRoot`、`CM_DialogNPC`/`CM_DialogPlayer`）**保留但不启用**：整体 `SetActive(false)`，不做资产删除，避免以后接对话时重建。

### 2.2 玩家

- 在场景根下放置 `安比大世界.prefab` 实例，位置 `(0, 0.02, -6)`、朝向 identity，层 `player`。只做实例级改动，不改预制体本体，避免影响挑战场景；该预制体的位移由 KCC 驱动（`KinematicCharacterMotor + AnbiWorldKccDriver`），`Player` 与状态机原样保留。
- 玩家行走、冲刺、攻击、闪避沿用 `Player` 既有状态机与 Animancer 动画；地面判定层（`whatIsGround=64`）已与大厅碰撞层一致，无需改动。
- 相机：`thirdcamera.prefab` 自带 POV + FramingTransposer + `Camera_ZoomController`（滚轮缩放）与碰撞体；`Player` 绑定后 `vcam.Follow/LookAt` 指向角色根。**不做正反打**：`CM_DialogNPC`/`CM_DialogPlayer` 保持禁用，相机全程只走 `thirdcamera`。
- 光标：`Player.Start()` 会锁鼠标；由 `DashijieHudView` 订阅 `PauseManager.PausedChanged` 派生指针状态——面板打开/暂停时释放并显示，恢复后重新锁定隐藏（与 `ChallengeHudView` 同款写法）。

### 2.3 输入门禁

新增 `PlayerInputGate`（静态、按来源记账，语义与 `PauseManager` 相同）：

- 来源：本轮只有 `WorldPause`（HUD 世界暂停占用）。`Dialogue` 来源**预留但不启用**（对话不接，留常量与接口即可）。
- `CharacterInputSystem` 的 `Movement/Look/dashHeld/dashPressed/Attack/Attack_Continue/HeavyAttack/HeavyAttack_Continue/Skill/Skill_Continue/ConsumeDefensePressed` 在门禁关闭时返回空值。
- 目的：暂停期间，鼠标点击 UI、按键不会打进角色状态机（`timeScale=0` 只能冻结动画，不能阻止输入沿触发状态切换）。

### 2.4 大世界 HUD

新增视图 `UIType.DashijieHudView = 26`（`Max = 27`），预制体 `Assets/Game/UI/Dashijie/DashijieHudView.prefab`，`NormalLayer`、`isWindow=false`，注册进 `UIConfig.json`。预制体由编辑器脚本 `Tools/UI/重建大世界 HUD` 生成，样式常量集中在构建脚本顶部。

布局（1920×1080 基准，锚点按屏幕四角）：

| 区域 | 内容 | 数据源 |
|---|---|---|
| 左上 | 任务追踪卡：菱形图标 + 任务名 + 目标行 + `进度/目标` | `QuestManager`（优先 `Processing` 的顶层任务，其次 `Finished`；`QuestsUpdated` 刷新） |
| 右上 | 功能图标行（9 个圆形按钮，圆内底部小字键位角标）：背包 B、任务 Y、角色 C、成就 V、邮件 M、好友 G、排行榜 L、世界聊天 T、设置 Esc | 点击走 `UIManager.Open`；图标为 AI 生成的白线稿（`Assets/Game/UI/Dashijie/Icons/dashijie_icon_*.png`），圆底用自绘的 `dashijie_circle.png`（深色实心 + 细亮边） |
| 底部中央 | 角色名 + `Lv.1` + 血条 + `当前/最大` 数值 + 数字键切人提示（`1 安比 · 2 艾莲`） | `Player` 根的 `HealthModel`（`CurrentHP/MaxHP.OnValueChanged`）；等级暂无数据源，固定展示 `Lv.1`；提示按 `BattleHeroCatalog` 拼接 |
| 左下 | 聊天气泡按钮 | 打开 `WorldChatView` |

明确不做：小地图、虚拟摇杆、攻击/技能/跳跃按钮、队伍列表、**世界交互提示条（随对话一起推迟）**。

显示规则：

- HUD 根节点常驻激活（热键与轮询都在 `Update` 里），隐藏用内容节点的 `CanvasGroup.alpha=0` + `blocksRaycasts=false`，不 `SetActive(false)`。
- 任一大世界面板打开时隐藏 HUD 内容；本轮没有 `DialogueView` 这条分支。
- 右上热键行与底部角色条各垫一层半透明深色底板：明亮天空/地面下纯文字与圆形按钮会糊掉。
- 字体复用工程内 `NotoSansSC` SDF（与其它面板一致），避免中文方块。

### 2.5 热键、面板与暂停

热键行为（`DashijieHudView.Update`，忽略 `Time.timeScale`）：

| 按键 | 面板 | 暂停层 |
|---|---|---|
| `B` | `BagView` | `PauseManager.Layer.Bag` |
| `Y` | `QuestView` | `Layer.Quest` |
| `C` | `CharacterView` | `Layer.Character`（新增） |
| `V` | `AchievementView` | `Layer.Achievement`（新增） |
| `M` | `MailView` | `Layer.Mail` |
| `G` | `FriendView` | `Layer.Friend`（新增） |
| `L` | `LeaderboardView` | `Layer.Leaderboard`（新增） |
| `T` | `WorldChatView` | `Layer.Chat`（新增） |
| `Esc` | 有面板 → 关当前面板；无面板 → `SettingsView` | `Layer.Settings`（由 `SettingsView` 自己 Push/Pop） |

开合规则：

1. 没有面板打开时，按键打开对应面板。
2. 同一按键在自己面板打开时关闭它。
3. 已有其它面板打开时，其余按键不响应（避免叠两层全屏面板）。
4. 打开/关闭用 `busy` 标志串行化，防止连点并发。
5. 面板开合时同步派生鼠标指针（释放/重新锁定），不再有第三方系统接管指针。

暂停时序（关键）：

- 打开：`await UIManager.Open(...)` **完成后再** Push 对应层。面板的开场动画吃缩放时间，先暂停会卡在首帧。
- 关闭：**先** Pop 该层，再 `await UIManager.Close(...)`。关闭动画同样吃缩放时间，等它播完再恢复会永久停在 0 倍速。
- `SettingsView` 用 `SettingsViewArgs{pauseWorld=true, showReturnHome=true}` 打开，暂停由它自己管理，HUD 不重复 Push。
- HUD 每 0.1 秒（非缩放时间）核对一次 `UIManager.IsOpen`：面板被自身关闭按钮关掉时，HUD 负责 Pop 并恢复 HUD 内容，保证暂停栈不泄漏。
- HUD 的 `OnClose`/场景卸载时，把 HUD 记账的所有暂停层 Pop 干净，避免把 `timeScale=0` 带进下一个场景。

### 2.6 交互与对话（本轮不做）

大世界本轮只做「可漫游 + HUD + 面板 + 暂停」，**不接任何交互物与对话**：

- 不做 `WorldInteractable`（`GenshinInteractionZone` 升级版）迁移；不做范围检测、不做 `F` 键提示条、不做 `InteractRequested` 事件。
- 不启用 `GenshinDialogDirector` / `GenshinCameraDirector` / `DialogueRunner`：场景里这三个物体 `SetActive(false)`，流萤只留模型且移除 `GenshinInteractionZone`（或该组件 `enabled=false`）。
- `DialogueView` 不参与大世界：不打开、不在 HUD 隐藏规则里出现。
- 后续要接交互时，按本节原方案补 `WorldInteractable` + 提示条 + `PlayerInputGate` 的 `Dialogue` 来源即可，HUD 布局与热键不需要重构（`F` 与交互提示条的位置此前已预留）。

### 2.7 场景存档

- `DialogueSceneSaveController` 改名为 `DashijieSceneSaveController`（保留 GUID），字段类型改为 `Player`：
  - 取根节点：`Player` 组件 Transform。
  - 位置恢复：`CharacterController.enabled=false` → `SetPositionAndRotation` → `enabled=true`（直接改 Transform 会被下一帧 `CharacterController.Move` 拉回）。
  - POV 与相机距离的读写保持不变（仍操作 `thirdcamera` 的 `CinemachinePOV` 与 `Camera_ZoomController`）。
  - **对话节点/变量/已触发选项三个字段不再序列化到存档**（大世界不接对话）；拉取到带对话字段的旧行时直接忽略。
- 服务端 `SceneSaveRepository.AllowedScenes` 改为 `{ "dashijie" }`。旧 `duihua` 存档行不做迁移，也不读取；改名后首次进入从默认出生点开始。

### 2.8 与主页和设置的衔接

- `HomeView` 的「开始冒险」改为加载 `dashijie`（先关 `HomeView`，再走 `SceneLoader.LoadAsync`）。
- `DashijieSceneBootstrap`（场景内挂载）在 `Start` 打开 `DashijieHudView`，`OnDestroy` 关闭它。
- 设置面板「返回主页」沿用现有流程：落一次存档 → 关面板（恢复时间）→ 加载 `zhuye`。

### 2.9 数字键切角色

- 键位：`1`–`4`（主键盘与小键盘），`charId = 键号 - 1`；候选来自 `DashijiePlayerSwitcher.heroPrefabs`（下标 = charId）：
  - `0` → `Assets/Content/Resource/人物/Real/安比大世界.prefab`（KCC 版本）
  - `1` → `Assets/Content/Resource/人物/le_Size02_Ellen_Ani_Idle (1).prefab`
  - 越界（当前 3/4 无预制体）或空槽直接忽略。
- 切换流程（`DashijiePlayerSwitcher.SwitchTo`）：
  1. 记录当前角色的位置与朝向；
  2. 旧角色 `SetActive(false)`（`Destroy` 在帧末执行，停用保证这一帧不会两个 `Player` 同时吃输入）；
  3. 在原位实例化目标预制体；
  4. 销毁旧角色；
  5. **把 `thirdcamera` 的 `Follow/LookAt` 指向新角色**（相机物体不替换），新角色 `Start` 随后把主相机写成自己的 `viewCamera`；
  6. 广播 `PlayerChanged`：HUD 重绑血条/名字，`DashijieSceneSaveController.BindPlayer` 换存档角色。
- 限制：面板打开或开合动画进行中不响应；按到当前角色不重建实例。
- 不做溶解/渐隐过渡：大世界换人是硬切（角色页 UI 预览的 shader 过渡是另一件事）。

---

## 3. 文件影响面

### 3.1 新增

| 文件 | 用途 |
|---|---|
| `Assets/Game/Scripts/World/DashijieHudView.cs` | 大世界 HUD 视图：热键、面板开合、暂停记账、任务追踪/血条、鼠标指针派生、数字键切人 |
| `Assets/Game/Scripts/World/DashijieSceneBootstrap.cs` | 场景生命周期内打开/关闭 HUD，换人时把新角色交给存档控制器 |
| `Assets/Game/Scripts/World/DashijiePlayerSwitcher.cs` | 世界角色切换：原位换预制体、销毁旧角色、重绑 `thirdcamera` 跟随目标 |
| `Assets/Game/Scripts/World/PlayerInputGate.cs` | 按来源记账的输入门禁（本轮只用 `WorldPause` 来源） |
| `Assets/Game/UI/Dashijie/DashijieHudView.prefab` | HUD 预制体（编辑器脚本生成） |
| `Assets/Game/UI/Dashijie/Icons/dashijie_icon_*.png` | 9 个功能图标 + 深色圆底（AI 精灵表 `Assets/TJGenerators/History/Sprite_20261028_170433.png` 按格切图、按 alpha 外接框归一化到 256×256；`dashijie_circle.png` 由编辑器脚本绘制） |
| `Assets/Game/Scripts/UI/Dashijie/Editor/DashijieHudViewBuilder.cs` | HUD 构建器（菜单 `Tools/UI/重建大世界 HUD`） |

### 3.2 修改

| 文件 | 改动 |
|---|---|
| `Assets/Game/Scenes/duihua.scene` → `dashijie.scene` | 改名；`Main Camera`→`Camera`；删 `CM_ThirdPerson` 与旧 `GenshinMenuCanvas`；放 `thirdcamera.prefab` 与 安比dan；新增 `DashijieRoot`（bootstrap / switcher / save controller）；停用 `DialogueRunner`、`GenshinDemoRoot`；FBX 内置相机改名停用 |
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIType.cs` | 新增 `DashijieHudView = 26`，`Max = 27` |
| `Assets/Game/UI/UIConfig.json` | 注册 `DashijieHudView` |
| `Assets/Game/Scripts/Pause/PauseManager.cs` | `Layer` 增加 `Character/Achievement/Friend/Leaderboard/Chat` |
| `Assets/Game/Scripts/Battle/Core/Input/CharacterInputSystem.cs` | 输入属性接 `PlayerInputGate` |
| `Assets/Game/Scripts/UI/Home/HomeView.cs` | 「开始冒险」加载 `dashijie` |
| `Assets/Game/Scripts/Dialogue/DialogueSceneSaveController.cs` → `DashijieSceneSaveController.cs` | 场景名、玩家类型、CharacterController 落位；去掉对话节点字段的读写 |
| `Assets/Game/Scripts/Save/SceneSaveController.cs`、`SceneSaveService.cs` | 白名单注释同步 |
| `../competeserves/Server/Database/SceneSaveRepository.cs` | `AllowedScenes = { "dashijie" }`（含表注释） |

### 3.3 保留不动（本轮不接进大世界）

- 对话系统本体（`DialogueRunner`/`DialogueViewModel`/对话图资产）、`DialogueView`、`GenshinCameraDirector`、`GenshinDialogDirector`、`DialogueCameraZoom`：文件与资产都不改，场景里只做「不启用」；大世界的滚轮缩放由 `thirdcamera.prefab` 自带的 `Camera_ZoomController` 提供。
- `GenshinInteractionZone`：文件保留，不再升级成 `WorldInteractable`（该迁移推迟）。
- 业务面板预制体与视图脚本（背包/任务/角色/成就/邮件/好友/排行榜/聊天/设置）。
- `DialoguePlayerController.cs`、`GenshinMenuController.cs`、`DialogueSceneMenu.cs` 文件保留，但场景内不再挂载。

---

## 4. 实施顺序

1. 编辑器脚本完成场景改名与物体改名，删 545 与旧 demo HUD，放 安比dan，挂 bootstrap；停用对话相关物体与交互区。
2. 代码层：`UIType`/`UIConfig`、`PauseManager.Layer`、`PlayerInputGate`、`CharacterInputSystem`、`HomeView`、存档控制器改造。
3. HUD：脚本 + 构建器 + 生成预制体 + 注册（含鼠标指针派生）。
4. 世界切人：`DashijiePlayerSwitcher`（场景两个世界预制体 + 相机跟随重绑）+ HUD 数字键分支。
5. 服务端白名单修改并重启编译。
6. 编译零错误后进 Play 模式按 §5 验收，最后把实测结论回填本文档（§8）。

---

## 5. E2E 验收项

| # | 场景 | 期望 |
|---|---|---|
| 1 | 主页点「开始冒险」 | 加载界面正常，进入 `dashijie`；场景内只有 安比大世界；角色站在 `(0, 0.02, -6)` |
| 2 | 玩法移动 | WASD 相机相对移动、鼠标转视角、Shift 冲刺、滚轮缩放；不穿地、不悬空、撞墙不抖 |
| 3 | 相机绑定 | Console 出现 `座位0: 主相机=Camera vcam=thirdcamera 已关联本角色`；无「没找到座位0的相机」重试日志 |
| 4 | 攻击/闪避 | 左键攻击、右键闪避、Shift 冲刺可用（无 UI 按钮），动画正常 |
| 5 | 无交互/无对话 | 场景内没有可交互 NPC；按 F 无反应、无正反打镜头切换；`DialogueView` 从未被打开 |
| 6 | 九个热键 | B/Y/C/V/M/G/L/T/Esc 分别打开正确面板；同键再按关闭；另一个面板开着时其它键无效 |
| 7 | 世界暂停 | 面板打开后 `Time.timeScale==0`：角色、动画、粒子、相机全静止；面板内滚动/点击/加载圆环继续走 |
| 8 | 暂停恢复 | 面板内关闭按钮关闭后 `Time.timeScale==1`，HUD 内容重新出现，角色可移动 |
| 9 | Esc 层级 | 面板打开时 Esc 关面板；无面板时 Esc 开设置；设置「返回主页」回 `zhuye` 且时间恢复正常 |
| 10 | 光标 | 玩法态锁定隐藏；面板打开/暂停时释放并可见；关闭后重新锁定隐藏 |
| 11 | 存档 | 移动后返回主页再进入，位置/朝向/相机角度恢复；存档里不再带对话节点字段 |
| 12 | 服务端 | 存档拉取/上报无「不支持的场景」；DB `player_scene_saves.scene_name = dashijie` |
| 13 | 断网启动 | HUD 任务卡显示「暂无进行中的任务」，血条正常，打开面板不报错、不卡死 |
| 14 | 分辨率 | 16:9 / 21:9 / 4:3 下 HUD 不越界、不遮挡血条 |
| 15 | 日志 | 全程 Console 无 error；无 TMP 缺字形警告；无对话框架相关报错 |
| 16 | 数字键切人 | `1`/`2` 在安比与艾莲之间切换：位置朝向保持、场上只有一个 Player、旧角色被销毁；`3`/`4` 无反应 |
| 17 | 切人后相机 | `thirdcamera.Follow/LookAt` 指向新角色，Console 出现新角色的座位相机绑定日志；HUD 名字/血条切到新角色 |

---

## 6. 风险与规避

| 风险 | 规避 |
|---|---|
| `Player` 在无 `NetworkManager` 的场景里运行 | 与挑战场景同机制，`IsSpawned=false` 走单机分支；进 Play 首轮重点看 NGO 日志 |
| 安比dan 的根运动与大厅 `MeshCollider` 台阶/斜坡不匹配 | 保留 `CharacterController` 的 `stepOffset=0.3`；首个 Play 轮实测上下台阶与撞墙 |
| 面板开场/关闭动画吃缩放时间，暂停时卡死 | 打开完再 Push、关闭前先 Pop（§2.5）；HUD 轮询兜底 |
| 面板用自身按钮关闭，HUD 不知道 | 0.1s 轮询 `IsOpen`，按面板状态对齐暂停栈与 HUD 显隐 |
| 暂停栈泄漏把 `timeScale=0` 带出场景 | HUD `OnClose` 与 bootstrap `OnDestroy` 强制清账；`PauseManager` 进 Play 复位 |
| 旧 `duihua` 存档与新场景不兼容 | 服务端白名单只留 `dashijie`，旧行不读取，首次进入走默认出生点 |
| 场景改名导致主页/存档/文档引用失效 | 用 `AssetDatabase.MoveAsset` 保留 GUID；全仓 `duihua` 字符串逐处替换并复查 |
| 场景里的对话导演没停干净，走到流萤被误触发 | 改名脚本里对 `GenshinDialogDirector`/`GenshinCameraDirector`/`DialogueRunner` 显式 `SetActive(false)`，并移除流萤的 `GenshinInteractionZone`；验收项 5 专门守这条 |
| 本轮无对话后没有系统接管鼠标指针 | 指针派生收进 `DashijieHudView`（订阅 `PausedChanged` + 面板开合），不再依赖导演 |
| 阿福悬浮球 `sortingOrder 5900` 盖住 HUD | 已知现象；本轮不改排序，验收时若遮挡明显再单独处理 |
| 21:9 / 4:3 下 HUD 偏移 | 全部锚点按四角/居中锚定并配同角 pivot；三种分辨率截图验收 |
| TMP 单行文案在行框略矮时整段消失 | `Ellipsis` + 行框高度小于字体行高时 TMP 会把字符全部裁掉（`m_isTextTruncated=true`）；HUD 固定文案统一用 `Overflow`，并给足行框高度（角色名 34、等级 24、血量 20） |
| 新增可切换角色后按键无反应 | `DashijiePlayerSwitcher.heroPrefabs` 是显式列表（下标=charId）；加角色必须同时补一个带 `Player`/`CharacterController` 的世界预制体，否则该数字键直接忽略 |
| FBX 场景自带同名相机 | 大厅模型内置 `Camera` 已改名 `ModelCamera` 并停用，避免 `Player` 按名绑定选到非主相机 |
| ui 包地址按文件名唯一，图标重名会整包构建失败 | HUD 图标统一加 `dashijie_` 前缀；`icon_bag` 等名字在 `Assets/Game/UI/Home/Art/Icons` 已存在，直接用会让 `EditorSimulateBuildPipeline` 抛 `Address already exists`、UI 框架整体起不来 |

---

## 7. 修订记录

| 日期 | 修订 | 影响 |
|---|---|---|
| 2026-10-28 | 初版方案确认 | 见 §0–§6 |
| 2026-10-28 | **大世界本轮不做对话功能** | §0 需求 3/5、取舍表的场景环境/交互键/交互实现；§1.1 场景物体状态；§1.4 光标来源；§1.6 存档字段；§2.1 对话物体不启用；§2.2 相机与光标；§2.3 输入门禁来源；§2.4 去掉交互提示条；§2.5 去掉「对话优先」规则；§2.6 由「通用交互组件」改写为「交互与对话（本轮不做）」；§2.7 存档去掉对话字段；§3 影响面移除 `WorldInteractable`/`GenshinDialogDirector` 改造；§4 实施顺序；§5 验收第 5 项改为「无交互/无对话」；§6 风险表增补两条 |
| 2026-10-28 | 同步 `新手引导系统设计方案.md` | §1.3 主行动入口与 §3 步骤 1（`mainCtaButton`）文案去掉「推进剧情与对话」 |
| 2026-10-28 | 追加数字键切角色 + `thirdcamera.prefab` 相机 | §0 范围；§1.1 场景层级；§2.1 相机替换与 FBX 同名相机；§2.2 相机来源；§2.4 切人提示与底板；新增 §2.9；§3 新增 `DashijiePlayerSwitcher.cs`；§5 验收 16/17；§6 风险 3 条；§8 实测结论 |

---

## 8. 实测结论（2026-10-28）

编译：

- 客户端 `unity_editor.refresh` 0 error（仅存量警告）；HUD 预制体由 `Tools/UI/重建大世界 HUD` 生成，`UIControlData` 绑定 21 项全部命中。
- 服务端 `dotnet build` 0 error；运行中的 `CompeteServer` 锁定 `bin` 下的 exe，本次用临时 `OutputPath` 验证。**白名单改动需重启服务端进程才生效**，本次未重启。

Play 实测（1920×1080，编辑器直接进 `dashijie`）：

| 项 | 结果 |
|---|---|
| 场景与玩家 | 只有 安比dan；出生 `(-0.01, -0.17, -6)` 稳定不下沉，脚下射线命中城市模型 `未命名 (1)/222_材質223`（y≈-0.10），场景内无隐形碰撞盒 |
| 大范围通行 | KCC 实测八方向各走 4 秒（4 m/s）均 ≥20 m 且全程贴地；向东越过原 `x=8` 边界到达 `x≈32`，向北被 `z≈-1` 处 1 m 高台挡住（真实场景几何，`MaxStepHeight=0.3`），向南/西各达 20 m 以上 |
| 相机绑定 | 日志 `座位0: 主相机=Camera vcam=thirdcamera 已关联本角色`；活动相机只有 `SkierUICamera` 与 `Camera`；`thirdcamera.Follow/LookAt` 指向玩家 |
| HUD | 任务卡「暂无进行中的任务」、角色名「安比」、`Lv.1`、`100/100`、绿色血条、`1 安比 · 2 艾莲` 提示均渲染 |
| 热键/暂停 | 点背包热键：面板打开、`timeScale=0`、HUD 内容隐藏、光标释放；再点一次：面板关闭、`timeScale=1`、HUD 恢复、光标锁定。设置面板同样暂停并隐藏 HUD |
| 输入门禁 | 暂停期间 `Movement=(0,0)`、`Attack=false`、`dashHeld=false` |
| 切角色 | 安比 → 艾莲 → 安比：场上始终只有 1 个 `Player`，旧实例被销毁；`thirdcamera.Follow/LookAt` 跟随新角色并出现新角色的绑定日志；HUD 名字/血条同步切换 |
| 存档快照 | `scene=dashijie`，位置/朝向/POV/距离均正确；`ApplyPosition` 后位置生效（未被 `CharacterController` 拉回），`ApplyCamera` 后 yaw/pitch/距离立即写入 |
| 分辨率 | 16:9 / 21:9 / 4:3 截图：任务卡、热键行、角色条、聊天入口均不越界、不互相遮挡 |
| HUD 图标 | 9 个热键与左下聊天入口显示 AI 白线稿图标（深色实心圆底 + 细亮边），键位角标在圆内底部；图标按钮点击仍能正常开合面板（背包实测暂停/恢复一致） |

未实测（环境限制）：

- 键位合成：工程用旧 Input Manager，`Input.GetKeyDown` 无法在自动化里合成；热键/数字键的**处理逻辑**通过按钮 `onClick` 与 `SwitchTo` 直接调用验证，键位映射本身待手动按一遍。
- 攻击/闪避的真实按键与动画：只验证了输入门禁与状态机初始化。
- 登录态下的服务端存档读写、从主页「开始冒险」进入的完整链路：本机未登录，服务端仍是旧进程（白名单未生效）。
