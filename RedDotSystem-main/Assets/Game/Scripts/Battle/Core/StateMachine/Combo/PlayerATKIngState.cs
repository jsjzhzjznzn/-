using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// 攻击执行状态
/// 接段通过"连击缓冲 + 检查点"触发：
/// 本段配置了 linkCancelTime（0~1 归一化时间，默认 0.6），动画前段打开输入窗口，
/// 窗口内按攻击只缓冲输入（不立即出手），动画播到 linkCancelTime 处触发
/// OnLinkCheckpoint：缓冲过按键 → 一次性消费并立即切下一段；无 → 关闭窗口等动画播完。
///
/// 收尾阶段（EnterRecovery）：
/// 攻击动画播完/断连/末尾段 → 播放收尾动画并打开输入（isRecovery=true）。
/// 此阶段移动状态机保持在空状态，但每帧检测输入：
///   - 按移动 → 恢复移动并回待机（收尾动画被打断）
///   - 按攻击 → 回待机重新起手第一段
///   - 无输入 → 收尾动画播完（OnRecoveryEnd）自动回待机
/// </summary>
public class PlayerATKIngState : PlayerComboState
{
    /// <summary>是否处于收尾动画阶段（此阶段允许移动 / 重新起手；远程端判断是否重播段动画用）</summary>
    public bool IsRecovery => isRecovery;

    private bool isRecovery;

    public PlayerATKIngState(PlayerComboStateMachine stateMachine) : base(stateMachine) { }

    public override ComboStateType StateType => ComboStateType.Attacking;

    public override void Enter()
    {
        base.Enter();
        isRecovery = false;

        // 远程镜像端：currentCombo 只由拥有者输入写入，这里回退到角色的轻击连招容器
        if (player.IsRemote && reusableData.currentCombo == null)
            reusableData.currentCombo = stateMachine.characterCombo.LightCombo;

        // 起手瞬间转向最近的敌人（软锁定，仅单机；让偏离朝向的挥击能命中）
        stateMachine.characterCombo.UpdateAttackLookAtEnemy();

        PlayAttackClip();
    }

    public override void Update()
    {
        base.Update();

        // 眩晕：中止攻击并回待机（冷却/输入缓冲交给各自逻辑，不在这里清）
        if (player.CharacterState != null && player.CharacterState.IsStun)
        {
            RestoreMovement();
            stateMachine.SwitchState(stateMachine.NullState);
            return;
        }

        // 起手自动索敌的平滑转向推进（0.1s 内到位，之后自动停）
        stateMachine.characterCombo.TickAutoAimRotation(Time.deltaTime);

        // 收尾阶段：检测到移动输入 → 打断收尾，恢复移动并回待机
        if (isRecovery && player.IsMoving)
        {
            isRecovery = false;
            RestoreMovement();
            stateMachine.SwitchState(stateMachine.NullState);
            return;
        }

        // 收尾阶段：Shift 按下 → 打断收尾直接进入前冲闪避
        // 闪避只在 Idle/Walk 的 Update 里检测，而收尾时移动状态机停在空状态，
        // 所以这里必须自己拦截；且 triggered 是按下帧一次性信号，不能等下一帧，直接切。
        // 顺序很关键：先让连击回空状态（此时清的是"收尾动画"的 OnEnd），
        // 再切闪避（此时闪避动画的 OnEnd 刚挂上，不会被 NullState.ClearAnimationEnd 误清）。
        if (isRecovery && player.IsSprintPressed && player.IsMoving)
        {
            isRecovery = false;
            stateMachine.SwitchState(stateMachine.NullState);

            var moveMachine = player.MovementStateMachine;
            if (moveMachine != null)
            {
                moveMachine.SwitchState(moveMachine.dashingState);
            }
        }
    }

    /// <summary>
    /// 攻击键按下：缓冲已在 CharacterInputSystem 统一写入（不看窗口），
    /// 本状态只在收尾阶段做"立即重新起手"的即时响应；
    /// 攻击段内的按键留给检查点 OnLinkCheckpoint 统一消费
    /// </summary>
    protected override void OnAttackInput(InputAction.CallbackContext context)
    {
        if (!isRecovery) return;

        // 收尾阶段按攻击 → 立即重新起手（不切回待机等待动画播完，响应更快）
        isRecovery = false;
        stateMachine.SwitchState(stateMachine.NullState);
    }

    /// <summary>
    /// 到达本段连击检查点（动画播到 linkCancelTime，默认 60% 处）：
    /// 缓冲过攻击指令 → 立即切下一段；否则关闭输入窗口，等动画播完进收尾
    /// </summary>
    protected override void OnLinkCheckpoint()
    {
        if (reusableData.ConsumeATKInput())
        {
            AdvanceToNextSegment();
        }
        else
        {
            reusableData.canInput = false;
        }
    }

    /// <summary>进入下一段攻击（段数+1 并重播新段动画）</summary>
    private void AdvanceToNextSegment()
    {
        var combo = reusableData.currentCombo;
        if (combo == null)
        {
            RestoreMovement();
            stateMachine.SwitchState(stateMachine.NullState);
            return;
        }

        // 已被断连（如格挡触发 DisConnectCombo）→ 不允许再连段，进入收尾
        if (!reusableData.canLink)
        {
            EnterRecovery();
            return;
        }

        // 已到末尾段则不再连击，进入收尾
        if (reusableData.currentIndex.Value >= combo.GetComboMaxCount() - 1)
        {
            EnterRecovery();
            return;
        }

        reusableData.canInput = false;   // 切段瞬间关窗，等新段自己的窗口时间再开
        SetATKIndex(reusableData.currentIndex.Value + 1);
        PlayAttackClip();
    }

    /// <summary>本段攻击动画播完（窗口未被消费时兜底）：进入收尾</summary>
    protected override void OnAnimationEndEvent()
    {
        EnterRecovery();
    }

    /// <summary>
    /// 进入收尾阶段：打开输入窗口，收尾动画期间可移动、可按攻击重新起手
    /// </summary>
    private void EnterRecovery()
    {
        reusableData.canInput = true;
        isRecovery = true;

        var combo = reusableData.currentCombo;
        if (combo == null || combo.GetAttackEndClip(reusableData.currentIndex.Value) == null)
        {
            // 无收尾动画 → 直接恢复移动并回待机，避免状态卡死在攻击态
            RestoreMovement();
            stateMachine.SwitchState(stateMachine.NullState);
            return;
        }

        // 播放收尾动画（播完 OnRecoveryEnd 自动恢复移动并回待机）
        PlayAttackEndClip();
    }
}
