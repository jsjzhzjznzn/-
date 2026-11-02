using UnityEngine;

/// <summary>
/// 全身受击（受击状态）。
///
/// 设计：
///  - **每刀都会进这里**（轻击/重击只差片段与时长）；
///  - 连打时由 <see cref="Refresh"/> 延长计时、**不重播片段**，所以怪会一直挂在受击状态里连续挨打，
///    而不是"每刀从第 0 帧重新抖一次"；
///  - 片段播完还没脱困就手动 wrap 归一化时间（<see cref="loopClip"/>），保证受击期间动作一直在动，
///    不会停在最后一帧"发愣"。
/// </summary>
public sealed class HurtAction : EnemyAction
{
    private readonly AnimationClip clipOverride;
    private readonly float durationOverride;
    private readonly float speed;
    private readonly bool loopClip;

    private Animancer.AnimancerState state;
    private float elapsed;
    private float duration;

    public AnimationClip Clip => clipOverride != null
        ? clipOverride
        : (brain.Profile != null ? brain.Profile.hurtClip : null);

    /// <param name="clipOverride">受击片段；null = 用 EnemyProfile.hurtClip</param>
    /// <param name="durationOverride">硬直时长（秒）；&lt;=0 = 用 EnemyProfile.hurtStaggerSeconds</param>
    /// <param name="speed">片段播放倍速；&lt;=0 = 自动按"片段长度 / 硬直时长"算（正/背面片段长度不同也能一次播完）</param>
    /// <param name="loopClip">受击时间超过片段长度时是否循环播放（默认 true，避免停在最后一帧）</param>
    public HurtAction(EnemyBrain brain, AnimationClip clipOverride = null, float durationOverride = -1f,
        float speed = -1f, bool loopClip = true)
        : base(brain)
    {
        this.clipOverride = clipOverride;
        this.durationOverride = durationOverride;
        this.speed = speed;
        this.loopClip = loopClip;
    }

    public override EnemyActionKind Kind => EnemyActionKind.Hurt;
    public override bool IsFinished { get; protected set; }

    public override float NormalizedTime => duration > 0.0001f ? Mathf.Clamp01(elapsed / duration) : 1f;

    public override void Enter()
    {
        elapsed = 0f;

        EnemyProfile profile = brain.Profile;
        duration = durationOverride > 0f
            ? durationOverride
            : (profile != null ? Mathf.Max(0.1f, profile.hurtStaggerSeconds) : 0.4f);

        state = brain.PlayClip(Clip, 0.06f);
        if (state != null)
        {
            // speed <= 0：按"片段长度 / 硬直时长"自动算，正/背面片段长度不同也不会播一半或重复
            float actualSpeed = speed > 0f
                ? speed
                : (Clip != null && Clip.length > 0.0001f ? Clip.length / duration : 1f);
            state.Speed = Mathf.Max(0.1f, actualSpeed);
        }
    }

    /// <summary>连打刷新：延长受击时长，但不重播片段（受击动画继续播）</summary>
    public void Refresh()
    {
        elapsed = 0f;
    }

    public override void Tick(float deltaTime)
    {
        elapsed += deltaTime;

        // 片段比受击时长短：手动循环，避免"卡在最后一帧"
        if (loopClip && state != null)
        {
            float normalized = state.NormalizedTime;
            if (normalized >= 1f) state.NormalizedTime = normalized - Mathf.Floor(normalized);
        }

        if (elapsed >= duration)
        {
            IsFinished = true;
            brain.PlayLocomotion(brain.Profile != null ? brain.Profile.idleClip : null);
        }
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        return next != null && (next.Kind == EnemyActionKind.Dead || next.Kind == EnemyActionKind.Stagger);
    }

    public override void Exit()
    {
        state = null;
    }
}
