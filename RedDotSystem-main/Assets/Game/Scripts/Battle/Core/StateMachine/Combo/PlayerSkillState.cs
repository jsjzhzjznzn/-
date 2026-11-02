using UnityEngine.InputSystem;

/// <summary>
/// 技能执行状态
/// 播放当前技能（SkillDefinition.combo）的攻击动画，播完回待机。
///
/// 触发方式：PlayerNullState.OnSkillInput 按下技能键后，先经 SkillComponent.RequestCast
/// 完成冷却/充能/沉默校验，成功才把 currentSkill 填进共享数据并切到本状态。
///
/// 职责：
/// - 进入时校验技能数据，非法则兜底回待机（防止空引用/黑屏卡死）
/// - 用 Animancer 播放技能动画，按招式的 hitFrameTime 注册打击帧（与轻击共用伤害管线）
/// - 眩晕时中止技能并回待机；冷却已提交，不回退
/// </summary>
public class PlayerSkillState : PlayerComboState
{
    public PlayerSkillState(PlayerComboStateMachine stateMachine) : base(stateMachine) { }

    public override ComboStateType StateType => ComboStateType.Skill;

    public override void Enter()
    {
        // 订阅本状态要用的输入事件（attack / heavyattk / skill）
        base.Enter();

        // 取出共享数据里的当前技能（PlayerNullState 在切状态前填好的）
        var skill = reusableData.currentSkill;

        // 远程镜像端：拥有者通过 netSkillIdHash 同步当前技能，按 id 反查配置
        if (skill == null && player.IsRemote && player.NetSkillIdHash != 0)
            skill = SkillDatabase.ResolveSkill(player.NetSkillIdHash);

        var combo = skill != null ? skill.combo : null;

        // 防御性校验：配置为空，或没配攻击动画 → 恢复移动并直接回待机，不播任何东西
        if (skill == null || combo == null || combo.attackClip == null)
        {
            RestoreMovement();
            stateMachine.SwitchState(stateMachine.NullState);
            return;
        }

        // 用 Animancer 播放技能的攻击动画（ClipTransition），返回 AnimancerState
        var state = player.characterAnimancer.Play(combo.attackClip);
        player.ApplyRemotePhaseOffset(state);   // 远程端：按拥有者已播放时长定位起点

        // 清除残留事件：同一 clip 被重复播放会复用同一个 state，不清会重复注册打击帧
        state.Events.Clear();
        state.Events.OnEnd = OnSkillAnimationEnd;

        // 打击帧：与轻击同一套判定/结算/打击感；远程端不注册（防重复伤害）
        if (!player.IsRemote && combo.hitFrameTime > 0f)
            state.Events.Add(combo.hitFrameTime, () => stateMachine.characterCombo.SkillATK(combo));
    }

    public override void Update()
    {
        base.Update();

        // 眩晕：中止技能并回待机（技能冷却已提交，不回退）
        if (player.CharacterState != null && player.CharacterState.IsStun)
        {
            RestoreMovement();
            stateMachine.SwitchState(stateMachine.NullState);
        }
    }

    /// <summary>技能动画播完 → 恢复移动并回待机</summary>
    private void OnSkillAnimationEnd()
    {
        RestoreMovement();
        stateMachine.SwitchState(stateMachine.NullState);
    }
}
