using UnityEngine;

/// <summary>
/// 环绕：沿目标的切向绕圈并朝目标，制造"不直勾勾贴脸"的压迫感。
/// 半径用 profile.strafeRadius，超出/不足时做径向修正。
/// </summary>
public sealed class StrafeAction : EnemyAction
{
    private readonly int direction;
    private float elapsed;
    private float duration;

    public StrafeAction(EnemyBrain brain, int direction) : base(brain)
    {
        this.direction = direction >= 0 ? 1 : -1;
    }

    public override EnemyActionKind Kind => EnemyActionKind.Strafe;
    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => duration > 0.0001f ? Mathf.Clamp01(elapsed / duration) : 1f;

    public override void Enter()
    {
        elapsed = 0f;
        duration = brain.Profile != null ? Mathf.Max(0.25f, brain.Profile.strafeDuration) : 0.9f;
        brain.PlayLocomotion(brain.Profile != null ? brain.Profile.runClip : null);
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;
        EnemyProfile profile = brain.Profile;
        if (profile == null || brain.Target == null)
        {
            IsFinished = true;
            return;
        }

        if (elapsed >= duration)
        {
            IsFinished = true;
            return;
        }

        Vector3 to = brain.Target.position - brain.transform.position;
        to.y = 0f;
        if (to.sqrMagnitude < 0.0001f)
        {
            IsFinished = true;
            return;
        }

        Vector3 radial = to.normalized;
        Vector3 tangent = Vector3.Cross(Vector3.up, radial) * direction;

        // 切向绕圈 + 径向修正（离太远就往内收，太近就往外退）
        float radius = Mathf.Max(0.5f, profile.strafeRadius);
        float radialError = to.magnitude - radius;
        Vector3 move = tangent + radial * Mathf.Clamp(radialError, -1f, 1f) * 0.6f;
        float speed = profile.moveSpeed * 0.85f;
        brain.MoveAlong(move, speed * deltaTime);

        // 始终面向目标（侧移而不是背身跑）
        brain.FaceTargetSmooth(deltaTime, profile.turnSpeedDeg);

        UpdateLocomotionClip(profile, move, speed);
    }

    /// <summary>
    /// 按移动方向选四向走片段：侧移时播正面跑步片段会横向滑脚，而 FBX 里本就有 Walk_F/B/L/R。
    /// 播放速度按 实际移速 / 片段设计移速 校正。
    /// </summary>
    private void UpdateLocomotionClip(EnemyProfile profile, Vector3 worldMove, float speed)
    {
        worldMove.y = 0f;
        Vector3 local = worldMove.sqrMagnitude > 0.0001f
            ? brain.transform.InverseTransformDirection(worldMove.normalized)
            : Vector3.forward;

        AnimationClip clip;
        if (Mathf.Abs(local.x) >= Mathf.Abs(local.z))
            clip = local.x > 0f ? profile.walkRightClip : profile.walkLeftClip;
        else
            clip = local.z > 0f ? profile.walkForwardClip : profile.walkBackClip;

        if (clip == null) clip = profile.runClip;
        if (clip == null) return;

        float design = profile.walkClipSpeed > 0.05f ? profile.walkClipSpeed : 1f;
        brain.PlayLocomotionScaled(clip, Mathf.Clamp(speed / design, 0.4f, 2f), 0.15f);
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        if (next == null) return false;
        return next.Kind == EnemyActionKind.Attack
            || next.Kind == EnemyActionKind.Stagger
            || next.Kind == EnemyActionKind.Dead
            || next.Kind == EnemyActionKind.Move
            || next.Kind == EnemyActionKind.Backstep;
    }
}
