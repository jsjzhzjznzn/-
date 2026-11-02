using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>全局出手节奏就绪（不在冷却里、没死）。</summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("现在可以出手（全局节奏就绪）")]
public class Cond_EnemyCanAttack : Conditional
{
    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null) return TaskStatus.Failure;
        return brain.CanAttackNow ? TaskStatus.Success : TaskStatus.Failure;
    }
}
