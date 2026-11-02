using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>韧性是否已被打空（破防中）。可用于让树走"处决/追击"分支。</summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("韧性已打空（破防硬直中）")]
public class Cond_EnemyPoiseBroken : Conditional
{
    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null) return TaskStatus.Failure;
        return brain.IsPoiseBroken ? TaskStatus.Success : TaskStatus.Failure;
    }
}
