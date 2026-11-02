# 《KCC 源码笔记》
### 从 3C 运动控制到 Controller 设计 —— 基于 `com.janooba.kcc` v1.0.1 的源码笔记

> 代码版本：本目录下 9 个 C# 文件，共 3170 行（`KinematicCharacterMotor.cs` 2494 行）
> 文中所有 `文件:行号` 均指向本仓库真实代码，可直接跳转对照
> **怎么读**：主线是 12 附 → 13 → 13 附 → 14；第 2、3、5、6 集只需要读"会改我速度的地方"，解算细节可以跳过。本版砍掉了面试与视频制作内容，专注 Controller（原稿见同名 `.bak`）。

---

## 课程结构

| 集 | 内容 |
|---|---|
| 0 | 资料地图与版本陷阱 |
| 1 | 为什么是 Kinematic |
| 1 附 | Unity CharacterController 的五个痛点 |
| 2 | 一次 Tick 全景（谁在什么时候改你的速度） |
| 3 | 两阶段调度与插值 |
| 4 | 三套位姿与四份报告 |
| 5 | Phase1：三处会改你速度的地方 |
| 6 | 地面判定：什么叫"稳定"、参数怎么调 |
| 9 | 台阶：参数与两个取舍 |
| 10 | 移动平台：数据流与坑 |
| 11 | 刚体交互：质量比与三种模式 |
| 12 | 插值与时间步 |
| **12 附** | **Controller 精要（主线，不读源码）** |
| **13** | **从零写一个 Controller** |
| **13 附** | **状态机、Root Motion、蹲伏** |
| **14** | **相机与 3C 闭环** |
| 15 | 性能、多角色、网络确定性 |
| 16 | 排错手册 |
| 附录 | 常量 / 回调顺序 / 术语 / 学习路线 / 方案对比 / FAQ / 实验课 |

> 编号沿用原文档：第 7、8 集（InternalCharacterMove 逐行、速度投影数学）与附录 B 已删除，其结论压缩进了第 5 集和 12 附。

**3C 定位**

- **Character** = 胶囊体 + 表现层 + 状态（`GroundingStatus`、`BaseVelocity`）
- **Control** = 意图 → 速度与朝向（`UpdateVelocity` / `UpdateRotation`）
- **Camera** = 参照系 + 表现平滑

> KCC 是"带碰撞解算的位移求解器"：不产生力，也不产生加速度。加速度、摩擦、跳跃全在 Controller 里。

**两条路线**

| 目标 | 顺序 | 产出 |
|---|---|---|
| **做玩法（主线）** | 1 → 2 → 4 → 5 → 6 → **12 附** → 13 → 13 附 → 14 | 可维护的 Controller + 调参表 |
| 想懂平台/台阶/推箱子 | 9 → 10 → 11 | 知道边界在哪、参数怎么配 |

---

# 架构总图（抄在纸上，配合 12 集附.2 看）

```
                 ┌──────────── KinematicCharacterSystem (FixedUpdate, order -100)
                 │
   ① Mover.VelocityUpdate ──► 目标位姿 → Velocity / AngularVelocity
                 │
   ② Motor.UpdatePhase1 ────► BeforeCharacterUpdate
                 │              ├ 解 MoveCharacter 队列
                 │              ├ 解初始重叠（只推静态体）
                 │              ├ ProbeGround + Snap  ← 用"平台旧位姿"
                 │              ├ 落地速度投影（杀掉下坠速度）
                 │              └ 跟随平台移动（attachedRigidbodyVelocity）
                 │
   ③ Mover 应用新位姿 ──────► 平台先动
                 │
   ④ Motor.UpdatePhase2 ────► UpdateRotation
                 │              ├ 解平台推挤产生的重叠（IsBeingCrushed）
                 │              ├ UpdateVelocity  ← 你的重力/加速/跳跃
                 │              ├ InternalCharacterMove  ← collide & slide
                 │              ├ 刚体冲量 / 平面约束 / 离散碰撞
                 │              └ AfterCharacterUpdate
                 │
   LateUpdate: Transform = Lerp(InitialTick, Transient, f)
```

---

# 第 0 集 · 资料地图与版本陷阱

**资料（按可信度排序）**

1. **本仓库源码**（9 个文件，3170 行）—— 唯一权威，文档与代码冲突以代码为准
2. **官方 User Guide（PDF）** —— 描述的是 **3.x 老架构**，算法一致、调度不同
3. **官方 Walkthrough** —— 示例角色的状态机、梯子、蹲伏、Root Motion
4. **OpenKCC** —— 另一套工业实现，适合对照

**版本陷阱**

| 官方 3.x | 本仓库 v1.0.1 |
|---|---|
| `BaseCharacterController` 抽象类 | `ICharacterController` 接口 |
| `CharacterUpdatePhase1/2/3` | `UpdatePhase1/2`（第三阶段被合并） |
| `PhysicsMover.SimulateAtGoal / Desimulate` | 直接 `SetPositionAndRotation` |
| 查询预算 32 / 32 | **16 / 16** |

**官方手册的五条使用契约**（都是坑）

1. 查询是 NonAlloc + 固定数组，超预算丢命中 → 穿模
2. 插值必须由系统统一做，不能交给 Rigidbody
3. 可以关 `Physics.autoSyncTransforms`，但你在回调里自建的查询要自己 `SyncTransforms`
4. 可以关自动模拟，自己驱动 tick（服务器 / 回放）
5. Root Motion 与 FixedUpdate 频率不一致，必须"累加 + 消费"（13 集附）

---

# 第 1 集 · 设计取舍：为什么是 Kinematic

**目标**：搞清 KCC 在整个角色方案谱系中的位置，能回答"为什么不用 Rigidbody / 不用 CharacterController"。

### 板书（一张图讲完）

```
                 手感可控性 ↑
   Dynamic Rigidbody ── CharacterController ── KCC ── 纯数学位移
   (物理真实)                                  (完全可控)
```

### 口播要点

1. **Dynamic Rigidbody 的问题**：力 → 加速度 → 速度，中间隔了物理引擎的求解器和摩擦模型。你给一个 `AddForce`，落地时会被 solver 弹一下、贴墙时会被 friction 粘住、斜坡上会自动滑。手感调不准，且不同 PhysX 版本行为会变。
2. **Unity 自带 CharacterController 的问题**：它是 C++ 黑盒，`Move()` 只返回 `CollisionFlags`，你拿不到**命中法线、命中点、是否稳定地面、是否台阶**。想做斜坡滑落、贴墙滑行、台阶自动上台，只能靠外挂射线硬猜。
3. **KCC 的答案**：**自己做 Sweep + 自己做解算**，物理引擎只负责两件事——`Physics.CapsuleCastNonAlloc`（扫掠查询）和 `Physics.ComputePenetration`（重叠解算）。
4. **代价**：你要自己实现重力、加速度、摩擦、跳跃、斜坡、平台、推箱子。KCC 把这部分留成了 `ICharacterController` 接口 —— 这就是本课程第 13 集的重点，也是 3C 里 Control 的真正战场。
5. **为什么"运动学"**：角色的位置由代码**直接写入**（`transform.SetPositionAndRotation`），不参与物理积分。所以**永远不会被物理引擎"偷偷推动"**，确定性高，帧间可复现，利于回放/联机。

---

# 第 1 集附 · 对照组：用 Unity CharacterController 做同一件事

**目标**：把"为什么最后都换 KCC"讲成**具体现象**，而不是"KCC 更高级"。这一节是踩坑史。

### 一、CharacterController 的五个"够用但难受"

下面每一条都是 Unity 官方 CC 的标准做法，也是它的天花板：

| 需求 | 官方 CC 的标准做法 | 卡在哪 |
|---|---|---|
| 移动 | `Move()`（完全手动）/ `SimpleMove()`（自带重力、忽略 y 速度） | 只返回 `CollisionFlags`，不知道撞的是**什么面、什么点、稳不稳** |
| 跳跃 | `isGrounded` + 自己积重力 | `isGrounded` 有延迟/抖动，落地那一帧速度不清零 → 起跳高度随机 |
| 下坡 | `isGrounded` 为 false 时手动 Raycast 往下贴 | 斜坡上 `isGrounded` 反复翻转，表现为**下坡抖动**（社区最经典的问题） |
| 下蹲 | 改 `height` / `center`，再手动检测头顶 | 站起时容易顶穿天花板；改尺寸和碰撞检测的时序要自己保证 |
| 推箱子 | `OnControllerColliderHit` 里给刚体 `AddForce` | 力度靠拍脑袋，没有质量比、没有冲量守恒，箱子会"黏"在角色上 |

### 二、同一件事，KCC 怎么解

| 问题 | KCC 的机制 | 代码 |
|---|---|---|
| 撞到了什么 | 命中法线 + 命中点 + `HitStabilityReport` | `1938` |
| 下坡抖 | 站地时把探测距离放大到 `max(radius, MaxStepHeight)`，一帧内"吸"回地面 | `930-940` |
| 落地速度 | 刚落地那一帧把速度投影到地面切面，干掉下坠分量 | `942-947` |
| 下蹲顶穿 | `SetCapsuleDimensions` 重算缓存几何 + 站起前用"站立尺寸"单独检测 | `758` |
| 推箱子 | 记账 + 质量比伪冲量 + `ForceMode.VelocityChange` | `1783` / `1860` |

### 三、一句话总结

> "CharacterController 是**能用**：它保证你不穿墙、能爬小台阶。但它把'为什么能站、撞到了什么、该怎么滑'全部封在黑盒里，你只能靠射线去猜。KCC 是把这套黑盒**打开**：同样的运动学思路，但每一步都给你法线、稳定性报告和投影决策点。所以换 KCC 不是换引擎，是**把不可控变成可控**。"

> 反过来说也成立：**如果你只是要一个能走能跳的原型，官方 CC 更快**。KCC 的成本在于你必须自己写 `ICharacterController`（第 13 集，200 行起步）。

---

# 第 2 集 · 一次 Tick 全景（谁在什么时候改你的速度）

**目标**：建立全局心智模型，之后每集都能"定位到地图上的哪一格"。

**这张图是全文的地基：每秒 50 次，按这个顺序发生。**

```
FixedUpdate (System, DefaultExecutionOrder = -100)
 ├─ [可选] PreSimulationInterpolationUpdate   把 Transform 拉回 Transient（上帧插值残影）
 ├─ foreach Mover: VelocityUpdate             (目标位姿 → Velocity/AngularVelocity)
 ├─ foreach Motor: UpdatePhase1
 │    ├─ BeforeCharacterUpdate                ← 你的回调
 │    ├─ 处理 MoveCharacter 队列
 │    ├─ 解算初始重叠（非交互刚体）
 │    ├─ 地面探测 + 吸附 + 落地速度投影
 │    ├─ PostGroundingUpdate                  ← 你的回调
 │    └─ 跟随 attachedRigidbody 移动
 ├─ foreach Mover: 应用新位姿到 Transform + Rigidbody   ← 平台先动
 ├─ foreach Motor: UpdatePhase2
 │    ├─ UpdateRotation                       ← 你的回调
 │    ├─ 处理 RotateCharacter 队列
 │    ├─ 解算 attachedRigidbody 上浮 + 二次重叠
 │    ├─ UpdateVelocity                       ← 你的回调（重力/加速/跳跃）
 │    ├─ InternalCharacterMove（Sweep 解算）
 │    ├─ 刚体冲量处理 / 平面约束 / 离散碰撞
 │    └─ AfterCharacterUpdate                 ← 你的回调
 └─ [可选] PostSimulationInterpolationUpdate  把 Transform 拉回 InitialTick（供 LateUpdate 插值）

LateUpdate: CustomInterpolationUpdate           InitialTick → Transient 的 Lerp/Slerp
```

---

# 第 3 集 · KinematicCharacterSystem：两阶段调度与插值

**目标**：能解释"为什么整个系统要拆成 Phase1 / Phase2"，这是 KCC 最核心的架构决策。

### 为什么必须两阶段（本集最重要的 3 分钟）

把一帧想成"**平台先动、角色后动**"，但角色的**地面检测**必须在平台移动**之前**完成，否则：

1. 平台从 A 移到 B，角色还站在 A 的空中；
2. 角色做地面检测 → 检测不到地面 → 判定为"离地"；
3. 角色开始下坠 → 下一帧又贴回平台 → **每帧上下抖动**。

KCC 的解法：

- **Phase1（平台移动前）**：用平台**旧位姿**做地面探测，确定"我是否站在平台上、站在哪个点"；然后把平台速度作为 `_attachedRigidbodyVelocity` **额外移动角色一次**（`KinematicCharacterMotor.cs:1041`）。
- **Phase2（平台移动后）**：解算因平台推挤/自身旋转产生的重叠（`1101-1174`），再跑自身的速度移动。

于是角色的位移被拆成两笔：**平台带的位移（Phase1）+ 自己走的位移（Phase2）**。这就是 `Velocity => BaseVelocity + _attachedRigidbodyVelocity`（`546`）的由来。

### 插值开关

`FixedUpdate` → `if (!Settings.AutoSimulation) return;`（`122`）
→ 关掉自动模拟后，你自己在任意时机调 `Simulate`（服务器/回放/自定义时间步）。

---

# 第 4 集 · 数据模型：Transient 世界与 Report 家族

**目标**：理解 KCC 的"三套坐标"和"四份报告"，这是读懂后面前提。

### 三套位姿（必须分清，90% 的抖动问题出在这里）

| 概念 | 字段 | 含义 |
|---|---|---|
| Transform | `_transform` | Unity 世界坐标，**渲染/其他系统看到的**，可被插值 |
| Transient | `_transientPosition`、`_transientRotation` | **物理真相**，本 tick 解算后的权威位姿 |
| InitialTick | `InitialTickPosition`、`InitialTickRotation` | 本 tick 开始前的位姿，**插值的起点** |

插值公式（`KinematicCharacterSystem.cs:250`）：

```csharp
transform.SetPositionAndRotation(
    Vector3.Lerp(InitialTickPosition, TransientPosition, f),
    Quaternion.Slerp(InitialTickRotation, TransientRotation, f));
```

> 一句话：**Transient 是"逻辑"，Transform 是"表演"**。所有查询/解算只认 Transient，绝不能用 `transform.position` 做碰撞。

### 四份报告（struct，全部值类型 = 零 GC）

1. `CharacterGroundingReport`（`67`）：`FoundAnyGround` / `IsStableOnGround` / `SnappingPrevented` / `GroundNormal` / `InnerGroundNormal` / `OuterGroundNormal` / `GroundCollider` / `GroundPoint`
2. `CharacterTransientGroundingReport`（`96`）：**去掉 Collider 引用**的版本 → 因为要序列化进 `GetState()`（存盘/回滚时不能持引用）。注意 `CopyFrom` 会把 `GroundCollider` 置 null（`88`）
3. `HitStabilityReport`（`119`）：一次命中判定的完整上下文 —— `IsStable`、内外法线、`ValidStepDetected`、`SteppedCollider`、以及 6 个 ledge 字段
4. `RigidbodyProjectionHit`（`143`）：把"撞到刚体"这件事**延迟记账**，等移动结束再统一算冲量

---

# 第 5 集 · Phase1：三处会改你速度的地方

> 这是"读 Motor 的唯一理由"：它们会在 `UpdateVelocity` 之前或之后动你的速度。

**① 地面吸附（`919-957`）**

站在地上时，向下探测距离被放大到 `max(胶囊半径, MaxStepHeight)`，远大于一帧重力位移，所以下坡不会"飞起来"；上一 tick 不在地面时只探 0.005，避免离地瞬间被吸回去（表现为跳不起来）。

**② 落地速度投影（`942-947`）**

从空中落到地面的那一 tick，Motor 会先投影掉竖直速度，再把速度转到坡面。于是：

- **落地帧读到的竖直速度是 0**，不能拿它做落地伤害或落地特效
- 想用落地速度，必须缓存**上一 tick 空中时**的 `Motor.BaseVelocity`

**③ 平台动量（`987-1005`）**

站平台时，平台速度单独做一次位姿移动；`Motor.Velocity = BaseVelocity + 平台速度`（只读）。脱离平台时 Motor 自动把平台速度补进 `BaseVelocity`。**手动再加一次就是双倍**，表现为"跳上移动平台直接飞出去"。

**两个上限**

- 迭代预算 `MaxMovementIterations = 5`：超了会按开关**清速度 / 清剩余位移**（`1563-1568`），极端情况表现为"卡住"
- 最小速度 `MinVelocityMagnitude = 0.01`（`private const`）：低于它的速度每 tick 被清零，影响极慢速微调

---

# 第 6 集 · 地面判定：什么叫"稳定"、参数怎么调

**三层判定**

1. **法线角**：`Angle(CharacterUp, normal) <= MaxStableSlopeAngle`（默认 60°）
2. **边缘 / 落差**：脚一半悬空时 `LedgeDetected` 且距离 > `MaxStableDistanceFromLedge`（0.5）→ 判为不稳定，会掉下去
3. **台阶**：只有"不稳定"才尝试抬升（第 9 集）

**三个必调参数**

| 参数 | 默认 | 调它会发生什么 |
|---|---|---|
| `MaxStableSlopeAngle` | 60 | 决定"能不能站"。超过阈值**不会自动滑**，滑不滑取决于你空中分支是否加重力 |
| `GroundDetectionExtraDistance` | 0 | 加大 → 高速下坡更贴地；太大 → 吸附到不想要的低处 |
| `LedgeAndDenivelationHandling` | true | 关掉 → 脚悬空也判可站（"磁铁脚"） |

**斜坡抖动排查顺序**：坡角是否贴着阈值（临界角每 tick 翻转）→ 装饰物是否进了 `StableGroundLayers` → 是否在 Controller 里又自己贴了一次地。

---

# 第 9 集 · 台阶 Step Handling

**目标**：能讲清"一次台阶检测要做几次射线"，以及 Standard 与 Extra 的区别。

### 两处台阶代码

1. **判定**：`DetectSteps`（`2024`）/ `CheckStepValidity`（`2058`）—— 回答"这个障碍物算不算能踩的台阶"
2. **执行**：`InternalCharacterMove` 里的 `1490-1523` —— 回答"那就传送上去"

### DetectSteps 算法（`2024`）

```
起点 = hitPoint - 垂直分量 + up*MaxStepHeight + 水平方向*0.03   (2030)
向下 CapsuleCast，高度 = MaxStepHeight + 0.01，收集多个命中     (2033)
CheckStepValidity: 挑"最远"的命中（= 台阶顶面）
    ① 该位置无重叠（Overlap）
    ② 从命中点向外再打一条向下射线，确认"外侧坡面稳定"
    ③ 从当前角色位置向上 CapsuleCast，确认上方没挡住（净空）
    ④ 内侧确认：AllowSteppingWithoutStableGrounding ? true :
         从"台阶高度的胶囊中心"向下打 → 或从"台阶内沿"向下打，任一稳定即可
```

- **复用判定结果（关键优化）**：`EvaluateHitStability` 在移动命中时就已经算出 `ValidStepDetected` 和 `SteppedCollider`（`2013`）。执行阶段只需再做一次向下扫掠，然后比对"扫到的 collider" 与 `SteppedCollider` **是不是同一个**（`1507`）——相同才算合法台阶，不同就说明扫到的是天花板或别的物体。这样省掉了重新判断台阶合法性的整套流程。

> 为什么选"最远"命中？台阶顶面是向下扫掠中**最后一个**有效面（再往前就是地面）。取最远 = 踩到顶面边缘，能最大化前进量。

### 执行（`1490-1523`）的两个关键取舍

```csharp
if (StepHandling != None && moveHitStabilityReport.ValidStepDetected) {
    float obstructionCorrelation = Mathf.Abs(Vector3.Dot(closestSweepHitNormal, _characterUp));
    if (obstructionCorrelation <= CorrelationForVerticalObstruction) {  // 0.01，只对"近乎垂直的墙"做台阶
        ... 向下扫到台阶面 ...
        tmpMovedPosition = endStepPosition;      // 直接传送
        transientVelocity = ProjectOnPlane(transientVelocity, CharacterUp);  // 水平速度保留，竖直清零
    }
}
```

- **竖直速度清零**：否则上台阶瞬间会带着"下压速度"，下一帧又被地面顶起来 → 抖动
- **不做位移补偿**：上台阶是"免费"的，不减速 —— 这是设计选择，追求手感。想要"上台阶变慢"就自己在这一步扣速度

### Standard vs Extra（`2043`）

- `Standard`：只做"从角色位置上方垂直下落"的检测，够用于大多数台阶
- `Extra`：再做一次"从角色位置 + MaxStepHeight、向前 `MinRequiredStepDepth` 处"的检测 —— 能踩到**更矮/更窄**的台阶（代价：多一次扫掠 + 可能踩到不该踩的矮边）

---

# 第 10 集 · 移动平台 PhysicsMover

**目标**：能解释 `VelocityUpdate` 的一行公式，以及为什么平台必须 kinematic。

### 数据流

```
IMoverController.UpdateMovement(out goalPos, out goalRot, dt)   ← 你决定去哪
        ↓
PhysicsMover.VelocityUpdate: Transient 直接设为 goal
        Velocity = (goalPos - initPos) / dt
        AngularVelocity = Deg2Rad * (rotationDelta).eulerAngles / dt   ← 注意这里有坑
        ↓
System.Simulate 把新位姿写入 Transform + Rigidbody.position
        ↓
角色的 Phase2 做重叠解算、Phase1 算 _attachedRigidbodyVelocity
```

### 代码要点

- `ValidateData`（`143`）：强制 `isKinematic = true`、`centerOfMass = 0`、`maxAngularVelocity = Infinity`、`maxDepenetrationVelocity = Infinity`、`interpolation = None`
  → 目的：让平台**完全由代码控制**，物理引擎不许推它；`maxDepenetrationVelocity = Infinity` 防止角色被挤在平台和墙之间时平台被"顶开"
- `MoveWithPhysics`（`35`）：`true` 用 `Rigidbody.MovePosition`（走物理插值管线，能正确推开动态刚体）；`false` 直接写 `position`
- 插值同步（`KinematicCharacterSystem.cs:262-267`）：平台每帧记录 `PositionDeltaFromInterpolation`，供"站在平台上的角色"做额外补偿（官方示例角色用它做视觉平滑）

### 已知坑：`eulerAngles` 的 0~360 回绕

`PhysicsMover.cs:255`：

```csharp
Quaternion rotationFromCurrentToGoal = TransientRotation * Quaternion.Inverse(InitialSimulationRotation);
AngularVelocity = (Mathf.Deg2Rad * rotationFromCurrentToGoal.eulerAngles) / deltaTime;
```

`eulerAngles` 返回 0~360，一个 **-1°** 的旋转会被解析成 **359°**，角速度瞬间变成 359 倍 → 角色被甩飞。
**修法**：把欧拉角归一化到 (-180, 180]：

```csharp
Vector3 e = rotationFromCurrentToGoal.eulerAngles;
e.x = e.x > 180 ? e.x - 360 : e.x;
e.y = e.y > 180 ? e.y - 360 : e.y;
e.z = e.z > 180 ? e.z - 360 : e.z;
AngularVelocity = Mathf.Deg2Rad * e / deltaTime;
```

> 这类"源码里的小 bug"值得记一笔：说明你不是背 API，而是真的读过、想过、验证过。同类还有 `StoreRigidbodyHit` 对"另一个 KCC"的提前 return（`1610`），使得 `ProcessVelocityForRigidbodyHits` 里"角色互推"的分支成为死代码（详见第 11 集）。

---

# 第 11 集 · 刚体交互与推箱子

**目标**：理解 KCC 的"伪物理"，能回答"为什么推箱子不是真的物理"。

### 三段式设计

1. **记账**：移动中撞到刚体 → `StoreRigidbodyHit`（`1606`）存 `RigidbodyProjectionHit`
   - 去重 ①：结算时同一个刚体只吃一次冲量（`_ridbodiesPushedThisMove.Contains`，`1789`）
   - 过滤 ②：**撞到另一个 KCC 直接 return，不记账**（`1610`：`if (hitRigidbody.GetComponent<KinematicCharacterMotor>()) return;`）
2. **结算**：`ProcessVelocityForRigidbodyHits`（`1783`）在移动结束后统一算
3. **冲量**：`ComputeCollisionResolutionForHitBody`（`1860`）

### 质量比模型

```csharp
characterToBodyMassRatio = mC / (mC + mB);          // 1822
if (!hitBodyIsDynamic) ratio = 0;                    // 撞不动的东西，角色吃全部反弹 (1832)
else if (InteractionType == Kinematic) ratio = 1;     // 运动学模式：角色弹回，箱子不动 (1837)
```

冲量（`1867-1886`）：

```csharp
// 1) 把移动阶段被投影掉的法线速度"还回来"
if (dot(characterVel, normal) < 0) velocityChangeOnCharacter += normal * dot(characterVel, normal);
// 2) 交换法线方向的相对速度（按质量比分配）
Vector3 rel = normal * (bodyVelOnN - charVelOnN);
velocityChangeOnCharacter += rel * (1 - ratio);
velocityChangeOnBody      += -rel * ratio;
```

给箱子的力用 `AddForceAtPosition(..., ForceMode.VelocityChange)`（`1852`）—— **直接改速度**，不是力，所以质量只通过 ratio 影响，不会出现"箱子太重推不动"的物理真实感。

### 三种交互模式（`RigidbodyInteractionType`，`7`）

| 模式 | 角色 | 刚体 | 用途 |
|---|---|---|---|
| `None` | 穿过去 | 不受影响 | 纯 PVE 小怪 |
| `Kinematic` | 完全弹回 | 不动（但会被 WakeUp） | "撞墙感"，最便宜 |
| `SimulatedDynamic` | 质量比交换 | 受力 | 推箱子、多人互撞 |

> `SimulatedDynamic` 额外调用 `HandleSimulatedRigidbodyInteraction`（`1775`，空实现）—— 留给你自定义，比如"角色质量随背包变化"、"冲量按部位衰减"。

### 重要发现：字符-字符互推在本版本是死代码

因为 `StoreRigidbodyHit` 在 `1610` 就把"撞到另一个 KCC"直接 return 掉了，所以 `_internalRigidbodyProjectionHits` 里**永远不会出现另一个角色**。于是 `ProcessVelocityForRigidbodyHits` 里这两段分支永远不执行：

- `hitBodyIsCharacter` 判定（`1800`）与 `hitCharacterMotor.SimulatedCharacterMass` 取值（`1807`）
- `hitCharacterMotor.BaseVelocity += velocityChangeOnCharacter`（`1848`）

**结论**：KCC 的角色间碰撞走的是"运动解算时相互被当作普通碰撞体挡住"，而不是"冲量交换"。玩家之间会互相挡住、能踩在别人头上（如果法线稳定），但不会"撞飞"。

**想要互推怎么做**：删掉 `1610` 的提前 return，并保证每个角色在 `ProcessVelocityForRigidbodyHits` 里收到的冲量能落到**下一个 tick** 的 `BaseVelocity`（否则会因 tick 顺序产生不对称）。

---

# 第 12 集 · 插值与时间步

**目标**：能解释"为什么 KCC 自己写插值，而不是用 Rigidbody.interpolation"。

### 完整帧流水（很重要，能背下来）

```
FixedUpdate #1（可能 0 次/帧，也可能 2 次）
   PreInterp:  变换 = Transient（上帧末位姿）
   Simulate:   Transient 更新到新位姿，并把 Transform 设为 Transient
   PostInterp: Transform 拉回 InitialTick（本 tick 起点）
LateUpdate
   f = (Time.time - tickStartTime) / tickDeltaTime
   Transform = Lerp(InitialTick, Transient, f)
```

关键点：
- `_lastCustomInterpolationStartTime` / `_lastCustomInterpolationDeltaTime`（`17-18`）记录**最后一次物理 tick 的时刻与步长**
- `f` 是 `Clamp01`：物理卡顿时 f 会到 1，角色停在最新位姿（不 extrapolate，避免抖动）
- 一帧两次 FixedUpdate 时：第二次 PreInterp 把 Transform 拉回 Transient（丢弃上一次插值的残影），再模拟。**所以插值不会累积误差**

### 为什么不用 `Rigidbody.interpolation`

1. 角色的 Transform 是"直接写的"，根本没有 Rigidbody 参与积分，Unity 的插值管不到
2. KCC 的 Body 是 kinematic capsule，`Rigidbody.interpolation` 在 kinematic 上行为不稳定且与 `MovePosition` 混用会打架（`PhysicsMover.RegisterPhysicsMover` 直接关掉它，`98`）
3. 自己写可以统一处理"角色 + 平台"的插值，且能拿到 `PositionDeltaFromInterpolation` 做额外补偿

### 时间步与手感

- 物理固定 `Time.fixedDeltaTime`（默认 0.02 = 50Hz）。角色移动在 50Hz 下解算，渲染在 60~144Hz 下插值
- **不要把 `Time.deltaTime` 传给 `Simulate`**。`FixedUpdate` 里用 `Time.deltaTime`（= fixedDeltaTime）是对的，但若你手动调 `Simulate`，应固定步长以保证确定性
- 步长越小 → 解算越精细（穿模更少），但 CPU 更高；步长越大 → 高速角色更容易"撞进"几何体

---

# 第 12 集附 · Controller 精要：KCC 只是"位移积分器"（gameplay 主线）

> 这一集不读源码，只回答一个问题：**抛开 KCC，一个能上线的角色控制器由哪几块组成、按什么顺序工作、参数怎么调。**
> 如果目标是做玩法，**这一集比第 13 集更重要**：第 13 集是"能跑"，这一集是"能改、能调、能搬走"。

## 12附.0 划线：谁负责什么

| 层 | 负责 | 不负责 |
|---|---|---|
| 意图（Input） | 把设备状态变成"想往哪走"、"这一刻想不想跳" | 决定速度多大 |
| 规则（Controller） | 加速、重力、跳跃、状态、朝向 | 几何约束（穿墙、贴地、台阶） |
| 解算（Motor） | 扫掠、投影、吸附、台阶、平台跟随 | 任何玩法判断 |
| 表现（Anim / Camera / VFX） | 读结果、做平滑 | 写逻辑位姿与速度 |

**一句话：Controller 决定"想怎么动"，Motor 决定"实际能怎么动"，表现层只读结果。**

两个最常见的越界，都会让手感变"玄学"：

- **Controller 里自己打射线贴地**：与 Motor 的吸附同时生效 → 双重补偿 → 下坡抖、跳跃高度不稳。
- **表现层里改速度或位置**：下一 tick 会被瞬态位姿覆盖，插值再把差值放大成抖动。

## 12附.1 铁律：一切"想去哪"都翻译成速度

KCC 连"精确移动到某点"都不直接写位置，而是先换成速度再走完整解算（`MoveCharacter` `701` / `GetVelocityForMovePosition` `2206`）。这不是实现细节，而是控制层的设计原则：

**Controller 对外的唯一输出是 `(velocity, rotation)`，不是 `position`。**

三个好处：

1. 所有位移共享同一套碰撞处理（冲刺撞墙会被挡住，而不是穿过去）
2. 外力、平台、击退、根运动可以统一成"速度叠加"，不需要各写一套移动代码
3. 可回放、可回滚：状态就是速度 + 位姿，录像和网络同步都简单

落地做法：任何"我要在 X 秒内到那儿"的需求（翻越、处决位移、被吸过去、梯子锚定）都写成

```csharp
velocity = Motor.GetVelocityForMovePosition(Motor.TransientPosition, targetPoint, dt);
```

## 12附.2 速度合成模型（本集核心，建议抄在纸上）

下面代码沿用第 13.1 节的字段（`_moveInput`、`Gravity`、`JumpUpSpeed`、`Motor` …）。

`UpdateVelocity` 每 tick 只调用一次，你写进去的就是角色的"自身速度"（`BaseVelocity`）。难点不是公式，而是**顺序和所有权**。

| # | 层 | 典型内容 | 写入方式 | 所有权 |
|---|---|---|---|---|
| 1 | 惯性 | 上一 tick 的速度、重力累积、击退衰减 | 读 `currentVelocity` | 你自己 |
| 2 | 位移驱动 | Root Motion、翻越、处决、传送门 | **覆盖** | 最高优先级 |
| 3 | 规则 | 地面加速/减速、空中加速、跳跃 | 地面**覆盖**、空中**累加** | 你自己 |
| 4 | 外力 | 击退、风、爆炸、载具推力 | **增量** | 多个系统可叠加 |
| 5 | 约束 | 落地投影、贴地、台阶、平面约束 | 由 Motor 做 | Motor（你只发信号） |
| 6 | 平台 | 平台带你的位移 | Motor 直接位移 | Motor（**不要手动加**） |
| 7 | 兜底 | 限速、NaN、最小速度 | clamp | 你自己 |

三条规则，能解决 80% 的"手感玄学"：

- **单写入者**：`UpdateVelocity` 里每 tick 只能有一个"最终决定权"的分支，用 early return 表达优先级，不要写成"谁都 `+=` 一脚"。
- **量纲不能混**：地面层写的是"目标速度"（覆盖），空中层写的是"加速度"（累加）。混着写会出现"帧率越高越飘"的隐藏惯性。
- **平台速度不进 `BaseVelocity`**：Phase1 用平台速度单独做了一次移动；`Motor.Velocity` 是只读的 `BaseVelocity + 平台速度`。离开平台时 Motor 会自动把平台速度补进 `BaseVelocity`（`PreserveAttachedRigidbodyMomentum`，`1002`）——**你手动再加一次就是双倍**，表现为"跳上移动平台直接飞出去"。

推荐骨架（可直接替换第 13 集的 `UpdateVelocity`）：

```csharp
public void UpdateVelocity(ref Vector3 velocity, float dt)
{
    // ① 位移驱动：有根运动就接管，本 tick 不再叠加规则
    if (_rootMotionDeltaPos.sqrMagnitude > 0f)
    {
        velocity = Vector3.ClampMagnitude(_rootMotionDeltaPos / dt, MaxRootMotionSpeed);
        _rootMotionDeltaPos = Vector3.zero;
        return;
    }

    // ② 外力（击退）：只衰减、不接受输入，但保留重力
    if (_knockbackTimer > 0f)
    {
        _knockbackTimer -= dt;
        velocity = Vector3.MoveTowards(velocity, Vector3.zero, KnockbackDrag * dt);
        if (!Motor.GroundingStatus.IsStableOnGround)
            velocity += Motor.CharacterUp * (-Gravity * dt);
        return;
    }

    // ③ 规则层：地面覆盖 + 指数收敛；空中累加 + 阻力
    if (Motor.GroundingStatus.IsStableOnGround && !Motor.MustUnground())
    {
        Vector3 target = _moveInput * CurrentMoveSpeed;
        target = Motor.GetDirectionTangentToSurface(target, Motor.GroundingStatus.GroundNormal) * target.magnitude;

        velocity = Vector3.Lerp(velocity, target, 1f - Mathf.Exp(-GroundSharpness * dt));

        if (TryConsumeJump())
        {
            velocity += Motor.CharacterUp * JumpUpSpeed;
            Motor.ForceUnground(0.1f);      // 先脱离，本 tick 的速度才不会被吸附吃掉
        }
    }
    else
    {
        velocity += _moveInput * AirAcceleration * dt;
        velocity += Motor.CharacterUp * (-Gravity * dt);
        velocity *= 1f - Mathf.Clamp01(Drag * dt);
    }

    // ④ 兜底：限速 + NaN 防御（NaN 一旦进 Transform 就永久损坏）
    if (velocity.magnitude > MaxSpeed) velocity = velocity.normalized * MaxSpeed;
    if (float.IsNaN(velocity.x) || float.IsNaN(velocity.y) || float.IsNaN(velocity.z))
        velocity = Vector3.zero;
}
```

注意地面分支**不要**再"保留竖直速度"：贴地时竖直分量要么被吸附吃掉，要么变成贴地弹跳。需要上抛就明确 `ForceUnground`，语义才清楚。

> 为什么这套顺序能防"玄学"：每个 tick 的速度只由**一个分支**决定，其余系统只能通过"外力累加器"或"Motor 的约束"影响结果。出现问题时，你可以按这张表逐层二分，而不是在十几个 `+=` 里猜。

## 12附.3 十个回调：用哪个，比"都实现"重要

接口方法必须全部实现，但**每个回调只有一两种正确用法**。本仓库的调用点：

| 回调 | 时机（`KinematicCharacterMotor.cs`） | 该做的事 | 不该做的事 |
|---|---|---|---|
| `BeforeCharacterUpdate` | Phase1 最开始 `831` | 清缓存、记录上 tick 状态、改胶囊尺寸 | 读输入、改速度 |
| `PostGroundingUpdate` | 探地完成、平台未动 `959` | 落地音效/粒子（此时位姿还会变） | 依赖最终位置 |
| `UpdateRotation` | Phase2 最前 `1067` | 决定朝向 | 期望读到本 tick 新速度 |
| `UpdateVelocity` | Phase2 中段 `1179` | 唯一的规则写入点 | 写 position、打大量查询 |
| `OnGroundHit` | 探地每次命中 `1322` | 改稳定性、记录地面材质 | 改速度 |
| `OnMovementHit` | 移动每次命中 `1532` | 撞墙事件、按材质扣速度、贴花 | 改 `transform` |
| `ProcessHitStabilityReport` | 命中后、判定前 `2020` | 自定义"什么能站"（冰面、单向平台、斜坡例外） | 重计算、分配内存 |
| `OnDiscreteCollisionDetected` | 移动结束后重叠 `1217` | 挤压伤害、卡住兜底 | 假设它每帧都触发 |
| `AfterCharacterUpdate` | 全部结束 `1220` | 读最终速度、派生事件、驱动动画参数 | 写速度/位姿 |
| `IsColliderValidForCollisions` | 每次查询过滤 `1930` | 忽略自己的武器、触发器 | 依赖帧序的状态 |

两个只有读过源码才知道的时序事实：

- **`UpdateRotation` 在 `UpdateVelocity` 之前**（`1067` vs `1179`）。所以"朝速度方向转"在 `UpdateRotation` 里读到的是**上一 tick 的速度**。想用本 tick 速度，就在 `AfterCharacterUpdate` 里缓存 `Motor.BaseVelocity`，下一 tick 再用——50Hz 下 20ms 延迟，通常无感。
- **你写进去的速度，最终会被解算改掉**（投影、台阶、限速、`MinVelocityMagnitude = 0.01` 清零，`1181`）。要"真实速度"（做动画、算伤害、判断是否撞墙），**只在 `AfterCharacterUpdate` 里读**。

## 12附.4 三拍时序：输入、逻辑、表现

| 拍 | 频率 | 只做三件事 | 绝对不能 |
|---|---|---|---|
| `Update` | 可变 | 采样输入、维护时间窗（buffer/coyote）、累加根运动 | 改速度/位置、做 Physics 查询 |
| `FixedUpdate`（Motor 内） | 固定 50Hz | `UpdateVelocity` / `UpdateRotation` | 读 `Input.GetButtonDown` |
| `LateUpdate` | 可变 | 相机、UI、IK 目标 | 读 `TransientPosition` |

核心原则：**输入是"状态"，不是"事件"。**

一帧里可能 0 次或 2 次物理 tick，`GetButtonDown` 这种边沿信号必须在 `Update` 里转成**带时间的计时器**，才能在物理帧里安全消费：

```csharp
// Update：只采样 + 计时，不消费
void Update()
{
    _moveInput = ComputeMoveInputFromCamera();
    _jumpBufferTimer = Input.GetButtonDown("Jump") ? JumpBufferTime : _jumpBufferTimer - Time.deltaTime;
    _coyoteTimer = Motor.GroundingStatus.IsStableOnGround ? CoyoteTime : _coyoteTimer - Time.deltaTime;
}

// UpdateVelocity：消费即清零，避免一次按键触发两次跳
bool TryConsumeJump()
{
    if (_jumpBufferTimer <= 0f || _coyoteTimer <= 0f) return false;
    _jumpBufferTimer = 0f;
    _coyoteTimer = 0f;
    return true;
}
```

同一个"时间窗"模式可复用到：攻击预输入、冲刺缓冲、连招窗口、翻越吸附。**这是 gameplay 里最值得抄走的一个模式。**

## 12附.5 四个旋钮：手感的数学

**① 收敛速度：`1 - exp(-k·dt)`**

`k` 的含义是"每秒收敛多少次"，达到 63% 的时间 = `1/k`：

| k | 63% 时间 | 手感 | 典型用途 |
|---|---|---|---|
| 5 | 0.20s | 滑、重 | 空中转向 |
| 10 | 0.10s | 有惯性 | 地面加速 |
| 15 | 0.067s | 跟手 | 地面减速、转身 |
| 25 | 0.04s | 硬 | 急停、瞄准 |

用 `Lerp(a, b, k * dt)` 是错的：`k * dt > 1` 会过冲，帧率一变手感就变。

**② 跳跃的三段重力**

只用一个 `Gravity`，跳起来像月球。可玩性最好的形状是三段：

```csharp
float g = Gravity;
if (velocity.y > 0f) g *= JumpRiseMultiplier;                 // 0.8~1.0：上升"轻"
else if (velocity.y < -ApexThreshold) g *= FallMultiplier;    // 1.5~2.0：下落"脆"
else g *= ApexMultiplier;                                     // 0.5：顶点悬停（可选）
velocity += Motor.CharacterUp * (-g * dt);
```

再加**松键截断**（可变跳跃高度）：松键且 `vy > 0` 时 `vy *= 0.5`。两者叠加，跳跃才有"重量"。

**③ 容错窗口**

| 参数 | 建议 | 作用 |
|---|---|---|
| Coyote Time | 0.08~0.12s | 刚离开平台还能跳 |
| Jump Buffer | 0.08~0.15s | 落地前按跳，落地即起跳 |
| 转身锁定 | 0.05~0.1s | 攻击/技能期间不转头（视觉稳定） |

本质都是"把离散输入变连续"的低通，和相机 `SmoothDamp` 同类。

**④ 不要在 Controller 里重复补偿**

KCC 已经做了：保长度投影（斜坡不减速）、贴地吸附（下坡不飞）、台阶抬升（不减速）。再在 Controller 里乘 `dot(v, groundNormal)`、加向下力、手动抬高度，就是**双重补偿**：

| 症状 | 常见错误补偿 | 正确做法 |
|---|---|---|
| 斜坡变慢 | 又乘了一次 `dot` | 交给 `GetDirectionTangentToSurface` |
| 下坡弹跳 | 加"贴地力" | 调 `GroundDetectionExtraDistance` |
| 台阶卡顿 | 手动插值高度 | 调 `MaxStepHeight` / `StepHandling` |
| 落地粘滞 | 落地再清一次速度 | Phase1 已做落地位移投影（`942`） |

## 12附.6 八条可迁移原则（换引擎也成立）

| # | 原则 | 在别的方案里长什么样 |
|---|---|---|
| 1 | 位移统一走速度通道 | UE CMC 的 `Velocity` + `SafeMoveUpdatedComponent`；Rigidbody 的 `MovePosition` 也应换算成速度 |
| 2 | 逻辑位姿与渲染位姿分离 | 固定步长 + 插值；网络游戏的"预测位姿 vs 渲染位姿" |
| 3 | 状态可查询，事件靠差分 | 不监听 `OnCollisionEnter`，而是比较本 tick / 上 tick 的 `IsGrounded` |
| 4 | 解算器不含玩法 | Motor 不知道"跳跃"；UE CMC 的 `MovementMode` 在组件层，不在物理层 |
| 5 | 单写入者 | 每 tick 只有一个系统写速度，其它系统只写"外力累加器" |
| 6 | 显式时序 | 平台先动 → 角色解算 → 相机跟随；ECS 用 SystemGroup 表达同一件事 |
| 7 | 预算与上限 | 迭代上限、查询上限、速度上限，保证最坏帧有界 |
| 8 | 默认零 GC + 显式契约 | 回调里不分配；坐标系/缩放/胶囊尺寸启动时校验并报错 |

> 这八条是这份文档里**最值得带走的东西**。KCC 会过时，这八条不会。

## 12附.7 验收：Controller 的 12 项自检

1. 所有移动都通过速度表达，没有一处直接改 `transform`
2. `UpdateVelocity` 只有一个写速度的主分支，优先级用 early return
3. 地面是覆盖、空中是累加，量纲没有混用
4. 所有收敛都用 `1 - exp(-k·dt)`
5. 跳跃前 `ForceUnground`，且 buffer 只消费一次
6. 没有手动加平台速度
7. "真实速度"只在 `AfterCharacterUpdate` 里读
8. 落地/离地/撞墙事件由状态差分产生
9. 回调里没有 `new`、LINQ、字符串拼接
10. 有速度上限和 NaN 兜底（`BaseVelocity` 一旦 NaN，Transform 永久损坏）
11. 蹲伏改胶囊用 `SetCapsuleDimensions`，且在 `BeforeCharacterUpdate` 里改
12. 相机只在 `LateUpdate` 读 `Transform`，不读 `TransientPosition`

## 12附.8 四个练习（做完就算掌握）

1. **重构**：把第 13 集的 Controller 改成 12附.2 的骨架，对比"贴墙走"和"斜坡加速"是否更稳
2. **击退**：加 `Knockback(dir, force)`，验证击退期间输入无效、落地后自动恢复
3. **位移驱动**：用 `GetVelocityForMovePosition` 做 2 米冲刺，确认撞墙会被挡住而不是穿过去
4. **事件**：不用任何碰撞回调，用 `AfterCharacterUpdate` 的差分实现"落地音效 + 落地压缩动画"

---

# 第 13 集 · 实战：从零写一个 Controller（Control 层）

**目标**：这是 3C 里 **Control** 的核心战场。KCC 只给你 "改速度/改朝向" 的插槽，手感全靠这 200 行。

### 13.1 骨架

```csharp
public class MyCharacterController : MonoBehaviour, ICharacterController
{
    public KinematicCharacterMotor Motor;
    [Header("Refs")] public Transform CameraTransform;   // 输入参照系
    [Header("Move")] public float MaxStableMoveSpeed = 10f;
    public float StableMovementSharpness = 15f;          // 地面加速/减速的"锐度"
    public float OrientationSharpness = 15f;             // 转身锐度
    [Header("Air")] public float AirMoveSpeed = 10f;
    public float AirAccelerationSpeed = 5f;
    public float Drag = 0.1f;
    [Header("Jump")] public float JumpUpSpeed = 10f;
    public float Gravity = 30f;
    public float CoyoteTime = 0.1f;
    public float JumpBufferTime = 0.1f;
    [Header("Slope")] public float MaxSlopeSlideSpeed = 20f;

    private Vector3 _moveInput;       // 相机空间的输入
    private bool _jumpRequested;
    private float _coyoteTimer, _jumpBufferTimer;
    private Vector3 _cameraForward, _cameraRight;

    void Awake() {
        Motor.CharacterController = this;      // 关键：注册回调
    }
}
```

> 第一步就要讲：**`Motor.CharacterController = this` 是唯一的绑定方式**。忘了它，Motor 会抛 NullReferenceException（每个 tick 都会调回调）。

### 13.2 输入（Control 的第一层：意图 → 方向）

```csharp
void Update() {
    // 1) 相机为参照系（3C 的 Camera 提供 Control 的坐标系）
    _cameraForward = Vector3.ProjectOnPlane(CameraTransform.forward, Motor.CharacterUp).normalized;
    _cameraRight   = Vector3.Cross(Motor.CharacterUp, _cameraForward).normalized;

    float h = Input.GetAxisRaw("Horizontal");
    float v = Input.GetAxisRaw("Vertical");
    _moveInput = (_cameraRight * h + _cameraForward * v);

    // 2) 跳跃缓冲 + 土狼时间（手感的灵魂）
    _jumpBufferTimer = Input.GetButtonDown("Jump") ? JumpBufferTime : _jumpBufferTimer - Time.deltaTime;
    _coyoteTimer = Motor.GroundingStatus.IsStableOnGround ? CoyoteTime : _coyoteTimer - Time.deltaTime;
}
```

> 要点：**输入在 `Update` 读，在 `UpdateVelocity`（FixedUpdate）用**。不要在 FixedUpdate 里读 `Input.GetKeyDown` —— 会丢帧（一帧多次物理 tick 或零次）。

### 13.3 UpdateVelocity（Control 的第二层：意图 → 速度）

```csharp
public void UpdateVelocity(ref Vector3 currentVelocity, float deltaTime) {
    // ---- 地面：覆盖式目标速度 + 指数收敛 ----
    if (Motor.GroundingStatus.IsStableOnGround && !Motor.MustUnground()) {
        Vector3 targetVel = _moveInput * MaxStableMoveSpeed;
        // 投影到坡面，并"保长度"：斜坡上不减速的关键
        targetVel = Motor.GetDirectionTangentToSurface(targetVel, Motor.GroundingStatus.GroundNormal) * targetVel.magnitude;

        // 指数收敛：与帧率无关，且 Sharpness 语义直觉
        currentVelocity = Vector3.Lerp(currentVelocity, targetVel, 1f - Mathf.Exp(-StableMovementSharpness * deltaTime));

        if (_jumpBufferTimer > 0f && _coyoteTimer > 0f) {
            currentVelocity += Motor.CharacterUp * JumpUpSpeed;
            _jumpBufferTimer = 0f; _coyoteTimer = 0f;
            Motor.ForceUnground(0.1f);      // ← 必须有，否则起跳瞬间被吸附回地面
        }
    }
    // ---- 空中：有限加速 + 阻力 ----
    else {
        Vector3 targetVel = _moveInput * AirMoveSpeed;
        currentVelocity += targetVel * AirAccelerationSpeed * deltaTime;
        currentVelocity += Motor.CharacterUp * (-Gravity * deltaTime);   // 重力只在空中加！
        currentVelocity *= (1f - Drag * deltaTime);                       // 一阶阻力
    }
}
```

**四个"必须"：**

1. **`1 - Mathf.Exp(-k * dt)` 而不是 `k * dt`**：帧率无关的指数收敛，`Sharpness` 大 = 更"硬"更跟手，小 = 更"滑"更重。这是 3C 调参的核心公式。
2. **重力只在空中加**：贴地时加重力会被吸附逻辑吃掉，却会在斜坡上产生"下坡加速"；想要这种效果可以保留（很多游戏要），但要知道这是你自己造的，不是 KCC 给的。
3. **跳跃必须 `ForceUnground`**（`1358`）：否则本 tick 的地面吸附会把刚起跳的角色拉回地面，表现为"跳不起来 / 跳跃高度随机"。
4. **地面用覆盖、空中用累加，别混**：地面分支写的是"目标速度"（量纲是 m/s），空中分支写的是"加速度"（m/s²）。同一个函数里两种写法是正常的，但**同一分支里不能既 `+=` 又 `=`**，否则先加的会被后写的覆盖（新手最常犯的错）。
5. **不要手动保留竖直速度**：贴地时竖直分量只会被吸附吃掉或造成贴地弹跳。真需要上抛，用 `ForceUnground` 明确表达意图。
6. **写成流水线，而不是一个大 if**：优先级的正确顺序见 **12附.2**（位移驱动 → 外力 → 规则 → 兜底）。

### 13.4 UpdateRotation（Control 的第三层：朝向）

```csharp
public void UpdateRotation(ref Quaternion currentRotation, float deltaTime) {
    Vector3 forward = _moveInput.sqrMagnitude > 0.01f ? _moveInput.normalized
                     : Vector3.ProjectOnPlane(currentRotation * Vector3.forward, Motor.CharacterUp);
    if (forward.sqrMagnitude < 0.001f) return;
    Quaternion target = Quaternion.LookRotation(forward, Motor.CharacterUp);
    currentRotation = Quaternion.Slerp(currentRotation, target, 1f - Mathf.Exp(-OrientationSharpness * deltaTime));
}
```

> 注意：**Motor 旋转的是胶囊体**。若你的模型挂在子物体上做"视觉转身"，可以在 Motor 上只转"移动朝向"，模型单独转；反之若用 Root Motion / Animator 驱动朝向，就别在这里转（否则打架）。
> 注意：**逻辑朝向 vs 视觉朝向**要分离。逻辑朝向决定碰撞与移动（必须平滑、可回滚），视觉朝向可以滞后、可以带过冲（弹簧）。

### 13.5 其余回调（必须实现，但不必都写逻辑）

接口方法**全部实现**是编译要求，不是设计要求。生产代码的做法：抽一个 `CharacterControllerBase : MonoBehaviour, ICharacterController` 把 10 个方法写成 `virtual` 空实现，业务类只重写需要的 3~5 个。**每个回调的语义、时机和禁忌见 12附.3 的矩阵**，这里给的是"不写会出事"的三个：

```csharp
public void BeforeCharacterUpdate(float dt) { /* 清空本帧缓存、记录上帧状态 */ }
public void PostGroundingUpdate(float dt) { /* 落地特效、脚步声、动画参数 */ }
public void AfterCharacterUpdate(float dt) {
    // 这里处理"落地/离地"事件（最可靠的时机：一 tick 结束，状态已确定）
    if (Motor.GroundingStatus.IsStableOnGround && !Motor.LastGroundingStatus.IsStableOnGround) OnLanded();
}
public bool IsColliderValidForCollisions(Collider coll) { return true; }
public void OnGroundHit(Collider c, Vector3 n, Vector3 p, ref HitStabilityReport r) { }
public void OnMovementHit(Collider c, Vector3 n, Vector3 p, ref HitStabilityReport r) { }
public void ProcessHitStabilityReport(Collider c, Vector3 n, Vector3 p, Vector3 pos, Quaternion rot, ref HitStabilityReport r) { }
public void OnDiscreteCollisionDetected(Collider c) { }
```

**三个容易写错的地方：**

- **落地/离地不要用"当前状态"判断**，要用差分：`Motor.GroundingStatus.IsStableOnGround` 与 `Motor.LastGroundingStatus.IsStableOnGround` 比较。前者是本 tick 结果，后者是上 tick 结果，二者都在 `AfterCharacterUpdate` 里已经确定。
- **`PostGroundingUpdate` 只适合"探地刚完成"的表现**（脚步材质、落地粒子），因为此时平台还没移动、角色还可能被推挤；需要最终位姿的表现放到 `AfterCharacterUpdate`。
- **`OnMovementHit` 会被调用多次**（每次扫掠命中一次，最多 6 次）。在那里扣速度、放特效都要做"每 tick 只处理第一次"的保护，否则墙角会触发 5 次伤害。

### 13.6 手感清单（按需求查）

| 需求 | 实现位置 | 做法 |
|---|---|---|
| 土狼时间 | `Update` 计时 + `UpdateVelocity` 判定 | 离地 0.1s 内仍可跳 |
| 跳跃缓冲 | 同上 | 落地前 0.1s 按键，落地即跳 |
| 可变跳跃高度 | `UpdateVelocity` | 松键时若 `vy > 0` → `vy *= 0.5` |
| 走/跑切换 | 输入层 | 改 `MaxStableMoveSpeed`，用同一个 Sharpness |
| 斜坡不减速 | 已有（KCC 保长度投影） | 不要额外乘 `dot(vel, groundNormal)` |
| 陡坡滑落 | `OnGroundHit` + `ProcessHitStabilityReport` | 判定 `!IsStable` 时把 `IsStable` 保持 false，让重力自然带走；或用 `HandleVelocityProjection` override 加下滑分量 |
| 台阶不减速 | 默认行为 | 想要减速就在 `OnMovementHit` 里扣速度 |
| 空中转向 | `UpdateVelocity` 空中分支 | 限制 `AirAccelerationSpeed`，或只在"起跳后 X 秒内"允许大转向 |
| 落地缓冲动画 | `AfterCharacterUpdate` | 缓存"上一 tick 空中时"的 `Motor.BaseVelocity.y`，在落地差分帧消费（落地瞬间竖直速度已被 Phase1 投影清零，直接读会是 0） |
| 推箱子手感 | Motor 的 `SimulatedCharacterMass` | 角色质量调大 → 推得更猛 |

---

# 第 13 集附 · 状态机化 Controller 与 Root Motion

**目标**：官方示例不是"一个大 if"，而是"状态类"。这是从"能跑"到"能维护"的分水岭。

### 一、为什么要把 Controller 拆成状态

官方 Walkthrough 里的示例角色，把行为拆成了若干状态类（默认移动、**梯子攀爬**、锚定/脱离锚定、蹲伏……）。核心原因是：**同一个回调在不同玩法下的语义完全不同**。

| 玩法定例 | UpdateVelocity 该做什么 | 用 if 写会怎样 |
|---|---|---|
| 普通移动 | 加速度 + 重力 | 还好 |
| 攀爬梯子 | 取消重力，锁定梯子轴向速度 | 变成 `if (climbing) {...} else if (crouching)...` |
| 被击飞 | 忽略输入，只受外力 | 分支嵌套 + 状态互相污染 |
| 载具驾驶 | 完全交给载具 | 分支爆炸 |

**状态机骨架（可直接抄）**

```csharp
public interface ICharacterState {
    void OnEnter(MyCharacterController c);
    void OnExit(MyCharacterController c);
    void UpdateVelocity(MyCharacterController c, ref Vector3 v, float dt);
    void UpdateRotation(MyCharacterController c, ref Quaternion r, float dt);
    void AfterUpdate(MyCharacterController c, float dt);
}

public class MyCharacterController : MonoBehaviour, ICharacterController {
    private ICharacterState _state;
    public void SetState(ICharacterState next) {
        _state?.OnExit(this);
        _state = next;
        _state?.OnEnter(this);
    }
    public void UpdateVelocity(ref Vector3 v, float dt) => _state.UpdateVelocity(this, ref v, dt);
    public void UpdateRotation(ref Quaternion r, float dt) => _state.UpdateRotation(this, ref r, dt);
    public void AfterCharacterUpdate(float dt) => _state.AfterUpdate(this, dt);
}
```

**攀爬状态的实现要点（官方示例的核心技巧）**

```csharp
// 锚定阶段：把"我要去梯子上的某个点"翻译成速度
if (anchoring)
    velocity = Motor.GetVelocityForMovePosition(Motor.TransientPosition, targetPoint, dt); // 701/2206
// 攀爬阶段：取消重力，速度只沿梯子轴向
else
    velocity = ladderUp * climbSpeed * inputY;   // 且不加重力
// 旋转：直接对齐梯子朝向（不受平滑影响）
rotation = Quaternion.LookRotation(-ladderNormal, Motor.CharacterUp);
```

> 关键点：**`GetVelocityForMovePosition` 是"用速度接口做位移插值"的官方推荐姿势**（`KinematicCharacterMotor.cs:2206`，`MoveCharacter` 内部也是这么做的 `846`）。它把"我要精确到达某点"转换为"这一 tick 该有的速度"，从而复用整套碰撞解算。爬梯、翻越、处决动画位移、传送门过渡都用它。

### 二、Root Motion 与 FixedUpdate 的帧率矛盾

**矛盾**：Animator 在 `Update` 里按**可变帧率**产出 root motion 位移；KCC 在 `FixedUpdate` 里按**固定 50Hz** 消费。两者不同步 —— 一帧可能 0 次物理 tick，也可能 2 次。

**标准解法：累加 + 消费**（官方 Walkthrough 的做法）

```csharp
private Vector3 _rootMotionDeltaPos;
private Quaternion _rootMotionDeltaRot = Quaternion.identity;

// 1) 动画帧：只累加，不使用
void OnAnimatorMove() {
    _rootMotionDeltaPos += Animator.deltaPosition;
    _rootMotionDeltaRot = Animator.deltaRotation * _rootMotionDeltaRot;
}

// 2) 物理帧：消费并清零（在 UpdateVelocity 里）
public void UpdateVelocity(ref Vector3 velocity, float dt) {
    if (applyRootMotion) {
        velocity = _rootMotionDeltaPos / dt;          // 位移 → 速度
        _rootMotionDeltaPos = Vector3.zero;
    }
    // 重力、加速...
}
public void UpdateRotation(ref Quaternion rot, float dt) {
    if (applyRootMotion) { rot = _rootMotionDeltaRot * rot; _rootMotionDeltaRot = Quaternion.identity; }
}
```

**三个必踩的坑**

1. **`Animator.applyRootMotion` 必须关掉**（`false`），否则 Unity 会自己把角色 Transform 挪走，和 KCC 打架
2. **`Animator.updateMode`**：默认 `Normal`（Update 驱动）。改成 `AnimatePhysics` 会让动画在物理帧更新，能减少一层累加，但**动画会变卡**（50Hz 采样），一般不用
3. **清零时机**：必须在 `UpdateVelocity` 里清零；写在 `LateUpdate` 会丢掉一帧位移；写在 `OnAnimatorMove` 会丢帧率差

> 一句话总结："Root Motion 的本质是**用动画曲线驱动位移**，它和物理 tick 的采样率不一致，所以要做'生产者-消费者'的累加缓冲。这和网络同步里的输入缓冲是同一个模式。"

### 三、蹲伏（改胶囊尺寸）的正确姿势

```csharp
// 蹲下：先缩碰撞体，再缩视觉
Motor.SetCapsuleDimensions(0.4f, 1.0f, 0.5f);   // 758
// 站起：必须先检测"站立尺寸"的胶囊有没有被挡住，否则会顶穿天花板
public bool CanStandUp() {
    const float r = 0.5f, h = 2f,  yOff = 1f;               // 站立尺寸
    Vector3 center = Motor.TransientPosition + Motor.TransientRotation * new Vector3(0f, yOff, 0f);
    Vector3 bottom = center - Motor.CharacterUp * (h * 0.5f - r);
    Vector3 top    = center + Motor.CharacterUp * (h * 0.5f - r);
    // 用"站起来的胶囊"单独做一次检测，不污染 Motor 的缓存几何
    return !Physics.CheckCapsule(bottom, top, r, ~0, QueryTriggerInteraction.Ignore);
}
```

**注意**：`SetCapsuleDimensions` 会重算 5 个缓存偏移，**必须在同一 tick 内完成"改尺寸 → 重新探地"**，否则本 tick 的地面判定用的是旧几何。所以官方做法是：在 `BeforeCharacterUpdate` 里改尺寸，Phase1 的地面探测自然用新尺寸。

---

# 第 14 集 · 相机（Camera）与 3C 闭环

**目标**：把 Camera 接进 Control 的坐标系，做出"跟手但不晕"的第三人称。

### 三个必须做对的事

**① 时序：相机在 `LateUpdate`，且读 Transform（已插值）**

```csharp
void LateUpdate() {
    // 目标位置 = 角色 Transform（已被 KCC 插值到渲染时刻）
    Vector3 focus = target.position + Vector3.up * heightOffset;
    // 轨道：yaw/pitch 由鼠标驱动，pitch 必须 clamp
    pitch = Mathf.Clamp(pitch, minPitch, maxPitch);
    Quaternion rot = Quaternion.Euler(pitch, yaw, 0f);
    Vector3 desired = focus - rot * Vector3.forward * distance;

    // 相机碰撞：用 SphereCast 从 focus 到 desired
    if (Physics.SphereCast(focus, camRadius, (desired - focus).normalized, out var hit, distance, mask))
        desired = focus + (desired - focus).normalized * (hit.distance - camRadius);

    transform.position = Vector3.SmoothDamp(transform.position, desired, ref _vel, smoothTime);
    transform.rotation = rot;
}
```

> 绝对不要在 `FixedUpdate` 里移动相机 —— 50Hz 的相机在 144Hz 屏幕上必然抖。也不要读 `Motor.TransientPosition`（那是逻辑位姿，会让相机也抖）。

**② 输入参照系：相机是 Control 的"世界坐标系"**

`UpdateVelocity` 里的 `_moveInput` 必须由**相机 forward/right 投影到地面**得到（`13.2`）。这带来两个经典问题：

- **相机俯视时 forward 接近竖直** → 投影后长度趋近 0 → 方向抖动。修法：用 `Pitch` 计算"水平 forward"（只取 yaw），或对投影结果做 `sqrMagnitude < 0.001` 保护
- **相机瞬移（切镜头）时角色方向突变** → 对 `_cameraForward` 做插值，或让角色朝向"是世界绝对方向"，相机只旋转视角

**③ 插值三段论（防晕三件套）**

| 层 | 频率 | 手段 |
|---|---|---|
| 角色逻辑 | FixedUpdate 50Hz | Transient 位姿 |
| 角色渲染 | LateUpdate | KCC 的 Lerp/Slerp 插值 |
| 相机 | LateUpdate（在角色之后） | SmoothDamp + 位置跟随 |

> 注意：**为什么相机要 SmoothDamp 而不是直接跟随？** 因为逻辑位移是离散的（每 tick 一步），直接跟随会把"台阶传送"和"平台位移"原样传给相机 → 画面弹跳。SmoothDamp 是低通滤波，把阶跃变成缓坡。
> 但**相机碰撞检测不能用平滑后的位置**（会穿墙），要用"当前实际相机位置"去 cast。

### 3C 闭环总结（口播收尾）

```
输入(Control) → 意图方向(相机空间) → UpdateVelocity/UpdateRotation(Control)
    → KCC 解算(Character) → Transient 位姿 → 插值 → Transform(表现)
    → 相机读 Transform 并平滑(Camera) → 新视角 → 影响下一帧输入方向
```

> 这就是 3C 的闭环：**相机改变参照系，参照系改变输入，输入改变运动，运动改变相机**。任何一环延迟或抖动，玩家都会觉得"不跟手"。

---

# 第 15 集 · 性能 / 多角色 / 网络与确定性

### 15.1 每 tick 的查询预算（一个角色）

| 阶段 | 查询 | 次数（典型） |
|---|---|---|
| Phase1 重叠解算 | `OverlapCapsuleNonAlloc` + `ComputePenetration` | 1 + 命中数 |
| Phase1 地面 | `CapsuleCastNonAlloc` | 1~3 |
| 每次命中判定 | 2 条 Raycast（ledge 内外） | 2/命中 |
| Phase2 移动 | `CapsuleCastNonAlloc` | 1~6 |
| 台阶 | 每次 1~2 次 CapsuleCast + 2~3 Raycast | 按需 |

**全是 NonAlloc + 预分配数组**（`506-509`）：`MaxHitsBudget = 16`、`MaxCollisionBudget = 16`。零 GC 是 KCC 的硬指标 —— 值得强调"我在几百个角色下也没有 GC spike"。

### 15.2 已知上限与坑

- **16 命中预算**：密集场景（草丛碰撞体、碎块）会把最近命中挤出数组 → 表现为"随机穿模"。修法：精简碰撞体、提高 budget、或用 `LayerMask` 排除杂物
- **静态 List 遍历**：回调里 `Destroy` 强制刷新 → `InvalidOperationException`。修法：延迟销毁
- **静态 List 的顺序即模拟顺序**：想保证确定性（联机/回放），必须保证注册顺序一致
- **多角色互相推挤**：见第 11 集的 `BaseVelocity` 时序问题

### 15.3 网络与回滚

KCC 天生适合帧同步/回滚，因为：

- 状态可完整取出：`GetState()` → `KinematicCharacterMotorState`（`719`）
- 可瞬时恢复：`ApplyState(state, bypassInterpolation)`（`741`）
- 完全确定性（不依赖 Unity 物理积分），只要 **浮点/顺序/步长**一致

```csharp
// 回滚三件套
var snapshot = motor.GetState();
// ... 回滚若干帧，重放输入 ...
motor.ApplyState(snapshot, bypassInterpolation: true);
```

**注意**：`GetState` 不含 `_movePositionDirty` / 动画状态 / 你的 Controller 字段。**你的 Controller 也必须可序列化**（输入缓冲、跳跃计时、朝向），否则回滚后手感错乱 —— 这是"引擎状态 vs 游戏状态"的分界线，回滚的关键。

---

# 第 16 集 · 排错手册

| 症状 | 根因 | 修法 |
|---|---|---|
| 角色原地抖 | 稳定/不稳定来回翻转（坡角接近 `MaxStableSlopeAngle`） | 调 `MaxStableSlopeAngle` 留 5~10° 余量；检查 `GroundDetectionExtraDistance` |
| 下坡"蹦蹦跳跳" | 吸附距离不足 / `StableGroundLayers` 不含地面 | 调大吸附（`StepHandling` 开启时自动用 `max(radius, MaxStepHeight)`）；检查 LayerMask |
| 跳跃高度随机 | 跳跃时没 `ForceUnground` / 落地速度未投影 | 补 `ForceUnground(0.1f)`；确认 `942-947` 分支被执行（`LastGroundingStatus` 正确） |
| 落地后移动"粘" | 落地下压速度未清零 | 同上；或在 `UpdateVelocity` 地面分支里显式 `vy = 0` |
| 上台阶抽搐 | `MinRequiredStepDepth` 过大 / 台阶碰撞体有缝 | `ValidateData` 会 clamp 到 `CapsuleRadius`；合并碰撞体；调 `MaxStepHeight` |
| 卡在墙角不动 | crease 误判 / 迭代预算耗尽 | 检查 `MaxMovementIterations`；确认 `LedgeAndDenivelationHandling` 没引入假 ledge |
| 站在平台上抖动 | 平台用了 `Rigidbody.interpolation` / `MoveWithPhysics` 配错 / 角速度回绕 | 见第 10 集；修 `eulerAngles` |
| 平台急停角色被甩飞 | 打滑检测阈值太敏感 | `RigidbodySlipVelocity` 调大；或 `PreserveAttachedRigidbodyMomentum = false` |
| 角色被挤进墙里 | `MaxDecollisionIterations = 1` 不够 | 调到 3~5；`CheckMovementInitialOverlaps = true` 保持开启 |
| 相机抖动 | 在 FixedUpdate 移动相机 / 读 TransientPosition | 改到 LateUpdate 读 `transform.position` |
| 完全不动 / NullReferenceException | 忘了 `Motor.CharacterController = this` | 在 `Awake` 里赋值 |
| 缩放后体积不对 | Transform 有缩放 | 保持 `localScale = 1`，改 `SetCapsuleDimensions` |
| 100 个角色掉帧 | 每个都在做 ledge 射线 + 台阶 + 16 命中扫掠 | 分频 tick、关特效项、简化 Layer |
| 高速上坡“一飞冲天” | 稳定命中把速度重定向到坡面切线，速度过大时切线朝上 | 限制最大速度 / 收紧 MaxStableSlopeAngle / override HandleVelocityProjection 加位移上限 |

### 三问定位法

1. **它是逻辑问题还是表现问题？** 看 `TransientPosition` 是否正常 → 正常则是插值/相机/动画。
2. **它是"检测错"还是"解算错"？** 打日志 `GroundingStatus` / `HitStabilityReport` → 检测错就调参数，解算错就调迭代/投影。
3. **它是单帧还是累积？** 单帧抖动 → 阈值；累积漂移 → 没复位（`InitialTick` / 状态没清）。

---

# 附录 A · 常量表（`KinematicCharacterMotor.cs:549-563`）

| 常量 | 值 | 作用 |
|---|---|---|
| `MaxHitsBudget` | 16 | CapsuleCast/Raycast 命中缓冲（官方 3.x 是 32） |
| `MaxCollisionBudget` | 16 | Overlap 缓冲（官方 3.x 是 32） |
| `MaxGroundingSweepIterations` | 2 | 地面探测额外迭代（最多 3 次扫掠） |
| `MaxRigidbodyOverlapsCount` | 16 | 刚体命中记账上限 |
| `CollisionOffset` | 0.01 | 皮肤厚度 |
| `GroundProbeReboundDistance` | 0.02 | 地面探测回弹距离 |
| `MinimumGroundProbingDistance` | 0.005 | 最小探测距离（离地时） |
| `GroundProbingBackstepDistance` | 0.1 | 地面扫掠起点后撤 |
| `SweepProbingBackstepDistance` | 0.002 | 移动扫掠起点后撤 |
| `SecondaryProbesVertical` | 0.02 | 边缘检测抬高 |
| `SecondaryProbesHorizontal` | 0.001 | 边缘检测横向偏移 |
| `MinVelocityMagnitude` | 0.01 | 速度小于此值归零 |
| `SteppingForwardDistance` | 0.03 | 台阶检测前探距离 |
| `MinDistanceForLedge` | 0.05 | 不启用台阶时的边缘检测高度 |
| `CorrelationForVerticalObstruction` | 0.01 | 判定"竖直障碍"的阈值 |

---

# 附录 C · 回调调用顺序（背下来）

```
Phase1: BeforeCharacterUpdate
        → [内部：MoveCharacter / 重叠 / 地面 / 平台跟随]
        → PostGroundingUpdate
Phase2: UpdateRotation
        → [内部：moveRotation / 重叠 / 压扁判定]
        → UpdateVelocity
        → [内部：移动解算]
            ├─ OnMovementHit（每次扫掠命中）
            ├─ OnGroundHit（地面探测命中时，在 Phase1 内）
            ├─ ProcessHitStabilityReport（每次稳定性判定后）
            └─ IsColliderValidForCollisions（每次查询过滤）
        → OnDiscreteCollisionDetected（可选）
        → AfterCharacterUpdate
```

> 注意：`OnGroundHit` 发生在 **Phase1**，比 `UpdateVelocity` 早！所以你不能在 `OnGroundHit` 里读"本帧最终速度"。

---

# 附录 D · 术语表

| 术语 | 含义 |
|---|---|
| Transient | 本 tick 解算出的权威位姿/速度 |
| Sweep | 用体积扫掠检测碰撞（连续检测） |
| Snap | 地面吸附（把角色拉到地面） |
| Stable | 法线在可站立角内（且通过 ledge/台阶检查） |
| Obstruction Normal | 用于投影的"有效阻挡法线"（可能被地面重算过） |
| Crease | 两平面交线，角色沿其滑行 |
| Attached Rigidbody | 当前站立的可跟随刚体（平台） |
| Decollision | 把角色从重叠中推出来 |
| Skin/Offset | 0.01 的安全缝 |
| Denivelation | 内外法线夹角过大导致的"弹射"现象 |

---

# 附录 E · 学习路线

| 天 | 内容 | 产出 |
|---|---|---|
| G1 | 12 附.0~12 附.2 + 13.1~13.4 | 画出"速度合成表"，写出可跑的 Controller |
| G2 | 12 附.3~12 附.4 + 第 5、6 集 | 落地/离地/撞墙事件改为状态差分 |
| G3 | 12 附.5 + 13 集附 | 调出满意的移动/跳跃手感，参数进配置 |
| G4 | 第 14 集 + 13.6 | 第三人称相机闭环，跟手不晕 |
| G5 | 12 附.6~12 附.8 + 第 16 集 | 完成 4 个练习，写一份"可迁移原则"笔记 |

> 不需要读 InternalCharacterMove。只要记住它的三个副作用：投影你的速度、预算耗尽时清零、平台速度加到位移上。

---

# 附录 F · 方案对比表

| 维度 | Dynamic Rigidbody | Unity CharacterController | **KCC（本仓库）** | OpenKCC | DOTS KCC |
|---|---|---|---|---|---|
| 移动方式 | 力/速度积分 | `Move()`（C++ 黑盒） | 直接写位姿 + 扫掠解算 | 同左（`MovePlayer`） | ECS + 物理查询 |
| 命中信息 | 碰撞回调 | 只有 `CollisionFlags` | 法线/点/稳定性/台阶/边缘**全都有** | 近似（`KCCGroundedState`） | 有（`TrackedTransform`） |
| 斜坡 | 摩擦模型决定 | 内置（不可调） | 可调角/可滑/可吸附 | 可调（`maxWalkAngle`） | 可调 |
| 台阶 | 靠碰撞体斜角 | 内置 | 算法级（Standard/Extra） | 两段式 Snap-Up | 有 |
| 移动平台 | 物理约束 | 需手写 | 一等公民（两阶段） | `IMovingGround` | `TrackedTransform` |
| 性能 | 中（物理求解） | 高 | 高（NonAlloc、固定预算） | 高（可换 ColliderCast） | 极高（Burst） |
| 确定性/回滚 | 差 | 差 | **好**（`GetState/ApplyState`） | 好（有 Netcode 实现） | 好（原生支持） |
| 扩展性 | 低（改物理参数） | 无 | **高**（10 个回调 + 3 个 virtual） | 高（全接口化） | 中（要写 System） |
| 适用 | 布娃娃/物理谜题 | 快速原型 | 动作游戏主角 | 需要测试/网络的团队 | 大规模同屏 |

> 一句话回答："**要真实物理选 Rigidbody；要快速出原型用 CharacterController；要手感、要网络、要可控，选 KCC 这类 kinematic 方案。**"

---

# 附录 G · 常见误区 FAQ（12 条，全部来自实战）

1. **"我把 `transform.position` 直接改了，为什么角色瞬移后卡住？"**
   → 必须用 `Motor.SetPosition()`（`652`）或 `Motor.MoveCharacter()`（`701`）。直接改 Transform，`_transientPosition` 没同步，下一 tick 会被拉回原地，且重叠没解算 → 卡在墙里。

2. **"为什么我加的竖直速度立刻消失了？"**
   → 站在地上时地面吸附会把你拉回去。上抬前必须 `ForceUnground()`（`1358`）。这是官方手册明确写的第一条注意事项。

3. **"为什么我每帧 `+= gravity` 之后角色越跑越快？"**
   → `BaseVelocity` 是持久字段。地面分支里要**赋目标速度**（覆盖），而不是累加；空中才累加。想不清就记住：**每 tick 只写一次最终速度**。

4. **"角色在平地上也有微小抖动"**
   → 检查 ① `transform` 是否有缩放；② 是否同时开了 Rigidbody 插值和 KCC 插值；③ 地面碰撞体是否和角色 capsule 初始就重叠（`CheckMovementInitialOverlaps`）；④ 坡角是否贴着 `MaxStableSlopeAngle`（临界角会让"稳定/不稳定"每 tick 翻转，表现为高频抖动）。

5. **"为什么角色能在 45° 斜坡上站着，但 61° 就滑？"**
   → `MaxStableSlopeAngle` 默认 60。注意：**超过阈值不会自动滑**，只是"判定为不稳定"，滑不滑取决于你在 `UpdateVelocity` 里是否还施加了重力沿坡分量。

6. **"改了 `Capsule` 的 radius 但角色碰撞没变"**
   → 必须用 `SetCapsuleDimensions`（`758`），它会同步 `Capsule` 并重算缓存偏移；直接改 `Capsule.radius` 会让 `CharacterCollisionsSweep` 里的半球球心算错。

7. **"为什么我的角色会被平台一点点挤进墙里？"**
   → `MaxDecollisionIterations` 默认只有 1。平台推 + 墙挤压同时发生时，一次解算不够 → 调 3~5；并确认 `Rigidbody.maxDepenetrationVelocity` 为 Infinity（`PhysicsMover.cs:149`）。

8. **"角色踩在别的角色头上会抖"**
   → 角色之间不进刚体记账（`1610`），只能靠"碰撞体互撞"；两个 kinematic 胶囊互相视为静态几何，稳定性判定会来回翻转。要么禁止叠人，要么给角色加一个专门的"可站立"层并处理。

9. **"物理查询偶尔返回空，角色穿地"**
   → 关闭 `Physics.autoSyncTransforms` 时，Transform 改动不会立刻反映到物理世界。KCC 自己在合适时机同步，但**你在回调里做的查询**可能读到旧位姿 —— 手动 `Physics.SyncTransforms()`，或干脆别关。

10. **"怎么做'被击退'？"**
    → `ForceUnground()` + 直接赋 `BaseVelocity`（不要用 `AddForce`，这里没有刚体）。若需要"击退期间不受输入控制"，切换到击飞状态（第 13 集附）。

11. **"为什么我的动画根运动让角色瞬移？"**
    → `Animator.applyRootMotion` 没关，Unity 直接改了 Transform，KCC 下一个 tick 又把它拉回 Transient → 视觉上"抽搐/瞬移"。见第 13 集附。

12. **"KCC 能用于 2D 吗？"**
    → 不能直接用。`CapsuleCast` / `CharacterUp` 都是 3D 假设；2D 需要换成 `CapsuleCast`(2D) + `Rigidbody2D`，本质要重写查询层（但"投影 + 迭代"的思想可以照搬）。

### 玩法向 FAQ（对应 12 集附：手感与状态的 7 个高频问题）

> 以下编号独立，便于单独引用。

1. **"角色像在冰上，松手滑很远"**
    → 地面分支写成了累加（`velocity += ...`）而不是覆盖；或收敛用了 `Lerp(a, b, k * dt)` 且 k 太小。地面必须是"目标速度 + `1 - exp(-k·dt)`"，k 取 10~15；只有空中才允许累加。

2. **"角色像磁铁，贴着斜坡和墙不放"**
    → 双重补偿：Controller 里又自己贴了一次地，Motor 也在吸附；或 `GroundDetectionExtraDistance` 给太大。另外关掉 `LedgeAndDenivelationHandling` 会让"脚一半悬空"也判成可站。

3. **"上坡变慢，平路正常"**
    → 在 Controller 里又乘了一次 `Vector3.Dot(velocity, groundNormal)`。`GetDirectionTangentToSurface` 已经"保长度"投影，再投影一次等于乘 `cos(坡角)`。

4. **"跳上移动平台被甩飞"**
    → 手动把 `Motor.AttachedRigidbodyVelocity` 加进了 `BaseVelocity`。Motor 在脱离平台时自动做动量守恒（`1002-1005`），你再加一次就是双倍。

5. **"攻击/技能期间角色还在转身"**
    → 朝向也要状态化：在 `UpdateRotation` 里按状态直接 `return`（不动 `currentRotation`）。不要指望 Animator 帮你锁朝向——它和 Motor 抢的是同一个旋转通道，见 13 集附的 Root Motion 部分。

6. **"落地那帧读 `Motor.Velocity.y` 怎么是 0？"**
    → 落地瞬间 Phase1 已经做了"落地位移投影"（`942-947`），把竖直速度清零并转到坡面。要算落地伤害/落地特效，缓存**上一 tick 空中时**的 `Motor.BaseVelocity`，在落地差分帧消费。

7. **"为什么我把重力写进地面分支，角色反而更飘？"**
    → 地面分支是覆盖式写入，`+重力` 会被后面的 `=` 目标速度覆盖；真正生效的是"目标速度的竖直分量"，通常为 0。坡上想要下滑就显式加"沿坡分量"，并且用 `IsStableOnGround` 判断是否该滑（陡坡判定为不稳定时会自动走空中分支）。

---

# 附录 H · 实验课（改一个参数，看一个结论）

每个实验都能在 10 分钟内做完，且**肉眼可见地验证源码结论**。

| # | 实验 | 操作 | 验证的源码结论 |
|---|---|---|---|
| 1 | 关掉吸附 | 把 `MaxStableSlopeAngle` 调 0 | 角色在平地也"跳着走"→ 理解稳定判定 |
| 2 | 下坡不飞 | 做 30° 斜坡跑下去 | `GroundDetectionExtraDistance = 0` 时开始"蹦"；加 0.5 后贴地 |
| 3 | 跳不起来 | 注释掉 `ForceUnground` | 跳跃高度为 0 或随机 → 吸附的威力 |
| 4 | 台阶临界 | 做 0.5 / 0.6 两个台阶 | `MaxStepHeight = 0.5` 的硬边界；看场景里的黄色圆圈（`KinematicCharacterMotorEditor.cs:19`） |
| 5 | 迭代预算 | 把 `MaxMovementIterations` 改 1 | 走进墙角立刻卡住 → 理解 crease |
| 6 | 平台相位 | 平台来回平移，角色站上去 | 对照 `_attachedRigidbodyVelocity` 的两笔位移；再把 `MoveWithPhysics` 关掉看抖动 |
| 7 | 打滑 | 平台速度从 5 突降到 0 | `RigidbodySlipVelocity` 触发 `ForceUnground`，角色被甩出 |
| 8 | 插值 | `KCCSettings.Interpolate = false` | 144Hz 下肉眼可见的 50Hz 抖动 → 理解三套位姿 |

> 调试技巧：用 `OnDrawGizmos` 画出 `GroundingStatus.GroundPoint`、`Overlaps`、`Motor.Velocity`，"看见"往往比断点更快定位问题。

---

# 结语

> 读 KCC 真正学到的不是"怎么用 KCC"，而是三件事：

1. **任何角色控制器都是"意图 → 速度 → 位移 → 碰撞解算 → 表现"的流水线**，KCC 只是把中间两步做到了极致。
2. **所有"手感玄学"背后都是可量化的数学**：吸附距离、皮肤厚度、指数收敛、保长度投影、迭代预算。
3. **工程上的优雅来自约束**：固定预算、零 GC、确定顺序、状态可序列化。

> 这三条和引擎无关，换 UE、换自研 2D、换下一家公司，都还用得上。
