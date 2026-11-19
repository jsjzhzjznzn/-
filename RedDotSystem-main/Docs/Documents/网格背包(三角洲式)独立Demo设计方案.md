# 网格背包（三角洲式）独立 Demo 设计方案

> 目标：用 uGUI 实现一个「非规则网格背包」demo——背包是一个由格子组成的矩形区域，每件物品占据矩形块（1×1、1×2、2×3……），
> 可以左键拖整包移动、右键旋转、从右侧货物清单拖入、拖出背包丢弃、拖到另一件物品上互换位置。
> 做成完全独立的 demo 场景 `beibao`（实际文件名 `biebao`），不依赖登录 / UI 框架 / YooAsset / 服务端，打开场景直接 Play 即可运行。

---

## 1. 现状分析

| 项 | 现状 | 对本 demo 的影响 |
| --- | --- | --- |
| 现有背包 `BagView` | 一维槽位列表（武器区 + 道具区），每槽 1 件物品，服务端权威 | 与本 demo 的数据模型完全不同，不复用其数据层 |
| 现有交互样板 | `BagSlotView` 已确立「格子实现空 `IDragHandler` 占位，起手后把 `eventData.pointerDrag` 抢到控制器」的写法 | 直接沿用，避免「拖不动 / OnEndDrag 收不到」两类老问题 |
| 场景 | `Assets/Game/Scenes/biebao.scene` 已存在且为空（只有 Main Camera + 平行光），未登记 Build Settings | 在该场景上搭建 |
| 中文字体 | `ChineseFontLocator`（Resources 三级回退，`Fonts/NotoSansSC-Regular SDF` 存在） | 所有程序化 TMP 文本用它 |
| 物品图标 | `Assets/Game/Resources/ui/80x80/` 11 个 80×80 方形图 + `ui/鸣潮：武器图标/` 7 个 369×512 竖图；`ItemIconLoader` 按 iconKey 加载 | 全部复用，不新增美术 |
| 面板美术 | `bag_cell.png`（128×128 白色描边，九宫 24）、`ui/Theme/home_bg.png`（浅色童话风背景） | 格子底、背景 |
| 程序化美术构建器 | `HomeViewSkinBuilder` 已确立：`File.WriteAllBytes` → `ImportAsset` → `TextureImporter`(Sprite/Single/Uncompressed/mipmap off) → `SaveAndReimport` | 照抄该导入流程 |

结论：**新增一套独立的数据层 + 视图层**，交互写法沿用 `BagSlotView` 的既有约定，美术全部复用现有资源；不碰现有背包系统与协议。

---

## 2. 设计总览

```
┌───────────────────────────────────────────────────────────────────────┐
│  网格背包 Demo                                      容量 44 / 60      │
├──────────────────────────────────────────┬────────────────────────────┤
│  10 × 6 格子区                            │  货物清单（可拖入）        │
│  ┌──┬──┬──┬──┬──┬──┬──┬──┬──┬──┐        │  ┌────┐ ┌────┐ ┌────┐     │
│  │▓▓│▓▓│▓▓│▓▓│▓▓│▓▓│▓▓│▓▓│▓▓│▓▓│        │  │图标│ │图标│ │图标│     │
│  │▓▓│▓▓│▓▓│▓▓│  │  │  │  │▓▓│▓▓│        │  └────┘ └────┘ └────┘     │
│  │▓▓│  │  │▓▓│▓▓│  │  │  │  │  │        │   弹药   弹匣   芯片        │
│  │  │  │  │  │  │  │  │  │  │  │        │  ……（可滚动的 6 列网格）    │
│  └──┴──┴──┴──┴──┴──┴──┴──┴──┴──┘        │                            │
└──────────────────────────────────────────┴────────────────────────────┘
        ▲ 左键拖动整块；右键/中键点击旋转；拖到面板外丢弃；拖到其他物品上互换
```

- 背包区：10×6 = 60 格，格子 64px（间距 4px，pitch 68）。
- 货物清单：右侧可滚动的独立网格（6 列），每格一件「未入包」物品，拖到背包合法位置即入包；拖回清单纯取消。
- 物品由「形状 + 朝向」决定占位：形状是 `int[]` 位图（行优先，1=占格），朝向有 4 个（0°/90°/180°/270°）。
  物品在背包里以左上角锚点 + 旋转后形状占据一组格子。

---

## 3. 数据层设计

### 3.1 物品与形状（`GridItemDef`，ScriptableObject）

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `itemId` | string | 唯一标识（构建器写入「序号_图标名」，保证同图标多物品不撞车） |
| `displayName` | string | 中文名 |
| `iconKey` | string | `ItemIconLoader` 的键 |
| `weight` | int | 重量（展示用，累计显示在标题栏） |
| `shape` | `int[]` | 形状位图，长度 = w×h，行优先；1=占格 |
| `shapeWidth` / `shapeHeight` | int | 位图尺寸 |

`SetRectShape(w,h)` 生成全 1 位图；`SetShape(w,h,mask)` 给自定义形状（L 形等）。

### 3.2 物品实例（`GridItem`，普通类）

| 成员 | 说明 |
| --- | --- |
| `Def` | 定义 |
| `Rotation` | 0~3 朝向（顺时针） |
| `Anchor` | 左上角格坐标（只在背包内有效） |
| `SizeX` / `SizeY` | 旋转后包围盒宽高（r 为奇数时互换） |
| `IsFilled(x, y, rotation)` | 取某朝向下某格是否占格（形状投影的唯一实现） |

### 3.3 背包（`GridBag`，普通类，无 Mono 依赖）

- `Width` / `Height`；占用表 `GridItem[] cells`（直接存物品引用，查占用 O(1)）。
- `CanPlace(item, anchor, rotation)`：包围盒内每个「形状=1」的格子都必须 ① 在网格内；② 空或被 `item` 自己占用。
  矩形包围盒内的「空透镜」不受限制——这是非矩形物品能互相咬合的关键。
- `Place` / `Remove`：写占用表并维护 `UsedCells`（按形状格数计，不按包围盒计）。
- `TryReplaceAt(incoming, target, ...)`：把已拿出背包的物品与落点物品互换（只摘 target，双方都能落到对方位置才换）。
- `TryFindNear(item, rotation, preferred, out anchor)`：按格距离由近到远找第一个可放位置（清单拖入的「吸位」）。
- `FillFromDefs(defs)`：初始化按配置顺序贪心放置。

---

## 4. 视图层设计

### 4.1 场景结构（`biebao.scene`）

```
Main Camera / Directional Light
GridBagDemo（预制体实例 = 组合根）
└── Canvas（1920×1080，CanvasScaler Match 0.5，GraphicRaycaster）
    ├── EventSystem（StandaloneInputModule）
    ├── Backdrop      铺满，home_bg 压暗到 0.42
    ├── DragLayer     拖动层（拖影，raycastTarget=false）
    ├── Sheet         左右两块面板的底板
    │   ├── BagPanel   标题「网格背包 Demo」+ 容量/重量 + 提示 + BagArea
    │   │   └── BagArea（GridBagView + GridBagDragTarget + 背板 Image）
    │   │       ├── Cells   60 个格子底（bag_cell 九宫）
    │   │       └── Items   物品视图池 + DropPreview（落点预览）
    │   └── LootPanel  标题「货物清单（拖到背包里）」+ 提示 + ScrollRect
    │       └── LootViewport(RectMask2D) / LootContent(GridLayoutGroup 6 列) / LootTemplate
    └── Toast        操作提示（CanvasGroup 淡出用）
```

所有节点由编辑器构建器生成并序列化进预制体，运行时不做 `Find`，也不做程序化美术生成（形状衬底在构建期烘焙）。

### 4.2 渲染方式

- **格子底**：建一次 60 个 `Image`，不随物品增减重建；空格深灰 `0.16`、被占更深 `0.11`。
- **物品**：每件物品一个 `Image`（icon，`preserveAspect`）+ 一层「形状衬底」`Image`；
  rect 尺寸 = 旋转后包围盒（格数 × 68 − 4），位置 = 锚点格左上角。
  形状衬底是构建期按「物品 × 朝向」烘焙的纯白 + 1px 浅蓝描边 PNG（FullRect），非矩形物品的空透镜因此能如实显示。
- 物品视图不参与射线（`raycastTarget=false`），命中判定统一由 `BagArea` 上的 `GridBagView` 按坐标换算。

### 4.3 交互规则

| 操作 | 位置 | 结果 |
| --- | --- | --- |
| 左键按下并移动 | 背包内物品 | 从数据层摘出、视图隐藏，生成跟随光标的拖影 + 落点预览 |
| 松手 | 背包内 | 落点合法 → 放入；落点压着另一件 → 尝试互换；越界/被挡 → 回原位 |
| 右键 / 中键点击 | 背包内物品 | 顺时针旋转 90°；原地放不下则就近找位；仍不行则保持原朝向并提示 |
| 拖动中（自动转向） | 背包内物品 | 当前朝向放不下、转 90°/270°/180° 后放得下时，**预览立即改为可放朝向**，松手按该朝向落位并提示「已自动转向」；当前朝向本来就放得下时不擅自转向 |
| 拖入（自动转向） | 右侧货物清单 | 清单物品没有「当前朝向」，横竖都会试：先按 0° 找最近的合法位置，找不到再用 90°/270°/180° |
| 拖入 | 右侧货物清单 | 落在背包内 → 吸到光标附近最近合法位置；落在背包外 → 取消，留在清单 |
| 拖出 | 背包内 | 拖到面板外松手 → 丢弃并提示 |

回滚是事务式的：起手记录 `anchor + rotation`，失败按记录还原，不产生「先删后加」的中间态。

---

## 5. 文件影响面

### 运行时（`Assets/Game/Scripts/GridBagDemo/`）

| 文件 | 职责 |
| --- | --- |
| `GridItemDef.cs` | 物品定义（形状位图 + 展示信息） |
| `GridItem.cs` | 物品实例（朝向、锚点、旋转后的形状投影） |
| `GridBag.cs` | 数据层：占用表、合法性校验、放置/移除/互换/自动找位 |
| `GridBagLayout.cs` | 坐标换算：格坐标 ↔ 本地坐标、物品/预览矩形 |
| `GridBagItemView.cs` | 单件物品视图（形状衬底 + 图标 + 重量角标） |
| `GridBagView.cs` | 背包视图：格子底、物品视图池、落点预览、起手与坐标换算 |
| `GridBagDragTarget.cs` | 背包拖拽的拖动/松手承接者 |
| `GridBagLootSlotView.cs` | 清单格（图标 + 名称 + 拖拽起点） |
| `GridBagLootView.cs` | 货物清单视图 |
| `GridBagDemo.cs` | 组合根：数据、两套视图、拖影、Toast、松手规则、调试入口 |

### 编辑器（`Assets/Game/Scripts/GridBagDemo/Editor/`）

| 文件 | 职责 |
| --- | --- |
| `GridBagDemoBuilder.cs` | 菜单 `Tools/Demo/重建网格背包 Demo`：幂等生成物品定义、形状衬底、预制体，在 `biebao` 场景放置实例并登记 Build Settings |

### 资产

- `Assets/Game/UI/GridBagDemo/GridBagDemo.prefab`
- `Assets/Game/UI/GridBagDemo/Shapes/*.png`（18 物品 × 4 朝向 = 72 张）
- `Assets/Game/Scripts/GridBagDemo/Defs/*.asset`（18 件物品定义）

### 修改

- `ProjectSettings/EditorBuildSettings.asset`：新增 `Assets/Game/Scenes/biebao.scene`。
- 不改现有背包系统、协议、服务端；不新增包依赖。

---

## 6. E2E 验收项

1. **场景可独立运行**：打开 `biebao` 直接 Play，画面完整渲染（背包 + 清单 + 提示）。
2. **初始布局**：物品全部落在网格内且互不重叠，容量 = 各物品形状格数之和。
3. **拖动移动**：拖到空白合法区域 → 锚点更新、原格释放、新格占用。
4. **非法回弹**：拖到越界/被占位置松手 → 回到原位，占用表不变。
5. **右键旋转**：1×4 → 4×1；放不下则保持原朝向 + 提示。
6. **互换**：能互相接住就换位；否则整体回滚。
7. **清单拖入**：落到空隙 → 入包并吸到最近合法位置；拖到背包外 → 仍在清单。
8. **丢弃**：拖到面板外松手 → 从占用表删除、视图回收，统计同步。
9. **视觉**：中文字体、图标、形状衬底、拖影、落点预览、Toast 正常，无黑屏/白块/裁切。
10. **回归**：编译 0 error；不向 `biebao` 以外的场景写入改动。

---

## 7. 风险与取舍

- **形状视觉依赖构建期烘焙**：新增物品必须走构建器重建，不会被随手另存的物品资产自动补图。
- **射线统一由 BagArea 处理**：要求 Canvas 为 Screen Space（相机或叠加）且 BagArea 无旋转缩放，由构建器固化。
- **独立 demo 不复用 `BagManager`**：不接服务端与存档；将来接真实背包需要把 `GridBag` 作为服务端槽位模型的投影层再评估。
- **美术风格**：复用现有图标与浅色背景，与三角洲写实战术风不同，只做功能演示。
- **设备适配**：固定 1920×1080 参考分辨率，窄屏/竖屏不在验收范围。

---

## 8. 实施顺序

1. 数据层 `GridItemDef` / `GridItem` / `GridBag` / `GridBagLayout`。
2. 构建器：物品定义 + 形状衬底 + 预制体 + 场景 + Build Settings。
3. 视图层与交互（`GridBagView` / `GridBagItemView` / `GridBagLootView` / `GridBagDemo`）。
4. Play 验证：按第 6 节逐条 E2E。

---

## 9. 实现与实测结论

### 9.1 落地形态

- 场景：`Assets/Game/Scenes/biebao.scene`（已登记 Build Settings，排在 9 个既有场景之后）。场景内只有 Main Camera、平行光与预制体实例 `GridBagDemo`。
- 预制体：`Assets/Game/UI/GridBagDemo/GridBagDemo.prefab`，根节点自带 Canvas(1920×1080 参考、Match 0.5) + CanvasScaler + GraphicRaycaster + EventSystem + `GridBagDemo`。
- 资产：`Defs/*.asset` 18 件物品（14 件初始、4 件留在清单），`Shapes/*.png` 72 张形状衬底。
- 背包为 **10×6 = 60 格**（初稿按 8×5 写，实现时按「初始物品全部放下且留出约 1/4 空格」调大；初始占用 44 格）。
- 形状：8 种矩形（1×1、1×2、2×1、2×2、2×3、1×4）+ 2 种 L 形（`破损弹匣` 2×2 取 3 格、`消音器` L 形），L 形用于演示空透镜被 1×1 物品嵌入。
- 图标复用 `Assets/Game/Resources/ui/80x80` 与 `ui/鸣潮：武器图标`；格子底复用 `bag_cell.png`；背景复用 `home_bg.png`（亮度压到 0.42）。
- 拖拽：背包内物品与清单物品两套起点，起手后 `pointerDrag` 分别交给 `GridBagDragTarget`（挂在背包区域节点）与组合根自身。

### 9.2 实测结论（Play 模式，1920×1080）

| 验收项 | 结果 |
| --- | --- |
| 初始布局 | 14 件全部放入：`UsedCells=44`、占用表逐格统计 44、形状格数之和 44，三者一致 |
| 拖动移动 | 1×4 步枪 (0,0) → (8,2)，锚点更新、原格释放、新格占用，计数仍 44 |
| 非法回弹 | 拖到被占格 / 越界（1×4 停到最底行）→ 锚点回起点，占用表与计数不变 |
| 旋转 | 原地可转直接转；被挡时就近找位（实测 (8,2) → (6,5) 且转成 4×1）；都放不下则保持原朝向并提示 |
| 互换（可换） | 1×1 ↔ 1×1 成功互换，锚点对调，计数一致 |
| 互换（不可换） | 1×2 → 1×1 整体回滚，锚点与计数完全不变 |
| 清单拖入 | 落到光标附近最近合法位置，该清单格随即置空 |
| 丢弃 | 拖到面板外松手 → 占用表移除、视图回收、容量与重量同步 |
| 拖影 / 落点预览 | 起手后拖影启用并带图标跟随光标；合法 `RGBA(0.35,0.78,0.45,0.55)`、非法 `RGBA(0.90,0.30,0.28,0.55)`；拖出面板预览隐藏、松手拖影关闭 |
| 视觉 | 录像确认中文可读、图标齐全、物品块与格子对齐、拖动与旋转过程可见、Toast 正常，无黑屏/白块/裁切 |

### 9.3 实现中踩到并修掉的问题

1. **同一文件里放多个 MonoBehaviour**：`GridBagDragTarget`、`GridBagLootSlotView` 起初与视图类同文件，Unity 拿不到 MonoScript，序列化成 `m_Script: {fileID: 0}`，运行时引用为 null（清单拖拽静默失效）。拆成同名独立文件后恢复。
2. **拖拽接口的选择**：`EventSystem` 用 `GetEventHandler<IDragHandler>(命中物)` 决定 `pointerDrag`，只实现 `IBeginDragHandler` 的组件收不到拖拽；起手后还必须把 `pointerDrag` 改指到另一个对象，否则等于自指、物品拖不动。清单格因此补了空实现 `IDragHandler`，背包视图把事件转交 `GridBagDragTarget`。
3. **空的 `pressEventCamera`**：画布在运行期被框架 `UICameraService` 接管成 ScreenSpaceCamera，按下时 `pressEventCamera` 可能为空；把屏幕像素直接当本地坐标会让落点整体错位（实测把 (0,0) 的按下算成 (8,0)）。`ScreenToLocal/IsInside/ScreenToCell` 现在统一在相机为空时回退到 `canvas.worldCamera`。
4. **互换的计数漂移**：起手时物品已从数据层摘出，旧实现仍按「两件都在背包里」各摘一次再各放一次，回滚分支多加了格数（实测 44→46）。改为 `GridBag.TryReplaceAt`：只摘落点那件，双方都能落到对方位置才交换。
5. **形状旋转索引**：`IsFilled` 的 90°/270° 映射写成 `shape[y*w + (w-1-x)]` 会越界；正解 `r=1 → shape[(h-1-x)*w + y]`、`r=3 → shape[x*w + (w-1-y)]`。
6. **`TextureImporter.spriteMeshType` 在本分叉不可用**：改走 `TextureImporterSettings` + `ReadTextureSettings/SetTextureSettings`，形状衬底设为 FullRect（低 alpha 纯色按 Tight 裁网格会画不出来）。
7. **同图标多物品**：两件物品共用一张图标时，定义与形状衬底文件名会撞车；资产文件名带序号前缀（`07_T_IconWup80_Mo_017_UI`）。

### 9.4 自适应转向（三角洲式）

- 数据层：`GridBag.ResolveRotation(item, anchor, preferred)` 返回「当前朝向放得下就用它，否则按顺时针 90° → 270° → 180° 依次试」的结果；`TryFindNear(item, rotation, preferred, out anchor, out rotation)` 找不到位置时按同一顺序换向重找（供清单拖入与初始布局使用）。
- 视图层：`GridBagView.UpdatePreview` 每次移动都用 `ResolveRotation` 算出 `DropRotation`，预览矩形按该朝向渲染；松手时把同一个 `DropRotation` 交给组合根落位——预览所见即落位所得。`DropRotation` 是单一来源，避免「预览转了、落位没转」。
- 实测：把 1×4 步枪拖到只能横放的缝隙 (1,5) 时，`DropRotation` 由 0 变 1、预览尺寸由 64×268 变 268×64 且为绿色，松手落在 (1,5) 且 `Rotation=1`、`UsedCells` 与占用表一致，Toast 显示「灰烬步枪 已自动转向」；拖到竖放合法的 (8,2) 时 `DropRotation` 保持 0、不擅自转向。
- 数据层单测：4×1 的板子里 1×3 物品 `TryFindNear` 返回 rotation=1（竖放不可能）；3×4 的板子里返回 rotation=0；`ResolveRotation` 在 (0,3)（下方无空间）返回 1、在 (0,0) 返回 0。
- 手动旋转（右键/中键）仍然可用，且旋转后放不下时依旧走「就近找位」。

### 9.5 当前边界

- 无堆叠/数量：每件物品独占一组格子（三角洲的弹药堆叠未做）。
- 无容器嵌套（背包里放背包）、无装备槽、无右键菜单、无音效。
- 旋转入口为右键/中键点击；滚轮旋转未接（滚轮留给清单滚动）。
- 固定 1920×1080 参考分辨率，未做窄屏/竖屏适配。
- 新增物品需走菜单 `Tools/Demo/重建网格背包 Demo` 重建（幂等：形状 PNG 已存在则跳过重写）。