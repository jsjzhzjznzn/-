using UnityEngine;

/// <summary>
/// 待机破绽（文档 §8 P2 #15）：挑衅/喘息，期间**无霸体**——任何受击都能打断。
/// 作用不是表演，是给玩家一个可预测的主动进攻窗口，避免 AI 一直处于"随时能出手"的压迫状态。
/// </summary>
public sealed class TauntAction : EnemyAction
{
    private float elapsed;
    private float duration;

    public TauntAction(EnemyBrain brain) : base(brain) { }

    public override EnemyActionKind Kind => EnemyActionKind.Taunt;

    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => duration > 0.0001f ? Mathf.Clamp01(elapsed / duration) : 1f;

    public override void Enter()
    {
        elapsed = 0f;

        EnemyProfile profile = brain.Profile;
        duration = profile != null ? Mathf.Max(0.3f, profile.tauntSeconds) : 1.2f;

        brain.PlayClip(profile != null ? profile.tauntClip : null, 0.1f);
        brain.NotifyTaunt();

        if (brain.logActions)
            Debug.Log($"[AI] {brain.name} 挑衅 {duration:F2}s（无霸体，可被打断）", brain);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;
        if (elapsed >= duration) IsFinished = true;
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        return next != null;   // 破绽的意义就在"什么都能打断"
    }
}
