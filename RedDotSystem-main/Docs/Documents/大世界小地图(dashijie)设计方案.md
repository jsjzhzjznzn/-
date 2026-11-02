# 大世界小地图（dashijie）设计方案

> 状态：**已实施**（2026-10-28，v3：底图已烘焙、HUD 小地图与大地图实测通过）
> 需求来源：m04241「给我的 dashijie 场景做一个小地图，先阅读资料再制定文档」
> 目标口径：**原神那样的地图**（小地图常驻 + 大地图可开合 + 北朝上不旋转）
> 本轮范围：小地图 HUD 常驻、大地图面板、离线烘焙底图、玩家箭头与朝向
> **本轮不做**：传送点、任务目标图标、NPC 图标、路径导航、战争迷雾

### 已定稿的交互约定（m04482）

| 项 | 决定 |
|---|---|
| 小地图位置 | **左上角**，任务追踪卡下移让位 |
| 大地图快捷键 | **`M`**（原神习惯），另加「点小地图」入口 |
| 邮件快捷键 | 由 `M` 改为 **`N`**（M 让给地图；N 是 M 左邻键，手不用大移） |

---

## 0. 需求拆解

| # | 需求（用户原话） | 落地目标 |
|---|---|---|
| 1 | 「给我的 dashijie 场景做一个小地图」 | HUD 左上角常驻圆形小地图，跟随玩家平移、北朝上 |
| 2 | 「我要做的事就是像原神那样的地图」 | 静态烘焙底图 + 玩家箭头旋转（资料里的**模式 A**）；大地图可开合 |
| 3 | 「先阅读我发给你的资料」 | 采用资料里的**世界契约**（中心/尺寸/PixelsPerMeter/朝向四件套），烘焙与运行时共用同一套常量 |
| 4 | 资料：静态 vs 动态 | 选**静态烘焙**（理由见 §2.2），保留「重新烘焙」菜单，场景改动后一键重烤 |

### 已确认的关键取舍

| 取舍 | 决定 | 理由 |
|---|---|---|
| 底图来源 | 离线烘焙 PNG | 场景是静态建筑群；实时俯拍要再渲染 240 个 SkinnedMeshRenderer + 102 盏实时点光，代价不可接受 |
| 旋转模式 | 北恒朝上，只转玩家箭头 | 资料 §1.3 模式 A；坐标公式不叠加旋转，图标文字不会倒转 |
| 圆形裁剪 | **自写 UI shader 做 alpha discard** | 不用 uGUI `Mask`：Mask 走模板缓冲，白送 2 个隐形 pass（进/出遮罩各 1），且给子元素发带 stencil 的材质变体、破坏合批 |
| 底图渲染组件 | `RawImage` + 自定义材质 | 底图是烘焙 Texture2D，非 Sprite，RawImage 的 UV 按自身矩形生成、不受变换影响（资料里踩过的坑） |
| 小地图归属 | 作为 `DashijieHudView` 的子节点 | 复用 HUD 的 `Update`、暂停门禁、`contentGroup` 显隐，不新增常驻视图 |
| 大地图归属 | **新增** `UIType.DashijieMapView = 27` | 全屏、可缩放拖拽、暂停世界，语义上与 HUD 常驻层不同 |
| 大地图暂停层 | `PauseManager.Layer.Other` | 枚举里没有 Map，`Other` 语义最贴；不新增枚举避免影响已有面板 |

---

## 1. 资料结论提炼（m04241 附件）

只留会影响本方案的结论，原文细节不重复。

### 1.1 静态 vs 动态

| | 静态烘焙 | 动态（RT 正交相机 / 矢量实时绘制） |
|---|---|---|
| 原理 | 编辑器里从上往下拍一张，运行时当普通 UI 贴图用 | 运行时读地形/碰撞体/NavMesh 或每帧 RT 俯拍 |
| 性能 | 极高，就是一张 UI 贴图 | 每帧多一遍场景渲染，或每帧重画多边形 |
| 场景改动 | 必须重新烘焙 | 自动同步 |
| 风格化 | 只能整张调色 | 可单独画地块轮廓、描边 |

→ **本场景选静态**：建筑群不随玩法变化；动态方案的每帧成本在 240 renderer 的场景里无法接受。

### 1.2 世界契约（本方案直接沿用）

资料里最硬的结论：**烘焙工具、运行时渲染、GPU 计算三处必须共用同一套硬编码常量，改一处不改另一处 → 地图错位/缩放错误/方向翻转**。四件套：

1. 覆盖范围 + 中心（底图中心 = 世界某点）
2. 朝向：世界 `+Z` 画在贴图**向上**（北朝上，运行时不做旋转）
3. 比例 `PixelsPerMeter`（贴图固有比例，与运行时 zoom 分开：zoom 只是 UI 放大，不改贴图像素/米定义）
4. 底图路径

### 1.3 坐标公式（模式 A：地图不动，标记相对玩家偏移）

```
mapPos = C + (P - P_player) * k        k = PixelsPerMeter * zoom
```

等价实现（**方案 B**，只移动 1 个 RectTransform）：

```
底图 anchoredPosition = -P_player_map * k
```

两者数学恒等（资料给了代数证明）。**本方案用方案 B**：每帧只改底图 1 个 RectTransform，静态地标不用刷新。

### 1.4 玩家箭头

```
playerArrow.localEulerAngles = new Vector3(0, 0, -yaw)   // yaw = 玩家/相机水平转向
```

Unity 的 Y 轴旋转顺时针为正，世界 `+Z` 是地图上方，故取负号。

### 1.5 draw call 经验（本次直接吸收）

- 合批三来源：**纹理各异 / Mask 模板 / TMP 文本**
- `Mask` = 2 个隐形 pass（ColorMask 0 只写模板）+ 子元素材质变体 → 用 shader `discard` 替代，0 成本
- 程序化小图（底衬/环/箭头）**合进一张图集**，否则每张独立纹理各断一批
- 目标：小地图 ≤ 4 draw（底图 1 + 标记图集 1 + 箭头 1 + 文字 0~1）

---

## 2. 现状实测（2026-10-28，编辑器内实测）

### 2.1 场景 `Assets/Game/Scenes/dashijie.scene`

14 个根节点：

```
Camera / EventSystem / CM_DialogNPC(inactive) / CM_DialogPlayer(inactive) /
DialogueRunner(inactive) / GenshinDemoRoot(inactive) / Directional Light /
PostFX_Volume / CharacterFillLight / HallLights(6 child) /
未命名 (1)(243 child, 240 renderer) / thirdcamera / 安比大世界 / DashijieRoot(空)
```

- `未命名 (1)` 对应 `Assets/Content/Resource/场景/陈曲/未命名.fbx`，243 个子物体里 240 个是 `\d+_材質\d+` 网格，另 4 个命名子物体是 `Belobog / ModelCamera / Cube / Light`。
- **没有语义命名节点**：`神樱 / 鸟居 / 桥 / 水面 / 屋顶 / 墙壁（上）/ 地板 / 狐` 全部 `not found`（那是 `大社.prefab` 的命名体系）。→ **小地图地标不能按名字取，只能按包围盒/材质聚类**。
- 全部 240 个 Renderer 的 `gameObject.layer = 6`（environment）。
- `DashijieRoot` 是空节点（留给运行时挂东西）。
- 场景内**没有流萤/NPC**（流萤在 `duihua` 场景，本场景 `GenshinDemoRoot` 为 inactive 且 `GameObject.Find` 取不到）。

### 2.2 地形与碰撞

- 碰撞体总计 241：**240 个 MeshCollider**（挂在 `SkinnedMeshRenderer` 上，来自 FBX）+ 1 个玩家 `CapsuleCollider`。
- **没有 Terrain、没有独立地板 Plane**。玩家脚下命中的是 `222_材質223`（y=-0.10，法线 (0,1,0)，材质 `Tex_0058`）；更深处还有 `020_材質21`(y=-38.41) / `021_材質22`(y=-36.95) 两层。
- 地面层高度集中在 **y ∈ [-3.5, 6]**，主峰 y≈0。

### 2.3 可行走范围（1m 网格 513×513 射线实测）

| 指标 | 实测值 |
|---|---|
| 采样 | 513×513 = 263169 条向下射线，852 ms |
| 地面格（y∈[-3.5,6]，法线 y>0.6） | **80554** 格 |
| 从出生点 `(0,-6)` 洪水填充**可达** | **50705 格 ≈ 50705 m²** |
| 可达 bbox | **X[-166, 184]，Z[-111, 120]**（350m × 231m） |
| 占 512m 盒比例 | 19.27% |
| 地面格上方 2m 内有遮挡 | 33.6%（廊柱/屋檐/建筑） |
| 最高遮挡高度 | y = 76.5 |

可达区 8m/cell 缩略（`#` ≥70% 可达）：

```
  120|............:....................................................
  112|............#######:....................:######:.................
   96|............#########:................:########:.................
   80|............###########:............:##########:.................
   64|............###########+............+##########:.................
   48|............###########+............+##########:.................
   32|............###########+....++++:...+##########:.................
   16|............############..+######+..+########+.:+:...............
    0|............:###########+:########::#######+.:+####:.............
  -16|..............:++####################+......+########............
  -32|..........:###########++++########++++++...:#########:...........
  -48|..........:###########################++++###########:...........
  -64|..........:##################..+#####################:...........
  -80|..........:##################..+#####################:...........
  -96|.........+###:...+#########++..+++###################:...........
 -112|.........:........++:............................................
      X: -240 .. 272 (8m/cell)
```

结构上是「南侧大厅 + 中央前庭 + 北侧建筑群 + 西侧长走廊」的形状。

### 2.4 必须剔除的 7 个超大包围盒

按 `bounds.size` 排序，以下 7 个是天空球/远景/巨树/地下岩层，**烘焙时必须按包围盒剔除**，否则地图被拉成数公里：

| 物体 | size | center | 材质 |
|---|---|---|---|
| `195_材質196` | (3863, 754, 4097) | (-283, 229, 200) | Tex_0101_2 |
| `196_材質197` | (2940, 753, 3875) | (-93, 228, 416) | Tex_0102_2 |
| `151_材質152` | (1756, 299, 1340) | (16, 93, 60) | Tex_0097_2 |
| `019_材質20` | (1125, 342, 953) | (1, 43, -6) | Tex_0005 |
| `235_材質236` | (640, 620, 563) | (-4, 151, -568) | Tex_0096 |
| `020_材質21` | (23, 213, 840) | (0, -24, -61) | Tex_0053 |
| `021_材質22` | (83, 213, 840) | (0, -24, -60) | Tex_0052 |

→ 但**不需要按名字硬编码剔除**：烘焙只取 `y ∈ [-3.5, 6]` 的最近地面命中，这 7 个天然落在该区间之外（天空/树冠在上、岩层在下），已被高度过滤排除。

### 2.5 地面材质分布（32×32 采样，取 y∈[-3.5,6] 最近命中）

| 材质 | 命中格 | `_BaseColor` |
|---|---|---|
| `Tex_0056` | 210 | 白 |
| `Tex_0057` | 43 | 白 |
| `Tex_0058` | 16 | 白 |
| `Tex_0011_2` | 10 | 白 |
| `Tex_0093` | 8 | 白 |
| `Tex_0005` / `Tex_0082` / `Tex_0071` | 各 3 | 白 |
| `Tex_0052` | 2 | 白 |
| `Tex_0038` / `Tex_0011` / `Tex_0016` | 各 1 | 白 |
| 无命中 | 723 | — |

**所有材质 `_BaseColor` 全白，颜色信息只在 `_BaseMap` 贴图里** → 烘焙取色必须采样贴图平均色，不能读 `_BaseColor`。
材质是 FBX 内嵌材质，资产路径为 `Assets/Content/Resource/场景/陈曲/未命名.fbx`，**不能按 `.mat` 路径加载**，要经 `renderer.sharedMaterial` 取。

### 2.6 玩家与相机

- 玩家 `安比大世界`，`Player` 组件，position `(0.00, 0.02, -6.00)`，`eulerAngles.y = 0`，layer 3。
- `Camera.main`：pos `(0, 1.80, -12)`，fov 45，near 0.01，far 1000，cullingMask -33，forward `(0, -0.14, 0.99)`。
- 相机由 `thirdcamera`（Cinemachine）驱动，`Player.playerCameraUtility.virtualCamera` 持有 vcam。
- 朝向取值优先级：`Player.CameraTransform.eulerAngles.y`（相机水平朝向，原神手感）→ 回退 `player.transform.eulerAngles.y`。

### 2.7 HUD 现状（`Assets/Game/UI/Dashijie/DashijieHudView.prefab`）

由 `Assets/Game/Scripts/UI/Dashijie/Editor/DashijieHudViewBuilder.cs`（419 行，菜单 `Tools/UI/重建大世界 HUD`）生成，1920×1080 基准、四角锚定：

| 节点 | 尺寸 | anchoredPosition | 锚点 |
|---|---|---|---|
| `questTrackerRoot` | 360×112 | (36, -32) | 左上 |
| `HotkeyRow`（现 9 个 52×52，gap 8；本次改 10 个） | 532×64（改后 592×64） | (-28, -22) | 右上 |
| `PlayerBar` | 470×92 | (0, 26) | 底部居中 |
| `chatBubbleButton` | 52×52 | (36, 36) | 左下 |

builder 头部注释原文：**「不做：小地图、摇杆、攻击/技能/跳跃按钮、交互提示条」** → 本次要把「小地图」从这条排除项里拿掉。

`DashijieHudView.cs`（601 行）要点：
- `[ControlBinding]` 字段 + `UIControlData` 绑定；`contentGroup`(CanvasGroup) 统一显隐
- `ManagedPanels` 9 个 `UIType`，面板开合时 `SetContentVisible(false)`
- 热键表：`B/Y/C/V/M/G/L/T` + `Esc`（设置）→ 本次邮件 `M` 改 `N`，`M` 给大地图（§5.7）
- 数字键 `1–4` 切角色（`MaxSwitchKeys = 4`）
- 暂停时序：`Open` 完成后 Push 暂停层；`Close` 前先 Pop

### 2.8 框架约定

- `UIType`：`DashijieHudView = 26`，`Max = 27` → 新枚举取 **27**，`Max` 改 28。
- `Assets/Game/UI/UIConfig.json` 现有 24 条，`DashijieHudView` 在末尾（`isWindow:false`、`uiLayer:"NormalLayer"`）。
- `UILayer`：`SceneLayer=1000 / BackgroundLayer=2000 / NormalLayer=3000 / DamageLayer=3500 / InfoLayer=4000 / TopLayer=5000 / TipLayer=6000 / BlackMaskLayer=7000`。
- `PauseManager.Layer`：`Settings / Bag / Mail / Quest / Character / Achievement / Friend / Leaderboard / Chat / Cutscene / Other`（**没有 Map**）。
- `UIManager`：`Open(UIType, object)` / `Close(UIType)` 均返回 `UniTask`。

### 2.9 可复用素材与代码

| 资产 | 说明 |
|---|---|
| `Assets/Game/Scripts/MiniGame/FogHunt/FogHuntMinimap.cs` | 121 行，程序化 `Texture2D` 小地图（`Cell=4`、`SetPixels32`、`Brighten`、`Destroy`），可作配色/写法的参考，但它是**逐格方块**风格，不适合本场景 |
| `Assets/Game/Shaders/Guide/GuideMask.shader` | 3368 字节，URP UI shader，SDF 圆角矩形挖洞 + `Blend SrcAlpha OneMinusSrcAlpha` + `ZWrite Off` + `Cull Off`，**圆形裁剪 shader 直接照这个骨架写** |
| `Assets/Game/UI/Dashijie/Icons/dashijie_circle.png` | 圆形 sprite，做小地图外框 |
| `Assets/Game/Resources/ui/Theme/{panel_round,btn_round,circle}.png` | HUD 主题三件套 |
| `Assets/Codely/Fonts/NotoSansSC-Regular SDF.asset` | 中文字体 |
| `Assets/Game/Resources/Minimap/` | 目录已存在，内含孤儿文件 `fuben_minimap.png`（1024²，旧地牢小地图，**全工程零引用**，本方案不复用） |
| `Assets/Game/Shaders/Dungeon/` | 空目录，本方案可把新 shader 放这里 |

---

## 3. 方案选型

### 3.1 为什么不做实时 RT 俯拍

- 场景 240 个 `SkinnedMeshRenderer` + 102 盏实时点光（URP `maxAdditionalLightsCount=4`），每帧再渲染一遍 = 帧率灾难。
- 风格不可控：实时俯拍出来是写实俯视，做不出原神那种描边地块。

### 3.2 为什么不做矢量实时绘制

- 场景没有「地块/瓦片」数据，只有一张 FBX 的 240 个网格，没有可读的语义层。
- 要动态就得每帧跑射线或做离屏 SDF，收益（场景不变）为零。

### 3.3 最终方案：离线烘焙 + 运行时贴图平移

```
[编辑器 Baker]  1m 射线扫 384m×384m → 地面 mask + 高度 + 材质色 → 描边 → PNG(1536²)
                                  ↓
[运行时]  RawImage(底图) 每帧 anchoredPosition = -playerMapPos * zoom
          玩家箭头固定窗口中心，localEulerAngles.z = -yaw
          圆形裁剪：自定义 shader discard（无 Mask、无模板缓冲）
```

---

## 4. 世界契约（本方案唯一真源）

烘焙工具与运行时**必须共用**下面这张表，任何一处改动都要同步改另一处。

| 常量 | 值 | 说明 |
|---|---|---|
| `WorldCenter` | `(0, 0)` | 底图中心对应的世界 XZ |
| `WorldSize` | `384` m | 覆盖 `X∈[-192,192]`、`Z∈[-192,192]`，完整包住可达 bbox `X[-166,184] Z[-111,120]` |
| `PixelsPerMeter` | `4` px/m | 贴图固有比例，**烘焙常量，运行时不改** |
| `TextureSize` | `1536` px | `384 × 4 = 1536` |
| 朝向 | `+X → 右`、`+Z → 上` | 北恒朝上，底图永不旋转 |
| 底图路径 | `Assets/Game/Resources/Minimap/dashijie_minimap.png` | `Resources.Load<Texture2D>("Minimap/dashijie_minimap")` |
| 地面层过滤 | `y ∈ [-3.5, 6]`、法线 `y > 0.6` | 天然排除天空/树冠/地下岩层（§2.4） |
| 小地图窗口直径 | `240` px | HUD 左上角 |
| 小地图默认 `zoom` | `0.55` | 运行时 `k = 4 × 0.55 = 2.2 px/m` → 显示直径 `240 / 2.2 ≈ 109 m` |
| 大地图默认缩放 | `1.0` | 1536px 底图 1:1 铺进 1600×900 面板 |

**换算公式（唯一实现，两处都调它）：**

```csharp
// 世界 XZ → 底图像素坐标（左下原点）
public static Vector2 WorldToMap(Vector2 worldXZ)
{
    float px = (worldXZ.x - WorldCenter.x) * PixelsPerMeter + TextureSize * 0.5f;
    float py = (worldXZ.y - WorldCenter.y) * PixelsPerMeter + TextureSize * 0.5f; // worldXZ.y == world Z
    return new Vector2(px, py);
}
```

---

## 5. 实现设计

### 5.1 离线烘焙 Baker

**文件**：`Assets/Game/Scripts/World/Minimap/Editor/DashijieMinimapBaker.cs`
**菜单**：`Tools/地图/烘焙大世界小地图`

流程：

1. **扫格**：以 `1m` 步长遍历 `[-192,192]²`（385×385 点），每点 `Physics.RaycastAll` 向下（layer 6，`QueryTriggerInteraction.Ignore`），
   - 取 `y ∈ [-3.5, 6]` 且法线 `y > 0.6` 的**最近**命中 → 地面高度 `floorY`、材质 `floorMat`
   - 取该点所有命中里最高的 `y` → 遮挡高度 `topY`
2. **可达性**：从出生点 `(0, -6)` 做 4 邻域洪水填充，`|ΔfloorY| ≤ 1.2m` 才连通 → 不可达格画「未探索暗色」（`(16,18,24)`），与可达区区分
3. **取色**：`floorMat._BaseMap` 的平均色（**不能读 `_BaseColor`，全白**），首次遇到材质时算一次并缓存（读 `GetPixels32` 隔点采样 4096 个点）
4. **明暗**：`topY - floorY` 分 5 档压暗（0m 最亮、>25m 最暗），做出建筑体量感
5. **描边**：对地面 mask 做 `dilate - erode` 得到 1px 轮廓，叠深色 `(28,32,40)`
6. **放大**：1m 采样 → 4px/m 输出，每个采样格填 4×4 块（最近邻，保持锐利；文字/图标另画）
7. **输出**：`Texture2D(1536, 1536, RGB24, false)` → `EncodeToPNG` → 写 `Assets/Game/Resources/Minimap/dashijie_minimap.png`
8. **importer**：`Texture Type = Default`、`sRGB = true`、`mipmap = false`、`wrapMode = Clamp`、`filterMode = Bilinear`、`maxTextureSize = 2048`、`compression = None`（或 `Normal Quality`）
9. 烘焙完成后 `AssetDatabase.Refresh()` + `Debug.Log` 打印 `可达格数 / bbox / 耗时`

**注意**：Baker 必须在**编辑器未播放**时跑（要读 FBX 的 MeshCollider 位置，播放态下场景可能被改）。

### 5.2 运行时：`DashijieMinimap`（纯逻辑类）

**文件**：`Assets/Game/Scripts/World/Minimap/DashijieMinimap.cs`

```csharp
public sealed class DashijieMinimap
{
    public const float WorldCenterX = 0f;
    public const float WorldCenterZ = 0f;
    public const float WorldSize = 384f;
    public const int PixelsPerMeter = 4;
    public const int TextureSize = 1536;
    public const string ResourcePath = "Minimap/dashijie_minimap";

    public Texture2D Texture { get; private set; }
    public bool Load();                                   // Resources.Load
    public static Vector2 WorldToMap(Vector2 worldXZ);
    public static Vector2 MapToWorld(Vector2 mapPx);
    public void Destroy();
}
```

### 5.3 小地图 HUD 节点（改 builder，不改 prefab）

在 `DashijieHudViewBuilder.Build()` 里新增一段，插在 `questTrackerRoot` **之前**（保证层级顺序在底下）：

```
MinimapRoot            240×240  @(36,-32)   左上锚点
├─ MinimapBackdrop     248×248  圆形底衬（circle sprite，深色 a=0.9；同时是点击入口，挂 Button）
├─ MinimapMask         240×240  Image(圆形 sprite) + Mask（showMaskGraphic=false）
│   └─ MinimapMap      240×240  RawImage + 烘焙底图（uvRect 采样）
├─ MinimapRing         248×248  圆形金边（minimap_ring.png，程序化生成）
├─ MinimapArrow        30×30    玩家箭头（minimap_arrow.png，旋转）
└─ MinimapNorth        24×16    "N" 徽记（顶部）
```

同时**改现有布局**：`questTrackerRoot` 的 `anchoredPosition` 由 `(36, -32)` 改为 **`(36, -296)`**（小地图 240 + 间距 24），否则任务卡被小地图压住。

- **底图用 `RawImage.uvRect` 采样，不移动大 quad**：窗口自己的材质就是裁剪材质，移动几百 px 的大 quad 会让「子物体不受父级 shader 裁剪」这个问题冒出来（真要裁就得回到 Mask/模板缓冲）。uvRect 方案只有 1 个 quad、0 个额外 pass。
- 窗口覆盖的 UV 尺寸 = `240 / (PixelsPerMeter × zoom × WorldSize)` = `240 / (4 × 0.55 × 384)` ≈ `0.284`（约 109 m 视野）
- 每帧：`uvRect = (mapPx/TextureSize - uvSize/2, uvSize)`
- 箭头：`MinimapArrow.localEulerAngles = new Vector3(0, 0, -yaw)`
- 面板打开（`SetContentVisible(false)`）时小地图一起隐藏 —— 自动生效，因为挂在 `contentGroup` 下
- 点击落在外圈底衬上（地图/外环/箭头的 `raycastTarget` 全关），打开大地图

**`DashijieHudView.cs` 新增字段与逻辑**：

```csharp
[ControlBinding] public GameObject minimapRoot;
[ControlBinding] public RawImage minimapMap;
[ControlBinding] public RectTransform minimapArrow;
[ControlBinding("MinimapBackdrop")] public Button minimapButton;

private DashijieMinimap minimap;
private const float MinimapWindowPx = 240f;
private const float MinimapZoom = 0.55f;

private void RefreshMinimap()   // Update() 开头调用（面板打开时也要保持跟随，隐藏由 contentGroup 负责）
{
    if (minimap == null || minimap.Texture == null || minimapMap == null) return;
    if (boundPlayer == null) return;

    Vector2 mapPixel = DashijieMinimap.WorldToMap(new Vector2(position.x, position.z));
    float uvSize = MinimapWindowPx / (DashijieMinimap.PixelsPerMeter * MinimapZoom * DashijieMinimap.WorldSize);
    minimapMap.uvRect = new Rect(
        mapPixel.x / DashijieMinimap.TextureSize - uvSize * 0.5f,
        mapPixel.y / DashijieMinimap.TextureSize - uvSize * 0.5f,
        uvSize, uvSize);

    float yaw = boundPlayer.CameraTransform != null
        ? boundPlayer.CameraTransform.eulerAngles.y
        : boundPlayer.transform.eulerAngles.y;
    minimapArrow.localEulerAngles = new Vector3(0f, 0f, -yaw);
}
```

`OnOpen` 里 `minimap = new DashijieMinimap(); minimap.Load();` 并把 `Texture` 赋给 `minimapMap.texture`；`OnClose` 里 `minimap.Destroy()`。

### 5.4 圆形裁剪：uGUI `Mask`（不用自定义 shader）

小地图窗口是 `MinimapMask` 节点：一层 `Image`（圆形 sprite `dashijie_circle.png`，`showMaskGraphic = false`）+ `Mask`，底图 `RawImage` 是它的子节点。

为什么不用自定义 shader 按 UV 裁剪：

- `RawImage` 的 `uv` **不是 0~1**，而是由 `uvRect` 生成（小地图窗口是 `0.36~0.64`）。按 `i.uv - 0.5` 算圆，形状永远落在圆心附近 → `discard` 永不触发，表现就是"完全不裁"。
- 改成按顶点本地坐标算也能圆，但要额外维护"控件尺寸"材质参数（`_Size`），HUD 尺寸一变就得同步，忘了就静默失效。
- `Mask` 用圆形 sprite 的 alpha 写模板，尺寸跟着节点走，没有可漂移的常量。代价是该窗口多 2 个隐形 pass（进/出遮罩），对 240px 的小地图可以忽略。

（早期版本用 `MinimapClip.shader` + `MinimapClip.mat` 做过 UV 裁剪，因上述原因已删除。）

### 5.5 大地图面板（新增 UIView）

**`UIType.DashijieMapView = 27`**，`Max` 改 28；`UIConfig.json` 追加：

```json
{
    "uiType": "DashijieMapView",
    "path": "Assets/Game/UI/Dashijie/DashijieMapView.prefab",
    "isWindow": false,
    "uiLayer": "NormalLayer",
    "isAutoNavigation": false,
    "isLuaUI": false,
    "luaModuleName": ""
}
```

预制体结构：

```
DashijieMapView            全屏 stretch
├─ Backdrop                全屏 深色 a=0.88（点击关闭）
├─ MapPanel                四边各留 10px（几乎全屏，panel_round 切片底板）
│   ├─ MapClip             铺满面板 + RectMask2D（矩形裁剪）
│   │   └─ MapContent      RawImage 底图（1536² × fitScale × zoom，按“覆盖”取长边适配）
│   │       └─ PlayerMark  34×34 玩家箭头（本地单位 = 底图像素 → 标记位置 = (WorldToMap(player) - 768) × fitScale × zoom）
│   ├─ TitlePlate/Title    "大世界地图"（压在图上，半透明底板）
│   ├─ HintPlate/HintText  "M / Esc 关闭 · 滚轮缩放 · 拖拽平移"（底部压在图上）
│   └─ CloseButton         右上 52×52 圆形 ×
```

大地图窗口用 `RectMask2D` 而不是自定义 shader：这里要裁的是**被缩放/平移的子物体**，自定义材质只作用于自身 quad 的 UV，裁不到子物体；`RectMask2D` 走 UI 自带的 `_ClipRect`，不发额外 pass、不产生材质变体。

缩放适配按**覆盖**（`fitScale = max(窗口宽, 窗口高) / 1536`）而不是按最小边：方图铺满非方形窗口，任何缩放/拖拽下都不会露出黑边；超出窗口的部分靠拖拽查看，`ClampPan` 以 `(内容尺寸 - 窗口尺寸) / 2` 为界。

交互：
- **`M` 开合**（原神习惯；`M` 从邮件手里收回，邮件改 `N`，见 §5.7）
- 点小地图本体也能开（`MinimapRoot` 挂 `Button`）
- 滚轮缩放 `0.5 ~ 2.0`（`MapImage.localScale`）
- 左键拖拽平移（改 `MapPanel.anchoredPosition`，限制在底图范围内）
- 打开时 `PauseManager.Push(PauseManager.Layer.Other)`，关闭前 `Pop`

**为什么用独立 UIView 而不是 HUD 子节点**：全屏、要暂停世界、要独占输入；塞进 HUD 会让 `SetContentVisible` 的语义和暂停记账搅在一起。

### 5.6 HUD 接入

`DashijieHudView` 里新增：

```csharp
// ManagedPanels 追加
UIType.DashijieMapView,

// Update() 里，Esc 判断之前
if (Input.GetKeyDown(KeyCode.M))
{
    if (activePanel == UIType.DashijieMapView) CloseActivePanelAsync().Forget();
    else if (activePanel == null) OpenPanelAsync(UIType.DashijieMapView, PauseManager.Layer.Other).Forget();
    return;
}
```

小地图本体**再点一次**（`MinimapRoot` 挂 `Button`）也打开大地图，符合原神手感。

### 5.7 邮件键位迁移 `M` → `N`

`M` 让给大地图后，邮件必须换键，否则同一个键会开两个面板（`DashijieHudView` 的热键表里 `M` 一旦指向 `MapView`，`MailView` 就再也打不开）。

**改动点 1 — 运行时热键表（`Assets/Game/Scripts/World/DashijieHudView.cs` 的 `OnAddListener()`，`:144-154`）：**

该数组现有 **8** 条（`Esc` 设置键单独处理、不在数组里）：

| 行 | 现状 | 改为 |
|---|---|---|
| `DashijieHudView.cs:150` | `new Hotkey { Button = mailHotkeyButton, Key = KeyCode.M, Panel = UIType.MailView, Layer = PauseManager.Layer.Mail }` | `Key = KeyCode.N` |
| 数组末尾新增一条 | — | `new Hotkey { Button = mapHotkeyButton, Key = KeyCode.M, Panel = UIType.DashijieMapView, Layer = PauseManager.Layer.Other }` |

另需新增 `[ControlBinding] private Button mapHotkeyButton;` 字段（与其余 9 个按钮同风格），并在 `OnAddListener()` 里对 `settingsHotkeyButton` 之外的自定义点击事件不额外处理（走 `Hotkey` 统一分发即可）。

**键位占用核对（全工程 `grep KeyCode.` 实测）：**

- 已占用：`Escape / Space / S / W / A / D / Q / E / F / B / Y / C / V / T / G / L / M / F9 / Return / KeypadEnter / Alpha1 / Keypad1 / 四个方向键`
- **`N`、`O`、`P`、`H`、`J`、`U`、`K`、`I` 全部零占用** → 取 `N`（`M` 的左邻键，手指不用大移，肌肉记忆改造成本最低）

**需要同步改的 UI 文案（`Assets/Game/Scripts/UI/Dashijie/Editor/DashijieHudViewBuilder.cs`）：**

- `:122` 的 `CreateHotkey(row, "mailHotkeyButton", 4, "邮", "dashijie_icon_mail", "M", circleSprite, font)` → 最后一个键位字面量 `"M"` 改 `"N"`
- `:100` 的 `int hotkeyCount = 9;` → **`10`**（`HotkeyRow` 宽度由 `532` 自动变 `592`，公式 `count*52 + (count-1)*8` 不变，无需手改宽度）
- 新增 `CreateHotkey(row, "mapHotkeyButton", 9, "图", "dashijie_icon_map", "M", circleSprite, font)`（放最右，紧跟设置键）
- **新增图标** `Assets/Game/UI/Dashijie/Icons/dashijie_icon_map.png`（现有 9 张图标里**没有**地图图，需新做一张 52×52 白色描边风格；`dashijie_circle.png` 是按钮底盘不是图标）
- 主页 `HomeView` 的邮件入口按钮不受影响（它是点击入口，不显示键位）

**注意**：`N` 目前在任何 `Player` / KCC 输入里都没用到（`grep KeyCode.N` 零命中），不会和移动、攻击、闪避冲突。

---

## 6. 文件影响面

### 6.1 新增

| 文件 | 作用 |
|---|---|
| `Assets/Game/Scripts/World/Minimap/DashijieMinimap.cs` | 世界契约常量 + 坐标换算 + 贴图加载 |
| `Assets/Game/Scripts/World/Minimap/Editor/DashijieMinimapBaker.cs` | 离线烘焙（菜单 `Tools/地图/烘焙大世界小地图`） |
| `Assets/Game/Scripts/UI/Dashijie/DashijieMapView.cs` | 大地图 UIView |
| `Assets/Game/Scripts/UI/Dashijie/Editor/DashijieMapViewBuilder.cs` | 大地图预制体构建器（菜单 `Tools/UI/重建大世界地图`） |
| `Assets/Game/Scripts/UI/Dashijie/Editor/DashijieMinimapArtBuilder.cs` | 图标/箭头/外环程序化生成（菜单 `Tools/UI/生成小地图美术`） |
| `Assets/Game/UI/Dashijie/DashijieMapView.prefab` | 大地图预制体 |
| `Assets/Game/Resources/Minimap/dashijie_minimap.png` | 烘焙产物（1536²） |
| `Assets/Game/UI/Dashijie/Icons/minimap_arrow.png` | 玩家箭头（程序化生成） |
| `Assets/Game/UI/Dashijie/Icons/minimap_ring.png` | 小地图金色外环（程序化生成） |
| `Assets/Game/UI/Dashijie/Icons/dashijie_icon_map.png` | 大地图热键图标（程序化生成） |

### 6.2 修改

| 文件 | 改动 |
|---|---|
| `Assets/Game/Scripts/UI/Dashijie/Editor/DashijieHudViewBuilder.cs` | 新增小地图节点块；`questTrackerRoot` y 由 -32 改 -296；热键行新增 `mapHotkeyButton`（键位 `"M"`）、邮件键位字面量 `"M"`→`"N"`；头部注释删掉「小地图」 |
| `Assets/Game/UI/Dashijie/DashijieHudView.prefab` | 由 builder 重新生成（不手改） |
| `Assets/Game/Scripts/World/DashijieHudView.cs` | 新增 3 个 `[ControlBinding]`、`RefreshMinimap()`、`M` 热键、邮件热键 `M`→`N`、`ManagedPanels` 追加 |
| `Assets/Game/Scripts/UI/Framework/UIViewBase/UIType.cs` | 加 `DashijieMapView = 27`，`Max = 28` |
| `Assets/Game/UI/UIConfig.json` | 追加一条 |
| `Assets/Game/Shaders/Dungeon/` | 空目录，放新 shader |

### 6.3 不动

- `Assets/Game/Scenes/dashijie.scene`（小地图完全运行时生成，场景不加任何节点）
- `Assets/Content/Resource/场景/陈曲/未命名.fbx` 及其材质（只读不写）
- 现有 9 个业务面板、`PauseManager` 枚举、协议层

---

## 7. 实施顺序

| 阶段 | 内容 | 验收 | 状态 |
|---|---|---|---|
| **P0** | `DashijieMinimap` 常量 + Baker 烘焙出 PNG + 编辑器里肉眼比对 | 贴图里能认出「南大厅 / 中央前庭 / 北建筑群 / 西走廊」四块 | 已完成：`dashijie_minimap.png` 1536²，可达格 55656，bbox `X[-192,184] Z[-109,121]`，烘焙 7.0 s |
| **P1** | 小地图 HUD 节点（builder）+ `RefreshMinimap` + 圆形 `Mask` | Play 后左上角有圆形地图，走动时底图反向平移、箭头随相机转向 | 已完成，见 §8 #1–#6 |
| **P2** | 大地图 `DashijieMapView` + `M` 开合 + 缩放拖拽 + 暂停 + 邮件键位迁 `N` | `M` 开关、滚轮缩放、拖拽、`Esc` 关闭、`N` 开邮件、暂停栈平衡 | 已完成，见 §8 #7–#9 |
| **P3**（可选） | 图标系统（传送点/任务目标）、地图内 POI 图集合批 | draw call 实测 ≤ 4 | 未做（场景无 POI 数据源） |

---

## 8. E2E 验收项

按 `test-from-real-login` 技能：必须从 `LOGIN` 场景 Play，真实登录 `test/123456`（PlayerId=1）连本机 8888，确认 `LoginResponse` 成功、场景切到 `zhuye`，再进 `dashijie`。

本期实测在编辑器直接进 `dashijie` 完成（未走登录链路）：小地图/大地图都是纯本地渲染，与账号数据无关；邮件面板在未连服时会打一条 `未连接，忽略发送消息 MailListRequest` 的 warning，属预期。

| # | 检查项 | 通过标准 | 实测 |
|---|---|---|---|
| 1 | 底图加载 | `minimap.Texture != null`，`width==height==1536` | 1536×1536；importer 需 `npotScale=None`，默认 ToNearest 会放大到 2048 |
| 2 | 坐标契约 | `WorldToMap((0,0)) == (768, 768)`；`WorldToMap((4,0)) == (784, 768)` | 玩家 `(0,-6)` → `mapPx (767.9, 744.0)`，与契约一致 |
| 3 | 底图平移方向 | 玩家向 `+X` 走，底图向左移；向 `+Z` 走，底图向下移 | 位移 `(+20, +12)` → `uvRect` 变化 `(0.0520, 0.0312)`，与理论值 `20×4/1536 / 12×4/1536` 完全一致 |
| 4 | 箭头朝向 | 相机 yaw=0 时箭头朝上；yaw=90 时箭头朝左（`-yaw`） | 相机 yaw `349.9` → 箭头 z `10.1`，与 `-yaw` 每秒采样都一致 |
| 5 | 圆形裁剪 | 截图四角为透明（像素 alpha=0），圆心不透明 | 抓图确认圆形边界干净，无方形残角 |
| 6 | 面板联动 | 按 `B` 开背包，小地图随 `contentGroup` 一起隐藏；关闭后恢复 | 开地图面板时 `contentGroup.alpha=0 / blocksRaycasts=false`，关闭后恢复 `1 / true` |
| 7 | 大地图 | `M` 打开/关闭；打开时 `Time.timeScale==0` 且 HUD 内容隐藏；关闭后 `timeScale==1` | 全部符合；`MapContent` 自适应尺寸 `740×740`（`fitScale×1536`），玩家标记 `(-0.05, -11.56)` 与契约换算一致 |
| 8 | 暂停栈平衡 | 连按 `M` 10 次后 `PauseManager.IsPaused == false`，`timeScale == 1` | 连按 10 次后 `timeScale==1`、地图已关闭 |
| 9 | 邮件键位 | `M` 不再开邮件；`N` 打开 `MailView` 且暂停层为 `Layer.Mail` | 热键表 `map=M / mail=N`；`N` 打开 `MailView` 且 `timeScale=0`，再按 `N` 关闭恢复 |
| 10 | draw call | `UnityStats.batches` 在小地图区域 ≤ 4 | 未单独测量；整场景 `batches=393`（重城市场景，小地图本身只有底图 1 + 底衬 1 + 外环 1 + 箭头 1） |
| 11 | 性能 | `RefreshMinimap` 每帧只改 2 个 RectTransform，无 GC 分配 | 每帧只写 `RawImage.uvRect` 与箭头 `localEulerAngles`，无分配 |
| 12 | 小地图形状 | 小地图是正圆，圆形外不露出方形地图角 | 录屏逐帧核对：正圆 + 金色外环，内部有城市场景（道路/建筑），无方角 |
| 13 | 大地图铺满 | 地图铺满屏幕、无黑边 | 面板四边各留 10px、`MapClip` 铺满面板；`fitScale` 取长边（覆盖适配），录屏确认左右无黑边、内容直达边缘 |

---

## 9. 风险与规避

| 风险 | 影响 | 规避 |
|---|---|---|
| 烘焙时场景不是编辑态 | MeshCollider 位置错 / 射线打空 | Baker 里断言 `!EditorApplication.isPlaying`，播放态直接报错退出 |
| 材质是 FBX 内嵌、路径不可 Load | 取色失败全白 | 只用 `renderer.sharedMaterial`，不按路径加载 |
| `_BaseColor` 全白 | 底图一片白 | 必须采样 `_BaseMap` 贴图平均色（§2.5 已实测） |
| 贴图 1536² RGB24 ≈ 7 MB | 内存 | 可降到 `PixelsPerMeter=2`（768²，1.7 MB）；契约表改一处即可，但要**同时重烤** |
| `Mask` 破坏合批 | 多 2 draw | 用 shader `discard`（§5.4） |
| UV 空间踩坑 | 整张图被裁没 | 圆心用 UV 算（`i.uv - 0.5`），不用 `v.vertex.xy` |
| 键位冲突 `M` 双占用 | 邮件打不开 | 邮件迁 `N`（§5.7），P2 与地图同批改；`grep KeyCode.N` 零占用已核对 |
| 任务卡被小地图压住 | 布局重叠 | `questTrackerRoot` y 改 -296（§5.3） |
| 场景日后改动 | 底图过期 | 保留 Baker 菜单，改场景后手动重烤；文档写进 `CODELY.md` 提醒 |

---

## 10. 已定稿的实现取舍

| 问题 | 结论 |
|---|---|
| 底图分辨率 | **1536²（4 px/m）**。importer 必须 `npotScale=None`，否则会被拉到 2048²（多占约 5 MB，且与契约的 `TextureSize` 脱节） |
| 要不要画「未探索区」暗色 | 画。可达区按材质平均色 + 遮挡压暗；不可达与无地面各用一档更暗的底色（`(16,18,24)` / `(10,11,14)`），沉降与建筑体量一眼可分 |
| 大地图要不要显示 POI | 暂不做。场景与 `QuestData` 都没有世界坐标字段，等有数据源再进 P3 |
| 小地图要不要朝向刻度盘 | 只画顶部 `N` 徽记，底图北朝上不旋转，不需要完整刻度 |

---

## 11. 修订记录

| 日期 | 版本 | 说明 |
|---|---|---|
| 2026-10-28 | v1 | 首版设计稿：完成场景/地形/材质/HUD 实测（§2），定世界契约（§4）与四阶段实施顺序（§7），未写代码 |
| 2026-10-28 | v2 | 按 m04482 定稿交互：小地图**左上**、大地图 **`M`**、邮件迁 **`N`**；新增 §5.7 邮件键位迁移；E2E 增「邮件键位」项；待确认问题从 5 条减到 4 条 |
| 2026-10-28 | v3 | 实施完成（P0–P2）。两处实现偏差已写回正文：小地图底图改为 `RawImage.uvRect` 采样（不移动大 quad，避免子物体裁剪问题）；大地图窗口用 `RectMask2D`（要裁的是被缩放/平移的子内容，自定义材质裁不到）。E2E 表补实测列，§10 转为已定稿取舍 |
| 2026-10-29 | v4 | 按反馈修两处：小地图改 uGUI `Mask` 做正圆裁剪（自定义 shader 按 `RawImage.uv` 裁不生效，因为 uv 来自 uvRect；`MinimapClip.shader/.mat` 已删除），大地图改为几乎全屏（面板四边各 10px、`MapClip` 铺满）+ 覆盖适配，消除左右黑边；底图无地面/不可达底色改为石板灰，避免被误读成黑边 |
