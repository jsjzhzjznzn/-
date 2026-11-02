# duihua 场景视觉配置说明

场景：`Assets/Game/Scenes/duihua.scene`（对话演示场景，主体为 `未命名` 舞台模型 36 个子网格）

## 1. 改前的状态与问题

| 项 | 改前 | 影响 |
| --- | --- | --- |
| 后处理 Volume | 场景里没有 | 后处理完全没用上 |
| Main Camera | `renderPostProcessing = false` | 同上 |
| **URP Renderer 的 `postProcessData`** | **NULL** | **URP 会静默跳过整条后处理链**，即使 Volume/相机都开了也不生效 |
| 方向光 | `shadows = None` | 没有投影，所有物体像浮在空中 |
| 环境光 | Skybox，intensity 1 | 只有环境光，缺少主光层次 |
| 材质 | FBX 内嵌材质，只有 BaseMap + 固定 smoothness 0.5 | 表面是平的，缺法线细节 |

## 2. 现在的配置

### 材质（外置，36 个）

`Assets/Content/Resource/场景/Materials/M_<贴图名>.mat`，场景 `未命名/000_材質1…035_材質36` 的 36 个 SkinnedMeshRenderer 各挂一个。

- `_BaseMap`：沿用原有贴图（原内嵌材质已带，按名字继承）
- `_BumpMap`：同套贴图的 `_Normal`，`_NORMALMAP` 关键字已开
- `_Smoothness`：取同套 `_Gloss` 贴图的平均灰度（`Tex_0004` 这类缺图集回退 0.4）
- `_Metallic`：先保持 0（金属度贴图未打包，避免整块材质变成镜面）

> **为什么外置**：FBX 内嵌材质的修改只活在内存里，任何一次 FBX Reimport 都会被打回原样（已实测）。外置 .mat 才能落盘、能被 git 追踪。

### 光照

- Directional Light：软阴影（strength 0.85）、intensity 1.35、色温暖白 (1, 0.96, 0.90)、角度 (48, 330)
- 环境光：Skybox，intensity 1
- 雾：ExponentialSquared，density 0.002，色 (0.45, 0.50, 0.58)，只为远处加一点空气感

### 后处理

`Assets/Game/Settings/duihua_PostFX.asset` → 场景 `PostFX_Volume`（Global，priority 1）

| 组件 | 参数 |
| --- | --- |
| Tonemapping | ACES |
| ColorAdjustments | postExposure +0.32，contrast +10，saturation +18 |
| WhiteBalance | temperature +6，tint +2 |
| Bloom | threshold 1.1，intensity 0.3，scatter 0.65，暖色 tint |
| Vignette | intensity 0.3，smoothness 0.45 |
| ShadowsMidtonesHighlights | 阴影偏冷、高光偏暖（很轻） |

Main Camera：`renderPostProcessing = true`，抗锯齿 SMAA（High），dithering 开。

## 3. 调参入口

- 整体色调/明暗：改 `duihua_PostFX.asset` 的 ColorAdjustments（数值实时可见）
- 对比是否太"灰"：先关 Tonemapping 对比，或把 saturation 提到 25–30
- 主光方向/强度：`Directional Light`；阴影糊/漏光时调 shadowNormalBias（模型 100 倍缩放，需偏大）
- 雾太脏：density 降到 0.001 或直接关 fog

## 4. 注意

`postProcessData` 的修复写在 `Assets/Game/Settings/URP-ForwardRenderer.asset`，是**全项目渲染管线**的设置。它只影响 `renderPostProcessing = true` 的相机；其他场景没有 Volume，观感不会变，但相机若原本开了 PP 会从此真正生效。
