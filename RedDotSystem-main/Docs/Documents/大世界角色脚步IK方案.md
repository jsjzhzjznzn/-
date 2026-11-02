# 大世界角色脚步 IK 方案

## 1. 现状分析

`Assets/Content/Resource/人物/Real/安比大世界.prefab`（大世界主角 安比）脚部与地面穿模，原因有两层：

| 层 | 现象 | 根因 |
|---|---|---|
| 静态整体下陷 | 站在平地上，脚趾也陷进地面约 7 cm | KCC 胶囊序列化值是 `radius 0.3 / height 1.43 / yOffset 0.8`，胶囊底在 pivot 上方 8.5 cm；KCC 让胶囊底贴地，于是整个角色被压低 8.5 cm |
| 地形贴合 | 斜坡、台阶上单脚悬空或插入地面 | 动画只是刚性播放腿部曲线，没有按左右脚各自的地面高度做 IK 修正 |

骨架与动画现状：

- `Animator.avatar = 安比Avatar`，`isHuman = False` —— **Generic 骨架**，Unity 自带的人形 Foot IK（`OnAnimatorIK` + `humanoid` IK goal）不可用。
- 动画由 Animancer 7.4.2 驱动，`Animator.applyRootMotion = True`，位移根运动由 `AnbiWorldKccDriver` 采样后交给 `KinematicCharacterMotor`。
- 骨链：`Bip001 L/R Thigh → Calf → Foot → Toe0`，公共父级 `Bip001 Pelvis`。
- 工程内已有 **Final IK**（`Assets/Plugins/RootMotion/FinalIK`），含 `Grounder` 包。

## 2. 设计

两处改动，都在预制体上，不改任何脚本。

### 2.1 胶囊高度改为 1.6

`KinematicCharacterMotor` 的 `CapsuleHeight` 由 `1.43` 改为 `1.6`，同时 `CapsuleCollider.height` 同步为 `1.6`。胶囊底从 pivot 上方 8.5 cm 变为与 pivot 齐平（脚底），角色不再整体下陷，站立时脚趾贴地。这是"穿模"的主因，IK 只负责地形贴合，不负责这根偏移。

### 2.2 Final IK：`GrounderIK` + 两条 `LimbIK`

| 挂载点 | 组件 | 配置 |
|---|---|---|
| `Bip001 L Thigh` | `LimbIK` | `bone1 = Bip001 L Thigh`、`bone2 = Bip001 L Calf`、`bone3 = Bip001 L Foot`，`target/poleTarget` 留空（由 GrounderIK 写 `IKPosition`） |
| `Bip001 R Thigh` | `LimbIK` | 同上，右腿 |
| 预制体根 | `GrounderIK` | `legs = [左 LimbIK, 右 LimbIK]`、`pelvis = Bip001 Pelvis`、`characterRoot = 预制体根`、`rootRotationWeight = 0` |

`characterRoot` 必须赋值：`GrounderIK.Initiate()` 里无条件执行 `characterRoot.GetComponent<Rigidbody>()`，留空会每帧抛 `UnassignedReferenceException` 且 IK 永远不初始化。`rootRotationWeight = 0` 时该引用只用于取 Rigidbody（角色没有 Rigidbody，取到 null），不参与旋转。

`GrounderIK` 求解参数（`Grounder.weight = 1`，`GrounderIK.solver`）：

| 参数 | 值 | 说明 |
|---|---|---|
| `layers` | 只勾 `environment`（第 6 层） | 与玩家 `whatIsGround = 64` 一致，避免打到自身胶囊 |
| `maxStep` | 0.6 | 同时决定"根部是否着地"的判定阈值（`rootHit.distance < maxStep × 2`）。跑动时双脚常同时离地、双脚中心一抬高就会被判为悬空，`Grounding.Leg.Process` 里 `if (!rootGrounded) offsetTarget = 0` 会把贴地目标整个清零；0.4 时楼梯上只有 68/110 帧判为着地，0.6 时 110/110 |
| `overstepFallsDown` | `false` | 射线在 `maxStep × 2` 内找不到地面时，脚留在根部高度，不下探一整个 `maxStep`；避免"脚悬在边缘被拉长 + 骨盆被压低" |
| `footRadius` | 0.18 | 脚部胶囊宽度。台阶踢面附近更早接触，脚尖会被提前抬起，不再插进上一级台阶的竖直面 |
| `heightOffset` | 0 | 胶囊已修正，不再额外抬高 |
| `footSpeed` / `pelvisSpeed` | 10 / 8 | KCC 一帧跨上 0.25 m 台阶后，双脚与骨盆要在约 1/40 s 内跟上；默认 2.5 会明显滞后 |
| `prediction` | 0.12 | 按脚速做提前量，落脚点提前对齐下一级踏面 |
| `footRotationWeight` / `maxFootRotationAngle` | 1 / 60 | 脚掌贴合命中法线；踢面附近命中法线是斜的，倾角上限从 45 提到 60 才能把脚尖抬起来 |
| `lowerPelvisWeight` / `liftPelvisWeight` | 1 / 0 | 骨盆只向下压到最低的脚，不被单脚顶高 |

`rootRotationWeight = 0`：角色朝向由 `AnbiWorldKccDriver.UpdateRotation` 独占写入（KCC 的插值每帧覆盖 `Transform.rotation`），GrounderIK 不再参与旋转，避免两套系统抢同一个 Transform。

### 2.3 艾莲大世界（同款处理的独立副本）

大世界切人 1 号位原本直接引用联机/战斗用的 `Assets/Content/Resource/人物/le_Size02_Ellen_Ani_Idle (1).prefab`：该预制体登记在 `DefaultNetworkPrefabs.asset`，带 `NetworkTransform` 与 `CharacterController`，`Animator.applyRootMotion = False`（战斗里位移由网络同步），不能直接改。因此新建大世界专用副本 `Assets/Content/Resource/人物/Real/艾莲大世界.prefab`（`AssetDatabase.CopyAsset`），在副本上：

| 项 | 处理 |
|---|---|
| `CharacterController` / `NetworkObject` / `NetworkTransform` | 删除（大世界单机，与安比大世界一致） |
| `Animator.applyRootMotion` | 改为 `True`，让 `AnbiWorldKccDriver` 采得到 `deltaPosition` |
| `Player.moveMult / whatIsGround / characterGravity` | `0 / 0 / 0`，旧 CharacterController 位移链路空转 |
| 胶囊 + KCC | `CapsuleCollider(center 0,0.8 / h1.6 / r0.3)` + `KinematicCharacterMotor`（序列化参数逐字段从安比大世界复制） |
| 驱动 | `AnbiWorldKccDriver`（gravity 20 / groundedStickSpeed 18） |
| 脚部 IK | `Bip001 L/R Thigh` 各挂 `LimbIK`，根节点 `GrounderIK`，参数同 2.2 |

艾莲骨架根是 `Avatar_Female_Size02_Ellen/Bip001/Bip001 Pelvis`，另有一套 `Bip002` 副骨架（不参与移动），IK 只挂 `Bip001`。她的 `PlayerSO` 是 `Assets/Game/Scripts/playso/鲨鱼连招/PlayerSOshatyu.asset`，`walk` 片段 `avgSpeed=(-2.52, 0, 5.58)` 带根运动。

场景 `dashijie.scene` 的 `DashijiePlayerSwitcher.heroPrefabs[1]` 已指向该副本；原战斗预制体保持不变。

### 2.4 执行顺序与耦合

- Final IK 的 IK 组件在 `LateUpdate`（`SolverManager`）里求解，晚于 Animancer 的动画采样与 KCC 的位移积分；只改骨骼本地旋转与骨盆位置，不动根节点，因此**不影响 KCC 的位移、碰撞与根运动累计**。
- 不改 `Player`、状态机、`AnbiWorldKccDriver`、`DashijiePlayerSwitcher`：切角色是实例化同一预制体，新角色自带 IK。
- 传送/存档落位后 `GrounderIK` 会按 `footSpeed`/`pelvisSpeed` 平滑收敛；需要瞬时对齐时调用 `GrounderIK.ResetPosition()`（当前不接）。

## 3. 影响面

- 改动文件：`Assets/Content/Resource/人物/Real/安比大世界.prefab`（改胶囊、新增 `LimbIK`×2 + `GrounderIK`）、新建 `Assets/Content/Resource/人物/Real/艾莲大世界.prefab`（KCC + 同款 IK）、`Assets/Game/Scenes/dashijie.scene`（切人器 1 号位改指向副本）。
- 无脚本改动、无协议改动、无 UI 改动。
- 联机/战斗用的原艾莲预制体与 `DefaultNetworkPrefabs.asset` 不受影响。

## 4. E2E 验收项

| 项 | 判据 | 实测 |
|---|---|---|
| 平地站立 | 双脚 `Toe0` 世界 Y 与脚下地面差 ≤ 2 cm，不再低于地面 | 改胶囊前：`Toe0` 比地面低约 7 cm（穿模）；改后：左脚 `+0.005`、右脚 `+0.004` |
| 静止稳定 | 30 帧内脚部高度无抖动 | 左 `std 0.0012 / range 0.0039`，右 `std 0.0007 / range 0.0025` |
| 台阶/边缘 | 高于地面的脚被 IK 拉向各自地面 | 0.22 m 台沿：`GrounderIK` 把右脚 IK 目标从骨骼 `0.184` 拉到 `0.120`，无穿透 |
| 跑动 | 跑/走动画正常播放，双脚无抖动、无反向弯折 | 行走 4 s：向东 24.1 m、向南 22.4 m，`grounded=True`、`minY=-0.09` 无坠落 |
| 位移 | KCC 位移量与改动前一致（同方向 4 s 行走距离误差 < 5%） | 改动前 4 s 各向 20.5~22.4 m；改动后 24.1 / 22.4 m，量级一致（`GrounderIK` 只改骨骼，不改根节点） |
| 初始化 | `GrounderIK.initiated = True`、`solver.isGrounded = True`、两条 `LimbIK` 的 `IKPositionWeight = 1` | 全部满足，Console 无 `UnassignedReferenceException` |
| 艾莲·站姿 | `SwitchTo(1)` 后双脚 `Toe0` 不低于地面 | 左脚 `+0.004`、右脚 `+0.009`（地面 `-0.098`），`GrounderIK.initiated = True` |
| 艾莲·位移 | 根运动经 KCC 真正推动角色 | Animancer 播 `walk` 60 帧：位移 `2.51 m`，`sum|deltaPosition| = 2.73 m`，`grounded = True` |
| 切人往返 | 0→1→0 都正常，相机重绑、HUD 同步 | `SwitchTo` 返回 `True`，日志 `切换角色 charId=1 → 艾莲大世界` + `座位0: 相机已关联本角色` |
| 楼梯·贴地判定 | 跑上楼梯时根部着地帧应占绝大多数 | 场景楼梯（`x≈-56, z=-38`，踏步 0.25 m / 进深 0.5 m）跑 110 帧：`maxStep=0.4` 时 `rootGrounded` 68/110；改 0.6 后 110/110 |
| 楼梯·脚尖穿透 | 脚尖不插进上一级台阶竖直面 | 慢放录像肉眼核对：改前每次落地脚尖都嵌进踢面（00:08-00:11、00:29-00:31 最严重）；`footRadius 0.18 + maxFootRotationAngle 60 + prediction 0.12` 后同一段楼梯"脚贴合踏面、无穿入、无悬空、无拉伸" |
| 平地回归 | 改参数后平地仍贴地、不抖 | 安比 `Toe0 +0.005 / +0.005`，骨盆抖动 `std 0.0004`；艾莲 `+0.005 / +0.009`，`std 0.0002` |

## 5. 风险

| 风险 | 处理 |
|---|---|
| IK 后于动画执行，若 Animancer 的更新被推迟到 LateUpdate 之后会出现锯齿 | 实测静止与跑动帧间角度；必要时把 `Grounder.weight` 降档或提高 `footRotationSpeed` |
| `maxStep` 偏大让脚在边缘下探过多 | `overstepFallsDown = false` 时下探只在射线真正命中地面时发生；`lowerPelvisWeight = 1` 会把骨盆一起压低，实测平地骨盆抖动 `std 0.0004` 无异常 |
| `footRadius 0.18` 让脚在台沿附近略微抬高（约 2 cm） | 取舍：抬高 2 cm 几乎不可见，脚尖插进踢面非常明显，按"不穿模"优先 |
| `rootGrounded` 依赖双脚中心，跑步全程腾空的片段仍可能短暂失准 | `maxStep 0.6` 把判定阈值提到 1.2 m，实测跑上 8 m 高差楼梯全程 `rootGrounded = true` |
| 骨架差异：安比是 Generic（`isHuman=False`），艾莲是 Humanoid | 两者都用 Final IK 的 `LimbIK`，直接按骨链 `Bip001 Thigh/Calf/Foot` 驱动，不依赖 Humanoid 重定向 |
