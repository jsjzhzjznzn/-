using BehaviorDesigner.Runtime.Tasks;

/// <summary>
/// 刚被障碍挡住（brain.NotifyMoveBlocked 开了 2s 绕行窗口）。
/// 追击卡住后如果不切换行为，树会在"追击→卡住→再追击"之间空转，
/// 表现就是玩家明明就在墙对面，BOSS 却一直贴着墙磨。
/// </summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("刚被障碍挡住（需要绕行）")]
public class Cond_EnemyMoveBlocked : Conditional
{
    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        return brain.IsMoveBlocked ? TaskStatus.Success : TaskStatus.Failure;
    }
}
