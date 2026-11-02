# 行为树搭建清单（挑战 BOSS · 怪兽）

> 配套文档：`敌人AI架构改进方案_行为树与动作层.md` §5.1 / §16.3 / §16.4
> 目的：把 `BT_Enemy_Boss_Goblin.asset`（当前是空树）在 Behavior Designer 窗口里搭出来，然后把决策权从内置决策切到树。
> 为什么必须手搭：BD 的序列化只写「窗口里当前打开的那棵树」，代码建图落盘得到 `types: []`（证据见 §16.4）。

---

## 0. 前置检查（30 秒）

| 检查 | 期望 |
| --- | --- |
| BD 编辑器能打开 | 菜单 `Tools → Behavior Designer → Editor` |
| 任务出现在菜单里 | 在树里右键 `Add Task`，能看到 `Game/Enemy/Action`（8 个）与 `Game/Enemy/Condition`（6 个） |
| 目标资产存在 | `Assets/Game/Configs/Battle/AI/BT_Enemy_Boss_Goblin.asset` |
| 场景 | 打开 `tiaozhan`，`怪兽` 上已有 `EnemyBrain` + `EnemyActionController` + `HealthModel`（内置决策目前是开着的，先别动） |

> 任务不需要拖任何场景对象：每个任务 `OnAwake` 里 `GetComponent<EnemyBrain>()` 自己拿。**不要**在任务面板里拖引用来“加速”，那会让树资产绑定场景实例，多实例会串台。

---

## 1. 目标结构

```
Selector（根）
├─ Sequence ① 破防硬直
│   ├─ Cond_EnemyPoiseBroken
│   └─ Action_EnemyWaitActionEnd        timeoutSeconds = 2.5
├─ Sequence ② 战斗
│   ├─ Cond_EnemyTargetValid
│   └─ Selector ②
│       ├─ Sequence ②-1 攻击
│       │   ├─ Cond_EnemyCanAttack
│       │   ├─ Cond_EnemyInAttackRange   tolerance = 0.6
│       │   ├─ Action_EnemyAttack        attackId 留空（按权重自动挑）
│       │   └─ Action_EnemyWaitActionEnd timeoutSeconds = 6
│       ├─ Sequence ②-2 后撤
│       │   ├─ Cond_EnemyTooClose        factor = 0.75
│       │   ├─ Cond_EnemyRandom          chance = 0.35
│       │   ├─ Action_EnemyBackstep
│       │   └─ Action_EnemyWaitActionEnd timeoutSeconds = 3
│       ├─ Sequence ②-3 环绕
│       │   ├─ Cond_EnemyRandom          chance = 0.45
│       │   ├─ Action_EnemyStrafe         direction 留空（0 = 随机方向）
│       │   └─ Action_EnemyWaitActionEnd timeoutSeconds = 3
│       └─ Sequence ②-4 追击
│           ├─ Action_EnemyMoveTo
│           └─ Action_EnemyWaitActionEnd timeoutSeconds = 5
└─ Action_EnemyIdle                    （没有目标时站桩）
```

共 19 个节点。顺序即优先级：**破防 > 战斗 > 站桩**；战斗内部 **攻击 > 后撤 > 环绕 > 追击**。

---

## 2. 搭树步骤

1. 打开 BD 编辑器（`Tools → Behavior Designer → Editor`），在 `Project` 里点中 `BT_Enemy_Boss_Goblin.asset`。
2. 根节点默认是 `Entry` → 右键 `Add Task` → `Composites/Selector`，作为根下的第一个节点。
3. 在 Selector 上右键 → `Add Task` → `Composites/Sequence`，得到 ①；再右键 Selector → 添加第二个 Sequence 得到 ②；第三个直接加 `Game/Enemy/Action/Action_EnemyIdle`。
4. 依次在对应 Sequence 右键添加子节点（任务在 `Game/Enemy/Action` 与 `Game/Enemy/Condition` 分组下）。
5. 选中节点，在 Inspector 里填参数（见第 1 节表格的右列）。**没写的参数保持默认**。
6. `Ctrl+S` 保存资产。保存后资产大小应明显大于 807 字节 —— 若仍是 807 字节说明没保存成功。

---

## 3. 切换到树驱动

| 步骤 | 操作 |
| --- | --- |
| 1 | 场景里选中 `怪兽` → `BehaviorTree` 组件：勾上 `enabled`，`External Behavior` 指向 `BT_Enemy_Boss_Goblin`，`Start When Enabled = true` |
| 2 | 同一个物体上 `EnemyBrain.enableBuiltInDecision` **取消勾选**（两套决策同时开会让意图打架） |
| 3 | 保存场景 |
| 4 | 进 Play，看 `BehaviorManager` 里该行为是否在 tick（BD 运行时窗口会高亮当前节点） |

---

## 4. 验收（对照文档 §12）

| 指标 | 目标 | 怎么看 |
| --- | --- | --- |
| 树在跑 | 节点高亮随行为变化 | BD 运行时窗口 |
| 出手率 | ≥ 8 次/分钟 | `EnemyBrain.AttacksStarted` 或 `logActions` 日志 |
| 招式不重复 | 连续 3 次不出同招 | `brain.LastAttackId` 序列 |
| 轻击打断率 | ≤ 5% | 前摇期间打它，出手不应中断 |
| 行为可分辨 | 能看到「追击 / 环绕 / 后撤 / 出招」四种状态 | 肉眼 + `brain.CurrentAction` |

---

## 5. 回退

出问题不用改代码：

1. `BehaviorTree.enabled = false`
2. `EnemyBrain.enableBuiltInDecision = true`

内置决策与树驱动是**互斥开关**，两者用的是同一套动作层（`EnemyActionController`），所以回退不影响手感参数的验证。

---

## 6. 搭完告诉我

如果树跑起来后出现「树在 Running 但怪不动」，优先检查：

- 任务节点是否被加成了 `Action` 而不是 `Conditional`（反过来会让分支恒真/恒假）
- `Action_EnemyWaitActionEnd` 的 `timeoutSeconds` 是否被清成 0（0 会立刻超时失败）
- 树里是否误拖了场景对象引用（会导致 `ExternalBehavior` 绑定实例）
