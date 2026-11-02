using UnityEngine;

/// <summary>死亡：播死亡片段后停住，不接受任何打断（控制器会被 brain 标记为 dead，不再决策）。</summary>
public sealed class DeadAction : EnemyAction
{
    private float elapsed;
    private float duration;

    public DeadAction(EnemyBrain brain) : base(brain) { }

    public override EnemyActionKind Kind => EnemyActionKind.Dead;
    public override bool IsFinished { get; protected set; }

    public override void Enter()
    {
        elapsed = 0f;
        EnemyProfile profile = brain.Profile;
        AnimationClip clip = profile != null ? profile.deathClip : null;
        duration = clip != null ? clip.length : 1.5f;
        brain.PlayClip(clip, 0.1f);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;
        if (elapsed >= duration) IsFinished = true;
    }

    public override bool CanBeInterruptedBy(EnemyAction next) => false;
}
