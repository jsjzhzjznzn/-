# 挑战 BOSS 流畅度改造方案

> 2026-10-29 | 范围：只改挑战场景（tiaozhan）的「怪兽」这一个 BOSS。
> 目标：出手节奏与动作衔接达到「动作游戏 BOSS」的可读性——玩家能读招、能闪、能抓后摇，BOSS 不会卡在半招或原地发呆。

---

## 1. 现状（实测数据）

数据由 Play 模式实测采集，不是估计值。

| 指标 | 当前值 | 判读 |
| --- | --- | --- |
| 单招总时长 | stamp 1.15s / swing 3.52s / slam 4.68s / slam_phase2 3.12s | 单次挥击 3.5~4.7s，玩家要盯着同一段动画很久 |
| 平均出手间隔 | 2.30s（最大 3.85s） | 节奏拖沓 |
| 出手前的空档 | 每次都是 `Attack → FaceTarget(0.12s) → None(0.01s) → Attack` | 打完先站一下、闪回 idle，再挥下一刀 |
| 侧移动作 | 播 `Goblin_Ani_Run`（正面跑步）却横向位移 | 脚滑 |
| 挑衅（Taunt） | 6~10s 一次，每次 1.2s 且 `tauntClip` 为空 | 周期性地站着不动、无动画 |
| 受击打断 | `Hurt` 对攻击恒可打断 | 玩家平A可无限打断，BOSS 常挥不完 |

两项实测佐证：

- 把招式播放速度提到 1.6× 后，出手间隔从 2.30s 降到 0.12s，说明瓶颈是**片段时长**而不是决策层。
- `怪兽.fbx` 内含 43 段动画，当前只用了 8 段。其中 `Goblin_Ani_Walk_F/B/L/R`（四向走）、`Goblin_Ani_Run_Start/End`、`Goblin_Ani_Attack_02/04/05/07`、`Goblin_Ani_Dodge` 均未被使用。

结论：行为树的分支结构本身没有问题，问题全部落在「动作层时长」与「多余的中间停顿」上。

---

## 2. 设计

### 2.1 片段裁剪（`AttackDef.startNormalized / endNormalized`）

招式片段是一整段「起手 → 命中 → 收招 → 回正」。收招段在实战里是纯冗余：玩家已经在等下一招，BOSS 还在慢慢把武器放下。

- `endNormalized` 砍掉尾部收招。取值口径：`min(1, recoveryStart + 0.12)`——`recoveryStart` 是原配置里「此后可被取消」的分界点，砍到这里不会切到命中动作本身。
- `startNormalized` 保留（当前全部为 0），用于将来跳过片段自带的静止帧。
- 动作结束不再硬切：`PlayClip` 的淡入时间（0.06~0.1s）同时充当淡出，下一动作以交叉淡出接入。

判定帧不随裁剪漂移：`hitFrames` 始终以原始片段归一化时间为准，动作内部换算成裁剪窗口内的本地进度用于窗口判断。

### 2.2 连段（`AttackDef.followUps`）

单招结束后必须回决策层（行为树 tick 0.15s + 决策间隔），这是「一刀一刀打」的根源。连段把后续招式放在动作层直接续接，绕过决策：

```
ComboLink { string attackId; float atNormalized; float chance; }
```

- 招式进入时按 `chance` 掷一次，命中则记下续接目标；
- 动作进度到达 `atNormalized` 时取消剩余后摇，同帧启动下一招（不经过树）；
- 连段深度上限 3，防止无限连；
- 续接的招式同样走完整的冷却登记与出手计数，指标口径不变。

### 2.3 侧移使用方向走片段

`Goblin_Ani_Walk_L/R/F/B` 是四向走路动画，与侧移位移方向一致。动作层按「移动方向在角色本地空间的象限」选片段：

- 本地 X 分量占优 → `Walk_R` / `Walk_L`
- 本地 Z 分量占优 → `Walk_F` / `Walk_B`
- 缺少片段时退回 `runClip`

播放速度按 `实际移速 / walkClipSpeed` 校正，`walkClipSpeed` 是该片段的作者设计移速。

### 2.4 受击打断：轻击按概率

| 受击等级 | 前摇 | 判定帧 | 后摇 |
| --- | --- | --- | --- |
| 轻击（`damage < heavyHitDamage` 且非暴击） | 30% 概率打断 | 30% 概率打断 | 必打断 |
| 重击 / 暴击 | 必打断 | 必打断 | 必打断 |
| 破韧（韧性归零） | 必打断 | 必打断 | 必打断 |

概率由 `EnemyProfile.lightHitInterruptChance` 控制（默认 0.3）。未打断时伤害与削韧**照常结算**，只是不切受击动作——玩家看到的是伤害数字与刀光，而不是 BOSS 被打断。

### 2.5 去掉出手前的独立转向动作

`AttackAction` 在前摇阶段（进度 < 首个判定帧）已经每帧 `FaceTargetSmooth`。行为树里的 `Action_EnemyFaceTarget` 是重复动作，且它会播 idle 片段，产生「打完站着闪一下」的观感。从树里移除该节点。

### 2.6 挑衅改为低频且仅在拉开距离时

- 间隔 6~10s → 15~25s；
- 触发条件增加「目标距离 > 攻击范围 × 1.5」，即只在玩家跑开时给一个进攻窗口，贴身缠斗时不再插入站桩。

---

## 3. 文件影响面

| 文件 | 改动 |
| --- | --- |
| `Battle/AI/Data/AttackDef.cs` | 新增 `startNormalized` / `endNormalized` / `followUps` 与 `ComboLink`；`Validate` 校正区间 |
| `Battle/AI/Data/EnemyProfile.cs` | 新增 `lightHitInterruptChance`、`walkLeftClip/walkRightClip/walkForwardClip/walkBackClip`、`walkClipSpeed`；挑衅间隔默认值 15/25 |
| `Battle/AI/Core/EnemyAction.cs` | 新增 `Next`（连段续接目标） |
| `Battle/AI/Core/EnemyActionController.cs` | 动作结束时优先接管 `Next`，无则正常结束 |
| `Battle/AI/Actions/AttackAction.cs` | 裁剪窗口、判定帧本地换算、连段掷选与续接 |
| `Battle/AI/Actions/StrafeAction.cs` | 按移动方向选走片段 + 速度校正 |
| `Battle/AI/Core/EnemyBrain.cs` | 轻击概率打断、`RegisterAttackUse`（冷却统一登记）、挑衅距离门槛、`PlayLocomotionScaled` |
| `Battle/AI/Editor/EnemyBehaviorTreeBuilder.cs` | 攻击分支去掉 `Action_EnemyFaceTarget` |
| `Battle/AI/Editor/EnemyAISetup.cs` | 档案写入裁剪区间、连段表、走位片段与新挑衅间隔 |

不涉及协议、服务端与存档。

---

## 4. 招式表（改造后）

| id | 片段 | 裁剪窗口 | 有效时长 | 连段 | 权重 | 冷却 |
| --- | --- | --- | --- | --- | --- | --- |
| stamp | Attack_02_Stamp (1.15s) | 0 → 0.74 | 0.85s | → stamp 55% / → swing 35%（0.80 处） | 1.25 | 1.4s |
| swing | Attack_01 (3.52s) | 0 → 0.84 | 2.37s（speed 1.25） | → stamp 45%（0.90 处） | 1.0 | 2.2s |
| slam | Attack_03 (4.68s) | 0 → 0.88 | 2.84s（speed 1.45） | 无（终结技） | 0.7 | 3.0s |
| slam_phase2 | Attack_06 (3.12s) | 0 → 0.84 | 2.28s（speed 1.15） | → stamp 50%（0.88 处） | 1.0 | 2.6s |

连段上限 3 段：`stamp → stamp → swing` 约 4.1s 的连续攻势，中间没有决策空档。

---

## 5. 验收项（Play 模式实测口径）

| 项 | 目标 | 采集方式 |
| --- | --- | --- |
| 单招有效时长 | ≤ 3.0s，最快招 ≤ 1.0s | `AttackAction` 起止时间戳 |
| 平均出手间隔 | ≤ 1.5s | 连续 Attack 起点差 |
| 出手前空档 | ≤ 0.05s | `Attack → None → Attack` 的中间时长 |
| 连段出现 | 30s 内至少 2 次 2 连击 | 动作时间线里连续 Attack 且中间无其他动作 |
| 侧移不脚滑 | 侧移期间播 `Goblin_Ani_Walk_R/L` | 运行时读 `Animancer.States.Current.Clip.name` |
| 轻击打断率 | 前摇阶段约 30% | 注入轻击计次 |
| 挑衅频次 | 30s 内 ≤ 1 次 | 动作时间线里 Taunt 出现次数 |
| 回归 | 破韧硬直、阶段切换、死亡、命中判定不变 | 注入伤害与击杀 |

## 5.1 实测结论（tiaozhan，Play 模式）

招式裁剪后的实际播出时长：

| id | 片段全长 | 裁剪窗口 | 实际时长 | 连段取消点 |
| --- | --- | --- | --- | --- |
| stamp | 1.15s | 0 → 0.74 | 0.74s | 0.59s（进度 0.80） |
| swing | 3.52s | 0 → 0.84 | 2.36s | 2.12s（进度 0.90） |
| slam | 4.68s | 0 → 0.88 | 2.84s | 无（终结技） |
| slam_phase2 | 3.12s | 0 → 0.84 | 2.28s | 2.01s（进度 0.88） |

20 秒贴身采样的动作时间线（节选）：

```
   3.26~ 3.86 (0.59s) Attack:stamp#0
   3.86~ 4.45 (0.59s) Attack:stamp#1      ← 连段，无空档
   4.45~ 5.19 (0.74s) Attack:stamp#2      ← 连段
   5.39~ 5.89 (0.50s) Strafe              ← 串间节拍
   6.15~ 6.66 (0.51s) Attack:slam#0
  14.27~17.19 (2.91s) Attack:slam#0
```

| 验收项 | 结果 |
| --- | --- |
| 单招有效时长 | 0.74s / 2.36s / 2.84s / 2.28s，全部 ≤ 3.0s |
| 连段 | stamp→stamp→stamp 与 stamp→swing 均实测出现，同帧续接、中间无 `None` |
| 串间节拍 | 0.30~0.50s（由 Move/Strafe 填充），不再是 0.9s 的站桩 |
| 侧移片段 | 实测播放 `Goblin_Ani_Walk_L` / `Goblin_Ani_Walk_R`，不再用正面跑步片段 |
| 轻击打断率 | 31 次前摇注入中 7 次被打断 = 23%（期望 30%，二项分布范围内） |
| 挑衅频次 | 20s 内 0 次（改前 30s 内 3 次） |
| 命中 | 玩家血量实测下降（同段 48 点），判定与伤害链路无回归 |
| 破韧 / 阶段 / 死亡 | 韧性归零仍进 Stagger；HP 45% 进阶段 2 并播 PhaseIntro 后解锁 slam_phase2；击杀进 Dead |
| 动画连续性 | 逐帧测骨骼（双手/头/胯）位移，全部 >0.30m/帧 的尖峰都落在命中帧（slam 进度 0.48、swing 进度 0.35），裁剪点与连段点没有出现位移尖峰 |

---

## 6. 风险与边界

| 项 | 说明 |
| --- | --- |
| 裁剪点依赖 `recoveryStart` | 该值原本按「可被取消」标定，砍到 `+0.12` 不会切到命中动作。若某个片段实际命中动作靠后，表现为收势突然，调小 `endNormalized` 即可 |
| 连段会抬高出手密度 | 连段期间忽略全局冷却，`slam` 作为终结技不参与连段，避免高伤害连发 |
| `tauntClip` / `phaseIntroClip` 仍为空 | 挑衅与阶段演出只计时不播动画（站桩 0.9s）。有了吼叫类片段后填上即生效，属美术资源缺口，不在本次改动范围 |
| 未做上半身分层 | 侧移时身体朝移动方向，而不是「下半身走、上半身看玩家」。分层需要 AvatarMask 与第二条 Animancer Layer，属后续打磨项 |
| 后撤步仍用 0.28s 动作配 2.18s 的 Dodge 片段 | 动作先结束、片段被下一动作交叉淡出覆盖，观感可接受；若要严格对齐需按片段标定 |
