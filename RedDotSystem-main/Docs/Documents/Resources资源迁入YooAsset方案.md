# Resources 资源迁入 YooAsset 方案

## 1 现状

### 1.1 两套加载体系并存

| 体系 | 覆盖范围 | 入口 |
| --- | --- | --- |
| YooAsset | UI 界面预制体（`Assets/Game/UI/**`，由 `UIConfig.json` 注册）、`Assets/Content/HotContent`（活动 Lua / 预制体）、音频（`AudioClipLease`）、`ResourceManager` 的全部加载调用 | `YooAssetService` / `ResourceManager` |
| Resources | 物品图标、面板底图、表情包、中文回退字体、小地图、战斗配置、框架根预制体等 | 各处的 `Resources.Load` / `Resources.LoadAll` |

`Assets/` 下的 `Resources` 目录共 6 处，资源 222 个、合计 77.0 MB。

### 1.2 体积构成（按 PerfAgent 同一规则统计）

| 分类 | 文件数 | 体积 | 加载方式 |
| --- | --- | --- | --- |
| `Resources/Fonts/NotoSansSC-Regular SDF.asset` | 1 | 40.5 MB | `TmpChineseFallbackBootstrap` / `ChineseFontLocator` |
| `UIControlBinding/Resources/Editor/MSYHMONO.ttf` + 2 个 guiskin | 5 | 14.3 MB | 仅 `UIControlDataEditor`（编辑器） |
| `Resources/ui/Theme` 底图与横幅 | 9 | 12.5 MB | `UITheme` 按 key 拼路径 |
| `Resources/biaoq` 表情包 | 79 | 4.8 MB | `StickerCatalog` 目录扫描 |
| 第三方 TMP / XLua / Odin | 22 | 2.2 MB | 各自系统的约定路径 |
| 其它 UI 小件、图标、Character 图 | 59 | 1.7 MB | `ItemIconLoader` / `UITheme` / 各处点名加载 |
| Agent / Minimap / Shaders / Battle / Quest / RedDot / Framework / Lua 等 | 47 | 0.9 MB | 各处点名加载 |

### 1.3 三条硬约束

1. **YooAsset 的 `LoadAssetSync` 要求包已初始化**。`YooAssetService.LoadAssetSync` 在 `IsInitialized == false` 时返回 null 并打 `未找到资源包` 错误。包初始化发生在 `GameBootstrap` 的 `ModuleHub.Boot` 阶段，因此任何**早于它**的加载点都不能改走 YooAsset。
2. **部分加载点必须同步返回**。`ItemIconLoader.Get`、`UITheme.LoadBackground`、`PanelSkin` 在 `OnInit` 里同步取图，签名改异步会波及 Bag / Quest / Character / Gacha / 好友列表等十余个界面。
3. **TMP 回退字体按类型查找**。`TMP_Settings.fallbackFontAssets` 要求 `TMP_FontAsset` 实例被挂进列表，没有「按 YooAsset 路径声明字体」的机制。

## 2 设计

### 2.1 统一门面 `GameAssets`

新增 `Assets/Game/Scripts/Resource/GameAssets.cs`，用**与 `ItemIconLoader` 相同的约定**承载迁移：location 为 `Assets/Game/Resources/...` 形式的完整路径。

```csharp
public static Sprite GetSprite(string resourcesRelativePath);   // 同步，命中缓存
public static T GetAsset<T>(string resourcesRelativePath) where T : UnityEngine.Object;
public static bool TryGetAsset<T>(string resourcesRelativePath, out T asset);
public static Sprite[] LoadAllSprites(string resourcesRelativePath);
```

查询顺序：**先 `Resources.Load`（迁移期返回已搬走的资源），再 `YooAssetService.LoadAssetSync`（已搬走的资源）**，两者都失败返回 null 并只警告一次。

这样做的好处是搬迁与改代码解耦：资源移出 `Resources` 后，同一份代码自动落到 YooAsset 分支，不需要为每一类资源写一份新逻辑。

查询顺序反过来没有意义：`Resources.Load` 命中即返回，不会产生 YooAsset 句柄泄漏；YooAsset 分支的句柄按 location 缓存并常驻（这些是跨界面共用的 UI 资源，与包生命周期同寿），不参与 `ResourceManager` 的引用计数。

**异步场景**（`AgentFloatBall`、`AgentView` 这类程序化 UI）使用 `LoadSpriteAsync`，在 `IsInitialized` 为真时才走 YooAsset，否则保留 Resources 分支并打日志。

### 2.2 搬迁顺序与目标

| 阶段 | 搬迁对象 | 目标目录（收集范围 `Assets/Game/Resources` 已覆盖） | 改动的加载点 |
| --- | --- | --- | --- |
| 一 | `UIControlBinding/Resources/Editor/MSYHMONO.ttf`、`UIControlDataSkinPro/Personal.guiskin` | `Assets/Game/Scripts/UI/Framework/UIControlBinding/Editor/Art/` | `UIControlDataEditor`（编辑器静态路径） |
| 二 | `Resources/ui/Theme/panel_bg`、`bg_*`、`home_bg`、`blur_home`、`panel_round`、`btn_round`、`circle`（banner 已删除） | `Assets/Game/Resources/ui/Theme/` 原地（由门面接管） | `UITheme`、`WorldPrompt`、`PanelSkin` |
| 二 | `Resources/ui/Friends/*`、`ui/Character/*`、`ui/80x80/*`、`ui/鸣潮：武器图标/*` | 原地 | `ItemIconLoader`、`FriendUIController`、`WorldPrompt` |
| 二 | `Resources/Agent/*`、`Minimap/*`、`Shaders/*`、`Battle/*`（预览预制体与配置） | 原地 | `AgentFloatBall`、`AgentView`、`DashijieMinimap`、`DamageTextRenderer`、`BattleHeroCatalog`、`CharacterView`、`HitFeelService` |
| 二 | `Resources/biaoq/*` | 原地 | `StickerCatalog`（清单文件 + 名称约定） |

阶段一只影响编辑器，不改变任何运行时行为，风险最低，作为门面与加载链路的第一道验证。

阶段二把资源**移出 `Resources` 目录**（位置按上面表格的「原地」指仍处于 `Assets/Game/Resources` 下但改成非 Resources 命名的平级目录）：实际落地为在 `Assets/Game/` 下新增 `Assets/Game/Art/UI` 等目录，资源体积与依赖不变，只改变寻址方式。

### 2.3 不搬迁的资源与原因

| 资源 | 原因 |
| --- | --- |
| `Resources/Fonts/NotoSansSC-Regular SDF.asset` | 40.5 MB，`atlasPopulationMode = Dynamic` 的运行时字体。TMP 回退机制只能挂 `TMP_FontAsset` 实例，运行时补字会回写资产；改走 YooAsset 需要把字体改成静态 SDF，且它必须在第一批 UI 显示前可用，而 YooAsset 包初始化晚于部分早期加载点 |
| `Resources/Lua/main.lua.txt` | `LuaEnvManager` 在 `Resources` 下按 `a.b` → `a/b.lua` 约定 require，xLua 的 `StaticLuaCallbacks` 直接调 `Resources.Load`，早于模块初始化 |
| `Resources/BuffDatabase/SkillDatabase/VfxCatalog/DOTweenSettings/RedDot/RedDotConfigAsset/RedDotEditorSettings/FrameRoot/EffectPlayer` | 首帧或框架启动期加载，此时 YooAsset 未必就绪；且这些是配置与框架资产，迁入热更包会引入无谓的下载依赖 |
| 第三方 `TextMeshPro/Resources`、`XLua/Resources`、`BehaviorDesigner/Resources`、Odin `Config/Resources` | 第三方包自带，改动会在升级时被覆盖 |

### 2.4 包体与热更影响

`Assets/Game/Resources` 已在 ui 包的 `GameUI` 收集路径内，因此搬迁后：

- **安装包**：资源本体仍是内置内容（OfflinePlay 的 Builtin 内容），体积不变。
- **热更包**：被搬迁的资源进入 ui 包的 base 标签，体积增加约 17 MB（阶段二内容，不含字体与编辑器字库）；发布 ui 包后客户端首次更新需多下载这部分。

> 搬迁到 YooAsset **不会**让安装包变小，它改变的是寻址方式与可热更性。要减小包体只能删除未使用资源（banner 已完成）或降低贴图分辨率。

## 3 协议 / 文件影响面

| 类别 | 影响 |
| --- | --- |
| 协议 | 无 |
| 服务端 | 无（热更 CDN 已支持子目录） |
| 收集配置 | 无（搬迁目标仍在 `Assets/Game/Resources` 内的 ui 包 GameUI 组收集路径） |
| 新增文件 | `Assets/Game/Scripts/Resource/GameAssets.cs`、`Assets/Game/Resources/biaoq/sticker_manifest.txt` |
| 修改文件 | `UITheme`、`PanelSkin`、`ItemIconLoader`、`StickerCatalog`、`WorldPrompt`、`AgentFloatBall`、`AgentView`、`FriendUIController`、`DashijieMinimap`、`DamageTextRenderer`、`BattleHeroCatalog`、`BattleRoomView`、`CharacterView`、`HitFeelService`、`UIControlDataEditor` |
| 删除文件 | 搬迁后原 `Resources` 下的 4 处资源目录 |

## 4 E2E 验收项

1. **主页**：打开主页，面板底图、回退字体注入日志正常，无 `未找到资源包` 错误。
2. **物品图标**：打开背包与任务面板，格子图标非空（`ItemIconLoader` 返回的 Sprite 不为 null）。
3. **表情包**：打开好友聊天表情面板，`StickerCatalog.All.Count` 与搬迁前一致，动画表情帧数不变。
4. **面板底图**：打开好友 / 邮件 / 背包 / 世界聊天，`PanelSkin` 应用的 `Image.sprite` 非空且为对应 `bg_<key>`。
5. **小地图**：进入大世界，小地图 RawImage 的纹理非空。
6. **编辑器检查器**：UIControlData 检查器皮肤加载不报错（阶段一）。
7. **迁移完整性**：`Resources` 下搬迁目录已不存在，`GameAssets` 全部命中 YooAsset 分支。
8. **回归**：`unity_editor.refresh` 0 error；登录进主页不卡在加载。

## 5 风险

| 风险 | 处置 |
| --- | --- |
| 同步加载赶上包未初始化 | 门面保留 Resources 分支作为兜底并打一次警告；早期加载点（字体、Lua、框架资产）不搬迁 |
| 目录扫描类资源（表情包）清单不一致 | 新增 `sticker_manifest.txt` 由 `Tools/UI/生成表情清单` 生成，含每个 location 与所属键；`StickerCatalog` 读清单后逐个加载 |
| `LoadSpriteAsync` 期间 YooAsset 正在下载 base 标签 | 调用方已有空图兜底（程序化 UI 会先显示占位），加载完成后回填 |
| 句柄未释放导致内存常驻 | 门面缓存的句柄限定在跨界面共用的 UI 资源；单次使用的资源由调用方持有到界面关闭 |
| 搬迁后忘记重新发布 ui 包，编辑器模拟模式下资源缺失 | 每阶段结束执行 `Tools/UI 热更/构建并发布 ui 包`，并在 HostPlay 下复测 |

## 6 实测结论

### 6.1 落地范围

| 搬迁对象 | 结果 |
| --- | --- |
| `Resources/ui` → `Assets/Game/Art/UI` | 68 个资源，MoveAsset 保 GUID |
| `Resources/Agent` → `Assets/Game/Art/UI/Agent` | 13 个资源 |
| `Resources/biaoq` → `Assets/Game/Art/UI/biaoq` | 77 张 Sprite + `sticker_manifest.txt` |
| `UIControlBinding/Resources/Editor` → `UIControlBinding/EditorArt` | 5 个文件，仅编辑器使用，不参与打包 |
| 收集配置 | `ItemIcons` 组采集路径改为 `Assets/Game/Art/UI`；`LocationRoutes` 增加 `Assets/Game/Art/UI` → `ui` |

### 6.2 验证结果（ui 包 1.2.0，HostPlay）

| 验收项 | 结果 |
| --- | --- |
| 面板底图 | `panel_bg` / `bg_friend` / `bg_mail` / `bg_bag` / `panel_round` / `btn_round` / `blur_home` 全部非空 |
| 物品图标 | `ItemIconLoader.Get("T_IconA80_02_UI")` 返回 Sprite |
| 表情包 | 3 个表情 / 77 帧（其中 1 个 75 帧动画，fps 16.7），与搬迁前一致 |
| 好友列表 | `row` 行底图非空 |
| 悬浮球 | `shouanren_avatar`、`afu_glow` 等 13 张全部非空 |
| 真实界面 | FriendView 底图 `bg_friend` 挂在 `FriendPanel`、`SkinBackdrop` 为 `blur_home`、行底图 `row`；BagView 底图 `bg_bag` 挂在 `BagPanel` |
| 编译 | `unity_editor.refresh` 0 error |

### 6.3 踩到的坑

1. **下载 bundle 后必须 `UnloadAllAssetsAsync` 再重新加载**：否则同名 bundle 仍被旧包占用，同步加载直接返回 `Failed`（错误信息停在 `Attempting to load ... from remote`）。表现为"文件已下载到沙箱但加载不出来"。
2. **下载器只补差异不补全量**：缓存里 bundle 名与清单 hash 不一致时（旧包残留），下载器会认为资源已就绪而不下载；此时需要 `ClearCacheAsync(ClearCacheMethods.ClearAllBundleFiles)` 后重载清单再下载。
3. **`Resources.LoadAll(null)` 直接抛异常**：`StickerCatalog` 已不再传 `ResourcesFolder`，`GameAssets.LoadAllSprites` 对空目录名做了判空。
4. **清单由构建自动生成**：YooAsset 会给每个资源生成短地址（如 `panel_bg`），但完整路径 `Assets/Game/Art/UI/Theme/panel_bg.png` 同样可寻址，门面统一用完整路径。

