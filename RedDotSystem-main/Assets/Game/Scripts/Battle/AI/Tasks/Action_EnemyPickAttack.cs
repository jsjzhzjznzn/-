using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>按距离/阶段/冷却/权重挑一招，把 id 写进 sharedAttackId，供 Action_EnemyAttack 使用。</summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("按权重挑一个可用招式 id（没有可用招式则 Failure）")]
public class Action_EnemyPickAttack : Action
{
    public SharedString sharedAttackId;

    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;

        string id = brain.PickAttackId();
        if (string.IsNullOrEmpty(id)) return TaskStatus.Failure;

        if (sharedAttackId != null) sharedAttackId.Value = id;
        return TaskStatus.Success;
    }
}
