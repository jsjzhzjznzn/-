using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 请求一次挑衅（待机破绽，文档 §8 P2 #15），并等动作层结束。
/// 决策权在 brain：间隔没到 / 动作层在忙时返回 Failure，树会落到下一个分支。
/// 挑衅期间无霸体（任何攻击都能打断）——这就是给玩家的进攻窗口。
/// </summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("间隔到了就挑衅一次（无霸体破绽），并等待结束")]
public class Action_EnemyTaunt : Action
{
    private EnemyBrain brain;
    private bool requested;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        requested = brain != null && brain.TryTaunt();
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (!requested) return TaskStatus.Failure;
        return brain.CurrentAction == EnemyActionKind.Taunt ? TaskStatus.Running : TaskStatus.Success;
    }
}
