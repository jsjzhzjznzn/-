using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>站桩待机（保证 idle 片段在播，不会因为动作层空闲而停在攻击末帧）。</summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("切回待机动画")]
public class Action_EnemyIdle : Action
{
    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        brain.Request(EnemyIntent.Idle());
        return TaskStatus.Success;
    }
}
