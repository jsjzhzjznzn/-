using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 请求追击（走到停止距离），等待动作层结束。
///
/// 关键：BD 的 Selector 在子节点 Running 期间**不会回头重估更高优先级的兄弟分支**。
/// 所以追击一旦开始，攻击分支就算早就就绪也只能干等——实测玩家在 2.6m 绕圈时，
/// 每次出手后都会出现整整 4s 的"追击"，之后才补一刀。
/// 这里加一条"能打就收手"的出口：目标已进入出手距离、有视线、招式也挑得出来时立刻
/// 返回 Success，把决策权交回树上，下一 tick 就会走攻击分支。
/// </summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("追击目标；一旦进入可出手状态就提前结束追击")]
public class Action_EnemyMoveTo : Action
{
    /// <summary>比 profile.attackRange 多出的宽容距离（米），与 Cond_EnemyInAttackRange 的默认口径一致</summary>
    public float attackReadyWindow = 0.6f;

    private EnemyBrain brain;
    private bool requested;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        requested = brain != null && brain.Request(EnemyIntent.MoveTo()).Accepted;
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (!requested) return TaskStatus.Failure;

        if (brain.CurrentAction != EnemyActionKind.Move) return TaskStatus.Success;

        if (CanAttackNow())
        {
            // 动作层会在下次 Update 里把这个 Move 结束掉；这里直接让树回到"重新决策"
            brain.Controller?.ForceStop();
            return TaskStatus.Success;
        }

        return TaskStatus.Running;
    }

    private bool CanAttackNow()
    {
        if (!brain.CanAttackNow || !brain.HasLineOfSight || brain.Profile == null) return false;
        if (brain.TargetDistance > brain.Profile.attackRange + attackReadyWindow) return false;
        return !string.IsNullOrEmpty(brain.PickAttackId());
    }
}
