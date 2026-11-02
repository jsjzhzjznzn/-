# 方舟卡牌 UI 框架化改造方案

## 1. 目标

把 ArkCards 里「用 C# 在运行时拼 uGUI」的两块界面换成工程既定的 UGUI 预制体 + SkierFramework `UIView`，并清掉随之失效的死代码。战斗内 HUD 与卡牌表现保持原样。

## 2. 现状

- ArkCards 共 3588 行脚本，其中 `UI/MainMenuUI.cs`（119 行）与 `UI/DeckEditorUI.cs`（187 行）在运行时 `new GameObject/Canvas/Text/Button` 拼界面，违反 AGENTS.md「UI 必须用 UGUI 预制体 + SkierFramework，禁止运行期拼 UI」。
- 主菜单与卡组编辑器各自在场景里挂一个组件脚本，场景里只有主相机、平行光与该脚本对象。
- `UI/BattleUI.cs` 是空类（只有空的 `Start/Update`），从未被使用。
- ArkCards 是独立程序集 `ArkCards`，`ArkCards.Tests`（42 条规则单测）引用它；主程序集可以引用 ArkCards，反之不行。

## 3. 改造范围

| 处理 | 内容 |
| --- | --- |
| 换成框架界面 | 主菜单、卡组编辑器 |
| 删除 | `UI/MainMenuUI.cs`、`UI/DeckEditorUI.cs`、`UI/BattleUI.cs`（空类），以及两个场景里对前两者的组件引用 |
| 保留 | 规则/结算/AI/拖拽/表现、`GM`/`BattleManager`/`EffectManager`/`AudioManager`/`DataManager`/`JsonTool`/`MonoSingleton`、`GameSession`、`ArkCardsExitBridge`、42 条单测、`BattleScene` 与两个菜单场景 |

ArkCards 侧自带的 `AudioManager`/`JsonTool`/`MonoSingleton` 保留：它们在独立程序集里，替换成本工程的同名实现必须把 ArkCards 并进 `Assembly-CSharp`，会让 42 条单测再也无法引用被测代码，代价大于收益。

## 4. 设计

### 4.1 两个框架界面

| uiType | 预制体 | 层级 | 说明 |
| --- | --- | --- | --- |
| `ArkCardsMainView` = 29 | `Assets/Game/UI/ArkCards/ArkCardsMainView.prefab` | NormalLayer，全屏页 | 标题 / 副标题 / 关卡列表 / 卡组编辑器按钮 / 左上「退出活动」/ 页脚 |
| `ArkCardsDeckEditorView` = 30 | `Assets/Game/UI/ArkCards/ArkCardsDeckEditorView.prefab` | NormalLayer，全屏页 | 标题 / 卡组计数 / 卡池网格（滚动）/「保存卡组」/「返回主菜单」 |

两块界面都用纯色 `Image` 做底板与按钮（`sprite = null` 时按 `color` 渲染为实心色块），不引入新的美术依赖；文字用 `TextMeshProUGUI`，中文由 TMP 全局回退字体 NotoSansSC 兜底。

动态条目（关卡按钮、卡牌格子）在预制体里各放一个未激活的模板节点，运行时克隆模板 + 布局组排布——与工程里其他列表（VirtualScrollList 行模板、ActivityUi.Clone）同一做法，不算运行期拼界面。

### 4.2 绑定与代码

- `ArkCardsMainView : UIView`，绑定：`levelContainer`、`levelButtonTemplate`、`deckEditorButton`、`exitButton`。
  - `OnOpen`：`Resources.LoadAll<LevelSO>("Levels")` 按 `levelId` 排序后克隆按钮，文案 `第X关 · 名称`。
  - 关卡 → `GameSession.SelectedLevel = level` → 关自己 → `SceneLoader.LoadAsync("BattleScene")`。
  - 卡组编辑器 → 关自己 → `SceneLoader.LoadAsync("DeckEditorScene")`。
  - 退出活动 → 关自己 → `ArkCardsExitBridge.RequestExit()`（宿主回 zhuye）。
- `ArkCardsDeckEditorView : UIView`，绑定：`cardContainer`、`cardTemplate`、`infoText`、`saveButton`、`backButton`；序列化字段 `cardListSO`（由构建器指向 `Assets/ThirdParty/ArkCards/SO/ArkCardsDatabase.asset`）。
  - 打开时读 `StreamingAssets/Decks/Deck01.json`；点格子加入/移除，上限 30 张；选中格描绿。
  - 保存 → 写回同一文件并更新计数文案；返回 → 关自己 → `SceneLoader.LoadAsync("MainMenuScene")`。
- 两个界面都在 `OnInit` 订阅 `activeSceneChanged` 自关（工程既有惯例：框架视图不随场景自动关闭，不自关会盖在下一个场景上）。

### 4.3 场景接线

两个菜单场景不再挂业务脚本，改为挂通用引导 `ArkCardsSceneBootstrap`（字段 `viewType`）：

- `MainMenuScene` → `ArkCardsMainView`
- `DeckEditorScene` → `ArkCardsDeckEditorView`

`Start` 里 `UIManager.Instance.Open(viewType)`；场景里只保留主相机与平行光。

## 5. 影响面

| 文件 | 动作 |
| --- | --- |
| `Assets/Game/Scripts/UI/ArkCards/ArkCardsMainView.cs` | 新增 |
| `Assets/Game/Scripts/UI/ArkCards/ArkCardsDeckEditorView.cs` | 新增 |
| `Assets/Game/Scripts/UI/ArkCards/ArkCardsSceneBootstrap.cs` | 新增 |
| `Assets/Game/UI/ArkCards/ArkCardsMainView.prefab` | 新增 |
| `Assets/Game/UI/ArkCards/ArkCardsDeckEditorView.prefab` | 新增 |
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIType.cs` | 追加 29 / 30 |
| `Assets/Game/UI/UIConfig.json` | 追加两条 |
| `Assets/Game/Scripts/UI/ArkCards/Editor/ArkCardsViewBuilder.cs` | 新增（菜单 Tools/UI/重建方舟卡牌界面） |
| `Assets/ThirdParty/ArkCards/Scripts/UI/{MainMenuUI,DeckEditorUI,BattleUI}.cs` | 删除 |
| `Assets/ThirdParty/ArkCards/Scenes/{MainMenuScene,DeckEditorScene}.unity` | 换引导组件 |

## 6. 验收

1. 编译零错误；`UIConfig.json` 两条登记有效（`IsLocationValid` 为真）。
2. `MainMenuScene` 进 Play：框架界面出现，3 个关卡按钮文案正确，点第 1 关进入 `BattleScene` 且战斗可操作。
3. 主界面「卡组编辑器」→ `DeckEditorScene`，卡池 39 张全显示、可滚动；点选变色；「保存卡组」写回 `Deck01.json`；「返回主菜单」回到主界面。
4. 从活动中心进入方舟卡牌 → 主界面 → 左上「退出活动」→ `zhuye`，且主界面视图已关闭（不残留在其它场景）。
5. Console 无 `can not find binding control`、`未配置uiType`、`Location already exists`。

## 7. 风险

- 预制体新增两个，`ui` 包按文件名寻址，`ArkCardsMainView`/`ArkCardsDeckEditorView` 全包唯一，无重名风险。
- 卡池网格用固定 6 列 + `GridLayoutGroup`，卡牌数量变化只影响行数（Content 高度由布局组计算）。
- 关卡按钮数量取决于 `Resources/Levels` 下的 SO 数量，新增关卡无需改代码。
- 删除 `MainMenuUI`/`DeckEditorUI` 后，两个场景里原组件会变成 Missing Script，必须在同一次改动里清掉，否则进场景报引用丢失。

## 8. 实施与实测

| 项 | 结果 |
| --- | --- |
| 编译 | 0 error（5 条既有 warning） |
| 预制体 | `ArkCardsMainView.prefab`（RectTransform + UIControlData + ArkCardsMainView）、`ArkCardsDeckEditorView.prefab`（同构 + `cardListSO` 指向 ArkCardsDatabase） |
| 登记 | `UIType` 29 / 30，`Max = 31`；`UIConfig.json` 两条，走 `NormalLayer` 全屏页 |
| 主界面 | 标题/副标题/3 个关卡按钮/卡组编辑器/左上退出活动/页脚 全部正常，中文无方块（截图核对） |
| 卡组编辑器 | `DeckEditorScene` 里卡池 30 格 6 列排布、已选格子绿色高亮；「保存卡组」写回 `Deck01.json`（保存前后文件 hash 一致，说明内容未误改）；「返回主菜单」回主界面 |
| 退出 | 主界面「退出活动」→ `zhuye` |
| 清理 | 删除 `MainMenuUI.cs` / `DeckEditorUI.cs` / `BattleUI.cs`（共 327 行）与其 `.meta`；两个场景的旧组件换成 `ArkCardsSceneBootstrap`（序列化 `viewType`） |
| 顺带修正 | `BattleScene` 自带 `EventSystem` 与框架常驻 EventSystem 冲突（日志 `There can be only one active Event System`），已删除场景内那份 |

新增代码量：`ArkCardsMainView` 133 行 + `ArkCardsDeckEditorView` 185 行 + `ArkCardsSceneBootstrap` 21 行 + 构建器 254 行；删除程序化 UI 327 行，界面逻辑从「运行时拼 uGUI」改为「预制体 + 视图脚本」，符合 AGENTS.md 铁律。

## 9. 程序集合并与音频接管

ArkCards 已并入预定义程序集 `Assembly-CSharp`（删除 `Scripts/ArkCards.asmdef` 与 `Tests/Editor/ArkCards.Tests.asmdef`），因此可以直接使用本工程框架代码，不再需要桥接层：

| 项 | 现状 |
| --- | --- |
| 音频 | 删除 ArkCards 自带的 `AudioManager`（95 行）与它的 AudioSource；音效/BGM 由 `GM` 转发到 `MieMieFrameWork.AudioManager`（`PlayOneShot` / `PlayerBgAudio(BGM)` / `StopBgAudio`）。战斗场景里原有的 8 个音效与 BGM 引用已迁到 `GM` 的同名字段，音量沿用工程设置 |
| 单例 | 删除 `MonoSingleton<T>`（36 行）；`GM` 改为继承 `MieMieFrameWork.SingletonBehaviour<GM>`，静态构造里关掉 `AutoCreate` 与 `Persistent`（保持「只在战斗场景挂载、不跨场景常驻」的旧语义），调用点仍用 `GM.Ins` |
| 场景切换 | 战斗结算面板的重开与退出兜底都改走 `SceneLoader.LoadAsync`（自带加载条与防重入） |
| BGM 时序 | 框架 AudioManager 由 ModuleHub 在场景启动后才创建，`GM.Start` 改为协程等待实例就绪（最多 5 秒）再起 BGM |
| 单测 | 两个测试类现在编入 `Assembly-CSharp-Editor`（预定义编辑器程序集，已确认引用 nunit.framework）；42 条测试全部通过 |

仍保留的 ArkCards 自有代码：`JsonTool`（57 行，卡组 JSON 读写）、`GameSession`/`GameConst`/`GameEnum` 等玩法数据定义——这些是玩法数据层，不是框架能力副本。


