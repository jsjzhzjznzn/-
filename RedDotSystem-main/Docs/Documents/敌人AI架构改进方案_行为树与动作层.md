# 敌人 AI 架构改进方案（行为树决策 + 动作层状态机 + Animancer）

> 2026-10-23 | 范围：挑战 BOSS 优先、框架同时服务副本小怪；手感"两者兼顾"；决策层用 Behavior Designer；暂不做联机同步（接口留口）。
> 结论先行：**需要状态机，但不需要"再写一套大而全的 AI FSM"**——行为树管"想做什么"，动作层状态机管"身体怎么执行"，Animancer 只管"身体长什么样"。三者职责不能混。

---

## 0. 直接回答你的两个问题

**Q1：这个 AI 需要用状态机吗？还是只用行为树就够了？**

需要状态机，但它是**动作层的小状态机**（Attack / Stagger / Dodge / Move 的生命周期），不是传统那种把"巡逻、追击、攻击、逃跑"全塞进 switch 的 AI FSM。

判定标准很简单：

| 你的 AI 类型 | 只需行为树？ | 原因 |
| --- | --- | --- |
| 塔防炮台、挂机怪、站桩施法 | ✅ 可以 | 动作没有"前摇/判定/后摇"，也没有被打断语义 |
| **动作战斗（你的项目）** | ❌ 不够 | 玩家会闪避、会打断、会绕后；AI 的一次出手必须保证"前摇 → 判定帧 → 后摇"完整，且要能被特定事件打断 |

**Q2：能不能用行为树直接操作动画？**

可以，但**只让树发"动作请求"，不要让树逐帧驱动动画**。树里可以有一次性表现节点（播个特效/语音），但"播哪段动画、什么时候结算伤害、什么时候可被打断"必须由动作层统一管。原因见 §2，这是本方案最核心的一条。

---

## 1. 现状诊断（代码级）

### 1.1 `ChallengeMonster`（挑战 BOSS 现状）

文件：`Assets/Game/Scripts/Challenge/ChallengeMonster.cs`

现状是一个**用布尔拼出来的隐式状态机**：

```csharp
private bool dead;
private bool busy;          // 攻击/受击中（不移动、不重复出手）
```

问题清单（按对"灵活感"的伤害排序）：

| # | 问题 | 现象 | 根因 |
| --- | --- | --- | --- |
| 1 | **任何伤害都能打断攻击** | 玩家一直左键，怪永远出不了手 → 像个木桩 | `OnDamaged` 无条件 `busy=true` 并切受击动画，没有"前摇霸体/受击等级"概念 |
| 2 | **只有一种攻击** | 2 秒一次、同一段动画，玩家 3 秒看穿 | `attackClip` 单段 + 固定 `attackInterval=2.0` |
| 3 | **没有预兆与转向对齐** | 出手瞬间原地转身，看起来"贴脸甩技能" | 朝向每帧 `Slerp(8/s)`，起手不锁方向；无 wind-up 提示 |
| 4 | **判定不看朝向/距离** | 怪背对玩家也能打中；玩家闪到侧后方仍扣血 | `DealDamage()` 直接对 `target` 扣血，没有扇形/胶囊判定 |
| 5 | **状态所有权混乱** | 攻击 → 受击 → 死亡三个 `Events.OnEnd` 互相覆盖 | 每个方法各自 `state.Events.Clear()` + `OnEnd=...`，没有唯一所有者 |
| 6 | **没有后摇惩罚窗口** | 玩家打完只能等，缺少"抓后摇"的博弈 | 攻击结束直接回 idle，没有 recovery 段的取消规则 |
| 7 | **行为只有两档** | 远了走、近了打 | `if (distance > attackRange) 走 else 打` |

### 1.2 `DungeonEnemy`（副本小怪现状）

文件：`Assets/Game/Scripts/Dungeon/DungeonEnemy.cs`

完全没有 AI：只做浮动、受击闪红、血条、死亡消散。`DungeonManager` 刷出来就是站桩。这套 AI 框架要能让它"挂上就活"，不挂保持现状（零回归）。

### 1.3 玩家侧已有的、值得直接复用的东西

| 能力 | 位置 | 复用方式 |
| --- | --- | --- |
| 状态机基类 `IState` / `StateMachine` | `Battle/Core/StateMachine/` | 动作层可直接实现 `IState`，与项目风格一致 |
| 攻击窗口 + 判定帧 + 顿帧 | `CharacterCombo.cs`（`ATK()` / `AttackDetection()` / `player.HitStop`） | AI 的命中判定照抄这套：扇形/球形 + 去重 + `DamageCalculator` |
| 动画事件（归一化时间） | `ChallengeMonster.Attack` 已用 `state.Events.Add(0.35f, ...)` | 判定帧继续用归一化时间（**已踩过"传秒数永不触发"的坑**） |
| 伤害/死亡事件 | `HealthModel.RequestDamage`、`EventCenter` 的 `E_OnDamage` / `E_OnDeath` | AI 受击/死亡统一走事件，不自己轮询血量 |
| 表现反馈 | `VfxManager`、`ActorAudioComponent`、`Player.HitStop`、`PauseManager` | 起手特效、命中顿帧、刀光/受击音 |
| 自动索敌加权里预留的 AI 接口 | `CharacterCombo.UpdateAttackLookAtEnemy` 注释提到 `AIStateMachine.target` | 新 AI 暴露 `Target` / `IsAttacking`，把"正在攻击我"加分项恢复 |

### 1.4 工具链实况（已实测）

| 工具 | 状态 | 对方案的影响 |
| --- | --- | --- |
| Behavior Designer | **完整版**（`BehaviorDesigner.Runtime` 621 个类型 + Editor） | 可以自写 Task、用 `BehaviorManager.UpdateIntervalSeconds` 控 tick |
| `BehaviorManager` 组件 | `tiaozhan.scene` 里已有 `behaviormanager` 对象 | 直接复用，不用新建管理器 |
| Animancer 7.4.2 | 支持 `Layers`（可访问 `Layers[1]`）、`LinearMixerState`、`AnimancerEvent.Sequence`、`Animancer.FSM` | 移动混合、上下半身分层、事件驱动都可行 |
| `AnimancerComponent.Play` | `Play(ITransition, float fade, FadeMode)` / `Play(AnimationClip)` | 用 `ClipTransition` 存每段动画的 fade/speed/events |
| `LinearMixerState` | `Add(AnimationClip)` + `SetThreshold(index, t)` + `Parameter` | 移动混合树写法确定 |

---

## 2. 为什么"纯行为树直接操作动画"会翻车

这不是教条，是四个具体故障模式：

1. **tick 频率 ≠ 动画帧率。**
   `BehaviorManager` 默认 0.2s 一次 tick（可调），而攻击判定帧精度要求 ±1 帧（16ms）。树在 Running 之间"看不见"动画已经走到哪，判定帧只能靠 Animancer 事件，不能靠树。

2. **BT 的 Running 没有"动作被抢占"的语义。**
   玩家一刀打断 AI，树的节点还在 Running；下一 tick 它继续 Running 旧攻击 → 出现"受击了还在挥"或"重播起手"。你要在每个节点手写中断检查，等于把状态机摊进树里。

3. **Sequence 重入会重播动画。**
   树每次重新进入攻击分支都会 `Play(attackClip)`。没有"同一动作不重入"的动作层，就会看到 AI 反复抖起手。

4. **打断规则无法集中表达。**
   "轻击不能打断前摇、重击可以、破防可以打断一切"是一张二维表（受击等级 × 动作阶段）。放进树里会变成散落各处的 `if`，改一次平衡要翻十棵树。

**结论**：BT 适合"选目标、选招、走位、阶段切换"这类**离散决策**；动画与判定是**连续执行**，必须由动作层独占。两者之间用"意图（Intent）"解耦。

---

## 3. 推荐架构（三层 + 一个黑板）

```
┌──────────────┐   0.1s   ┌────────────┐  0.15~0.25s  ┌──────────────┐  per-frame  ┌───────────────┐
│  Perception  │ ───────▶ │ Blackboard │ ───────────▶ │ Behavior Tree│ ──────────▶ │ Action Layer  │
│ 视觉/受击记忆 │          │ 目标/距离   │              │  BD 图形树    │  Intent     │ 动作窗口/打断  │
└──────────────┘          └────────────┘              └──────────────┘             └───────┬───────┘
                                                                                          │ Play/Events
                                                                                  ┌───────▼───────┐
                                                                                  │   Animancer   │
                                                                                  └───────────────┘
```

| 层 | 更新频率 | 只负责 | 绝对不做 |
| --- | --- | --- | --- |
| Perception | 0.1s（分帧） | 距离/夹角/遮挡/受击来源/仇恨 | 不选招、不播动画 |
| Behavior Tree | 0.15~0.25s | 选目标、选招、走位意图、阶段 | 不 `Play` 动画、不结算伤害 |
| Action Layer | 每帧 | 动作生命周期、窗口、打断、位移、判定帧 | 不做"该打谁"的决策 |
| Animancer | 引擎驱动 | 过渡、混合、事件回调 | 不承载游戏逻辑 |

**Intent（意图）只有 5 种**，动作层是唯一消费方：

```csharp
public enum EnemyIntentType
{
    Idle,        // 站桩/破绽
    MoveTo,      // 追到某点（带停止距离）
    Strafe,      // 环绕/侧移（带方向与持续时间）
    FaceTarget,  // 原地转身对齐（起手前用）
    Attack,      // 请求第 id 号招式
    Backstep,    // 后撤/拉开
    Stagger,     // 强制硬直（由受击逻辑直接触发，不经树）
}
```

动作层收到 `Attack` 后**立即返回"是否接受"**：
- 接受 → 树进入 `WaitActionEnd`（Running），直到动作层回调完成；
- 拒绝（正在前摇霸体/已在攻击/硬直中）→ 返回 Failure，树换分支。

这样树永远不需要知道动画细节，也不会重入。

---

## 4. 感知层设计

### 4.1 数据

```csharp
public struct EnemyPerception
{
    public Transform Target;          // 当前仇恨目标
    public float Distance;            // 水平距离
    public float Angle;               // 与自身 forward 的夹角
    public bool HasLineOfSight;       // 视线（Raycast environment 层）
    public Vector3 LastKnownPosition; // 丢失视野后的最后位置（3s 记忆）
    public float Threat;              // 仇恨值
    public bool TargetIsAttacking;    // 供"正在攻击我"权重使用
}
```

### 4.2 规则

| 项 | 建议值 | 说明 |
| --- | --- | --- |
| 感知间隔 | 0.1s，按 `instanceID % 10` 分帧 | 20 只怪 = 每帧 2 次 Raycast，不抖 |
| 视野角 | 120°（BOSS 可 160°） | 背后偷袭不立刻转身，给玩家绕后空间 |
| 视野距离 | 12m（BOSS 18m） | 与副本/挑战场地尺度匹配 |
| 遮挡检测 | 只打 `environment` 层 | 别把玩家/掉落物挡视线 |
| 受击记忆 | 被远程打中 → 记录攻击者 3s | 避免"打它没反应" |
| 丢失目标 | 5s 无视野 → 走向 `LastKnownPosition` → 再 3s 回巡逻 | 有"搜索"行为才像活物 |
| 仇恨 | 伤害 ×1.0 + 治疗 ×0.5 + 距离衰减 | BOSS 单目标时可退化为"最近 + 攻击我者优先" |

> 现阶段单机 1v1 可以先把 Perception 简化成"距离 + 角度 + 遮挡 + 3s 受击记忆"，但接口按上表留好，小怪/多目标时直接开。

---

## 5. 决策层（Behavior Designer）

### 5.1 树形（BOSS 版）

```
Root
└─ Selector（优先级从上到下）
   ├─ Sequence  Dead?              → Stop（ReturnSuccess）
   ├─ Sequence  Staggered?         → WaitActionEnd（硬直结束）
   ├─ Sequence  PhaseChangeReady?  → PlayPhaseIntro → 切阶段数据 → Success
   ├─ Sequence  TargetValid?       → Combat
   │  └─ Selector
   │     ├─ Sequence  CanAttack?   → AcquireToken → PickAttack(权重) → FaceTarget(0.1s) → Attack → WaitActionEnd
   │     ├─ Sequence  TooClose?    → Backstep → WaitActionEnd
   │     ├─ Sequence  ShouldStrafe?→ Strafe(0.6~1.2s) → Success
   │     └─ Sequence  NeedChase?   → MoveTo(StopDistance=attackRange*0.8) → Success
   └─ Sequence  Search/Patrol       → MoveTo(LastKnown) → Idle(0.5s) → Patrol
```

### 5.2 需要的自定义 Task（约 10 个，全部很薄）

| 任务 | 类型 | 关键字段 | 说明 |
| --- | --- | --- | --- |
| `BtEnemyContext`（共享变量） | SharedVariable | `EnemyBrain brain` | 树只拿 brain，不拿具体动画 |
| `Cond_TargetValid` | Conditional | 视野/记忆阈值 | 读 Blackboard，不自己 Raycast |
| `Cond_InAttackRange` | Conditional | 距离区间 + 角度 | 支持"太近/合适/太远"三档 |
| `Cond_CooldownReady` | Conditional | 招式 id 或全局 | 读 `AttackDef.cooldown` |
| `Cond_PhaseChanged` | Conditional | hpPercent 阈值 | 只触发一次（brain 记 flag） |
| `Action_AcquireAttackToken` | Action | 群体令牌（多怪用） | BOSS 恒成功 |
| `Action_PickAttack` | Action | 权重表 | 输出 `brain.PendingAttackId` |
| `Action_FaceTarget` | Action | 时长 0.1~0.15s | 走动作层 `FaceTarget`，smoothstep 转向 |
| `Action_RequestAttack` | Action | — | 发 Intent，返回 Running 直到动作层回执 |
| `Action_Strafe` | Action | 时长/方向/半径 | 环绕时保持面向目标（侧向移动） |
| `Action_MoveTo` | Action | 停止距离/超时 | 走导航或直线（先直线 + 简单避障） |
| `Action_WaitActionEnd` | Action | 超时保护 | **不要用 OnEnd 驱动状态**，这里只是等回执 |

### 5.3 树的使用纪律（很重要）

1. **树资产共享，引用不落地**：`ExternalBehavior` 资产里不放场景对象；运行时用 `SharedTransform`/`SharedVariable` 注入（BD 的标准做法），否则多实例互相污染。
2. **tick 频率**：`BehaviorManager.UpdateIntervalSeconds = 0.15f`；20 只怪用 `ExecutionsPerTick` 限流，避免同帧全量 tick。
3. **树不写 `Play`**：唯一例外是"纯表现节点"（播一次 VFX/语音/镜头震动），且必须 `TaskStatus.Success` 立刻返回。
4. **可中断的 Running**：`Action_RequestAttack` 在被动作层拒绝或被打断时立即返回 Failure，避免树卡在 Running。
5. **调试**：BD 的运行时节点高亮 + `brain` 里打点（`[AI] Attack=3 dist=2.1 accepted=true`），两边对照排查。

---

## 6. 动作层（本方案的核心）

### 6.1 为什么需要它（对应 §2 的四个问题）

- 保证一次攻击的 **Startup → Active → Recovery** 三段完整；
- 把"打断规则"收拢成一张表，而不是散在树/受击回调里；
- 用**动作自己的计时器**推进阶段，不再依赖 `state.Events.OnEnd`（解决现有 `OnEnd` 互相覆盖）；
- 让树拿到明确的"接受/拒绝/完成"回执。

### 6.2 动作生命周期

```
AttackAction
 ├─ Startup   0 ──────▶ s1      （前摇：可转向、不可移动、霸体等级 A）
 ├─ Active    s1 ─────▶ s2      （判定帧：伤害/削韧/特效，可被破防打断）
 └─ Recovery  s2 ─────▶ end     （后摇：可被任何打断，玩家惩罚窗口）
```

参数全部来自数据资产（§9），不写死在代码：

```csharp
[Serializable]
public class AttackDef
{
    public string id;
    public AnimationClip clip;
    public float fadeIn = 0.08f;
    public float fadeOut = 0.12f;
    public float[] hitFrames;        // 归一化 0~1（可多段，例如 0.32 / 0.55）
    public float recoveryCancelAt;   // 归一化，之后允许被取消
    public float damage = 10f;
    public float poiseDamage = 20f;
    public float range = 2.2f;
    public float arc = 100f;         // 扇形判定角
    public float advanceDistance = 0.8f;  // 起手前冲
    public float cooldown = 1.6f;
    public float weight = 1f;        // 选招权重
    public int minPhase = 1;         // 阶段限制
    public string vfxKey;            // 起手/命中特效
    public string audioKey;
}
```

### 6.3 打断矩阵（把"像木桩"直接治好）

受击等级由伤害来源决定（轻击/重击/破防/处决）：

| 受击等级 | Startup（前摇） | Active（判定中） | Recovery（后摇） |
| --- | --- | --- | --- |
| 轻击 | ❌ 不打断（霸体） | ❌ | ✅ 打断 |
| 重击 | ⚠️ 仅当韧性被打空 | ❌ | ✅ |
| 破防/处决 | ✅ | ✅ | ✅ |
| DoT | ❌ | ❌ | ❌（不产生硬直） |

**这一条是"AI 灵活感"的第一开关**：现在玩家轻击就能无限打断，AI 永远出不了手；改成"轻击只打后摇"后，怪物能完整打完一套，玩家必须学会看前摇、抓后摇。

### 6.4 韧性（Poise）与硬直

```
削韧累计 poiseCurrent -= attack.poiseDamage
poiseCurrent <= 0 → 进入 Stagger(0.8s)，poiseCurrent = maxPoise
Stagger 期间不再被削韧；结束后给 1.2s 韧性回复延迟（防连锁）
```

- 轻击削韧 8、重击 25，BOSS `maxPoise = 60`（约 3 重击破一次）。
- 硬直动画期间**无敌霸体但不免伤**（伤害照吃），避免"破了防反而打不动"。
- 硬直结束留 0.15s 的"起身无敌帧"，防止玩家踩点连到死（更像动作游戏）。

### 6.5 判定：让 AI 也会"打空"

现有 `DealDamage()` 直接对 `target` 扣血，导致玩家闪避无效、背对也能中。改为**命中帧在动作层做几何判定**（照抄玩家 `CharacterCombo.AttackDetection`）：

```csharp
// 命中帧调用（由 Animancer 事件触发）
int count = Physics.OverlapSphereNonAlloc(
    transform.position + transform.forward * def.range * 0.5f,
    def.range * 0.5f, buffer, LayerMask.GetMask("player"));

foreach (hit)
{
    Vector3 to = hit.transform.position - transform.position;
    if (Vector3.Angle(transform.forward, to) > def.arc * 0.5f) continue; // 扇形外 = 打空
    targetHealth.RequestDamage(new DamageRequest { damage = def.damage, ... }, gameObject);
}
```

好处：
- 玩家侧闪/后撤真的能躲开 → 闪避才有意义；
- AI 的"挥空"本身就是表演（配合刀光/音效），看起来更真实；
- 后续接 `CharacterCombo` 的 autoAim "正在攻击我"加权也自然成立。

### 6.6 位移与转向

| 阶段 | 位移 | 转向 |
| --- | --- | --- |
| Startup | 沿 forward 前冲 `advanceDistance`（0.15~0.3s 曲线） | 允许转向，角速度 240°/s |
| Active | 停在原地（或小幅前冲） | **锁死**（禁止 360° 追尾） |
| Recovery | 轻微回位 | 允许 120°/s 缓慢回正 |
| Move/Strafe | 直线/环绕 | 朝向目标 ±25° 内可侧移 |

转向用 `smoothstep` 而不是线性 `Slerp`（玩家自动索敌里已经这么做了），起手 0.1s 内完成对齐，视觉上"先瞄再打"。

---

## 7. 动画层（Animancer 落地写法）

### 7.1 移动混合（Idle / Walk / Run）

实测 `LinearMixerState` 可用：

```csharp
var mixer = new LinearMixerState();
mixer.Add(idleClip);
mixer.Add(walkClip);
mixer.Add(runClip);
mixer.SetThreshold(0, 0f);
mixer.SetThreshold(1, 0.5f);
mixer.SetThreshold(2, 1f);

// 每帧：速度归一化 0..1 写 Parameter，动画自己混合
mixer.Parameter = Mathf.Clamp01(currentSpeed / maxSpeed);
animancer.Play(mixer);
```

> 如果不想引入 mixer，最低成本方案是"Idle ↔ Walk ↔ Run 三段 cross-fade"，也能用；mixer 的价值是速度连续变化时不抖。

### 7.2 动作过渡

用 `ClipTransition`（`FadeDuration` / `Speed` / `Events` 都能存进资产）：

```csharp
// 攻击：快速切入，避免"软绵绵"
animancer.Play(transition, 0.08f, FadeMode.FixedDuration);

// 受击：从 0 开始播，保证硬直时长可预期（别从中段续播）
animancer.Play(hurtTransition, 0.05f, FadeMode.FromStart);
```

### 7.3 事件（判定帧）

```csharp
var state = animancer.Play(def.transition);
state.Events.Clear();                       // 唯一所有者：动作层
for (int i = 0; i < def.hitFrames.Length; i++)
{
    float t = Mathf.Clamp01(def.hitFrames[i]);   // ★ 归一化 0~1，不是秒
    state.Events.Add(t, () => ResolveHit(i));
}
```

- **归一化时间是硬约束**：项目里已经踩过"传秒数导致事件永不触发"。
- 播放速度变化（难度/加速 Buff）时，判定帧仍按动画时间走，天然同步；但**硬直/后摇的秒数**要用 `clip.length / state.Speed` 换算，不能写死。
- 动作完成用动作层自己的计时器（`elapsed >= clip.length / speed`），不要用 `Events.OnEnd` 当唯一出口；`OnEnd` 只做兜底。

### 7.4 上半身分层（可选，P2）

实测 `animancer.Layers[1]` 可访问。适用场景："边走边挥"或"施法中保持移动"。做法：

```csharp
var upper = animancer.Layers[1];
upper.SetMask(upperBodyMask);       // AvatarMask：只含 Spine/Chest/Arms/Head
upper.Play(castTransition, 0.1f);
```

代价：需要给每个模型配 AvatarMask，且 Lite/Pro 授权差异要确认。**现阶段不建议上**——BOSS 1v1 用全身动作更清晰，分层留到 P2。

### 7.5 根位移

保持现有做法：`animancer.Animator.applyRootMotion = false`，位移完全由动作层曲线控制。原因：现有 `ChallengeMonster` 已经踩过"Goblin 片段 root motion 累积把怪顶上天"。

---

## 8. 让 AI "看起来灵活"的 13 个手法（按性价比排序）

> 公式：**灵活感 = 可读性（看得出它要干什么）× 变化性（不重复）× 反馈（打中有感）**
> 只做变化不做可读性 = 乱；只做可读性不做变化 = 木桩。

### P0：半天内能明显见效（BOSS 优先）

| # | 手法 | 参数建议 | 观感收益 | 实现位置 |
| --- | --- | --- | --- | --- |
| 1 | **招式变体池 + 权重 + 不连续重复** | 3 段普攻（0.35/0.3/0.35）+ 1 突进；同招 2 次内不重复 | 玩家无法背板，最直接 | `AttackDef.weight` + `PickAttack` |
| 2 | **距离分档选招** | 近身横扫(≤2m) / 突进(2~6m) / 投掷或吼(>6m) | 不再"贴脸一招走天下" | `Cond_InAttackRange` 三档 |
| 3 | **起手预兆 + 对齐** | 0.25~0.45s windup，前 0.1s 完成转向；起手播金色刀光/低吼 | 玩家能读招、能闪 | 动作层 Startup + VFX/音效 |
| 4 | **反应延迟 + 间隔抖动** | 发现目标后 120~250ms 才反应；间隔 ×(0.85~1.15) | 去掉"机器人同步感" | Perception 计时 |
| 5 | **后摇惩罚窗口** | Recovery 后 40% 可被玩家打断 | 博弈成立，玩家有反打空间 | 打断矩阵 |
| 6 | **受击等级/霸体** | 轻击只打后摇，重击破霸体 | 解决"被无限连" | §6.3 |
| 7 | **命中判定几何化** | 扇形 100°，玩家闪避可躲 | 闪避有意义，挥空也好看 | §6.5 |

### P1：一到两天，让 BOSS 有"性格"

| # | 手法 | 参数建议 | 说明 |
| --- | --- | --- | --- |
| 8 | **环绕/侧移（Strafe）** | 2~4m 距离 60% 概率侧移 0.6~1.2s，方向随机但每 3s 最多换一次向 | 不会一直直线贴脸 |
| 9 | **后撤步（Backstep）** | 玩家贴脸 <1.2m 时 30% 概率后撤 2m | 制造"拉开—突进"的节奏 |
| 10 | **韧性条 + 破防** | maxPoise 60，破防硬直 0.8s + 起身无敌 0.15s | 玩家有"打崩"的成就感 |
| 11 | **阶段转换** | HP 50%：0.8s 演出（吼叫/爆气）+ 换招式表 + 攻速 +10% | BOSS 记忆点 |
| 12 | **群体攻击令牌** | 同屏 N 只怪，最多 2 只同时处于 Active | 小怪不齐射，玩家不被秒 |

### P2：打磨项（有余力再做）

| # | 手法 | 说明 |
| --- | --- | --- |
| 13 | **假动作 / 变招（feint）** | Startup 在 0.6s 时 30% 概率转二段（不同判定），玩家不能只靠"看到抬手就滚" |
| 14 | 动态难度 | 连续被闪避 3 次 → 追踪角速度 +20%；连续挨打 → 霸体等级 +1（有上限） |
| 15 | 随机待机破绽 | 每 6~10s 播一次挑衅/喘息（1.2s 无霸体），给玩家主动进攻窗口 |
| 16 | 上半身分层 | 移动施法/边走边吼（P2，成本最高） |

> **最容易被忽略但最有效的是 #4（反应延迟）和 #5（后摇窗口）**：它们不增加任何美术资源，却直接改变"像人还是像机器"的体感。

---

## 9. 数据资产设计

```
Assets/Game/Configs/Battle/AI/
├─ EnemyProfile_Boss_Goblin.asset      // 属性/韧性/感知/阶段
├─ AttackSet_Boss_Goblin.asset         // 招式表（AttackDef[]）
├─ EnemyProfile_Mob_Rat.asset
└─ AttackSet_Mob_Rat.asset
```

`EnemyProfile` 字段：

| 分组 | 字段 |
| --- | --- |
| 基础 | `moveSpeed` / `turnSpeed` / `attackRange` / `stopDistance` |
| 感知 | `sightRange` / `sightAngle` / `loseTargetSeconds` / `reactionDelay` |
| 防御 | `maxPoise` / `poiseRecoverDelay` / `staggerSeconds` / `getUpInvincible` |
| 攻击 | `globalCooldown` / `intervalJitter` / `AttackDef[]` |
| 阶段 | `phaseHpPercents[]` / 每阶段招式过滤 |
| 表现 | `hurtClip` / `deathClip` / `idleClip` / `locomotionMixer` 片段引用 |

> 招式表用 SO 而不是散在 Prefab 字段：策划改数值不动场景，也不会让预制体产生大量覆盖。

---

## 10. 与现有系统的接入点

| 系统 | 接入方式 |
| --- | --- |
| 伤害 | AI 受击：订阅 `E_OnDamage`（现有做法），按 `DamageData` 的伤害量判定受击等级；AI 出手：`HealthModel.RequestDamage(new DamageRequest{...}, gameObject)` |
| 死亡 | 订阅 `E_OnDeath` → 动作层进 Dead（播死亡动画、关判定、停树） |
| Buff | `BuffComponent` 的攻速/移速/减伤照常作用于 `EnemyProfile` 的运行时副本 |
| 顿帧 | 命中时 `Player.HitStop` 由**受击方或攻击方**统一触发（建议攻击方，和玩家一致） |
| 特效/音效 | `VfxManager.Play/PlayAttached`、`ActorAudioComponent.PlayByStyle` |
| 层 | 敌人整棵子树 `SetLayerRecursively(LayerMask.NameToLayer("enemy"))`（现有做法保留） |
| 玩家自动索敌 | `EnemyBrain.Target` / `IsAttacking` 暴露给 `CharacterCombo`，恢复"正在攻击我"加分 |
| 场景 | 复用 `tiaozhan.scene` 已有的 `behaviormanager` 对象 |

### 10.1 新增目录

```
Assets/Game/Scripts/Battle/AI/
├─ Core/            EnemyBrain.cs / EnemyIntent.cs / EnemyActionController.cs
│                   EnemyAction.cs（基类）/ AttackAction.cs / StaggerAction.cs
│                   MoveAction.cs / StrafeAction.cs / DeadAction.cs
├─ Perception/      EnemyPerception.cs / EnemyThreatTable.cs
├─ Data/            EnemyProfile.cs / AttackDef.cs / AttackSet.cs
├─ Tasks/           BD 自定义任务（§5.2 那 10 个）
└─ Editor/          EnemyProfileEditor.cs（招式表预览/合法性校验）
```

`EnemyBrain` 是唯一对外门面：

```csharp
public class EnemyBrain : MonoBehaviour
{
    public Transform Target { get; }
    public bool IsAttacking { get; }
    public bool IsStaggered { get; }
    public EnemyIntentResult Request(EnemyIntent intent);   // 树调这个
    public event Action<EnemyActionKind> ActionFinished;    // 树用 WaitActionEnd 等它
}
```

---

## 11. 落地计划（每阶段都能单独验收、可回退）

| 阶段 | 内容 | 验收 | 风险 |
| --- | --- | --- | --- |
| **P0** 动作层骨架 | `EnemyActionController` + `AttackAction` + `StaggerAction`；用 `EnemyBrain` 替换 `ChallengeMonster`，**行为与数值保持等价**（1 招、2s 间隔） | 挑战场景打怪：伤害/血量/死亡与现在一致；受击不再无限打断（先按"只打后摇"） | 低；保留 `ChallengeMonster` 组件不删，可一键切回 |
| **P1** 数据化 + 灵活性 P0 七条 | SO 招式表、变体/权重/距离分档、预兆 VFX、反应延迟、后摇窗口、几何判定 | 录屏对比：10 秒内出现 ≥2 种招式；玩家侧闪可躲；轻击无法打断前摇 | 中；需要给怪加 2~3 段动画片段 |
| **P2** 接入行为树 | `ExternalBehavior` 资产 + 10 个 Task；决策从 `EnemyBrain` 内置分支迁到树 | 树 ticking 正常；切换"攻击/环绕/追击"可肉眼分辨；BD 运行时高亮与日志一致 | 中；树资产需避免引用场景对象 |
| **P3** 韧性/阶段/侧移 | Poise、破防硬直、HP50% 阶段演出、Strafe/Backstep | 破防硬直可稳定复现；阶段切换只触发一次 | 中低 |
| **P4** 小怪复用 + 性能 | `DungeonEnemy` 可选挂 `EnemyBrain`（配置开关）；分帧感知、tick 限流 | 20 只小怪帧耗时增量 < 0.3ms；不挂 Brain 时行为与现在完全一致 | 低 |

> P0 的关键是"**先等价替换，再改手感**"。否则一次改太多，出问题分不清是架构还是数值。

---

## 12. 验收指标（可量化，不靠"感觉"）

| 指标 | 目标 | 采集方式 |
| --- | --- | --- |
| 有效出手率 | ≥ 8 次/分钟（BOSS） | `[AI] attack start/finish` 日志计数 |
| 被轻击打断出手率 | ≤ 5% | 打断日志 / 总出手 |
| 招式重复度 | 连续 3 次内不出同招 | 招式 id 序列断言 |
| 玩家闪避成功率 | 30%~60%（能躲但不容易） | 命中帧判定 vs 闪避无敌窗 |
| TTK（玩家被击杀） | 45~70s（BOSS） | 战斗日志 |
| 帧耗时 | 每只怪 < 0.05ms（不含动画） | Profiler 标记 `EnemyAI` |
| GC | 稳态 0B/帧 | Profiler GC Alloc |
| 树 tick | ≤ 7 次/秒/怪 | `BehaviorManager` 计数 |

---

## 13. 风险与对策

| 风险 | 影响 | 对策 |
| --- | --- | --- |
| 树与动作层时序不同步 | 出现"树说在攻击、实际在硬直" | 动作层是唯一真相：树的 `WaitActionEnd` 只等回执，超时强制 Failure |
| `Events.OnEnd` 被多处覆盖（现有 bug） | 状态卡死 | 动作层独占 `Events`，进入动作先 `Clear()`；完成判定用动作层计时器 |
| BD 树资产共享导致多实例互相污染 | 小怪行为串台 | 树只放 `SharedVariable`，运行时注入实例数据；不要直接拖场景引用进资产 |
| Animancer 事件用秒数 | 判定帧永不触发 | 强制归一化 0~1；编辑器校验 `hitFrames` 全部 ≤1 |
| 动画片段缺失 | 报错/静止 | 动作层片段回退：缺攻击段 → 只结算不播；缺受击 → 短硬直 |
| 一次改太多 | 回归难定位 | P0 严格等价替换；每阶段单独提交 + 录屏 |
| 世界空间血条/飘字开销 | 多怪时 draw call 上升 | 沿用现有 `DungeonDropLabels` 的池化思路（P4 再优化） |

---

## 14. 附录

### A. 树与动作层的交互时序（一次攻击）

```
t=0.00  Tree: Cond_InAttackRange ✓ → PickAttack(id=2) → FaceTarget(0.10s)
t=0.10  Tree: RequestAttack(2) ──────────────▶ ActionLayer.Accept? 
t=0.10  ActionLayer: Enter AttackAction（Startup，霸体，锁移动）
t=0.10  Tree: WaitActionEnd (Running)
t=0.35  Animancer Event 0.32 → ResolveHit()  （几何判定、伤害、削韧、顿帧）
t=0.55  Animancer Event 0.55 → ResolveHit()
t=0.75  ActionLayer: Recovery 开始，取消窗口打开
t=1.10  ActionLayer: Finish → ActionFinished 事件
t=1.10  Tree: WaitActionEnd → Success → 下一 tick 重新决策
```

### B. 动作层骨架（可直接落地）

```csharp
public enum ActionKind { None, Attack, Stagger, Move, Strafe, Backstep, Dead }

public sealed class EnemyActionController : MonoBehaviour
{
    private EnemyAction current;
    private readonly Queue<EnemyIntent> pending = new();

    public bool IsBusy => current != null && !current.CanBeInterrupted;

    public bool TryStart(EnemyAction action)
    {
        if (current != null && !current.CanBeInterruptedBy(action)) return false;
        current?.Exit();
        current = action;
        current.Enter();
        return true;
    }

    private void Update()
    {
        if (current == null) return;
        current.Tick(Time.deltaTime);
        if (current.IsFinished) { var finished = current; current = null; finished.Exit(); }
    }
}

public abstract class EnemyAction
{
    public abstract ActionKind Kind { get; }
    public virtual bool CanBeInterrupted => true;
    public abstract void Enter();
    public abstract void Tick(float dt);
    public abstract void Exit();
    public abstract bool IsFinished { get; }
    public virtual bool CanBeInterruptedBy(EnemyAction next) => CanBeInterrupted;
}
```

### C. AttackAction 关键实现（窗口 + 事件 + 几何判定）

```csharp
public sealed class AttackAction : EnemyAction
{
    private readonly EnemyBrain brain;
    private readonly AttackDef def;
    private AnimancerState state;
    private float elapsed;
    private bool hitResolved;

    public override ActionKind Kind => ActionKind.Attack;
    public override bool IsFinished { get; private set; }

    public override void Enter()
    {
        brain.StopMoving();
        state = brain.Animancer.Play(def.transition, def.fadeIn, FadeMode.FixedDuration);
        state.Events.Clear();
        for (int i = 0; i < def.hitFrames.Length; i++)
        {
            int index = i;
            state.Events.Add(Mathf.Clamp01(def.hitFrames[i]), () => ResolveHit(index));
        }
        brain.PlayVfx(def.vfxKey);          // 起手预兆
    }

    public override void Tick(float dt)
    {
        elapsed += dt;
        // 用动画时长（含 Speed）而不是 Events.OnEnd 判完成
        float clipLength = def.transition.Clip.length / Mathf.Max(0.01f, state.Speed);
        if (elapsed >= clipLength) IsFinished = true;
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        if (next.Kind == ActionKind.Dead) return true;
        if (next.Kind == ActionKind.Stagger)
        {
            // 破防可打断任何阶段；普通硬直只能打后摇
            return brain.IsPoiseBroken || elapsed >= def.recoveryStartSeconds;
        }
        return elapsed >= def.recoveryStartSeconds;   // 后摇可被取消
    }

    private void ResolveHit(int index)
    {
        if (hitResolved && def.hitFrames.Length == 1) return;
        brain.DealSectorDamage(def);
    }

    public override void Exit() => state?.Events.Clear();
}
```

### D. 一条"10 分钟见效"的清单（如果只想先试水）

1. 把 `ChallengeMonster` 的 `OnDamaged` 改成"只在前摇之后才打断"（3 行）；
2. `attackInterval` 乘 `Random.Range(0.85f, 1.15f)`；
3. 准备 2 段攻击动画，轮流播，同招不连续；
4. 起手加一个 0.3s 的前摇 + 一次刀光/低吼；
5. 命中判定改成扇形（照 `CharacterCombo.AttackDetection`）。

只做这 5 条，观感就会从"木桩"变成"会打架的东西"。完整框架再按 P0→P4 推进。

---

## 15. 待你拍板的两个细节（不影响开工，P1 前给答案即可）

1. **BOSS 的攻击片段数量**：现在 `怪兽.fbx` 只有 `Goblin_Ani_Attack_01` 一段。变体池至少需要 3 段；是补美术资源，还是先用"同一段动画 + 不同速度/位移/命中帧"做出 3 个变体？
2. **阶段演出**：HP 50% 阶段切换要不要做 0.8s 的不可打断演出（玩家全程可打），还是做成"打断即失败、重新蓄力"（更硬核）？

> **实施后更正**：第 1 条不成立了——`怪兽.fbx` 实际包含 `Attack_01~07`、`Attack_02_Stamp`、`Dodge`、`Debuff_Stun_*`、多向受击/死亡。默认档案已用**真实片段**配了 4 招，不需要造假变体。

---

## 16. 实施记录（2026-10-23，P0 + P1 已落地）

### 16.1 已交付

| 层 | 文件 | 说明 |
| --- | --- | --- |
| 数据 | `Battle/AI/Data/AttackDef.cs`、`EnemyProfile.cs` | 招式表/韧性/感知/走位倾向全部数据化（SO：`Assets/Game/Configs/Battle/AI/EnemyProfile_Boss_Goblin.asset`） |
| 动作层 | `Core/EnemyAction.cs`、`EnemyActionController.cs`、`Actions/*.cs` | Attack / Move / Strafe / Backstep / FaceTarget / Hurt / Stagger / Dead 八种动作，窗口与打断矩阵写在动作内 |
| 决策层 | `Core/EnemyBrain.cs`（内置决策）+ `Tasks/*.cs`（12 个 BD 任务） | 内置决策先用（P1），BD 任务已就绪；搭好树后把 `enableBuiltInDecision` 关掉即切换 |
| 接入 | `Editor/EnemyAISetup.cs` | 菜单 `Tools/Battle/AI/安装挑战 BOSS AI（怪兽）`：建档案、挂组件、停用旧 `ChallengeMonster` |
| 场景 | `tiaozhan.scene` | 怪兽：EnemyBrain + EnemyActionController + 档案；旧脚本 `enabled=false`（可一键回退） |

默认招式表（全部使用真实片段）：

| id | 片段 | 时长 | 伤害 | 射程 | 冷却 | 阶段 |
| --- | --- | --- | --- | --- | --- | --- |
| stamp | Attack_02_Stamp | 1.15s | 8 | 2.0 | 1.4s | 1 |
| swing | Attack_01 | 3.52s | 12（两段判定） | 2.4 | 2.2s | 1 |
| slam | Attack_03 | 4.68s | 18 | 2.8 | 3.0s | 1 |
| slam_phase2 | Attack_06 | 3.12s | 16（两段） | 2.9 | 2.6s | 2（HP≤50%） |

### 16.2 Play 实测（tiaozhan）

```
brain=True ctrl=True legacyEnabled=False profile=True dist=6.37 playerHp=1000
attacks=1 lastAttack=stamp action=Strafe playerHpAfter=992      ← 命中判定生效（8 伤害）
poise 1.00->0.00 broken=True staggered=True actionNow=Stagger    ← 破韧硬直生效
startupAttack=True stillAttacking=True monsterHp=225->220        ← 前摇霸体：轻击不打断
```

动作 ↔ 动画绑定（8s 采样）：

```
Attack → Goblin_Ani_Attack_02_Stamp / Goblin_Ani_Attack_03
Move/Strafe → Goblin_Ani_Run
Y 恒为 0.000（无根位移漂移）
```

录屏：`screenshots/enemy_ai_boss.mp4`。

### 16.3 已知边界

- **内置决策**是"效用式"简化版（攻击/后撤/环绕/追击/站台）；行为树资产（`ExternalBehavior`）尚未搭建——P2 请在 BD 编辑器里用 `Game/Enemy/*` 的 12 个任务搭树，搭好后关掉 `enableBuiltInDecision`。
- 阶段切换目前只做"解锁阶段 2 招式"，**没有演出**（第 15 节第 2 问仍待你定）。
- 群体攻击令牌未做（P4）；小怪 `DungeonEnemy` 未接 EnemyBrain（P4 配置开关即可复用）。
- 编辑器实测中 `怪兽` 触发了 "NetworkBehaviours require a NetworkObject" 弹窗（HealthModel 是 NetworkBehaviour）——**已选择"No"，未给怪兽加 NetworkObject**；单人挑战不需要它。


### 16.4 补充（P3 阶段演出 + 行为树落盘阻塞记录）

**已完成：阶段切换演出（P3 #11）**

| 项 | 内容 |
| --- | --- |
| 新增动作 | `Core/EnemyActionKind.PhaseIntro` + `Actions/PhaseIntroAction.cs` |
| 数据字段 | `EnemyProfile.phaseIntroClip` / `phaseIntroSeconds`（默认 0.8s） |
| 触发 | `EnemyBrain.UpdatePhase`：阶段变化时挂起演出，空闲立刻播 |
| 抢占规则 | 可抢占位移类动作（Move/Strafe/Backstep/FaceTarget/Idle）；**不抢**攻击/受击/硬直/死亡，等其结束再播 |
| 打断规则 | 演出期间只有死亡能打断；受击请求被拒，但**伤害照吃**（避免"破阶段时打不动"） |

实测（tiaozhan，把 HP 打到 45%）：

```
actions: PhaseIntro → Move → Move ...
logs:   [AI] 怪兽 进入阶段 2
        [AI] 怪兽 阶段演出 0.80s（阶段 2）
```

**未完成 / 阻塞：行为树资产无法用代码落盘**

> **已更正（2026-10-24，见 §16.8）**：这一节当时的结论是错的。真正的根因不是"只能序列化编辑器窗口里的树"，
> 而是**每个 Task 缺少 `NodeData`/`ID`**——`JSONSerialization.SerializeTask` 会直接访问 `task.NodeData`，
> 为 null 就 NRE（表现为资产只剩 807 字节、types=[]）。补齐 `NodeData = new NodeData()` 与递增 `ID` 后，
> `JSONSerialization.Save(source)` 能正常把内存任务图写进 `source.TaskData`，再由 `AssetDatabase.SaveAssets()` 落盘。
> 下面保留原始排查记录，便于对照。

`BehaviorDesigner.Editor.JSONSerialization.Save(BehaviorSource)` **序列化的是 BD 窗口里当前打开的那棵树**，不是传入的 source。实测：

| 调用方式 | 结果 |
| --- | --- |
| `Save(src)`（TaskData 为 null） | NullReferenceException |
| 预分配 `TaskData` 后 `Save(src)` | 不抛错，但 `types=0`、`JSONSerialization="[]"` |
| 资产落盘 | 始终 807 字节的 `types: []`，运行时 `BehaviorTree.ExecutionStatus = Inactive` |

结论：`EnemyBehaviorTreeBuilder` 只能建**内存任务图**，无法生成可用的 `ExternalBehavior`。要启用 P2，需在 Behavior Designer 窗口里打开 `BT_Enemy_Boss_Goblin.asset` 保存一次（或直接在窗口里手搭）。

因此场景已恢复：`EnemyBrain.enableBuiltInDecision = true`，`BehaviorTree` 组件 `enabled = false`（组件保留，树可用后勾上并关内置决策即可切换）。

**仍待实现**：阶段攻速 +10%、群体攻击令牌（P4 #12）、`DungeonEnemy` 接入 EnemyBrain（P4）。

### 16.5 补充：阶段攻速（P3 收尾）+ 走位碰撞约束

**阶段攻速 +10%（§8 P1 #11）**

| 项 | 内容 |
| --- | --- |
| 档案字段 | `EnemyProfile.phaseAttackSpeedBonus`（默认 0.1，即每进一阶段 +10%） |
| 作用点 | `EnemyBrain.AttackSpeedMultiplier` 同时乘到**动画播放速度**与**招式冷却**上 |
| 为什么两处一起改 | 只快动画不改冷却 → 出手节奏还是旧的，观感是断的；只改冷却 → 动作本身没变快 |

实测：phase=1 mult=1.00 → HP 打到 45% → phase=2 mult=1.10。

**走位碰撞约束（§6.5 判定的必要前置）**

敌人没有 CharacterController / Rigidbody，原实现直接改 `transform.position`，不参与碰撞 —— 会穿墙（实测两点 LOS=false 却直线穿过）。

- 位移前用 `Physics.CheckSphere` 判断**目的地可站立**（不是从当前位置 SphereCast：贴墙时会 0 距离命中面前那面墙，实测把速度压到 0.007m/s）
- 撞墙时沿切向滑，**侧向锁定**（每帧重选会让左右交替、位移互相抵消）
- `MoveAction` 加卡住检测（0.5s/0.05m），卡住即 `NotifyMoveBlocked()`，决策层开 2s 绕行窗口只走 `StrafeAction`（承诺动作，不会每帧翻向）

实测（临时墙）：停在墙前 0.65m 不穿透，3s 沿墙侧移 1.8m；正常场景 12s 内走到 1.6m 并出手。

> 局限：这是文档 §5.2 说的「先直线 + 简单避障」，长墙尽头靠「环绕→直行」交替推进，不够顺滑。要顺滑需 NavMesh（烘焙场景 + NavMeshAgent），属后续可选项。

**挑战范围剩余**：~~仅 P2 行为树（阻塞于 BD 只能序列化编辑器窗口里的树，见 §16.4）~~ → **P2 已于 2026-10-24 完成，见 §16.8**。副本小怪复用（P4）按要求不做。

### 16.6 补充：§12 验收指标采集（已可量化）

`EnemyBrain` 增加指标计数 + 每 30 秒一条汇总（勾 `logMetrics`），口径与 §12 表一致：

| 文档指标 | 实现字段 | 说明 |
| --- | --- | --- |
| 有效出手率 | `AttacksStarted`（默认已有） | 折算 /min |
| 招式中断率 | `InterruptedByStagger` / `InterruptedByHurt` ÷ `AttacksFinished` | 由 `OnActionSwitched(Attack→Stagger/Hurt)` 采集 |
| 玩家闪避成功率 | `HitsLanded` ÷ `HitFramesResolved`（挥空率取反） | 判定帧几何未命中即挥空 |
| 破防次数 | `PoiseBreakCount` | 韧性归零计数 |
| 帧耗时 | `ProfilerMarker("EnemyAI")` | Profiler 里按单只怪看 |

实测（Play 30s，挑战场景）：

```
[AI-METRIC] 怪兽 出手=7(14.0/min) 完成=6 打断(硬直)=0 打断(受击)=0 打断率=0% 判定帧=11 命中=10 挥空率=9% 破韧=0 阶段=1
```

对照 §12：出手率 14/min ≥ 目标 8/min；挥空率 9% 说明几何判定确实会让 AI 打空。

### 16.7 补充：待机破绽（§8 P2 #15）

| 项 | 内容 |
| --- | --- |
| 新增动作 | `EnemyActionKind.Taunt` + `Actions/TauntAction.cs` |
| 档案字段 | `tauntClip` / `tauntSeconds`(1.2) / `tauntIntervalMin`(6) / `tauntIntervalMax`(10) |
| 触发 | 决策层：距上次挑衅超过随机间隔 → 插一次（排在攻击/后撤/环绕/追击之前） |
| 霸体 | **无**：`CanBeInterruptedBy` 返回 true，任何攻击都能打断——这就是给玩家的进攻窗口 |
| 计数 | `EnemyBrain.TauntCount` |

实测（直接构造动作，排除决策层干扰）：

```
start: accepted=True action=Taunt taunts=1
mid  (0.6s): action=Taunt
end  (1.8s): action=Move      ← 1.2s 后正常结束
```

> 自动触发在两次实测里没观察到：一次怪一路冲到近身直接开打（间隔未到），一次目标丢失（`dist=999`，`target==null` 时决策层整体早退）。参数已数据化，实战中嫌少就把 `tauntIntervalMin/Max` 调到 3~5s。

### 16.8 补充：P2 行为树落地（挑战部分收尾，2026-10-24）

#### 根因更正：不是"BD 只能序列化窗口里的树"

§16.4 的阻塞结论作废。真实原因只有一条：**`Task.NodeData` 为 null**。
`BehaviorDesigner.Editor.JSONSerialization.Save(source)` 会对每个任务调用
`SerializeNodeData(task.NodeData)`，null 直接 NRE；异常被吞掉后资产只剩 `types:[]` 的 807 字节。
补齐两样东西后即可完全用代码落盘（`types/parentIndex/startIndex` 是旧格式遗留，JSON 里已带类型名，空数组不影响反序列化）：

```csharp
var task = new Cond_EnemyCanAttack {
    NodeData = new NodeData(),   // 必须
    ID = nextId++,               // 必须唯一
};
```

落盘三步：`source.Save(null, root, null)` → `JSONSerialization.Save(source)`（写入 `source.TaskData`）
→ `EditorUtility.SetDirty(ext)` + `AssetDatabase.SaveAssets()`。
新增菜单 **Tools/Battle/AI/验证行为树资产（反序列化）** 做落盘后自检（`JSONDeserialization.Load` + 结构打印）。

#### 场景接线（已保存到 `tiaozhan.scene`）

| 项 | 值 |
| --- | --- |
| 资产 | `Assets/Game/Configs/Battle/AI/BT_Enemy_Boss_Goblin.asset`（25 节点，JSON ≈3.3KB，Version 1.7.9） |
| 组件 | 怪兽：`BehaviorTree`（enabled、`ExternalBehavior`=该资产） |
| 决策权 | `EnemyBrain.enableBuiltInDecision = false`（两套决策只留一套） |
| 树的循环 | `BehaviorTree.RestartWhenComplete = true` —— **必须**：BD 的树在根返回 Success 后就"完成"并停住，不勾就会在第一个 tick（例如目标尚未绑定）永久结束 |
| tick | `BehaviorManager.UpdateInterval = SpecifySeconds / 0.15s`。注意该模式下**组件 `enabled=false` 是设计行为**（编辑期不跑、运行期用协程 tick），不是配置错误 |

树结构（= §5.1 + 挑衅分支）：

```
Selector
├─ Sequence 破防? → WaitActionEnd(2.5s)
├─ Sequence 目标有效? → Selector
│    ├─ Sequence Taunt                       ← 冷却未到 / 动作层忙 → 任务自己 Failure
│    ├─ Sequence CanAttack → InRange → PickAttack → FaceTarget → Attack
│    ├─ Sequence TooClose(0.75) → 35% → Backstep
│    ├─ Sequence 45% → Strafe
│    └─ Sequence MoveTo
└─ Idle
```

#### 为"切到树"补的三处行为对齐（避免回归）

| 文件 | 改动 | 原因 |
| --- | --- | --- |
| `Tasks/Cond_EnemyTargetValid.cs` | 只判「有目标 + 在 `sightRange` 内」，**不再要求当前有视线** | 原实现在玩家绕后时直接 Failure → 树走 Idle，BOSS 变木桩；追击会边追边转，视线自然恢复 |
| `Tasks/Cond_EnemyInAttackRange.cs` | 补 `HasLineOfSight` | 视线条件从"目标有效"下移到"允许出手"：不给木桩，也不隔墙挥刀 |
| `Core/EnemyBrain.cs` | 目标绑定从 `Start()` 前移到 `Awake()`（`BindDefaultTarget`）+ 新增 `CanTaunt` / `TryTaunt` | ①`BehaviorTree.Start` 可能早于 `EnemyBrain.Start`，target 为 null 时首 tick 会走 Idle 且树就此完成；②内置决策关闭后挑衅需由树驱动，语义与 §16.7 完全一致（无霸体、6~10s 间隔） |
| `Tasks/Action_EnemyTaunt.cs` | 新任务：`TryTaunt()` → 等 `CurrentAction == Taunt` | 保持"树只发意图、动作层决定生命周期"的边界 |

#### Play 实测（`tiaozhan`，树接管决策）

第一段（不干预，18s，玩家静止在 8.4m）：

```
START tree=Running restart=True builtIn=False target=True monsterHp=300 playerHp=1000 dist=8.38
t=0.00  action=Move        dist=8.38
t=3.76  action=FaceTarget  dist=2.10
t=3.92  action=Attack      attackId=slam
t=8.93  action=Taunt       (taunts=1)
t=10.48 action=Attack      attackId=stamp
t=12.52 action=Attack      attackId=swing
END attacksStarted=4 attacksFinished=3 hits=4/4 playerHp=950 tree=Running
```

第二段（把玩家钉在 3.2m 逼出环绕；再注入伤害验证破防/阶段）：

```
A_END seen=Move|None|Taunt|Strafe        ← 环绕分支确实被树走到
B_INJECT poise=0.00 broken=True hp=240
t=20.00 action=Stagger  → 0.9s 后回战斗，poise 自动回满 1.00
C_INJECT hp=126/300 phase=1
t=24.92 action=PhaseIntro (0.8s) → phase=2
t=27.43 action=Attack attackId=slam_phase2   ← 阶段 2 招式解锁
C_END actions=Stagger|None|PhaseIntro|Taunt|FaceTarget|Attack|Strafe|Move tree=Running
```

覆盖到：追击 / 对准 / 攻击（slam、stamp、swing、slam_phase2）/ 挑衅 / 环绕 / 破防硬直 / 阶段演出，全部经由树触发；
玩家掉血 1000→822 证明命中判定与伤害链路在树下无误。

#### 已知边界（非缺陷）

- `Action_EnemyAttack` 的 `attackId` 与 `Action_EnemyPickAttack` 共享树级共享变量 `pendingAttackId`；即使共享失效任务也会自己再选一次（`PickAttackId()`），不会出现"选了不打"。
- 树在"一次动作结束"后会整体重启（`RestartWhenComplete`），等价于重新决策；实测动作间隔 0.2s 左右（1~2 个 tick），属正常决策节奏。
- 挑战范围剩余：仅小怪复用（P4）与群体攻击令牌，按要求不做。

### 16.9 修复：明明贴身却"追击 4 秒才出手"（2026-10-24）

#### 症状（可复现）

把玩家钉在 2.6m 绕圈（40°/s），每次出手后都出现**整整 4.00s 的连续 Move**，之后才补下一次攻击：

```
t=3.06 Attack->None  dist=2.60 atk=10
t=3.34 None->Taunt   ...
t=4.70 None->Move    dist=2.60
t=8.70 Move->None    dist=2.57      ← 4.00s（MoveAction 超时）
t=9.11 None->Attack  dist=2.60 atk=11
```

45s 只出手 5 次，其中 4 段 ≥4s 的追击全部发生在目标 2.5~2.6m（早已进入 2.9m 出手距离）。

#### 根因

1. **BD 的 Selector 在子节点 Running 期间不会回头重估更高优先级的兄弟分支**。追击一旦开始就"占住"整棵树，
   而 `MoveAction` 的超时是 4s —— 这 4 秒内攻击分支根本没有机会被求值（不是招式冷却，是决策被锁）。
   玩家绕圈时又永远进不了 `stopDistance=1.9m`，于是每次都吃满 4s 超时。
2. `MoveAction` 的到位判定写在**转向之前**：距离 ≤ stopDistance 就直接结束、一帧都不转。
   玩家贴着站在背后/侧面时，BOSS 会"追击→立即完成"空转，永远不转身（视线一直不通），也就永远不攻击。
3. 切到行为树后，`EnemyBrain.blockedUntil`（撞墙绕行窗口）**没有消费方了**——内置决策时期由它切环绕，
   树接管后没有对应分支，撞墙只会"追击→卡住→再追击"地贴墙磨。

#### 修复

| 文件 | 改动 |
| --- | --- |
| `Tasks/Action_EnemyMoveTo.cs` | 新增"能出手就提前收手"：`CanAttackNow && HasLineOfSight && dist ≤ attackRange+0.6 && PickAttackId()` 非空时 `ForceStop` 当前 Move 并返回 Success，把决策权立刻交回树 |
| `Actions/MoveAction.cs` | 超时 4s→2s；**先转向再判到位**；到位但没对准（>15°）则原地转身（播待机，不空跑），对准后才结束 |
| `Actions/FaceTargetAction.cs` | 对准（≤8°）立即结束；未对准最多转 0.25s（原来固定 0.12s，转身不够） |
| `Tasks/Cond_EnemyMoveBlocked.cs`（新） | 读 `brain.IsMoveBlocked` 的绕行窗口 |
| `Tasks/Action_EnemyStrafe.cs` | 新增 `useBlockedSide`：绕行分支沿用 `brain.BlockedSide`，不每帧重选左右（否则位移互相抵消） |
| `Core/EnemyBrain.cs` | 暴露 `IsMoveBlocked` / `BlockedSide` / `IsFacingTarget(deg)` |
| `Editor/EnemyBehaviorTreeBuilder.cs` | 树新增"被挡住 → 沿选定侧绕行"分支（排在随机环绕之前）；28 节点 |

最终树结构：

```
Selector
├─ Sequence 破防? → WaitActionEnd(2.5s)
├─ Sequence 目标有效? → Selector
│    ├─ Taunt
│    ├─ CanAttack → InRange → PickAttack → FaceTarget → Attack
│    ├─ TooClose(0.75) → 35% → Backstep
│    ├─ MoveBlocked → Strafe(blockedSide)      ← 新增
│    ├─ 45% → Strafe
│    └─ MoveTo（能出手就提前收手）             ← 新增出口
└─ Idle
```

#### 修复后实测（同一绕圈场景）

| 指标 | 修复前 | 修复后 |
| --- | --- | --- |
| 45s 出手次数 | 5 | **12** |
| ≥1s 的连续追击段 | 4 段（各 4.00s） | **0 段** |
| 背对玩家 1.2m 时出手 | 一直不转身（视线永不通） | **2.53s 内转 180° 并出手**（`None→Taunt→Move(转身)→FaceTarget→Attack`） |
| 站桩 2.6m 命中率 | — | 4 次攻击 / 4 个判定帧 = **100%** |
| 绕圈命中率 | — | 4/9 = **44%**（§12 目标 30~60%，移动中挥空属预期） |
| 撞墙绕行 | 无对应分支 | `NotifyMoveBlocked()` 后 1.13s 内进入 `Strafe` |

> 说明：绕圈场景里两次出手的最大间隔仍有 6.02s，但那是 `slam` 片段本身 4.68s + 后摇/冷却，
> 不是"决策被追击锁住"——同一段日志里 `Move` 已不再长时间占住树。
