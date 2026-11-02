using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>当前阶段是否达到要求（阶段招式解锁用）。</summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("当前阶段 >= minPhase（血量阶段）")]
public class Cond_EnemyPhaseAtLeast : Conditional
{
    public SharedInt minPhase = 1;

    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null) return TaskStatus.Failure;
        return brain.Phase >= minPhase.Value ? TaskStatus.Success : TaskStatus.Failure;
    }
}
