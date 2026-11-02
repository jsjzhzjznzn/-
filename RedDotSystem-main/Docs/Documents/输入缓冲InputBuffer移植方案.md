# 输入缓冲（InputBuffer）移植方案

> 目标工程：`D:\tuanjieproject\frined\RedDotSystem-main`（团结引擎 2022.3.62t11）
> 参考框架：`C:\Users\www11\Desktop\Mm-FrameWork-main\Mm-FrameWork-main\Assets\MieMieFrameTools\Scripts\Frame\E_Input`
> 文档状态：**先文档后实施**　编写日期：2026-10-21

---

## 0. 结论摘要

1. 只移植源框架 `E_Input` 里的**输入缓冲层**（`InputBuffer3D`），**不移植** `InputManager` 的 Player/UI Map 切换、`ChangeKey` 改键、`InputInteract` 交互系统。理由：本工程已由 `CharacterInputSystem` 承担输入读取，UI 走 UGUI EventSystem + InputSystemUIInputModule，改键与交互无需求。
2. 缓冲从"连招私有的时间戳字段"升级为**框架级统一缓冲管理器**（`ModuleHub` 注册、`MonoManager` 帧驱动），攻击 / 重击 / 冲刺 / 技能共用一套 API。
3. **写入点收敛**：按键按下即入缓冲（`CharacterInputSystem` 单点写入），不再被 `canInput` 门禁拦在门外；消费点仍保留原有窗口判定。→ 少丢键，也不乱出手。
4. 缓冲窗口由"1.0s 时间戳"改为**按动作可配的剩余时间槽**（默认 0.2s，攻击 0.4s），删除 `atkBufferWindow`。
5. 缓冲进度改用 `Time.deltaTime` 递减（源实现即如此），顿帧（timeScale→0）期间缓冲不会被"偷走"，与动画节奏一致 —— 现有 `Time.time` 时间戳做不到这点。
6. 分两步落地，每步一次提交：**P1 框架模块（零业务改动）→ P2 连招接入 + 写入点收敛**。P3（冲刺/技能/跳跃缓冲、队列连按）留接口不实施。

---

## 1. 现状与问题

| # | 问题 | 位置 | 后果 |
|---|---|---|---|
| 1 | 缓冲只服务连招，其它动作（冲刺/技能/重击）完全没有缓冲 | `PlayerComboReusableData.cs:47-64` | 动画尾巴上按 Shift / 技能直接丢失，手感"没反应" |
| 2 | 写入点被 `canInput` 门禁挡住 | `PlayerComboState.cs:76`、`PlayerATKIngState.cs:81` | 窗口关闭期（段未配 linkTime、技能中）按下的攻击键**直接丢弃**，而不是留给下一次机会 |
| 3 | 缓冲窗口 1.0s 过长且硬编码 | `PlayerComboReusableData.cs:47` | 段间隔内提前 0.8s 乱按也会接段 → "黏手"、误连段 |
| 4 | 用 `Time.time` 时间戳判定过期 | `PlayerComboReusableData.cs:53-59` | 命中顿帧把 `timeScale` 压到 0.03 时，时间照走 → 顿帧期间按下立刻"过期"，打击反馈越强越吃键 |
| 5 | 缓冲语义与连招数据耦合 | 同上 | 重击/技能想加缓冲必须再写一套时间戳，重复且不一致 |
| 6 | 每个状态各自订阅 `inputActions` | `PlayerComboState.cs:57-59`、`PlayerMovementState.cs:56-58`、`PlayerIdlingState.cs:32` | 订阅/退订分散，漏退订即泄漏（远程端靠 `IsRemote` 手动挡） |
| 7 | 待机轻点判定自造 0.13s 计时器 | `PlayerIdlingState.cs:54-84` | 与缓冲是两套时间窗概念，后续接缓冲容易打架（本次不动，见 §6） |

---

## 2. 源框架对照：移植 / 不移植

| 源文件 | 处理 | 说明 |
|---|---|---|
| `InputBuffer/InputBuffer3D.cs` | **移植** | 单槽 + 队列 + Interrupt 三件套，语义完整，正是本次需要的"预处理" |
| `InputBuffer/InputBuffer2D.cs` | 不移植 | 无队列、无 Interrupt，3D 版是其超集 |
| `InputManager/InputManager.cs` | 不移植 | Player/UI Map 互斥切换在本工程无场景（战斗只有一套 player map，UI 由 EventSystem 处理） |
| `InputManager/InputManager.PlayerInput.cs` | 不移植 | 读取能力已由 `CharacterInputSystem` 提供，重复封装会分裂两套输入入口 |
| `InputManager/InputManager.UIMap.cs` | 不移植 | 本工程无 OpenBag/ItemWheel 这类全局命令键 |
| `InputManager/InputManager.ChangeKey.cs` | 不移植 | 无改键需求 |
| `InputInteract/**` | 不移植 | 交互系统（拾取/开门）不在本次范围 |
| `InputSystemAction.inputactions` | 不导入 | 用现有 `playerinputaction.inputactions`（move/look/dash/attack/defense/skill/zoom） |

命名空间沿用 `MieMieFrameWork.M_InputSystem`，目录按本工程框架布局放 `Assets/Game/Scripts/Framework/F_Input/`。

---

## 3. 详细设计

### 3.1 文件清单

| 动作 | 文件 |
|---|---|
| 新增 | `Assets/Game/Scripts/Framework/F_Input/InputBuffer3D.cs` |
| 改 | `Assets/Game/Scripts/Framework/A_Frame/ModuleHub.Register.cs`（注册管理器） |
| 改 | `Assets/Game/Scripts/Battle/Core/Input/CharacterInputSystem.cs`（唯一写入点） |
| 改 | `Assets/Game/Scripts/Battle/Core/StateMachine/Combo/PlayerComboReusableData.cs`（改委托到缓冲） |
| 改 | `Assets/Game/Scripts/Battle/Core/StateMachine/PlayerComboState.cs`（去写入门禁） |
| 改 | `Assets/Game/Scripts/Battle/Core/StateMachine/Combo/PlayerATKIngState.cs`（去写入门禁） |
| 改 | `Assets/Game/Scripts/Battle/Core/StateMachine/PlayerStates/PlayerDeadState.cs`（死亡清缓冲） |

### 3.2 枚举与 API

```csharp
namespace MieMieFrameWork.M_InputSystem
{
    // 按本工程 playerinputaction 实际动作裁剪（源含 Jump，本工程无跳跃动作）
    public enum E_InputType { Attack, HeavyAttack, Dash, Skill }

    [ManagerAttribute(8)]
    public class InputBuffer3D : MonoBehaviour, IManagerBase
    {
        // 配置（Inspector）
        // defaultBufferTime = 0.2f；attackBufferTime = 0.4f；maxQueueSize = 3

        void   BufferInput(E_InputType type, float customBufferTime = -1f);
        bool   ConsumeBuffer(E_InputType type, UnityAction callback = null);
        bool   HasBuffer(E_InputType type);
        float  GetRemainTime(E_InputType type);
        float  GetProgress(E_InputType type);
        void   Interrupt(E_InputType type);
        void   InterruptAll();

        // 源实现的队列版（预留，本阶段无人调用）
        void   BufferQueueInput(E_InputType type, float customBufferTime = -1f);
        bool   ConsumeQueueBuffer(E_InputType type, UnityAction callback = null);
    }
}
```

要点：
- `Init()` 里 `ModuleHub.Instance.GetManager<MonoManager>().AddUpdateListener(Tick)`，`Dispose()` 里摘除 —— 与 `F_Audio` 一致的框架生命周期。
- 长度用 `Enum.GetValues(typeof(E_InputType)).Length` 一次性建槽，无字典查表、无 GC。
- 攻击走 `attackBufferTime`（0.4s）以覆盖"按键 → linkCancelTime 检查点"的常见间隔；其余动作走 0.2s。

### 3.3 数据流

```
按键按下
  └─ CharacterInputSystem.OnEnable 注册的 action.started 回调（唯一写入点）
       └─ InputBuffer3D.BufferInput(Attack / HeavyAttack / Dash / Skill)
            └─ BufferSlot{ isActive=true, remaining=时长 }
MonoManager.Update
  └─ InputBuffer3D.Tick(Time.deltaTime)  // 递减，过期自动失效
消费
  └─ 连招：OnLinkCheckpoint / PlayerNullState.Update → ConsumeBuffer(Attack)
  └─ 后续 P3：冲刺/技能消费点
```

### 3.4 关键语义（防坑约定）

1. **写入不再看窗口，消费才看窗口**。`canInput` 只保留在消费点（`PlayerNullState.Update`、`OnLinkCheckpoint`），写入点无条件入缓冲 → 窗口关死期间的按键能被后续窗口接住（在其剩余时间内）。
2. **消费即清空，一次性**，杜绝"一次按键接两段"。
3. **窗口打开时不再清缓冲**：删掉 `PlayAttackClip` 里的 `ClearATKInputBuffer()` 调用，跨段串味交给"0.4s 过期 + 消费即清空"两个机制兜住：
   - 提前 >0.4s 的按键自然失效，不会跨段生效；
   - 每段检查点消费后缓冲已空，只有检查点之后的按键才留到下一段（这正是想要的提前输入）。
   清理接口收敛为"死亡/断连/切场景时 `Interrupt`"，不再按段清。
4. **过期用 deltaTime**，与 `timeScale` 同步：顿帧（`timeScale=0.03`）期间缓冲照常计入但流逝极慢，等于"顿帧不吃键"；暂停（`timeScale=0`）期间**不写入**缓冲，避免解除暂停后自动出手。
5. **死亡清空**：`PlayerDeadState.Enter` 追加 `InterruptAll()`，避免死亡前 0.4s 内的按键在复活后自动出手。
6. **远程镜像端不受影响**：远程端状态机本就不订阅输入（`player.IsRemote` 早退），缓冲只被本地玩家消费；缓冲是全局单例，与现有"全局输入单例"模型一致，不为每个 Player 建实例。
7. **失败降级**：`ModuleHub` 未就绪（`TryGetManager` 返回 false）时，`CharacterInputSystem` 只跳过入缓冲，不抛异常；`PlayerComboReusableData` 的 `ConsumeATKInput` 返回 false 即"无缓冲"，连招退化为只吃实时输入，不崩。

### 3.5 公开 API 兼容性

`PlayerComboReusableData` 保留 `BufferATKInput()` / `ConsumeATKInput()` / `ClearATKInputBuffer()` 三个方法名（内部改为委托 `InputBuffer3D`），`PlayerATKIngState` / `PlayerNullState` 的调用点零改动；仅删除 `atkBufferWindow` 属性（非序列化字段，无资产引用）。

---

## 4. 分阶段实施与提交

| 阶段 | 内容 | 提交信息 |
|---|---|---|
| P1 | 新增 `InputBuffer3D`（移植 + 依赖适配）+ `ModuleHub.Register` 注册一行 | `feat(input): 新增输入缓冲模块 InputBuffer3D（P1）` |
| P2 | `CharacterInputSystem` 单点写入；连招缓冲委托到模块；去写入门禁；死亡清缓冲 | `refactor(input): 连招缓冲切换到 InputBuffer3D，按键按下即入缓冲（P2）` |
| P3（未做） | 冲刺/技能消费缓冲、连按队列、`PlayerIdlingState` 轻点判定并入缓冲 | 待 P2 验收后另立文档 |

提交只 add 本次相关文件，不碰工作区里既有的脏文件（场景、材质、csproj）。

---

## 5. 验收清单

- [ ] 编辑器编译通过，控制台无新增 error。
- [ ] 连招：段动画中提前按攻击（提前 ≤0.4s）→ 到检查点稳定接下一段。
- [ ] 连招：提前 >0.4s 乱按 → 不接段（比现状 1.0s 更"干净"）。
- [ ] 收尾阶段按攻击 → 立即重新起手（行为与现状一致）。
- [ ] 段未配 `linkCancelTime`（`canInput=false`）时按键 → 进入窗口/收尾后可被接住，不再凭空消失。
- [ ] 命中顿帧期间按攻击 → 缓冲不丢（`timeScale` 恢复后仍可消费）。
- [ ] 死亡 → 复活后不自动出手。
- [ ] 双人联网：远程镜像角色不会因为本地按键多播一段。
- [ ] `PlayerComboReusableData.atkBufferWindow` 全库无引用残留。

---

## 6. 明确不做（避免过度设计）

- 不移植 `InputManager` 的 Map 互斥 / UICommand / 改键：本工程无对应场景，硬搬会分裂出第二套输入入口。
- 不重构 `PlayerIdlingState` 的 0.13s 轻点计时器：它是"走路起步"的手感阈值，不是漏输入补偿，与缓冲职责不同；合并需先定手感参数，留给 P3。
- 不动 `Assets/Game/Scripts/Compete/` 下的旧战斗复制体：该目录等待整体下线，不在此次范围。
- 不为每个 Player 建独立缓冲：当前输入是全局单例模型（本地只控一个角色），拆分会牵动所有读取点。
