# ArkCards 移植方案

## 1. 目标与结论

把 `D:\tuanjieproject\ArkCards`（明日方舟主题回合制卡牌游戏）整体搬进本工程，作为**独立可玩的子工程**存在，不介入现有登录 / 主页 / 大世界 / 挑战流程。

两侧同为 Tuanjie 1.9.3（m_EditorVersion 2022.3.62t11），可以直接按资产拷贝移植，不需要工程转换。ArkCards 的入口是自带场景 `MainMenuScene`，三个场景加入 Build Settings 后即可用 `SceneManager.LoadScene` 按名加载。

## 2. 源工程现状

- 玩法：回合制卡牌对战（类炉石），3D 场景 + 2D 精灵，透视相机 + `Physics.Raycast`（非 Physics2D），演出走 DOTween。
- 架构：`GM` 门面 → `BattleManager` 编排 → `EffectManager` 效果队列 → `EffectResolver`（纯结算）/ `BattleRules`（纯规则）/ `AIScorer`（效用打分）。
- 资产：脚本 31 个（`Assets/Scripts`，含 `ArkCards.asmdef`）、场景 4 个、卡牌预制体 1 个、卡池 SO 1 个、关卡 SO 3 个、立绘精灵 73 张、音频 34 个、卡组 JSON 2 个、EditMode 测试 42 条。
- 数据：卡池在 `Assets/SO/ArkCardsDatabase.asset`；关卡走 `Resources.LoadAll<LevelSO>("Levels")`；卡组走 `Application.streamingAssetsPath/Decks/Deck0X.json`。
- TMP：ArkCards 自带一套 "Text Mesh Pro" 模板资源与 `Assets/Codely/Fonts` 字体；其 TMP Settings 把 NotoSansSC 设为全局 fallback。

## 3. 目标结构

统一放在一个新建目录下，与本工程既有资产零交错：

```
Assets/ThirdParty/ArkCards/
├── Scripts/            (31 个 .cs + ArkCards.asmdef)
├── Scenes/             (MainMenuScene / BattleScene / DeckEditorScene)
├── Prefabs/Card.prefab
├── Sprites/            (73 张精灵，含中文目录「明日方舟立绘」)
├── SO/ArkCardsDatabase.asset
├── Audio/              (34 个音效与 BGM)
├── Resources/Levels/   (Level01-03.asset，资源路径保持 "Levels/xxx")
└── Tests/Editor/       (ArkCards.Tests.asmdef + BattleRulesTests / EffectResolverTests)

Assets/StreamingAssets/Decks/   (Deck01.json、Deck02.json)
```

Unity 只把 `Assets/StreamingAssets` 这一个路径当作流目录，放在子目录里 `Application.streamingAssetsPath` 读不到，所以卡组 JSON 留在 `Assets/StreamingAssets/Decks`（本工程原本没有 `Assets/StreamingAssets`，新建后只有这一个 `Decks` 子目录）。`Resources/Levels` 放在 ArkCards 目录内，`Resources.LoadAll<LevelSO>("Levels")` 的路径不变。

## 4. 不搬的内容

ArkCards 自带的三块模板资产与本工程现有资产 GUID 完全重叠（共 63 条重叠，全部落在这三块），复制会造成程序集重复定义与资源寻址打架，因此不搬：

| 不搬 | 理由 |
| --- | --- |
| `Assets/Plugins/Demigiant`（DOTween.dll + Modules） | 本工程已有 `Assets/ThirdParty/Plugins/Demigiant/DOTween`（含 Pro），同版本同 GUID；重复会出现两份 `DOTween` / `DOTweenModuleUI` 类型 |
| `Assets/TextMesh Pro`（模板资源 + TMP Settings） | 本工程已有 `Assets/ThirdParty/TextMeshPro`，`LiberationSans SDF`、TMP 着色器等模板 GUID 相同，引用会自动解析过去；且 `Resources.Load("TMP Settings")` 同名会随机取一个，破坏本工程 TMP 配置 |
| `Assets/Resources/DOTweenSettings.asset` | 本工程已有 `Assets/Game/Resources/DOTweenSettings.asset`，资源名 `DOTweenSettings` 同名会冲突 |
| `Assets/Codely/Fonts`（NotoSansSC，16MB） | ArkCards 内无任何场景 / 预制体 / SO 引用它（仅其自带的 TMP Settings 引用），字体改由本工程 `Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset` 提供 |
| `Assets/Font`（思源黑体，40MB） | 按决定改用本工程字体，避免仓库再涨 40MB |
| `Assets/Scenes/SampleScene.unity` | 空模板场景，未被任何脚本加载 |
| `Library/`、`obj/`、`Logs/`、`.vs/`、`.idea/`、`.plastic/` | 非资产目录 |

## 5. 需要修改的既有内容

改动只有两处，且都是追加式：

1. **字体引用**：ArkCards 的 `BattleScene.unity` + `Card.prefab` 里 TMP 用的是思源黑体（guid `51ad3aaa…`），`MainMenuScene.unity` + `DeckEditorScene.unity` 的 `MainMenuUI.font` / `DeckEditorUI.font` 指向 guid `29f85468…`（该资产在 ArkCards 里已缺失）。移植后用编辑器脚本把这几处统一设为 `Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset`，场景内所有 `TMP_Text` 一并设置，保证中文不出方块。用编辑器 API 赋值而不是手改 YAML，避免 Tuanjie 加密 guid 的引用格式问题。
2. **Build Settings**：在现有 6 个场景之后追加 `Assets/ThirdParty/ArkCards/Scenes/MainMenuScene.unity`、`BattleScene.unity`、`DeckEditorScene.unity`。ArkCards 内部用 `SceneManager.LoadScene("BattleScene")` 按名加载，必须入表。

除此之外不修改任何现有脚本、场景、预制体、包清单与工程设置。

## 6. 兼容性核对

- **输入**：本工程 `activeInputHandler: 2`（新旧输入都开），ArkCards 使用旧版 `Input.GetMouseButton*` / `Input.mousePosition`，可直接工作。
- **渲染管线**：ArkCards 是 Built-in 管线工程，但只用 `SpriteRenderer` + `Sprites/Default`；URP 下可正常渲染，相机缺 `UniversalAdditionalCameraData` 时 URP 会自动补。
- **程序集**：`ArkCards.asmdef` 引用 `Unity.TextMeshPro`（本工程内嵌 `Packages/com.unity.textmeshpro` 提供同名程序集）；DOTween.dll、Newtonsoft.Json.dll 为预编译程序集，自动可见，无需改 asmdef。
- **命名冲突**：ArkCards 原来把 `UIManager`、`AudioManager` 等类放在全局命名空间，会遮蔽本工程 `using SkierFramework;` / `using MieMieFrameWork;` 引入的同名类型（全局命名空间优先于 using 指令）。因此 32 个脚本统一包进 `namespace ArkCards`，场景与预制体通过 guid 引用 MonoScript，改命名空间不影响引用。
- **TMP 版本**：本工程内嵌 TMP 是修过材质销毁问题的内嵌包，`TMP_FontAsset` / `TMP_Text` API 与 ArkCards 所用 3.0.9 一致。
- **坐标系与物理**：ArkCards 用 3D 碰撞体 + `Physics.Raycast`，与本工程 3D 物理设置一致，不依赖自定义 Layer / Tag（场景里只有 `MainCamera` 与 `Untagged`）。
- **中文资源路径**：立绘目录与文件名含中文，git 与本工程 YooAsset 收集范围（仅 `Assets/Game/UI`）都不受影响。

## 7. 实施步骤

1. 建目录，按第 3 节清单复制资产与 `.meta`（保留原始 guid，场景与预制体引用不变）。
2. `unity_editor.refresh` 一次，读 Console 编译错误（重点 `CS0433` 重复类型、`CS0246` 找不到类型）。
3. 编辑器脚本改字体引用（BattleScene / Card.prefab / MainMenuScene / DeckEditorScene）并保存。
4. 编辑器脚本追加 Build Settings 三个场景。
5. 按第 8 节逐项验证，Play 走通主菜单 → 对战。

## 8. 验收项

1. refresh 后 Console 无编译错误，`ArkCards` 程序集编译通过。
2. `MainMenuScene`：标题「方舟卡牌 ARK CARDS」中文正常显示（非方块），关卡按钮可点，「开始游戏」进入 `BattleScene`。
3. `BattleScene`：手牌显示立绘与攻防数值，拖拽出牌、召唤、攻击结算正常，伤害飘字中文与数字正常，BGM 播放。
4. `DeckEditorScene`：卡组编辑器能读到 `StreamingAssets/Decks/Deck01.json`，保存后文件内容变化。
5. Build Settings 中 3 个场景存在，`SceneManager.LoadScene` 按名加载成功（点按钮跳转即可证）。
6. EditMode 测试 42 条全绿（TestRunnerApi）。

## 9. 风险与回滚

- **DOTween 行为差异**：本工程 DOTween 与 ArkCards 自带版本均为 1.x 同代；若个别 API（如模块扩展方法）在编译期缺失，会直接报 `CS1061`，按报错补 asmdef 引用或改用本工程已有实现，不动 DOTween 本体。
- **场景里残留的旧引用**：ArkCards 内 `MainMenuUI.font` / `DeckEditorUI.font` 原本就是悬空 guid，移植后由第 5 节统一赋值修复，不会留下新的悬空引用。
- **图形资源预算**：一次导入 73 张图与 34 个音频会推高编辑器资源 ID，按项目级 skill 的做法只 refresh 一次、不做 Reimport All，随后再进 Play。
- **回滚**：删除 `Assets/ThirdParty/ArkCards` 与 `Assets/StreamingAssets/Decks`，移除 Build Settings 三条记录即可完全回滚；本方案不改动任何既有资产，回滚无残留。

## 10. 实施与实测

已按上述结构落地，编译器零错误零警告。实测结论：

| 项 | 结果 |
| --- | --- |
| 编译 | 全工程 0 error / 0 warning，`ArkCards` 与 `ArkCards.Tests` 程序集均编译通过 |
| 字体 | `Card.prefab` 5 处、`BattleScene` 29 处 `TMP_Text`，`MainMenuScene` / `DeckEditorScene` 各 1 个 `MainMenuUI.font` / `DeckEditorUI.font` 字段，全部指向 `NotoSansSC-Regular SDF` |
| 主菜单 | 13 条中文文本生成正常（`characterCount` 与字数一致，字体 `NotoSansSC-Regular SDF`），4 个关卡按钮可交互 |
| 进入对战 | 点击「第1关 · 演习作战」后场景切到 `BattleScene`，`CardController` 64 个、`HandContainer` 2 个、手牌 11 张 |
| 对战渲染 | `SpriteRenderer` 845 个全部有 sprite，`AudioSource` 12 个其中 1 个正在播放（BGM）；录像核对卡面中文与费用/血量数字清晰，无品红材质、无空白方块 |
| 卡组编辑器 | `DeckEditorScene` 读到 `Assets/StreamingAssets/Decks/Deck01.json`，卡池 35 条富文本（含 `<size=60%>` 标签）中文与职业/费用正常 |
| Build Settings | 9 条：既有 6 个场景在前，ArkCards 三个场景在后 |

本工程常驻系统（`WorldPromptCanvas`、`AfuBallCanvas`、`AchievementToastCanvas` 等）会通过 `RuntimeInitializeOnLoad` 随编辑器 Play 一起启动，与 ArkCards 自身的 `Canvas`（ScreenSpaceCamera / order 0）并存；实测未遮挡对战画面。阿福悬浮球画布 `sortingOrder = 5900`，若后续 ArkCards 加全屏页面需要临时隐藏它。
