# 陈曲 未命名.fbx 渲染美化方案

> 目标资产：`Assets/Content/Resource/场景/陈曲/未命名.fbx`
> 贴图目录：`Assets/Content/Resource/场景/陈曲/Tex/`（1786 个 PNG）
> 本文所有数字均来自编辑器实测（`AssetDatabase` / `ModelImporter` / `TextureImporter` / `Renderer.bounds` 与 PNG 像素统计），不是估算。
> 状态：**仅方案，尚未修改任何资产。**

---

## 0. 一句话结论

这栋模型现在难看的根本原因不在"没有 shader"，而在**三层数据是断的**：

1. **240 个材质只接了 1 张 base map**，法线 / 光滑度 / 金属度贴图在硬盘上躺着（184 / 184 / 208 张）却一个都没接；
2. **576 张非颜色贴图全被当成 sRGB 的 Default 贴图导入**（`textureType=Default, sRGB=True`），法线贴图连"Normal map"类型都不是，接上去也是错的；
3. **全场景只有 1 盏主光 + 阴影距离 50**，而模型包围盒是 **3862 × 765 × 4201**（对角线 5758）。阴影距离 50 的直射光对 3800 单位宽的城市场景等于**完全没有影子**，所以画面必然是"一片平涂的灰模"。

所以正确顺序是：**先修数据 → 再选 shader → 最后调光照与后处理**。只换 shader 不动前两层，换什么都会难看。

---

## 1. 现状诊断（实测）

### 1.1 模型规模

| 项 | 实测值 |
|---|---|
| 文件大小 | 57,072,236 字节（57 MB） |
| 类型 | `Model`，`materialImportMode = ImportViaMaterialDescription`，`materialLocation = **InPrefab**` |
| 网格 | 240 个 Mesh；**239 SkinnedMeshRenderer + 1 MeshRenderer**（那个是 `Cube`） |
| 三角面 | **1,206,000+ tris**；顶点 1,850,000+ |
| 骨骼 | 239/239 都带 bones（是蒙皮导出的，但实际是静态建筑，**白付蒙皮开销**） |
| UV | `uv` 有、`uv2`(lightmap) 无、`tangents` 有（Mikk 计算）、`vertex color` 无 |
| 内置相机 | 1 个：pos(-7.4, 5.0, 6.9)、fov 27、near 0.1、**far 100** |
| 内置灯 | 1 个 Point Light，intensity 1000 |
| 包围盒 | center(-283.3, 223.0, 252.4)，size(3862.7, 764.9, 4201.5)，min(-2214.7, -159.5, -1848.3)，max(1648.0, 605.4, 2353.1) |
| 分布 | 236/239 个渲染器的中心都在原点 500 单位内（是紧凑的城市街区，不是空旷大地图） |

> `Belobog` 节点存在（belobog_arm 骨架），这是崩坏星穹铁道贝洛伯格城的资产。当前它已经作为 `未命名 (1)` 实例放在 `Assets/Game/Scenes/duihua.scene` 里（`PrefabInstanceStatus = Connected`，scale 1，pos 0,0,0）。

### 1.2 材质现状（240 个，全部内嵌在 FBX 内）

| 项 | 实测 |
|---|---|
| Shader | **240/240 全部 `Universal Render Pipeline/Lit`** |
| `_BaseMap` | 239 个有（1 个是 `Cube` 的 `Material`，无贴图） |
| `_BumpMap` | **0 个** |
| `_SpecGlossMap` / `_MetallicGlossMap` | **0 个** |
| `_EmissionMap` | **0 个** |
| `_Smoothness` | **239 个全是 0.5**（无差异，等于没有材质区分） |
| `_Metallic` | 未设（全是默认 0） |
| `_BaseColor` | **239 个全是纯白 (1,1,1,1)** |
| `_BaseMap_ST` | **全部 (1,1,0,0)**，无平铺差异 |
| `_Surface` | 239 个全 = 0（opaque），**没有一个透明** |
| `_AlphaClip` | 239 个全 = 0 |
| `_Cull` | 全 2（Back），无一个双面 |
| base 贴图来源 | 239/239 都在 `Assets/Content/Resource/场景/陈曲/Tex/`，单目录无分散 |

**结论：这是一个"只有 albedo 的灰模级"导入结果。** 材质的全部个性（粗糙度、金属感、表面凹凸）都在未接的贴图里。

### 1.3 贴图现状（1786 个 PNG）

按命名分类：

| 类别 | 数量 | 说明 |
|---|---|---|
| base（`Tex_XXXX.png`） | 227 | 颜色贴图 |
| extra（`Tex_XXXX_A1/A2/_2/...`） | 51 | base 的变体 |
| `_Normal` | 184 | 法线 |
| `_Gloss` | 184 | 光滑度（灰度） |
| `_Metalness` | 208 | 金属度（灰度） |
| `_A` | 39 | 遮罩/alpha |

尺寸分布：`1024×1024` ×390、`512×512` ×289、`256×256` ×147、`128×128` ×21、**`2048×2048` ×18**、`64×64` ×16、`128×256` ×1、`32×32` ×2、`5×5` ×9。

**导入设置（抽样 5 张，全部一致）：**

| 文件 | textureType | sRGB | mipmap | maxSize | 压缩 |
|---|---|---|---|---|---|
| `Tex_0001.png` | **Default** | **True** | True | 2048 | Compressed |
| `Tex_0001_Normal.png` | **Default** ← 错 | **True** ← 错 | True | 2048 | Compressed |
| `Tex_0001_Gloss.png` | **Default** ← 错 | **True** ← 错 | True | 2048 | Compressed |
| `Tex_0001_Metalness.png` | **Default** ← 错 | **True** ← 错 | True | 2048 | Compressed |
| `Tex_0001_A.png` | Default | True | True | 2048 | Compressed |

三处错误：
- `_Normal` 必须是 `textureType = NormalMap`（否则采样时不会做 `UnpackNormal`，切线空间法线会被人当颜色处理）；
- `_Normal` / `_Gloss` / `_Metalness` 必须是 **sRGB = false**（线性数据被当 sRGB 解码后，数值会被 gamma 曲线压扁）；
- `_A` 若当遮罩用也应线性。

**像素统计（证实贴图本身是好的，只是没接）：**
- `_Normal` 均值 ≈ (0.50, 0.50, 0.96~0.99) → **标准切线空间法线图**，B 通道接近 1，完全正确可用。
- `_Gloss` 均值 0.18~0.53，std 0.085~0.208 → 有真实变化的粗糙度图。
- `_Metalness` 中位数 0.359；208 张里 32 张恒定 1.0、大量恒定 0.0 → 是有明确金属/非金属分区的遮罩图。
- `_A` 均值 0.10~0.97、std 可达 0.49 → 明显是镂空/遮罩图（树叶类）。
- base 贴图**没有一张带真 alpha**（227/227 `alpha min = 255`）→ 半透明只能靠 `_A` 遮罩 + alpha clip，不能靠 base 的 alpha 通道。

### 1.4 材质 ↔ 贴图对应关系（关键可行）

用 base 贴图文件名反推同名 `_Normal/_Gloss/_Metalness`：

| 情况 | 材质数 | 处理 |
|---|---|---|
| base 名直接有全套 N/G/M | **175** | 直接接，无需换算 |
| base 名剥掉 `_A1/_A2/_2/_数字` 后缀后有 N/G | **21** | 剥后缀后接（例：`Tex_0002_2` → `Tex_0002_Normal`） |
| 完全没有 N/G | **43** | 不接法线，只接 metal（其中 8 个有 `_Metalness`） |
| 有 `_A` 遮罩 | **15** | 加 alpha clip（树叶类，重点看 `材質10/14/16/25/27/66/110/150/172/173/174/175/176/210/223`） |

→ **196/239 个材质能接上法线贴图，覆盖率 82%。** 这是性价比最高的一步。

### 1.5 光照与环境现状

**URP 配置（`URP-PipelineAsset_High`）：**

| 项 | 值 | 对本题的影响 |
|---|---|---|
| HDR | True | 好 |
| MSAA | 4x | 好，但 1200 万三角 + 4x MSAA 有开销 |
| **shadowDistance** | **50** | **致命**：模型 3800 单位宽，50 单位外无阴影 |
| shadowCascadeCount | 2 | 少；3800 单位应 4 级 |
| mainLightShadowmapResolution | 2048 | 偏小，城市级应 4096 |
| additionalLightsPerObject | 4 | 够用 |
| softShadows | True | 好 |
| SRP Batcher | True | 好（240 个不同材质必须开） |
| supportsCameraDepthTexture | **False** | 若要做深度雾/描边需打开 |
| ColorSpace | Linear | 好 |

**`duihua.scene` 环境：**
- `ambientMode = Flat`，`ambientLight = (0.150, 0.160, 0.190)`，强度 1
- `skybox = Default-Skybox`（Unity 默认灰蓝渐变，**不是美术天空盒**）
- `fog = True`，`ExponentialSquared`，density 0.002，color (0.45, 0.50, 0.58)
- 主光 `Directional Light`：rot(48, 330, 0)，intensity **1.35**，soft shadow，shadowStrength 0.85
- Volume `PostFX_Volume` → profile `duihua_PostFX`：Tonemapping / ColorAdjustments / WhiteBalance / Bloom / Vignette / ShadowsMidtonesHighlights 全 active
- 场景主相机：pos(0, 1.7, -9.6)，rot(8, 0, 0)，fov 45，far 5000

→ **相机在 (0,1.7,-9.6) 但模型是 3800 单位宽的城市**，等于站在城市正中心仰视，加上 far=5000 能看到全部 → 这大概率就是"一点也不好看"的直接观感来源之一：**没有构图**。

### 1.6 shader 选型实测

**A. `Assets/ThirdParty/YSAToon/Shaders/Toon Lit Shader/ToonLit_Code.shader`**（628 行，`YSA Toon/Toon Lit Code`）

- 3 个 Pass：`UniversalForward` / `ShadowCaster` / `DepthOnly`（无 DepthNormals、无 Meta）
- 71 个属性，包含：`_BaseMap`、`_BaseColor`、`_BaseMapTiling`、`_Alpha_Clipping`、渐变着色（`_Gradient_Enabled`/`_GColor_1/2`）、主光（`_Ambient`/`_MainLightMidPoint`/`_MainLightSmoothness`）、附加光、`_SpecularMap`（SDF）、Rim、`_OcclusionMap`、`_Emission`
- **致命限制：没有 `_BumpMap` / 法线贴图属性**（grep 全文 0 命中）
- 另有 `Shaders/Outline Shader/Perfect Outline.shadergraph`（描边，需配合相机)

**B. 大社当时自写的 `NarukamiStylized.shader` → 已不存在**

`find Assets -name "NarukamiStylized*"` 返回 0。`Assets/Content/Resource/场景/大社/` 整个目录已从工程里移除（`ls` 报 No such file）。所以不能复用，要重写。

**选型结论：**

| | YSAToon `ToonLit_Code` | 自写 `Chenqu/StylizedLit` |
|---|---|---|
| 法线贴图 | ❌ 无 | ✅ |
| 用上 184 张 `_Normal` | 不能 | 能 |
| 用上 184 张 `_Gloss` | 仅当 SDF 用，语义错 | ✅ 作 roughness |
| 用上 208 张 `_Metalness` | ❌ | ✅ |
| 遮罩 alpha clip | ✅ `_Alpha_Clipping` | ✅ |
| 卡通阶梯明暗 | ✅ 成熟 | 需自写平滑 `smoothstep` |
| 对建筑是否合适 | ⚠️ SDF/半身渐变是给角色脸/身体设计的，对墙瓦无效 | ✅ 可控 |
| 改动风险 | 低（现成） | 中（要编译验证） |
| 与工程既有 Toon 一致性 | ✅ 与 流萤Toon 等 24 个材质同源 | ⚠️ 新 shader 家族 |

→ **推荐：自写 `Chenqu/StylizedLit`**，思路是"URP Lit 的正确数据管线 + 卡通化明暗 + 可选描边"，因为它能吃掉 196 个材质的法线/粗糙/金属三张图，而 YSAToon 会把这 576 张图全部浪费。

但**先做数据修复（P0），P0 完成后用 URP Lit 就能看到明显改善**——这一步不依赖任何 shader 决定。

---

## 2. 目标效果与验收标准

**目标（用户原话）**："现在一点也不好看，没有 shader" → 要"好看"。

落地为 4 条可验收标准：

| # | 标准 | 怎么验 |
|---|---|---|
| V1 | 196 个材质接上法线/粗糙/金属，材质间有差异 | 脚本统计：`_BumpMap != null` 的材质数 ≥ 196；`_Smoothness` 取值种类 > 1 |
| V2 | 576 张非颜色贴图导入正确 | 脚本统计：`textureType==NormalMap` 的 `_Normal` 图 = 184；`sRGB==false` 的 gloss/metal = 392 |
| V3 | 有构图、有阴影、有环境的 1920×1080 成图 | 输出 `Preview/chenqu_after_1920.png`，且像素统计里动态范围 ≥ 8:1、主体四边留白 5~10% |
| V4 | 不改坏现有玩法场景 | `duihua.scene` 能否正常 Play；登录链路不受影响（见 `test-from-real-login` 技能） |

---

## 3. 分阶段实施

### P0 — 修数据（不换 shader，先让 URP Lit 正确起来）

**P0-1 修贴图导入设置**（只动 576 张非颜色贴图，不动 227 张 base）

- 目标：`Assets/Content/Resource/场景/陈曲/Tex/` 下
  - `*_Normal.png`（184 张）→ `textureType = TextureImporterType.NormalMap`，`sRGBTexture = false`
  - `*_Gloss.png`（184 张）→ `textureType = Default`，`sRGBTexture = **false**`
  - `*_Metalness.png`（208 张）→ `textureType = Default`，`sRGBTexture = **false**`
  - `*_A.png`（39 张）→ `sRGBTexture = false`（当遮罩用）
- 手段：编辑器批处理脚本（`AssetImporter.GetAtPath` → 改 → `SaveAndReimport()`），按 50 张一批 + `EditorUtility.DisplayProgressBar`，避免一次性 reimport 卡死编辑器
- 预计耗时：576 张，其中 18 张 2048 → 约 **6~12 分钟**（BC7 压缩）
- 风险：重导入期间编辑器卡顿；中断会留下部分已改部分未改 → **脚本要幂等**，可重复执行
- 回滚：不涉及资产删除，只需把 sRGB 改回 true（或 `git checkout -- Assets/Content/Resource/场景/陈曲/Tex`，但这些文件目前是 `??` 未跟踪，**需先提交基线**）

**P0-2 提取 240 个内嵌材质为外部 .mat**

- 现状：`materialLocation = InPrefab` → 材质是 FBX 子资产，改一次 FBX 重导入就全丢
- 目标目录：`Assets/Content/Resource/场景/陈曲/Materials/`
- 手段：`ModelImporter.materialLocation = ModelImporterMaterialLocation.External` 后 `SaveAndReimport()`（Unity 会自动把 240 个材质抽成同目录 `.mat`），或用 `ModelImporter.extractTextures` 类似流程 + 手动 `AssetDatabase.CreateAsset`
  - **推荐**先 `materialLocation = External` + `SaveAndReimport`，再用脚本把抽出来的材质统一改名回 `材質N.mat`
- 风险：`duihua.scene` 里 `未命名 (1)` 是 Connected 实例，材质重定向后引用会全部重建 → **必须先在 `main` 提交基线**，然后验证场景引用没断
- 验证：`LoadAllAssetsAtPath(fbx)` 里 Material 数量应为 0；`Materials/` 下 240 个 `.mat`；场景里 239 个 SMR 的 `sharedMaterial` 非 null

**P0-3 接线：给 196 个材质补 N/G/M**

- 规则（已实测覆盖率 175 + 21）：
  ```
  对每个材质 M，取 base 贴图名 B（如 Tex_0164 或 Tex_0002_2）
  候选基名 = [B, B 去掉尾部 _A\d+ / _\d+ / _\d+ 之后的串]
  取第一个同时存在 B_Normal 与 B_Gloss 的候选 X：
      _BumpMap        = Tex/X_Normal.png
      _SpecGlossMap   = Tex/X_Gloss.png   （URP Lit 需要先开 _SPECULAR_SETUP 或直接用 _MetallicGlossMap）
      _MetallicGlossMap = Tex/X_Metalness.png
      _Smoothness 来源 = _Gloss（需在 P1 的自写 shader 里读；URP Lit 下把 gloss 塞进 _MetallicGlossMap 的 A 通道）
  剩 43 个无 N/G 的：只接 _MetallicGlossMap（8 个有），其余保持单贴图
  ```
- **URP Lit 的坑**：`_MetallicGlossMap` 是 **R=metallic, A=smoothness** 打包图，而这里 metal 和 gloss 是**两张独立图**。在 Lit 下必须先离线合成一张 `Tex_XXXX_MG.png`（R=metal, A=gloss），否则只能二选一。
  - 所以 P0-3 里对 URP Lit 的最省事做法：**只接 `_BumpMap`**（收益最大、无打包问题），metal/gloss 留给 P1 的自写 shader 直接读两张独立图
  - 若要现在就用 Lit 吃满：需额外生成 184 张合成图（Pillow 脚本，约 30 秒），会新增 184 个资产
- 验证：`_BumpMap != null` 计数 ≥ 175

**P0-4 光照与阴影修正**（这一步对观感提升最大）

| 项 | 现值 | 改为 | 理由 |
|---|---|---|---|
| `shadowDistance` | 50 | **1200~2000** | 模型 3800 宽，50 等于没影子；太高会让 2048 阴影图糊，故配合下一行 |
| `mainLightShadowmapResolution` | 2048 | **4096** | 抵消 shadowDistance 增大后的精度损失 |
| `shadowCascadeCount` | 2 | **4** | 近处细节 + 远处覆盖 |
| 主光强度 | 1.35 | 1.1~1.3（保持），但**必须换角度** | 见下 |
| 主光角度 | rot(48, 330, 0) | 用日出/黄昏侧逆光，如 rot(22, 145, 0) | 平光无体积感；侧逆光能打出建筑轮廓 |
| ambientLight | (0.150,0.160,0.190) Flat | 保持偏低，但改 `ambientMode = Skybox` 或 TriLight（天蓝/地暖） | Flat 让阴影侧死平 |
| skybox | `Default-Skybox` | 换程序化渐变天空盒（新建 `ChenquSky.mat`，用 `Skybox/Procedural`） | 默认灰蓝天空本身就很"脏" |
| fog | ExpSquared 0.002 | 保持但改色为暖灰蓝 (0.52,0.58,0.66)，density 0.0012 | 城市远景需要大气透视，但 0.002 会糊掉细节 |

> ⚠️ 这些是**场景级**设置，改 `URP-PipelineAsset_High` 会影响所有场景。方案：**复制一份** `URP-PipelineAsset_Cinematic.asset` 专供预览渲染，或在预览场景里用 `QualitySettings.renderPipeline` 局部覆盖。

**P0 验收**：P0-1/2/3 完成后，`duihua.scene` 里那栋建筑的表面应该有凹凸细节、玻璃/金属有反光差异、树木有镂空；P0-4 后有明确投影。**这一步不换 shader 就能看到质变。**

---

### P1 — 自写 shader（吃掉法线/粗糙/金属）

**新建**：`Assets/Content/Resource/场景/陈曲/Shaders/ChenquStylized.shader`（约 400~500 行，URP 手写 HLSL，不依赖 ShaderGraph）

**属性设计**：

| 分组 | 属性 | 类型 | 默认 | 用途 |
|---|---|---|---|---|
| 基础 | `_BaseMap` | 2D | white | albedo |
| | `_BaseColor` | Color | white | 染色 |
| | `_BaseMap_ST` | Vector | 1,1,0,0 | 平铺 |
| 法线 | `_NormalMap` | 2D | bump | ← `Tex_XXXX_Normal` |
| | `_NormalScale` | Range(0,2) | 1 | 强度 |
| | `_UseNormalMap` | Toggle | 1 | 无图材质关掉 |
| 表面 | `_MetallicMap` | 2D | black | ← `Tex_XXXX_Metalness`（R） |
| | `_Metallic` | Range(0,1) | 0 | 无图时用 |
| | `_GlossMap` | 2D | white | ← `Tex_XXXX_Gloss`（R） |
| | `_Smoothness` | Range(0,1) | 0.4 | 无图时用 |
| | `_OcclusionMap` / `_OcclusionStrength` | 2D/Range | white/1 | 可选 |
| 卡通明暗 | `_RampOffset` | Range(-1,1) | 0 | 明暗分界偏移 |
| | `_RampSoftness` | Range(0.001,0.5) | 0.06 | 分界柔化（越小越硬） |
| | `_ShadowColor` | Color | (0.32,0.36,0.46) | **冷色阴影**（关键，暖光冷影是日系标配） |
| | `_ShadowStrength` | Range(0,1) | 0.75 | 阴影浓度 |
| 高光 | `_SpecColor` / `_SpecIntensity` | Color/Range | white/0.6 | 卡通高光块 |
| | `_SpecSmoothness` | Range(0,1) | 0.9 | 高光硬边 |
| Rim | `_RimColor` / `_RimPower` / `_RimIntensity` | Color/Range/Range | (0.6,0.8,1)/3/0.35 | 边缘光，勾建筑轮廓 |
| 自发光 | `_EmissionMap` / `_EmissionColor` | 2D/HDR | black/black | 窗户灯光（可选） |
| 镂空 | `_AlphaClip` / `_Cutoff` | Toggle/Range | 0/0.5 | 树叶 |
| 描边 | `_OutlineWidth` / `_OutlineColor` | Range/Color | 0/black | 0 默认关（1200 万面描边代价高） |

**Pass 设计**：`UniversalForward` + `ShadowCaster` + `DepthOnly` + `DepthNormals`（比 YSAToon 多一个 DepthNormals，供 SSAO/后处理）。全部 `Cull Back`，`ZWrite On`，`Queue = Geometry`（镂空的走 `AlphaTest`）。

**明暗模型（核心）**：
```
half NdotL = dot(N, L);
half ramp  = smoothstep(_RampOffset - _RampSoftness, _RampOffset + _RampSoftness, NdotL);
half3 lit  = albedo * (_MainLightColor * ramp + _ShadowColor * (1 - ramp) * _ShadowStrength);
// 附加光用同一 ramp，权重更低
// 高光：GGX 换成 pow(NdotH, 1/(1-_SpecSmoothness)) 的硬边版本，乘 glossMap
// Rim：pow(1 - saturate(dot(N, V)), _RimPower) * _RimColor * _RimIntensity，并被 ramp 调制
```

**接线**：给 240 个材质批量改 shader 为 `Chenqu/StylizedLit`，按 P0-3 的映射表填 4 张图 + `_UseNormalMap`。

**为什么不用 YSAToon**：它会丢掉 184 张法线 + 208 张金属度；且它的 SDF/半身渐变是角色专用，对墙瓦只会让明暗更假。保留它给角色（流萤等 24 个材质还在用）。

**验收**：shader 编译 0 error 0 warning；240 个材质全部切过去无粉色（missing shader）。

---

### P2 — 构图、渲染出图与后处理

**P2-1 专用预览场景** `Assets/Content/Resource/场景/陈曲/ChenquRender.unity`

- 相机：位置由脚本自动算 `bounds` → 放到城市东南侧 45° 俯角、距离 = `diag * 1.1`，`fov 35`，`near 1`，`far = diag * 3`
- 三点光：主光（暖 5500K 侧逆光，强度 1.2，软阴影）+ 补光（冷 8000K，0.25，无影）+ 轮廓光（背向，0.4）
- 天空：`Skybox/Procedural` 材质，`_SunSize 0.04`、`_AtmosphereThickness 1.2`、`_SkyTint (0.55,0.62,0.75)`
- Volume：`ChenquRender_PostFX.asset` —— Tonemapping(ACES) + ColorAdjustments(contrast +12, saturation +8) + Bloom(threshold 1.0, intensity 0.35) + Vignette(0.25) + **SSAO（若 URP 14 渲染器已挂 SSAO feature，需先确认）**
- 雾：ExpSquared density 0.0008，暖灰蓝

**P2-2 渲染出图**

- 编辑器脚本 `Temp/RenderChenquPreview.cs`（用 `tuanjie_eval` 执行，不落盘）：建临时相机 → `RenderTexture(1920,1080, 24, ARGBHalf)` → `Camera.Render()` → `ReadPixels` → `EncodeToPNG` → 写 `Preview/chenqu_after_1920.png`
- 至少出 4 张：全景 / 街道近景 / 建筑特写 / 俯视，用于对比

**P2-3 前后对比与量化**

- 保留 `Preview/chenqu_before_1920.png`（同机位，改前状态）
- 用 Pillow 统计：动态范围、亮度直方图分布、色度占比、边缘数（用 PIL `FIND_EDGES`），对比 before/after
- **注意**：我（当前模型）没有视觉能力，无法"看"图。所有观感判断必须由你确认，我只能给量化指标 + 技术正确性验证。

---

## 4. 风险与回滚

| 风险 | 影响 | 缓解 |
|---|---|---|
| `Assets/Content/Resource/场景/陈曲/` 目前是 **git `??` 未跟踪** | 改坏了无法 `git checkout` 恢复 | **P0 前必须先提交基线**（含 57MB FBX + 1786 张贴图）；或至少 `cp -r` 到 `Temp/` 备份 |
| FBX `materialLocation` 改 External | 场景里 239 个 SMR 材质引用全部重定向，可能断链 | 改前提交；改后立即验证 `sharedMaterial != null` 计数 = 239 |
| 576 张贴图重导入 | 编辑器卡顿 6~12 分钟，期间桥可能超时 | 分批 + `EditorApplication.delayCall`；桥超时就重试 |
| 改 `URP-PipelineAsset_High` | 影响所有场景（战斗、对话、迷雾信标） | **不直接改**，复制成 `*_Cinematic` 只给预览场景用 |
| 1200 万三角 + 4x MSAA + 4096 阴影 | 编辑器视图卡、显存涨 | 预览场景关 MSAA 到 2x；或渲染时临时降 |
| 239 个 SkinnedMeshRenderer 但无动画 | 每帧白付蒙皮开销 | **P2 可选**：改成 MeshRenderer（需重建网格引用，风险高，建议不动） |

---

## 5. 代价估算

| 阶段 | 工作内容 | 预计耗时 | 风险 |
|---|---|---|---|
| 前置 | 提交 `陈曲/` 基线（57MB + 1786 图） | 2~5 分钟 | 低 |
| P0-1 | 576 张贴图重导入 | 6~12 分钟（含等待） | 中 |
| P0-2 | 240 材质提取为外部 .mat | 1 分钟 | 中 |
| P0-3 | 196 材质接法线（+ 可选 184 张 MG 合成图） | 2 分钟（+30 秒合成） | 低 |
| P0-4 | 光照/阴影/天空/雾 | 3 分钟 | 低（用独立 pipeline asset） |
| P1 | 写 ~450 行 shader + 编译 + 240 材质切 shader | 20~30 分钟 | 中 |
| P2 | 预览场景 + 4 张 1920×1080 出图 + 量化对比 | 10 分钟 | 低 |
| **合计** | | **约 45~65 分钟** | |

**建议节奏**：先做 **P0（约 15 分钟）** 出一张对比图给你看，你确认方向和力度后再做 P1/P2。不要一次性全推完再给你看。

---

## 6. 需要你确认的 4 件事

1. **风格方向**：日系卡通渲染（硬明暗 + 冷色阴影 + 描边）？还是写实 PBR（只把法线/粗糙/金属接对，保持 URP Lit）？
   → 影响 P1 是否要写 shader。选写实则 P0 做完基本就结束。
2. **目标用途**：这栋城是要放在游戏里跑（性能敏感），还是只要一张好看的效果图？
   → 只出图可以关 MSAA、开更高阴影、甚至用 8K 渲染再缩放。
3. **是否要保留在 `duihua.scene` 里**：现在 `未命名 (1)` 就在对话场景里，和 NPC 对话镜头混在一起。要不要挪到独立场景？
4. **描边要不要**：1200 万三角描边（多一个 pass 膨胀顶点）代价明显，城市级场景通常不开。

---

## 附：本次诊断用到的可复现命令

```bash
# 材质→贴图映射表（已生成）
Temp/chenqu_matmap.tsv          # 240 行：材质名 / base 贴图 / N-G-M 可用标记

# 贴图分通道统计
"<python>" Temp/tex_probe_chenqu.py    # 抽样均值/std/alpha
"<python>" Temp/tex_probe2.py          # 分类计数、金属度分布、base alpha
"<python>" Temp/tex_variant_check.py   # 变体命名对应率（175 直连 / 21 剥后缀 / 43 无）
```

> 这些脚本只读不写资产，可重复执行。
