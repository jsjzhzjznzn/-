using UnityEngine;

/// <summary>
/// 追击：朝目标走到停止距离；超时或**卡住**自动结束，避免顶着墙永远 Running。
/// 卡住后由决策层改选环绕/后撤，形成"绕开障碍"的行为（配合 EnemyBrain 的碰撞约束）。
///
/// 两个会把"追击"拖成"走很久才出手"的点（2026-10-24 修）：
///  1) 到位判定原来在转向之前：一旦距离 ≤ stopDistance 就直接结束，**一帧都不转**。
///     玩家贴着站在背后/侧面时，BOSS 会原地背对玩家循环"追击→立即完成"，永远不转身、也打不到。
///     现在先转身；到位但没对准就原地转身（播待机，不空跑），对准后才结束。
///  2) 超时 4s 太长：BD 的 Selector 在子节点 Running 时不会回头重估高优先级分支，
///     所以这 4 秒里就算招式早就好了，树也没机会去打（实测玩家绕圈时出现 4.00s 连续追击）。
///     决策层的 Action_EnemyMoveTo 现在会在"可出手"时提前结束追击；超时也压到 2s 兜底。
/// </summary>
public sealed class MoveAction : EnemyAction
{
    private const float TimeoutSeconds = 2f;

    /// <summary>判定卡住的位移阈值（米）与观察窗口（秒）</summary>
    private const float StuckDistance = 0.05f;
    private const float StuckWindow = 0.5f;

    /// <summary>到位后"算对准"的角度（度）：留一点余量，避免每帧微调导致永远不结束</summary>
    private const float AlignedAngle = 15f;

    private float elapsed;
    private float stuckTimer;
    private Vector3 stuckAnchor;

    public MoveAction(EnemyBrain brain) : base(brain) { }

    public override EnemyActionKind Kind => EnemyActionKind.Move;

    public override bool IsFinished { get; protected set; }

    public override void Enter()
    {
        elapsed = 0f;
        stuckTimer = 0f;
        stuckAnchor = brain.transform.position;
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

        // 先转向：贴近目标时也要对齐，否则会出现"贴着玩家背对站桩"
        brain.FaceTargetSmooth(deltaTime, profile.turnSpeedDeg);

        if (brain.HorizontalDistanceToTarget() <= profile.stopDistance)
        {
            // 到位但还没对准 → 原地转身（换回待机，避免原地空跑）
            if (brain.IsFacingTarget(AlignedAngle))
            {
                IsFinished = true;
                return;
            }

            brain.PlayLocomotion(profile.idleClip);
            return;
        }

        if (elapsed >= TimeoutSeconds)
        {
            IsFinished = true;
            return;
        }

        brain.MoveTowardsTarget(profile.moveSpeed, deltaTime);

        // 卡住检测：贴着墙时位移几乎为 0，继续 Running 只会原地磨——交给决策层换招
        stuckTimer += deltaTime;
        if (stuckTimer < StuckWindow)
            return;

        if (Vector3.Distance(brain.transform.position, stuckAnchor) < StuckDistance)
        {
            brain.NotifyMoveBlocked();
            IsFinished = true;
        }
        else
        {
            stuckAnchor = brain.transform.position;
            stuckTimer = 0f;
        }
    }

    public override bool CanBeInterruptedBy(EnemyAction next)
    {
        if (next == null) return false;
        return next.Kind != EnemyActionKind.None;
    }
}
