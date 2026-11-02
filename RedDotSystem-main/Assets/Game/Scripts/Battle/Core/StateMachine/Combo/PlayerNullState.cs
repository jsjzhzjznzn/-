using UnityEngine.InputSystem;

/// <summary>
/// 连击空状态（待机占位）
/// 连击状态机的默认/兜底状态：什么都不执行，只负责等待玩家输入。
///
/// 职责：
/// - Enter 时复位连击信息（打开输入窗口、段数归零、清除上次动画回调），并恢复移动（解锁）
/// - 监听轻/重攻击、技能三个输入：
///     * 轻攻击 → 先缓冲按键（时间戳输入窗），在 Update 里统一消费，
///       保证"点按的时机"与"动画播放的时机"解耦（避免快速点击漏判）
///     * 重攻击 → 直接启动重击连招
///     * 技能   → 记录当前技能数据，切到技能状态
/// - 任何起手（轻/重/技能）都会先锁定移动（移动状态机 → PlayerMovementNullState），
///   直到攻击流程结束回到本状态 Enter 才恢复移动
/// </summary>
public class PlayerNullState : PlayerComboState
{
    public PlayerNullState(PlayerComboStateMachine stateMachine) : base(stateMachine) { }

    public override ComboStateType StateType => ComboStateType.Null;

    public override void Enter()
    {
        // 1. 订阅本状态要用的输入事件（attack / heavyattk / skill）
        base.Enter();

        // 2. 复位连击信息：段数归零、canInput/canLink/canATK 全部打开
        //    （保证从任意状态回到待机后，都"回到第一段 + 可重新起手"）
        //    输入缓冲是带时间戳的独立事实，复位不触碰，收尾阶段缓冲的按键由下面 Update 消费
        stateMachine.characterCombo.ReSetComboInfo();

        // 3. 段数下标归零：currentIndex 决定播的是连招里的第几段动画，
        //    同时同步给 UI/动画（BindableProperty 通知）
        SetATKIndex(0);

        // 4. 清除当前 Animancer 状态上残留的 OnEnd 回调，
        //    避免上一次攻击/技能的"播完"事件误触发本状态逻辑
        ClearAnimationEnd();

        // 5. 死亡状态下不再恢复移动（Die() 会接着切 deadState，这里跳过避免中间态闪烁）
        if (player.IsDead) return;

        // 6. 回到待机 → 恢复移动（攻击流程结束才允许重新移动）
        player.MovementStateMachine.SwitchState(
            player.IsMoving ? player.MovementStateMachine.walkingState
                            : player.MovementStateMachine.idlingState);
    }

    public override void Update()
    {
        base.Update();

        // 眩晕：不消费攻击缓冲、不起手（缓冲会按时间自行过期）
        if (player.CharacterState != null && player.CharacterState.IsStun) return;

        // 轻攻击采用"缓冲-消费"模式：
        // - OnAttackInput 在攻击键按下时只记录按键时刻（不立即执行）
        // - 这里在每一帧检查是否"窗口内按过 且 输入窗口还开着"，满足才一次性消费并真正启动连招
        // 好处：玩家在窗口末尾狂点也能稳定接上，不会漏输入；收尾阶段缓冲的按键回待机后在此接住
        if (reusableData.canInput && reusableData.ConsumeATKInput())
        {
            // 行走中（移动状态机处于 walkingState）→ 前进攻击；否则普通平A第一段
            var moveMachine = player.MovementStateMachine;
            if (moveMachine != null && moveMachine.CurrentState == moveMachine.walkingState)
                stateMachine.characterCombo.ForwardCombo();
            else
                stateMachine.characterCombo.LightComboInput();

            // 起手攻击 → 锁定移动（移动状态机切到空状态，攻击期间不再响应方向输入）
            player.MovementStateMachine.SwitchState(player.MovementStateMachine.playerMovementNullState);

            // 交给攻击状态去播动画，本状态退出
            stateMachine.SwitchState(stateMachine.ATKIngState);
        }
    }

    /// <summary>重攻击按下（事件回调，帧内同步触发；heavyCombo 未配置，暂注释）</summary>
    /*protected override void OnHeavyAttackInput(InputAction.CallbackContext context)
    {
        // 输入窗口关闭时忽略（例如正在收招/技能中，禁止直接起手重击）
        if (!reusableData.canInput) return;

        // 重击走独立连招容器（heavyCombo），与轻击互不串段
        stateMachine.characterCombo.HeavyComboInput();

        // 起手攻击 → 锁定移动
        player.MovementStateMachine.SwitchState(player.MovementStateMachine.playerMovementNullState);

        stateMachine.SwitchState(stateMachine.ATKIngState);
    }*/

    /// <summary>技能按下（事件回调）</summary>
    protected override void OnSkillInput(InputAction.CallbackContext context)
    {
        // 输入窗口关闭时忽略
        if (!reusableData.canInput) return;

        // 角色没有配置技能时，按了也没反应，留在待机
        var skill = player.DefaultSkill;
        if (skill == null) return;

        // 冷却/充能/公共冷却/眩晕/沉默全部由技能容器校验；未通过不锁移动、不切状态
        if (player.SkillComponent == null || player.SkillComponent.RequestCast(skill) != SkillCastResult.Ok)
            return;

        // 校验通过：记录本次技能并同步给远程端，再进入技能状态
        reusableData.currentSkill = skill;
        player.SyncSkillId(skill.skillIdHash);

        // 起手技能 → 锁定移动
        player.MovementStateMachine.SwitchState(player.MovementStateMachine.playerMovementNullState);

        // 切到技能状态去播技能动画
        stateMachine.SwitchState(stateMachine.SkillState);
    }
}
