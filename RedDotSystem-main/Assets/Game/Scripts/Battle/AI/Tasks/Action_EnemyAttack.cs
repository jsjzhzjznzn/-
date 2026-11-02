using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 请求一次攻击，并等到动作层结束。
/// 树只发意图：接受→Running；被拒绝（霸体/冷却）→Failure；动作结束/被打断→Success。
/// </summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("请求第 attackId 号攻击并等待动作层完成")]
public class Action_EnemyAttack : Action
{
    public SharedString attackId;

    private EnemyBrain brain;
    private bool requested;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        requested = false;

        if (brain == null || brain.IsDead) return;

        string id = attackId != null ? attackId.Value : string.Empty;
        if (string.IsNullOrEmpty(id)) id = brain.PickAttackId();
        if (string.IsNullOrEmpty(id)) return;

        EnemyIntentResult result = brain.Request(EnemyIntent.Attack(id));
        requested = result.Accepted;
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (!requested) return TaskStatus.Failure;

        // 动作层已结束（正常完成或被硬直打断）→ 让树继续决策
        return brain.CurrentAction == EnemyActionKind.Attack ? TaskStatus.Running : TaskStatus.Success;
    }
}
