using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 请求环绕（方向可指定，0=随机），等待动作层结束。
/// `useBlockedSide` = 用于"被障碍挡住"的绕行分支：方向沿用 brain 选定的贴墙侧，
/// 避免每次重选左右导致位移互相抵消（内置决策时期的老问题）。
/// </summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("绕目标侧移一段时间")]
public class Action_EnemyStrafe : Action
{
    public SharedInt direction;

    /// <summary>true = 忽略 direction，使用 EnemyBrain.BlockedSide（绕行分支用）</summary>
    public bool useBlockedSide = false;

    private EnemyBrain brain;
    private bool requested;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        int dir = direction != null ? direction.Value : 0;
        if (useBlockedSide && brain != null) dir = brain.BlockedSide;
        requested = brain != null && brain.Request(EnemyIntent.Strafe(dir)).Accepted;
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (!requested) return TaskStatus.Failure;
        return brain.CurrentAction == EnemyActionKind.Strafe ? TaskStatus.Running : TaskStatus.Success;
    }
}
