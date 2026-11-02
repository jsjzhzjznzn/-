using BehaviorDesigner.Runtime.Tasks;

using UnityEngine;

/// <summary>
/// 距离过近（贴脸）：用于触发后撤步，制造"拉开—突进"的节奏。
/// 阈值 = profile.stopDistance × factor，默认 0.75（与内置决策口径一致）。
/// </summary>
[TaskCategory("Game/Enemy/Condition")]
[TaskDescription("目标贴得过近（用于后撤步分支）")]
public class Cond_EnemyTooClose : Conditional
{
    /// <summary>相对 stopDistance 的倍率：距离 &lt; stopDistance × factor 时成立</summary>
    public float factor = 0.75f;

    private EnemyBrain brain;

    public override void OnAwake() { brain = GetComponent<EnemyBrain>(); }

    public override TaskStatus OnUpdate()
    {
        if (brain == null || brain.IsDead || brain.Profile == null) return TaskStatus.Failure;

        float threshold = brain.Profile.stopDistance * Mathf.Max(0.1f, factor);
        return brain.TargetDistance < threshold ? TaskStatus.Success : TaskStatus.Failure;
    }
}
