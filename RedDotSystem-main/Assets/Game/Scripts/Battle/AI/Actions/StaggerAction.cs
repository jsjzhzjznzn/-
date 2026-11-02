using UnityEngine;

/// <summary>
/// 破防硬直：韧性被打空时进入，期间不能行动、也不再被削韧。
/// 结束时通知 brain 回满韧性（并留一段回复延迟），防止被连锁破防。
/// </summary>
public sealed class StaggerAction : EnemyAction
{
    private readonly bool poiseBroken;
    private readonly float durationOverride;
    private float elapsed;
    private float duration;

    /// <param name="durationOverride">小于等于 0 时用 EnemyProfile.staggerSeconds（弹刀会传一个更短的硬直）</param>
    public StaggerAction(EnemyBrain brain, bool poiseBroken, float durationOverride = -1f) : base(brain)
    {
        this.poiseBroken = poiseBroken;
        this.durationOverride = durationOverride;
    }

    public override EnemyActionKind Kind => EnemyActionKind.Stagger;
    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => duration > 0.0001f ? Mathf.Clamp01(elapsed / duration) : 1f;

    public override void Enter()
    {
        elapsed = 0f;
        EnemyProfile profile = brain.Profile;
        duration = durationOverride > 0f
            ? durationOverride
            : (profile != null ? Mathf.Max(0.2f, profile.staggerSeconds) : 0.9f);

        AnimationClip clip = profile != null && profile.staggerClip != null ? profile.staggerClip : (profile != null ? profile.hurtClip : null);
        brain.PlayClip(clip, 0.06f);

        if (brain.logActions)
            Debug.Log($"[AI] {brain.name} 硬直 {duration:F2}s（破韧={poiseBroken}）", brain);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;
        if (elapsed >= duration) IsFinished = true;
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        // 硬直期间只允许死亡打断
        return next != null && next.Kind == EnemyActionKind.Dead;
    }

    public override void Exit()
    {
        if (poiseBroken) brain.OnStaggerEnd();
    }
}
