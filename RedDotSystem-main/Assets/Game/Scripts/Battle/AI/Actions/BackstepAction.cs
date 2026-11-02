using UnityEngine;

/// <summary>后撤步：贴脸时拉开距离，制造"拉开—突进"的节奏；播放 dodge 片段（有则用）。</summary>
public sealed class BackstepAction : EnemyAction
{
    private const float Duration = 0.28f;

    private float elapsed;
    private float distance;

    public BackstepAction(EnemyBrain brain) : base(brain) { }

    public override EnemyActionKind Kind => EnemyActionKind.Backstep;
    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => Mathf.Clamp01(elapsed / Duration);

    public override void Enter()
    {
        elapsed = 0f;
        distance = brain.Profile != null ? Mathf.Max(0.5f, brain.Profile.backstepDistance) : 2f;

        AnimationClip clip = brain.Profile != null && brain.Profile.dodgeClip != null
            ? brain.Profile.dodgeClip
            : (brain.Profile != null ? brain.Profile.runClip : null);
        brain.PlayClip(clip, 0.08f);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;
        if (elapsed >= Duration)
        {
            IsFinished = true;
            return;
        }

        brain.MoveForward(-distance * (deltaTime / Duration));   // 沿背向后退

        EnemyProfile profile = brain.Profile;
        if (profile != null) brain.FaceTargetSmooth(deltaTime, profile.turnSpeedDeg);
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        if (next == null) return false;
        return next.Kind == EnemyActionKind.Dead || next.Kind == EnemyActionKind.Stagger;
    }
}
