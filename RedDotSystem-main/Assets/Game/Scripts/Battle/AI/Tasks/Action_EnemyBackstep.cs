using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>请求后撤步（贴脸时拉开），等待动作层结束。</summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("后撤拉开距离")]
public class Action_EnemyBackstep : Action
{
    private EnemyBrain brain;
    private bool requested;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        requested = brain != null && brain.Request(EnemyIntent.Backstep()).Accepted;
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (!requested) return TaskStatus.Failure;
        return brain.CurrentAction == EnemyActionKind.Backstep ? TaskStatus.Running : TaskStatus.Success;
    }
}
