using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 距离在攻击射程内且看得见目标（可加一点宽容度，配合前冲）。
/// 视线条件放在这里而不是 TargetValid：追击时不要求视线（否则玩家绕后 = 木桩），
/// 但出手必须看得见（避免隔墙挥刀）。
/// </summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("目标在攻击射程内且有视线")]
public class Cond_EnemyInAttackRange : Conditional
{
    /// <summary>额外宽容距离（米）：前冲会补上这段距离</summary>
    public float tolerance = 0.6f;

    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead || brain.Profile == null) return TaskStatus.Failure;
        if (!brain.HasLineOfSight) return TaskStatus.Failure;
        return brain.TargetDistance <= brain.Profile.attackRange + tolerance ? TaskStatus.Success : TaskStatus.Failure;
    }
}
