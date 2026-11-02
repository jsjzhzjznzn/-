using UnityEngine;

/// <summary>
/// 原地对准目标（0.12s 平滑转向）：起手前"先瞄再打"，避免贴脸甩招。
/// 已经对准就立刻结束（省掉那 0.12s）；没对准时最多转到 0.25s，
/// 让树可以在"目标从侧后方绕过来"时用一次动作把朝向拉正，而不是每 tick 只转几度。
/// </summary>
public sealed class FaceTargetAction : EnemyAction
{
    private const float Duration = 0.12f;
    private const float MaxDuration = 0.25f;
    private const float AlignedAngle = 8f;

    private float elapsed;

    public FaceTargetAction(EnemyBrain brain) : base(brain) { }

    public override EnemyActionKind Kind => EnemyActionKind.FaceTarget;
    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => Mathf.Clamp01(elapsed / MaxDuration);

    public override void Enter()
    {
        elapsed = 0f;
        brain.PlayLocomotion(brain.Profile != null ? brain.Profile.idleClip : null);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;

        EnemyProfile profile = brain.Profile;
        if (profile == null)
        {
            IsFinished = true;
            return;
        }

        brain.FaceTargetSmooth(deltaTime, profile.turnSpeedDeg * 2f);

        if (elapsed >= Duration && brain.IsFacingTarget(AlignedAngle))
        {
            IsFinished = true;
            return;
        }

        if (elapsed >= MaxDuration) IsFinished = true;
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        if (next == null) return false;
        return next.Kind == EnemyActionKind.Attack
            || next.Kind == EnemyActionKind.Stagger
            || next.Kind == EnemyActionKind.Dead;
    }
}
