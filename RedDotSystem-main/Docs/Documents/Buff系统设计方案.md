# Buff 系统设计方案

> 状态：设计定稿（2026-10-27）。本文只描述采用的一版设计，不含备选方案。

## 1. 设计目标

所有"临时改变角色状态"的效果——持续掉血、持续回血、属性增减、眩晕/沉默/无敌——走同一套结构：

- **配置与运行实例分离**：一份 `BuffData` 可以被多个角色、多次施加，互不干扰。
- **角色持有状态**：谁身上有什么 Buff、剩多久、几层，由角色自己的容器维护，不挂在界面或技能上。
- **外部只提请求**：施加/移除都经过容器，效果策略不直接改别的系统。
- **属性走来源账本**：生效时登记自己的加成，结束时只移除自己那一笔，不覆盖别人的变化。
- **服务端权威**：效果的增删、周期结算、到期只在服务端（单机为本地）执行；客户端只显示结果。
- **表现只读**：界面/音效订阅事件，界面关闭不影响效果运行。

## 2. 分层与职责

```
配置层    BuffData（标识/时长/层数上限/叠加规则）+ BuffEffect（做什么）
实例层    BuffInstance（挂在谁身上/来源/剩余时间/层数/tick 计时/退出锁）
容器层    BuffComponent（角色身上的唯一入口：添加/叠加/刷新/周期/退出/网络同步）
属性层    AttributeComponent → MutableAttribute → AttributeModifier(source)
控制层    CharacterState（眩晕/沉默/无敌的引用计数）
表现层    EventCenter 的 E_BuffAdd / E_BuffRemove，UI 与音效只订阅
```

职责边界：

| 层 | 可以做什么 | 不可以做什么 |
| --- | --- | --- |
| `BuffEffect` | 读写 `BuffInstance` 的状态；通过 `AttributeComponent` 登记/移除自己的 Modifier；读 `HealthModel` 结算 DoT/HoT | 存运行时状态（资产全局共享）；直接改别人的属性值；启动自己的计时器 |
| `BuffComponent` | 创建/查找/移除实例；推进时间；派发事件；网络同步 | 知道某类效果的具体数值规则 |
| `CharacterState` | 维护控制状态计数，供输入/状态机读取 | 决定谁能施加控制 |
| `AttributeComponent` | 按来源保存加成，按公式算最终值 | 判断加成的业务来源 |

## 3. 数据模型

### 3.1 BuffData（静态配置）

保留现有字段，追加叠加规则：

| 字段 | 含义 | 默认 |
| --- | --- | --- |
| `buffId` | 叠加判定键（空则回退资产名） | 空 |
| `buffName` | 显示名 | 空 |
| `effect` | 效果策略资产 | 空（挂上但无效果） |
| `duration` | 持续秒数；`<=0` 为永久 | 5 |
| `maxStack` | 层数上限 | 1 |
| `stackRule` | 叠加规则（见第 4 节） | `StackAndRefresh` |

`buffIdHash` 继续用 `Animator.StringToHash` 预计算，网络与容器都用它做键。

### 3.2 BuffInstance（运行实例）

保留：`data`、`owner`、`source`、`stacks`、`remainTime`、`tickTimer`。

新增：

- `bool removed`：退出锁。任何退出路径先检查并置位，保证同一实例的效果清理只执行一次。

语义约定：

- `source` 是"最近一次施加者"，用于 DoT 击杀归属。
- `remainTime` 对永久 Buff 用 `float.MaxValue`，不参与递减。
- `tickTimer` 保存的是"距上次 tick 之后累计的秒数"，tick 后减去间隔而不是清零，避免长期漂移。

### 3.3 BuffNetState（网络摘要）

| 字段 | 含义 |
| --- | --- |
| `buffIdHash` | 配置键 |
| `stacks` | 当前层数 |
| `remainSeconds` | 同步时刻的剩余秒数；`-1` 表示永久 |
| `controlFlags` | 该 Buff 贡献的控制位（眩晕/沉默/无敌） |

改为传剩余秒数而不是绝对 `ServerTime` 到期时刻：服务端与客户端各自用本地 `Time.time` 计时，两端都受 `timeScale` 影响，暂停一起停、顿帧一起慢，不存在两个时钟域。客户端收到后用 `remainSeconds` 重建本地剩余时间；后续变化由新的快照覆盖。

## 4. 叠加与刷新

同一 `buffId` 视为同一种 Buff。重复施加时按配置的规则处理：

| 规则 | 层数 | 时长 | 来源 | 调用钩子 |
| --- | --- | --- | --- | --- |
| `StackAndRefresh`（默认） | `min(现有+本次, maxStack)` | 刷新为完整 `duration` | 更新为本次施加者 | `OnStackChanged` + `OnRefresh` |
| `RefreshOnly` | 不变 | 刷新为完整 `duration` | 更新为本次施加者 | `OnRefresh` |
| `Replace` | 以本次施加的层数重建 | 从完整 `duration` 重新开始 | 本次施加者 | 旧实例 `OnRemove` → 新实例 `OnApply` |

来源约定：**同 buffId 合并计算，不按来源拆成多条实例；`source` 记最近施加者；DoT 击杀归属最近施加者。** 这条规则与当前 1v1 / 单机玩法一致，避免在容器键、网络摘要和 UI 里额外引入"来源实例"维度。

新增钩子：

```csharp
public virtual void OnRefresh(BuffInstance buff) { }
```

用于"刷新时长时需要重算或重新登记"的效果（属性类在层数变化时重算，刷新只需保持现状；DoT/HoT 无需处理）。

## 5. 生命周期与统一退出

### 5.1 添加

```
AddBuff(data, source, stacks)
  → 找同 buffId 实例
      有 → 按 stackRule 处理（第 4 节）
      无 → new BuffInstance → OnApply → 入列表
  → 同步网络 + 派发 E_BuffAdd
```

跨端施加统一走 `RequestAddBuff`：单机本地添加；联网发 ServerRpc 只传 `buffIdHash`，服务端用 `BuffDatabase` 还原配置，客户端无法凭空构造配置。

### 5.2 计时（服务端 / 单机）

每帧倒序遍历：

- `duration > 0` 时 `remainTime -= Time.deltaTime`。
- `effect.tickInterval > 0` 时累加 `tickTimer`；`while (tickTimer >= interval) { tickTimer -= interval; effect.OnTick(...) }`。
- `remainTime <= 0` → 走统一退出，原因记为"到期"。

Unity 的 `deltaTime` 有上限（默认约 0.33s），加载卡顿不会产生成百上千次补 tick；减去间隔而不是清零，保证 0.2s 间隔在 30/60 帧下都不漂移。

### 5.3 统一退出

所有结束入口收敛到一个内部方法：

| 入口 | 原因 | 行为 |
| --- | --- | --- |
| 时间到 | Expired | `OnExpire` |
| 主动移除 / 净化 | Removed | `OnRemove`（默认转 `OnExpire`） |
| 死亡清空 | Removed | 逐个 `OnRemove` |
| 叠加规则 Replace | Removed | 旧实例 `OnRemove` 后再创建新实例 |

```csharp
private void RemoveInternal(BuffInstance buff, bool expired)
{
    if (buff == null || buff.removed) return;   // 退出锁：只退一次
    buff.removed = true;
    if (expired) buff.data.effect?.OnExpire(buff);
    else        buff.data.effect?.OnRemove(buff);
    _buffs.Remove(buff);
}
```

- 属性类效果在 `OnExpire` 里按 `source` 移除自己登记的 Modifier；重复退出不会重复扣减。
- 每次退出后统一 `SyncNetBuffs()` 并派发 `E_BuffRemove`；循环内只收集，循环结束同步一次。
- 死亡：服务端 `Update` 检测 `!IsAlive` → `RemoveAllBuffs()`；客户端由网络列表清空后重建，不再单独维护。
- 净化不新增标签体系：需要"驱散某类效果"时由上层按 `BuffData` 列表调用 `RemoveBuff`；只有出现"免疫/驱散类型"玩法时再给 `BuffData` 加类型字段。

## 6. 属性与控制

### 6.1 属性账本（保留现有做法）

- 每条加成是 `AttributeModifier { type, value, source = BuffInstance }`。
- 属性重算：白值 `(Base + ΣAdd) × ΠMul`；系数 `(1 + ΣAdd) × ΠMul - 1`。
- 叠层时先按来源删旧再加新，避免 `1+2+3` 三角叠加。
- 结束只删自己那笔，不改 `BaseValue`，不覆盖其它来源。

### 6.2 控制状态真正生效

`CharacterState` 的计数已经就绪，本次把读取点补齐：

| 控制 | 读取点 | 行为 |
| --- | --- | --- |
| 眩晕 | `PlayerMovementState.PollInput` | 输入方向、世界移动、局部移动全部置零，不转向（所有移动状态共用的唯一入口） |
| 眩晕 | `Player.HandleDodgeInput` | 不能起手闪避 |
| 眩晕 | `PlayerNullState.Update`（攻击起手） | 不消费攻击缓冲，不进入连击 |
| 眩晕 | `PlayerATKIngState.Update` / `PlayerSkillState.Update` | 攻击/技能进行中被晕 → 恢复移动并切回待机 |
| 眩晕 | `SkillComponent.TryCast` | 不能释放技能 |
| 沉默 | `SkillComponent.TryCast` | 不能释放技能，其余行动不受影响 |
| 无敌 | `HealthModel.TakeDamage` | `闪避无敌 || CharacterState.IsInvincible` 都拦下普通伤害；DoT 仍无视无敌 |

Buff 无敌与闪避无敌的统一判定：

- `HealthModel` 在原有 `_isInvincible` 之外增加 `CharacterState.IsInvincible` 的判断。
- 只有"闪避无敌拦截"才触发闪避表现；Buff 无敌只拦伤害。
- 为此 `DamageData` 增加 `bool canTriggerDodge`，`Player.OnDamageBlocked` 仅在为 true 时 `EnterDodgeState()`，避免"开了无敌 Buff 却自动翻滚"。

移动被控的完整表现（专用眩晕动画/被控移动状态机）不在本次范围：本次保证"被晕时不能移动、不能起手、当前攻击/技能中止"，角色停在原地播待机。

## 7. 网络同步

- 服务端（房主）/单机：唯一执行效果、计时、到期、属性变更的一端。
- 客户端：
  - `OnNetworkSpawn` 与 `OnListChanged` 时按 `BuffNetState` 整表重建只读镜像；
  - 只递减 `remainTime` 供 UI 显示，不跑 `OnTick`、不写 Modifier、不移除实例；
  - 控制计数整份写入 `CharacterState.SetControlStates`。
- 迟到加入/重连：`OnNetworkSpawn` 时服务端全量推一次，客户端首次同步主动重建（`NetworkList` 首次同步不触发 `OnListChanged`）。
- `NetworkList` 的相等性必须比较全部字段（`buffIdHash/stacks/remainSeconds/controlFlags`），否则叠层/刷新会被相等门禁吞掉。
- `NetworkList` 在运行时创建并在 `OnDestroy` 显式 `Dispose`，避免编辑器重载出现 Native Collection 泄漏。

## 8. 与伤害系统的接口

- DoT 统一走 `HealthModel.TakeDamage(amount, buff.source, isCritical: false, isDoT: true)`：
  - 不吃受击硬直、不触发闪避；
  - 伤害来源取 `buff.source`（最近施加者），击杀/助攻归属由来源决定；
  - 伤害数值走受方段（防御/减伤），不走攻方段——DoT 的强度由 Buff 配置决定，不叠攻方属性。
- 命中附带 Buff（`ComboData.hitBuff`）：本次伤害结算完之后再挂，增伤/减伤从下一击生效；施加走 `RequestAddBuff`，联网时由服务端落地。
- 反伤、护盾等未来规则归伤害管线，不放 Buff 层：Buff 只负责"挂上/移除一个效果"，结算顺序由 `DamageCalculator` 决定。

## 9. 文件影响面

修改：

| 文件 | 改动 |
| --- | --- |
| `Battle/Core/Combat/Buff/BuffData.cs` | 新增 `BuffStackRule` 枚举与 `stackRule` 字段 |
| `Battle/Core/Combat/Buff/BuffInstance.cs` | 新增退出锁 `removed` |
| `Battle/Core/Combat/Buff/BuffEffect.cs` | 新增 `OnRefresh` 虚方法 |
| `Battle/Core/Combat/Buff/BuffComponent.cs` | 按规则叠加/刷新；统一 `RemoveInternal`；tick 余量；死亡清空；网络改用剩余秒数 |
| `Battle/Core/Combat/Buff/BuffNetState.cs` | `endServerTime` → `remainSeconds`（`-1` = 永久），相等性同步修改 |
| `Battle/Core/Combat/Health/HealthModel.cs` | 合并 Buff 无敌；`DamageData` 增加 `canTriggerDodge` |
| `Battle/Core/Events/EventCenter.cs` | `DamageData` 增加 `canTriggerDodge` |
| `Battle/Core/Player/Player.cs` | 暴露 `CharacterState`；闪避入口加眩晕门禁；`OnDamageBlocked` 只有闪避无敌才进闪避 |
| `Battle/Core/StateMachine/PlayerMovementState.cs` | `PollInput` 眩晕时清零输入 |
| `Battle/Core/StateMachine/Combo/PlayerNullState.cs` | 眩晕时不消费攻击缓冲、不起手 |
| `Battle/Core/StateMachine/Combo/PlayerATKIngState.cs` | 眩晕时中止攻击回待机 |
| `Battle/Core/StateMachine/Combo/PlayerSkillState.cs` | 眩晕时中止技能回待机 |
| `Battle/Core/Combat/Skill/SkillComponent.cs` | `TryCast` 读取眩晕/沉默（与技能方案共用） |

新增文件：无。

资源与协议：

- 现有 Buff 资产默认 `StackAndRefresh`，行为不变，不需要重配。
- 不改 protobuf、不改 `competeserves`；只有 NGO `NetworkList` 的字段语义在同一构建版本内变化。
- 不改 UI；事件契约保持 `E_BuffAdd/E_BuffRemove`。

## 10. E2E 验收

单机：

1. 施加一个 5 秒 Buff：`E_BuffAdd` 派发一次；5 秒后 `E_BuffRemove` 派发一次；层数/剩余时间正确。
2. 同 buffId 再次施加（`StackAndRefresh`，上限 3）：层数 1→2→3→3；每次时长刷回 5 秒；属性加成为"每层值 × 层数"而不是三角叠加。
3. `RefreshOnly`：重复施加层数不变、时长刷新、属性不变。
4. `Replace`：重复施加后旧实例的 Modifier 被移除、新实例重新登记，属性值不重复累加。
5. tick 间隔 0.2 秒、持续 1 秒：总 tick 次数为 5，不因帧率变化少打或多打。
6. 到期、主动移除、死亡清空三条路径：属性都回到基础值；同一 Buff 的退出钩子只执行一次。
7. 两个不同 Buff 各加同一条属性：移除其中一个，另一个的加成立即仍在（来源账本正确）。
8. DoT：每 tick 掉血、飘字、不进受击硬直；玩家在闪避无敌窗口内仍会掉血（`isDoT` 无视无敌）。
9. 两个来源施加同一 DoT：层数合并；击杀归属最近施加者。
10. 眩晕 Buff：起手攻击/闪避/技能被拒；攻击/技能进行中被晕立即回待机；移动输入无效；结束后全部恢复。
11. 沉默 Buff：技能被拒，移动/攻击/闪避正常。
12. 无敌 Buff：普通伤害被拦下且不掉血，不触发闪避动作；DoT 仍然掉血。

联机（multiplecompete）：

13. 客户端挂 Buff：服务端生效，双端层数/剩余时间/控制图标一致；客户端本地不执行 tick。
14. 迟到加入/重连：已有 Buff、层数、剩余时间、控制状态正确重建。
15. 客户端改本地倒计时不影响服务端结算；到期仍由服务端移除。

## 11. 风险

1. `BuffNetState` 字段语义变化只在同一构建版本内有效；跨版本联机本来就不兼容，不额外做兼容层。
2. 控制接入会改变现有手感：眩晕中止攻击/技能、移动清零需要回归轻击连招、闪避、弹刀。
3. 无敌合并后，Buff 无敌与闪避无敌的优先级只有"都拦伤害"；表现上必须区分是否触发闪避，否则会出现无敌 Buff 自动翻滚。
4. 客户端镜像只递减显示，极端情况下与服务端有几个 RTT 的偏差；每次服务端状态变化都会校正，不额外做插值。
5. 叠加规则默认值必须保持 `StackAndRefresh`，否则现有 Buff 资产行为变化。

## 12. 不做（边界）

- 不按来源拆分独立实例（1v1/单机合并语义足够）。
- 不做驱散类型/免疫/优先级系统（出现对应玩法再加字段）。
- 不做 Buff 图标栏与倒计时界面（本期只保证事件与查询接口可用）。
- 不引入 GAS 作为第二套属性/效果系统。
- 不做专用眩晕动画与被控移动状态机。

## 13. 实测结论（2026-10-27）

在程序化对象上执行 Play 模式 E2E（HealthModel / AttributeComponent / CharacterState / BuffComponent / SkillComponent）：

- 叠加：2 层 → DamageUp 0.600；3 层 → 0.900；再次施加层数封顶 3，时长刷新到 5.00 秒。
- RefreshOnly：层数保持 2，时长刷新到 5.00 秒。
- Replace：2 层重建为 1 层，属性从 0.600 落到 0.300，旧 Modifier 已清。
- 统一退出：重复 RemoveBuff 不产生残留属性；RemoveAll 后属性归零。
- DoT：1 秒 / 0.2 秒间隔 = 5 次 tick，HP 1000 → 975。
- 眩晕：IsStun 生效且技能被拒；移除后恢复。
- 无敌：普通 50 伤害被拦下（HP 保持 1000）；同期间 DoT 仍扣血（1000 → 990）。
- 死亡：Buff 清空、技能冷却清空。

未实测：联机 NetworkList 同步（需两客户端）、移动/攻击进行中被眩晕打断的实机手感、Buff 图标 UI。

