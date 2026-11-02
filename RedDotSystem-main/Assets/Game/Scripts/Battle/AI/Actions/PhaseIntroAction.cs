using UnityEngine;

/// <summary>
/// 阶段演出：HP 跨过阶段阈值时播放一次（吼叫/爆气）。
/// 期间不可被打断（只有死亡能打断），但**伤害照吃**——避免"破阶段时打不动"。
/// 时长与片段来自 EnemyProfile，不写死。
/// </summary>
public sealed class PhaseIntroAction : EnemyAction
{
    private float elapsed;
    private float duration;

    public PhaseIntroAction(EnemyBrain brain) : base(brain) { }

    public override EnemyActionKind Kind => EnemyActionKind.PhaseIntro;

    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => duration > 0.0001f ? Mathf.Clamp01(elapsed / duration) : 1f;

    public override void Enter()
    {
        elapsed = 0f;

        EnemyProfile profile = brain.Profile;
        duration = profile != null ? Mathf.Max(0.2f, profile.phaseIntroSeconds) : 0.8f;

        AnimationClip clip = profile != null ? profile.phaseIntroClip : null;
        brain.PlayClip(clip, 0.08f);

        if (brain.logActions)
            Debug.Log($"[AI] {brain.name} 阶段演出 {duration:F2}s（阶段 {brain.Phase}）", brain);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;
        if (elapsed >= duration) IsFinished = true;
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        // 只有死亡能打断演出；受击请求会被拒绝，但伤害已经在 HealthModel 里结算
        return next != null && next.Kind == EnemyActionKind.Dead;
    }
}
