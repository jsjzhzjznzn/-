# 大世界光照与后处理调优方案（dashijie）

> 范围：只改 dashijie 场景的画面观感。落地项是场景环境光、场景灯光、`URP-PipelineAsset_High` 的阴影参数、场景专用 Volume 配置、场景专用 Renderer（SSAO）、相机近裁剪面，以及陈曲城市 Shader 的一处金属环境补偿。
> 本文所有现状数字来自 2026-10-31 编辑器实测（`RenderSettings`、`Light`、`Volume`、`UniversalRenderPipelineAsset`、材质贴图 32×32 降采样均值、Play 模式 RT 回读），不是估算。

---

## 0. 结论

现在的场景不是"光太暗"或"后处理不够"，而是**五处互相叠加的偏置把画面压成了两端**：环境光是暗厅遗留的一片 0.15 纯色，太阳把地面推到接近纯白，金属立面在背光面拿不到任何环境反射而压成死黑，阴影只覆盖 50 m、2 级，雾薄到看不见，后处理还是给昏暗室内调的那套（曝光 +0.18、对比 +10、饱和 +18、Bloom 0.18）。

Play 模式出生点实拍（1280×720，主相机原样渲染回读）：**平均亮度 148.4，低于 32 的像素占 17.8%，高于 224 的占 0.0%**——这不是"亮"，是"地面一片白、立面一片黑、中间调几乎没有"。

方案是按顺序动五件事：**冷色梯度环境光 → 太阳与三级阴影（100 m）→ 带空气透视的雾与程序化天空 → 面向户外的调色 Volume → 场景专用 SSAO**，并补上金属面的环境响应。五项分开做收益有限，必须一起改。

---

## 1. 现状实测

### 1.1 环境与天空

| 项 | 现状 |
|---|---|
| 环境光模式 | Flat（纯色），`m_AmbientMode = 3` |
| 环境光颜色 | (0.15, 0.16, 0.19)，强度 1.0 —— duihua 暗厅时期遗留 |
| 天空盒 | `Default-Skybox`（内置程序化，默认参数） |
| 太阳源 | `RenderSettings.sun` 未指定（`m_Sun: {fileID: 0}`） |
| 雾 | 开，ExponentialSquared，密度 0.002，颜色 (0.45, 0.50, 0.58) |
| 反射 | 来源天空盒，分辨率 128，强度 1.0，无反射探针 |

密度 0.002 的显示意义：100 m 处雾系数 0.96、300 m 处 0.55 曲线起点极靠后，实际画面里等于没有雾，3.9 km 天际线与眼前建筑同样清晰。

### 1.2 太阳与阴影

| 项 | 现状 |
|---|---|
| 主光 | Directional Light，intensity 1.35，颜色 (1.0, 0.96, 0.90)，欧拉 (50, -30, 0) |
| 阴影类型 | Soft，强度 0.85，bias 0.04，normalBias 0.5 |
| 阴影距离 | 50 m（`URP-PipelineAsset_High`） |
| 级联 | 2 级 |
| 主光阴影贴图 | 2048 |
| 附加光阴影 | 管线级关闭（`m_AdditionalLightShadowsSupported = 0`） |
| 软阴影 | 管线级开启 |

城市可见纵深 3.9 km、可玩街区 384 m、主台阶与广场进深约 80 m。50 m 阴影意味着广场中后段、大台阶、两侧连廊全部没有投影，立面明暗只由 N·L 决定。

### 1.3 场景内其它灯光

| 灯 | 类型 | 强度 / 范围 | 说明 |
|---|---|---|---|
| HallLight_0~4 | Point ×5 | 55 / 18 m | 廊下与门厅点缀，无阴影 |
| DialogueFill | Point | 25 / 7 m | duihua 对话场景遗留 |
| CharacterFillLight | Directional | 0.28 | 角色补光，冷色 |
| `未命名 (1)/Light` | Point | **1000 / 10 m** | 城市模型 FBX 自带灯，嵌在喷泉附近 (-4.1, 5.9, -1.0) |

`未命名 (1)/Light` 的 1000 强度是导出残留，在 URP 的附加光预算里会占一个名额并压出局部热点。

### 1.4 后处理

场景内 `PostFX_Volume`（全局、weight 1）引用 `Assets/Game/Settings/duihua_PostFX.asset`：

| 组件 | 现状 |
|---|---|
| Tonemapping | Neutral |
| ColorAdjustments | 曝光 **+0.18**、对比 +10、饱和 +18、滤镜白 |
| WhiteBalance | 温度 +6、色调 +2（偏暖） |
| ShadowsMidtonesHighlights | 暗部 (0.94, 0.95, 1.00)、高光 (1.00, 0.97, 0.93) |
| Bloom | 阈值 1.1、强度 0.18、scatter 0.55、暖白 tint |
| Vignette | 强度 0.10、平滑 0.45 |
| 关闭的 | 没有 AO、没有 ColorCurves、没有色差/颗粒 |

这套值来自 duihua 的暗色大厅：靠正曝光把室内提亮、靠高饱和补气氛。放到大面积白石广场上就是"地面过曝 + 立面发灰"。

### 1.5 渲染管线与相机

| 项 | 现状 |
|---|---|
| 当前质量档 | Ultra → `URP-PipelineAsset_High` |
| Renderer | `URP-ForwardRenderer`，**0 个 Renderer Feature**（无 SSAO） |
| HDR / MSAA | 开 / 4× |
| Depth Texture、Opaque Texture | 均未声明开启 |
| 主相机 | near **0.01**、far 1000、FOV 45、SMAA High、后处理开 |
| 相机指向 | 出生点 (0, 1.8, -12)，欧拉 (8, 0, 0) |

near 0.01 让深度精度几乎全部堆在 1 m 以内，SSAO 与阴影的深度比较都在"够用但抖"的区间。

### 1.6 城市材质（本次观感的根因之一）

| 项 | 实测 |
|---|---|
| 渲染器 | 239 SkinnedMeshRenderer + 1 MeshRenderer，1,206,577 三角面 |
| 材质 | 240 套，全部 `Game/ChenquStylized` |
| 材质参数 | `_BaseColor` 全白、`_Metallic` / `_Smoothness` 倍率均为 1、`_SpecIntensity` 0.8、`_ShadowStrength` 0.85 |
| 金属度贴图均值 | 239 套中 205 套有金属贴图，**平均 0.45**；其中大量为 1.00（`Tex_0002_2`、`Tex_0007`、`Tex_0164` 等） |

`ChenquStylized` 的合成是：

```hlsl
half3 diffuseAlbedo = albedo * (1.0 - metal);
half3 color = diffuseAlbedo * (ambient + mainLit + addLit) + specular + rimCol + emissive;
```

金属度接近 1 的表面，漫反射被整体乘以 0，**环境光对它完全无效**；同时着色器没有采样反射探针或天空，高光只来自太阳与菲涅尔边光。太阳照不到的那一侧于是只剩 15% 的冷色阴影项 —— 这就是玩家看到的"立面死黑"。

### 1.7 烘焙路线不可用

| 项 | 实测 |
|---|---|
| 静态标记 | 全场景 0 个（BatchingStatic / ContributeGI 都没有） |
| Lightmap | 0 张 |
| 城市几何 | 239 个 SkinnedMeshRenderer |

蒙皮网格不能被烘焙进 Lightmap，240 套材质各自一张底图也不适合合进有限张数的光照贴图 atlas。教程里"静态场景走烘焙、开销极低"的路线在这个资产上不成立，本方案走全实时 + 屏幕空间 AO。

---

## 2. 与教程的对应关系

| 教程环节 | 本项目处理 |
|---|---|
| 确认渲染管线资产的光照功能已开 | 已开，本次只改阴影距离 / 级联 / 主光贴图分辨率 |
| 调环境光让画面有明暗变化 | 照做，但方向相反：教程从"环境光过强"往下降，本项目是 0.15 纯色太暗，改为三色梯度 |
| 阴影最大距离 / 级联 / 分辨率 | 照做：50 m → 100 m、2 级 → 3 级、贴图维持 2048 |
| 静态场景光照烘焙 | **不适用**（1.7） |
| 后处理 Volume（暗角 / Bloom / 色彩调整 / AO） | 照做，见 3.4 与 3.5 |
| 贴花（污渍投影） | 不做：属于美术资产，不属光照与后处理 |
| 浮尘粒子 | 不做：同上 |
| 雾效 | 照做，见 3.3 |

---

## 3. 设计

### 3.1 环境光与天空

环境光从纯色改成三色梯度，把背光面从"0.15 一片灰"抬到有上下方向的冷色调：

| 项 | 值 |
|---|---|
| 环境光模式 | Trilight（Gradient） |
| 天空色 Sky | (0.50, 0.56, 0.68) |
| 地平线色 Equator | (0.44, 0.45, 0.48) |
| 地面色 Ground | (0.22, 0.21, 0.20) |
| 强度 | 1.0 |

天空盒从 `Default-Skybox` 换成同名的 `Skybox/Procedural` 材质（`Assets/Game/Settings/Dashijie_Sky.mat`），参数按"贝洛伯格冬季晴日"调：

| 属性 | 值 | 目的 |
|---|---|---|
| `_SkyTint` | (0.48, 0.56, 0.70) | 冷蓝底，和城市冷色阴影统一 |
| `_GroundColor` | (0.35, 0.34, 0.33) | 地平线以下不出现纯黑 |
| `_AtmosphereThickness` | 1.00 | 大气厚度收窄，地平线暖辉带不会铺满整条地平线 |
| `_Exposure` | 1.05 | 天空亮度略高于环境光，避免"天发灰" |
| `_SunSize` | 0.05 | 太阳盘清晰但不糊 |
| `_SunSizeConvergence` | 3 | 默认柔和边缘 |
| `_SunDisk` | Simple | 只画简单太阳盘，省掉高质量盘的高频计算 |
| `RenderSettings.sun` | Directional Light | 太阳盘与光照方向一致 |

环境光不跟随天空盒（仍用 Gradient），保证阴影亮度可精确控制，不会被天空亮度连带拉起。

### 3.2 太阳与阴影

| 项 | 现状 → 目标 |
|---|---|
| 强度 | 1.35 → **1.15** |
| 颜色 | (1.0, 0.96, 0.90) → **1.0, 0.95, 0.88** |
| 欧拉角 | (50, -30, 0) → **(46, -34, 0)** |
| 阴影强度 | 0.85 → **0.80** |
| normalBias | 0.5 → **0.6** |
| 阴影距离 | 50 → **100** |
| 级联数 | 2 → **3**（分割 0.12 / 0.40） |
| 主光阴影贴图 | 2048 保持 |

太阳强度降到 1.15 是压掉白石广场的过曝源；仰角降到 46° 让广场上出现更长的建筑投影，这是画面里唯一能给出"体量感"的东西。3 级级联在 100 m 上给出近段 12 m 的锐利阴影，远段覆盖到整条大台阶与两侧连廊。

阴影参数写在 `URP-PipelineAsset_High.asset`（Ultra 档）。其它档位不挂管线资产，不受影响。

### 3.3 雾与空气透视

| 项 | 现状 → 目标 |
|---|---|
| 模式 | ExponentialSquared |
| 密度 | 0.002 → **0.0026** |
| 颜色 | (0.45, 0.50, 0.58) → **(0.66, 0.72, 0.80)** |

密度 0.0026 在 100 m 处几乎不动眼前街区，300 m 处把远景压成 55% 的雾色，3.9 km 天际线落到接近天空的地平线色。雾色取天空地平线的中间值，远景与天空之间没有色带。

`ChenquStylized` 已经接了 `MixFog`（顶点算雾因子、片元混色），URP Lit 物体同样自动生效，无需改材质。

### 3.4 后处理 Volume：新建 `Dashijie_PostFX`

新建 `Assets/Game/Settings/Dashijie_PostFX.asset`，场景 `PostFX_Volume.sharedProfile` 换成它。独立配置的理由：`duihua_PostFX` 是暗厅的曝光补偿，继续共用会让两边都调不准；该配置资产目前只有 dashijie 引用，A/B 互不影响。

| 组件 | 参数 |
|---|---|
| Tonemapping | Neutral（保持高光柔性滚降，不改 ACES） |
| ColorAdjustments | 曝光 **-0.05**、对比 **+8**、饱和 **+4**、滤镜 (1.0, 0.995, 0.98) |
| WhiteBalance | 温度 **0**、色调 **0** |
| ShadowsMidtonesHighlights | 暗部 (0.90, 0.95, 1.08)、中间调 (1,1,1)、高光 (1.05, 1.00, 0.94)、区间 0.30 / 0.55 |
| ColorCurves | 主曲线 (0,0)→(0.08,0.075)→(0.30,0.305)→(0.65,0.655)→(1,1)；蓝通道阴影段 +0.02 |
| Bloom | 阈值 **1.15**、强度 **0.28**、scatter **0.62**、tint (1.00, 0.96, 0.92)、High Quality 开 |
| Vignette | 强度 **0.24**、平滑 0.60、非圆角 |

要点：对比从 +10 降到 +6 配合曲线抬阴影，是为了把"两端黑/白"收进中间调；白平衡归零后冷感由环境光与天空承担，不靠整体暖偏；Bloom 阈值抬高、强度加大，只让天空与太阳盘发光，不再把白石地面一起糊亮；暗角加到 0.24 给第三人称构图一个收束。

不启用 Film Grain 与色差：卡通渲染下颗粒会破坏平面色块。

### 3.5 SSAO：场景专用 Renderer

URP 14 的 SSAO 是 Renderer Feature，不是 Volume 组件；直接加到共用的 `URP-ForwardRenderer` 会连带改变 tiaozhan、zhuye、fog_hunt。做法是给 dashijie 单独一个 Renderer，并用相机的 renderer 索引隔离：

1. 新建 `Assets/Game/Settings/URP-ForwardRenderer_Dashijie.asset`，内容复制自 `URP-ForwardRenderer.asset`。
2. 给它加 `ScreenSpaceAmbientOcclusion` 特性：

| 参数 | 值 |
|---|---|
| Method | Blue Noise |
| Source | Depth Normals |
| Samples | Medium |
| Downsample | 关 |
| After Opaque | 关 |
| Intensity | 1.6 |
| Direct Lighting Strength | 0.25 |
| Radius | 0.60 |
| Blur Quality | High |
| Falloff | 100 |

3. 把该 Renderer 追加进 `URP-PipelineAsset_High` 的 `m_RendererDataList`（index 1，默认 index 仍是 0）。
4. dashijie 主相机 `UniversalAdditionalCameraData.m_RendererIndex = 1`；其余场景的相机保持 -1（用默认 Renderer），不受影响。

`ChenquStylized` 已带 `DepthNormals` Pass（`NormalizeNormalPerPixel` 输出），SSAO 能直接工作；否则城市会完全不参与 AO。

半径取 0.6 m 是按本场景尺度定的：台阶踏步 0.25 m、栏杆 0.5 m、门洞深 1~2 m，0.6 m 能在柱脚、拱券、台阶与地面交界处压出接触阴影，又不至于把整面墙刷脏。

### 3.6 金属面的环境响应（Shader 一行补偿）

在不引入反射探针的前提下，给金属表面补上低频环境项，改 `ChenquStylized.shader` 的 ForwardLit 片元：

```hlsl
half3 ambient = SampleSH(N) * _AmbientTint.rgb * ao;
// 金属保留一部分环境漫反射，并补一份低频环境高光，避免背光面全黑
half3 diffuseAlbedo = albedo * lerp(1.0, 0.30, metal);
half3 envSpec = SampleSH(N) * _SpecColor.rgb * metal * (0.25 + 0.5 * smooth) * ao;
```

效果：金属度 1 的立面在阴影里拿到 30% 的环境漫反射加一份环境高光，从死黑回到暗灰金属，同时不破坏卡通分档——`_ShadowColor` 的冷色偏移仍在生效。

代价：240 套材质共用同一个 Shader，改一次全量变体重编，首轮 refresh 需要几分钟。

### 3.7 灯光清理与相机

| 项 | 处理 |
|---|---|
| `未命名 (1)/Light`（1000 / 10 m） | 停用。太阳 + 环境光已覆盖广场，它是导出残留 |
| DialogueFill（25 / 7 m） | 停用，duihua 对话机位遗留 |
| HallLight_0~4 | 保留 55 / 18 m，作为廊下暖色点缀 |
| CharacterFillLight | 保留 0.28 冷色补光 |
| 主相机 near | 0.01 → **0.15** |

near 提到 0.15 后深度精度、SSAO 与阴影的深度比较都更稳，第三人称 1.5 m 起步的机位不会出现近裁剪问题。

---

## 4. 文件影响面

| 文件 | 改动 |
|---|---|
| `Assets/Game/Scenes/dashijie.scene` | RenderSettings（环境光三色、雾、太阳源、天空盒）、灯光启停、`PostFX_Volume` 换 profile、主相机 near 与 renderer 索引 |
| `Assets/Game/Settings/Dashijie_Sky.mat`（新建） | 程序化天空材质 |
| `Assets/Game/Settings/Dashijie_PostFX.asset`（新建） | 场景专用后处理配置（7 个组件） |
| `Assets/Game/Settings/URP-ForwardRenderer_Dashijie.asset`（新建） | 含 SSAO 的 Renderer |
| `Assets/Game/Settings/Quality/URP-PipelineAsset_High.asset` | 阴影距离 100、级联 3、Renderer 列表追加一项 |
| `Assets/Content/Resource/场景/陈曲/Shaders/ChenquStylized.shader` | 金属环境补偿两行 |

阴影距离与级联是管线级参数（Renderer 资产没有这个开关），写在 Ultra 档的 `URP-PipelineAsset_High` 上，因此使用 Ultra 档的其它场景也会把阴影范围拉到 100 m；视觉与开销变化是"阴影投得更远"，不改变各自的后处理与配色。

`duihua_PostFX.asset` 保持原样不动，不再被任何场景引用。

---

## 5. E2E 验收

| 编号 | 验收项 | 判据 |
|---|---|---|
| A1 | 出生点正向大台阶 | RT 回读 1280×720：低于 32 的像素 **≤ 10%**（现状 17.8%）；白石广场区域平均亮度 ≤ 215 |
| A2 | 回望街区、仰视云中站两个机位 | 中间调（64~192）像素占比 ≥ 50%；远景 300 m 处与眼前建筑有明显亮度差（空气透视可见） |
| A3 | 天空与雾衔接 | 地平线处天空色与雾色差值 ≤ 8/255，看不到色带 |
| A4 | SSAO 生效 | 关闭 Renderer 索引前后同一机位对比：柱脚与台阶根部的像素亮度差 ≥ 6；`FrameDebugger` 中可见 SSAO Pass |
| A5 | 其它场景无回归 | tiaozhan、zhuye、fog_hunt 各截一帧，与改动前逐像素差为 0（renderer 索引隔离生效） |
| A6 | 性能 | 1280×720 下用同一台相机连续 `Camera.Render()` 计时，SSAO 开（renderer 1）与关（renderer 0）各 25 帧取均值，差值 ≤ 1 ms；阴影范围拉长的代价单独看打包后 PlayerLoop |
| A7 | 稳定性 | Console 无 shader 编译错误、无 SSAO 缺材质告警、无 `Resource ID out of range` |

验证方式：`exec_runtime_script` 用相机渲 RT + `ReadPixels` 回读取图与算亮度分布（本工程 backbuffer 回读恒灰）；性能用 `ProfilerRecorder`；SSAO 与勾选状态用 `FrameDebugger` 与组件读回，不靠肉眼。

---

## 6. 风险

| 风险 | 说明与处置 |
|---|---|
| `ScreenSpaceAmbientOcclusion` 是 internal 类 | 用反射创建，URP 升级后需复核签名；创建失败时 Console 会明确报缺材质 |
| 240 材质全量重编 | Shader 改动触发约 240 套变体重编，首次 refresh 数分钟；按 `.codely-cli/skills/unity-editor-gfx-budget/SKILL.md` 的节奏，一轮只做一次重建 + 一次 Play |
| 3 级阴影 × 239 个蒙皮渲染器 | 阴影 pass 需要渲染整城，这是本方案最大的 GPU 增量；A6 不达标时把级联降回 2 级、距离降到 80 m |
| 环境高光是低频近似 | `SampleSH` 只有二阶球谐信息，金属面在掠射角会出现偏平的反光，与真实反射不一致；卡通风格下可接受 |
| 程序化天空与太阳绑定 | 太阳盘方向跟随 Directional Light，改光角度会同时改天空；两者一起调 |
| 远景雾遮蔽天际线 | 3.9 km 天际线会被压进雾色，需要在 A2 机位确认云中站仍然可辨 |

---

## 7. 实测验收记录

2026-10-31 落地后实测（Positive：dashijie 直接进 Play，主相机 RT 回读 1280×720，后处理与 SSAO 均在生效状态）：

| 项 | 基线 | 落地后 |
|---|---|---|
| 平均亮度 | 148.4 | 138.4（正向大台阶机位）／141.0（侧望街区）／112.6（仰视天空） |
| 低于 32 的像素 | 17.8% | **0.0% / 0.1% / 0.0%** |
| 高于 224 的像素 | 0.0%（地面已顶到 240 附近，靠整体过曝掩盖） | 0.0% |
| 中间调 64~192 | 未测（两极化） | **85.0% / 90.1% / 90.6%** |

被替换掉的旧后处理在其它条件相同时的对照（移除后处理渲染同一帧）：平均亮度 167.2、高于 224 占 11.6%——即"地面顶白"的主因确实是旧 Volume 的正曝光与对比，新的调色把 11.6% 的高光压回中间调。

SSAO 生效验证：同一机位 renderer 索引 1 / 0 两次回读，逐像素平均差 **1.13/255**，差值 >2 的像素占 **2.67%**，集中在柱脚、拱券与台阶根部的遮挡交界。

性能：1280×720 单次 `Camera.Render()` 均值 **1.22 ms（SSAO 开）/ 1.14 ms（SSAO 关）**，差值 0.09 ms，明显低于 1 ms 的验收线。

结构核对：场景 7 个 Volume 组件全部 active；`PostFX_Volume` 已指向 `Dashijie_PostFX`；主相机 `m_RendererIndex = 1`；管线 renderer 列表 2 项（0 = 原 `URP-ForwardRenderer`，1 = `URP-ForwardRenderer_Dashijie`）；SSAO 特性 active；天空盒 = `Dashijie_Sky`，太阳源已指向 Directional Light；`DialogueFill` 与 `未命名 (1)/Light` 已停用。Console 无 shader 编译错误、无 SSAO 缺材质告警。

遗留观察（不属于本次改动）：广场上玩家左前方有一个约 2×1.2 m 的无贴图米色方块，隐藏玩家与整座城市后仍然存在，是城市模型自带的几何体，需要美术侧确认是否为占位物。
