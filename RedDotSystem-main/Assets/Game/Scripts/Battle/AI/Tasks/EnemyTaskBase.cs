using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 敌人行为树任务的统一基类：只负责拿 EnemyBrain，不写任何业务。
/// 约定：树的节点只通过 brain.Request(...) 发意图，绝不直接播动画。
/// </summary>
public abstract class EnemyTaskBase : Action
{
    protected EnemyBrain brain;

    public override void OnAwake()
    {
        brain = GetComponent<EnemyBrain>();
    }

    public override void OnStart()
    {
        if (brain == null) brain = GetComponent<EnemyBrain>();
    }

    protected bool BrainReady => brain != null && brain.enabled && !brain.IsDead;
}
