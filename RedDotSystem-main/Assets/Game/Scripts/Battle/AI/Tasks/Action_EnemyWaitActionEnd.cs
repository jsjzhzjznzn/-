using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>等待动作层空闲（攻击/硬直/位移结束）。带超时兜底，防树卡在 Running。</summary>
[TaskCategory("Game/Enemy/Action")]
[TaskDescription("等待当前动作结束（带超时保护）")]
public class Action_EnemyWaitActionEnd : Action
{
    /// <summary>超时（秒）：超过直接 Success，让树重新决策</summary>
    public float timeoutSeconds = 6f;

    private EnemyBrain brain;
    private float startTime;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override void OnStart()
    {
        startTime = Time.time;
        if (brain == null) brain = GetComponent<EnemyBrain>();
    }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead) return TaskStatus.Failure;
        if (brain.Controller == null) return TaskStatus.Success;

        if (brain.Controller.IsIdle) return TaskStatus.Success;
        if (Time.time - startTime >= Mathf.Max(0.1f, timeoutSeconds)) return TaskStatus.Success;
        return TaskStatus.Running;
    }
}
