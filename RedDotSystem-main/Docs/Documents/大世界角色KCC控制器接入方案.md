# 大世界角色（安比大世界）KCC 控制器接入方案

> 日期：2026-10-28
> 范围：`Assets/Content/Resource/人物/Real/安比大世界.prefab` 与 `dashijie` 场景里的该实例
> 插件：`Assets/Plugins/KinematicCharacterController-main`（`Gavidev.KCC`，`KinematicCharacterMotor` + `ICharacterController`）
> 约束：不改动任何已有脚本（`Player`、状态机、连招、音频、交互全部原样）；预制体功能不减少

---

## 1. 现状

`安比大世界.prefab` 的组件：`Animator / CharacterController / AnimancerComponent / Player / ActorAudioComponent / HealthModel / BuffComponent`，`Player.playerSO = 安比连招/Playeranbi.asset`。

实际位移链路是「动画根运动」：

1. `Player`（继承 `CharacterMoveControllerBase : NetworkBehaviour`）在 `OnAnimatorMove` 里取 `Animancer.Animator.deltaPosition`；
2. 经 `ResetVelocityOnSlop` 斜坡修正后乘 `moveMult`，交给 `CharacterController.Move`；
3. `CharacterController.Move` 负责碰撞、台阶与阻挡；重力由基类自己的球体地面检测 + `verticalSpeed` 走同一条 `Move`。

移动/连招/闪避/技能/受击/死亡全部由 `Player` 内部的两个状态机驱动，状态只播动画、写朝向，不直接改位置。因此**位移后端可以从 `CharacterController` 换成 KCC，而不动状态机**。

## 2. 设计

### 2.1 分工

| 层 | 归属 |
|---|---|
| 动画、状态机、连招、闪避/无敌、技能冷却、受击/死亡、音频、生命/Buff/装备、相机绑定、光标 | `Player`（原脚本，不改） |
| 位移、碰撞、台阶、坡度、重力、贴地 | `KinematicCharacterMotor`（KCC 插件） |
| 把「根运动」翻译成 KCC 速度 | 新增 `AnbiWorldKccDriver.cs`（本方案唯一的运行时代码） |

### 2.2 `Player` 的保留与 `CharacterController` 的移除

- `Player` 是唯一持有两套状态机的对象，且状态机内部直接使用 `Player` 类型与私有字段；外部无法替换，只能保留。
- `Player` 的基类每帧调用 `CharacterController.Move`。为了把 KCC 角色上的这个组件彻底删掉，`CharacterMoveControllerBase` 只加了两处空引用保护（不改任何行为）：
  - `UpdateVerticalVelocity`：`characterController == null` 时直接返回；
  - `UpdateCharacterVelocity`：`characterController == null` 时直接返回（`OnAnimatorMove` 的根运动由 KCC 驱动脚本消费）。
- 预制体上原位移链路的参数仍保持 `moveMult = 0`、`whatIsGround = 0`、`characterGravity = 0`，即使以后误加回 `CharacterController` 也不会与 KCC 抢位移。
- 有 `CharacterController` 的角色（安比dan / 艾莲 / 战斗角色）走原路径，两处判空不命中，行为不变。

### 2.3 驱动脚本 `AnbiWorldKccDriver`

`MonoBehaviour + ICharacterController`，挂在角色根上（与 Motor 同物体）：

- `Awake`：`motor.CharacterController = this`；缓存 `Animator`。
- `LateUpdate`：累加 `animator.deltaPosition`（在 `OnAnimatorMove` 之后采样，拿到本帧根运动）。
- `UpdateVelocity`：把累加位移换算成速度写进 KCC：
  - 着地：按地面法线投影根运动，竖直给一个小的贴地速度；
  - 离地：保留 KCC 累积的竖直速度并按 `gravity` 递减；
  - 根运动每次 tick 消费后清零（与 `CharacterController` 原语义一致：动画不动就不位移）。
- `UpdateRotation`：**不能**沿用 `Transform` 当前朝向。KCC 的插值每帧都会用 `TransientRotation` 覆盖 `Transform`（`PreSimulationInterpolationUpdate` + `CustomInterpolationUpdate`），状态机在 `Update` 里写的朝向会被抹掉；驱动改为从 `Player.MovementStateMachine.reusableData` 取 `worldMovement`（状态机算好的「相机相对移动方向」）与 `rotationTime`，在 KCC 的 tick 内按同样的指数平滑转向。
- `IsColliderValidForCollisions`：忽略自身与子物体上的碰撞体（`CapsuleCollider`、武器 `HitCollider`/`WallCollider`），否则 KCC 会把自己当墙。

### 2.4 预制体改动

| 项 | 改动 |
|---|---|
| `CapsuleCollider` | 新增：`radius 0.3`、`height 1.43`、`center (0, 0.8, 0)`、层 `player`（与 KCC 胶囊一致，原 `CharacterController` 同尺寸） |
| `KinematicCharacterMotor` | 新增：胶囊尺寸同上；`StableGroundLayers = environment(6)`；`MaxStableSlopeAngle = 45`；`StepHandling = Standard`、`MaxStepHeight = 0.3`、`MinRequiredStepDepth = 0.1`；`LedgeAndDenivelationHandling = true`；`CollidableLayers = Everything` |
| `AnbiWorldKccDriver` | 新增：`gravity = 20`、`groundedStickSpeed = 18`；`CharacterController` 字段指向自身（运行时 `Awake` 赋值，接口字段不参与序列化） |
| `Player`（基类字段） | `moveMult = 0`、`whatIsGround = 0`、`characterGravity = 0`（原位移链路空转，防误加回 CC 时抢位移） |
| `CharacterController` | **已移除**（基类两处判空后不再需要） |

`dashijie` 场景里的 `DashijiePlayerSwitcher.heroPrefabs[0]` 从 `安比dan.prefab` 改为 `安比大世界.prefab`，保证数字键切回 1 时拿到的是 KCC 版本。

### 2.5 明确不做

- 不改 `Player` 与任何状态机/连招脚本；`CharacterMoveControllerBase` 只加两处空引用保护（见 §2.2），不改变任何角色的原有行为；
- 不新增第二套状态机，不复制动画/战斗逻辑；
- 不改 `CharacterController` 以外的原有组件引用、不换模型与动画资产；
- 不把 `CharacterCombo` 的「粘刀」改成 KCC（它走 `CharacterController.Move`，只在有敌人的战斗里生效，世界场景无影响）。

---

## 3. 验收项

| # | 场景 | 期望 |
|---|---|---|
| 1 | 进 `dashijie` | 角色站在出生点，`KinematicCharacterMotor` 有 tick，Console 无 `CharacterController.Move` 警告、无 NRE |
| 2 | 走/跑 | WASD 相机相对移动、Shift 冲刺，速度与改前一致（根运动驱动），不穿地、不悬空 |
| 3 | 台阶/斜坡 | 上 0.3m 台阶顺畅，斜坡不抖动、不下滑 |
| 4 | 攻击/闪避 | 连招与闪避位移正常（根运动仍然生效），动画与打击帧不变 |
| 5 | 相机/血条/音频 | 相机绑定、HUD 血条、脚步音效与改前一致 |
| 6 | 切人 | 1/2 切换后安比是 KCC 版本，艾莲仍为原控制器，两者都能移动、相机跟随正常 |

---

## 4. 风险

| 风险 | 规避 |
|---|---|
| 根运动采样时机不对导致位移丢失或翻倍 | 在 `LateUpdate` 采样（`OnAnimatorMove` 之后），每 tick 消费后清零；验收 2/4 专门看位移 |
| KCC 把自己的 `CharacterController`/武器碰撞体当障碍 | 驱动里过滤自身子树的所有碰撞体 |
| 根运动是逐帧量、KCC 是定步长 | 按 `deltaTime` 换算成速度，跨帧累加；台阶/坡度由 KCC 解算 |
| `Player` 内部仍假设有地面/重力 | 出厂 `whatIsGround=0`、`characterGravity=0`，这些状态只影响已被清零的原位移链路 |
| 场景实例存在旧字段覆盖 | 改完预制体后检查场景实例的 `Player` 字段没有把新值盖回去 |

---

## 5. 实测结论（2026-10-28）

预制体组件顺序：`Transform / Animator / AnimancerComponent / Player / ActorAudioComponent / HealthModel / BuffComponent / CapsuleCollider / KinematicCharacterMotor / AnbiWorldKccDriver`（`CharacterController` 已移除）。

Motor 配置：胶囊 `0.3 / 1.43 / yOffset 0.8`，`StableGroundLayers = environment(64)`，`MaxStableSlopeAngle = 45`，`StepHandling = Standard / MaxStepHeight 0.3 / MinRequiredStepDepth 0.1`，`LedgeAndDenivelationHandling = true`；驱动 `gravity = 20`、`groundedStickSpeed = 18`。

| 项 | 结果 |
|---|---|
| 静止稳定 | 1 秒漂移 `0.0000m`（修复前会在台阶/曲面交界处以 ~2m/s 绕圈：常驻的向下贴地速度被 KCC 投影到斜坡法线后变成水平推力；现在只在有根运动时按 `dt` 施加，且 <5cm/s 的待机残留视为静止） |
| 相机相对转向 | 修复前：`Transform` 外部写入会被 KCC 插值抹回原朝向（实测设 90° 立刻回到旧角度），角色永远朝出生方向跑，按 WASD 只会沿世界 +Z 移动。修复后：把 `worldMovement` 注入状态机共享数据，KCC 内转向到 +X / -Z 均正确，随后播放跑动画的位移方向与朝向一致（`movedDir` = `charFwd`） |
| 行走位移 | 空地按住 `W`：实际位移 / 动画根运动累计 = `0.99`（没有双倍或丢失） |
| 台阶 | 临时 0.2m 台阶：跨过时最高点 `y=0.15`，成功上台；1m 高的方块正常被挡（不越级爬墙） |
| 落位 | `DashijieSceneSaveController` 走 `motor.SetPositionAndRotation(..., true)` 并清空驱动残留根运动，落位后 1 秒漂移 `0.0000m` |
| 切人 | `1 ↔ 2` 切换：艾莲仍是原 `CharacterController` 版本；切回 1 是 `安比大世界`，`KinematicCharacterMotor` 存在、`stableGround=True`、静止漂移 `0.0000m`，相机绑定日志正常 |
| 日志 | 除既有的未登录存档提示与 UI 相机 Overlay 警告外，无 error、无 `CharacterController.Move` 警告、无 NRE |
| 无 CharacterController | 移除后 `Player` 基类两处判空生效：Idle 漂移 `0.0000m`、转向 `fwd=(1,0,0)`、跑动画位移方向与朝向一致；`1↔2` 切人后切回仍是 KCC 角色、`drift=0.0000`，艾莲（有 CC）不受影响 |

未实测：真实键盘手感与连招/闪避的按键链路（位移链路与行走同源，均为动画根运动）；角色没有跳跃，未验证空中控制。

