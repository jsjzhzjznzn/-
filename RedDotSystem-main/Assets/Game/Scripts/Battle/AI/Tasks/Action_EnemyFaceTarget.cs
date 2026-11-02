using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>原地对准目标（起手前"先瞄再打"）。</summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("短暂平滑转向目标")]
public class Action_EnemyFaceTarget : Action
{
    private EnemyBrain brain;
    private bool requested;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        requested = brain != null && brain.Request(EnemyIntent.FaceTarget()).Accepted;
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (!requested) return TaskStatus.Failure;
        return brain.CurrentAction == EnemyActionKind.FaceTarget ? TaskStatus.Running : TaskStatus.Success;
    }
}
