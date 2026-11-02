# 方舟卡牌活动 Lua 化（照抄 BattleScene 到主页场景）设计方案

## 1. 目标

在主页场景 `zhuye` 里还原 `Assets/ThirdParty/ArkCards/Scenes/BattleScene.unity` 的**原版 3D 牌桌效果**：牌桌预制体、立绘与音效随客户端发布，玩法逻辑、演出节奏与卡池/卡组数值放在活动热更包，改玩法不用发版。

- 活动页点「开始战斗」→ 出现加载界面 → 世界与卡牌实例化完成 → 进入战斗。
- 战斗期间收起主页 UI；退出后原样恢复主页界面与相机。
- 不再有选关、主菜单、卡组编辑器：固定卡组、固定对手，配置放热更包 JSON。
- 撤下热更包后客户端只剩牌桌视觉资源与通用外壳，活动入口点开不再出牌桌。

## 2. 照抄清单（实测自 BattleScene）

| 元素 | 原始参数（照抄依据） |
| --- | --- |
| 相机 | 位置 `(0,10,-2.5)`、旋转 `(80,0,0)`、透视 FOV 60 —— 斜俯视牌桌 |
| 桌面底 | `Scene/Plane` 缩放 `(2,1,1)`；`Scene/BackGround` 旋转 `(90,0,0)`、Sprite `CardGameUI_2`、sortingOrder 0 |
| 战场框 | `Field1` / `Field2`（SpriteRenderer + BoxCollider，白描边卡片框） |
| 手牌区 | `hand` 根节点旋转 `(80,0,0)`，手牌逐个带轻微倾斜形成扇形 |
| 卡牌 | `Card.prefab`：`Front`（描边 `CardGameUI_1` order10、卡面 `CardGameUI_2` order1、立绘 `立绘_xxx` order2、名称/效果面板 `CardGameUI_10`、费用宝石 `CardGameUI_19`、攻击/生命图标 `CardGameUI_14` + TMP 文本、`Mask` 裁立绘）、`CardBack`（卡背 + 罗德岛 Logo order25） |
| 角标 | 费用（蓝宝石、左上）、攻击（红）、生命（绿）、玩家血量徽章（绿圆 30） |
| 顶部横幅 | 5 面白旗的棕色横幅（敌方牌库指示） |
| 牌库 | 左右两张卡背堆 + 敌方/我方计数 `1/1`、`2/2` |
| HUD | `Canvas`：回合结束按钮、WinPanel、TurnPanel（这些在 Lua 版里用 uGUI 重建，不再用场景 Canvas） |

照抄方式：把上述**视觉层级**（SpriteRenderer + TMP，去掉 `CardController`/`CardDisplay`/`GM` 等 C# 组件）做成一个热更包预制体 `arkcards_stage.prefab`，其中卡牌结构做成 `CardTemplate` 模板；运行时由 Lua 克隆模板、填充数据。

## 3. 架构

```
activity 热更包（可整包撤下）
├── lua/arkcards.battle.lua.txt   战斗入口：界面/生命周期/指针/规则/演出
├── lua/activity_main.lua.txt     活动中心入口，type=4 页「开始战斗」
└── config/cards.json / battle.json  卡池、双方卡组、初始数值
客户端常驻（Assets/Game/Resources/Activity/arkcards）
├── prefabs/arkcards_battle.prefab 牌桌 + 卡牌模板（照抄 BattleScene）
├── art/…                         卡框、卡面、费用宝石、攻防图标、立绘、卡背、Logo、牌桌材质
└── audio/…                       音效与 BGM
客户端通用外壳（与玩法无关，保留）
├── ActivitySceneViewport  舞台放到场景远端、接管主相机、收起其它画布、屏幕坐标拾取、卡牌克隆、原样还原
├── ActivityDragProbe      全屏输入探针（屏幕坐标事件，Lua 每帧轮询；挂在战斗 HUD 画布下）
├── LuaTween               DOTween 静态封装（位移/缩放/透明度/界面位移）
└── LuaActivityHost        预制体/贴图/音频/文本加载（先取客户端 Resources，再回落热更包；关面板统一释放）
```

要点：

- **资源归属**：牌桌预制体、立绘、音效随客户端发布，改画面直接改工程资源；规则、演出节奏与卡池/卡组数值在热更包，改玩法逻辑与数值才需要构建发布活动包。

- **不建独立场景**：舞台预制体实例化在 `zhuye` 的远端锚点 `(0,3000,0)`，主相机被临时挪到牌桌视角，cullingMask 只留 `ActivityStage` 层；UI Overlay 栈照常渲染 HUD。
- **清 UI / 恢复 UI**：进入战斗时收起活动面板自身的侧栏与内容页、以及其它画布（`Canvas.enabled=false`）；退出时逐个恢复，相机的位置/旋转/清屏/裁剪/视场逐项还原（实测相机已能精确还原）。
- **加载界面**：点「开始战斗」立即显示加载遮罩（活动内轻量提示 + 进度），然后实例化舞台、克隆手牌、初始化规则，再淡入战斗界面。
- **纯 Lua 逻辑**：规则、效果结算、AI 全部在 Lua；C# 只保留上述通用外壳，不出现任何卡牌玩法代码。

## 4. 与旧实现的差异

| 旧（C# 版） | 新（Lua 版） |
| --- | --- |
| 独立场景 `BattleScene` + 3 个 MonoBehaviour 管理器 | 主页场景内实例化的热更预制体 + Lua 模块 |
| `CardController`/`CardDisplay`/`PlayerController` 驱动表现 | Lua 持有卡牌数据表，克隆模板后填 Sprite/TMP/角标 |
| `DragManager`/`TargetManager` 用 `Input.mousePosition` + 物理射线 | `ActivityDragProbe` 屏幕坐标 → 主相机射线，规则判定在 Lua |
| `EffectManager` 用 DOTween 演算 | Lua 调 `LuaTween`，节奏由 Lua 队列控制 |
| 卡池在 `ArkCardsDatabase.asset`、卡组在 `StreamingAssets` | `cards.json` / `battle.json` 在热更包，改数值不用发版 |

## 5. 实施阶段

1. **舞台照抄**：从 BattleScene/Card.prefab 提取视觉层级与精灵，生成 `arkcards_battle.prefab`（含 `CardTemplate`），预制体与其精灵、音频一起放客户端 `Assets/Game/Resources/Activity/arkcards`。
2. **加载与进场**：点开始 → 加载遮罩 → 实例化舞台/手牌 → 淡入；退出还原 UI 与相机（相机还原与 UI 收起已验证）。
3. **核心对战**：状态机 + 规则 + 基础效果（召唤/攻击/法术伤害/治疗/死亡/抽牌）+ AI，能打完整一局并结算。
4. **表现补齐**：扇形手牌排布与拖拽手感、抽牌/召唤/攻击动画、伤害飘字、音效、回合提示。
5. **效果对齐**：按原版 `EffectResolver` 逐条补齐剩余效果分支。
6. **清理**：删除 C# 版（BattleScene、MainMenuScene、DeckEditorScene、ArkCards 全部 C# 脚本与编辑器构建器）、移出旧素材、更新 Build Settings。

## 6. 验收

1. 活动页点「开始战斗」出现加载界面，完成后进入与旧版一致的 3D 牌桌（斜俯视、白描边战场框、扇形手牌、费用/攻防角标、血量徽章、牌库计数）。
2. 手牌可拖拽出牌，落点判定正确；召唤、攻击、法术、死亡、抽牌、回合结束可用。
3. 能打到胜负结算并再来一局。
4. 退出战斗后：相机位置/视角恢复、主页 UI 全部还原、无残留物体（`ArkCardsBattle` / `ActivitySceneRoot` 均已销毁）。
5. 改 `cards.json` / `battle.json` 后重开活动即生效，不用重新发包。
6. 删除热更包内的 `arkcards` 内容与活动条目后，客户端无任何残留；Build Settings 不再含卡牌场景。
7. Console 无 `can not find binding control`、无 YooAsset location 报错；反复进出战斗相机与画布状态稳定。

## 7. 风险

- **卡牌数量与性能**：每张卡是多层 SpriteRenderer + TMP，手牌上限 10 张、场上各 7 个，总量可控；但 TMP 文本对象较多，需控制重建频率（只在数值变化时改文本）。
- **相机接管**：主页相机若被 Cinemachine 或其它脚本驱动，必须在战斗期间禁用并在退出时恢复（当前实现会停掉非 Overlay 的其它相机，需实测没有残留驱动）。
- **UI 收起范围**：`UIManager` 的各层画布、阿福悬浮球、成就 toast 等都需收起并恢复，遗漏会盖在牌桌上。
- **素材体积**：立绘与音频搬进热更包会显著增大包体，首次下载时间变长（见待确认项）。
- **效果分支工作量**：原版 `EffectResolver` 数十条效果需要在 Lua 里逐条对齐，允许首版先覆盖常用效果并记录差异。

## 8. 实施与实测（最终形态）

| 项 | 结果 |
| --- | --- |
| 形态 | 客户端常驻通用外壳与牌桌视觉（`Resources/Activity/arkcards` 下的 `arkcards_battle.prefab` + 立绘 + 音效）；战斗逻辑与配置来自活动热更包（`arkcards.battle.lua.txt` + `cards.json`/`battle.json`） |
| 入口 | 主页「活动」→ 侧栏「方舟卡牌」→「开始战斗」：加载遮罩（工程 `LoadingScreen`，最短 0.35 秒）→ 牌桌淡入 |
| 画面 | 与 BattleScene 直接 Play 一致：斜俯视牌桌、白描边战场、顶部 5 张卡背扇形、两侧牌库堆、双方头像与血量、`1/1` `2/2` 计数、回合结束按钮、扇形手牌（真实立绘/名称/费用/攻防/描述）。预制体里每个 SpriteRenderer / SpriteMask 与牌桌材质都指向客户端 `Activity/arkcards/art` 内的同名资源（`CardGameUI.png` 图集按 sprite 名对应，牌桌用 `Universal Render Pipeline/Unlit` 的 `table_plane.mat`） |
| 交互 | 输入探针由 `ActivitySceneViewport.EnsureInputProbe()` 建在战斗 HUD 画布下（`Canvas` 首个子节点，Canvas 补 `GraphicRaycaster`），活动面板画布在战斗期间 `enabled=false`，探针不放在那里。按下→拖动→松手的屏幕坐标经主相机射线拾取卡牌碰撞体，Lua 判定落点（手牌拖到己方半场即出牌，场上卡拖到敌方卡上即攻击） |
| 规则 | 费用每回合 +1（上限 10）、血量 30、场上 7 / 手牌 10、开局手牌 4；召唤校验费用与场位；法术按 effects 结算 Damage/Heal/Draw；攻击对拼、嘲讽优先；回合结束 → 敌方 AI → 回玩家回合并抽 1 张 |
| 演出 | 照抄原版（`D:\tuanjieproject\ArkCards\ArkCards\Assets\Scripts`）的时间轴：手牌/场上重排是 `DOLocalMove`+`DOLocalRotate` 0.5s；抽牌从牌库节点滑进槽位（0.5s）；悬停放大 1.4/0.3s 并把 sortingOrder 提前 10；拖拽保持抓取点偏移跟手；场上卡攻击时卡不动、瞄准图标跟手；攻击是 `DOMove(目标, 0.4)+Yoyo+InExpo`（冲过去再弹回），命中时刻出飘字；法术先飞到发动位 `spellPos (4.68, 4.75, -0.98)` 0.5s，结算后 0.5s 滑进己方墓地堆；阵亡牌同样滑进墓地堆并摆正；费用数字变化时 `DOScale(1.5, 0.3)+Yoyo`；回合提示从右侧 100px 滑入 1s；飘字 4pt、`+Y 2/0.8s OutQuad` 同时 0.8s 淡出 |
| 弃牌堆 | 阵亡与已结算的法术牌挂到 `player/grave` / `player2/grave`（本地 (8.69,0,-1.8) 与 (8.31,0,-1.3)，与原版场景一致），面朝上堆在牌桌右/左侧、牌库旁边，不留在桌面中央 |
| 结算 | 敌方血量归零弹 WinPanel「我方获胜」（失败为「战斗失败」）：「重新开始」重开一局，「退出」关战斗并收起活动面板回到主页 |
| 清理 | `Assets/ThirdParty/ArkCards`、`Assets/StreamingAssets/Decks`、两个 ArkCards 框架视图与构建器、`ArkCardsActivityBridge` 全部删除；Build Settings 只剩 6 个正式场景；UIConfig 去掉两条 |
| 发布 | 活动热更包 `1.4.4`（17 个补丁文件，只含 Lua 与配置），HostPlay 模式生效 |
| 资源 | 牌桌预制体、立绘、音效在客户端 `Assets/Game/Resources/Activity/arkcards`；`LuaActivityHost` 先查客户端 Resources（`Activity/arkcards/{prefabs,art,audio}/`、`Activity/`），没有才回落热更包 |
| 实测 | 悬停 0.3 秒内 1.00→1.40、移开 0.26 秒回到 1.0；抽牌从牌库滑到槽位并带动其余手牌重排；攻击冲撞 0.4s 过去 + 0.4s 回位；法术 0.4s 时在发动位 (4.48,4.69,-0.90)、2 秒后落在 `player/grave`（世界 (8.69,3000,-1.8)，屏幕 (1775,442)，面朝上、桌外右侧）；视觉迁到客户端后 195 个 SpriteRenderer 全部有图，退出后牌桌/输入区/场景根全部销毁、画布逐个恢复 |

已知差异：法术只覆盖常用效果分支（未实现的效果写日志）；原版效果队列是「每 1 秒结算一个效果、发牌卡先绕 Z 轴 20° 摆动 0.3s」，当前实现为同步结算。
